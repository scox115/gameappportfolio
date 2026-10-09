<#
.SYNOPSIS
    One-time setup for a new portfolio app on the shared base.

.DESCRIPTION
    Every portfolio app runs in the shared Container Apps environment in rg-portfolio-shared, created by
    infra/setup-shared.ps1 (see docs/adr/0040-shared-portfolio-base.md). This creates what is only the new
    app's, in its own resource group:

      - the resource group (rg-<AppName>), in the shared environment's region
      - a managed identity the app runs as
      - with -Database, a free serverless database on the shared SQL server, which the identity can use
      - an app registration GitHub Actions signs in as, using OIDC (no password or key), trusted only for
        the app repository's "production" environment, with Contributor on the app's resource group and
        on the shared environment
      - the repository variables templates/new-app/deploy.yml reads

    Safe to run again: anything that already exists is reused.
    Works in Windows PowerShell 5.1 and PowerShell 7. Run it from a clone of this repository. Needs the
    Azure CLI (az), signed in with "az login" to the subscription that holds the shared base, and the
    GitHub CLI (gh), signed in with "gh auth login".

.EXAMPLE
    .\templates\new-app\setup-app.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000 `
        -AppName budget-tracker -GitHubRepo scox115/budget-tracker -Database
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SubscriptionId,

    # Lower-case letters, digits and hyphens; it names the resource group, the container app and the database.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z][a-z0-9-]{1,30}[a-z0-9]$')]
    [string] $AppName,

    # owner/repo of the app's repository.
    [Parameter(Mandatory = $true)]
    [string] $GitHubRepo,

    # Give the app a database on the shared SQL server.
    [switch] $Database,

    [string] $SharedResourceGroup = 'rg-portfolio-shared',
    [string] $SqlAdminGroupName = 'portfolio-sql-admins'
)

$ErrorActionPreference = 'Stop'

# Runs an az command and stops the script if it fails (az doesn't throw on its own).
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed." }
    return $output
}

function Grant-Role([string] $principalId, [string] $role, [string] $scope) {
    $assigned = Invoke-Az role assignment list --assignee $principalId --role $role --scope $scope --query '[0].id' --output tsv
    if (-not $assigned) {
        Write-Host "  granting $role on $($scope.Split('/')[-1])"
        Invoke-Az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
            --role $role --scope $scope --output none
    }
}

# Waits until a just-created principal is visible, which can take a minute, so role and group changes work.
function Wait-Principal([string] $principalId, [string] $name) {
    for ($attempt = 1; -not (Invoke-Az ad sp list --filter "id eq '$principalId'" --query '[0].id' --output tsv); $attempt++) {
        if ($attempt -gt 36) { throw "$name still isn't visible in Entra ID. Run the script again in a few minutes." }
        Start-Sleep -Seconds 5
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'This needs the GitHub CLI (gh). Install it and run "gh auth login".' }

$resourceGroup = "rg-$AppName"
$deployAppName = "$AppName-github-deploy"

Write-Host "Using subscription $SubscriptionId" -ForegroundColor Cyan
Invoke-Az account set --subscription $SubscriptionId | Out-Null
$tenantId = Invoke-Az account show --query tenantId --output tsv

$shared = Invoke-Az deployment group show --resource-group $SharedResourceGroup --name portfolio-shared `
    --query '{environment: properties.outputs.environmentName.value, server: properties.outputs.sqlServer.value}' --output json | ConvertFrom-Json
$environment = Invoke-Az containerapp env show --resource-group $SharedResourceGroup --name $shared.environment `
    --query '{id: id, location: location}' --output json | ConvertFrom-Json
Write-Host "Shared environment $($shared.environment) in $($environment.location)" -ForegroundColor Cyan

# The container app has to be in the environment's region, so its group is too.
Write-Host "Resource group $resourceGroup" -ForegroundColor Cyan
Invoke-Az group create --name $resourceGroup --location $environment.location --tags "app=$AppName" --output none
$groupScope = Invoke-Az group show --name $resourceGroup --query id --output tsv

Write-Host "Managed identity id-$AppName" -ForegroundColor Cyan
$identity = Invoke-Az identity create --resource-group $resourceGroup --name "id-$AppName" --tags "app=$AppName" `
    --query '{id: id, clientId: clientId, principalId: principalId}' --output json | ConvertFrom-Json

