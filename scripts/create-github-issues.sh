#!/bin/bash
# ============================================================================
# OrderPulse — Create GitHub Issues
# Run this after pushing to the repo:
#   chmod +x scripts/create-github-issues.sh
#   ./scripts/create-github-issues.sh
#
# Requires: gh cli (https://cli.github.com/) authenticated
# ============================================================================

REPO="settlesteven1/OrderPulse20250214v1"

echo "Creating GitHub Issues for OrderPulse..."
echo "Repo: $REPO"
echo ""

# ── LABELS ──
echo "Creating labels..."
gh label create "infrastructure" --repo "$REPO" --color "0075ca" --description "Azure resource provisioning" --force
gh label create "database" --repo "$REPO" --color "5319e7" --description "SQL schema and data" --force
gh label create "backend" --repo "$REPO" --color "e99695" --description "API and service implementation" --force
gh label create "ai" --repo "$REPO" --color "f9d0c4" --description "Azure OpenAI / LLM agents" --force
gh label create "frontend" --repo "$REPO" --color "bfd4f2" --description "Blazor WASM UI" --force
gh label create "auth" --repo "$REPO" --color "d4c5f9" --description "Authentication and authorization" --force
gh label create "devops" --repo "$REPO" --color "c2e0c6" --description "CI/CD and deployment" --force
gh label create "email" --repo "$REPO" --color "fbca04" --description "Email ingestion pipeline" --force
gh label create "priority:high" --repo "$REPO" --color "b60205" --description "Must have for MVP" --force
gh label create "priority:medium" --repo "$REPO" --color "e4e669" --description "Important but not blocking" --force
gh label create "priority:low" --repo "$REPO" --color "0e8a16" --description "Nice to have" --force
gh label create "phase:1-foundation" --repo "$REPO" --color "006b75" --description "Phase 1: Foundation (Weeks 1-3)" --force
gh label create "phase:2-ai" --repo "$REPO" --color "006b75" --description "Phase 2: AI Parsing (Weeks 3-5)" --force
gh label create "phase:3-ui" --repo "$REPO" --color "006b75" --description "Phase 3: Web UI (Weeks 5-8)" --force
gh label create "phase:4-multi-tenant" --repo "$REPO" --color "006b75" --description "Phase 4: Multi-Tenant (Weeks 8-10)" --force
echo ""

# ── MILESTONES ──
echo "Creating milestones..."
gh api repos/$REPO/milestones -f title="Phase 1: Foundation" -f description="Azure infra, DB schema, Graph API, basic email ingestion" -f due_on="2026-03-08T00:00:00Z" 2>/dev/null
gh api repos/$REPO/milestones -f title="Phase 2: AI Parsing" -f description="All 7 parsing agents, order matching, state machine, queues" -f due_on="2026-03-22T00:00:00Z" 2>/dev/null
gh api repos/$REPO/milestones -f title="Phase 3: Web UI" -f description="Blazor WASM dashboard, order list, detail, return center" -f due_on="2026-04-12T00:00:00Z" 2>/dev/null
gh api repos/$REPO/milestones -f title="Phase 4: Multi-Tenant & Polish" -f description="Onboarding, RLS, backfill, notifications, performance" -f due_on="2026-04-26T00:00:00Z" 2>/dev/null
echo ""

# ════════════════════════════════════════════════════════════════════
# INFRASTRUCTURE ISSUES (Azure Setup)
# ════════════════════════════════════════════════════════════════════
echo "Creating infrastructure issues..."

# --- Issue: Create Azure Resource Group ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create the resource group that will contain all OrderPulse resources.

