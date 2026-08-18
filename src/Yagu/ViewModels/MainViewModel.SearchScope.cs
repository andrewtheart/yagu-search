using CommunityToolkit.Mvvm.ComponentModel;
using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.ViewModels;

/// <summary>
/// What gets searched: binary/hidden/online-only files, image (OCR) and PDF text, archive
/// contents, the content index toggle, OCR engine settings, startup-directory pinning, the
/// skip/binary/archive extension lists, and the MB-to-bytes size conversions.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] public partial bool SkipBinary { get; set; } = true;

    /// <summary>When true, search cloud-only (online-only) placeholder files by hydrating
    /// them on demand when a live provider is present; when false (default) they are
    /// skipped so the scan never blocks on hydration.</summary>
    [ObservableProperty] public partial bool SearchOnlineOnlyFiles { get; set; }

    /// <summary>When true (default), files and folders carrying the Windows Hidden
    /// attribute are included in the search; when false they are excluded. Seeded from
    /// the persisted <c>SearchHiddenFiles</c> setting and surfaced as the Advanced
    /// Options ▸ Content options toggle.</summary>
    [ObservableProperty] public partial bool SearchHiddenFiles { get; set; } = true;

    /// <summary>When true, raster image files (PNG/JPG/etc.) are OCR'd on a background queue and
    /// their recognized text is searched. Default false. Seeded from the persisted
    /// <c>SearchImageText</c> setting and surfaced as the Advanced Options ▸ Filters toggle.</summary>
    [ObservableProperty] public partial bool SearchImageText { get; set; }

    /// <summary>When true, PDF files are converted to text (via the bundled Xpdf <c>pdftotext</c>) on a
    /// background queue and their extracted text is searched. Default false. Seeded from the persisted
    /// <c>SearchPdfText</c> setting and surfaced as the Advanced Options ▸ Filters toggle.</summary>
    [ObservableProperty] public partial bool SearchPdfText { get; set; }

    /// <summary>Session-only per-search opt-in to the persistent content index (plan §5/§6.1). Seeded
    /// from the effective default (<see cref="AppSettings.ContentIndexActiveByDefault"/>: master feature
    /// on AND used-by-default on) and surfaced as the Advanced Options ▸ Filters toggle. Never persisted;
    /// it only changes whether the ordinary-text candidate set is pruned and is orthogonal to the image/
    /// PDF/archive content-source toggles. When the master feature is off it has no effect.</summary>
    [ObservableProperty] public partial bool UseContentIndex { get; set; }

    /// <summary>OCR engine used when <see cref="SearchImageText"/> is on: "paddle" (PaddleSharp) or
    /// "tesseract". Defaults to <see cref="AppSettings.EffectiveDefaultImageOcrEngine"/> (PaddleSharp on
    /// x64/Arm64; Tesseract on x86, where PaddleOCR's x64-only runtime cannot load). Settings-only.</summary>
    [ObservableProperty] public partial string ImageOcrEngine { get; set; } = AppSettings.EffectiveDefaultImageOcrEngine;

    /// <summary>PaddleSharp recognition model used for image OCR (e.g. "EnglishV4", "ChineseV5").
    /// Higher quality models trade speed for accuracy. Ignored by the Tesseract engine, which uses a
    /// fixed pipeline. Settings-only; configured on the OCR settings tab.</summary>
    [ObservableProperty] public partial string ImageOcrModel { get; set; } = AppSettings.DefaultImageOcrModel;

    /// <summary>Maximum detection resolution (longest image side, in pixels) for PaddleSharp OCR.
    /// Larger values find smaller text at the cost of speed; 0 means unlimited (use the image's
    /// native resolution). Settings-only; configured on the OCR settings tab.</summary>
    [ObservableProperty] public partial int ImageOcrMaxSide { get; set; } = AppSettings.DefaultImageOcrMaxSide;

    /// <summary>Independent OCR worker processes used by image-text search. 0 = conservative automatic;
    /// explicit range 1–4. The effective count is resolved separately for each root so the existing
    /// HDD parallelism safeguard can force one process on rotational media.</summary>
    [ObservableProperty] public partial int ImageOcrWorkerParallelism { get; set; } = AppSettings.DefaultImageOcrWorkerParallelism;

    /// <summary>When true, the directory box is restored to <see cref="AppSettings.PinnedStartupDirectory"/>
    /// at launch; when false, the box starts empty (search all drives). Bound to the star toggle next to
    /// the Browse button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentDirectoryPinned))]
    public partial bool PinStartupDirectory { get; set; }

    /// <summary>True only when the directory currently shown in the box IS the pinned startup directory.
    /// The star toggle's highlighted state binds to this (not to <see cref="PinStartupDirectory"/> alone),
    /// so switching the box to any other folder clears the highlight even though the pin remains saved;
    /// restoring the pinned folder lights it back up. Comparison is case-insensitive and ignores trailing
    /// path separators so <c>C:\foo</c> and <c>C:\foo\</c> are treated as the same folder.</summary>
    public bool IsCurrentDirectoryPinned =>
        PinStartupDirectory
        && !string.IsNullOrWhiteSpace(_settings.PinnedStartupDirectory)
        && string.Equals(
            (Directory ?? string.Empty).Trim().TrimEnd('\\', '/'),
            _settings.PinnedStartupDirectory!.Trim().TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The registered root that covers the directory currently shown in the box, or null.</summary>
    public string? CurrentDirectoryIndexRoot => string.IsNullOrWhiteSpace(Directory)
        ? null
        : IndexedRootsPolicy.FindBestCoveringRoot(_settings.IndexedRoots, Directory!);

    /// <summary>True when the current directory is either an explicit registered index root or lies below
    /// one. The indexing toggle therefore stays selected for <c>C:\src</c> when the single maintained root
    /// is <c>C:\</c>, instead of encouraging a redundant child index.</summary>
    public bool IsCurrentDirectoryIndexed => CurrentDirectoryIndexRoot is not null;


    /// <summary>UI-facing inverse of <see cref="SkipBinary"/> for the "Search binary" toggle.</summary>
    public bool SearchBinary
    {
        get => !SkipBinary;
        set => SkipBinary = !value;
    }

    partial void OnSkipBinaryChanged(bool value)
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SearchBinary)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(BinaryExtensionsVisibility)));

        // Keep the binary-types dropdown consistent with the toggle. "Search binary" ON means "search all
        // binary types by default"; because BinaryExtensions is internally the SKIP list, that maps to an
        // EMPTY skip list (every type selected -> N/N shown). OFF restores the full skip list so content-only
        // mode still early-skips binary types (the dropdown is hidden in that state). Skipped during
        // construction, where the initial extension lists are seeded directly.
        if (!_binaryExtensionsInitialized) return;
        BinaryExtensions = value ? SettingsBinaryExtensions : string.Empty;
        SyncBinaryExtensionItems();
    }

    [ObservableProperty] public partial string SkipExtensions { get; set; } = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] public partial string BinaryExtensions { get; set; } = AppSettings.DefaultBinaryExtensions;
    [ObservableProperty] public partial bool SearchInsideArchives { get; set; }
    [ObservableProperty] public partial string ArchiveExtensions { get; set; } = AppSettings.DefaultArchiveExtensions;
    [ObservableProperty] public partial string SettingsSkipExtensions { get; set; } = AppSettings.DefaultSkipExtensions;
    [ObservableProperty] public partial string SettingsBinaryExtensions { get; set; } = AppSettings.DefaultBinaryExtensions;
    [ObservableProperty] public partial string SettingsArchiveExtensions { get; set; } = AppSettings.DefaultArchiveExtensions;
    [ObservableProperty] public partial int PreviewEditorMaxSizeMB { get; set; } = 32;
    [ObservableProperty] public partial int PreviewEditorMaxTextLength { get; set; } = 20_000_000;
    [ObservableProperty] public partial int PreviewEditorMaxLineLength { get; set; } = 1_000_000;
    [ObservableProperty] public partial int PreviewEditorPopOutMaxSizeMB { get; set; } = 100;
    [ObservableProperty] public partial int PreviewEditorPopOutArrangementIndex { get; set; }
    [ObservableProperty] public partial int ContentSearchFileSizeMB { get; set; } = 100;
    [ObservableProperty] public partial int MaxResultsCeiling { get; set; } = 50_000;
    [ObservableProperty] public partial int MmfConcurrencyLimit { get; set; }
    [ObservableProperty] public partial int NativeConcurrencyLimit { get; set; }
    [ObservableProperty] public partial int MaxMatchesPerSection { get; set; }
    [ObservableProperty] public partial int PreviewSectionPageSize { get; set; }
    // Configurable preview safety caps (0 = use the built-in default; see the Effective* properties in
    // MainWindow.PreviewCommands.cs / MainWindow.PreviewBuilder.cs). They bound how much of a very large
    // checked selection or a huge file the preview surface prepares/renders so the UI stays responsive.
    [ObservableProperty] public partial int MaxSelectedFilesPerPreview { get; set; }
    [ObservableProperty] public partial int MaxSelectedResultsPerPreview { get; set; }
    [ObservableProperty] public partial int MaxRenderedMatchesPerSection { get; set; }
    [ObservableProperty] public partial int FullFilePreviewLimitMB { get; set; }
    [ObservableProperty] public partial int FullFilePreviewMaxRenderLines { get; set; }
    [ObservableProperty] public partial int FullFilePreviewMaxRenderChars { get; set; }
    [ObservableProperty] public partial int ArchiveMaxNestingDepth { get; set; }
    [ObservableProperty] public partial int ArchiveMaxEntryMB { get; set; }

    public double MinFileSizeMB
    {
        get => MinFileSizeBytes == 0 ? double.NaN : MinFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (MinFileSizeBytes != bytes)
                MinFileSizeBytes = bytes;
        }
    }

    public double MaxFileSizeMB
    {
        get => MaxFileSizeBytes == 0 ? double.NaN : MaxFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (MaxFileSizeBytes != bytes)
                MaxFileSizeBytes = bytes;
        }
    }

    public double DefaultMinFileSizeMB
    {
        get => DefaultMinFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (DefaultMinFileSizeBytes != bytes)
                DefaultMinFileSizeBytes = bytes;
        }
    }

    public double DefaultMaxFileSizeMB
    {
        get => DefaultMaxFileSizeBytes / (1024d * 1024d);
        set
        {
            long bytes = MegabytesToBytes(value);
            if (DefaultMaxFileSizeBytes != bytes)
                DefaultMaxFileSizeBytes = bytes;
        }
    }

    private static long MegabytesToBytes(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return 0;

        double bytes = value * 1024d * 1024d;
        if (bytes >= long.MaxValue)
            return long.MaxValue;

        return (long)Math.Round(bytes);
    }

    private static bool IsDateRangeInvalid(DateTimeOffset? after, DateTimeOffset? before)
        => after.HasValue && before.HasValue && after.Value.LocalDateTime.Date > before.Value.LocalDateTime.Date;

    /// <summary>Records that the user approved the one-time OCR component download. Sets the in-process
    /// gate (so concurrent OCR inits proceed) and persists the consent so the warning is shown at most
    /// once across sessions.</summary>
    public async Task MarkOcrDownloadConsentedAsync()
    {
        Yagu.Services.Ocr.OcrDownloadGate.ConsentGranted = true;
        _settings.OcrDownloadConsented = true;
        await PersistSettingsAsync().ConfigureAwait(true);
    }

    /// <summary>Revokes authorization for future OCR component downloads so the consent prompt appears
    /// again when an asset is missing. Already-installed OCR components are not changed.</summary>
    public async Task ResetOcrDownloadConsentAsync()
    {
        if (await PersistPromptResetAsync(settings => settings.OcrDownloadConsented = false)
            .ConfigureAwait(true))
            Yagu.Services.Ocr.OcrDownloadGate.ConsentGranted = false;
    }
}
