# OrderPulse

**AI-Powered Order Lifecycle Tracking Platform**

OrderPulse monitors a dedicated email inbox for online purchase notifications, uses Azure OpenAI to classify and parse each message, stores structured order data in a relational database, and presents everything through a clean, filterable web dashboard.

Stop losing track of orders, returns, and refunds across dozens of retailers. One inbox, one dashboard, full visibility.

---

## Features

- **Automated email ingestion** — Connects to Exchange Online via Microsoft Graph API; polls every 5 minutes
- **AI-powered classification** — Two-pass system: fast pre-filter (GPT-4o-mini) removes noise, then detailed classifier (GPT-4o) identifies 14 message types
- **Structured data extraction** — 7 specialized parsing agents extract order details, shipment tracking, delivery status, return labels, refund amounts, and more
- **Order lifecycle tracking** — 14-state state machine computes order status from child entities (shipments, deliveries, returns, refunds)
- **Return center** — Consolidated view of all open returns with QR codes, labels, drop-off locations, and deadline countdowns
- **Review queue** — Low-confidence classifications and parse failures are flagged for manual review with approve/reprocess actions
- **Multi-tenant ready** — Shared infrastructure with row-level security (SESSION_CONTEXT-based); designed to scale from personal use to SaaS

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌──────────────────────┐
│  Exchange Online │────▶│ EmailPolling     │────▶│ Service Bus:         │
│  (Graph API)     │     │ Function (Timer) │     │ emails-pending       │
└─────────────────┘     └──────────────────┘     └──────────┬───────────┘
                                                            │
                                                            ▼
                        ┌──────────────────┐     ┌──────────────────────┐
                        │ EmailClassifier  │◀────│ Service Bus Trigger  │
                        │ Function         │     └──────────────────────┘
                        │ (GPT-4o-mini +   │
                        │  GPT-4o)         │────▶ Service Bus:
                        └──────────────────┘     emails-classified
                                                            │
                                                            ▼
                        ┌──────────────────┐     ┌──────────────────────┐
                        │ EmailParsing     │◀────│ Service Bus Trigger  │
                        │ Function         │     └──────────────────────┘
                        │ (Orchestrator +  │
                        │  7 AI Parsers)   │────▶ Azure SQL (Orders,
                        └──────────────────┘     Shipments, Returns...)
                                                            │