$connectionString = ''
if ($Database) {
    $databaseName = $AppName
    Write-Host "Database $databaseName on $($shared.server)" -ForegroundColor Cyan
    $template = Join-Path $PSScriptRoot '../../infra/modules/database.bicep'
    Invoke-Az deployment group create --resource-group $SharedResourceGroup --name "database-$AppName" --template-file $template `
        --parameters serverName=$($shared.server) databaseName=$databaseName location=$($environment.location) `
        --output none
    $serverAddress = Invoke-Az sql server show --resource-group $SharedResourceGroup --name $shared.server `
        --query fullyQualifiedDomainName --output tsv

    # The app signs in to the database as its identity. Like the game's, it joins the server's admin group.
    Wait-Principal $identity.principalId "id-$AppName"
    $sqlGroupId = Invoke-Az ad group list --display-name $SqlAdminGroupName --query '[0].id' --output tsv
    $isMember = Invoke-Az ad group member check --group $sqlGroupId --member-id $identity.principalId --query value --output tsv
    if ($isMember -ne 'true') {
        Write-Host "  adding id-$AppName to $SqlAdminGroupName"
        Invoke-Az ad group member add --group $sqlGroupId --member-id $identity.principalId --output none
    }
    $connectionString = "Server=tcp:$serverAddress,1433;Database=$databaseName;Authentication=Active Directory Managed Identity;User Id=$($identity.clientId);Encrypt=True;Connect Timeout=60"
}

Write-Host "GitHub deploy app $deployAppName" -ForegroundColor Cyan
$appId = Invoke-Az ad app list --display-name $deployAppName --query '[0].appId' --output tsv
if (-not $appId) {
    $appId = Invoke-Az ad app create --display-name $deployAppName --query appId --output tsv
}
$spId = Invoke-Az ad sp list --display-name $deployAppName --query '[0].id' --output tsv
if (-not $spId) {
    $spId = Invoke-Az ad sp create --id $appId --query id --output tsv
}

# GitHub names the repository in its sign-in token either as "owner/repo" or, for newer repositories,
# with their numeric IDs ("owner@123/repo@456"), so trust both forms.
$ids = @(& gh api "repos/$GitHubRepo" --jq '.owner.id, .id')
if ($LASTEXITCODE -ne 0 -or $ids.Count -ne 2) { throw "gh api repos/$GitHubRepo failed." }
$owner, $repo = $GitHubRepo.Split('/')
$credentials = [ordered]@{
    'github-production'     = "repo:${GitHubRepo}:environment:production"
    'github-production-ids' = "repo:${owner}@$($ids[0])/${repo}@$($ids[1]):environment:production"
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

Wait-Principal $spId $deployAppName
Grant-Role $spId 'Contributor' $groupScope
Grant-Role $spId 'Contributor' $environment.id

Write-Host "Saving settings to GitHub ($GitHubRepo)" -ForegroundColor Cyan
$variables = [ordered]@{
    AZURE_CLIENT_ID                    = $appId
    AZURE_TENANT_ID                    = $tenantId
    AZURE_SUBSCRIPTION_ID              = $SubscriptionId
    AZURE_RESOURCE_GROUP               = $resourceGroup
    AZURE_CONTAINER_APP                = $AppName
    AZURE_CONTAINERAPPS_ENVIRONMENT_ID = $environment.id
    AZURE_APP_IDENTITY_ID              = $identity.id
    # Not a secret: the app signs in as its identity, so the connection string holds no password.
    SQL_CONNECTION_STRING              = $connectionString
}
foreach ($name in $variables.Keys) {
    if (-not $variables[$name]) { continue }
    & gh variable set $name --body $variables[$name] --repo $GitHubRepo
    if ($LASTEXITCODE -ne 0) { throw "gh variable set $name failed." }
}

Write-Host ''
Write-Host "Done. Copy templates/new-app/deploy.yml to $GitHubRepo as .github/workflows/deploy.yml and push;" -ForegroundColor Green
Write-Host 'its first run creates the container app in the shared environment.'
