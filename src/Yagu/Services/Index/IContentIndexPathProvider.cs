using Microsoft.Win32;

namespace Yagu.Services.Index;

/// <summary>
/// Resolves the on-disk location of the content index (plan §3.4/§9.2). Production uses
/// <see cref="DefaultContentIndexPathProvider"/>, which honors the configured
/// <c>AppSettings.IndexStorageDirectory</c> or falls back to <c>%LOCALAPPDATA%\Yagu\content-index</c>.
/// Every test must inject its own provider rooted under a unique sandbox so no test ever reads,
/// writes, or mutates a developer's real Yagu index.
/// </summary>
public interface IContentIndexPathProvider
{
    /// <summary>The effective index storage root directory.</summary>
    string IndexRoot { get; }

    /// <summary>The directory holding a single scope's generations, keyed by its stable scope id.</summary>
    string GetScopeDirectory(string scopeId);
}

/// <summary>
/// Default production path provider. The custom <c>IndexStorageDirectory</c> (when set) must already
/// have passed the fixed-local-NTFS / writable validation elsewhere (plan §6.1); this type only
/// resolves and composes paths.
/// </summary>
public sealed class DefaultContentIndexPathProvider : IContentIndexPathProvider
{
    private const string RegistrySubkey = @"Software\Yagu";
    private const string PreservedStorageValueName = "PreservedIndexStorageDirectory";

    /// <summary>Sub-directory name under the storage root: <c>content-index</c>.</summary>
    public const string DefaultSubdirectory = "content-index";

    /// <summary>Vendor sub-directory under LocalAppData: <c>Yagu</c>.</summary>
    public const string VendorDirectory = "Yagu";

    public string IndexRoot { get; }

    /// <summary>
    /// Creates a provider. When <paramref name="configuredDirectory"/> is empty/whitespace, the root
    /// is <c>&lt;localAppDataRoot&gt;\Yagu\content-index</c>. <paramref name="localAppDataRoot"/> is
    /// injected so the resolution is deterministic and testable.
    /// </summary>
    public DefaultContentIndexPathProvider(string? configuredDirectory, string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(localAppDataRoot);
        string configured = string.IsNullOrWhiteSpace(configuredDirectory) ? string.Empty : configuredDirectory.Trim();
        IndexRoot = configured.Length > 0
            ? configured
            : Path.Combine(localAppDataRoot, VendorDirectory, DefaultSubdirectory);
    }

    /// <summary>Creates a provider using the current user's real LocalAppData folder (production).</summary>
    public static DefaultContentIndexPathProvider Create(string? configuredDirectory)
        => new(configuredDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Reads the minimal locator left when uninstall kept a custom index but removed settings.</summary>
    public static bool TryGetPreservedStorageDirectory(out string directory)
    {
        directory = string.Empty;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistrySubkey);
            string? stored = key?.GetValue(PreservedStorageValueName) as string;
            if (string.IsNullOrWhiteSpace(stored) || !Path.IsPathFullyQualified(stored))
                return false;

            directory = stored.Trim();
            return Directory.Exists(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Removes the one-time custom-index locator after it has been saved back to settings.</summary>
    public static void ClearPreservedStorageDirectory()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistrySubkey, writable: true);
            key?.DeleteValue(PreservedStorageValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best effort. A stale locator is harmless once settings carry the explicit location again.
        }
    }

    public string GetScopeDirectory(string scopeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        return Path.Combine(IndexRoot, scopeId);
    }
}
