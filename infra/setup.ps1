<#
.SYNOPSIS
    One-time Azure and GitHub setup for deploying Kings of the Card Arena.

.DESCRIPTION
    Creates the pieces the deploy workflow can't (or shouldn't) create itself, then stores the
    values the workflow needs as GitHub repository variables and secrets:

      - the resource group
      - the API's user-assigned managed identity
      - an Entra ID group that administers the SQL server, containing you, the API's identity and the deploy app
      - an app registration GitHub Actions signs in as, using OIDC (no password or key)
      - role assignments that let that app deploy into the resource group only
      - a JWT signing key and a RabbitMQ password

    Run it once for production, and again with -Environment staging to add a staging copy that every
    commit reaches first, plus pull request previews (see docs/adr/0026-staging-and-previews.md).
    Staging gets its own resource group and identity; its settings go on the "staging" environment
    in GitHub, so they override the repository's values only there.

    Safe to run again: anything that already exists is reused, and existing secrets are kept.
    Works in Windows PowerShell 5.1 and PowerShell 7. Needs the Azure CLI (az), signed in with
    "az login", and optionally the GitHub CLI (gh), signed in with "gh auth login".

.EXAMPLE
    .\infra\setup.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000

.EXAMPLE
    .\infra\setup.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000 -Environment staging
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SubscriptionId,

    [ValidateSet('production', 'staging')]
    [string] $Environment = 'production',

    [string] $ResourceGroup = 'rg-card-arena',
    [string] $Location = 'eastus2',
    [string] $GitHubRepo = 'scox115/gameappportfolio',
    [string] $IdentityName = 'id-card-arena-api',
    [string] $SqlAdminGroupName = 'card-arena-sql-admins',
    [string] $DeployAppName = 'card-arena-github-deploy'
)

$ErrorActionPreference = 'Stop'

# Staging gets its own resource group and API identity unless they were named.
if ($Environment -eq 'staging') {
    if (-not $PSBoundParameters.ContainsKey('ResourceGroup')) { $ResourceGroup = 'rg-card-arena-staging' }
    if (-not $PSBoundParameters.ContainsKey('IdentityName')) { $IdentityName = 'id-card-arena-api-staging' }
}

# Runs an az command and stops the script if it fails (az doesn't throw on its own).
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed." }
    return $output
}

