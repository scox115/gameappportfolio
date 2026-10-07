// Kings of the Card Arena on Azure, sized for free and near-free tiers.
//
//   Static Web Apps (Free)            Blazor WebAssembly client
//   Container Apps (Consumption)      API + RabbitMQ sidecar, scales to zero, one replica at most
//   Azure SQL Database (free offer)   serverless, pauses when idle, Entra ID sign-in only
//   Storage account (Standard LRS)    portraits, reached with the API's managed identity
//   Key Vault (Standard)              JWT signing key and RabbitMQ password
//   Log Analytics + App Insights      logs, traces and metrics from OpenTelemetry
//
// The managed identity and the SQL admin group are created once by infra/setup.ps1, because
// creating Entra groups needs directory permissions the deploy pipeline shouldn't have.

targetScope = 'resourceGroup'

@description('Region for everything except the Static Web App.')
param location string = resourceGroup().location

@description('Static Web Apps (Free) only runs in a few regions.')
@allowed(['westus2', 'centralus', 'eastus2', 'westeurope', 'eastasia'])
param staticWebAppLocation string = 'eastus2'

@description('Region for the SQL server and database. Some regions stop accepting new SQL servers on some subscriptions (RegionDoesNotAllowProvisioning), so it can differ from the rest.')
param sqlLocation string = 'centralus'

@description('Short lowercase name used to build every resource name.')
@minLength(3)
@maxLength(12)
param appName string = 'cardarena'

@description('API container image, for example ghcr.io/owner/card-arena-api:<sha>.')
param apiImage string

@description('User-assigned managed identity the API runs as (created by setup.ps1).')
param apiIdentityName string

@description('Display name of the Entra group that administers the SQL server (created by setup.ps1).')
param sqlAdminGroupName string

@description('Object id of that Entra group.')
param sqlAdminGroupObjectId string

@secure()
@minLength(32)
@description('HMAC key that signs access tokens.')
param jwtSigningKey string

@secure()
@minLength(16)
@description('Password for the RabbitMQ sidecar.')
param rabbitMqPassword string

@description('User that can read the API image from GitHub Container Registry. Leave empty if the package is public.')
param registryUsername string = ''

@secure()
@description('Token with read:packages for that user. Leave empty if the package is public.')
param registryPassword string = ''

@description('Optional address for the game, such as play.example.com. Add a CNAME record pointing it at the Static Web App\'s default host name first; leave empty to use only the default address.')
param customDomain string = ''

@description('Optional email address for alerts when the live API fails. Leave empty for no alerts.')
param alertEmail string = ''

@description('Set to true to keep feature flags in Azure App Configuration (free tier), so they can be flipped without a deploy.')
param appConfiguration string = ''

@description('The API revision serving players now. A deploy keeps all traffic on it, so the new revision starts with none until infra/blue-green.sh has tested it. Leave empty on the very first deploy.')
param liveRevision string = ''

var suffix = uniqueString(resourceGroup().id)
var tags = { app: 'kings-of-the-card-arena' }
var databaseName = 'GameDb'
var usePrivateRegistry = !empty(registryUsername)
var featureFlagStoreEnabled = toLower(appConfiguration) == 'true'

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: apiIdentityName
}

// --- Monitoring ---

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${appName}-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // The first 5 GB a month are free; a cap keeps a log storm from costing money.
    workspaceCapping: { dailyQuotaGb: json('0.15') }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// --- Secrets ---

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${appName}-${take(suffix, 8)}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource jwtSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'jwt-signing-key'
  properties: { value: jwtSigningKey }
}

resource rabbitSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'rabbitmq-password'
  properties: { value: rabbitMqPassword }
}

var keyVaultSecretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource apiReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apiIdentity.id, keyVaultSecretsUser)
  scope: vault
  properties: {
    roleDefinitionId: keyVaultSecretsUser
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Feature flags ---

// The free tier allows one store per subscription, 10 MB and 1,000 requests a day; the API checks for
// changes at most every two minutes, and only while it's running. Flags that aren't in the store keep
// the defaults from appsettings.json (everything on), so an empty store changes nothing.
resource flags 'Microsoft.AppConfiguration/configurationStores@2024-05-01' = if (featureFlagStoreEnabled) {
  name: 'appcs-${appName}-${take(suffix, 8)}'
  location: location
  tags: tags
  sku: { name: 'free' }
  properties: {
    disableLocalAuth: true // Entra ID only: no access keys to leak
  }
}

var appConfigDataReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '516239f1-63e1-4d78-a4de-a74fb236a071')
var appConfigDataOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b')

