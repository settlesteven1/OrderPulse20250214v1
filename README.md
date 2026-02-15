# OrderPulse

**AI-Powered Order Lifecycle Tracking Platform**

OrderPulse monitors a dedicated email inbox for online purchase notifications, uses Azure OpenAI to classify and parse each message, stores structured order data in a relational database, and presents everything through a clean, filterable web dashboard.

Stop losing track of orders, returns, and refunds across dozens of retailers. One inbox, one dashboard, full visibility.

---

## Features

- **Automated email ingestion** — Connects to Exchange Online via Microsoft Graph API; polls every 5 minutes (webhook upgrade path available)
- **AI-powered classification** — Two-pass system: fast pre-filter (GPT-4o-mini) removes noise, then detailed classifier (GPT-4o) identifies 14 message types
- **Structured data extraction** — 7 specialized parsing agents extract order details, shipment tracking, delivery status, return labels, refund amounts, and more
- **Order lifecycle tracking** — 14-state state machine computes order status from child entities (shipments, deliveries, returns, refunds)
- **Return center** — Consolidated view of all open returns with QR codes, labels, drop-off locations, and deadline countdowns
- **Multi-tenant ready** — Shared infrastructure with row-level security; designed to scale from personal use to SaaS

## Architecture

```
┌─────────────────┐     ┌──────────────┐     ┌──────────────────┐
│  Exchange Online │────▶│ Azure        │────▶│ Azure Service Bus │
│  (Graph API)     │     │ Functions    │     │ (queues)          │
└─────────────────┘     │ (polling)    │     └────────┬─────────┘
                        └──────────────┘              │
                                                      ▼
┌─────────────────┐     ┌──────────────┐     ┌──────────────────┐
│  Blazor WASM    │────▶│ ASP.NET Core │────▶│ Azure SQL        │
│  (Frontend)     │     │ Web API      │     │ Database         │
└─────────────────┘     └──────────────┘     └──────────────────┘
                                                      │
                        ┌──────────────┐              │
                        │ Azure OpenAI │◀─────────────┘
                        │ (GPT-4o)     │
                        └──────────────┘
```

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Backend API | .NET 8 / ASP.NET Core |
| Background Processing | Azure Functions (Consumption) |
| AI / LLM | Azure OpenAI (GPT-4o + GPT-4o-mini) |
| Database | Azure SQL Database |
| Storage | Azure Blob Storage |
| Auth | Microsoft Entra ID (Azure AD B2C) |
| Frontend | Blazor WebAssembly |
| Hosting | Azure App Service |
| Email | Microsoft Graph API |
| Queuing | Azure Service Bus |

## Project Structure

```
OrderPulse/
├── OrderPulse.sln                    # Solution file
├── OrderPulse.Domain/                # Domain entities, enums, interfaces
│   ├── Entities/                     # Order, Shipment, Return, Refund, etc.
│   ├── Enums/                        # OrderStatus, ReturnStatus, etc.
│   └── Interfaces/                   # Repository & service contracts
├── OrderPulse.Infrastructure/        # Data access, AI services, external integrations
│   ├── Data/                         # EF Core DbContext, tenant isolation
│   ├── Services/                     # State machine, retailer matching
│   └── AI/
│       └── Prompts/                  # AI agent system prompts with few-shot examples
├── OrderPulse.Api/                   # ASP.NET Core Web API
│   ├── Controllers/                  # Orders, Returns, Dashboard, Emails
│   ├── DTOs/                         # Request/response models
│   └── Middleware/                   # Tenant provider, error handling
├── OrderPulse.Functions/             # Azure Functions
│   ├── EmailIngestion/               # Timer-triggered mailbox polling
│   └── EmailProcessing/              # Service Bus-triggered AI classification
├── db/
│   └── migrations/                   # SQL migration scripts
│       ├── 001_CreateSchema.sql      # Tables, indexes, constraints
│       ├── 002_RowLevelSecurity.sql  # RLS policies for tenant isolation
│       └── 003_SeedRetailers.sql     # 20 pre-loaded retailers
└── docs/
    ├── OrderPulse_Functional_Design_Spec.docx
    └── OrderPulse_UX_Mockup.html     # Interactive clickable prototype
```

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
- [Azure Functions Core Tools v4](https://docs.microsoft.com/azure/azure-functions/functions-run-local)
- An Azure subscription
- A Microsoft 365 mailbox (Exchange Online) for purchase emails

### 1. Azure Infrastructure

See the [GitHub Issues](../../issues) labeled `infrastructure` for step-by-step provisioning guides, or refer to Section 11 of the [Functional Design Spec](docs/OrderPulse_Functional_Design_Spec.docx).

**Quick summary of Azure resources needed:**

| Resource | SKU | Est. Cost |
|----------|-----|-----------|
| App Service Plan | B1 | $13/mo |
| Azure SQL Database | Basic (5 DTU) | $5/mo |
| Storage Account | Standard LRS | $2–5/mo |
| Azure OpenAI | S0 (GPT-4o + mini) | $5–25/mo |
| Service Bus | Basic | ~$0 |
| Functions | Consumption | ~$0–5/mo |
| AD B2C | Free tier | $0 |
| Key Vault | Standard | ~$0 |
| App Insights | Pay-as-you-go | $2–5/mo |
| **Total** | | **$30–70/mo** |

### 2. Database Setup

Run the migration scripts in order against your Azure SQL instance:

```bash
sqlcmd -S sql-orderpulse-prod.database.windows.net -d sqldb-orderpulse -U <admin> -P <password> -i db/migrations/001_CreateSchema.sql
sqlcmd -S sql-orderpulse-prod.database.windows.net -d sqldb-orderpulse -U <admin> -P <password> -i db/migrations/002_RowLevelSecurity.sql
sqlcmd -S sql-orderpulse-prod.database.windows.net -d sqldb-orderpulse -U <admin> -P <password> -i db/migrations/003_SeedRetailers.sql
```

### 3. Configuration

Create `appsettings.Development.json` in the Api project (gitignored):

```json
{
  "ConnectionStrings": {
    "OrderPulseDb": "Server=localhost;Database=OrderPulse;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "AzureAdB2C": {
    "Instance": "https://orderpulse.b2clogin.com/",
    "ClientId": "<your-client-id>",
    "Domain": "orderpulse.onmicrosoft.com",
    "SignUpSignInPolicyId": "B2C_1_signup_signin"
  },
  "AzureOpenAI": {
    "Endpoint": "https://aoai-orderpulse.openai.azure.com/",
    "ApiKey": "<from-key-vault>",
    "ClassifierDeployment": "orderpulse-classifier",
    "ParserDeployment": "orderpulse-parser"
  },
  "AllowedOrigins": "https://localhost:5001"
}
```

### 4. Run Locally

```bash
# API
cd OrderPulse.Api
dotnet run

# Functions (separate terminal)
cd OrderPulse.Functions
func start
```

### 5. Deploy

```bash
# Build and publish
dotnet publish OrderPulse.Api -c Release -o ./publish/api
dotnet publish OrderPulse.Functions -c Release -o ./publish/functions

# Deploy to Azure
az webapp deploy --resource-group rg-orderpulse-prod --name app-orderpulse --src-path ./publish/api
az functionapp deployment source config-zip -g rg-orderpulse-prod -n func-orderpulse --src ./publish/functions
```

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
| 14 | Promotional | Noise — filtered out |

## AI Cost

Estimated ~$2.14/user/month at 200 purchase emails/month. See [design spec](docs/OrderPulse_Functional_Design_Spec.docx) Section 3.5 for detailed breakdown.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
