targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Intune Device Actions Portal — Bicep (App Service Linux + UAMI + RBAC on existing LAW)
//
// Deploys a Blazor Server portal that reads structured device-actions audit
// events (wipe / autopilot-register / bitlocker-rotate) from the Log Analytics
// workspace already provisioned by the intune-device-actions deployment in
// the same resource group.
//
// The portal does NOT create a new LAW or App Insights — it queries the
// existing one via a User-Assigned Managed Identity with the role
// `Log Analytics Reader` on the workspace.
//
// Auth (Entra ID OIDC) is configured via app settings AzureAd__*; the client
// secret should ideally be a Key Vault reference and NOT a literal value.
// ---------------------------------------------------------------------------

@minLength(3)
@maxLength(12)
param namePrefix string = 'intwipe'
param location string = resourceGroup().location

@description('Name of the existing Log Analytics workspace that hosts the device-actions audit events (created by intune-device-actions).')
param logAnalyticsWorkspaceName string

@description('Entra ID tenant id for OIDC sign-in.')
param entraTenantId string = subscription().tenantId

@description('App Registration ClientId for the portal (created by infra/entra/create-app-registration.ps1).')
param entraClientId string

@description('Verified domain of the tenant, e.g. contoso.onmicrosoft.com.')
param entraDomain string

@description('Client secret for the portal app registration. Pass via Key Vault reference in production, e.g. @Microsoft.KeyVault(SecretUri=...).')
@secure()
param entraClientSecret string

@description('App Service Plan SKU. B1 for dev; P0v3 or P1v3 for prod.')
@allowed([ 'B1', 'B2', 'P0v3', 'P1v3' ])
param appServicePlanSku string = 'B1'

@description('Run with HTTPS-only enforcement.')
param httpsOnly bool = true

@description('Optional suffix appended to every resource name to keep them globally unique. Defaults to uniqueString(resourceGroup().id) for back-compat. Pass an empty string to deploy without any suffix (resource names will be e.g. <namePrefix>-portal). Must be lowercase alphanumeric.')
@maxLength(13)
param nameSuffix string = uniqueString(resourceGroup().id)

@description('Fully-qualified Service Bus namespace hosting the action queues (e.g. idactions-sb-dev.servicebus.windows.net). Required by the cruscotto to read queue runtime properties.')
param serviceBusFullyQualifiedNamespace string = ''

@description('Name of the existing Service Bus namespace (in the same RG). Used to scope the data-owner role assignment. Leave empty to derive from <namePrefix>-sb<sep><suffix>.')
param serviceBusNamespaceName string = ''

@description('Name of the existing storage account that holds the action-ledger container (Proc storage). Leave empty to derive from <namePrefix>stproc<suffix> (no separator, no hyphens — storage account naming).')
param ledgerStorageAccountName string = ''

@description('Container name on ledgerStorageAccountName that holds the per-device ledger JSON blobs.')
param ledgerContainerName string = 'action-ledger'

@description('Endpoint of the Azure App Configuration store used by the API. The portal uses this to manage operational settings. Leave empty to disable the Configuration page.')
param appConfigEndpoint string = ''

// ---------------------------------------------------------------------------
var suffix       = nameSuffix
var sep          = empty(suffix) ? '' : '-'
var planName     = toLower('${namePrefix}-plan-portal${sep}${suffix}')
var webAppName   = toLower('${namePrefix}-portal${sep}${suffix}')
var uamiName     = toLower('${namePrefix}-uami-portal${sep}${suffix}')

// Derive Service Bus namespace + Proc storage account from convention if not supplied explicitly.
var sbNsResolved      = empty(serviceBusNamespaceName)        ? toLower('${namePrefix}-sb${sep}${suffix}')        : serviceBusNamespaceName
var sbFqdnResolved    = empty(serviceBusFullyQualifiedNamespace) ? '${sbNsResolved}.servicebus.windows.net' : serviceBusFullyQualifiedNamespace
var ledgerAcctResolved = empty(ledgerStorageAccountName)      ? toLower('${namePrefix}stp${suffix}')               : ledgerStorageAccountName

