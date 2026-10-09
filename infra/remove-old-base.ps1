<#
.SYNOPSIS
    Deletes the game's own SQL server and Container Apps environment, left behind by the move to the
    shared portfolio base.

.DESCRIPTION
    After "Move to the shared base" the game's API runs in the shared environment and its data lives on
    the shared SQL server (see docs/adr/0040-shared-portfolio-base.md). This removes, from each of the
    game's resource groups, what that left unused:

      - the old SQL server (sql-cardarena-...), with the old GameDb copy and its backups
      - the old Container Apps environment (cae-cardarena-...)

    The Log Analytics workspace stays: Application Insights still writes to it.

    It refuses to touch a resource group whose latest deployment isn't on the shared base, or an
    environment that still has a container app in it, and lists everything and asks before deleting.
    Deleting a SQL server also deletes its backups, so this can't be undone.
    Works in Windows PowerShell 5.1 and PowerShell 7. Needs the Azure CLI (az), signed in with "az login".

.EXAMPLE
    .\infra\remove-old-base.ps1 -SubscriptionId 00000000-0000-0000-0000-000000000000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SubscriptionId,

    [string[]] $ResourceGroups = @('rg-card-arena-staging', 'rg-card-arena')
)

$ErrorActionPreference = 'Stop'

# Runs an az command and stops the script if it fails (az doesn't throw on its own).
function Invoke-Az {
    $output = & az @args
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed." }
    return $output
}

Invoke-Az account set --subscription $SubscriptionId | Out-Null

$toDelete = @()
foreach ($group in $ResourceGroups) {
    if ((Invoke-Az group exists --name $group) -ne 'true') {
        Write-Host "$group doesn't exist, skipped." -ForegroundColor Yellow
        continue
    }
    $sqlGroup = Invoke-Az deployment group list --resource-group $group --query `
        "sort_by([?starts_with(name, 'card-arena-') && properties.provisioningState == 'Succeeded'], &properties.timestamp)[-1].properties.outputs.sqlResourceGroup.value" `
        --output tsv
    if (-not $sqlGroup -or $sqlGroup -eq $group) {
        throw "$group isn't on the shared base yet (its latest deployment still uses its own SQL server). Run the move first."
    }

    foreach ($server in @(Invoke-Az sql server list --resource-group $group --query "[?starts_with(name, 'sql-cardarena-')].name" --output tsv)) {
        if ($server) { $toDelete += [pscustomobject]@{ Group = $group; Kind = 'SQL server'; Name = $server } }
    }
    foreach ($environment in @(Invoke-Az containerapp env list --resource-group $group --query "[?starts_with(name, 'cae-cardarena-')].name" --output tsv)) {
        if (-not $environment) { continue }
        $apps = Invoke-Az containerapp list --environment $environment --resource-group $group --query 'length(@)' --output tsv
        if ([int]$apps -gt 0) {
            Write-Host "$environment in $group still has $apps container app(s), so it stays." -ForegroundColor Yellow
            continue
        }
        $toDelete += [pscustomobject]@{ Group = $group; Kind = 'Container Apps environment'; Name = $environment }
    }
}

if ($toDelete.Count -eq 0) {
    Write-Host 'Nothing left to delete.' -ForegroundColor Green
    return
}

Write-Host 'This deletes, permanently:' -ForegroundColor Cyan
$toDelete | Format-Table -AutoSize | Out-String | Write-Host
if ((Read-Host 'Type "delete" to go ahead') -ne 'delete') {
    Write-Host 'Nothing deleted.'
    return
}

foreach ($item in $toDelete) {
    Write-Host "Deleting $($item.Kind) $($item.Name) in $($item.Group)..." -ForegroundColor Cyan
    if ($item.Kind -eq 'SQL server') {
        Invoke-Az sql server delete --resource-group $item.Group --name $item.Name --yes | Out-Null
    }
    else {
        Invoke-Az containerapp env delete --resource-group $item.Group --name $item.Name --yes | Out-Null
    }
}
Write-Host 'Done.' -ForegroundColor Green
