using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace Yagu.Services.Ai;

/// <summary>
/// Detects whether the machine has a hardware compute accelerator (a real GPU or NPU) capable of
/// running a local Foundry model with acceleration. Used to decide whether Semantic search should
/// be the default query mode: machines with an accelerator default to Semantic, machines without
/// one (or with only Windows' software/basic display fallback) default to Traditional.
/// </summary>
public interface ISemanticCapabilityDetector
{
    /// <summary>True when a non-software GPU or an NPU is present on a physical bus.</summary>
    bool HasAcceleratedHardware();
}

/// <summary>
/// Registry-only capability detector. It inspects the installed display-adapter and
/// compute-accelerator device classes without any native interop, network access, or model
/// downloads, so it is fast and safe to run synchronously at startup. Detection failures are
/// treated conservatively as "not accelerated".
/// </summary>
public sealed class GpuNpuCapabilityDetector : ISemanticCapabilityDetector
{
    // Display adapters class (GPUs — discrete and integrated).
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // Compute Accelerators / "Neural processors" class (NPUs). Best-effort: absent on older
    // Windows builds, in which case GPU detection above still covers most accelerated machines.
    private const string ComputeAcceleratorClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";

    // Windows software/basic/remote fallback adapters that are NOT real accelerators even though
    // they appear in the display class.
    private static readonly string[] SoftwareAdapterMarkers =
    [
        "Microsoft Basic Render Driver",
        "Microsoft Basic Display Adapter",
        "Microsoft Remote Display Adapter",
        "Microsoft Hyper-V Video",
        "Remote Desktop",
    ];

    /// <summary>A single device-class instance's identifying values, as read from the registry.</summary>
    public readonly record struct DeviceClassEntry(string DriverDesc, string MatchingDeviceId);

    private readonly Func<string, IEnumerable<DeviceClassEntry>> _readDeviceClass;

    /// <summary>Production constructor: enumerates real device-class instances from the registry.</summary>
    public GpuNpuCapabilityDetector() : this(ReadDeviceClassFromRegistry) { }

    /// <summary>Test seam: supply a fake device-class reader to drive the classification logic
    /// deterministically without touching the live registry.</summary>
    internal GpuNpuCapabilityDetector(Func<string, IEnumerable<DeviceClassEntry>> readDeviceClass)
    {
        _readDeviceClass = readDeviceClass;
    }

    public bool HasAcceleratedHardware()
    {
        try
        {
            return HasHardwareDeviceInClass(DisplayClassKey)
                || HasHardwareDeviceInClass(ComputeAcceleratorClassKey);
        }
        catch
        {
            // Be conservative: an unreadable registry / unexpected layout means we cannot confirm
            // an accelerator, so default to Traditional rather than risk a slow CPU-only model.
            return false;
        }
    }

    private bool HasHardwareDeviceInClass(string classKeyPath)
    {
        foreach (DeviceClassEntry entry in _readDeviceClass(classKeyPath))
            if (IsHardwareAccelerator(entry.DriverDesc, entry.MatchingDeviceId))
                return true;

        return false;
    }

    /// <summary>Thin registry adapter: walks a device class's 4-digit instance subkeys and yields
    /// each instance's <c>DriverDesc</c>/<c>MatchingDeviceId</c>. Excluded from coverage because it
    /// only exercises against the live OS registry, which cannot be driven deterministically.</summary>
    [ExcludeFromCodeCoverage]
    private static IEnumerable<DeviceClassEntry> ReadDeviceClassFromRegistry(string classKeyPath)
    {
        var entries = new List<DeviceClassEntry>();
        using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(classKeyPath);
        if (classKey is null) return entries;

        foreach (string subName in classKey.GetSubKeyNames())
        {
            // Device instances are 4-digit indices ("0000", "0001", …). Skip "Properties" etc.
            if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;

            using RegistryKey? instance = classKey.OpenSubKey(subName);
            if (instance is null) continue;

            string driverDesc = instance.GetValue("DriverDesc") as string ?? string.Empty;
            string matchingDeviceId = instance.GetValue("MatchingDeviceId") as string ?? string.Empty;

            entries.Add(new DeviceClassEntry(driverDesc, matchingDeviceId));
        }

        return entries;
    }

    /// <summary>
    /// Pure classification used by the detector (and unit tests): a real hardware accelerator sits
    /// on a physical bus (PCI/ACPI) and is not one of Windows' software/basic/remote fallback
    /// adapters. Virtual/software adapters enumerate on ROOT\ or SWD\ buses and are rejected.
    /// </summary>
    public static bool IsHardwareAccelerator(string driverDesc, string matchingDeviceId)
    {
        if (string.IsNullOrWhiteSpace(matchingDeviceId)) return false;

        string id = matchingDeviceId.Trim();
        bool onPhysicalBus =
            id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase);
        if (!onPhysicalBus) return false;

        foreach (string marker in SoftwareAdapterMarkers)
            if (driverDesc.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}
