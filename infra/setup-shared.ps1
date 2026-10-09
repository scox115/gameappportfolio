<#
.SYNOPSIS
    One-time setup of the shared base every portfolio app runs on.

.DESCRIPTION
    Creates the shared resource group and deploys infra/shared.bicep into it (a Container Apps
    environment, its Log Analytics workspace and an Azure SQL server for one free database per app),
    then lets this game's deploy workflow use it:

      - an Entra ID group that administers the shared SQL server, containing you, the deploy app and
        the API's identities (production and staging, where they exist)
      - Contributor on the shared resource group for the deploy app, so it can add the game's database
        and run its API in the shared environment
      - the AZURE_SHARED_RESOURCE_GROUP repository variable, which the deploy workflow reads

    Nothing moves until you run the "Move to the shared base" workflow, once for staging and once for
    production (see docs/adr/0040-shared-portfolio-base.md).

    Safe to run again: anything that already exists is reused, and a change to shared.bicep is applied.
    Works in Windows PowerShell 5.1 and PowerShell 7. Needs the Azure CLI (az), signed in with
    "az login", and optionally the GitHub CLI (gh), signed in with "gh auth login".

.EXAMPLE
    .\infra\setup-shared.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SubscriptionId,

    [string] $ResourceGroup = 'rg-portfolio-shared',
    [string] $Location = 'centralus',
    [string] $SqlAdminGroupName = 'portfolio-sql-admins',
    [string] $GitHubRepo = 'scox115/gameappportfolio',
    [string] $DeployAppName = 'card-arena-github-deploy',
    [string[]] $ApiIdentities = @('rg-card-arena/id-card-arena-api', 'rg-card-arena-staging/id-card-arena-api-staging')
)

$ErrorActionPreference = 'Stop'

# Runs an az command and stops the script if it fails (az doesn't throw on its own).
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed." }
    return $output
}

function Add-GroupMember([string] $groupId, [string] $memberId) {
    $isMember = Invoke-Az ad group member check --group $groupId --member-id $memberId --query value --output tsv
    if ($isMember -ne 'true') {
        Invoke-Az ad group member add --group $groupId --member-id $memberId | Out-Null
    }
}

Write-Host "Using subscription $SubscriptionId" -ForegroundColor Cyan
Invoke-Az account set --subscription $SubscriptionId | Out-Null

Write-Host 'Registering resource providers (first time only)...' -ForegroundColor Cyan
foreach ($namespace in 'Microsoft.App', 'Microsoft.OperationalInsights', 'Microsoft.Sql') {
    Invoke-Az provider register --namespace $namespace | Out-Null
}

Write-Host "Resource group $ResourceGroup in $Location" -ForegroundColor Cyan
Invoke-Az group create --name $ResourceGroup --location $Location --tags app=portfolio-shared --output none
$groupScope = Invoke-Az group show --name $ResourceGroup --query id --output tsv

Write-Host "SQL admin group $SqlAdminGroupName" -ForegroundColor Cyan
$sqlGroupId = Invoke-Az ad group list --display-name $SqlAdminGroupName --query '[0].id' --output tsv
if (-not $sqlGroupId) {
    $sqlGroupId = Invoke-Az ad group create --display-name $SqlAdminGroupName --mail-nickname $SqlAdminGroupName --query id --output tsv
}
Add-GroupMember $sqlGroupId (Invoke-Az ad signed-in-user show --query id --output tsv)

# The API signs in to its database as its managed identity, and the deploy app runs migrations,
# the database move and the restore drill, so all of them administer the shared server.
foreach ($identity in $ApiIdentities) {
    $group, $name = $identity.Split('/')
    # Staging may not exist. A filtered list that finds nothing returns empty rather than an error.
    $principalId = $null
    if ((Invoke-Az group exists --name $group) -eq 'true') {
        $principalId = Invoke-Az identity list --resource-group $group --query "[?name=='$name'].principalId | [0]" --output tsv
    }
    if ($principalId) {
        Write-Host "  adding $name"
        Add-GroupMember $sqlGroupId $principalId
    }
    else {
        Write-Host "  $name not found in $group, skipped" -ForegroundColor Yellow
    }
}

$spId = Invoke-Az ad sp list --display-name $DeployAppName --query '[0].id' --output tsv
if (-not $spId) { throw "The deploy app $DeployAppName doesn't exist yet. Run infra/setup.ps1 first." }
Write-Host "  adding $DeployAppName"
Add-GroupMember $sqlGroupId $spId

# Contributor is enough here: the deploy app adds a database to the server and joins the API to the
# environment, but grants no roles in this group.
$assigned = Invoke-Az role assignment list --assignee $spId --role Contributor --scope $groupScope --query '[0].id' --output tsv
if (-not $assigned) {
    Write-Host "  granting Contributor on $ResourceGroup to $DeployAppName"
    Invoke-Az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal `
        --role Contributor --scope $groupScope --output none
}

Write-Host 'Deploying the shared base (a few minutes the first time)...' -ForegroundColor Cyan
$template = Join-Path $PSScriptRoot 'shared.bicep'
Invoke-Az deployment group create --resource-group $ResourceGroup --name portfolio-shared --template-file $template `
    --parameters location=$Location sqlAdminGroupName=$SqlAdminGroupName sqlAdminGroupObjectId=$sqlGroupId --output none
$outputs = Invoke-Az deployment group show --resource-group $ResourceGroup --name portfolio-shared `
    --query '{environment: properties.outputs.environmentName.value, server: properties.outputs.sqlServer.value}' --output json | ConvertFrom-Json
Write-Host "  environment $($outputs.environment)"
Write-Host "  SQL server  $($outputs.server)"

if (Get-Command gh -ErrorAction SilentlyContinue) {
    Write-Host "Saving AZURE_SHARED_RESOURCE_GROUP to GitHub ($GitHubRepo)" -ForegroundColor Cyan
    & gh variable set AZURE_SHARED_RESOURCE_GROUP --body $ResourceGroup --repo $GitHubRepo
    if ($LASTEXITCODE -ne 0) { throw 'gh variable set AZURE_SHARED_RESOURCE_GROUP failed.' }
}
else {
    Write-Host ''
    Write-Host 'GitHub CLI not found. Add this repository variable in GitHub (Settings > Secrets and variables > Actions):' -ForegroundColor Yellow
    Write-Host "  AZURE_SHARED_RESOURCE_GROUP = $ResourceGroup"
}

Write-Host ''
Write-Host 'Done. The game keeps running where it is until you move it:' -ForegroundColor Green
Write-Host '  run the "Move to the shared base" workflow from the Actions tab for staging, check the'
Write-Host '  staging site, then run it again for production.'