┌─────────────────┐     ┌──────────────────┐               │
│  Blazor WASM    │────▶│ ASP.NET Core     │◀──────────────┘
│  (SPA Frontend) │     │ Web API          │
└─────────────────┘     └──────────────────┘
```

## Email Processing Pipeline (Detailed Flow)

The pipeline is fully asynchronous and decoupled via Azure Service Bus queues.

### Stage 1: Email Ingestion (`EmailPollingFunction`)

**Trigger:** Timer — every 5 minutes (`0 */5 * * * *`)

1. Fetches all active tenants from the `Tenants` table
2. For each tenant, calls Microsoft Graph API to get new emails since `LastSyncAt`
3. Stores full email body HTML in Azure Blob Storage (`email-bodies` container)
4. Creates an `EmailMessage` record with `ProcessingStatus = Pending`
5. Publishes the `EmailMessageId` to the `emails-pending` Service Bus queue
6. Updates the tenant's `LastSyncAt` timestamp
7. Deduplication: skips emails already in the database (matched by Graph message ID)

### Stage 2: AI Classification (`EmailClassifierFunction`)

**Trigger:** Service Bus — `emails-pending` queue

1. Receives `EmailMessageId` from the queue
2. Looks up the `EmailMessage` record (bypasses RLS with `IgnoreQueryFilters`)
3. Sets tenant context (`SESSION_CONTEXT` + `AsyncLocal`) for subsequent DB writes
4. **Pass 1 — Pre-filter (GPT-4o-mini):** Calls `IsOrderRelatedAsync()` to quickly determine if the email is purchase-related. If not, classifies as `Promotional` and stops (no further processing)
5. **Pass 2 — Full classification (GPT-4o):** Calls `ClassifyAsync()` which returns one of 14 `EmailClassificationType` values plus a confidence score (0–1)
6. If confidence < 0.7, flags as `ManualReview` and stops
7. Updates `EmailMessage` with classification type, confidence, and `ProcessingStatus = Classified`
8. Publishes `EmailMessageId` to the `emails-classified` Service Bus queue

### Stage 3: Parsing & Order Creation (`EmailParsingFunction`)

**Trigger:** Service Bus — `emails-classified` queue

1. Receives `EmailMessageId` from the queue
2. Looks up the `EmailMessage` record (bypasses RLS)
3. Sets tenant context for RLS
4. Delegates to `EmailProcessingOrchestrator.ProcessEmailAsync()`

The orchestrator then:

1. **Retrieves full email body** from Azure Blob Storage (falls back to 500-char preview if blob unavailable)
2. **Matches retailer** from sender email address using the `RetailerMatcher` service
3. **Routes to the appropriate AI parser** based on classification type:

| Classification Type | Parser | Creates/Updates |
|---|---|---|
| OrderConfirmation, OrderModification | `OrderParserService` | **Order** + OrderLines |
| ShipmentConfirmation, ShipmentUpdate | `ShipmentParserService` | Shipment + ShipmentLines |
| DeliveryConfirmation, DeliveryIssue | `DeliveryParserService` | Delivery (linked to Shipment) |
| ReturnInitiation, ReturnLabel, ReturnReceived, ReturnRejection | `ReturnParserService` | Return + ReturnLines |
| RefundConfirmation | `RefundParserService` | Refund |
| OrderCancellation | `CancellationParserService` | Updates OrderLine status |
| PaymentConfirmation | `PaymentParserService` | Updates Order payment info |

4. **Find-or-create order**: If a shipment, delivery, return, refund, or cancellation email references an order number that doesn't exist yet, the orchestrator creates a stub Order record automatically. This ensures every email has an order to attach to, regardless of processing order.
5. **Merges line items**: If an email contains items not already on the order, they are added as new OrderLines.
6. **Recalculates order status** via `OrderStateMachine.RecalculateStatusAsync()` — computes aggregate status from all child entities (shipments, deliveries, returns, refunds, cancellations)
7. Updates `EmailMessage` to `ProcessingStatus = Parsed` with `ProcessedAt` timestamp
8. Writes detailed step-by-step entries to the `ProcessingLog` table for debugging

### Service Bus Queues

| Queue | Producer | Consumer | Message |
|---|---|---|---|
| `emails-pending` | EmailPollingFunction | EmailClassifierFunction | EmailMessageId (GUID string) |
| `emails-classified` | EmailClassifierFunction | EmailParsingFunction | EmailMessageId (GUID string) |
| `emails-deadletter` | Service Bus (automatic) | — | Failed messages after max retries |

### Processing Log

The `ProcessingLog` table records every step of the parsing pipeline with no RLS (always queryable):

```sql
SELECT * FROM ProcessingLog ORDER BY Timestamp DESC;
```

Each entry includes: EmailMessageId, Step (Start, BlobFetch, RetailerMatch, OrderParser, etc.), Status (Info/Success/Warning/Error), Message, and Details.

## Order Status State Machine

The state machine computes order status from all child entities:

```
Placed → PartiallyShipped → Shipped → InTransit → OutForDelivery
    → PartiallyDelivered → Delivered → Closed (30 days after delivery)
    → ReturnInProgress → ReturnReceived → Refunded
    → DeliveryException
    → PartiallyCancelled → Cancelled
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend API | .NET 8 / ASP.NET Core |
| Background Processing | Azure Functions (isolated worker, Consumption plan) |
| AI / LLM | Azure OpenAI (GPT-4o + GPT-4o-mini) |
| Database | Azure SQL Database |
| Storage | Azure Blob Storage |
| Auth | Microsoft Entra ID (JWT Bearer) |
| Frontend | Blazor WebAssembly (Static Web App) |
| Hosting | Azure App Service (API), Azure Static Web Apps (SPA) |
| Email | Microsoft Graph API (client credentials flow) |
| Queuing | Azure Service Bus |
| Secrets | Azure Key Vault |
| Monitoring | Application Insights |

