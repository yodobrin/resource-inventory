targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the workload which is used to generate a short unique hash used in all resources.')
param workloadName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Name of the resource group. If empty, a unique name will be generated.')
param resourceGroupName string = ''

@description('Tags for all resources.')
param tags object = {}

var abbrs = loadJsonContent('./abbreviations.json')
var roles = loadJsonContent('./roles.json')
var resourceToken = toLower(uniqueString(subscription().id, workloadName, location))

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.managementGovernance.resourceGroup}${workloadName}'
  location: location
  tags: union(tags, {})
}

module managedIdentity './security/managed-identity.bicep' = {
  name: '${abbrs.security.managedIdentity}${resourceToken}'
  scope: resourceGroup
  params: {
    name: '${abbrs.security.managedIdentity}${resourceToken}'
    location: location
    tags: union(tags, {})
  }
}

// Required RBAC roles for reading resources across a subscription

resource readerRole 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' existing = {
  scope: resourceGroup
  name: roles.general.reader
}

module subscriptionRoleAssignment './security/subscription-role-assignment.bicep' = {
  name: 'subscription-role-assignment-${resourceToken}'
  scope: subscription()
  params: {
    roleAssignments: [
      {
        principalId: managedIdentity.outputs.principalId
        roleDefinitionId: readerRole.id
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

// Required RBAC roles for Azure Functions to access the storage account
// https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob&pivots=programming-language-csharp#connecting-to-host-storage-with-an-identity

resource storageAccountContributorRole 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' existing = {
  scope: resourceGroup
  name: roles.storage.storageAccountContributor
}

resource storageBlobDataOwnerRole 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' existing = {
  scope: resourceGroup
  name: roles.storage.storageBlobDataOwner
}

resource storageQueueDataContributorRole 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' existing = {
  scope: resourceGroup
  name: roles.storage.storageQueueDataContributor
}

resource storageTableDataContributorRole 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' existing = {
  scope: resourceGroup
  name: roles.storage.storageTableDataContributor
}

module storageAccount './storage/storage-account.bicep' = {
  name: '${abbrs.storage.storageAccount}${resourceToken}'
  scope: resourceGroup
  params: {
    name: '${abbrs.storage.storageAccount}${resourceToken}'
    location: location
    tags: union(tags, {})
    sku: {
      name: 'Standard_LRS'
    }
    roleAssignments: [
      {
        principalId: managedIdentity.outputs.principalId
        roleDefinitionId: storageAccountContributorRole.id
        principalType: 'ServicePrincipal'
      }
      {
        principalId: managedIdentity.outputs.principalId
        roleDefinitionId: storageBlobDataOwnerRole.id
        principalType: 'ServicePrincipal'
      }
      {
        principalId: managedIdentity.outputs.principalId
        roleDefinitionId: storageQueueDataContributorRole.id
        principalType: 'ServicePrincipal'
      }
      {
        principalId: managedIdentity.outputs.principalId
        roleDefinitionId: storageTableDataContributorRole.id
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

module appServicePlan './compute/app-service-plan.bicep' = {
  name: '${abbrs.compute.appServicePlan}${resourceToken}'
  scope: resourceGroup
  params: {
    name: '${abbrs.compute.appServicePlan}${resourceToken}'
    location: location
    tags: union(tags, {})
    sku: { name: 'Y1' }
    kind: 'linux'
    reserved: true
  }
}

module functionApp './compute/function-app.bicep' = {
  name: '${abbrs.compute.functionApp}${resourceToken}'
  scope: resourceGroup
  params: {
    name: '${abbrs.compute.functionApp}${resourceToken}'
    location: location
    tags: union(tags, {})
    identityId: managedIdentity.outputs.id
    appServicePlanId: appServicePlan.outputs.id
    appSettings: [
      {
        name: 'FUNCTIONS_EXTENSION_VERSION'
        value: '~4'
      }
      {
        name: 'FUNCTIONS_WORKER_RUNTIME'
        value: 'dotnet-isolated'
      }
      {
        name: 'AzureWebJobsStorage__accountName'
        value: storageAccount.outputs.name
      }
      {
        name: 'AzureWebJobsStorage__credential'
        value: 'managedidentity'
      }
      {
        name: 'AzureWebJobsStorage__clientId'
        value: managedIdentity.outputs.clientId
      }
      {
        name: 'MANAGED_IDENTITY_CLIENT_ID'
        value: managedIdentity.outputs.clientId
      }
    ]
  }
}
