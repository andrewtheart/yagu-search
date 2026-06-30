using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Yagu telemetry/bug-report proxy. Keeps the Application Insights connection string and the storage
// account server-side so the desktop client never embeds them. The client only knows this Function's
// base URL plus a rotatable shared app token.
//
// Security choices (per Azure best practices):
//   * Managed Identity for Blob storage (DefaultAzureCredential) — NO account keys.
//   * Application Insights connection string read from app settings (server-side only).
//   * A shared app token (YAGU_APP_TOKEN) is validated with a fixed-time comparison; it is rotatable
//     without shipping a new client build because the client treats it as opaque configuration.
//   * Request size caps and anonymous-but-token-gated endpoints.
var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights for the isolated worker. The connection string comes from
// APPLICATIONINSIGHTS_CONNECTION_STRING in app settings (never from the client).
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// BlobServiceClient via Managed Identity. STORAGE_ACCOUNT_BLOB_URI is the account blob endpoint, e.g.
// https://<account>.blob.core.windows.net. The Function's identity needs "Storage Blob Data
// Contributor" on the account. Falls back gracefully: if unset, bug-report blob upload is skipped.
builder.Services.AddSingleton(_ =>
{
    string? blobUri = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_BLOB_URI");
    if (string.IsNullOrWhiteSpace(blobUri))
        return null!; // Functions resolve this lazily and handle the null (storage optional).
    return new BlobServiceClient(new Uri(blobUri), new DefaultAzureCredential());
});

builder.Build().Run();
