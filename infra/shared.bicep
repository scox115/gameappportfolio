// The shared base every portfolio app runs on (see docs/adr/0040-shared-portfolio-base.md).
//
//   Container Apps environment (Consumption)   every app's API runs here, on one free monthly grant
//   Log Analytics                              the environment's container logs
//   Azure SQL server                           one free serverless database per app, Entra ID sign-in only
//
// Each app keeps everything that is only its own (its container app, database, storage, secrets,
// Application Insights) in its own resource group, and points at this one. Deployed once, and again
// after a change here, by infra/setup-shared.ps1 as the deployment named "portfolio-shared"; the apps'
// deploy workflows read its outputs.

targetScope = 'resourceGroup'

@description('Region for the shared base. Each app\'s API runs here, next to its database.')
param location string = resourceGroup().location

@description('Display name of the Entra group that administers the SQL server (created by setup-shared.ps1).')
param sqlAdminGroupName string

@description('Object id of that Entra group.')
param sqlAdminGroupObjectId string

var suffix = uniqueString(resourceGroup().id)
var tags = { app: 'portfolio-shared' }

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-portfolio-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // The first 5 GB a month are free; a cap keeps one app's log storm from costing money.
    workspaceCapping: { dailyQuotaGb: json('0.15') }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-portfolio-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-portfolio-${suffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // No SQL logins or passwords: members of the admin group (each app's identity, the deploy apps and
    // you) sign in with Entra ID.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: subscription().tenantId
      principalType: 'Group'
    }
  }
}

// Container Apps on the Consumption plan have no fixed outbound address, so allow Azure services.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output environmentName string = environment.name
output sqlServerName string = sqlServer.name
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output location string = location