## Project Structure

```
OrderPulse/
├── OrderPulse.sln
├── OrderPulse.Domain/                    # Domain entities, enums, interfaces
│   ├── Entities/                         # Order, Shipment, Delivery, Return, Refund, etc.
│   ├── Enums/                            # OrderStatus, ProcessingStatus, EmailClassificationType, etc.
│   └── Interfaces/                       # ITenantProvider, IEmailClassifier, IEmailParser<T>, etc.
├── OrderPulse.Infrastructure/            # Data access, AI services, external integrations
│   ├── Data/
│   │   ├── OrderPulseDbContext.cs        # EF Core context with RLS query filters
│   │   └── TenantSessionInterceptor.cs   # Sets SESSION_CONTEXT on connection open
│   ├── Services/
│   │   ├── EmailProcessingOrchestrator.cs # Routes classified emails → parsers → DB records
│   │   ├── OrderStateMachine.cs           # Computes aggregate order status
│   │   ├── RetailerMatcher.cs             # Matches sender email → Retailer record
│   │   ├── EmailBlobStorageService.cs     # Stores/retrieves email bodies from Blob Storage
│   │   ├── GraphMailService.cs            # Microsoft Graph API email fetching
│   │   └── ProcessingLogger.cs            # Writes to ProcessingLog table (no RLS)
│   └── AI/
│       ├── AzureOpenAIService.cs          # Dual-client wrapper (classifier + parser endpoints)
│       ├── EmailClassifierService.cs      # Two-pass classification (pre-filter + full)
│       ├── Parsers/                       # 7 specialized parsers (Order, Shipment, Delivery, etc.)
│       └── Prompts/                       # System prompt markdown files with few-shot examples
├── OrderPulse.Api/                       # ASP.NET Core Web API
│   ├── Controllers/
│   │   ├── OrdersController.cs           # CRUD + search for orders
│   │   ├── DashboardController.cs        # Stats, summary, recent activity
│   │   ├── EmailsController.cs           # Review queue, reprocess, debug endpoints
│   │   ├── ReviewController.cs           # Manual review approve/reject
│   │   ├── ReturnsController.cs          # Return center data
│   │   └── SettingsController.cs         # Tenant settings
│   ├── DTOs/                             # Request/response models
│   └── Middleware/
│       └── HttpTenantProvider.cs          # Resolves tenant from JWT email claim via SQL lookup
├── OrderPulse.Functions/                 # Azure Functions (isolated worker)
│   ├── EmailIngestion/
│   │   └── EmailPollingFunction.cs       # Timer: polls Graph API every 5 min
│   ├── EmailProcessing/
│   │   ├── EmailClassifierFunction.cs    # SB trigger: emails-pending → classify → emails-classified
│   │   └── EmailParsingFunction.cs       # SB trigger: emails-classified → parse → create records
│   └── FunctionsTenantProvider.cs        # AsyncLocal<Guid>-based tenant context for Functions
├── OrderPulse.Web/                       # Blazor WebAssembly SPA
│   ├── Pages/                            # Dashboard, Orders, OrderDetail, ReturnCenter, ReviewQueue
│   ├── Services/                         # API client services
│   └── wwwroot/
│       ├── appsettings.json              # API base URL, Entra ID config, API scope
│       └── staticwebapp.config.json      # SWA routing config
├── db/
│   └── migrations/
│       ├── 001_CreateSchema.sql          # All tables, indexes, constraints
│       ├── 002_RowLevelSecurity.sql      # RLS policies (SESSION_CONTEXT-based)
│       ├── 003_SeedRetailers.sql         # 20 pre-loaded retailer records
│       ├── 004_SeedTestData.sql          # Test data for development/demo
│       ├── 005_RemoveTestData.sql        # Cleans test data, preserves schema
│       └── 006_ProcessingLog.sql         # ProcessingLog table (no RLS)
└── docs/
    ├── OrderPulse_Functional_Design_Spec.docx
    └── OrderPulse_UX_Mockup.html
```

