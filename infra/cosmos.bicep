targetScope = 'resourceGroup'

@description('Existing Cosmos DB account name.')
param cosmosAccountName string

@description('Cosmos DB SQL database name.')
param cosmosDatabaseName string

@description('Cosmos DB SQL container name.')
param cosmosContainerName string

@description('Provisioned throughput for the Cosmos DB SQL container.')
@minValue(400)
@maxValue(1000000)
param cosmosContainerThroughput int

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
  }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: cosmosContainerName
  properties: {
    resource: {
      id: cosmosContainerName
      partitionKey: {
        paths: [
          '/Type'
        ]
        kind: 'Hash'
      }
    }
    options: {
      throughput: cosmosContainerThroughput
    }
  }
}

output databaseName string = cosmosDatabase.name
output containerName string = cosmosContainer.name
