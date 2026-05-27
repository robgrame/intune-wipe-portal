# Intune Wipe Portal

Blazor Server (.NET 10) portale di **observability** sulle richieste di wipe
emesse dalla [`intune-wipe-api`](https://github.com/robgrame/intune-wipe-api).
Legge gli eventi strutturati (`customEvents`) dal workspace Log Analytics che
alimenta Application Insights e li espone tramite dashboard KQL.

## Autenticazione e autorizzazione

L'accesso è protetto da **Microsoft Entra ID** tramite OpenID Connect
(`Microsoft.Identity.Web`). L'utente, dopo il sign-in, deve avere almeno uno
dei seguenti **ruoli applicativi** definiti sulla App Registration:

| Ruolo          | Descrizione                                                    |
| -------------- | -------------------------------------------------------------- |
| `Wipe.Observer`| Lettura dashboard e trail eventi                               |
| `Wipe.Auditor` | Lettura + future capacità di export / audit-trail estesa       |

Gli utenti autenticati ma privi di ruolo vedono una pagina **Accesso negato**.

### Setup app registration

Esegui (una sola volta per tenant) lo script:

```powershell
./infra/entra/create-app-registration.ps1 `
    -WebAppHostname <your-web-app>.azurewebsites.net `
    -AssignUserUpn alice@contoso.com `
    -AssignRole Wipe.Observer `
    -CreateClientSecret `
    -RequireAssignment
```

Lo script:
1. Crea (o aggiorna) la app registration `Intune Wipe Portal`.
2. Registra entrambi i reply URL: `/signin-oidc` e `/signout-callback-oidc`.
3. Carica i due app role da `infra/entra/app-roles.json`.
4. Crea il service principal; con `-RequireAssignment` blocca a livello tenant gli utenti non assegnati.
5. Con `-CreateClientSecret` genera un secret 1 anno (stampato una sola volta — mettilo in Key Vault).
6. Opzionalmente assegna un utente a un ruolo via Microsoft Graph (idempotente).

Annota i valori `TenantId` / `ClientId` e configurali in
`appsettings.json` (locale) oppure come App Service settings
(`AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__Domain`).

## Configurazione

`appsettings.json`:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "Domain":   "contoso.onmicrosoft.com",
    "TenantId": "<tenant-guid>",
    "ClientId": "<app-registration-client-id>",
    "ClientSecret": "<from Key Vault — never commit>",
    "CallbackPath": "/signin-oidc",
    "SignedOutCallbackPath": "/signout-callback-oidc"
  },
  "Monitor": {
    "WorkspaceId": "<log-analytics-workspace-customer-id-guid>"
  }
}
```

> `Microsoft.Identity.Web` usa il flusso OIDC **authorization code** (web confidential client) → il `ClientSecret` è obbligatorio per il code redemption. In produzione configuralo come App Service setting `AzureAd__ClientSecret`, idealmente come **Key Vault reference**.

> `Monitor:WorkspaceId` è il **customerId** del workspace LA (GUID), non il
> resource ID. Si recupera da `az monitor log-analytics workspace show
> --query customerId -o tsv`.

In Azure il portale usa una **User-Assigned Managed Identity** con ruolo
`Log Analytics Reader` sul workspace. Imposta `AZURE_CLIENT_ID` come app
setting per puntare alla UAMI corretta.

## Sviluppo locale

```powershell
dotnet restore
dotnet run
```

L'app si autentica con `DefaultAzureCredential` → Azure CLI (`az login`)
per le query KQL. Per il sign-in OIDC serve aver creato la app registration
e popolato `appsettings.json` o `appsettings.Development.json`.

## Stack

- Blazor Server interactive · .NET 10
- `Microsoft.Identity.Web` 4.x — OIDC + app role enforcement
- `Azure.Monitor.Query` 1.7 — KQL via `LogsQueryClient`
- `Azure.Identity` — `DefaultAzureCredential` (UAMI in Azure / CLI in dev)
- Bootstrap 5 (incluso nel template)