function New-RandomSecret([int] $byteCount) {
    $bytes = New-Object byte[] $byteCount
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    # URL-safe characters only, so the value survives command lines and connection strings.
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

Write-Host "Using subscription $SubscriptionId" -ForegroundColor Cyan
Invoke-Az account set --subscription $SubscriptionId | Out-Null
$tenantId = Invoke-Az account show --query tenantId --output tsv

Write-Host 'Registering resource providers (first time only)...' -ForegroundColor Cyan
foreach ($namespace in 'Microsoft.App', 'Microsoft.OperationalInsights', 'Microsoft.Insights', 'Microsoft.Web',
                       'Microsoft.Sql', 'Microsoft.KeyVault', 'Microsoft.Storage', 'Microsoft.ManagedIdentity',
                       'Microsoft.AppConfiguration', 'Microsoft.Communication') {
    Invoke-Az provider register --namespace $namespace | Out-Null
}

Write-Host "Resource group $ResourceGroup in $Location" -ForegroundColor Cyan
Invoke-Az group create --name $ResourceGroup --location $Location --output none
$groupScope = Invoke-Az group show --name $ResourceGroup --query id --output tsv

Write-Host "Managed identity $IdentityName" -ForegroundColor Cyan
Invoke-Az identity create --name $IdentityName --resource-group $ResourceGroup --output none
$identityPrincipalId = Invoke-Az identity show --name $IdentityName --resource-group $ResourceGroup --query principalId --output tsv

Write-Host "SQL admin group $SqlAdminGroupName" -ForegroundColor Cyan
$sqlGroupId = Invoke-Az ad group list --display-name $SqlAdminGroupName --query '[0].id' --output tsv
if (-not $sqlGroupId) {
    $sqlGroupId = Invoke-Az ad group create --display-name $SqlAdminGroupName --mail-nickname $SqlAdminGroupName --query id --output tsv
}
$myObjectId = Invoke-Az ad signed-in-user show --query id --output tsv
# A managed identity created a moment ago can take a minute or two to appear in Entra ID,
# and group commands fail until it does. A filtered list returns empty rather than an error.
for ($attempt = 1; -not (Invoke-Az ad sp list --filter "id eq '$identityPrincipalId'" --query '[0].id' --output tsv); $attempt++) {
    if ($attempt -gt 36) { throw "Managed identity $IdentityName still isn't visible in Entra ID. Run the script again in a few minutes." }
    Write-Host '  Waiting for the new identity to appear in Entra ID...'
    Start-Sleep -Seconds 5
}
foreach ($member in $myObjectId, $identityPrincipalId) {
    $isMember = Invoke-Az ad group member check --group $sqlGroupId --member-id $member --query value --output tsv
    if ($isMember -ne 'true') {
        Invoke-Az ad group member add --group $sqlGroupId --member-id $member | Out-Null
    }
}

Write-Host "GitHub deploy app $DeployAppName" -ForegroundColor Cyan
$appId = Invoke-Az ad app list --display-name $DeployAppName --query '[0].appId' --output tsv
if (-not $appId) {
    $appId = Invoke-Az ad app create --display-name $DeployAppName --query appId --output tsv
}
# A lookup that finds nothing returns empty instead of an error, which Windows PowerShell 5.1
# would turn into a terminating error under $ErrorActionPreference = 'Stop'.
$spId = Invoke-Az ad sp list --display-name $DeployAppName --query "[0].id" --output tsv
if (-not $spId) {
    $spId = Invoke-Az ad sp create --id $appId --query id --output tsv
}

# The monthly restore drill signs in to the database as this app to compare the restored copy with
# the live one, so it joins the SQL admin group too. (It can already change the server as Contributor.)
$isMember = Invoke-Az ad group member check --group $sqlGroupId --member-id $spId --query value --output tsv
if ($isMember -ne 'true') {
    Invoke-Az ad group member add --group $sqlGroupId --member-id $spId | Out-Null
}

# Only the workflow's environments in this repository ("production", and "staging" once set up) can sign in as the app.
# GitHub names the repository in its sign-in token either as "owner/repo" or, for newer
# repositories, with their numeric IDs ("owner@123/repo@456"), so trust both forms.
$credentials = [ordered]@{ "github-$Environment" = "repo:${GitHubRepo}:environment:$Environment" }
$hasGh = $null -ne (Get-Command gh -ErrorAction SilentlyContinue)
if ($hasGh) {
    $ids = @(& gh api "repos/$GitHubRepo" --jq '.owner.id, .id')
    if ($LASTEXITCODE -ne 0 -or $ids.Count -ne 2) { throw "gh api repos/$GitHubRepo failed." }
    $owner, $repo = $GitHubRepo.Split('/')
    $credentials["github-$Environment-ids"] = "repo:${owner}@$($ids[0])/${repo}@$($ids[1]):environment:$Environment"
}
else {
    Write-Host '  GitHub CLI not found, so only the owner/repo sign-in name is trusted.' -ForegroundColor Yellow
}
$subjects = @(Invoke-Az ad app federated-credential list --id $appId --query '[].subject' --output tsv)
foreach ($name in $credentials.Keys) {
    if ($subjects -contains $credentials[$name]) { continue }
    Write-Host "  trusting $($credentials[$name])"
    $credentialFile = [System.IO.Path]::GetTempFileName()
    @{
        name      = $name
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = $credentials[$name]
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json | Set-Content -Path $credentialFile -Encoding ASCII
    Invoke-Az ad app federated-credential create --id $appId --parameters "@$credentialFile" | Out-Null
    Remove-Item $credentialFile
}

# Contributor creates the resources; Role Based Access Control Administrator lets the template
# grant the API's identity access to Key Vault and storage. Both apply to this resource group only.
foreach ($role in 'Contributor', 'Role Based Access Control Administrator') {
    $assigned = Invoke-Az role assignment list --assignee $spId --role $role --scope $groupScope --query '[0].id' --output tsv
    if (-not $assigned) {
        Write-Host "  granting $role"
        Invoke-Az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal `
            --role $role --scope $groupScope --output none
    }
}

$variables = [ordered]@{
    AZURE_CLIENT_ID          = $appId
    AZURE_TENANT_ID          = $tenantId
    AZURE_SUBSCRIPTION_ID    = $SubscriptionId
    AZURE_RESOURCE_GROUP     = $ResourceGroup
    AZURE_API_IDENTITY       = $IdentityName
    AZURE_SQL_ADMIN_GROUP    = $SqlAdminGroupName
    AZURE_SQL_ADMIN_GROUP_ID = $sqlGroupId
}
# Staging shares the sign-in and SQL admin settings with production; only these differ.
$scope = @('--repo', $GitHubRepo)
if ($Environment -eq 'staging') {
    $variables = [ordered]@{ AZURE_RESOURCE_GROUP = $ResourceGroup; AZURE_API_IDENTITY = $IdentityName }
    $scope = @('--repo', $GitHubRepo, '--env', 'staging')
}

if ($hasGh) {
    Write-Host "Saving settings to GitHub ($GitHubRepo, $Environment)" -ForegroundColor Cyan
    if ($Environment -eq 'staging') {
        & gh api --method PUT "repos/$GitHubRepo/environments/staging" --silent
        if ($LASTEXITCODE -ne 0) { throw "Creating the staging environment in GitHub failed." }
    }
    foreach ($name in $variables.Keys) {
        & gh variable set $name --body $variables[$name] @scope
        if ($LASTEXITCODE -ne 0) { throw "gh variable set $name failed." }
    }

    # Keep existing secrets: a new JWT key would sign everyone out. Staging gets keys of its own,
    # so a token from one copy of the game is never accepted by the other.
    $existingSecrets = (& gh secret list @scope --json name --jq '.[].name') -split "`n"
    $secrets = @{ JWT_SIGNING_KEY = 48; RABBITMQ_PASSWORD = 24 }
    foreach ($name in $secrets.Keys) {
        if ($existingSecrets -notcontains $name) {
            & gh secret set $name --body (New-RandomSecret $secrets[$name]) @scope
            if ($LASTEXITCODE -ne 0) { throw "gh secret set $name failed." }
        }
    }

    if ($Environment -eq 'staging') {
        # Turns on the staging step of "Deploy to Azure" and the pull request previews.
        & gh variable set STAGING_ENABLED --body 'true' --repo $GitHubRepo
        if ($LASTEXITCODE -ne 0) { throw "gh variable set STAGING_ENABLED failed." }
    }
}
else {
    $where = if ($Environment -eq 'staging') { 'Settings > Environments > staging (create it)' } else { 'Settings > Secrets and variables > Actions' }
    Write-Host ''
    Write-Host "GitHub CLI not found. Add these in GitHub: $where." -ForegroundColor Yellow
    Write-Host 'Variables:' -ForegroundColor Yellow
    foreach ($name in $variables.Keys) { Write-Host "  $name = $($variables[$name])" }
    Write-Host 'Secrets (new random values; copy them now):' -ForegroundColor Yellow
    Write-Host "  JWT_SIGNING_KEY = $(New-RandomSecret 48)"
    Write-Host "  RABBITMQ_PASSWORD = $(New-RandomSecret 24)"
    if ($Environment -eq 'staging') { Write-Host 'Then add the repository variable STAGING_ENABLED = true.' -ForegroundColor Yellow }
}

Write-Host ''
Write-Host 'Done. One manual step remains if the repository is private:' -ForegroundColor Green
Write-Host '  Create a classic GitHub token with only the read:packages scope and save it as the'
Write-Host '  GHCR_READ_TOKEN repository secret, so Azure can pull the API image.'
Write-Host 'Then run the "Deploy to Azure" workflow from the Actions tab.'
if ($Environment -eq 'staging') {
    Write-Host 'From now on each commit deploys to staging first, and production only if staging works.'
    Write-Host 'To approve each production release by hand, add yourself as a required reviewer on the'
    Write-Host '"production" environment (Settings > Environments > production).'
}
