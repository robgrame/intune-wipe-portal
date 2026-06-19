<#
.SYNOPSIS
End-to-end deployment of the Intune Device Actions Portal.

.DESCRIPTION
1. Verifies prerequisites (az login, dotnet, target RG).
2. Ensures the Entra app registration exists (delegates to
   infra/entra/create-app-registration.ps1) and captures Tenant/Client/Secret.
3. Runs `az deployment group create` against infra/main.bicep with the
   captured Entra values + the existing Log Analytics workspace name.
4. Publishes the Blazor app (Release, Linux x64), zips and pushes via
   `az webapp deploy --type zip`.

This script is idempotent; subsequent runs update the existing app
registration, redeploy infra in place, and replace the running app.

.PARAMETER ResourceGroup
Target resource group. Default: rg-intwipe-dev.

.PARAMETER Location
Azure location for the new resources. Default: westeurope.

.PARAMETER LogAnalyticsWorkspaceName
Name of the EXISTING workspace created by the intune-wipe-api deployment.
The portal will be granted Log Analytics Reader on it.

.PARAMETER AssignUserUpn
Optional. UPN of the user to assign to the portal app role on first run.

.PARAMETER AssignRole
Role to grant the user. Default: Actions.Observer.

.PARAMETER SkipAppRegistration
Skip step 2 (useful when the app reg already exists and TenantId/ClientId/Secret
are provided as parameters).

.PARAMETER EntraTenantId / EntraClientId / EntraClientSecret
Use when -SkipAppRegistration is set, or when the secret was stored in a
secure location and you don't want this script to rotate it.

.EXAMPLE
./infra/deploy.ps1 `
    -ResourceGroup rg-intwipe-dev `
    -LogAnalyticsWorkspaceName intwipe-law-qupxwx6egkr3e `
    -AssignUserUpn alice@contoso.com
#>

[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-intwipe-dev',
    [string] $Location      = 'westeurope',
    [Parameter(Mandatory)]
    [string] $LogAnalyticsWorkspaceName,

    [string] $NamePrefix      = 'intwipe',
    [ValidateSet('B1','B2','P0v3','P1v3')]
    [string] $AppServicePlanSku = 'B1',

    # Optional override for the per-resource suffix. Defaults to whatever
    # uniqueString(resourceGroup().id) produces (= back-compat with the
    # original deployment). Pass an empty string to deploy without any
    # suffix at all (e.g. <prefix>-portal instead of <prefix>-portal-<sfx>).
    # Must be lowercase alphanumeric.
    [AllowEmptyString()]
    [ValidatePattern('^[a-z0-9]*$')]
    [string] $NameSuffix = $null,

    [string] $AssignUserUpn,
    [ValidateSet('Actions.Observer','Actions.Auditor')]
    [string] $AssignRole = 'Actions.Observer',

    [switch] $SkipAppRegistration,
    [string] $EntraTenantId,
    [string] $EntraClientId,
    [SecureString] $EntraClientSecret,
    [switch] $RequireAssignment,
    # Skip infra (Bicep) + Entra reply URL update. Only build & zip deploy the app code.
    [switch] $SkipInfra
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Write-Host "==> Repo root: $root"

# Track whether the caller explicitly passed -NameSuffix (even as an empty
# string). When they did, we forward it to Bicep so the user can opt in to
# the no-suffix layout; otherwise we omit the parameter and Bicep falls back
# to uniqueString(resourceGroup().id) for back-compat.
$script:NameSuffixOverridden = $PSBoundParameters.ContainsKey('NameSuffix')

# --- Step 0: prerequisites -------------------------------------------------
Write-Host "==> Verifying prerequisites..."
foreach ($cmd in 'az','dotnet') {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "$cmd not found in PATH."
    }
}
$account = az account show -o json | ConvertFrom-Json
if (-not $account) { throw "az login required." }
Write-Host "    Subscription: $($account.name) ($($account.id))"
Write-Host "    Tenant:       $($account.tenantId)"

# Ensure target RG exists.
$rgExists = az group exists --name $ResourceGroup
if ($rgExists -ne 'true') {
    Write-Host "==> Creating resource group $ResourceGroup in $Location..."
    az group create --name $ResourceGroup --location $Location | Out-Null
}

# Confirm the existing workspace. Use `az resource show` (core CLI) rather
# than `az monitor log-analytics workspace show` to avoid pulling the
# `log-analytics` CLI extension, which is fragile on Windows and not needed
# for this read-only existence/customerId check.
$law = az resource show `
    --resource-group $ResourceGroup `
    --name $LogAnalyticsWorkspaceName `
    --resource-type 'Microsoft.OperationalInsights/workspaces' `
    --query "{customerId: properties.customerId}" `
    -o json 2>$null | ConvertFrom-Json
if (-not $law) {
    throw "Log Analytics workspace '$LogAnalyticsWorkspaceName' not found in '$ResourceGroup'. Deploy intune-device-actions first."
}
Write-Host "    Workspace customerId: $($law.customerId)"

