- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.

## Build & Launch Rules

- **Never use `dotnet publish`**. Only use `dotnet build`.
- Always build **both** Debug and Release: `dotnet build Yagu/Yagu.csproj -c Debug` and `dotnet build Yagu/Yagu.csproj -c Release`.
- When launching the app, always launch the **Debug** build: `Yagu\bin\Debug\net10.0-windows10.0.19041.0\Yagu.exe`.
