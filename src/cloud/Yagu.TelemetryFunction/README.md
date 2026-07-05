# Yagu Telemetry & Bug-Report Proxy (Azure Function)

A small **C# .NET 8 isolated** Azure Function that sits between the Yagu desktop app and your Azure
resources. It exists so the desktop client never embeds your Application Insights connection string or
your storage credentials — the client only knows **this Function's base URL** plus an optional,
rotatable shared token.

```
Yagu (desktop)  ──HTTPS──▶  Function (this)  ──▶  Application Insights   (telemetry + bug-report events)
                                             └──▶  Blob Storage (MI)     (bug-report attachments)
```

## Endpoints

| Route            | Method | Purpose                                                                 |
|------------------|--------|-------------------------------------------------------------------------|
| `/api/telemetry` | POST   | Anonymized perf/error events → Application Insights.                     |
| `/api/bugreport` | POST   | User-reviewed report → AI event + settings/log attachments to blob.     |

Both validate the `X-Yagu-App-Token` header (when a token is configured) and cap request size.

## Security model

- **Managed Identity** for Blob storage (`DefaultAzureCredential`) — no account keys anywhere.
- **Application Insights connection string** lives only in the Function's app settings.
- **`YAGU_APP_TOKEN`** is a rotatable shared secret compared with a fixed-time comparison. Rotate it
  any time by changing the app setting and the client config — no client rebuild required for the URL.
- Anonymous trigger level (the token is the gate); size caps provide defense-in-depth.

## One-time provisioning (you do this)

You need: an Application Insights resource, a **dedicated** storage account, and a Function App.

```powershell
# Variables — adjust names/region.
$rg       = "rg-yourapp-telemetry"
$loc      = "eastus"
$ai       = "appi-yourapp"
$stg      = "yourappbugreports"       # 3-24 lowercase alphanumerics, globally unique
$plan     = "plan-yourapp-telemetry"
$func     = "func-yourapp-telemetry"  # globally unique → https://func-yourapp-telemetry.azurewebsites.net
$container= "bugreports"

az group create -n $rg -l $loc

# Application Insights (workspace-based).
az monitor app-insights component create -g $rg -l $loc --app $ai --kind web
$aiConn = az monitor app-insights component show -g $rg --app $ai --query connectionString -o tsv

# Dedicated storage account for bug-report attachments (and Functions runtime).
az storage account create -g $rg -n $stg -l $loc --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2

# Flex Consumption (or Consumption) Function App, .NET 8 isolated, with a system-assigned identity.
az functionapp create -g $rg -n $func --storage-account $stg `
  --runtime dotnet-isolated --runtime-version 8.0 --functions-version 4 `
  --consumption-plan-location $loc --assign-identity "[system]"

# Grant the Function's identity data access to blobs (no keys).
$principalId = az functionapp identity show -g $rg -n $func --query principalId -o tsv
$stgId       = az storage account show -g $rg -n $stg --query id -o tsv
az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
  --role "Storage Blob Data Contributor" --scope $stgId

# App settings (server-side only).
$blobUri = "https://$stg.blob.core.windows.net"
$token   = [Convert]::ToBase64String([Guid]::NewGuid().ToByteArray())   # rotatable shared token
az functionapp config appsettings set -g $rg -n $func --settings `
  "APPLICATIONINSIGHTS_CONNECTION_STRING=$aiConn" `
  "STORAGE_ACCOUNT_BLOB_URI=$blobUri" `
  "BUGREPORT_CONTAINER=$container" `
  "YAGU_APP_TOKEN=$token"

Write-Host "Function base URL: https://$func.azurewebsites.net"
Write-Host "App token (paste into the client config): $token"
```

## Deploy the code

```powershell
cd cloud/Yagu.TelemetryFunction
func azure functionapp publish func-yourapp-telemetry --dotnet-isolated
```

(Requires the Azure Functions Core Tools and the .NET 8 SDK.)

## Wire the client

Edit `Yagu/Services/Telemetry/TelemetryConfig.cs`:

- Set `ProxyBaseUrl` to your Function base URL (e.g. `https://func-yourapp-telemetry.azurewebsites.net`).
- Set `AppToken` to the `YAGU_APP_TOKEN` value above (or leave empty to run the proxy token-free).

Until `ProxyBaseUrl` is a real `https://` URL, the client stays fully offline (`IsConfigured == false`)
and never sends anything regardless of user consent.

## Local development

```powershell
cd cloud/Yagu.TelemetryFunction
Copy-Item local.settings.json.example local.settings.json   # then fill in values
func start
```

`local.settings.json` is gitignored. For blob uploads locally, sign in with `az login` (so
`DefaultAzureCredential` can authenticate) and set `STORAGE_ACCOUNT_BLOB_URI`, or leave it blank to
skip attachment uploads while testing telemetry.

## What's stored where

- **Application Insights**: `YaguError`, `YaguPerformance`, and `YaguBugReport` custom events +
  metrics. Telemetry events carry only a random install GUID, app/OS version, scrubbed error
  type/message/stack, and timings — no file paths, search text, or machine identifiers.
- **Blob Storage** (`bugreports` container, private): per-report `{{correlationId}}/settings.json`,
  `{{correlationId}}/yagu.log`, and `{{correlationId}}/report.json`. These come only from explicit,
  user-reviewed bug reports. The `correlationId` ties the blobs to the `YaguBugReport` AI event.