// Built-in role ids.
var roleLogAnalyticsReader        = '73c42c96-874c-492b-b04d-ab87d138a893'
var roleStorageBlobDataReader     = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var roleServiceBusDataOwner       = '090c5cfd-751d-490a-894a-3ce6f1109419'

// ---------------------------------------------------------------------------
// User-assigned managed identity used by the portal to call the Log Analytics
// query API. Separate from any UAMI used by intune-wipe-api so it can hold
// the least-privilege role on exactly one workspace.
resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
}

// Reference the EXISTING workspace owned by the api repo deployment.
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

// Grant the portal UAMI read access on the workspace.
resource lawReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: law
  name: guid(law.id, uami.id, roleLogAnalyticsReader)
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleLogAnalyticsReader)
  }
}

// Reference the EXISTING Service Bus namespace owned by the API repo deployment.
// Cruscotto needs Azure Service Bus Data Owner to call GetQueueRuntimePropertiesAsync.
resource sbNs 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: sbNsResolved
}
resource sbOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: sbNs
  name: guid(sbNs.id, uami.id, roleServiceBusDataOwner)
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleServiceBusDataOwner)
  }
}

// Reference the EXISTING Proc storage account that holds the action-ledger container.
// Cruscotto needs Storage Blob Data Reader to enumerate & download ledger blobs.
resource ledgerStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: ledgerAcctResolved
}
resource ledgerReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: ledgerStorage
  name: guid(ledgerStorage.id, uami.id, roleStorageBlobDataReader)
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleStorageBlobDataReader)
  }
}

// ---------------------------------------------------------------------------
// App Service Plan — Linux. Dedicated to the portal so it doesn't share
// host VMs with the wipe Function Apps (defense-in-depth: a hypothetical
// host escape on the portal cannot read the worker's environment).
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  sku: { name: appServicePlanSku }
  kind: 'linux'
  properties: { reserved: true }
}

// Web App — Blazor Server (.NET 10).
resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${uami.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: httpsOnly
    keyVaultReferenceIdentity: uami.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      http20Enabled: true
      ftpsState: 'Disabled'
      alwaysOn: appServicePlanSku != 'B1'
      use32BitWorkerProcess: false
      appSettings: [
        // Pick the right UAMI when multiple identities are attached.
        { name: 'AZURE_CLIENT_ID',                 value: uami.properties.clientId }
        { name: 'Monitor__WorkspaceId',            value: law.properties.customerId }
        { name: 'AzureAd__Instance',               value: 'https://login.microsoftonline.com/' }
        { name: 'AzureAd__Domain',                 value: entraDomain }
        { name: 'AzureAd__TenantId',               value: entraTenantId }
        { name: 'AzureAd__ClientId',               value: entraClientId }
        { name: 'AzureAd__ClientSecret',           value: entraClientSecret }
        { name: 'AzureAd__CallbackPath',           value: '/signin-oidc' }
        { name: 'AzureAd__SignedOutCallbackPath',  value: '/signout-callback-oidc' }
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE',        value: '1' }
        { name: 'Cruscotto__ServiceBusFullyQualifiedNamespace', value: sbFqdnResolved }
        { name: 'Cruscotto__LedgerStorageAccount', value: ledgerAcctResolved }
        { name: 'Cruscotto__LedgerContainer',      value: ledgerContainerName }
        { name: 'AppConfig__Endpoint',             value: appConfigEndpoint }
      ]
    }
  }
}

output webAppName string = web.name
output webAppHostname string = web.properties.defaultHostName
output uamiClientId string = uami.properties.clientId
output uamiPrincipalId string = uami.properties.principalId
output workspaceCustomerId string = law.properties.customerId
