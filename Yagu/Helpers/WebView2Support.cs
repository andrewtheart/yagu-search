namespace Yagu.Helpers;

using System;
using System.IO;

/// <summary>
/// Shared configuration for the app's WebView2 instances (the embedded terminal and the help viewer).
/// </summary>
public static class WebView2Support
{
    private const string UserDataFolderEnvVar = "WEBVIEW2_USER_DATA_FOLDER";

    /// <summary>
    /// Ensures WebView2 stores its user data in a per-user, writable folder by creating that folder and
    /// pointing WebView2 at it via the <c>WEBVIEW2_USER_DATA_FOLDER</c> environment variable (honored by
    /// <c>CoreWebView2Environment.CreateAsync()</c> when no explicit folder is given).
    ///
    /// WebView2's default user-data folder is a directory next to the executable
    /// (e.g. <c>C:\Program Files (x86)\Yagu\Yagu.exe.WebView2</c>). When Yagu is installed for all users
    /// and run without elevation, that location is read-only, so initialization fails and the
    /// terminal/help WebView never starts. Anchoring the folder under
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> (cache-like, machine-local,
    /// non-roaming) keeps it writable regardless of how Yagu is launched. Idempotent.
    /// </summary>
    public static void ConfigureUserDataFolder()
    {
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(localAppData, "Yagu", "WebView2");
            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable(UserDataFolderEnvVar, folder);
        }
        catch
        {
            // Best effort: if the writable folder can't be prepared, fall back to WebView2's default.
        }
    }
}