resource apiReadsFlags 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (featureFlagStoreEnabled) {
  name: guid(flags.id, apiIdentity.id, appConfigDataReader)
  scope: flags
  properties: {
    roleDefinitionId: appConfigDataReader
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// The admins group (you) flips flags in the portal under App Configuration > Feature manager.
resource adminsEditFlags 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (featureFlagStoreEnabled) {
  name: guid(flags.id, sqlAdminGroupObjectId, appConfigDataOwner)
  scope: flags
  properties: {
    roleDefinitionId: appConfigDataOwner
    principalId: sqlAdminGroupObjectId
    principalType: 'Group'
  }
}

// --- Portraits ---

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${appName}${take(suffix, 10)}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    // Portraits are shown straight from blob storage, so their container allows anonymous reads...
    allowBlobPublicAccess: true
    // ...but nothing can write with an account key: only the API's managed identity can upload.
    allowSharedKeyAccess: false
  }
}

// Deleted or overwritten portraits stay recoverable for 7 days (see docs/disaster-recovery.md).
// A player's old portrait is deleted when they upload a new one, so this is the undo for that too.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

var storageBlobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource apiWritesBlobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, apiIdentity.id, storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: storageBlobDataContributor
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Database ---

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  // The region is part of the name, so moving the database never collides with a server left behind elsewhere.
  name: 'sql-${appName}-${uniqueString(resourceGroup().id, sqlLocation)}'
  location: sqlLocation
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // No SQL logins or passwords: members of the admin group (the API's identity and you) sign in with Entra ID.
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

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: sqlLocation
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    // The Azure SQL free offer: 100,000 vCore seconds and 32 GB a month. When the month's
    // allowance runs out the database pauses instead of billing.
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// --- Client ---

resource client 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'swa-${appName}-${suffix}'
  location: staticWebAppLocation
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

// Azure checks the CNAME record and then issues and renews a free certificate for the domain.
resource clientDomain 'Microsoft.Web/staticSites/customDomains@2023-12-01' = if (!empty(customDomain)) {
  parent: client
  name: customDomain
  properties: {
    validationMethod: 'cname-delegation'
  }
}