## Steps
1. Log into [Azure Portal](https://portal.azure.com)
2. Navigate to **Resource Groups** > **Create**
3. Name: `rg-orderpulse-prod`
4. Region: **East US 2** (cost-optimized)
5. Tags: `project=OrderPulse`, `environment=production`
6. Click **Review + Create** > **Create**

## Acceptance Criteria
- [ ] Resource group `rg-orderpulse-prod` exists in East US 2
- [ ] Tags applied

## Estimated Time
5 minutes
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Create Azure Resource Group" \
  --label "infrastructure,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Provision Azure SQL Database ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Provision the Azure SQL server and database for all OrderPulse data.

## Steps
1. Navigate to **Azure SQL** > **Create**
2. Select **Single database**
3. Create new SQL Server:
   - Name: `sql-orderpulse-prod`
   - Authentication: SQL authentication (save credentials in Key Vault later)
   - Region: East US 2
4. Database settings:
   - Name: `sqldb-orderpulse`
   - Tier: **Basic (5 DTU, 2GB)** -- sufficient for up to ~50 users
   - Backup redundancy: Locally-redundant
5. Networking:
   - Allow Azure services: **Yes**
   - Add your client IP for development access
6. After creation, run migration scripts:
   ```bash
   sqlcmd -S sql-orderpulse-prod.database.windows.net -d sqldb-orderpulse -U <admin> -P <password> -i db/migrations/001_CreateSchema.sql
   sqlcmd -S sql-orderpulse-prod.database.windows.net -d sqldb-orderpulse -U <admin> -P <password> -i db/migrations/002_RowLevelSecurity.sql
   sqlcmd -S sql-orderpulse-prod.database.windows.net -d sqldb-orderpulse -U <admin> -P <password> -i db/migrations/003_SeedRetailers.sql
   ```

## Acceptance Criteria
- [ ] SQL Server `sql-orderpulse-prod` provisioned
- [ ] Database `sqldb-orderpulse` created on Basic tier
- [ ] All 13 tables created via migration 001
- [ ] RLS policies active via migration 002
- [ ] 20 retailers seeded via migration 003
- [ ] Firewall allows Azure services + your dev IP

## Estimated Time
15 minutes

## Cost
~$5/month
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Provision Azure SQL Database" \
  --label "infrastructure,database,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Create Azure Storage Account ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create blob storage for raw emails, return labels, QR codes, and attachments.

## Steps
1. Navigate to **Storage Accounts** > **Create**
2. Name: `storderpulse` (must be globally unique -- adjust if taken)
3. Performance: **Standard**
4. Redundancy: **LRS** (locally redundant)
5. Access tier: **Hot**
6. After creation, create containers:
   - `emails` -- for raw email body HTML
   - `assets` -- for return labels, QR codes, delivery photos

## Acceptance Criteria
- [ ] Storage account created
- [ ] `emails` container exists
- [ ] `assets` container exists

## Estimated Time
5 minutes

## Cost
~$2-5/month
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Create Azure Storage Account" \
  --label "infrastructure,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Create Azure AI Foundry Project and Deploy Models ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Set up an Azure AI Foundry project with GPT-4o and GPT-4o-mini model deployments for email classification and parsing.

## Steps
1. Navigate to [Azure AI Foundry](https://ai.azure.com)
2. Create a new **Hub** (if you do not already have one):
   - Name: `aihub-orderpulse`
   - Region: **East US 2**
   - Resource group: `rg-orderpulse-prod`
3. Create a new **Project** inside the hub:
   - Name: `orderpulse`
4. Go to **Model catalog** > **Model deployments** > **Deploy model**
5. Deploy two models:
   - **GPT-4o** > deployment name: `orderpulse-classifier`
     - Used for: email classification and complex parsing (orders, shipments, returns)
   - **GPT-4o-mini** > deployment name: `orderpulse-parser`
     - Used for: pre-filtering, simple parsing (deliveries, refunds, payments, cancellations)
6. Note the **Endpoint URL** and **API Key** from the deployment details page

## Note
The `Azure.AI.OpenAI` NuGet package works with AI Foundry endpoints. The deployment names act as aliases -- you can repoint them to newer models later without code changes.

## Acceptance Criteria
- [ ] AI Foundry hub and project created
- [ ] `orderpulse-classifier` deployment (GPT-4o) active
- [ ] `orderpulse-parser` deployment (GPT-4o-mini) active
- [ ] Endpoint URL and API key saved (will go to Key Vault)

## Estimated Time
10 minutes

## Cost
~$5-25/month (usage-based, estimated ~$2.14/user/month)
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Create Azure AI Foundry Project and Deploy Models" \
  --label "infrastructure,ai,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Create Azure Service Bus ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Set up the message queuing infrastructure for the email processing pipeline.

## Steps
1. Navigate to **Service Bus** > **Create**
2. Name: `sb-orderpulse`
3. Tier: **Basic**
4. Region: East US 2
5. After creation, create queues:
   - `emails-pending` -- newly ingested emails awaiting classification
     - Enable duplicate detection: **Yes**, window: 5 minutes
   - `emails-classified` -- classified emails awaiting parsing
   - `emails-deadletter` -- failed messages after 3 retries

## Acceptance Criteria
- [ ] Service Bus namespace `sb-orderpulse` created
- [ ] 3 queues created with proper settings
- [ ] Duplicate detection enabled on `emails-pending`

## Estimated Time
10 minutes

## Cost
~$0.05/million operations
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Create Azure Service Bus Namespace and Queues" \
  --label "infrastructure,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Set Up Microsoft Entra External ID ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Configure Microsoft Entra External ID for user authentication. This is set up within your existing **rysetechnologies** Entra tenant.

## Steps
1. Go to [Microsoft Entra admin center](https://entra.microsoft.com)
2. Navigate to **External Identities** > **External collaboration settings**
   - Ensure external user sign-up is enabled
3. **App registrations** > **New registration**:
   - Name: `OrderPulse Web`
   - Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**
   - Redirect URI type: **SPA**
   - Redirect URI: `https://app.orderpulse.rysetechnologies.com/authentication/login-callback`
4. After registration, go to **Authentication** blade:
   - Click **Add URI** and add: `https://localhost:5001/authentication/login-callback` (dev)
   - Under **Implicit grant and hybrid flows**, enable **ID tokens** and **Access tokens**
5. Note the **Application (client) ID** and **Directory (tenant) ID**

## Notes
- User flows are not needed -- Blazor WASM with MSAL handles the sign-in/sign-up UI and token flow directly
- Custom attributes (TenantId, PurchaseMailbox) are handled in the OrderPulse database, not as directory attributes
- The SPA redirect type uses authorization code flow with PKCE (recommended for browser apps)

## Acceptance Criteria
- [ ] App `OrderPulse Web` registered with SPA redirect type
- [ ] Production and localhost redirect URIs configured
- [ ] Supported account types includes personal Microsoft accounts
- [ ] ID tokens and access tokens enabled
- [ ] Client ID and tenant ID noted for app config

## Estimated Time
10 minutes

## Cost
Free (up to 50K authentications/month)
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Set Up Microsoft Entra External ID for Authentication" \
  --label "infrastructure,auth,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Register Microsoft Graph API App ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Register a separate app in your **rysetechnologies** Entra tenant for server-side access to Exchange Online mailboxes via Microsoft Graph API.

## Steps
1. Go to [Microsoft Entra admin center](https://entra.microsoft.com) (rysetechnologies tenant)
2. **App registrations** > **New registration**
   - Name: `OrderPulse Mail Connector`
   - Supported account types: Accounts in any organizational directory and personal Microsoft accounts
   - Redirect URI type: **Web** (this is a server-side confidential client, not SPA)
   - Redirect URI: `https://app.orderpulse.rysetechnologies.com/mailbox/callback`
3. After registration, go to **Authentication** blade:
   - Click **Add URI** and add: `https://localhost:5001/mailbox/callback` (dev)
4. **API permissions** > Add:
   - Microsoft Graph > Delegated: `Mail.Read`, `Mail.ReadBasic`, `User.Read`
5. **Certificates & secrets** > New client secret:
   - Description: `OrderPulse Production`
   - Expiry: 12 months (set calendar reminder to rotate)
   - **Save the secret value immediately** -- it is only shown once
6. Note: Client ID, Tenant ID, Client Secret -- all go to Key Vault

## Note
This is a separate app registration from `OrderPulse Web` (the SPA for end users). This one uses the **Web** redirect type because Azure Functions calls the Graph API server-side with a client secret.

## Acceptance Criteria
- [ ] App `OrderPulse Mail Connector` registered with Web redirect type
- [ ] Delegated permissions: Mail.Read, Mail.ReadBasic, User.Read
- [ ] Client secret created and saved securely
- [ ] Production and localhost redirect URIs configured

## Estimated Time
10 minutes
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Register Microsoft Graph API App" \
  --label "infrastructure,email,auth,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Create Azure Key Vault ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Central secrets store for all connection strings, API keys, and credentials.

## Steps
1. Navigate to **Key Vault** > **Create**
2. Name: `kv-orderpulse`
3. Region: East US 2
4. Pricing: Standard
5. After creation, add these secrets:
   - `SqlConnectionString` -- Azure SQL connection string
   - `BlobStorageConnectionString` -- Storage account connection string
   - `AzureOpenAiKey` -- Azure OpenAI API key (shared across both deployments)
   - `AzureOpenAiClassifierEndpoint` -- AI Foundry endpoint for classifier (GPT-4o)
   - `AzureOpenAiParserEndpoint` -- AI Foundry endpoint for parser (GPT-4o-mini)
   - `GraphClientId` -- Graph app registration client ID
   - `GraphClientSecret` -- Graph app client secret
   - `ServiceBusConnectionString` -- Service Bus connection string
6. **Access policies**: Will be granted to App Service and Functions managed identities (after those are created)

## Acceptance Criteria
- [ ] Key Vault `kv-orderpulse` created
- [ ] All 8 secrets stored
- [ ] Access policies will be configured in the deployment issue

## Estimated Time
15 minutes

## Cost
~$0.03/10K operations
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Create Azure Key Vault and Store Secrets" \
  --label "infrastructure,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Deploy App Service ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create the hosting environment for the ASP.NET Core API and Blazor WASM frontend.

## Steps
1. **App Service Plan**:
   - Name: `plan-orderpulse`
   - OS: Linux
   - Tier: **B1 (Basic)** -- $13/month
   - Region: East US 2
2. **Web App**:
   - Name: `app-orderpulse` (gives you `app-orderpulse.azurewebsites.net`)
   - Runtime: .NET 8
   - Enable **System Assigned Managed Identity**
3. **Custom domain**:
   - Add: `app.orderpulse.rysetechnologies.com`
   - DNS: CNAME `app.orderpulse` to `app-orderpulse.azurewebsites.net`
   - SSL: Azure managed certificate (free)
4. **App Settings** (reference Key Vault):
   ```
   ConnectionStrings__OrderPulseDb = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=SqlConnectionString)
   AzureOpenAI__ClassifierEndpoint = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=AzureOpenAiClassifierEndpoint)
   AzureOpenAI__ParserEndpoint = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=AzureOpenAiParserEndpoint)
   AzureOpenAI__ApiKey = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=AzureOpenAiKey)
   ```
5. **Key Vault access**: Grant the managed identity **GET** permission on secrets in `kv-orderpulse`

## Acceptance Criteria
- [ ] App Service Plan on B1 Linux
- [ ] Web App running with managed identity
- [ ] Custom domain with SSL
- [ ] Key Vault references configured
- [ ] Test endpoint responds at https://app.orderpulse.rysetechnologies.com/health

## Estimated Time
20 minutes

## Cost
$13/month (included in plan)
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Deploy App Service for Web API" \
  --label "infrastructure,devops,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Deploy Azure Functions ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create the serverless compute environment for email ingestion and AI processing.

## Steps
1. **Function App**:
   - Name: `func-orderpulse`
   - Runtime: .NET 8 (Isolated worker)
   - Plan: **Consumption** (pay per execution)
   - Region: East US 2
   - Enable **System Assigned Managed Identity**
2. **App Settings**:
   - Key Vault references (same pattern as App Service)
   - `ServiceBusConnection` to Key Vault reference
3. **Key Vault access**: Grant the managed identity GET permission
4. The function app will host 3 functions:
   - `EmailPollingFunction` -- timer trigger, every 5 minutes
   - `EmailClassifierFunction` -- Service Bus trigger on `emails-pending`
   - `EmailParserFunction` -- Service Bus trigger on `emails-classified`

## Acceptance Criteria
- [ ] Function App `func-orderpulse` created on Consumption plan
- [ ] Managed identity enabled with Key Vault access
- [ ] App settings configured
- [ ] Functions deploy and appear in the portal

## Estimated Time
15 minutes

## Cost
~$0-5/month (consumption plan)
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Deploy Azure Functions for Email Processing" \
  --label "infrastructure,devops,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Create Exchange Mailbox ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create the dedicated purchase email inbox that OrderPulse will monitor.

## Steps
1. Go to [Microsoft 365 Admin Center](https://admin.microsoft.com)
2. **Users** > **Active users** > **Add a user**
   - Name: `OrderPulse Purchases`
   - Username: `purchases@rysetechnologies.com`
   - Assign: Exchange Online Plan 1 license (~$4/month)
3. After creation, configure mailbox:
   - Set up a mail flow rule to auto-mark all messages as read (prevents unread count anxiety)
4. Start using `purchases@rysetechnologies.com` for all online purchases going forward

## Going Forward
- Use this email at checkout everywhere: Amazon, Target, Best Buy, Nike, etc.
- Old orders on your regular email will not be tracked (unless you forward them)
- Consider setting up email forwarding rules from your existing email for purchase-related senders

## Acceptance Criteria
- [ ] `purchases@rysetechnologies.com` mailbox active
- [ ] Can log in and receive emails
- [ ] Auto-read rule configured

## Estimated Time
10 minutes

## Cost
~$4/month (Exchange Online Plan 1)
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Create Exchange Mailbox for Purchase Emails" \
  --label "infrastructure,email,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Application Insights ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Enable monitoring and diagnostics for the App Service and Functions.

## Steps
1. Navigate to **Application Insights** > **Create**
2. Name: `ai-orderpulse`
3. Resource group: `rg-orderpulse-prod`
4. Region: East US 2
5. Link to App Service and Function App (can be done from their Monitoring settings)
6. Create availability test:
   - URL: `https://app.orderpulse.rysetechnologies.com/health`
   - Frequency: Every 5 minutes
   - Locations: East US, West US

## Acceptance Criteria
- [ ] Application Insights resource created
- [ ] Linked to App Service and Functions
- [ ] Availability test configured

## Estimated Time
10 minutes

## Cost
~$2-5/month
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Set Up Application Insights Monitoring" \
  --label "infrastructure,devops,priority:medium,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Configure DNS ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Point the custom domain to Azure App Service.

## Steps
1. In your DNS provider for `rysetechnologies.com`:
2. Add CNAME record:
   - Host: `app.orderpulse`
   - Points to: `app-orderpulse.azurewebsites.net`
   - TTL: 3600
3. Azure will verify domain ownership and provision the SSL certificate automatically
4. Verification may take 5-15 minutes

## Acceptance Criteria
- [ ] DNS CNAME record created
- [ ] Azure domain verification passes
- [ ] SSL certificate provisioned
- [ ] `https://app.orderpulse.rysetechnologies.com` resolves

## Estimated Time
10 minutes
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Configure DNS for OrderPulse Custom Domain" \
  --label "infrastructure,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# ════════════════════════════════════════════════════════════════════
# CODE IMPLEMENTATION ISSUES
# ════════════════════════════════════════════════════════════════════
echo "Creating implementation issues..."

# --- Issue: Repository classes ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Implement the repository interfaces defined in `OrderPulse.Domain/Interfaces/` using Entity Framework Core.

## Files to Create
- `OrderPulse.Infrastructure/Data/Repositories/OrderRepository.cs`
- `OrderPulse.Infrastructure/Data/Repositories/ReturnRepository.cs`
- `OrderPulse.Infrastructure/Data/Repositories/EmailMessageRepository.cs`

## Key Requirements
- Implement all methods from `IOrderRepository`, `IReturnRepository`, `IEmailMessageRepository`
- Use `Include` / `ThenInclude` for eager loading navigation properties
- Implement the smart filter shortcuts (awaiting-delivery, needs-attention, etc.)
- Support pagination, sorting, and search across product names / order numbers
- All queries are automatically tenant-scoped via the EF global query filter

## Acceptance Criteria
- [ ] All repository methods implemented
- [ ] Smart filter shortcuts working
- [ ] Pagination returns correct total counts
- [ ] Search works across order numbers and product names
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Implement repository classes (EF Core)" \
  --label "backend,database,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Azure OpenAI service wrapper ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create the C# service that wraps Azure OpenAI calls, loading prompts from the `/AI/Prompts/` markdown files.

## Files to Create
- `OrderPulse.Infrastructure/AI/AzureOpenAIService.cs` -- base HTTP client wrapper
- `OrderPulse.Infrastructure/AI/EmailClassifierService.cs` -- implements `IEmailClassifier`
- `OrderPulse.Infrastructure/AI/Parsers/OrderParserService.cs`
- `OrderPulse.Infrastructure/AI/Parsers/ShipmentParserService.cs`
- `OrderPulse.Infrastructure/AI/Parsers/DeliveryParserService.cs`
- `OrderPulse.Infrastructure/AI/Parsers/ReturnParserService.cs`
- `OrderPulse.Infrastructure/AI/Parsers/RefundParserService.cs`
- `OrderPulse.Infrastructure/AI/Parsers/CancellationParserService.cs`
- `OrderPulse.Infrastructure/AI/Parsers/PaymentParserService.cs`

## Key Requirements
- Use `Azure.AI.OpenAI` NuGet package
- Load system prompts from the markdown files in `/AI/Prompts/`
- Use structured JSON output mode
- Return confidence scores alongside parsed data
- Flag results below 0.7 confidence for manual review
- Handle API errors gracefully with retry logic

## Prompt Files (already written with few-shot examples)
All 8 prompt templates are in `OrderPulse.Infrastructure/AI/Prompts/`

## Acceptance Criteria
- [ ] Pre-filter returns true/false for order-related emails
- [ ] Classifier returns one of 14 types with confidence score
- [ ] All 7 parsers extract structured JSON matching the database schema
- [ ] Low confidence results flagged for review
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Implement Azure OpenAI service wrapper" \
  --label "backend,ai,priority:high,phase:2-ai" \
  --milestone "Phase 2: AI Parsing" \
  --body "$BODY"

# --- Issue: Email processing orchestrator ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Build the orchestrator that routes classified emails to the correct parsing agent and writes results to the database.

## File to Create
- `OrderPulse.Infrastructure/Services/EmailProcessingOrchestrator.cs`

## Key Requirements
- Implements `IEmailProcessingOrchestrator`
- Routes by `ClassificationType` to the appropriate parser
- After parsing: creates/updates Order, Shipment, Delivery, Return, or Refund entities
- Uses `RetailerMatcher` to identify the retailer from the sender address
- Uses order matching logic to link new emails to existing orders
- Calls `OrderStateMachine.RecalculateStatusAsync()` after any data change
- Creates `OrderEvent` timeline entries for every action
- Handles multi-type emails (processes primary type, then secondary if present)

## Acceptance Criteria
- [ ] All 13 order-related email types routed correctly
- [ ] New orders created from order confirmation emails
- [ ] Shipments linked to existing orders by order number
- [ ] Returns created with label/QR data extracted
- [ ] Order status recalculated after each update
- [ ] Timeline events created for all actions
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Implement email processing orchestrator" \
  --label "backend,ai,email,priority:high,phase:2-ai" \
  --milestone "Phase 2: AI Parsing" \
  --body "$BODY"

# --- Issue: Microsoft Graph email ingestion ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Complete the Graph API integration in `EmailPollingFunction` -- the TODO skeleton is already there.

## Key Requirements
- Use `Microsoft.Graph` SDK to authenticate with client credentials
- Fetch messages from each tenant mailbox since `LastSyncAt`
- Store raw email body HTML in Azure Blob Storage
- Create `EmailMessage` records in the database
- Publish message IDs to Service Bus `emails-pending` queue
- Handle Graph API throttling (429 responses) with exponential backoff
- Update `Tenant.LastSyncAt` after successful sync

## OAuth Flow
- The tenant onboarding flow (separate issue) will capture the refresh token
- This function uses the stored refresh token to get access tokens

## Acceptance Criteria
- [ ] Polls all active tenant mailboxes every 5 minutes
- [ ] New emails stored in blob + database
- [ ] Deduplication by Graph message ID
- [ ] Service Bus messages published for processing
- [ ] Graph throttling handled gracefully
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Implement Microsoft Graph email ingestion" \
  --label "backend,email,priority:high,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

# --- Issue: Blazor Dashboard and Order List ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create the Blazor WebAssembly frontend project with the Dashboard and Order List pages.

## Reference
- Interactive UX mockup: `docs/OrderPulse_UX_Mockup.html` (open in browser to see all screens)
- Design spec Section 9 for detailed UI requirements

## Pages to Build
1. **Dashboard** (`/`)
   - Status summary cards (clickable, navigate to filtered orders)
   - Recent activity feed (last 20 events)
   - Needs attention panel
2. **Order List** (`/orders`)
   - Filter chips (smart shortcuts)
   - Retailer dropdown, date range, sort controls
   - Paginated order table with status badges
   - Click row to navigate to order detail

## Technical Requirements
- Blazor WASM hosted model
- Authentication via MSAL (Microsoft Entra External ID)
- HttpClient calls to the OrderPulse API
- Responsive layout (desktop/tablet)

## Acceptance Criteria
- [ ] Dashboard shows live data from API
- [ ] Status cards show correct counts
- [ ] Activity feed displays recent events
- [ ] Order list supports filtering and pagination
- [ ] Navigation between pages works
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Build Blazor WASM frontend -- Dashboard and Order List" \
  --label "frontend,priority:high,phase:3-ui" \
  --milestone "Phase 3: Web UI" \
  --body "$BODY"

# --- Issue: Blazor Order Detail ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Build the Order Detail page with full lifecycle view.

## Sections
1. **Order header** -- retailer, order #, date, status, payment, address, totals
2. **Line items table** -- product image, name, SKU, qty, price, per-line status
3. **Price breakdown** -- subtotal, shipping, tax, discount, total
4. **Shipments** -- carrier, tracking link, ship date, ETA, delivery status
5. **Returns** -- RMA, status, label/QR, deadline, refund status
6. **Timeline** -- vertical chronological event list with icons

## Acceptance Criteria
- [ ] All sections render with real API data
- [ ] Tracking numbers link to carrier websites
- [ ] Return labels/QR codes display inline
- [ ] Timeline shows complete order history
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Build Blazor WASM frontend -- Order Detail and Timeline" \
  --label "frontend,priority:high,phase:3-ui" \
  --milestone "Phase 3: Web UI" \
  --body "$BODY"

# --- Issue: Blazor Return Center ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Build the Return Center and Awaiting Refund pages.

## Pages
1. **Return Center** (`/returns`)
   - Tab navigation: Need to Ship, Shipped/In Transit, Awaiting Refund, Completed, Rejected
   - Print All Labels button
   - Return cards with: retailer, items, deadline countdown, QR code, label, drop-off location
   - Sorted by urgency (closest deadline first)

2. **Awaiting Refund** (`/returns/awaiting-refund`)
   - Table: retailer, order #, items, refund amount, days waiting, expected timeline
   - Summary banner with total pending refunds

## Acceptance Criteria
- [ ] Return cards display QR codes scannable from screen
- [ ] Deadline countdowns update correctly
- [ ] Print All Labels generates combined PDF
- [ ] Awaiting refund shows accurate wait times
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Build Blazor WASM frontend -- Return Center" \
  --label "frontend,priority:high,phase:3-ui" \
  --milestone "Phase 3: Web UI" \
  --body "$BODY"

# --- Issue: Review Queue and Settings ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Build the Review Queue for manual email corrections and the Settings page.

## Pages
1. **Review Queue** (`/review`)
   - Side-by-side: original email content | AI parsed output
   - Editable fields for corrections
   - Approve, Dismiss, Reprocess buttons
2. **Settings** (`/settings`)
   - Connected mailbox status with last sync time
   - Sync settings (polling interval, webhook toggle)
   - Historical import (date range selector + import button)
   - Notification preferences (checkboxes)

## Acceptance Criteria
- [ ] Review queue shows original email alongside AI output
- [ ] Corrections save and reprocess the email
- [ ] Settings page displays current config
- [ ] Historical import triggers backfill
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Build review queue and settings pages" \
  --label "frontend,priority:medium,phase:3-ui" \
  --milestone "Phase 3: Web UI" \
  --body "$BODY"

# --- Issue: Tenant Onboarding ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Build the end-to-end flow for new users to sign up and connect their mailbox.

## Flow
1. User signs up via Microsoft Entra External ID (Microsoft account)
2. Tenant record created in database
3. User enters their purchase mailbox email
4. OAuth consent flow for Graph API Mail.Read permission
5. Refresh token stored (encrypted) in Tenant record
6. Graph subscription registered (or polling begins)
7. Optional: historical backfill of emails (user selects date range)

## Acceptance Criteria
- [ ] Sign-up creates Tenant record
- [ ] OAuth flow captures Graph API consent
- [ ] Mailbox polling begins immediately after connection
- [ ] Historical backfill processes emails from selected date range
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Implement tenant onboarding flow" \
  --label "backend,auth,email,priority:high,phase:4-multi-tenant" \
  --milestone "Phase 4: Multi-Tenant & Polish" \
  --body "$BODY"

# --- Issue: CI/CD ---
BODY=$(cat <<'ISSUE_EOF'
## Task
Create GitHub Actions workflows for build, test, and deployment.

## Workflows to Create
1. **CI** (`.github/workflows/ci.yml`) -- triggers on PR to `main`/`develop`
   - dotnet restore, build, test
   - Fail on build warnings
2. **Deploy API** (`.github/workflows/deploy-api.yml`) -- triggers on push to `main`
   - Build + publish OrderPulse.Api
   - Deploy to Azure App Service
3. **Deploy Functions** (`.github/workflows/deploy-functions.yml`) -- triggers on push to `main`
   - Build + publish OrderPulse.Functions
   - Deploy to Azure Functions

## Acceptance Criteria
- [ ] PRs get build status checks
- [ ] Merge to main auto-deploys API and Functions
- [ ] Azure credentials stored as GitHub Secrets
ISSUE_EOF
)
gh issue create --repo "$REPO" --title "Set up CI/CD with GitHub Actions" \
  --label "devops,priority:medium,phase:1-foundation" \
  --milestone "Phase 1: Foundation" \
  --body "$BODY"

echo ""
echo "All issues created! Check https://github.com/$REPO/issues"
