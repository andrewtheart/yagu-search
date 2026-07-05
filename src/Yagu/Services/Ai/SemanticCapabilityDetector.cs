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

    /// <summary>True when a real (non-software) GPU is present on a physical bus.</summary>
    bool HasGpu();

    /// <summary>True when a compute accelerator / NPU is present on a physical bus.</summary>
    bool HasNpu();

    /// <summary>Largest dedicated video memory (in bytes) among the real (non-software) GPUs present,
    /// or 0 when unknown/none. Used to decide whether the machine can auto-run a larger, more accurate
    /// model (e.g. phi-4 14B) instead of the small default.</summary>
    long GetMaxDedicatedGpuMemoryBytes();
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
    public readonly record struct DeviceClassEntry(string DriverDesc, string MatchingDeviceId, long DedicatedMemoryBytes = 0);

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
            bool accelerated = HasHardwareDeviceInClass(DisplayClassKey)
                || HasHardwareDeviceInClass(ComputeAcceleratorClassKey);
            LogService.Instance.Verbose("Semantic.Capability",
                $"Accelerated hardware detected: {accelerated} (default query mode = {(accelerated ? "Semantic" : "Traditional")}).");
            return accelerated;
        }
        catch (Exception ex)
        {
            // Be conservative: an unreadable registry / unexpected layout means we cannot confirm
            // an accelerator, so default to Traditional rather than risk a slow CPU-only model.
            LogService.Instance.Verbose("Semantic.Capability",
                "Hardware accelerator detection failed; assuming no accelerator (defaulting to Traditional).", ex);
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

    /// <summary>True when a real (non-software) GPU sits in the display-adapter class.</summary>
    public bool HasGpu()
    {
        try { return HasHardwareDeviceInClass(DisplayClassKey); }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Semantic.Capability", "GPU detection failed; assuming none.", ex);
            return false;
        }
    }

    /// <summary>True when an NPU sits in the compute-accelerator class.</summary>
    public bool HasNpu()
    {
        try { return HasHardwareDeviceInClass(ComputeAcceleratorClassKey); }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Semantic.Capability", "NPU detection failed; assuming none.", ex);
            return false;
        }
    }

    /// <summary>Largest dedicated video memory (bytes) among the real (non-software) GPUs, read from
    /// each display adapter's <c>HardwareInformation.qwMemorySize</c> registry value. Returns 0 when no
    /// real GPU is present or the value is unavailable. Software/basic/remote adapters are excluded via
    /// <see cref="IsHardwareAccelerator"/>, so a headless/Sandbox box reports 0.</summary>
    public long GetMaxDedicatedGpuMemoryBytes()
    {
        try
        {
            long max = 0;
            foreach (DeviceClassEntry entry in _readDeviceClass(DisplayClassKey))
            {
                if (!IsHardwareAccelerator(entry.DriverDesc, entry.MatchingDeviceId)) continue;
                if (entry.DedicatedMemoryBytes > max) max = entry.DedicatedMemoryBytes;
            }
            return max;
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Semantic.Capability", "GPU memory detection failed; assuming unknown.", ex);
            return 0;
        }
    }

    /// <summary>Best-effort human-readable descriptions of the real (non-software) GPUs present, e.g.
    /// "NVIDIA GeForce RTX 4070". Empty when none are detected or the registry is unreadable. Used to
    /// enrich user-reviewed bug reports; never sent on the silent telemetry channel.</summary>
    public IReadOnlyList<string> GetGpuDescriptions() => GetHardwareDescriptions(DisplayClassKey);

    /// <summary>Best-effort human-readable descriptions of the NPUs / compute accelerators present,
    /// e.g. "Intel(R) AI Boost". Empty when none are detected.</summary>
    public IReadOnlyList<string> GetNpuDescriptions() => GetHardwareDescriptions(ComputeAcceleratorClassKey);

    private IReadOnlyList<string> GetHardwareDescriptions(string classKeyPath)
    {
        try
        {
            var names = new List<string>();
            foreach (DeviceClassEntry entry in _readDeviceClass(classKeyPath))
            {
                if (string.IsNullOrEmpty(entry.DriverDesc))
                    continue;
                if (!IsHardwareAccelerator(entry.DriverDesc, entry.MatchingDeviceId))
                    continue;
                string desc = entry.DriverDesc.Trim();
                if (desc.Length > 0 && !names.Contains(desc, StringComparer.OrdinalIgnoreCase))
                    names.Add(desc);
            }
            return names;
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("Semantic.Capability", "Hardware description enumeration failed.", ex);
            return Array.Empty<string>();
        }
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
            long dedicatedMemoryBytes = ReadDedicatedMemoryBytes(instance);

            entries.Add(new DeviceClassEntry(driverDesc, matchingDeviceId, dedicatedMemoryBytes));
        }

        return entries;
    }

    /// <summary>Reads a display adapter's dedicated VRAM from <c>HardwareInformation.qwMemorySize</c>,
    /// which the driver stores as a REG_QWORD (long) or, on some drivers, a little-endian REG_BINARY.
    /// Returns 0 when absent/unreadable.</summary>
    [ExcludeFromCodeCoverage]
    private static long ReadDedicatedMemoryBytes(RegistryKey instance)
    {
        object? value = instance.GetValue("HardwareInformation.qwMemorySize");
        return value switch
        {
            long l => l,
            int i => i,
            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
            _ => 0,
        };
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