## Azure Resources

| Resource | Name | Purpose |
|----------|------|---------|
| Resource Group | `rg-orderpulse-prod` | Container for all resources |
| App Service | `app-orderpulse` | Hosts the ASP.NET Core API |
| Static Web App | `swa-orderpulse` | Hosts the Blazor WASM frontend |
| Function App | `func-orderpulse` | Runs email ingestion + classification + parsing |
| SQL Server | `sql-orderpulse-prod` | Database server |
| SQL Database | `sqldb-orderpulse` | Application database |
| Service Bus | `sb-orderpulse` | Message queues for async processing |
| Storage Account | `storderpulse` | Email body blob storage |
| Key Vault | `kv-orderpulse` | Secrets (connection strings, API keys) |
| OpenAI | `aoai-orderpulse` | GPT-4o + GPT-4o-mini deployments |

### App Registrations (Microsoft Entra ID)

| Name | Client ID | Purpose |
|------|-----------|---------|
| OrderPulse Web | `55bc5eb6-271f-4791-8380-c9ee6f987c7e` | SPA authentication (MSAL) |
| OrderPulse Mail Connector | `be7e5655-f3e0-4cfa-a6e4-d9e69ab24101` | Graph API mail access (client credentials) |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
- [Azure Functions Core Tools v4](https://docs.microsoft.com/azure/azure-functions/functions-run-local)
- An Azure subscription
- A Microsoft 365 mailbox (Exchange Online) for purchase emails

### 1. Database Setup

Run the migration scripts in order:

```bash
export SQL_SERVER="sql-orderpulse-prod.database.windows.net"
export SQL_DB="sqldb-orderpulse"
export SQL_USER="sqladmin"
export SQL_PASS='<your-password>'

sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -i db/migrations/001_CreateSchema.sql
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -i db/migrations/002_RowLevelSecurity.sql
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -i db/migrations/003_SeedRetailers.sql
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -i db/migrations/006_ProcessingLog.sql
```

### 2. Configuration

**API (`OrderPulse.Api`)** — set via App Service config or `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "OrderPulseDb": "Server=sql-orderpulse-prod.database.windows.net;Database=sqldb-orderpulse;..."
  },
  "AzureEntraId": {
    "TenantId": "<your-entra-tenant-id>",
    "ClientId": "<your-web-app-client-id>"
  },
  "AzureOpenAI": {
    "ClassifierEndpoint": "https://aoai-orderpulse.openai.azure.com/",
    "ParserEndpoint": "https://aoai-orderpulse.openai.azure.com/",
    "ApiKey": "<from-key-vault>",
    "ClassifierDeployment": "orderpulse-classifier",
    "ParserDeployment": "orderpulse-parser"
  },
  "AllowedOrigins": "https://your-swa-url.azurestaticapps.net"
}
```

**Function App (`OrderPulse.Functions`)** — set via Function App config:

```
ConnectionStrings__OrderPulseDb = <sql-connection-string>
ServiceBusConnection = <service-bus-connection-string>
BlobStorageConnection = <storage-account-connection-string>
GraphApi__TenantId = <entra-tenant-id>
GraphApi__ClientId = <mail-connector-client-id>
GraphApi__ClientSecret = <mail-connector-secret-value>
AzureOpenAI__ClassifierEndpoint = https://aoai-orderpulse.openai.azure.com/
AzureOpenAI__ParserEndpoint = https://aoai-orderpulse.openai.azure.com/
AzureOpenAI__ApiKey = <openai-api-key>
```

**Frontend (`OrderPulse.Web`)** — `wwwroot/appsettings.json`:

```json
{
  "ApiBaseUrl": "https://app.orderpulse.rysetechnologies.com",
  "ApiScope": "api://<web-app-client-id>/access_as_user",
  "AzureEntraId": {
    "Authority": "https://login.microsoftonline.com/<entra-tenant-id>",
    "ClientId": "<web-app-client-id>"
  }
}
```

### 3. Run Locally

```bash
# API
cd OrderPulse.Api && dotnet run

# Functions (separate terminal)
cd OrderPulse.Functions && func start
```

### 4. Deploy

```bash
# API
cd OrderPulse.Api && dotnet publish -c Release -o ./publish
cd publish && zip -r ../api.zip .
az webapp deployment source config-zip --name app-orderpulse --resource-group rg-orderpulse-prod --src ../api.zip

# Functions
cd ../../OrderPulse.Functions && dotnet publish -c Release -o ./publish
cd publish && zip -r ../functions.zip .
az functionapp deployment source config-zip --name func-orderpulse --resource-group rg-orderpulse-prod --src ../functions.zip

# Frontend (SWA CLI)
cd ../../OrderPulse.Web && dotnet publish -c Release -o ./publish
swa deploy ./publish/wwwroot --env production
```

## Row-Level Security (RLS)

All tenant-scoped tables use SQL Server Row-Level Security with `SESSION_CONTEXT('TenantId')`. This means:

- Every query through EF Core automatically filters by tenant (via `TenantSessionInterceptor`)
- Direct SQL queries (sqlcmd, Azure Query Editor) require `EXEC sp_set_session_context @key=N'TenantId', @value='<tenant-guid>'` before any SELECT/INSERT/UPDATE/DELETE
- The `ProcessingLog` table intentionally has no RLS for easy debugging
- In Azure Functions, `FunctionsTenantProvider` uses `AsyncLocal<Guid>` to flow tenant context through async calls

## Email Message Types

The AI classifier recognizes 14 email types:

| # | Type | Description |
|---|------|-------------|
| 1 | Order Confirmation | Purchase placed with items, totals, delivery estimate |
| 2 | Order Modification | Changes before shipment |
| 3 | Order Cancellation | Full/partial cancellation |
| 4 | Payment Confirmation | Charge processed notification |
| 5 | Shipment Confirmation | Items shipped with tracking |
| 6 | Shipment Update | In-transit status changes |
| 7 | Delivery Confirmation | Package delivered |
| 8 | Delivery Issue | Failed/missing/damaged delivery |
| 9 | Return Initiation | Return request approved |
| 10 | Return Label | Return label/QR code provided |
| 11 | Return Received | Retailer confirms receipt |
| 12 | Return Rejection | Return denied |
| 13 | Refund Confirmation | Refund processed |
| 14 | Promotional | Noise — filtered out at classification stage |

## Debugging

### Check processing logs

```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SELECT TOP 30 LogId, Timestamp, Step, Status, Message FROM ProcessingLog ORDER BY Timestamp DESC;"
```

### Check email status

```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SET QUOTED_IDENTIFIER ON; EXEC sp_set_session_context @key=N'TenantId', @value='<tenant-id>'; SELECT EmailMessageId, Subject, ClassificationType, ProcessingStatus, ErrorDetails FROM EmailMessages ORDER BY ReceivedAt DESC;"
```

### Reset the email pipeline

```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; EXEC sp_set_session_context @key=N'TenantId', @value='<tenant-id>'; DELETE FROM ProcessingLog; DELETE FROM OrderEvents; DELETE FROM Refunds; DELETE FROM Returns; DELETE FROM Deliveries; DELETE FROM Shipments; DELETE FROM OrderLines; DELETE FROM Orders; DELETE FROM EmailMessages; UPDATE Tenants SET LastSyncAt = NULL WHERE TenantId = '<tenant-id>'; SELECT 'Done';"
```

## AI Cost

Estimated ~$2.14/user/month at 200 purchase emails/month. See [design spec](docs/OrderPulse_Functional_Design_Spec.docx) Section 3.5 for detailed breakdown.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
