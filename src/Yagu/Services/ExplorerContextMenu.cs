using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services;

/// <summary>
/// Registers (and detects) the Windows Explorer <b>"Search with Yagu"</b> right-click entry under
/// <c>HKCU\Software\Classes\Directory\shell\Yagu</c> (and the folder-background equivalent). Shared by
/// the GUI first-run prompt and the CLI first-run prompt so both surfaces write the identical registry
/// keys the installer's <c>contextmenu</c> task also writes.
/// </summary>
internal static class ExplorerContextMenu
{
    private const string RegKeyDir = @"Software\Classes\Directory\shell\Yagu";
    private const string RegKeyBackground = @"Software\Classes\Directory\Background\shell\Yagu";
    private const string MenuText = "Search with Yagu";

    /// <summary>True when the "Search with Yagu" Directory entry is already registered for the current user.</summary>
    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegKeyDir);
        bool registered = key is not null;
        YaguLog.For("ContextMenu").LogDebug("IsRegistered={Registered} (HKCU\\{RegKeyDir}).", registered, RegKeyDir);
        return registered;
    }

    /// <summary>
    /// Writes the "Search with Yagu" entry to the folder and folder-background context menus for the
    /// current user, pointing at the running Yagu executable (<c>Yagu.exe --dir "%V"</c>). Throws on a
    /// registry failure so the caller can surface the error.
    /// </summary>
    public static void Register()
    {
        string exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Yagu.exe");
        YaguLog.For("ContextMenu").LogInformation("Registering the \"{MenuText}\" Explorer entry for the current user -> \"{ExePath}\" --dir \"%V\".", MenuText, exePath);

        try
        {
            foreach (var regPath in new[] { RegKeyDir, RegKeyBackground })
            {
                using var shellKey = Registry.CurrentUser.CreateSubKey(regPath);
                shellKey.SetValue(null, MenuText);
                shellKey.SetValue("Icon", exePath);

                using var cmdKey = Registry.CurrentUser.CreateSubKey(regPath + @"\command");
                cmdKey.SetValue(null, $"\"{exePath}\" --dir \"%V\"");
                YaguLog.For("ContextMenu").LogDebug("Wrote HKCU\\{RegPath} (+\\command).", regPath);
            }
            YaguLog.For("ContextMenu").LogInformation("Explorer context-menu entry registered successfully.");
        }
        catch (Exception ex)
        {
            YaguLog.For("ContextMenu").LogWarning(ex, "Failed to write the Explorer context-menu registry keys: {Error}", ex.Message);
            throw;
        }
    }
}
