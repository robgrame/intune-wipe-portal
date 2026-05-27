targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Intune Wipe Portal — Bicep (App Service Linux + UAMI + RBAC on existing LAW)
//
// Deploys a Blazor Server portal that reads structured wipe audit events from
// the Log Analytics workspace already provisioned by the intune-wipe-api
// deployment in the same resource group.
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

@description('Name of the existing Log Analytics workspace that hosts the wipe customEvents (created by intune-wipe-api).')
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

// ---------------------------------------------------------------------------
var suffix       = uniqueString(resourceGroup().id)
var planName     = toLower('${namePrefix}-plan-portal-${suffix}')
var webAppName   = toLower('${namePrefix}-portal-${suffix}')
var uamiName     = toLower('${namePrefix}-uami-portal-${suffix}')

// Built-in role id for "Log Analytics Reader".
var roleLogAnalyticsReader = '73c42c96-874c-492b-b04d-ab87d138a893'

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
      ]
    }
  }
}

output webAppName string = web.name
output webAppHostname string = web.properties.defaultHostName
output uamiClientId string = uami.properties.clientId
output uamiPrincipalId string = uami.properties.principalId
output workspaceCustomerId string = law.properties.customerId