// --- API ---

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${appName}-${suffix}'
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

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;User Id=${apiIdentity.properties.clientId};Encrypt=True;Connect Timeout=60'

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${appName}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${apiIdentity.id}': {} }
  }
  dependsOn: [apiReadsSecrets, apiWritesBlobs, apiReadsFlags, allowAzureServices, database]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      // Blue-green: each deploy adds a revision beside the live one. Traffic stays pinned to the live
      // revision by name until the new one passes its smoke test (see docs/adr/0016-blue-green-deploys.md).
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto' // HTTP/1.1 and WebSockets, for the SignalR hubs
        allowInsecure: false
        traffic: empty(liveRevision) ? [
          { latestRevision: true, weight: 100 }
        ] : [
          { revisionName: liveRevision, weight: 100 }
        ]
      }
      secrets: concat([
        {
          name: 'jwt-signing-key'
          keyVaultUrl: jwtSecret.properties.secretUri
          identity: apiIdentity.id
        }
        {
          name: 'rabbitmq-password'
          keyVaultUrl: rabbitSecret.properties.secretUri
          identity: apiIdentity.id
        }
      ], usePrivateRegistry ? [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ] : [])
      registries: usePrivateRegistry ? [
        {
          server: 'ghcr.io'
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'AZURE_CLIENT_ID', value: apiIdentity.properties.clientId }
            { name: 'ConnectionStrings__DefaultConnection', value: sqlConnectionString }
            { name: 'ConnectionStrings__AzureBlobStorage', value: storage.properties.primaryEndpoints.blob }
            { name: 'Jwt__SigningKey', secretRef: 'jwt-signing-key' }
            { name: 'RabbitMq__HostName', value: '127.0.0.1' }
            { name: 'RabbitMq__UserName', value: 'cardarena' }
            { name: 'RabbitMq__Password', secretRef: 'rabbitmq-password' }
            { name: 'ForwardedHeaders__TrustAllProxies', value: 'true' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
            { name: 'Cors__AllowedOrigins__0', value: 'https://${client.properties.defaultHostname}' }
          ], empty(customDomain) ? [] : [
            { name: 'Cors__AllowedOrigins__1', value: 'https://${customDomain}' }
          ], featureFlagStoreEnabled ? [
            { name: 'AppConfig__Endpoint', value: flags!.properties.endpoint }
          ] : [])
          probes: [
            {
              // Database migrations run before the API starts listening, and a paused database can
              // take a minute to wake, so allow up to five minutes before liveness checks begin.
              type: 'Startup'
              httpGet: { path: '/health/live', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
        }
        {
          // RabbitMQ runs beside the API in the same replica (it's reached on localhost), so it costs
          // no extra app. Its queue lives in the container, which is fine here: the API publishes and
          // consumes in the same replica and drains the queue within seconds.
          name: 'rabbitmq'
          image: 'docker.io/library/rabbitmq:3.13-alpine'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'RABBITMQ_DEFAULT_USER', value: 'cardarena' }
            { name: 'RABBITMQ_DEFAULT_PASS', secretRef: 'rabbitmq-password' }
          ]
        }
      ]
      scale: {
        // Scale to zero when nobody is playing, so an idle game costs nothing. The first visit after
        // a quiet spell waits for the container (and the database) to start.
        minReplicas: 0
        // The PvP lobby is kept in memory, so there must never be two replicas of a revision. During a
        // release the new revision runs beside the live one, but players only ever reach one of them.
        maxReplicas: 1
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
}

// --- Alerts ---
// Email is free up to 1,000 a month. The log alert runs every 15 minutes (about $0.50 a month) and the
// metric alert costs about $0.10 a month.

var alertsEnabled = !empty(alertEmail)

resource alertEmails 'Microsoft.Insights/actionGroups@2023-01-01' = if (alertsEnabled) {
  name: 'ag-${appName}-email'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'cardarena'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Health probes aren't traced, so these are real players' requests failing.
resource serverErrorsAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (alertsEnabled) {
  name: 'alert-${appName}-server-errors'
  location: location
  tags: tags
  properties: {
    displayName: 'Card Arena API is returning server errors'
    description: 'Five or more requests failed with a 5xx status in the last 15 minutes. Open Application Insights > Failures to see which endpoint and exception.'
    severity: 1
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'requests | where toint(resultCode) >= 500'
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 5
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: true
    actions: {
      actionGroups: [alertEmails.id]
    }
  }
}

// Repeated restarts mean the container is crashing or failing its liveness probe. Scaling to zero
// when idle doesn't count as a restart.
resource restartsAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (alertsEnabled) {
  name: 'alert-${appName}-api-restarts'
  location: 'global'
  tags: tags
  properties: {
    description: 'The API container restarted 3 or more times in 15 minutes. Check the Container App\'s Log stream and revision status.'
    severity: 1
    enabled: true
    scopes: [api.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'restarts'
          metricNamespace: 'Microsoft.App/containerApps'
          metricName: 'RestartCount'
          timeAggregation: 'Total'
          operator: 'GreaterThanOrEqual'
          threshold: 3
        }
      ]
    }
    autoMitigate: true
    actions: [
      { actionGroupId: alertEmails.id }
    ]
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output clientUrl string = 'https://${client.properties.defaultHostname}'
output gameUrl string = empty(customDomain) ? 'https://${client.properties.defaultHostname}' : 'https://${customDomain}'
output staticWebAppName string = client.name
output apiContainerAppName string = api.name
output sqlServer string = sqlServer.properties.fullyQualifiedDomainName
output storageAccount string = storage.name
// Browser telemetry (wwwroot/js/telemetry.js). Not a secret: a connection string only lets a browser
// send telemetry, and every page using the Application Insights JavaScript SDK carries one.
output appInsightsConnectionString string = appInsights.properties.ConnectionString
