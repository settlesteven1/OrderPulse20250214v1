# CLAUDE.md — Agent Instructions for OrderPulse

## What is OrderPulse?

AI-powered order lifecycle tracker. Polls an Exchange Online inbox via Microsoft Graph, classifies emails with Azure OpenAI (GPT-4o-mini pre-filter + GPT-4o classifier), parses order/shipment/delivery/return data with 7 specialized AI parsers, stores everything in Azure SQL, and serves a Blazor WASM dashboard.

## Tech Stack

- .NET 8 (C# 12)
- Azure Functions (isolated worker, timer + Service Bus triggers)
- Azure OpenAI (GPT-4o for parsing, GPT-4o-mini for classification)
- Azure SQL with Row-Level Security (SESSION_CONTEXT)
- Azure Blob Storage (email body storage)
- Azure Service Bus (async email processing pipeline)
- ASP.NET Core Web API + Blazor WASM frontend
- Microsoft Graph API (Exchange Online email polling)
- Entity Framework Core 8

## Project Structure

```
OrderPulse.Domain/          # Entities, enums, interfaces (no dependencies)
OrderPulse.Infrastructure/  # EF Core, AI services, Graph API, parsers, blob storage
OrderPulse.Functions/       # Azure Functions (EmailPolling, EmailClassifier, EmailParsing)
OrderPulse.Api/             # ASP.NET Core Web API (serves dashboard data)
OrderPulse.Web/             # Blazor WASM frontend
db/migrations/              # SQL migration scripts (run manually via sqlcmd)
```

## Azure Resources

| Resource | Name | Purpose |
|----------|------|---------|
| Resource Group | `rg-orderpulse-prod` | Container for all resources |
| App Service | `app-orderpulse` | Hosts ASP.NET Core API |
| Function App | `func-orderpulse` | Email ingestion, classification, parsing |
| SQL Server | `sql-orderpulse-prod` | Database server |
| SQL Database | `sqldb-orderpulse` | Application database |
| Service Bus | `sb-orderpulse` | Queues: `emails-pending`, `emails-classified` |
| Storage Account | `storderpulse` | Container: `email-bodies` |
| Key Vault | `kv-orderpulse` | Connection strings, API keys |
| Azure OpenAI | `aoai-orderpulse` | GPT-4o + GPT-4o-mini deployments |

## SQL Access

The user has environment variables set for database access. Always use this pattern:

```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "<query>"
```

Never hardcode server names or credentials. The variables are:
- `$SQL_SERVER` — Azure SQL server hostname
- `$SQL_DB` — Database name (`sqldb-orderpulse`)
- `$SQL_USER` — SQL admin username
- `$SQL_PASS` — SQL admin password

### Useful diagnostic queries

**Processing logs (most recent first):**
```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SELECT TOP 30 LogId, Step, Status, Message FROM ProcessingLog ORDER BY Timestamp DESC;"
```

**Email messages with status:**
```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SELECT TOP 20 EmailMessageId, Subject, ClassificationType, ProcessingStatus, ErrorDetails FROM EmailMessages ORDER BY CreatedAt DESC;"
```

**Orders with line counts:**
```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SELECT o.ExternalOrderNumber, o.Status, COUNT(ol.OrderLineId) as LineCount FROM Orders o LEFT JOIN OrderLines ol ON o.OrderId = ol.OrderId GROUP BY o.ExternalOrderNumber, o.Status;"
```

**Check tenant LastSyncAt (confirms polling is running):**
```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -Q "SELECT TenantId, Name, PurchaseMailbox, LastSyncAt FROM Tenants;"
```

**Note:** `ProcessingLog` has no RLS, so it's always queryable. All other tables use RLS — you need `sp_set_session_context` for direct queries outside the app.

## Database Migrations

Migrations are plain SQL files in `db/migrations/`. They are run manually:

```bash
sqlcmd -S $SQL_SERVER -d $SQL_DB -U $SQL_USER -P $SQL_PASS -i db/migrations/007_AddOriginalFromAddress.sql
```

EF Core does NOT manage migrations — the schema is managed via these SQL scripts. When adding columns to entities, you must also create a corresponding migration SQL file AND run it against the database.

## Email Processing Pipeline

```
EmailPollingFunction (5-min timer)
  → Fetches from Inbox via Graph API
  → Stores body in Blob Storage
  → Creates EmailMessage record
  → Publishes to Service Bus: emails-pending

EmailClassifierFunction (Service Bus trigger)
  → Pre-filter with GPT-4o-mini (is this a purchase email?)
  → Full classification with GPT-4o (14 email types)
  → Updates EmailMessage.ClassificationType
  → Publishes to Service Bus: emails-classified

EmailParsingFunction (Service Bus trigger)
  → Calls EmailProcessingOrchestrator.ProcessEmailAsync()
  → Retrieves body from Blob Storage
  → Strips forwarding preamble + HTML bloat (ForwardedEmailHelper)
  → Routes to type-specific parser (Order, Shipment, Delivery, Return, etc.)
  → Creates/updates Orders, OrderLines, Shipments, Deliveries, Returns, Refunds
  → Reconciles orphaned records when stub orders get enriched
  → Writes step-by-step entries to ProcessingLog
```

## Key Code Paths

- **Email fetching:** `GraphMailService.GetNewMessagesAsync()` — queries `MailFolders["Inbox"].Messages`
- **Email body prep:** `ForwardedEmailHelper.ExtractOriginalBody()` — strips forwarding headers, HTML bloat, truncates to 30K
- **Orchestrator:** `EmailProcessingOrchestrator.ProcessEmailAsync()` — master routing + processing logic
- **Retailer matching:** `RetailerMatcher.MatchAsync(fromAddress, originalFromAddress)` — matches sender domain to known retailers, falls back to OriginalFromAddress for forwarded emails
- **AI parsing:** `OrderParserService`, `ShipmentParserService`, `DeliveryParserService`, `ReturnParserService` — each uses a prompt from `AI/Prompts/*.md`
- **Order state machine:** `OrderStateMachine.RecalculateStatusAsync()` — computes order status from child entities

## Forwarded Emails

Users forward purchase emails to a shared inbox. This means:
- `FromAddress` is the user's personal email (e.g., `bangupjobasusual@gmail.com`), NOT the retailer
- `OriginalFromAddress` is extracted from email headers (`X-Forwarded-From`) or body patterns (Gmail/Outlook/Apple Mail forwarding format)
- `ForwardedEmailHelper` strips the forwarding preamble before parsing
- Subject lines have `FW:` or `Fwd:` prefixes that get cleaned before parsing

## Build & Deploy

```bash
# Build
dotnet build

# Deploy Functions
cd OrderPulse.Functions && dotnet publish -c Release -o ./publish
cd publish && zip -r ../functions.zip .
az functionapp deployment source config-zip --name func-orderpulse --resource-group rg-orderpulse-prod --src ../functions.zip

# Deploy API
cd OrderPulse.Api && dotnet publish -c Release -o ./publish
cd publish && zip -r ../api.zip .
az webapp deployment source config-zip --name app-orderpulse --resource-group rg-orderpulse-prod --src ../api.zip
```

CI/CD workflows in `.github/workflows/` auto-deploy on push to main.

## Common Issues

1. **"Invalid column name 'X'"** — Entity has a property not yet in the SQL schema. Check `db/migrations/` for a missing migration and run it.
2. **Parsers return null/empty data** — Check `ProcessingLog` for ForwardStrip output. If body is truncated to 20K (old behavior), the HTML stripping may not be deployed. Product data in Amazon emails starts at ~12KB into the HTML.
3. **"No retailer match"** — For forwarded emails, `OriginalFromAddress` must be populated. Check that `internetMessageHeaders` is in the Graph API Select fields and that `ExtractOriginalSender()` is running.
4. **LastSyncAt not updating** — The Function App may not be running, or SaveChangesAsync is failing after processing. Check Application Insights logs.
5. **RLS blocking queries** — Direct SQL queries need `EXEC sp_set_session_context @key=N'TenantId', @value='<guid>'` before any DML on tenant-scoped tables. `ProcessingLog` is exempt.

## Conventions

- Commit messages: conventional format (`fix:`, `feat:`, `chore:`)
- Branches: `feature/*`, `fix/*`, or `claude/*` (for AI-assisted work)
- Always include `Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>` in commits
- XML doc comments on all public methods
- Use `IgnoreQueryFilters()` when querying across tenants in background processing
