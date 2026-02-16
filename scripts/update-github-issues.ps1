# ============================================================================
# OrderPulse — Update Existing GitHub Issues
# Run from the repo root:
#   pwsh scripts/update-github-issues.ps1
#
# Requires: gh cli (https://cli.github.com/) authenticated
# ============================================================================

$REPO = "settlesteven1/OrderPulse20250214v1"

Write-Host "Updating GitHub Issues for OrderPulse..." -ForegroundColor Cyan
Write-Host "Repo: $REPO"
Write-Host ""

# --- #6: Entra External ID (was Azure AD B2C) ---
$body = @"
## Task
Configure Microsoft Entra External ID for user authentication. This is set up within your existing **rysetechnologies** Entra tenant.

## Steps
1. Go to [Microsoft Entra admin center](https://entra.microsoft.com)
2. Navigate to **External Identities** > **External collaboration settings**
   - Ensure external user sign-up is enabled
3. **App registrations** > **New registration**:
   - Name: ``OrderPulse Web``
   - Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**
   - Redirect URI type: **SPA**
   - Redirect URI: ``https://app.orderpulse.rysetechnologies.com/authentication/login-callback``
4. After registration, go to **Authentication** blade:
   - Click **Add URI** and add: ``https://localhost:5001/authentication/login-callback`` (dev)
   - Under **Implicit grant and hybrid flows**, enable **ID tokens** and **Access tokens**
5. Note the **Application (client) ID** and **Directory (tenant) ID**

## Notes
- User flows are not needed -- Blazor WASM with MSAL handles the sign-in/sign-up UI and token flow directly
- Custom attributes (TenantId, PurchaseMailbox) are handled in the OrderPulse database, not as directory attributes
- The SPA redirect type uses authorization code flow with PKCE (recommended for browser apps)

## Acceptance Criteria
- [ ] App ``OrderPulse Web`` registered with SPA redirect type
- [ ] Production and localhost redirect URIs configured
- [ ] Supported account types includes personal Microsoft accounts
- [ ] ID tokens and access tokens enabled
- [ ] Client ID and tenant ID noted for app config

## Estimated Time
10 minutes

## Cost
Free (up to 50K authentications/month)
"@
gh issue edit 6 --repo $REPO --title "Set Up Microsoft Entra External ID for Authentication" --body $body
Write-Host "Updated #6: Entra External ID" -ForegroundColor Green

# --- #7: Graph API App (rysetechnologies + Web redirect) ---
$body = @"
## Task
Register a separate app in your **rysetechnologies** Entra tenant for server-side access to Exchange Online mailboxes via Microsoft Graph API.

## Steps
1. Go to [Microsoft Entra admin center](https://entra.microsoft.com) (rysetechnologies tenant)
2. **App registrations** > **New registration**
   - Name: ``OrderPulse Mail Connector``
   - Supported account types: Accounts in any organizational directory and personal Microsoft accounts
   - Redirect URI type: **Web** (this is a server-side confidential client, not SPA)
   - Redirect URI: ``https://app.orderpulse.rysetechnologies.com/mailbox/callback``
3. After registration, go to **Authentication** blade:
   - Click **Add URI** and add: ``https://localhost:5001/mailbox/callback`` (dev)
4. **API permissions** > Add:
   - Microsoft Graph > Delegated: ``Mail.Read``, ``Mail.ReadBasic``, ``User.Read``
5. **Certificates & secrets** > New client secret:
   - Description: ``OrderPulse Production``
   - Expiry: 12 months (set calendar reminder to rotate)
   - **Save the secret value immediately** -- it is only shown once
6. Note: Client ID, Tenant ID, Client Secret -- all go to Key Vault

## Note
This is a separate app registration from ``OrderPulse Web`` (the SPA for end users). This one uses the **Web** redirect type because Azure Functions calls the Graph API server-side with a client secret.

## Acceptance Criteria
- [ ] App ``OrderPulse Mail Connector`` registered with Web redirect type
- [ ] Delegated permissions: Mail.Read, Mail.ReadBasic, User.Read
- [ ] Client secret created and saved securely
- [ ] Production and localhost redirect URIs configured

## Estimated Time
10 minutes
"@
gh issue edit 7 --repo $REPO --body $body
Write-Host "Updated #7: Graph API App" -ForegroundColor Green

# --- #9: App Service (rysetechnologies domain) ---
$body = @"
## Task
Create the hosting environment for the ASP.NET Core API and Blazor WASM frontend.

## Steps
1. **App Service Plan**:
   - Name: ``plan-orderpulse``
   - OS: Linux
   - Tier: **B1 (Basic)** -- `$13/month
   - Region: East US 2
2. **Web App**:
   - Name: ``app-orderpulse`` (gives you ``app-orderpulse.azurewebsites.net``)
   - Runtime: .NET 8
   - Enable **System Assigned Managed Identity**
3. **Custom domain**:
   - Add: ``app.orderpulse.rysetechnologies.com``
   - DNS: CNAME ``app.orderpulse`` to ``app-orderpulse.azurewebsites.net``
   - SSL: Azure managed certificate (free)
4. **App Settings** (reference Key Vault):
   ``````
   ConnectionStrings__OrderPulseDb = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=SqlConnectionString)
   AzureOpenAI__Endpoint = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=AzureOpenAiEndpoint)
   AzureOpenAI__ApiKey = @Microsoft.KeyVault(VaultName=kv-orderpulse;SecretName=AzureOpenAiKey)
   ``````
5. **Key Vault access**: Grant the managed identity **GET** permission on secrets in ``kv-orderpulse``

## Acceptance Criteria
- [ ] App Service Plan on B1 Linux
- [ ] Web App running with managed identity
- [ ] Custom domain with SSL
- [ ] Key Vault references configured
- [ ] Test endpoint responds at https://app.orderpulse.rysetechnologies.com/health

## Estimated Time
20 minutes

## Cost
`$13/month (included in plan)
"@
gh issue edit 9 --repo $REPO --body $body
Write-Host "Updated #9: App Service" -ForegroundColor Green

# --- #11: Exchange Mailbox (rysetechnologies) ---
$body = @"
## Task
Create the dedicated purchase email inbox that OrderPulse will monitor.

## Steps
1. Go to [Microsoft 365 Admin Center](https://admin.microsoft.com)
2. **Users** > **Active users** > **Add a user**
   - Name: ``OrderPulse Purchases``
   - Username: ``purchases@rysetechnologies.com``
   - Assign: Exchange Online Plan 1 license (~`$4/month)
3. After creation, configure mailbox:
   - Set up a mail flow rule to auto-mark all messages as read (prevents unread count anxiety)
4. Start using ``purchases@rysetechnologies.com`` for all online purchases going forward

## Going Forward
- Use this email at checkout everywhere: Amazon, Target, Best Buy, Nike, etc.
- Old orders on your regular email will not be tracked (unless you forward them)
- Consider setting up email forwarding rules from your existing email for purchase-related senders

## Acceptance Criteria
- [ ] ``purchases@rysetechnologies.com`` mailbox active
- [ ] Can log in and receive emails
- [ ] Auto-read rule configured

## Estimated Time
10 minutes

## Cost
~`$4/month (Exchange Online Plan 1)
"@
gh issue edit 11 --repo $REPO --title "Create Exchange Mailbox for Purchase Emails" --body $body
Write-Host "Updated #11: Exchange Mailbox" -ForegroundColor Green

# --- #12: Application Insights (rysetechnologies URL) ---
$body = @"
## Task
Enable monitoring and diagnostics for the App Service and Functions.

## Steps
1. Navigate to **Application Insights** > **Create**
2. Name: ``ai-orderpulse``
3. Resource group: ``rg-orderpulse-prod``
4. Region: East US 2
5. Link to App Service and Function App (can be done from their Monitoring settings)
6. Create availability test:
   - URL: ``https://app.orderpulse.rysetechnologies.com/health``
   - Frequency: Every 5 minutes
   - Locations: East US, West US

## Acceptance Criteria
- [ ] Application Insights resource created
- [ ] Linked to App Service and Functions
- [ ] Availability test configured

## Estimated Time
10 minutes

## Cost
~`$2-5/month
"@
gh issue edit 12 --repo $REPO --body $body
Write-Host "Updated #12: Application Insights" -ForegroundColor Green

# --- #13: DNS (rysetechnologies) ---
$body = @"
## Task
Point the custom domain to Azure App Service.

## Steps
1. In your DNS provider for ``rysetechnologies.com``:
2. Add CNAME record:
   - Host: ``app.orderpulse``
   - Points to: ``app-orderpulse.azurewebsites.net``
   - TTL: 3600
3. Azure will verify domain ownership and provision the SSL certificate automatically
4. Verification may take 5-15 minutes

## Acceptance Criteria
- [ ] DNS CNAME record created
- [ ] Azure domain verification passes
- [ ] SSL certificate provisioned
- [ ] ``https://app.orderpulse.rysetechnologies.com`` resolves

## Estimated Time
10 minutes
"@
gh issue edit 13 --repo $REPO --title "Configure DNS for OrderPulse Custom Domain" --body $body
Write-Host "Updated #13: DNS" -ForegroundColor Green

# --- #18: Blazor Dashboard (Entra External ID ref) ---
$body = @"
## Task
Create the Blazor WebAssembly frontend project with the Dashboard and Order List pages.

## Reference
- Interactive UX mockup: ``docs/OrderPulse_UX_Mockup.html`` (open in browser to see all screens)
- Design spec Section 9 for detailed UI requirements

## Pages to Build
1. **Dashboard** (``/``)
   - Status summary cards (clickable, navigate to filtered orders)
   - Recent activity feed (last 20 events)
   - Needs attention panel
2. **Order List** (``/orders``)
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
"@
gh issue edit 18 --repo $REPO --body $body
Write-Host "Updated #18: Blazor Dashboard" -ForegroundColor Green

# --- #22: Tenant Onboarding (Entra External ID ref) ---
$body = @"
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
"@
gh issue edit 22 --repo $REPO --body $body
Write-Host "Updated #22: Tenant Onboarding" -ForegroundColor Green

Write-Host ""
Write-Host "Done! 8 issues updated." -ForegroundColor Cyan
Write-Host "Check https://github.com/$REPO/issues" -ForegroundColor Cyan
