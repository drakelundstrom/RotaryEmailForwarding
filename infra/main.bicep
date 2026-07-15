targetScope = 'resourceGroup'

@description('Short environment label used in resource names and app settings.')
@allowed([
  'test'
  'prod'
])
param environmentName string

@description('Azure region for all resources in this environment.')
param location string = resourceGroup().location

@description('Function App name. Must be globally unique.')
param functionAppName string

@description('Flex Consumption hosting plan name.')
param functionPlanName string

@description('Storage account name for Azure Functions host/deployment storage.')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Application Insights component name.')
param applicationInsightsName string

@description('Log Analytics workspace name.')
param logAnalyticsWorkspaceName string

@description('Key Vault name for application secrets and Key Vault references.')
param keyVaultName string

@description('Cosmos DB account name.')
param cosmosAccountName string

@description('Resource group that contains the shared Cosmos DB account.')
param cosmosAccountResourceGroupName string

@description('Cosmos DB SQL database name.')
param cosmosDatabaseName string = 'EmailForwarding'

@description('Cosmos DB SQL container name.')
param cosmosContainerName string = 'ContactInfoAndRequests'

@description('Provisioned throughput for the Cosmos DB SQL container.')
@minValue(400)
@maxValue(1000000)
param cosmosContainerThroughput int = 400

@description('Canonical SMTP host setting. Credentials are supplied through Key Vault.')
param mailHost string = 'smtp.gmail.com'

@description('Canonical SMTP port setting.')
param mailPort string = '587'

@description('Canonical SMTP security mode setting.')
param mailSecurityMode string = 'StartTls'

@description('Timezone used by the 3:00 AM email retry timer.')
param emailRetryTimeZone string = 'Eastern Standard Time'

@description('Maximum request body size accepted by the public submission endpoint.')
param maxRequestBodyBytes string = '131072'

@description('Maximum scale-out instance count for the Flex Consumption app.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

@description('Memory allocated to each Flex Consumption instance.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

@description('Enable zone redundancy for regions that support it.')
param zoneRedundant bool = false

var tags = {
  application: 'RotaryEmailForwarding'
  environment: environmentName
  managedBy: 'bicep'
}

var deploymentPackageContainerName = 'function-releases'
var keyVaultSecretUriPrefix = '${keyVault.properties.vaultUri}secrets'
var functionAppSettings = {
  AzureWebJobsStorage__credential: 'managedidentity'
  AzureWebJobsStorage__blobServiceUri: 'https://${storage.name}.blob.${environment().suffixes.storage}'
  AzureWebJobsStorage__queueServiceUri: 'https://${storage.name}.queue.${environment().suffixes.storage}'
  AzureWebJobsStorage__tableServiceUri: 'https://${storage.name}.table.${environment().suffixes.storage}'
  FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
  APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsights.properties.ConnectionString
  APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
  appEnvironment: environmentName
  cosmosDatabaseName: cosmosDatabaseName
  cosmosContainerName: cosmosContainerName
  cosmosAccountEndpoint: cosmosAccount.properties.documentEndpoint
  databaseConnectionString: '@Microsoft.KeyVault(SecretUri=${databaseConnectionStringSecret.properties.secretUri})'
  sendingEmailAddress: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretUriPrefix}/sendingEmailAddress)'
  operatorEmail: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretUriPrefix}/operatorEmail)'
  supportEmail: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretUriPrefix}/supportEmail)'
  sendingEmailPassword: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretUriPrefix}/sendingEmailPassword)'
  mailHost: mailHost
  mailPort: mailPort
  mailSecurityMode: mailSecurityMode
  emailRetryTimeZone: emailRetryTimeZone
  adminApiKey: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretUriPrefix}/adminApiKey)'
  nonProductionSafeRecipient: environmentName == 'prod' ? '' : '@Microsoft.KeyVault(SecretUri=${keyVaultSecretUriPrefix}/nonProductionSafeRecipient)'
  allowUnsafeNonProductionEmail: 'false'
  maxRequestBodyBytes: maxRequestBodyBytes
  FUNCTIONS_REQUEST_BODY_SIZE_LIMIT: maxRequestBodyBytes
}

var storageBlobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var storageQueueDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var storageTableDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
var keyVaultSecretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var monitoringMetricsPublisherRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    DisableLocalAuth: true
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource deploymentPackageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentPackageContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
  scope: resourceGroup(cosmosAccountResourceGroupName)
}

module cosmosResources 'cosmos.bicep' = {
  name: 'cosmos-${environmentName}'
  scope: resourceGroup(cosmosAccountResourceGroupName)
  params: {
    cosmosAccountName: cosmosAccountName
    cosmosDatabaseName: cosmosDatabaseName
    cosmosContainerName: cosmosContainerName
    cosmosContainerThroughput: cosmosContainerThroughput
  }
}

resource databaseConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'databaseConnectionString'
  properties: {
    value: cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString
  }
  dependsOn: [
    cosmosResources
  ]
}

resource functionPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: functionPlanName
  location: location
  kind: 'functionapp'
  tags: tags
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
    zoneRedundant: zoneRedundant
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentPackageContainer.name}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      cors: {
        allowedOrigins: [
          'https://portal.azure.com'
          'https://ms.portal.azure.com'
        ]
        supportCredentials: true
      }
    }
  }
}

resource functionAppAppSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: functionAppSettings
  dependsOn: [
    functionKeyVaultRoleAssignment
  ]
}

resource functionBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, 'blob')
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataOwnerRoleId
  }
}

resource functionTableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, 'table')
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource functionQueueRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, 'queue')
  scope: storage
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageQueueDataContributorRoleId
  }
}

resource functionKeyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, 'secrets-user')
  scope: keyVault
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleId
  }
}

resource functionApplicationInsightsRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, functionApp.id, 'monitoring-metrics-publisher')
  scope: applicationInsights
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: monitoringMetricsPublisherRoleId
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output keyVaultName string = keyVault.name
output cosmosAccountName string = cosmosAccount.name
output cosmosDatabaseName string = cosmosResources.outputs.databaseName
output cosmosContainerName string = cosmosResources.outputs.containerName
