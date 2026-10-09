// One app's database: an Azure SQL free-offer database on an existing server. main.bicep deploys it
// into the shared base's resource group when the game runs there (see docs/adr/0040-shared-portfolio-base.md).

targetScope = 'resourceGroup'

@description('The SQL server the database lives on.')
param serverName string

@description('The database\'s name.')
param databaseName string

@description('The server\'s region.')
param location string

param tags object = {}

resource server 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: serverName
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    // The Azure SQL free offer: 100,000 vCore seconds and 32 GB a month, for up to 10 databases per
    // subscription. When the month's allowance runs out the database pauses instead of billing.
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

output id string = database.id
