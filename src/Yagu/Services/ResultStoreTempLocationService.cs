namespace Yagu.Services;

public enum ResultStoreDriveTier
{
    Nvme,
    SolidState,
    Sata,
    HardDisk,
    Unknown,
}

public sealed record ResultStoreTempDriveOption(
    string DriveRoot,
    string TempDirectory,
    string DisplayName,
    long AvailableFreeBytes,
    bool IsLaunchDrive,
    ResultStoreDriveTier DriveTier = ResultStoreDriveTier.Unknown,
    uint? AdvertisedSpeedRpm = null);

public sealed record ResultStoreTempLocationProbe(
    bool CurrentDirectoryIsUsable,
    string? LaunchDriveRoot,
    IReadOnlyList<ResultStoreTempDriveOption> DriveOptions);

public static class ResultStoreTempLocationService
{
    public const long MinimumFreeBytes = 50L * 1024 * 1024 * 1024;

    public static string? GetLaunchDriveRoot()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = AppContext.BaseDirectory;

        return NormalizeDriveRoot(Path.GetPathRoot(processPath));
    }

    public static string BuildTempDirectory(string driveRoot) =>
        Path.Combine(NormalizeDriveRoot(driveRoot) ?? driveRoot, "Temp", "Yagu");

    public static IReadOnlyList<ResultStoreTempDriveOption> GetWritableDriveOptions(string? launchDriveRoot = null)
    {
        string? normalizedLaunchRoot = NormalizeDriveRoot(launchDriveRoot);
        var options = new List<ResultStoreTempDriveOption>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            ResultStoreTempDriveOption? option = TryCreateOption(drive, normalizedLaunchRoot);
            if (option is not null)
                options.Add(option);
        }

        SortDriveOptions(options);

        return options;
    }

    internal static void SortDriveOptions(List<ResultStoreTempDriveOption> options)
    {
        options.Sort(static (left, right) =>
        {
            int tierCompare = left.DriveTier.CompareTo(right.DriveTier);
            if (tierCompare != 0) return tierCompare;

            int speedAvailabilityCompare = right.AdvertisedSpeedRpm.HasValue.CompareTo(left.AdvertisedSpeedRpm.HasValue);
            if (speedAvailabilityCompare != 0) return speedAvailabilityCompare;

            int speedCompare = (right.AdvertisedSpeedRpm ?? 0).CompareTo(left.AdvertisedSpeedRpm ?? 0);
            if (speedCompare != 0) return speedCompare;

            int launchCompare = left.IsLaunchDrive.CompareTo(right.IsLaunchDrive);
            if (launchCompare != 0) return launchCompare;

            int freeCompare = right.AvailableFreeBytes.CompareTo(left.AvailableFreeBytes);
            if (freeCompare != 0) return freeCompare;

            return string.Compare(left.DriveRoot, right.DriveRoot, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static Task<IReadOnlyList<ResultStoreTempDriveOption>> GetWritableDriveOptionsAsync(
        string? launchDriveRoot = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => GetWritableDriveOptions(launchDriveRoot), cancellationToken);

    public static Task<ResultStoreTempLocationProbe> ProbeForStartupAsync(
        string? currentTempDirectory,
        bool validateCurrentDirectory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (validateCurrentDirectory &&
                IsUsableTempDirectory(currentTempDirectory, requireMinimumFreeSpace: false))
            {
                return new ResultStoreTempLocationProbe(
                    CurrentDirectoryIsUsable: true,
                    LaunchDriveRoot: null,
                    DriveOptions: Array.Empty<ResultStoreTempDriveOption>());
            }

            string? launchDriveRoot = GetLaunchDriveRoot();
            return new ResultStoreTempLocationProbe(
                CurrentDirectoryIsUsable: false,
                LaunchDriveRoot: launchDriveRoot,
                DriveOptions: GetWritableDriveOptions(launchDriveRoot));
        }, cancellationToken);

    public static ResultStoreTempDriveOption? ChoosePreferredOption(
        IReadOnlyList<ResultStoreTempDriveOption> options,
        string? currentTempDirectory,
        string? launchDriveRoot = null)
    {
        if (options.Count == 0) return null;

        string? currentRoot = NormalizeDriveRoot(Path.GetPathRoot(currentTempDirectory ?? string.Empty));
        if (currentRoot is not null)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].DriveRoot, currentRoot, StringComparison.OrdinalIgnoreCase))
                    return options[i];
            }
        }

        string? normalizedLaunchRoot = NormalizeDriveRoot(launchDriveRoot);
        for (int i = 0; i < options.Count; i++)
        {
            if (!string.Equals(options[i].DriveRoot, normalizedLaunchRoot, StringComparison.OrdinalIgnoreCase))
                return options[i];
        }

        return options[0];
    }

    public static bool IsUsableTempDirectory(string? tempDirectory, bool requireMinimumFreeSpace)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return false;

        try
        {
            string? root = NormalizeDriveRoot(Path.GetPathRoot(tempDirectory));
            if (root is null) return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return false;
            if (requireMinimumFreeSpace && drive.AvailableFreeSpace < MinimumFreeBytes) return false;

            return CanWriteToDirectory(tempDirectory);
        }
        catch
        {
            return false;
        }
    }

    public static string NormalizeTempDirectory(string? tempDirectory)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempDirectory));
        }
        catch
        {
            return tempDirectory.Trim();
        }
    }

    public static string FormatBytesAsGiB(long bytes) => $"{bytes / (1024d * 1024d * 1024d):N1} GB";

    private static ResultStoreTempDriveOption? TryCreateOption(DriveInfo drive, string? launchDriveRoot)
    {
        try
        {
            if (!drive.IsReady) return null;
            if (drive.AvailableFreeSpace < MinimumFreeBytes) return null;

            string? root = NormalizeDriveRoot(drive.Name);
            if (root is null) return null;

            string tempDirectory = BuildTempDirectory(root);
            if (!CanWriteToDirectory(tempDirectory)) return null;

            bool isLaunchDrive = string.Equals(root, launchDriveRoot, StringComparison.OrdinalIgnoreCase);
            ResultStoreDriveHardwareProfile hardware = ResultStoreDriveProfileDetector.Detect(root);
            return new ResultStoreTempDriveOption(
                root,
                tempDirectory,
                BuildDisplayName(drive, root, isLaunchDrive, hardware),
                drive.AvailableFreeSpace,
                isLaunchDrive,
                hardware.Tier,
                hardware.AdvertisedSpeedRpm);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildDisplayName(
        DriveInfo drive,
        string root,
        bool isLaunchDrive,
        ResultStoreDriveHardwareProfile hardware)
    {
        string label = string.Empty;
        try { label = drive.VolumeLabel; }
        catch { }

        string name = string.IsNullOrWhiteSpace(label) ? root : $"{root} ({label})";
        string hardwareName = hardware.Tier switch
        {
            ResultStoreDriveTier.Nvme => "NVMe",
            ResultStoreDriveTier.SolidState => "SSD",
            ResultStoreDriveTier.Sata => "SATA",
            ResultStoreDriveTier.HardDisk => "HDD",
            _ => string.Empty,
        };
        string speed = hardware.AdvertisedSpeedRpm.HasValue
            ? $" ({hardware.AdvertisedSpeedRpm.Value:N0} RPM)"
            : string.Empty;
        string hardwareSuffix = hardwareName.Length > 0 ? $" - {hardwareName}{speed}" : string.Empty;
        string launchSuffix = isLaunchDrive ? " - launch drive" : string.Empty;
        return $"{name}{hardwareSuffix} - {FormatBytesAsGiB(drive.AvailableFreeSpace)} free{launchSuffix}";
    }

    private static bool CanWriteToDirectory(string directory)
    {
        string? probePath = null;
        try
        {
            Directory.CreateDirectory(directory);
            probePath = Path.Combine(directory, $".yagu-write-test-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probePath is not null)
            {
                try { File.Delete(probePath); }
                catch { }
            }
        }
    }

    private static string? NormalizeDriveRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;

        try
        {
            string fullRoot = Path.GetPathRoot(Path.GetFullPath(root)) ?? root;
            return fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
        }
        catch
        {
            return root.Trim();
        }
    }
}