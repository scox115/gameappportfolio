// Kings of the Card Arena on Azure, sized for free and near-free tiers.
//
//   Static Web Apps (Free)            Blazor WebAssembly client
//   Container Apps (Consumption)      API + RabbitMQ sidecar, scales to zero, and out to maxReplicas under load
//   Azure SQL Database (free offer)   serverless, pauses when idle, Entra ID sign-in only
//   Storage account (Standard LRS)    portraits, reached with the API's managed identity
//   Key Vault (Standard)              JWT signing key and RabbitMQ password
//   Log Analytics + App Insights      logs, traces and metrics from OpenTelemetry
//
// The managed identity and the SQL admin group are created once by infra/setup.ps1, because
// creating Entra groups needs directory permissions the deploy pipeline shouldn't have.
//
// With sharedResourceGroup set, the API runs in the shared portfolio base's Container Apps environment
// and its database lives on the shared SQL server (infra/shared.bicep), instead of the game having its
// own. See docs/adr/0040-shared-portfolio-base.md.

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

@description('Set to true to send account recovery emails with Azure Communication Services (see docs/adr/0022-account-recovery-by-email.md). Needs the Microsoft.Communication resource provider registered first.')
param emailRecovery string = ''

@description('Set to true to screen uploaded portraits with Azure AI Content Safety on its free tier (see docs/adr/0029-portrait-screening.md). Needs the Microsoft.CognitiveServices resource provider registered first.')
param contentSafety string = ''

@description('Usernames that are admins, separated by commas (see docs/adr/0021-admin-roles-and-audit-log.md). Leave empty for none.')
param adminUsernames string = ''

@description('The API revision serving players now. A deploy keeps all traffic on it, so the new revision starts with none until infra/blue-green.sh has tested it. Leave empty on the very first deploy.')
param liveRevision string = ''

@description('Which copy of the game this is. Staging lives in its own resource group, so every resource gets its own name, and it also accepts calls from the Static Web App\'s pull request previews (see docs/adr/0026-staging-and-previews.md).')
@allowed(['production', 'staging'])
param environmentName string = 'production'

@description('The most API replicas Container Apps may run under load. Above 1, the replicas pass live messages to each other through SQL (see docs/adr/0027-scale-out.md). Set 1 to keep a single replica.')
@minValue(1)
@maxValue(10)
param maxReplicas int = 3

@description('Resource group of the shared portfolio base (infra/shared.bicep). Leave empty to give the game its own Container Apps environment and SQL server.')
param sharedResourceGroup string = ''

@description('The shared base\'s Container Apps environment (its "environmentName" output).')
param sharedEnvironmentName string = ''

@description('The shared base\'s SQL server (its "sqlServerName" output).')
param sharedSqlServerName string = ''

@description('The game\'s database on the shared SQL server.')
param sharedDatabaseName string = ''

@description('The shared base\'s region (its "location" output), where the API and its database run.')
param sharedLocation string = ''

var suffix = uniqueString(resourceGroup().id)
var tags = { app: 'kings-of-the-card-arena', environment: environmentName }
var isStaging = environmentName == 'staging'
var useSharedBase = !empty(sharedResourceGroup)
var databaseName = useSharedBase ? sharedDatabaseName : 'GameDb'
var usePrivateRegistry = !empty(registryUsername)
var featureFlagStoreEnabled = toLower(appConfiguration) == 'true'
var emailEnabled = toLower(emailRecovery) == 'true'
var contentSafetyEnabled = toLower(contentSafety) == 'true'

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

// --- Account recovery email ---
// Azure Communication Services with an Azure-managed sender domain (DoNotReply@<guid>.azurecomm.net):
// no DNS to set up, no monthly fee, about $0.00025 an email. The API sends with its managed identity.

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = if (emailEnabled) {
  name: 'ecs-${appName}-${take(suffix, 8)}'
  location: 'global'
  tags: tags
  properties: { dataLocation: 'United States' }
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = if (emailEnabled) {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled' // no tracking pixels or rewritten links in recovery emails
  }
}

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' = if (emailEnabled) {
  name: 'acs-${appName}-${take(suffix, 8)}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: 'United States'
    linkedDomains: [emailDomain.id]
  }
}

