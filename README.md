# Intune Device Actions Portal

Blazor Server (.NET 10) portale di **observability** sulle azioni emesse dalla
[`intune-device-actions`](https://github.com/robgrame/intune-device-actions) API
(wipe, autopilot-register, bitlocker-rotate). Legge gli eventi strutturati
(`AppEvents`) dal workspace Log Analytics che alimenta Application Insights
e li espone tramite dashboard KQL multi-capability.

## Capability supportate

Il portale interroga sia il taxonomy **capability-agnostic** (`action.*` —
request received/accepted/denied, dispatch, polling, ledger lifecycle) sia
quello **per-capability**:

| Prefisso evento  | Capability             | Esempi                                          |
| ---------------- | ---------------------- | ----------------------------------------------- |
| `wipe.*`         | Wipe                   | `wipe.graph.issued`, `wipe.action.completed`    |
| `autopilot.*`    | Autopilot register     | `autopilot.graph.import.issued`                 |
| `bitlocker.*`    | BitLocker key rotate   | `bitlocker.graph.rotate.issued`                 |

La dashboard ha un selettore (tabs) **All / Wipe / Autopilot / BitLocker** che
filtra sia i counter `action.*` (via la property `actionType`) sia i counter
Graph per-capability.

## Autenticazione e autorizzazione

L'accesso è protetto da **Microsoft Entra ID** tramite OpenID Connect
(`Microsoft.Identity.Web`). L'utente, dopo il sign-in, deve avere almeno uno
dei seguenti **ruoli applicativi** definiti sulla App Registration:

| Ruolo               | Descrizione                                                |
| ------------------- | ---------------------------------------------------------- |
| `Actions.Observer`  | Lettura dashboard e trail eventi                           |
| `Actions.Auditor`   | Lettura + future capacità di export / audit-trail estesa   |
| `Actions.Operator`  | Lettura + scrittura sulla pagina **/schedule** (creazione, modifica, cancellazione wave e membership). Senza questo ruolo la voce di menu "Schedule" non compare e l'accesso diretto all'URL viene negato. |

Gli utenti autenticati ma privi di ruolo vedono una pagina **Accesso negato**.

> **Migrazione dai ruoli legacy `Wipe.Observer` / `Wipe.Auditor`**: i GUID
> degli app role nel file `infra/entra/app-roles.json` sono invariati, quindi
> rieseguire `create-app-registration.ps1` aggiorna il displayName e il claim
> value emesso nei token **senza perdere le assegnazioni esistenti**. La
> finestra di refresh dei token successiva alla rinomina è ormai conclusa: il
> portale accetta **solo** i nomi `Actions.*` e i vecchi nomi `Wipe.*` non sono
> più riconosciuti.

### Setup app registration

Esegui (una sola volta per tenant) lo script:

```powershell
./infra/entra/create-app-registration.ps1 `
    -WebAppHostname <your-web-app>.azurewebsites.net `
    -AssignUserUpn alice@contoso.com `
    -AssignRole Actions.Observer `
    -CreateClientSecret `
    -RequireAssignment
```

Lo script:
1. Crea (o aggiorna) la app registration `Intune Device Actions Portal`
   (se trova una legacy `Intune Wipe Portal` la rinomina in place).
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

### Wipe schedule (waves) — `/schedule`

Il portale espone una pagina **Schedule** (riservata al ruolo
`Actions.Operator`) che permette agli operatori di:

- creare / modificare / cancellare *wave* di wipe (nome, descrizione,
  data/ora UTC, stato `draft|scheduled|executing|completed|canceled`);
- aggiungere / rimuovere device da una wave (per `EntraDeviceId`).

Le wave sono persistite in due tabelle Azure (`wipeschedulewaves` e
`wipeschedulemembers`) sullo **storage account del role Web** della API
`intune-device-actions` — il portale ci scrive direttamente con
`TableServiceClient` + `DefaultAzureCredential`, evitando un round-trip
HTTP via Function. Stessa strategia già usata per Log Analytics.

Il client Win32 scarica la propria wave imminente via il nuovo endpoint
mTLS `GET /api/schedule/me` esposto dal Web role della API; il
`WipeActionRunner` applica anche un gate server-side (defense-in-depth)
consultando direttamente le stesse tabelle.

#### Config richiesta

`appsettings.json` (o variabile d'ambiente):

```json
"WipeSchedule": {
  "StorageAccountName": "<idactionsstw...>",
  "WavesTableName":   "wipeschedulewaves",
  "MembersTableName": "wipeschedulemembers"
}
```

`StorageAccountName` è il nome dello storage del **Web role** della API,
NON di un account dedicato al portale.

#### Role assignment necessario

La UAMI del portale deve avere il ruolo **Storage Table Data Contributor**
sullo storage account del Web role. Questo role assignment è ora incluso nel
modulo Bicep (`infra/main.bicep`) e viene creato automaticamente al deploy
quando il parametro `wipeScheduleStorageAccount` è valorizzato.

Se per qualche motivo lo storage non fosse gestito dallo stesso deploy, lo si
può assegnare manualmente:

```pwsh
$webStorage = az storage account show -g rg-idactions-dev `
  -n <idactionsstw...> --query id -o tsv
$portalUami = az identity show -g rg-idactions-portal-dev `
  -n <portal-uami-name> --query principalId -o tsv
az role assignment create --assignee $portalUami `
  --role 'Storage Table Data Contributor' --scope $webStorage
```

Senza questo ruolo la pagina `/schedule` mostra
"Permission denied" ma non rompe il resto del portale.

#### Schema contract con la API repo

I nomi tabella + colonna sono il **contratto** con la wipe capability
nell'altro repo (`src/Capabilities.Wipe/Schedule/`). Qualsiasi rename
deve essere fatto in lockstep nei due repo, o il flusso si rompe in
silenzio (portale scrive, runner non gating).

## Sviluppo locale

```powershell
dotnet restore
dotnet run
```

L'app si autentica con `DefaultAzureCredential` → Azure CLI (`az login`)
per le query KQL. Per il sign-in OIDC serve aver creato la app registration
e popolato `appsettings.json` o `appsettings.Development.json`.

### Test

I test unitari vivono in `tests/IntuneWipePortal.Tests` (xUnit) e coprono la
logica pura senza dipendenze Azure (parsing bulk import, serializzazione
dell'export audit). Eseguili con:

```powershell
dotnet test tests/IntuneWipePortal.Tests
```

## Stack

- Blazor Server interactive · .NET 10
- `Microsoft.Identity.Web` 4.x — OIDC + app role enforcement
- `Azure.Monitor.Query` 1.7 — KQL via `LogsQueryClient`
- `Azure.Identity` — `DefaultAzureCredential` (UAMI in Azure / CLI in dev)
- Bootstrap 5 (incluso nel template)