# --- Step 1: app registration ----------------------------------------------
if ($SkipInfra) {
    Write-Host ""
    Write-Host "==> Skipping app registration + Bicep (SkipInfra). Code-only deploy."
    # Resolve the web app name from the existing deployment.
    $webAppName = (& az webapp list -g $ResourceGroup `
        --query "[?contains(name, '$NamePrefix-portal')].name | [0]" `
        -o tsv --only-show-errors)
    if (-not $webAppName) {
        throw "Cannot find existing portal web app matching '$NamePrefix-portal*' in $ResourceGroup. Run without -SkipInfra first."
    }
    $webAppName = $webAppName.Trim()
    Write-Host "    Target app: $webAppName"
} else {
if (-not $SkipAppRegistration) {
    Write-Host ""
    Write-Host "==> Ensuring Entra app registration..."
    # We don't know the final hostname yet (depends on bicep uniqueString),
    # so first pass uses a placeholder. Step 3 below patches the real URL
    # once the Web App exists.
    $placeholderHost = "placeholder-$NamePrefix.invalid"
    $createArgs = @{
        WebAppHostname     = $placeholderHost
        CreateClientSecret = $true
    }
    if ($RequireAssignment) { $createArgs.RequireAssignment = $true }

    $reg = & (Join-Path $PSScriptRoot 'entra\create-app-registration.ps1') @createArgs
    if (-not $reg -or -not $reg.ClientId -or -not $reg.ClientSecret) {
        throw "create-app-registration.ps1 did not return TenantId/ClientId/ClientSecret."
    }
    $EntraTenantId    = $reg.TenantId
    $EntraClientId    = $reg.ClientId
    $EntraClientSecret = ConvertTo-SecureString $reg.ClientSecret -AsPlainText -Force
} else {
    if (-not $EntraTenantId -or -not $EntraClientId -or -not $EntraClientSecret) {
        throw "When -SkipAppRegistration is set, -EntraTenantId, -EntraClientId, and -EntraClientSecret are required."
    }
}

$entraDomain = "$($account.tenantDefaultDomain)"
if ([string]::IsNullOrWhiteSpace($entraDomain)) {
    $entraDomain = (az rest --method GET --uri 'https://graph.microsoft.com/v1.0/organization?$select=verifiedDomains' -o json |
        ConvertFrom-Json).value[0].verifiedDomains |
        Where-Object isDefault -eq $true |
        Select-Object -ExpandProperty name
}

# --- Step 2: infra deployment ----------------------------------------------
Write-Host ""
Write-Host "==> Deploying Bicep..."
$secretPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($EntraClientSecret))

$deployParams = @(
    "namePrefix=$NamePrefix"
    "logAnalyticsWorkspaceName=$LogAnalyticsWorkspaceName"
    "entraTenantId=$EntraTenantId"
    "entraClientId=$EntraClientId"
    "entraDomain=$entraDomain"
    "entraClientSecret=$secretPlain"
    "appServicePlanSku=$AppServicePlanSku"
)
if ($script:NameSuffixOverridden) {
    # Forward nameSuffix only when the caller explicitly passed -NameSuffix.
    # Empty string is meaningful (= deploy without any suffix) so we forward
    # it as-is; Bicep then disables the separator dash via empty(nameSuffix).
    $deployParams += "nameSuffix=$NameSuffix"
}
# Auto-detect App Configuration endpoint from the RG (created by API infra).
$appcfgEndpoint = (az appconfig list -g $ResourceGroup --query "[0].endpoint" -o tsv --only-show-errors 2>$null)
if ($appcfgEndpoint) {
    $deployParams += "appConfigEndpoint=$($appcfgEndpoint.Trim())"
    Write-Host "    App Config: $($appcfgEndpoint.Trim())"
}
# Auto-detect wipe schedule storage account (Web role storage, contains 'stw').
$wipeStAccount = (az storage account list -g $ResourceGroup -o json --only-show-errors 2>$null | ConvertFrom-Json) |
    Where-Object { $_.name -like "${NamePrefix}stw*" } | Select-Object -First 1 -ExpandProperty name
if ($wipeStAccount) {
    $deployParams += "wipeScheduleStorageAccount=$wipeStAccount"
    Write-Host "    Wipe Schedule SA: $wipeStAccount"
}

$deployment = az deployment group create `
    --resource-group $ResourceGroup `
    --template-file (Join-Path $PSScriptRoot 'main.bicep') `
    --parameters @deployParams `
    -o json | ConvertFrom-Json

$webAppName = $deployment.properties.outputs.webAppName.value
$hostname   = $deployment.properties.outputs.webAppHostname.value
Write-Host "    Web App: https://$hostname"

# --- Step 3: patch reply URLs with real hostname ---------------------------
Write-Host ""
Write-Host "==> Updating Entra reply URLs to https://$hostname/signin-oidc..."
az ad app update --id $EntraClientId `
    --web-redirect-uris "https://$hostname/signin-oidc" "https://$hostname/signout-callback-oidc" | Out-Null

} # end of: } else { (not SkipInfra)

# --- Step 4: build & deploy app code ---------------------------------------
Write-Host ""
Write-Host "==> Publishing Blazor app (linux-x64)..."
$publishDir = Join-Path $root 'publish'
$zipPath    = Join-Path $root 'publish.zip'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $zipPath)    { Remove-Item -Force $zipPath }

Push-Location $root
try {
    dotnet publish 'intune-wipe-portal.csproj' `
        -c Release `
        -r linux-x64 --self-contained false `
        -o $publishDir | Out-Host
} finally { Pop-Location }

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force

Write-Host "==> Pushing zip to $webAppName..."
az webapp deploy `
    --resource-group $ResourceGroup `
    --name $webAppName `
    --src-path $zipPath `
    --type zip | Out-Null

# --- Step 5: optional role assignment --------------------------------------
if ($AssignUserUpn) {
    Write-Host ""
    Write-Host "==> Assigning $AssignUserUpn -> $AssignRole on the portal app..."
    & (Join-Path $PSScriptRoot 'entra\create-app-registration.ps1') `
        -WebAppHostname $hostname `
        -AssignUserUpn $AssignUserUpn `
        -AssignRole $AssignRole | Out-Null
}

Write-Host ""
Write-Host "✔  Deployment complete."
Write-Host "    URL: https://$hostname"
Write-Host "    Sign in with $($AssignUserUpn ?? '<an assigned user>')"