var communicationEmailServiceOwner = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '09976791-48a7-449e-bb21-39d1a415f350')

resource apiSendsEmail 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (emailEnabled) {
  name: guid(communication.id, apiIdentity.id, communicationEmailServiceOwner)
  scope: communication
  properties: {
    roleDefinitionId: communicationEmailServiceOwner
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Portrait screening ---
// Azure AI Content Safety on the free tier (5,000 images a month, 5 a second). Keys are switched off:
// the API signs in with its managed identity.

resource portraitScreen 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (contentSafetyEnabled) {
  name: 'cs-${appName}-${take(suffix, 8)}'
  location: location
  tags: tags
  kind: 'ContentSafety'
  sku: { name: 'F0' }
  properties: {
    customSubDomainName: 'cs-${appName}-${suffix}' // needed for Microsoft Entra ID sign-in
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

var cognitiveServicesUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')

resource apiScreensPortraits 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (contentSafetyEnabled) {
  name: guid(portraitScreen.id, apiIdentity.id, cognitiveServicesUser)
  scope: portraitScreen
  properties: {
    roleDefinitionId: cognitiveServicesUser
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
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
// On the shared SQL server when the game runs on the shared base, otherwise on a server of its own.

resource sharedSqlServer 'Microsoft.Sql/servers@2023-08-01-preview' existing = if (useSharedBase) {
  name: sharedSqlServerName
  scope: resourceGroup(sharedResourceGroup)
}

module sharedDatabase 'modules/database.bicep' = if (useSharedBase) {
  name: 'card-arena-database-${environmentName}'
  scope: resourceGroup(sharedResourceGroup)
  params: {
    serverName: sharedSqlServerName
    databaseName: databaseName
    location: sharedLocation
    tags: tags
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = if (!useSharedBase) {
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
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!useSharedBase) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = if (!useSharedBase) {
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

var sqlServerAddress = useSharedBase ? sharedSqlServer!.properties.fullyQualifiedDomainName : sqlServer!.properties.fullyQualifiedDomainName
var databaseId = useSharedBase ? sharedDatabase!.outputs.id : database!.id

// --- Client ---

resource client 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'swa-${appName}-${suffix}'
  location: staticWebAppLocation
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

// Where players reach the game: the custom domain when there is one. Used for links in emails.
var gameUrl = empty(customDomain) ? 'https://${client.properties.defaultHostname}' : 'https://${customDomain}'

// Azure checks the CNAME record and then issues and renews a free certificate for the domain.
resource clientDomain 'Microsoft.Web/staticSites/customDomains@2023-12-01' = if (!empty(customDomain)) {
  parent: client
  name: customDomain
  properties: {
    validationMethod: 'cname-delegation'
  }
}

// --- API ---

resource sharedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = if (useSharedBase) {
  name: sharedEnvironmentName
  scope: resourceGroup(sharedResourceGroup)
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = if (!useSharedBase) {
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

// A container app runs in its environment's region, which for the shared base is the database's.
var apiLocation = useSharedBase ? sharedLocation : location
var environmentId = useSharedBase ? sharedEnvironment.id : environment.id

var sqlConnectionString = 'Server=tcp:${sqlServerAddress},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;User Id=${apiIdentity.properties.clientId};Encrypt=True;Connect Timeout=60'

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${appName}-api'
  location: apiLocation
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${apiIdentity.id}': {} }
  }
  dependsOn: [apiReadsSecrets, apiWritesBlobs, apiReadsFlags, allowAzureServices, database, sharedDatabase]
  properties: {
    managedEnvironmentId: environmentId
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
            { name: 'Admin__Usernames', value: adminUsernames }
            { name: 'Email__ClientBaseUrl', value: gameUrl }
            // Players on different replicas still see each other's moves (see docs/adr/0027-scale-out.md).
            { name: 'ScaleOut__Backplane', value: maxReplicas > 1 ? 'Sql' : 'None' }
          ], emailEnabled ? [
            { name: 'Email__Provider', value: 'AzureCommunicationServices' }
            { name: 'Email__Endpoint', value: 'https://${communication!.properties.hostName}' }
            { name: 'Email__Sender', value: 'DoNotReply@${emailDomain!.properties.mailFromSenderDomain}' }
          ] : [], contentSafetyEnabled ? [
            { name: 'ContentSafety__Endpoint', value: portraitScreen!.properties.endpoint }
          ] : [], empty(customDomain) ? [] : [
            { name: 'Cors__AllowedOrigins__1', value: 'https://${customDomain}' }
          ], featureFlagStoreEnabled ? [
            { name: 'AppConfig__Endpoint', value: flags!.properties.endpoint }
          ] : [], isStaging ? [
            { name: 'Cors__PreviewsOf', value: client.properties.defaultHostname }
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
        // Each replica adds capacity under load. The lobby, the outbox and live messages are shared through
        // SQL, so a player may reach any replica (see docs/adr/0027-scale-out.md).
        maxReplicas: maxReplicas
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

// --- Ops dashboard ---
// An Azure Monitor workbook (free) with the live game's traffic, errors, speed, players, battles,
// releases and resources. It's kept as JSON next to this file, checked by check-workbook.cs in CI, and
// pointed at this environment's resources here. See docs/adr/0033-ops-dashboard.md.

resource opsDashboard 'Microsoft.Insights/workbooks@2023-06-01' = {
  // A workbook's name must be a GUID; this one is the same on every deploy, so the deploy updates it.
  name: guid(resourceGroup().id, 'ops-dashboard')
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'Card Arena operations (${environmentName})'
    category: 'workbook'
    sourceId: appInsights.id
    serializedData: replace(replace(replace(loadTextContent('ops-dashboard/workbook.json'),
      '__APP_INSIGHTS_ID__', appInsights.id),
      '__API_ID__', api.id),
      '__DATABASE_ID__', databaseId)
  }
}

// --- Alerts ---
// Email is free up to 1,000 a month. Each log alert runs every 15 minutes (about $0.50 a month) and the
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

// Slow answers are the first sign of a struggling database or an API at its replica limit. SignalR
// connections stay open for a whole visit, so they aren't response times; quiet periods are skipped
// because a handful of cold starts would trip it.
resource slowRequestsAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (alertsEnabled) {
  name: 'alert-${appName}-slow-requests'
  location: location
  tags: tags
  properties: {
    displayName: 'Card Arena API is slow'
    description: 'The slowest 5% of player requests took over 2 seconds in the last 15 minutes. Open the ops dashboard (Endpoints, slowest first) and the Azure resources section.'
    severity: 2
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'requests | where url !contains "/hubs/" | summarize Requests = count(), SlowestFivePercent = percentile(duration, 95) | where Requests >= 20'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'SlowestFivePercent'
          operator: 'GreaterThan'
          threshold: 2000
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

// The server can be fine while the game is broken in the browser, for example after a client release.
resource browserErrorsAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (alertsEnabled) {
  name: 'alert-${appName}-browser-errors'
  location: location
  tags: tags
  properties: {
    displayName: 'Card Arena is failing in players\' browsers'
    description: 'Players\' browsers reported 10 or more errors in the last 15 minutes. Open the ops dashboard (Errors in the API and in players\' browsers).'
    severity: 2
    enabled: true
    scopes: [appInsights.id]
    evaluationFrequency: 'PT15M'
    windowSize: 'PT15M'
    criteria: {
      allOf: [
        {
          query: 'exceptions | where client_Type == "Browser"'
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 10
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

output environmentName string = environmentName
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output clientUrl string = 'https://${client.properties.defaultHostname}'
output gameUrl string = gameUrl
output staticWebAppName string = client.name
output apiContainerAppName string = api.name
output sqlServer string = sqlServerAddress
output sqlResourceGroup string = useSharedBase ? sharedResourceGroup : resourceGroup().name
output databaseName string = databaseName
output storageAccount string = storage.name
// Browser telemetry (wwwroot/js/telemetry.js). Not a secret: a connection string only lets a browser
// send telemetry, and every page using the Application Insights JavaScript SDK carries one.
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output opsDashboardUrl string = 'https://portal.azure.com/#@${tenant().tenantId}/resource${opsDashboard.id}/workbook'
