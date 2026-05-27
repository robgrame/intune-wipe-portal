<#
.SYNOPSIS
Creates (or updates) the Entra ID App Registration that secures the
Intune Wipe Portal, defines the Wipe.Observer / Wipe.Auditor app roles,
and assigns a user (or group) to one of them.

.DESCRIPTION
The portal authenticates users with OpenID Connect (Microsoft.Identity.Web)
and gates access through a fallback authorization policy that requires
membership in either app role:

    Wipe.Observer  -> read dashboards
    Wipe.Auditor   -> read + audit-trail / export (future)

Run this script once per environment (dev / prod). It is idempotent:
re-runs update the existing app registration in place.

.PARAMETER DisplayName
Display name of the app registration. Default: "Intune Wipe Portal".

.PARAMETER ReplyUrl
The HTTPS reply URL of the deployed Web App, e.g.
  https://intwipe-portal-xyz.azurewebsites.net/signin-oidc

.PARAMETER AssignUserUpn
(Optional) UPN of the user to assign to the role.

.PARAMETER AssignRole
Which role to assign. Default: Wipe.Observer.

.EXAMPLE
./create-app-registration.ps1 `
    -ReplyUrl https://intwipe-portal-xyz.azurewebsites.net/signin-oidc `
    -AssignUserUpn alice@contoso.com `
    -AssignRole Wipe.Observer
#>

[CmdletBinding()]
param(
    [string] $DisplayName = "Intune Wipe Portal",
    [Parameter(Mandatory)] [string] $WebAppHostname,  # e.g. intwipe-portal-xyz.azurewebsites.net
    [string] $AssignUserUpn,
    [ValidateSet("Wipe.Observer", "Wipe.Auditor")]
    [string] $AssignRole = "Wipe.Observer",
    [switch] $CreateClientSecret,
    [switch] $RequireAssignment
)

$ErrorActionPreference = "Stop"
$rolesPath = Join-Path $PSScriptRoot "app-roles.json"
if (-not (Test-Path $rolesPath)) {
    throw "app-roles.json not found next to the script."
}

$signInUrl  = "https://$WebAppHostname/signin-oidc"
$signOutUrl = "https://$WebAppHostname/signout-callback-oidc"

Write-Host "==> Looking up existing app registration '$DisplayName'..."
$app = az ad app list --display-name $DisplayName --query "[0]" -o json | ConvertFrom-Json

if (-not $app) {
    Write-Host "==> Creating app registration..."
    $app = az ad app create `
        --display-name $DisplayName `
        --sign-in-audience AzureADMyOrg `
        --web-redirect-uris $signInUrl $signOutUrl `
        --enable-id-token-issuance true `
        --app-roles "@$rolesPath" `
        -o json | ConvertFrom-Json
} else {
    Write-Host "==> Updating reply URLs and app roles on existing app $($app.appId)..."
    az ad app update --id $app.appId `
        --web-redirect-uris $signInUrl $signOutUrl `
        --enable-id-token-issuance true `
        --app-roles "@$rolesPath" | Out-Null
}

$appId = $app.appId
Write-Host "==> appId: $appId"

# Ensure service principal exists (required for role assignments).
$sp = az ad sp list --filter "appId eq '$appId'" --query "[0]" -o json | ConvertFrom-Json
if (-not $sp) {
    Write-Host "==> Creating service principal..."
    $sp = az ad sp create --id $appId -o json | ConvertFrom-Json
}

if ($RequireAssignment) {
    Write-Host "==> Setting appRoleAssignmentRequired = true on the SP..."
    az ad sp update --id $sp.id --set appRoleAssignmentRequired=true | Out-Null
}

$clientSecret = $null
if ($CreateClientSecret) {
    Write-Host "==> Creating a 1-year client secret..."
    $secret = az ad app credential reset `
        --id $appId `
        --display-name "portal-deploy-$(Get-Date -Format yyyyMMdd)" `
        --years 1 -o json | ConvertFrom-Json
    $clientSecret = $secret.password
}

Write-Host ""
Write-Host "Tenant ID : $(az account show --query tenantId -o tsv)"
Write-Host "Client ID : $appId"
Write-Host "Object ID : $($sp.id)"
if ($clientSecret) {
    Write-Host "ClientSecret (store NOW — won't be shown again):"
    Write-Host "  $clientSecret"
}
Write-Host ""
Write-Host "Set these as App Service application settings (or appsettings.json):"
Write-Host "  AzureAd__TenantId     = <tenant id>"
Write-Host "  AzureAd__ClientId     = $appId"
Write-Host "  AzureAd__Domain       = <tenant>.onmicrosoft.com"
Write-Host "  AzureAd__ClientSecret = <client secret>   (preferably from Key Vault reference)"

if ($AssignUserUpn) {
    Write-Host ""
    Write-Host "==> Assigning $AssignUserUpn -> $AssignRole"
    $user = az ad user show --id $AssignUserUpn -o json | ConvertFrom-Json
    $roles = Get-Content $rolesPath | ConvertFrom-Json
    $role = $roles | Where-Object value -eq $AssignRole
    if (-not $role) { throw "Role $AssignRole not found in app-roles.json" }

    # Idempotent: skip if assignment already exists.
    $existing = az rest --method GET `
        --uri "https://graph.microsoft.com/v1.0/users/$($user.id)/appRoleAssignments" `
        -o json | ConvertFrom-Json

    $already = $existing.value | Where-Object {
        $_.resourceId -eq $sp.id -and $_.appRoleId -eq $role.id
    }

    if ($already) {
        Write-Host "    Assignment already exists; nothing to do."
    } else {
        $body = @{
            principalId = $user.id
            resourceId  = $sp.id
            appRoleId   = $role.id
        } | ConvertTo-Json -Compress

        az rest --method POST `
            --uri "https://graph.microsoft.com/v1.0/users/$($user.id)/appRoleAssignments" `
            --headers "Content-Type=application/json" `
            --body $body | Out-Null

        Write-Host "    Assignment created."
    }
}

Write-Host ""
Write-Host "Done."
