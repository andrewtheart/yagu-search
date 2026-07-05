namespace Yagu.Services;

/// <summary>
/// Central facts about the voidtools Everything setup the app offers to install, and where the
/// <b>offline</b> installer edition pre-stages that setup so it can be run without a download.
/// <para>
/// The GUI (<c>MainWindow.CheckEverythingAsync</c>) and the CLI
/// (<c>CliRunner.OfferEverythingSetupAsync</c>) both prompt for the user's consent and then either
/// run <see cref="BundledInstallerPath"/> when it is present (the offline edition pre-staged it) or
/// download <see cref="DownloadUrl"/> (the lite editions). Installing Everything <b>always</b>
/// requires explicit consent — bundling only changes where the installer comes from, never whether
/// the consent prompt is shown, nor the Authenticode publisher check that gates running it elevated.
/// </para>
/// </summary>
public static class EverythingAssetPaths
{
    /// <summary>voidtools Everything version the app installs (matches the bundled and downloaded setup).</summary>
    public const string Version = "1.4.1.1032";

    /// <summary>Authenticode publisher the installer must be signed by before it is run elevated.</summary>
    public const string TrustedPublisher = "voidtools";

    /// <summary>
    /// Folder beside the app where the offline installer edition stages the Everything setup
    /// (see <c>build-installer.ps1 -IncludeOcr</c> and <c>scripts/everything-prereq.ps1</c>).
    /// </summary>
    public static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "everything-setup");

    /// <summary>File name of the voidtools Everything setup for the given process bitness.</summary>
    public static string SetupFileName(bool is64Bit)
        => is64Bit
            ? $"Everything-{Version}.x64-Setup.exe"
            : $"Everything-{Version}.x86-Setup.exe";

    /// <summary>Download URL for the voidtools Everything setup for the given process bitness.</summary>
    public static string DownloadUrl(bool is64Bit)
        => $"https://www.voidtools.com/{SetupFileName(is64Bit)}";

    /// <summary>
    /// Full path to the bundled voidtools Everything setup for the given bitness when the offline
    /// edition pre-staged it beside the app; <c>null</c> otherwise (lite editions download instead).
    /// </summary>
    public static string? BundledInstallerPath(bool is64Bit)
    {
        string path = Path.Combine(BundledRoot, SetupFileName(is64Bit));
        return File.Exists(path) ? path : null;
    }
}
