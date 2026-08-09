using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Yagu.Models;
using Yagu.Services.Index;
using Yagu.Services.Logging;
using Yagu.Services.Ocr;
using System.Runtime.InteropServices;

namespace Yagu.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, Ai.SemanticModelGenerationOverride>))]
[JsonSerializable(typeof(Ai.SemanticModelGenerationOverride))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext { }

public sealed class AppSettings
{
    public const string LegacyDefaultSkipExtensions = "exe;dll;pdb;obj;lib;so;dylib;zip;gz;tar;7z;rar;bz2;xz;iso;cab;msi;nupkg;whl;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;docx;xls;xlsx;ppt;pptx";
    public const string LegacyExpandedBinaryPrefilterExtensions = "exe;dll;pdb;obj;lib;so;dylib;png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;woff;woff2;ttf;eot;otf;pdf;doc;xls;ppt;com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm;bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;m4a;webm;heic;heif;avif";
    public const string DefaultSkipExtensions = "png;jpg;jpeg;gif;bmp;ico;tif;tiff;webp;svg;mp3;mp4;avi;mov;wmv;flv;mkv;wav;ogg;flac;m4a;webm;woff;woff2;ttf;eot;otf;pdf;doc;xls;ppt;bin;dat;db;db3;sqlite;sqlite3;edb;mdb;accdb;ldb;sdf;cache;tmp;bak;etl;evtx;dmp;mdmp;hdmp;hprof;vhd;vhdx;vmdk;pak;usm;bundle;assets;heic;heif;avif";
    public const string DefaultBinaryExtensions = "exe;dll;pdb;obj;lib;so;dylib;com;scr;sys;drv;ocx;cpl;mui;winmd;pri;cat;res;resources;o;a;lo;la;ilk;iobj;ipdb;exp;pyc;pyo;class;dex;wasm";
    public const string DefaultArchiveExtensions = "zip;jar;war;ear;nupkg;vsix;apk;aab;aar;appx;msix;appxbundle;msixbundle;docx;xlsx;pptx;odt;ods;odp;epub;whl;gz;tar;7z;rar;bz2;xz;iso;cab;msi;tgz;tbz2;txz;zst;zstd;br;lz4;lzma";
    /// <summary>Raster image extensions that are OCR'd when "Search image text" is on. These are
    /// normally in <see cref="DefaultSkipExtensions"/>; image-text mode bypasses the skip list for
    /// them (mirroring how archive search bypasses skip for archive extensions).</summary>
    public const string DefaultImageOcrExtensions = "png;jpg;jpeg;bmp;gif;tif;tiff;webp";
    /// <summary>Document extensions converted to text (via the bundled Xpdf <c>pdftotext</c>) when
    /// "Search PDF text" is on. These are normally in <see cref="DefaultSkipExtensions"/>; PDF-text
    /// mode bypasses the skip list for them (mirroring image-text and archive search).</summary>
    public const string DefaultPdfTextExtensions = "pdf";
    public const string DefaultExcludeGlobs = "node_modules;bin;obj;.git";
    public const string DefaultSelectedPreviewContentBackgroundColor = "#FF000000";
    public const string DefaultUnselectedPreviewContentBackgroundColor = "#FF1E1E1E";

    // Preview editor font colors (ARGB hex strings)
    public const string LegacyDefaultPreviewGutterContextColor = "#FF505050";
    public const string LegacyDefaultPreviewGutterMatchColor = "#FF32CD32";
    public const string DefaultPreviewGutterColor = "#FF9CDCFE";
    public const string DefaultPreviewGutterContextColor = DefaultPreviewGutterColor;
    public const string DefaultPreviewGutterMatchColor = DefaultPreviewGutterColor;
    public const string DefaultPreviewEditorGutterColor = "#FF3A8FD6"; // Darker blue (passes light+dark)
    // Empty string means "follow the app/system theme" (white on dark, near-black on light). A non-empty
    // ARGB hex string is an explicit user override applied to the built-in editor's body text.
    public const string DefaultPreviewEditorTextColor = "";
    public const string DefaultPreviewMatchTextColor = "#FFFFD700"; // Gold
    public const string DefaultPreviewOverlayColor = "#FFFF4500"; // OrangeRed
    public const string DefaultPreviewMatchLineColor = "#FFFFFFFF"; // White
    public const string DefaultPreviewShowMoreEllipsisColor = "#FF1E90FF"; // DodgerBlue
    public const int DefaultPreviewShowMoreEllipsisFontSize = 17;
    public const string DefaultPreviewTextFontFamily = "Consolas";
    public const int DefaultPreviewTextFontSize = 14;
    public const string DefaultPreviewEditorFontFamily = "Consolas, Cascadia Mono, Segoe UI, Segoe UI Symbol, Segoe UI Emoji";
    public const int DefaultPreviewEditorFontSize = 13;
    public const string DefaultResultListMatchTextFontFamily = "Consolas";
    public const int DefaultResultListMatchTextFontSize = 12;
    public const string DefaultResultListMatchHighlightColor = "#FFB8860B"; // DarkGoldenrod (passes light+dark)

    // ── File list overlay (sticky header in results list) ──
    public const int DefaultFileListOverlayHeight = 36;
    public const int DefaultFileListOverlayFontSize = 12;
    public const string DefaultFileListOverlayFontColor = "#FFFFFFFF";
    public const string DefaultFileListOverlayFontFamily = "Segoe UI";

    // ── Preview sticky file header overlay ──
    public const int DefaultPreviewStickyHeaderHeight = 36;
    public const int DefaultPreviewStickyHeaderFileNameFontSize = 14;
    public const string DefaultPreviewStickyHeaderFileNameFontColor = "#FFFFFFFF";
    public const string DefaultPreviewStickyHeaderFileNameFontFamily = "Segoe UI";
    public const int DefaultPreviewStickyHeaderDetailFontSize = 12;
    public const string DefaultPreviewStickyHeaderDetailFontColor = "#B3FFFFFF"; // White @ 70% opacity
    public const string DefaultPreviewStickyHeaderDetailFontFamily = "Segoe UI";

    // ── File list drawer labels ──
    public const int DefaultDrawerFileNameFontSize = 13;
    public const string DefaultDrawerFileNameFontColor = "#FFFFFFFF";
    public const string DefaultDrawerFileNameFontFamily = "Segoe UI";
    public const int DefaultDrawerDirectoryFontSize = 13;
    public const string DefaultDrawerDirectoryFontColor = "#8CFFFFFF"; // White @ 55% opacity
    public const string DefaultDrawerDirectoryFontFamily = "Segoe UI";
    public const int DefaultDrawerMetadataFontSize = 11;
    public const string DefaultDrawerMetadataFontColor = "#73FFFFFF"; // White @ 45% opacity
    public const string DefaultDrawerMetadataFontFamily = "Segoe UI";

    public const int DefaultLowDiskSpaceWarningPercent = 90;
    public const int MinimumLowDiskSpaceWarningPercent = 1;
    public const int MaximumLowDiskSpaceWarningPercent = 99;

    public static int NormalizeLowDiskSpaceWarningPercent(int value) => value <= 0
        ? DefaultLowDiskSpaceWarningPercent
        : Math.Clamp(value, MinimumLowDiskSpaceWarningPercent, MaximumLowDiskSpaceWarningPercent);

    /// <summary>Normalizes the persisted OCR engine id to a known value, defaulting to
    /// <see cref="EffectiveDefaultImageOcrEngine"/>. On x86, "paddle" is coerced to "tesseract"
    /// because PaddleOCR's x64-only native runtime cannot load in a 32-bit process
    /// (see <see cref="PaddleOcrSupported"/>).</summary>
    public static string NormalizeImageOcrEngine(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        var engine = v switch
        {
            "tesseract" => "tesseract",
            "paddle" or "paddleocr" or "paddlesharp" => "paddle",
            _ => EffectiveDefaultImageOcrEngine,
        };
        return CoerceImageOcrEngineForArch(engine, PaddleOcrSupported);
    }

    /// <summary>Normalizes the persisted PaddleOCR model name to a known value (canonical casing),
    /// defaulting to <see cref="DefaultImageOcrModel"/>.</summary>
    public static string NormalizeImageOcrModel(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            "englishv3" or "english_v3" or "en_v3" => "EnglishV3",
            "englishv4" or "english_v4" or "en_v4" => "EnglishV4",
            "chinesev4" or "chinese_v4" or "zh_v4" => "ChineseV4",
            "chinesev5" or "chinese_v5" or "zh_v5" => "ChineseV5",
            _ => DefaultImageOcrModel,
        };
    }

    /// <summary>Normalizes the persisted PaddleOCR detection resolution cap. 0 (or negative) means
    /// "unlimited"; any other value is clamped to [<see cref="MinimumImageOcrMaxSide"/>,
    /// <see cref="MaximumImageOcrMaxSide"/>].</summary>
    public static int NormalizeImageOcrMaxSide(int value)
        => value <= 0 ? 0 : Math.Clamp(value, MinimumImageOcrMaxSide, MaximumImageOcrMaxSide);

    /// <summary>Preserves 0 as automatic and clamps explicit OCR process counts to 1–4.</summary>
    public static int NormalizeImageOcrWorkerParallelism(int value)
        => OcrWorkerParallelism.Normalize(value);

    // ── Content index (plan §6.1 "Indexing" tab) ──
    // Every persisted indexing knob is normalized/validated here so the Settings tab and the CLI
    // --index-config surface share one validator. Correctness never depends on any of these values —
    // an over-budget / out-of-range case simply live-scans (plan §11).

    public const bool DefaultEnableContentIndex = true;
    public const bool DefaultUseContentIndexByDefault = true;

    public const int DefaultIndexQueryStartupBudgetMs = 200;
    public const int MinimumIndexQueryStartupBudgetMs = 25;
    public const int MaximumIndexQueryStartupBudgetMs = 2000;

    public const int DefaultIndexMaxCandidatePercent = 25;
    public const int MinimumIndexMaxCandidatePercent = 1;
    public const int MaximumIndexMaxCandidatePercent = 100;

    public const int DefaultIndexMaxFileSizeMB = 100;
    public const int MinimumIndexMaxFileSizeMB = 1;
    public const int MaximumIndexMaxFileSizeMB = 4096;

    public const int DefaultIndexRetainedGenerationCount = 2;
    public const int MinimumIndexRetainedGenerationCount = 1;
    public const int MaximumIndexRetainedGenerationCount = 16;

    public const int DefaultIndexStaleTemporaryHours = 24;
    public const int MinimumIndexStaleTemporaryHours = 1;
    public const int MaximumIndexStaleTemporaryHours = 720;

    public const int DefaultIndexQuarantineRetentionDays = 7;
    public const int MinimumIndexQuarantineRetentionDays = 1;
    public const int MaximumIndexQuarantineRetentionDays = 90;

    public const int DefaultIndexIdleDelayMinutes = 5;
    public const int MinimumIndexIdleDelayMinutes = 1;
    public const int MaximumIndexIdleDelayMinutes = 120;
    public const int DefaultIndexContinuousIntervalMinutes = 5;
    public const int FirstRunDriveIndexContinuousIntervalMinutes = 1;
    public const int MinimumIndexContinuousIntervalMinutes = 1;
    public const int MaximumIndexContinuousIntervalMinutes = 120;

    // Storage/memory quotas scale with pointer size, giving x86 half the x64 defaults
    // for its constrained address space (plan §6.1/§11).
    // 50 GiB on x64: a whole-drive index (content.bin plus the format-v3 sidecars that roughly double it)
    // lands around 30 GB, so a smaller ceiling halts maintenance on the first pass after a rebuild.
    public static int DefaultIndexMaxDiskSizeMB => 6400 * IntPtr.Size;
    /// <summary>The pre-50 GiB default, kept only so a settings file still carrying it can be migrated once.</summary>
    public static int LegacyDefaultIndexMaxDiskSizeMB => 512 * IntPtr.Size;
    public const int MinimumIndexMaxDiskSizeMB = 256;

    public const int DefaultIndexMinimumFreeSpaceMB = 2048;
    public const int MinimumIndexMinimumFreeSpaceMB = 256;

    /// <summary>Stop an index build when the index drive's used space reaches this percentage (plan §11.2).
    /// Default 90; range 50–99. A build in progress is stopped and the already-written partial index is kept.</summary>
    public const int DefaultIndexMaxDiskUsagePercent = 90;
    public const int MinimumIndexMaxDiskUsagePercent = 50;
    public const int MaximumIndexMaxDiskUsagePercent = 99;

    public static int DefaultIndexQueryMemoryBudgetMB => 8 * IntPtr.Size;
    public const int MinimumIndexQueryMemoryBudgetMB = 16;
    public const int MaximumIndexQueryMemoryBudgetMB = 4096;

    // In-process index size cap (GUI/CLI query path): the largest CURRENT on-disk index (base + active
    // segments) Yagu will deserialize into memory to accelerate a search. A large layered index expands to
    // several GB in the managed heap, which trips the search memory monitor into degraded mode and makes
    // searches SLOWER than a plain live scan — so at/above this size the search live-scans instead. 0 =
    // never load an index in memory (always live-scan); default 2048 MB (2 GB).
    public const int DefaultIndexMaxInProcessSizeMB = 2048;
    public const int MaximumIndexMaxInProcessSizeMB = 1_048_576;

    // Out-of-process (worker) index size cap: the largest CURRENT on-disk index the isolated worker will
    // memory-MAP to serve a query WITHOUT loading it into the host process. Because the worker pages the
    // mapped v3 structures (a bounded resident footprint) rather than deserializing the whole index into the
    // managed heap, this cap can be far larger than the in-process one. At/above this size the search
    // live-scans instead. 0 = never use the worker (always live-scan); default 30720 MB (30 GB).
    public const int DefaultIndexMaxWorkerQuerySizeMB = 30720;
    public const int MaximumIndexMaxWorkerQuerySizeMB = 1_048_576;

    public static int DefaultIndexBuildMemoryBudgetMB => 48 * IntPtr.Size;
    public const int MinimumIndexBuildMemoryBudgetMB = 64;
    public const int MaximumIndexBuildMemoryBudgetMB = 8192;

    public const int DefaultIndexBuildWorkerParallelism = IndexWorkerParallelism.Automatic;
    public const int DefaultIndexQueryWorkerParallelism = IndexWorkerParallelism.Automatic;
    public const int MaximumIndexWorkerParallelism = IndexWorkerParallelism.Maximum;

    public const int DefaultIndexMaxJournalCatchupMB = 64;
    public const int MinimumIndexMaxJournalCatchupMB = 1;
    public const int MaximumIndexMaxJournalCatchupMB = 4096;

    public const int DefaultIndexMaxJournalCatchupRecords = 2_000_000;
    public const int FirstRunDriveIndexMaxJournalCatchupRecords = 8_000_000;
    public const int MinimumIndexMaxJournalCatchupRecords = 1000;
    public const int MaximumIndexMaxJournalCatchupRecords = 100_000_000;

    public const int DefaultIndexPostBuildCatchUpThresholdChanges = 30_000;
    public const int MinimumIndexPostBuildCatchUpThresholdChanges = 0;
    public const int MaximumIndexPostBuildCatchUpThresholdChanges = 100_000_000;
    public const int DefaultFileIoTimeoutSeconds = 30;
    public const int MinimumFileIoTimeoutSeconds = 1;
    public const int MaximumFileIoTimeoutSeconds = 600;

    // Phase 3 incremental maintenance (plan §11.4): append-only delta segments compact into a fresh base
    // once EITHER bound is hit.
    public const int DefaultIndexMaxDeltaSegments = 8;
    public const int MinimumIndexMaxDeltaSegments = 1;
    public const int MaximumIndexMaxDeltaSegments = 64;

    public const int DefaultIndexCompactionThresholdMB = 256;
    public const int MinimumIndexCompactionThresholdMB = 16;
    public const int MaximumIndexCompactionThresholdMB = 8192;

    // Safety cap on the AUTOMATIC over-segmented compaction (plan §11.4): folding a large index into a fresh
    // base re-materializes every layer's documents + a combined posting index + a serialization buffer in
    // memory (a transient multi-GB spike). Above this total on-disk size the auto-compaction is skipped and
    // the index is left segmented (queries still work). 0 = no cap. Manual/explicit compaction ignores it.
    public const int DefaultIndexMaxAutoCompactionSizeMB = 512;
    public const int MaximumIndexMaxAutoCompactionSizeMB = 1_048_576;

    // Segment coalescing bounds (plan §11.4). Coalescing merges a contiguous run of small delta segments
    // into one replacement without ever opening the base, so it is the only reclamation an index above the
    // auto-compaction cap can still perform. These were previously hard-coded at 8 MB/32 MB/8-in-a-row,
    // which no real whole-drive index could satisfy, leaving such indexes with no reclamation at all.
    // Measured on real whole-drive indexes: paged full-build layers and incremental segments both land
    // around 200 MB, so the previous 128 MB cap made 3 of 27 segments eligible and never produced a run.
    // The batch cap must stay >= MinRun * MaxSegment or a minimum-length run can never fit.
    public const int DefaultIndexCoalesceMaxSegmentMB = 256;
    public const int MaximumIndexCoalesceMaxSegmentMB = 8192;
    public const int DefaultIndexCoalesceMaxBatchMB = 1024;
    public const int MaximumIndexCoalesceMaxBatchMB = 32768;
    public const int DefaultIndexCoalesceMinRun = 4;
    public const int MinimumIndexCoalesceMinRun = 2;
    public const int MaximumIndexCoalesceMinRun = 64;
    public const int DefaultIndexCoalesceMaxRunsPerPass = 8;
    public const int MaximumIndexCoalesceMaxRunsPerPass = 64;

    /// <summary>Pre-migration coalescing defaults, kept only so settings still carrying them can be lifted once.</summary>
    public const int LegacyDefaultIndexCoalesceMaxSegmentMB = 128;
    public const int LegacyDefaultIndexCoalesceMaxBatchMB = 512;

    /// <summary>Build trigger: how a build/update is kicked off (plan §6.1).</summary>
    public const string DefaultIndexBuildTrigger = "Manual";
    /// <summary>The <see cref="IndexBuildTrigger"/> value that runs build passes on a user-defined schedule
    /// (an interval, or on chosen days of the week at a set time) while Yagu is running.</summary>
    public const string IndexBuildTriggerOnSchedule = "OnSchedule";
    /// <summary>Runs idle-style maintenance continuously while Yagu is open, without requiring actual
    /// keyboard/mouse idleness. The idle-delay value becomes the minimum interval between passes.</summary>
    public const string IndexBuildTriggerContinuous = "Continuous";

    /// <summary>Schedule mode for the OnSchedule trigger: repeat on a fixed interval. Default.</summary>
    public const string DefaultIndexScheduleMode = "Interval";
    /// <summary>Schedule mode for the OnSchedule trigger: run on chosen weekdays at a set time.</summary>
    public const string IndexScheduleModeWeekly = "Weekly";
    public const int DefaultIndexScheduleIntervalMinutes = 60;
    public const int MinimumIndexScheduleIntervalMinutes = 5;
    public const int MaximumIndexScheduleIntervalMinutes = 10080; // one week
    /// <summary>Days-of-week bitmask default: every day (bit 0 = Sunday … bit 6 = Saturday).</summary>
    public const int DefaultIndexScheduleDaysOfWeekMask = 127;
    /// <summary>Default time of day (HH:mm, 24h) for the weekly schedule.</summary>
    public const string DefaultIndexScheduleTimeOfDay = "03:00";
    /// <summary>Removable-drive policy: never index removable media unless a root is explicitly added.</summary>
    public const string DefaultIndexRemovableDrivePolicy = "Never";
    /// <summary>Update mode: what an automatic pass does (plan §6.1). V1 supports a manual-only build and a
    /// full rebuild when the root is proven dirty; <c>AutomaticIncremental</c> is a Phase 3 addition.</summary>
    public const string DefaultIndexUpdateMode = "ManualFullRebuild";
    /// <summary>The <see cref="IndexUpdateMode"/> value that auto-rebuilds a root's index once the change
    /// journal proves its content changed since the index was built (plan §6.1, V1).</summary>
    public const string IndexUpdateModeAutomaticFullRebuildWhenDirty = "AutomaticFullRebuildWhenDirty";
    /// <summary>The <see cref="IndexUpdateMode"/> value (Phase 3) that applies incremental delta segments
    /// when the change journal proves a root is dirty, compacting into a fresh base once the segment/size
    /// bounds are hit (plan §11.4) — instead of a full rebuild.</summary>
    public const string IndexUpdateModeAutomaticIncremental = "AutomaticIncremental";

    /// <summary>The individually selectable automatic build triggers, in canonical order. "Manual" is the
    /// absence of all of these (an empty selection) and is therefore not part of this list.</summary>
    private static readonly string[] IndexBuildTriggerFlags = { "WhenEnabled", "AtStartup", "WhenIdle", "Continuous", "OnSchedule" };
    private static readonly char[] IndexBuildTriggerSeparators = { ',', ';', '+', ' ', '\t' };
    private static readonly string[] IndexScheduleModes = { DefaultIndexScheduleMode, IndexScheduleModeWeekly };
    private static readonly string[] IndexRemovableDrivePolicies = { "Never", "ExplicitRootsOnly" };
    private static readonly string[] IndexUpdateModes = { DefaultIndexUpdateMode, IndexUpdateModeAutomaticFullRebuildWhenDirty, IndexUpdateModeAutomaticIncremental };

    public static int NormalizeIndexQueryStartupBudgetMs(int value)
        => value <= 0 ? DefaultIndexQueryStartupBudgetMs
            : Math.Clamp(value, MinimumIndexQueryStartupBudgetMs, MaximumIndexQueryStartupBudgetMs);

    // 0 is a MEANINGFUL value here (never load an index in-process → always live-scan), so only a NEGATIVE
    // value falls back to the default; a positive value is clamped to the ceiling.
    public static int NormalizeIndexMaxInProcessSizeMB(int value)
        => value < 0 ? DefaultIndexMaxInProcessSizeMB
            : Math.Min(value, MaximumIndexMaxInProcessSizeMB);

    // 0 is MEANINGFUL here too (never serve a scope via the worker → always live-scan), so only a NEGATIVE
    // value falls back to the default; a positive value is clamped to the ceiling.
    public static int NormalizeIndexMaxWorkerQuerySizeMB(int value)
        => value < 0 ? DefaultIndexMaxWorkerQuerySizeMB
            : Math.Min(value, MaximumIndexMaxWorkerQuerySizeMB);

    public static int NormalizeIndexMaxCandidatePercent(int value)
        => value <= 0 ? DefaultIndexMaxCandidatePercent
            : Math.Clamp(value, MinimumIndexMaxCandidatePercent, MaximumIndexMaxCandidatePercent);

    public static int NormalizeIndexMaxFileSizeMB(int value)
        => value <= 0 ? DefaultIndexMaxFileSizeMB
            : Math.Clamp(value, MinimumIndexMaxFileSizeMB, MaximumIndexMaxFileSizeMB);

    public static int NormalizeIndexRetainedGenerationCount(int value)
        => value <= 0 ? DefaultIndexRetainedGenerationCount
            : Math.Clamp(value, MinimumIndexRetainedGenerationCount, MaximumIndexRetainedGenerationCount);

    public static int NormalizeIndexStaleTemporaryHours(int value)
        => value <= 0 ? DefaultIndexStaleTemporaryHours
            : Math.Clamp(value, MinimumIndexStaleTemporaryHours, MaximumIndexStaleTemporaryHours);

    public static int NormalizeIndexQuarantineRetentionDays(int value)
        => value <= 0 ? DefaultIndexQuarantineRetentionDays
            : Math.Clamp(value, MinimumIndexQuarantineRetentionDays, MaximumIndexQuarantineRetentionDays);

    public static int NormalizeIndexIdleDelayMinutes(int value)
        => value <= 0 ? DefaultIndexIdleDelayMinutes
            : Math.Clamp(value, MinimumIndexIdleDelayMinutes, MaximumIndexIdleDelayMinutes);

    public static int NormalizeIndexContinuousIntervalMinutes(int value)
        => value <= 0 ? DefaultIndexContinuousIntervalMinutes
            : Math.Clamp(value, MinimumIndexContinuousIntervalMinutes, MaximumIndexContinuousIntervalMinutes);

    public static int NormalizeIndexMaxDiskSizeMB(int value)
        => value <= 0 ? DefaultIndexMaxDiskSizeMB
            : Math.Max(MinimumIndexMaxDiskSizeMB, value);

    public static int NormalizeIndexMinimumFreeSpaceMB(int value)
        => value <= 0 ? DefaultIndexMinimumFreeSpaceMB
            : Math.Max(MinimumIndexMinimumFreeSpaceMB, value);

    public static int NormalizeIndexMaxDiskUsagePercent(int value)
        => value <= 0 ? DefaultIndexMaxDiskUsagePercent
            : Math.Clamp(value, MinimumIndexMaxDiskUsagePercent, MaximumIndexMaxDiskUsagePercent);

    public static int NormalizeIndexQueryMemoryBudgetMB(int value)
        => value <= 0 ? DefaultIndexQueryMemoryBudgetMB
            : Math.Clamp(value, MinimumIndexQueryMemoryBudgetMB, MaximumIndexQueryMemoryBudgetMB);

    public static int NormalizeIndexBuildMemoryBudgetMB(int value)
        => value <= 0 ? DefaultIndexBuildMemoryBudgetMB
            : Math.Clamp(value, MinimumIndexBuildMemoryBudgetMB, MaximumIndexBuildMemoryBudgetMB);

    /// <summary>Normalizes worker parallelism while preserving zero as the hardware-based automatic mode.</summary>
    public static int NormalizeIndexBuildWorkerParallelism(int value)
        => IndexWorkerParallelism.NormalizeSetting(value);

    /// <summary>Normalizes worker parallelism while preserving zero as the hardware-based automatic mode.</summary>
    public static int NormalizeIndexQueryWorkerParallelism(int value)
        => IndexWorkerParallelism.NormalizeSetting(value);

    public static int NormalizeIndexMaxJournalCatchupMB(int value)
        => value <= 0 ? DefaultIndexMaxJournalCatchupMB
            : Math.Clamp(value, MinimumIndexMaxJournalCatchupMB, MaximumIndexMaxJournalCatchupMB);

    public static int NormalizeIndexMaxJournalCatchupRecords(int value)
        => value <= 0 ? DefaultIndexMaxJournalCatchupRecords
            : Math.Clamp(value, MinimumIndexMaxJournalCatchupRecords, MaximumIndexMaxJournalCatchupRecords);

    public static int NormalizeIndexPostBuildCatchUpThresholdChanges(int value)
        => value < 0 ? DefaultIndexPostBuildCatchUpThresholdChanges
            : Math.Clamp(
                value,
                MinimumIndexPostBuildCatchUpThresholdChanges,
                MaximumIndexPostBuildCatchUpThresholdChanges);

    public static int NormalizeFileIoTimeoutSeconds(int value)
        => value <= 0 ? DefaultFileIoTimeoutSeconds
            : Math.Clamp(value, MinimumFileIoTimeoutSeconds, MaximumFileIoTimeoutSeconds);

    public static int NormalizeIndexMaxDeltaSegments(int value)
        => value <= 0 ? DefaultIndexMaxDeltaSegments
            : Math.Clamp(value, MinimumIndexMaxDeltaSegments, MaximumIndexMaxDeltaSegments);

    public static int NormalizeIndexCompactionThresholdMB(int value)
        => value <= 0 ? DefaultIndexCompactionThresholdMB
            : Math.Clamp(value, MinimumIndexCompactionThresholdMB, MaximumIndexCompactionThresholdMB);

    /// <summary>Normalizes the automatic-compaction size cap (MB): negative → default; 0 = no cap (kept);
    /// otherwise capped at <see cref="MaximumIndexMaxAutoCompactionSizeMB"/>.</summary>
    public static int NormalizeIndexMaxAutoCompactionSizeMB(int value)
        => value < 0 ? DefaultIndexMaxAutoCompactionSizeMB
            : Math.Min(value, MaximumIndexMaxAutoCompactionSizeMB);

    /// <summary>Normalizes the largest individual segment (MB) eligible to join a coalescing run.</summary>
    public static int NormalizeIndexCoalesceMaxSegmentMB(int value)
        => value <= 0 ? DefaultIndexCoalesceMaxSegmentMB
            : Math.Min(value, MaximumIndexCoalesceMaxSegmentMB);

    /// <summary>Normalizes the largest total size (MB) of one coalescing run.</summary>
    public static int NormalizeIndexCoalesceMaxBatchMB(int value)
        => value <= 0 ? DefaultIndexCoalesceMaxBatchMB
            : Math.Min(value, MaximumIndexCoalesceMaxBatchMB);

    /// <summary>Normalizes the fewest contiguous eligible segments that make a coalescing run worthwhile.</summary>
    public static int NormalizeIndexCoalesceMinRun(int value)
        => value <= 0 ? DefaultIndexCoalesceMinRun
            : Math.Clamp(value, MinimumIndexCoalesceMinRun, MaximumIndexCoalesceMinRun);

    /// <summary>Normalizes the most coalescing runs merged in one maintenance pass.</summary>
    public static int NormalizeIndexCoalesceMaxRunsPerPass(int value)
        => value <= 0 ? DefaultIndexCoalesceMaxRunsPerPass
            : Math.Min(value, MaximumIndexCoalesceMaxRunsPerPass);

    /// <summary>Normalizes the build-trigger selection to a canonical, de-duplicated, ordered combination of
    /// the automatic triggers (e.g. <c>"AtStartup, OnSchedule"</c>). Several triggers can be active at once.
    /// Accepts any separator (comma/space/plus/semicolon) and any casing; unknown tokens and the sentinel
    /// <c>"Manual"</c> are dropped. An empty selection normalizes back to <c>"Manual"</c> (nothing runs on
    /// its own).</summary>
    public static string NormalizeIndexBuildTrigger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultIndexBuildTrigger;

        string[] tokens = value.Split(IndexBuildTriggerSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = new List<string>(IndexBuildTriggerFlags.Length);
        foreach (string flag in IndexBuildTriggerFlags)
        {
            foreach (string token in tokens)
            {
                if (string.Equals(token, flag, StringComparison.OrdinalIgnoreCase))
                {
                    selected.Add(flag);
                    break;
                }
            }
        }
        return selected.Count == 0 ? DefaultIndexBuildTrigger : string.Join(", ", selected);
    }

    /// <summary>Whether a (possibly combined) build-trigger value includes the given individual trigger
    /// flag (e.g. <c>"AtStartup"</c>). Case-insensitive and separator-agnostic.</summary>
    public static bool IndexBuildTriggerHas(string? trigger, string flag)
    {
        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(flag))
            return false;
        foreach (string token in trigger.Split(IndexBuildTriggerSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(token, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Normalizes the OnSchedule schedule mode (Interval / Weekly), defaulting to Interval.</summary>
    public static string NormalizeIndexScheduleMode(string? value)
    {
        foreach (string m in IndexScheduleModes)
            if (string.Equals(value?.Trim(), m, StringComparison.OrdinalIgnoreCase))
                return m;
        return DefaultIndexScheduleMode;
    }

    /// <summary>Clamps the scheduled build interval to a sane range (5 min – 1 week); 0/unset → default 60.</summary>
    public static int NormalizeIndexScheduleIntervalMinutes(int value)
        => value <= 0 ? DefaultIndexScheduleIntervalMinutes
            : Math.Clamp(value, MinimumIndexScheduleIntervalMinutes, MaximumIndexScheduleIntervalMinutes);

    /// <summary>Keeps only the 7 day bits (bit 0 = Sunday … bit 6 = Saturday); an empty selection → every day.</summary>
    public static int NormalizeIndexScheduleDaysOfWeekMask(int value)
    {
        int mask = value & 0x7F;
        return mask == 0 ? DefaultIndexScheduleDaysOfWeekMask : mask;
    }

    /// <summary>Normalizes the weekly time-of-day to HH:mm (24h); anything unparseable → default 03:00.</summary>
    public static string NormalizeIndexScheduleTimeOfDay(string? value)
    {
        if (TimeSpan.TryParse((value ?? string.Empty).Trim(), System.Globalization.CultureInfo.InvariantCulture, out TimeSpan t)
            && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return new TimeSpan(t.Hours, t.Minutes, 0).ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
        return DefaultIndexScheduleTimeOfDay;
    }

    /// <summary>Normalizes the removable-drive policy enum to a known value, defaulting to Never.</summary>
    public static string NormalizeIndexRemovableDrivePolicy(string? value)
    {
        foreach (string p in IndexRemovableDrivePolicies)
            if (string.Equals(value?.Trim(), p, StringComparison.OrdinalIgnoreCase))
                return p;
        return DefaultIndexRemovableDrivePolicy;
    }

    /// <summary>Normalizes the update-mode enum to a known value (case-insensitive), defaulting to
    /// ManualFullRebuild. Accepts <c>ManualFullRebuild</c>, <c>AutomaticFullRebuildWhenDirty</c>, and the
    /// Phase 3 <c>AutomaticIncremental</c>; any unknown value coerces to the manual default.</summary>
    public static string NormalizeIndexUpdateMode(string? value)
    {
        foreach (string m in IndexUpdateModes)
            if (string.Equals(value?.Trim(), m, StringComparison.OrdinalIgnoreCase))
                return m;
        return DefaultIndexUpdateMode;
    }

    /// <summary>Normalizes a custom index storage directory: trims and rejects whitespace-only to empty
    /// (empty means the default <c>%LOCALAPPDATA%\Yagu\content-index</c>). Deeper validation (fixed
    /// local NTFS, writable, ACLs — plan §6.1) is performed by the index storage validator, not here.</summary>
    public static string NormalizeIndexStorageDirectory(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public string? LastDirectory { get; set; }
    /// <summary>When true, Yagu pre-fills the directory box at launch with <see cref="PinnedStartupDirectory"/>
    /// (the user "pinned" a startup directory via the star toggle). When false (default), the box starts
    /// empty (search all drives). This only affects the value the box has at startup — it never overrides a
    /// directory the user types or browses to during a session.</summary>
    public bool PinStartupDirectory { get; set; }
    /// <summary>The directory restored into the box at launch when <see cref="PinStartupDirectory"/> is on.</summary>
    public string? PinnedStartupDirectory { get; set; }
    /// <summary>Order of the Advanced Options tab column, as stable tab keys (see
    /// <c>MainWindow.AdvancedOptions.cs</c>), after the user drag-reorders it. Empty (the default)
    /// means "use the shipped order". Unknown keys are ignored and tabs missing from the list fall
    /// back to their shipped position, so the setting survives tabs being added or renamed.</summary>
    public List<string> AdvancedOptionsTabOrder { get; set; } = [];
    /// <summary>Custom Advanced Options row placements, keyed by stable option ID with a stable tab key
    /// value. Missing entries use the shipped tab. Unknown IDs/tabs are ignored so upgrades fail safe.</summary>
    public Dictionary<string, string> AdvancedOptionPlacements { get; set; } = [];
    /// <summary>The user-editable one-click searches shown on the Advanced Options ▸ Quick searches tab,
    /// in display order. Seeded from the built-in catalog on first run (see
    /// <see cref="QuickSearchesInitialized"/>); afterwards the list is entirely user-owned, so an empty
    /// list means "the user deleted them all" and is preserved rather than re-seeded.</summary>
    public List<Yagu.Helpers.QuickSearchItem> QuickSearches { get; set; } = [];
    /// <summary>False until the built-in quick searches have been seeded into <see cref="QuickSearches"/>.
    /// Distinguishes a fresh profile from one where the user emptied the list.</summary>
    public bool QuickSearchesInitialized { get; set; }
    public List<string> RecentDirectories { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];
    /// <summary>Separate autocomplete history for the Semantic (natural-language) query mode, kept
    /// distinct from the Traditional <see cref="SearchHistory"/> so the two suggestion lists never mix.</summary>
    public List<string> SemanticSearchHistory { get; set; } = [];
    /// <summary>When each entry was last added/used, keyed by the entry value, for the autocomplete
    /// dropdowns' trailing date column. Entries recorded before this existed simply have no key.</summary>
    public Dictionary<string, DateTimeOffset> RecentDirectoryTimes { get; set; } = new();
    public Dictionary<string, DateTimeOffset> SearchHistoryTimes { get; set; } = new();
    public Dictionary<string, DateTimeOffset> SemanticSearchHistoryTimes { get; set; } = new();
    [JsonIgnore] public bool CaseSensitive { get; set; }
    [JsonIgnore] public bool UseRegex { get; set; }
    [JsonIgnore] public bool ExactMatch { get; set; } = true;
    /// <summary>Initial state of the search-box "Match across lines (multiline)" toggle. Shipped
    /// default false so multiline stays strictly opt-in. Like <see cref="CaseSensitive"/> /
    /// <see cref="UseRegex"/> / <see cref="ExactMatch"/>, this is <c>[JsonIgnore]</c> — session-only,
    /// NOT persisted: the toggle resets to this default on every launch instead of remembering the
    /// last-used state, so a reinstall (or restart) never carries a previously-enabled multiline
    /// toggle forward.</summary>
    [JsonIgnore] public bool MultilineSearchDefault { get; set; }
    /// <summary>Persisted native multiline engine (Phase 2): 0 = hand-rolled regex::bytes (default),
    /// 1 = grep-searcher. Both produce identical results — a pure performance knob. Global (not
    /// per-search); also settable via the CLI <c>--multiline-engine</c> flag.</summary>
    public int MultilineEngine { get; set; }
    [JsonIgnore] public bool ObeyGitignore { get; set; }
    public bool GitignoreTakesPrecedence { get; set; } = true;
    // User's saved preference for .gitignore vs Include-filter precedence on conflict.
    // null = unset (ask via the precedence dialog), true = .gitignore wins, false = Include filter wins.
    public bool? GitignorePrecedencePreference { get; set; }
    public int ContextLines { get; set; } = 3;
    public int PreviewContextLines { get; set; } = 10;
    // Advanced-Options path filters are per-session only: edits made in Advanced
    // Options must never persist to settings.json. They reset to defaults on next
    // launch, matching the size/date filters below.
    [JsonIgnore] public string IncludeGlobs { get; set; } = string.Empty;
    [JsonIgnore] public string ExcludeGlobs { get; set; } = DefaultExcludeGlobs;
    [JsonIgnore] public int IncludeFilterModeIndex { get; set; }
    [JsonIgnore] public int ExcludeFilterModeIndex { get; set; }
    [JsonIgnore] public long MinFileSizeBytes { get; set; }
    [JsonIgnore] public long MaxFileSizeBytes { get; set; }
    [JsonIgnore] public DateTimeOffset? CreatedAfterDate { get; set; }
    [JsonIgnore] public DateTimeOffset? CreatedBeforeDate { get; set; }
    [JsonIgnore] public DateTimeOffset? ModifiedAfterDate { get; set; }
    [JsonIgnore] public DateTimeOffset? ModifiedBeforeDate { get; set; }
    public long DefaultMinFileSizeBytes { get; set; }
    public long DefaultMaxFileSizeBytes { get; set; }
    public DateTimeOffset? DefaultCreatedAfterDate { get; set; }
    public DateTimeOffset? DefaultCreatedBeforeDate { get; set; }
    public DateTimeOffset? DefaultModifiedAfterDate { get; set; }
    public DateTimeOffset? DefaultModifiedBeforeDate { get; set; }
    public int MaxResults { get; set; }
    public string EditorCommand { get; set; } = EditorLauncher.DefaultCommand;
    public double SplitPanePosition { get; set; } = 0.5;
    public bool GlobalHotkeyEnabled { get; set; }
    public string GlobalHotkeyKey { get; set; } = HotkeyService.DefaultStartKey.ToString();
    public int PreviewModeIndex { get; set; } = 1; // 0 = Concatenated, 1 = Multi-highlight
    public int ThemeModeIndex { get; set; } // 0 = Auto (system theme), 1 = Dark, 2 = Light
    public bool PreviewWordWrap { get; set; }
    public int PreviewWrapModeIndex { get; set; } = 2; // 0 = Wrap, 1 = legacy PartialWrap, 2 = NoWrap
    public int PreviewLongLineWarningIndex { get; set; } // 0 = Ask every time, 1 = Always open without word wrap, 2 = Always open with word wrap
    public string SelectedPreviewContentBackgroundColor { get; set; } = DefaultSelectedPreviewContentBackgroundColor;
    public string UnselectedPreviewContentBackgroundColor { get; set; } = DefaultUnselectedPreviewContentBackgroundColor;
    public string PreviewGutterContextColor { get; set; } = DefaultPreviewGutterContextColor;
    public string PreviewGutterMatchColor { get; set; } = DefaultPreviewGutterMatchColor;
    public string PreviewEditorGutterColor { get; set; } = DefaultPreviewEditorGutterColor;
    public string PreviewEditorTextColor { get; set; } = DefaultPreviewEditorTextColor;
    public string PreviewMatchTextColor { get; set; } = DefaultPreviewMatchTextColor;
    public string PreviewOverlayColor { get; set; } = DefaultPreviewOverlayColor;
    public string PreviewMatchLineColor { get; set; } = DefaultPreviewMatchLineColor;
    public string PreviewShowMoreEllipsisColor { get; set; } = DefaultPreviewShowMoreEllipsisColor;
    public int PreviewShowMoreEllipsisFontSize { get; set; } = DefaultPreviewShowMoreEllipsisFontSize;
    public string PreviewTextFontFamily { get; set; } = DefaultPreviewTextFontFamily;
    public int PreviewTextFontSize { get; set; } = DefaultPreviewTextFontSize;
    public string PreviewEditorFontFamily { get; set; } = DefaultPreviewEditorFontFamily;
    public int PreviewEditorFontSize { get; set; } = DefaultPreviewEditorFontSize;
    public string ResultListMatchTextFontFamily { get; set; } = DefaultResultListMatchTextFontFamily;
    public int ResultListMatchTextFontSize { get; set; } = DefaultResultListMatchTextFontSize;
    public string ResultListMatchHighlightColor { get; set; } = DefaultResultListMatchHighlightColor;

    // ── File list overlay settings ──
    public int FileListOverlayHeight { get; set; } = DefaultFileListOverlayHeight;
    public int FileListOverlayFontSize { get; set; } = DefaultFileListOverlayFontSize;
    public string FileListOverlayFontColor { get; set; } = DefaultFileListOverlayFontColor;
    public string FileListOverlayFontFamily { get; set; } = DefaultFileListOverlayFontFamily;

    // ── Preview sticky file header overlay settings ──
    public int PreviewStickyHeaderHeight { get; set; } = DefaultPreviewStickyHeaderHeight;
    public int PreviewStickyHeaderFileNameFontSize { get; set; } = DefaultPreviewStickyHeaderFileNameFontSize;
    public string PreviewStickyHeaderFileNameFontColor { get; set; } = DefaultPreviewStickyHeaderFileNameFontColor;
    public string PreviewStickyHeaderFileNameFontFamily { get; set; } = DefaultPreviewStickyHeaderFileNameFontFamily;
    public int PreviewStickyHeaderDetailFontSize { get; set; } = DefaultPreviewStickyHeaderDetailFontSize;
    public string PreviewStickyHeaderDetailFontColor { get; set; } = DefaultPreviewStickyHeaderDetailFontColor;
    public string PreviewStickyHeaderDetailFontFamily { get; set; } = DefaultPreviewStickyHeaderDetailFontFamily;

    // ── File list drawer label settings ──
    public int DrawerFileNameFontSize { get; set; } = DefaultDrawerFileNameFontSize;
    public string DrawerFileNameFontColor { get; set; } = DefaultDrawerFileNameFontColor;
    public string DrawerFileNameFontFamily { get; set; } = DefaultDrawerFileNameFontFamily;
    public int DrawerDirectoryFontSize { get; set; } = DefaultDrawerDirectoryFontSize;
    public string DrawerDirectoryFontColor { get; set; } = DefaultDrawerDirectoryFontColor;
    public string DrawerDirectoryFontFamily { get; set; } = DefaultDrawerDirectoryFontFamily;
    public int DrawerMetadataFontSize { get; set; } = DefaultDrawerMetadataFontSize;
    public string DrawerMetadataFontColor { get; set; } = DefaultDrawerMetadataFontColor;
    public string DrawerMetadataFontFamily { get; set; } = DefaultDrawerMetadataFontFamily;

    public int LogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose (file logging)
    public int ConsoleLogLevelIndex { get; set; } = 1; // -1 = None, 0 = Critical, 1 = Warning, 2 = Info, 3 = Verbose
    public int FileListerBackendIndex { get; set; } // 0 = Auto, 1 = SDK, 2 = es.exe, 3 = Managed
    public int ParallelismIndex { get; set; } = 4; // 0 = safe cap, 1 = 1, 2 = half cores, 3 = 2x cores, 4 = all cores
    public int IoOversubscriptionIndex { get; set; } // 0 = Auto (SSD 1x, HDD 2x), 1 = 1x, 2 = 2x, 3 = 3x
    public int LineTruncationLength { get; set; } = 500;
    public int MaxRecentItems { get; set; } = 20;
    /// <summary>Max Semantic-mode natural-language queries to remember for autocomplete.</summary>
    public int MaxSemanticRecentItems { get; set; } = 20;
    /// <summary>How many directory / search-pattern autocomplete suggestions are visible at once in the
    /// dropdown before scrolling is required (GUI only). Distinct from the "max ... to remember" caps above,
    /// which control how many are stored. Default 5.</summary>
    public int AutocompleteDropdownVisibleItems { get; set; } = 5;
    /// <summary>Hard process memory cap in MB. 0 = automatic sub-GB paging target.</summary>
    public int MemoryLimitMB { get; set; }
    /// <summary>System-wide memory pressure threshold (0-100). Search evicts cached results and switches to memory-saving mode when total machine memory usage exceeds this %. 0 = disabled.</summary>
    public int MemoryPressurePercent { get; set; } = 75;
    /// <summary>Directory used for memory-saving search result temp files.</summary>
    public string? SearchResultTempDirectory { get; set; }
    /// <summary>Whether the user has chosen the search result temp-file location.</summary>
    public bool HasChosenSearchResultTempDirectory { get; set; }
    /// <summary>Terminates active searches when the search result temp-file drive is more than this full. Valid range 1-99.</summary>
    public int LowDiskSpaceWarningPercent { get; set; } = DefaultLowDiskSpaceWarningPercent;
    /// <summary>When true, show the memory pressure warning label in the results toolbar. Hidden by default.</summary>
    public bool ShowMemoryPressureWarningLabel { get; set; }
    /// <summary>When true, show throughput labels and disk utilization sparkline in the bottom status bar.</summary>
    public bool ShowStatsForNerds { get; set; }
    /// <summary>When true, show result-temp, content-index storage, and Yagu/worker RAM usage in the
    /// bottom status bar. Hidden by default and configurable under Developer Options.</summary>
    public bool ShowResourceUsageInStatusBar { get; set; }
    /// <summary>When true, show the bottom-right live-log button and debug panel. On by default and
    /// configurable under Developer Options.</summary>
    public bool ShowDebugPanel { get; set; } = true;
    /// <summary>When true, append the app version/build number to the main title bar.</summary>
    public bool ShowBuildNumberInTitleBar { get; set; }
    /// <summary>When true, show the Auto-scroll checkbox in the results toolbar. Hidden by default.</summary>
    public bool ShowAutoScrollResultsCheckbox { get; set; }
    /// <summary>Bounded channel buffer size for the Everything SDK streaming path. Higher values use more memory but can improve throughput.</summary>
    public int SdkChannelBufferSize { get; set; } = 4096;
    /// <summary>Current directory recursion depth. 0 = unlimited. This is intentionally session-only.</summary>
    [JsonIgnore] public int MaxSearchDepth { get; set; }
    /// <summary>Optional hard cap on stored matches per file. 0 = unlimited (default). Useful for capping pathological files (massive logs, generated dumps) that would otherwise dominate the heap.</summary>
    public int MaxMatchesPerFile { get; set; }
    /// <summary>Maximum matches emitted from a single line before the scanner moves to the next line. 0 = unlimited (default).
    /// A positive value bounds a match-everything pattern (e.g. the regex <c>.</c>) on a very long minified line from
    /// emitting millions of matches.</summary>
    public int MaxMatchesPerLine { get; set; }
    /// <summary>Absolute safety ceiling on total matches that applies EVEN WHEN <c>MaxResults</c> is 0 (unlimited).
    /// When &gt; 0, an unbounded content search stops once reached (result marked truncated). Default 0 (disabled —
    /// no truncation); memory-pressure eviction and the per-line cap still protect against runaway usage.</summary>
    public int AbsoluteMaxResults { get; set; }
    /// <summary>Whether to skip binary files during content search. Default true.</summary>
    [JsonIgnore] public bool SkipBinary { get; set; } = true;
    /// <summary>When true, the scanner opens cloud-only (online-only) placeholder files
    /// — OneDrive Files On-Demand / Google Drive — hydrating them on demand when a live
    /// provider is present. When false (default), such files are skipped so the scan can
    /// never block on a hydration that may never complete. Default false.</summary>
    public bool SearchOnlineOnlyFiles { get; set; }
    /// <summary>When true (default), files and folders with the Windows Hidden attribute are searched. When false, hidden items are excluded. Persisted; also the default for the per-search Advanced Options toggle.</summary>
    public bool SearchHiddenFiles { get; set; } = true;
    /// <summary>When true, raster image files (PNG/JPG/etc.) are OCR'd on a background queue and their
    /// recognized text is searched. Default false. Persisted; also the default for the per-search
    /// Advanced Options ▸ Filters "Search image text" toggle.</summary>
    public bool SearchImageText { get; set; }
    /// <summary>When true, PDF files are converted to text (via the bundled Xpdf <c>pdftotext</c>) on a
    /// background queue and their extracted text is searched. Default false. Persisted; also the default
    /// for the per-search Advanced Options ▸ Filters "Search PDF text" toggle.</summary>
    public bool SearchPdfText { get; set; }
    /// <summary>OCR engine used when <see cref="SearchImageText"/> is on: "paddle" (PaddleSharp) or
    /// "tesseract". Defaults to <see cref="EffectiveDefaultImageOcrEngine"/> (PaddleSharp on x64 and
    /// Arm64; Tesseract on x86, where PaddleOCR's x64-only runtime cannot load). Normalized on load —
    /// a persisted "paddle" is coerced to "tesseract" on x86.</summary>
    public string ImageOcrEngine { get; set; } = EffectiveDefaultImageOcrEngine;
    /// <summary>Preferred OCR engine where it can run (PaddleSharp): faster and more accurate than
    /// Tesseract on CPU (OCR runs on CPU in the x64 worker), and the offline installer bundles its full
    /// native runtime + PP-OCR models so it needs no download. NOTE: PaddleOCR's native runtime is
    /// win-x64 only, so this is the effective default only on x64/Arm64 — see
    /// <see cref="EffectiveDefaultImageOcrEngine"/>. Tesseract remains a user-selectable engine
    /// (also bundled offline).</summary>
    public const string DefaultImageOcrEngine = "paddle";

    /// <summary>Whether PaddleOCR can run in this process. Its native runtime (PaddleInference + OpenCV)
    /// is win-x64 only and cannot load in a 32-bit (x86) process. True on x64 and Arm64 (Arm64 runs the
    /// x64 runtime under emulation); false on x86, where the effective OCR engine is always Tesseract.</summary>
    internal static bool PaddleOcrSupported =>
        RuntimeInformation.ProcessArchitecture
            != Architecture.X86;

    /// <summary>Resolves the default OCR engine given whether Paddle can run: PaddleSharp when
    /// supported, otherwise Tesseract. Pure (arch injected) so both branches are testable.</summary>
    internal static string ResolveDefaultImageOcrEngine(bool paddleSupported) =>
        paddleSupported ? DefaultImageOcrEngine : "tesseract";

    /// <summary>Coerces a resolved engine id to Tesseract when Paddle is unsupported (x86), since
    /// PaddleOCR's x64-only runtime cannot load there. Pure (arch injected) for testability.</summary>
    internal static string CoerceImageOcrEngineForArch(string engine, bool paddleSupported) =>
        engine == "paddle" && !paddleSupported ? "tesseract" : engine;

    /// <summary>The effective default OCR engine for this build: <see cref="DefaultImageOcrEngine"/>
    /// (PaddleSharp) on x64 and Arm64, but "tesseract" on x86 because PaddleOCR's x64-only runtime
    /// cannot load in a 32-bit process (<see cref="PaddleOcrSupported"/>). Users can still switch
    /// engines in settings, but a paddle selection is coerced back to tesseract on x86.</summary>
    public static readonly string EffectiveDefaultImageOcrEngine = ResolveDefaultImageOcrEngine(PaddleOcrSupported);
    /// <summary>PaddleOCR model used for image-text recognition: "EnglishV3", "EnglishV4",
    /// "ChineseV4" or "ChineseV5" (default; PP-OCRv5, multilingual). Higher/newer models trade speed for
    /// accuracy. Normalized on load. Ignored by the Tesseract engine.</summary>
    public string ImageOcrModel { get; set; } = DefaultImageOcrModel;
    public const string DefaultImageOcrModel = "ChineseV5";
    /// <summary>PaddleOCR detection resolution cap (longest image side, in pixels) for image-text OCR.
    /// Higher = better accuracy on small text, slower. 0 = unlimited. Default 960. Ignored by Tesseract.</summary>
    public int ImageOcrMaxSide { get; set; } = DefaultImageOcrMaxSide;
    public const int DefaultImageOcrMaxSide = 960;
    public const int MinimumImageOcrMaxSide = 320;
    public const int MaximumImageOcrMaxSide = 4096;
    /// <summary>Independent OCR worker processes per image-text search. 0 = conservative automatic
    /// selection by engine/CPU; explicit range 1–4. The global HDD parallelism safeguard can still
    /// force the effective per-root count to one.</summary>
    public int ImageOcrWorkerParallelism { get; set; } = DefaultImageOcrWorkerParallelism;
    public const int DefaultImageOcrWorkerParallelism = OcrWorkerParallelism.Automatic;
    public const int MaximumImageOcrWorkerParallelism = OcrWorkerParallelism.Maximum;
    /// <summary>True once the user has approved the one-time download of the OCR engine + language
    /// models (the native PaddleOCR runtime and models, ~365 MB). Default false: image-text (OCR)
    /// search warns and asks for consent before initiating any external download. Set to true when
    /// the user approves the prompt, or implicitly when an OCR-bundled installer pre-stages the
    /// assets (no download is ever needed). Persisted so the warning is shown at most once.</summary>
    public bool OcrDownloadConsented { get; set; }
    // ── Content index (plan §6.1 "Indexing" tab) ──
    /// <summary>Master switch for the persistent content index. Default true, but no folder is indexed
    /// until the user adds one. Disabling stops new build/update work without deleting existing data.</summary>
    public bool EnableContentIndex { get; set; } = DefaultEnableContentIndex;
    /// <summary>Whether GUI and CLI searches use the index by default. Effectively false while
    /// <see cref="EnableContentIndex"/> is off; per-search <c>--no-index</c>/<c>--use-index</c> can
    /// override it only when the master is enabled. The session-only
    /// <c>SearchOptions.UseContentIndex</c> is derived from this and never persisted (plan §6.1).</summary>
    public bool UseContentIndexByDefault { get; set; } = DefaultUseContentIndexByDefault;
    /// <summary>Query-family acceleration gates (plan §6.1). Each can only narrow the correctness
    /// eligibility gate — it can never force an unsafe query onto the index.</summary>
    public bool IndexAccelerateLiterals { get; set; } = true;
    public bool IndexAccelerateWholeWord { get; set; } = true;
    public bool IndexAccelerateRegex { get; set; } = true;
    public bool IndexAccelerateMultiline { get; set; } = true;
    /// <summary>Run build/maintenance work and legacy candidate-set evaluation in isolated
    /// <c>Yagu.IndexWorker</c> processes. The legacy query path still opens/classifies the index in the
    /// Yagu process and remains subject to <see cref="IndexMaxInProcessSizeMB"/>. Full mapped query
    /// sessions are controlled independently by <see cref="IndexUseWorkerQuerySessions"/>. Default true.</summary>
    public bool IndexUseNativeWorker { get; set; } = true;
    /// <summary>
    /// Build a PDF-text extended-source namespace during an index build and use it to skip (prune) PDFs
    /// whose extracted text cannot contain a match (plan §7 Phase 4). Default FALSE (extended-source
    /// pruning is opt-in). It only ever prunes when: the extractor (<c>pdftotext.exe</c>) is proven
    /// repeatable at build time, its fingerprint still matches, the PDF was seen at build time, and the
    /// file is unchanged — otherwise the PDF is always live-extracted. Matching PDFs are always read live.
    /// </summary>
    public bool IndexBuildPdfTextExtendedSource { get; set; }
    /// <summary>
    /// Build a positive-only image-text namespace during a full index build. OCR is non-deterministic,
    /// so this namespace may only prioritize images whose prior recognized text is a query candidate;
    /// it never skips OCR for a nonmember. Default FALSE (opt-in because whole-drive OCR can be costly).
    /// </summary>
    public bool IndexBuildImageTextExtendedSource { get; set; }
    /// <summary>
    /// Produce the additive <b>format-v3 query structures</b> (plan §5.1) during an index build — the
    /// query-ready, memory-map-friendly inverted postings + collision-verified path/tombstone indexes +
    /// reverse identity index consumed by mapped out-of-process query sessions and the optional in-process
    /// v3 reader. Default TRUE. Enabling it adds build work and disk sidecars; every
    /// active layer must have them before the all-v3 mapped worker can serve a scope.
    /// </summary>
    public bool IndexProduceV3QueryStructures { get; set; } = true;
    /// <summary>
    /// Consume the additive <b>format-v3 query structures</b> (plan §5.1) in-process during search: when a
    /// generation has the v3 sidecars, the candidate content-id set is produced by the memory-mapped
    /// <c>ContentIndexV3Reader</c> instead of the deserialized posting index, so resident memory tracks only
    /// the pages a query touches. Default FALSE and EXPERIMENTAL. A generation without v3 (un-upgraded) or
    /// any read fault transparently falls back to the in-process evaluation — identical results. Requires
    /// <see cref="IndexProduceV3QueryStructures"/> to have been on when the index was built.
    /// </summary>
    public bool IndexUseV3QueryReader { get; set; }
    /// <summary>
    /// User-selectable gate for the out-of-process <b>mapped query worker</b> (plan §5.8).
    /// When on, eligible scopes are actively pruned in the isolated worker over memory-mapped format-v3
    /// structures (base + segments), with B1 rescue and fail-closed live-scan fallback. The main process
    /// holds no index postings. Default TRUE; all active layers require v3 sidecars and tombstone coverage.
    /// It is separate from <see cref="IndexUseNativeWorker"/>, whose default candidate-offload path queries
    /// content.bin rather than opening a mapped per-path session. Exposed in Settings ▸ Indexing ▸
    /// Query Acceleration and through <c>--index-config IndexUseWorkerQuerySessions=true</c>.
    /// </summary>
    public bool IndexUseWorkerQuerySessions { get; set; } = true;
    /// <summary>One-time migration guard for the former format-v3/mapped-worker false/false defaults.
    /// Once set, later user changes to either persisted toggle are preserved.</summary>
    public bool IndexMappedWorkerDefaultsMigrated { get; set; }
    /// <summary>Maximum foreground worker open/catch-up/planning budget before a search bypasses the
    /// index. Normalized to [25, 2000] ms; default 200. Discovery never waits for it (plan §6.1).</summary>
    public int IndexQueryStartupBudgetMs { get; set; } = DefaultIndexQueryStartupBudgetMs;
    /// <summary>Performance-only bypass when estimated posting candidates exceed this corpus
    /// percentage. Normalized to [1, 100]; default 25. Can only choose live scan (plan §6.1).</summary>
    public int IndexMaxCandidatePercent { get; set; } = DefaultIndexMaxCandidatePercent;
    /// <summary>Worker budget for posting decode / candidate / provisional bitmaps. Default 64 MB on
    /// 64-bit / 32 MB on x86. Exceeding it bypasses the index (plan §6.1).</summary>
    public int IndexQueryMemoryBudgetMB { get; set; } = 0;
    /// <summary>Mapped-query worker classification degree. 0 = automatic from logical processors;
    /// otherwise 1-32. The existing HDD safeguard forces one for rotational search roots.</summary>
    public int IndexQueryWorkerParallelism { get; set; } = DefaultIndexQueryWorkerParallelism;
    /// <summary>Largest CURRENT on-disk index (base + active segments) the search will load into memory to
    /// accelerate a query; larger indexes live-scan instead (a multi-GB in-memory index degrades search
    /// speed more than it helps). 0 = never load in memory. Default 2048 MB (2 GB).</summary>
    public int IndexMaxInProcessSizeMB { get; set; } = DefaultIndexMaxInProcessSizeMB;
    /// <summary>Largest CURRENT on-disk index the isolated out-of-process worker will memory-map to serve a
    /// query (the worker pages the mapped v3, so this is far larger than the in-process deserialize cap);
    /// larger indexes live-scan instead. 0 = never use the worker. Default 30720 MB (30 GB).</summary>
    public int IndexMaxWorkerQuerySizeMB { get; set; } = DefaultIndexMaxWorkerQuerySizeMB;
    /// <summary>Custom index storage directory; empty means <c>%LOCALAPPDATA%\Yagu\content-index</c>.
    /// A custom value must resolve to a fixed local NTFS volume (validated elsewhere, plan §6.1).</summary>
    public string IndexStorageDirectory { get; set; } = string.Empty;
    /// <summary>Hard per-file size cap for ingestion (MB). Over-cap files live-scan. Default 100.</summary>
    public int IndexMaxFileSizeMB { get; set; } = DefaultIndexMaxFileSizeMB;
    /// <summary>Storage ceiling for one index (MB); 0 = no ceiling. Reaching it escalates reclamation and,
    /// if the index still cannot be brought under, pauses that index's maintenance. Default 51200 MB
    /// (50 GiB) on 64-bit / 25600 MB on x86.</summary>
    public int IndexMaxDiskSizeMB { get; set; } = 0;
    /// <summary>One-time migration guard for settings written with the pre-50 GiB size budget and the
    /// 128/512 MB coalescing bounds, which together let a whole-drive index halt with nothing able to
    /// reclaim it.</summary>
    public bool IndexSizeDefaultsMigrated { get; set; }
    /// <summary>Reserved free-space floor (MB). Default 2048 (plan §6.1/§11.2).</summary>
    public int IndexMinimumFreeSpaceMB { get; set; } = DefaultIndexMinimumFreeSpaceMB;
    /// <summary>Stop an index build when the index drive is at least this percent full (plan §11.2).
    /// Default 90; range 50–99. A staged full build is discarded and the prior index stays active.</summary>
    public int IndexMaxDiskUsagePercent { get; set; } = DefaultIndexMaxDiskUsagePercent;
    /// <summary>Total generations retained including current. Minimum 1; default 2 (plan §6.1).</summary>
    public int IndexRetainedGenerationCount { get; set; } = DefaultIndexRetainedGenerationCount;
    /// <summary>Stale temporary-build cleanup age (hours). Default 24 (plan §6.1).</summary>
    public int IndexStaleTemporaryHours { get; set; } = DefaultIndexStaleTemporaryHours;
    /// <summary>Quarantine retention for a failed generation (days). Default 7 (plan §6.1).</summary>
    public int IndexQuarantineRetentionDays { get; set; } = DefaultIndexQuarantineRetentionDays;
    /// <summary>Build trigger(s): any combination of WhenEnabled / AtStartup / WhenIdle / Continuous / OnSchedule,
    /// stored as a normalized, comma-separated list (e.g. <c>"AtStartup, OnSchedule"</c>). "Manual" (the
    /// default) means none are selected, so indexing never begins unexpectedly (plan §6.1).</summary>
    public string IndexBuildTrigger { get; set; } = DefaultIndexBuildTrigger;

    /// <summary>What an automatic build pass does (plan §6.1). <c>ManualFullRebuild</c> (default) only builds
    /// roots that have no index; <c>AutomaticFullRebuildWhenDirty</c> also rebuilds a root whose change
    /// journal proves it changed since the index was built. Only takes effect when <see cref="IndexBuildTrigger"/>
    /// is automatic. Incremental delta updates are a Phase 3 addition.</summary>
    public string IndexUpdateMode { get; set; } = DefaultIndexUpdateMode;
    /// <summary>Required time without keyboard or mouse input before a WhenIdle pass, and the minimum
    /// interval between repeated idle passes while the machine remains idle. Default 5 minutes.</summary>
    public int IndexIdleDelayMinutes { get; set; } = DefaultIndexIdleDelayMinutes;
    /// <summary>Minimum interval between Continuous maintenance passes while Yagu remains open. Default
    /// 5 minutes.</summary>
    public int IndexContinuousIntervalMinutes { get; set; } = DefaultIndexContinuousIntervalMinutes;
    /// <summary>One-time migration guard for settings written when <see cref="IndexIdleDelayMinutes"/>
    /// controlled both idle and continuous maintenance cadence.</summary>
    public bool IndexContinuousIntervalMigrated { get; set; }
    /// <summary>One-time migration guard for the former one-minute first-run continuous cadence.</summary>
    public bool IndexOneMinuteContinuousIntervalMigrated { get; set; }

    /// <summary>Schedule mode when <see cref="IndexBuildTrigger"/> is OnSchedule: <c>Interval</c> (every N
    /// minutes) or <c>Weekly</c> (on chosen days of the week at a set time). Default Interval.</summary>
    public string IndexScheduleMode { get; set; } = DefaultIndexScheduleMode;
    /// <summary>Minutes between scheduled build passes in <c>Interval</c> mode. Range 5–10080; default 60.</summary>
    public int IndexScheduleIntervalMinutes { get; set; } = DefaultIndexScheduleIntervalMinutes;
    /// <summary>Days-of-week bitmask for <c>Weekly</c> mode (bit 0 = Sunday … bit 6 = Saturday). Default 127 (every day).</summary>
    public int IndexScheduleDaysOfWeekMask { get; set; } = DefaultIndexScheduleDaysOfWeekMask;
    /// <summary>Time of day (HH:mm, 24h) for <c>Weekly</c> mode. Default 03:00.</summary>
    public string IndexScheduleTimeOfDay { get; set; } = DefaultIndexScheduleTimeOfDay;

    /// <summary>Total worker build commit budget (MB). Default 384 on 64-bit / 192 on x86.</summary>
    public int IndexBuildMemoryBudgetMB { get; set; } = 0;
    /// <summary>Full-build file-read/classification degree. 0 = automatic from physical cores and the
    /// build-memory budget; otherwise 1-32. The existing HDD safeguard forces one for rotational roots.</summary>
    public int IndexBuildWorkerParallelism { get; set; } = DefaultIndexBuildWorkerParallelism;
    /// <summary>Pause a same-volume build while a foreground search runs. Default true (plan §6.1).</summary>
    public bool IndexPauseDuringForegroundSearch { get; set; } = true;
    /// <summary>Pause builds while on battery. Default true (plan §6.1).</summary>
    public bool IndexPauseOnBattery { get; set; } = true;
    /// <summary>Removable-drive policy: Never / ExplicitRootsOnly. Default Never (plan §6.1).</summary>
    public string IndexRemovableDrivePolicy { get; set; } = DefaultIndexRemovableDrivePolicy;
    /// <summary>Whether index builds follow reparse points (same-volume, in-root targets only).
    /// Default false (plan §6.1).</summary>
    public bool IndexFollowReparsePoints { get; set; }
    /// <summary>Whether index builds ingest hidden files. Default aligned with the search setting.</summary>
    public bool IndexIncludeHiddenFiles { get; set; } = true;
    /// <summary>Foreground journal catch-up budget (MB). Default 64. Exceeding it bypasses the search
    /// while low-priority catch-up continues (plan §6.1).</summary>
    public int IndexMaxJournalCatchupMB { get; set; } = DefaultIndexMaxJournalCatchupMB;
    /// <summary>Foreground journal catch-up record budget. Default 2,000,000 (plan §6.1).</summary>
    public int IndexMaxJournalCatchupRecords { get; set; } = DefaultIndexMaxJournalCatchupRecords;
    /// <summary>After a full build, automatically apply an incremental delta before publication when the
    /// journal contains more than this many changes since the build started. Zero catches up any non-empty
    /// delta; default 30,000.</summary>
    public int IndexPostBuildCatchUpThresholdChanges { get; set; } = DefaultIndexPostBuildCatchUpThresholdChanges;

    /// <summary>Maximum wall time for one file open/read or low-level volume I/O operation. A timed-out
    /// search file is skipped; an index mutation fails closed or leaves the file live-scanned.</summary>
    public int FileIoTimeoutSeconds { get; set; } = DefaultFileIoTimeoutSeconds;
    /// <summary>Corruption/incompatibility auto-repair (schedules a rebuild per the build trigger).
    /// Default true; false means report only. Fallback to live scan is unconditional (plan §6.1).</summary>
    public bool IndexAutoRepair { get; set; } = true;
    // ── Phase 3 incremental maintenance (plan §11.4) ──
    /// <summary>Whether the incremental updater consults a <see cref="System.IO.FileSystemWatcher"/> as a
    /// low-latency hint about which roots changed. Default false. The watcher is only a hint — USN
    /// continuity remains authoritative — so a watch-registration failure/limit degrades to USN/manual with
    /// no correctness impact (plan §11.4).</summary>
    public bool IndexUseWatcherHints { get; set; }
    /// <summary>Maximum append-only delta segments layered over a base before a compaction folds them into a
    /// fresh base (plan §11.4). Normalized to [1, 64]; default 8.</summary>
    public int IndexMaxDeltaSegments { get; set; } = DefaultIndexMaxDeltaSegments;
    /// <summary>Accumulated delta-segment size (MB) that triggers a compaction into a fresh base, whichever
    /// bound is hit first (plan §11.4). Normalized to [16, 8192]; default 256.</summary>
    public int IndexCompactionThresholdMB { get; set; } = DefaultIndexCompactionThresholdMB;
    /// <summary>Largest total on-disk index size (MB) that the AUTOMATIC over-segmented compaction is allowed
    /// to fold in-process. Compaction briefly loads the whole index into memory (a transient multi-GB spike),
    /// so above this cap a large over-segmented index is left segmented instead — searches still use it, and
    /// the query-mode load already bounds their footprint. 0 disables the cap (always compact). Default 512.
    /// Manual/explicit compaction is never gated by this.</summary>
    public int IndexMaxAutoCompactionSizeMB { get; set; } = DefaultIndexMaxAutoCompactionSizeMB;
    /// <summary>Default size-management strategy for every index, overridable per folder via
    /// <see cref="IndexedRootSizePolicies"/>. One of <see cref="Yagu.Services.Index.IndexSizeManagementPolicy.Modes"/>;
    /// default <c>CoalesceThenCompact</c>. Only decides how an index reorganizes its own storage — never what a
    /// search returns.</summary>
    public string IndexSizeManagementMode { get; set; } = Yagu.Services.Index.IndexSizeManagementModes.CoalesceThenCompact;
    /// <summary>Largest individual delta segment (MB) eligible to join a coalescing run. Default 256.</summary>
    public int IndexCoalesceMaxSegmentMB { get; set; } = DefaultIndexCoalesceMaxSegmentMB;
    /// <summary>Largest total size (MB) of one coalescing run. Bounds maintenance-worker memory. Default 1024.</summary>
    public int IndexCoalesceMaxBatchMB { get; set; } = DefaultIndexCoalesceMaxBatchMB;
    /// <summary>Fewest contiguous eligible segments that make a coalescing run worth merging. Default 4.</summary>
    public int IndexCoalesceMinRun { get; set; } = DefaultIndexCoalesceMinRun;
    /// <summary>Most coalescing runs merged in a single maintenance pass. Default 8.</summary>
    public int IndexCoalesceMaxRunsPerPass { get; set; } = DefaultIndexCoalesceMaxRunsPerPass;
    /// <summary>Opt-in to sharing <b>aggregate-only</b> index telemetry (build/refresh time, segment and
    /// compaction counts, index-used vs bypassed) on top of global telemetry. Default false, and inert
    /// unless <see cref="TelemetryEnabled"/> is also on and an endpoint is configured (plan §6.4). Never
    /// carries roots, paths, queries, trigrams, file identities, or any content-derived data.</summary>
    public bool ShareAggregateIndexTelemetry { get; set; }
    /// <summary>Show indexed/full/partial/bypassed status in the main window. Default true (plan §6.2).</summary>
    public bool ShowIndexStatusInMainWindow { get; set; } = true;
    /// <summary>Show background build progress notifications. Default true (plan §6.2).</summary>
    public bool ShowIndexBuildNotifications { get; set; } = true;
    /// <summary>Show a per-result index/live provenance glyph in the results list and preview header.
    /// Default true, but only rendered when the index participated in the search (plan §6.2).</summary>
    public bool ShowIndexProvenanceInResults { get; set; } = true;
    /// <summary>Comma/semicolon-separated build-time ingestion exclude globs (not query filters).
    /// Empty by default (plan §6.1).</summary>
    public string IndexExcludedGlobs { get; set; } = string.Empty;
    /// <summary>Comma/semicolon-separated build-time ingestion exclude extensions. Empty by default.</summary>
    public string IndexExcludedExtensions { get; set; } = string.Empty;
    /// <summary>The folders the user has registered for content indexing (plan §6.1). Canonicalized and
    /// de-duplicated on load by <see cref="Yagu.Services.Index.IndexedRootsPolicy"/>; managed from the
    /// Indexing tab and the CLI root commands. Empty by default.</summary>
    public List<string> IndexedRoots { get; set; } = [];
    /// <summary>Unregistered search roots whose "not indexed" pre-search warning the user dismissed.
    /// Searches still scan these locations live; only the warning is suppressed. Registered roots and
    /// stale/broken indexes always remain actionable and are never suppressed by this list.</summary>
    public List<string> ContentIndexLiveScanWarningDismissedRoots { get; set; } = [];
    /// <summary>Optional per-folder build-time glob overrides for the registered <see cref="IndexedRoots"/>
    /// (plan §6.1). Each entry layers on top of <see cref="IndexExcludedGlobs"/>: the root's exclude globs
    /// add more excludes and its include globs re-admit paths a broader exclude would drop (so, e.g.,
    /// <c>node_modules</c> can be excluded globally but indexed under one specific folder). Canonicalized on
    /// load by <see cref="Yagu.Services.Index.IndexedRootFilterPolicy"/>. Empty by default.</summary>
    public List<Yagu.Services.Index.IndexedRootFilter> IndexedRootFilters { get; set; } = [];
    /// <summary>Optional per-folder size-management overrides for the registered <see cref="IndexedRoots"/>.
    /// Each entry may pin the strategy, the storage ceiling, and the automatic-compaction cap for one index,
    /// inheriting whatever it leaves unset. Canonicalized on load by
    /// <see cref="Yagu.Services.Index.IndexSizeManagementPolicy"/>. Empty by default.</summary>
    public List<Yagu.Services.Index.IndexedRootSizePolicy> IndexedRootSizePolicies { get; set; } = [];

    public static List<string> NormalizeContentIndexLiveScanWarningDismissedRoots(IEnumerable<string>? roots)
    {
        var normalized = new List<string>();
        if (roots is null)
            return normalized;

        foreach (string? root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string path = Yagu.Services.Index.IndexScopeIdentity.NormalizePath(root);
            if (path.Length > 0 && !normalized.Contains(path, StringComparer.OrdinalIgnoreCase))
                normalized.Add(path);
        }
        return normalized;
    }
    /// <summary>Effective query memory budget (MB) after normalization and architecture default.</summary>
    [JsonIgnore] public int EffectiveIndexQueryMemoryBudgetMB => NormalizeIndexQueryMemoryBudgetMB(IndexQueryMemoryBudgetMB);
    /// <summary>Effective disk quota (MB) after normalization and architecture default.</summary>
    [JsonIgnore] public int EffectiveIndexMaxDiskSizeMB => NormalizeIndexMaxDiskSizeMB(IndexMaxDiskSizeMB);
    /// <summary>Effective "stop indexing when the drive is this percent full" limit after normalization.</summary>
    [JsonIgnore] public int EffectiveIndexMaxDiskUsagePercent => NormalizeIndexMaxDiskUsagePercent(IndexMaxDiskUsagePercent);
    /// <summary>Effective build memory budget (MB) after normalization and architecture default.</summary>
    [JsonIgnore] public int EffectiveIndexBuildMemoryBudgetMB => NormalizeIndexBuildMemoryBudgetMB(IndexBuildMemoryBudgetMB);
    /// <summary>True when a search is allowed to use the index this session: the master feature is on
    /// and the persisted default opts in (plan §6.1). Per-search overrides apply on top of this.</summary>
    [JsonIgnore] public bool ContentIndexActiveByDefault => EnableContentIndex && UseContentIndexByDefault;

    /// <summary>
    /// Applies the approved indexing profile when a user opts into drive indexing from the one-time
    /// first-launch prompt. Storage location, registered roots, and per-root filters are intentionally left
    /// alone because the onboarding flow resolves those separately. Existing users are never migrated to
    /// this profile.
    /// </summary>
    public void ApplyFirstRunDriveIndexingProfile()
    {
        EnableContentIndex = true;
        UseContentIndexByDefault = true;
        IndexAccelerateLiterals = true;
        IndexAccelerateWholeWord = true;
        IndexAccelerateRegex = true;
        IndexAccelerateMultiline = true;
        IndexUseNativeWorker = true;
        IndexBuildPdfTextExtendedSource = false;
        IndexBuildImageTextExtendedSource = false;
        IndexProduceV3QueryStructures = true;
        IndexUseV3QueryReader = false;
        IndexUseWorkerQuerySessions = true;
        IndexMappedWorkerDefaultsMigrated = true;
        IndexQueryStartupBudgetMs = DefaultIndexQueryStartupBudgetMs;
        IndexMaxCandidatePercent = DefaultIndexMaxCandidatePercent;
        IndexQueryMemoryBudgetMB = DefaultIndexQueryMemoryBudgetMB;
        IndexQueryWorkerParallelism = DefaultIndexQueryWorkerParallelism;
        IndexMaxInProcessSizeMB = DefaultIndexMaxInProcessSizeMB;
        IndexMaxWorkerQuerySizeMB = DefaultIndexMaxWorkerQuerySizeMB;
        IndexMaxFileSizeMB = DefaultIndexMaxFileSizeMB;
        IndexMaxDiskSizeMB = DefaultIndexMaxDiskSizeMB;
        IndexSizeDefaultsMigrated = true;
        IndexMinimumFreeSpaceMB = DefaultIndexMinimumFreeSpaceMB;
        IndexMaxDiskUsagePercent = DefaultIndexMaxDiskUsagePercent;
        IndexRetainedGenerationCount = DefaultIndexRetainedGenerationCount;
        IndexStaleTemporaryHours = DefaultIndexStaleTemporaryHours;
        IndexQuarantineRetentionDays = DefaultIndexQuarantineRetentionDays;
        IndexBuildTrigger = IndexBuildTriggerContinuous;
        IndexUpdateMode = IndexUpdateModeAutomaticIncremental;
        IndexIdleDelayMinutes = DefaultIndexIdleDelayMinutes;
        IndexContinuousIntervalMinutes = DefaultIndexContinuousIntervalMinutes;
        IndexContinuousIntervalMigrated = true;
        IndexOneMinuteContinuousIntervalMigrated = true;
        IndexScheduleMode = DefaultIndexScheduleMode;
        IndexScheduleIntervalMinutes = DefaultIndexScheduleIntervalMinutes;
        IndexScheduleDaysOfWeekMask = DefaultIndexScheduleDaysOfWeekMask;
        IndexScheduleTimeOfDay = DefaultIndexScheduleTimeOfDay;
        IndexBuildMemoryBudgetMB = DefaultIndexBuildMemoryBudgetMB;
        IndexBuildWorkerParallelism = DefaultIndexBuildWorkerParallelism;
        IndexPauseDuringForegroundSearch = true;
        IndexPauseOnBattery = true;
        IndexRemovableDrivePolicy = DefaultIndexRemovableDrivePolicy;
        IndexFollowReparsePoints = false;
        IndexIncludeHiddenFiles = true;
        IndexMaxJournalCatchupMB = DefaultIndexMaxJournalCatchupMB;
        IndexMaxJournalCatchupRecords = FirstRunDriveIndexMaxJournalCatchupRecords;
        IndexPostBuildCatchUpThresholdChanges = DefaultIndexPostBuildCatchUpThresholdChanges;
        IndexAutoRepair = true;
        IndexUseWatcherHints = false;
        IndexMaxDeltaSegments = DefaultIndexMaxDeltaSegments;
        IndexCompactionThresholdMB = DefaultIndexCompactionThresholdMB;
        IndexMaxAutoCompactionSizeMB = DefaultIndexMaxAutoCompactionSizeMB;
        IndexSizeManagementMode = Yagu.Services.Index.IndexSizeManagementModes.CoalesceThenCompact;
        IndexCoalesceMaxSegmentMB = DefaultIndexCoalesceMaxSegmentMB;
        IndexCoalesceMaxBatchMB = DefaultIndexCoalesceMaxBatchMB;
        IndexCoalesceMinRun = DefaultIndexCoalesceMinRun;
        IndexCoalesceMaxRunsPerPass = DefaultIndexCoalesceMaxRunsPerPass;
        ShareAggregateIndexTelemetry = false;
        ShowIndexStatusInMainWindow = true;
        ShowIndexBuildNotifications = true;
        ShowIndexProvenanceInResults = true;
        IndexExcludedGlobs = string.Empty;
        IndexExcludedExtensions = string.Empty;
    }

    /// <summary>True once the first-run telemetry/bug-reporting consent dialog has been shown. The
    /// dialog records the user's choices below and is then never shown again (regardless of what they
    /// chose). Default false.</summary>
    public bool TelemetryConsentPromptShown { get; set; }
    /// <summary>User consent for the SILENT, anonymized performance + error telemetry channel. Default
    /// false (opt-in). When true and an endpoint is configured, Yagu sends scrubbed crash/error
    /// summaries and timings (never paths, queries, or file contents).</summary>
    public bool TelemetryEnabled { get; set; }
    /// <summary>User consent for the bug-report flow: when a critical error occurs, Yagu offers a
    /// dialog showing exactly what would be submitted (stack trace, GPU/NPU, settings file, log) and
    /// only sends it if the user clicks Submit. Independent of <see cref="TelemetryEnabled"/>. Default
    /// false (opt-in).</summary>
    public bool BugReportingEnabled { get; set; }
    /// <summary>Optional contact email the user supplies so we can follow up on a bug report.
    /// Remembered to pre-fill the bug-report dialog. Empty by default.</summary>
    public string BugReportContactEmail { get; set; } = string.Empty;
    /// <summary>Random, non-PII identifier generated once per install (GUID "N" form). Lets telemetry
    /// count distinct installs without identifying the user or machine. Empty until first generated.</summary>
    public string TelemetryInstallId { get; set; } = string.Empty;
    /// <summary>When the directory is left empty ("search all drives"), include ready network/mapped drives. Default false (can be slow/metered).</summary>
    public bool SearchAllDrivesIncludesNetwork { get; set; }
    /// <summary>When the directory is left empty ("search all drives"), include ready removable/USB drives. Default false.</summary>
    public bool SearchAllDrivesIncludesRemovable { get; set; }
    /// <summary>When the directory is left empty ("search all drives"), include detected cloud-backed drives (e.g. Google Drive). Default false (can trigger downloads).</summary>
    public bool SearchAllDrivesIncludesCloud { get; set; }
    /// <summary>When searching all drives, bypass the Everything index and walk every drive with the built-in scanner. Default false. Slower, but guarantees completeness on drives whose Everything index is partial (e.g. folders excluded in Everything's settings).</summary>
    public bool SearchAllDrivesForceFullScan { get; set; }
    /// <summary>When true, detect ZIP archives by file header and search text files inside them. Default true.</summary>
    [JsonIgnore] public bool SearchInsideArchives { get; set; }
    /// <summary>Semicolon-separated file extensions that are known ZIP-like containers (bypassed from skip-extensions when archive search is on). e.g. "zip;jar;docx;xlsx".</summary>
    public string ArchiveExtensions { get; set; } = DefaultArchiveExtensions;
    /// <summary>Semicolon-separated file extensions to skip entirely (no binary check, no content read).</summary>
    public string SkipExtensions { get; set; } = DefaultSkipExtensions;
    /// <summary>Semicolon-separated known binary/media/data extensions that are skipped by extension prefilter.</summary>
    public string BinaryExtensions { get; set; } = DefaultBinaryExtensions;
    /// <summary>When true, do not show the non-admin access warning banner on startup.</summary>
    public bool SuppressAdminWarning { get; set; }
    /// <summary>When true, do not prompt to start Everything Search on startup when it is installed but not running.</summary>
    public bool SuppressEverythingNotRunningPrompt { get; set; }
    /// <summary>When true, do not warn before a search whose root drive/folder is not covered by
    /// Everything's configured volume/folder indexes.</summary>
    public bool SuppressEverythingIndexCoverageWarning { get; set; }
    /// <summary>When true, do not warn before a search pauses an in-progress content-index warm-up.
    /// Resettable from Settings > Developer Options > Reminders and Warnings.</summary>
    public bool SuppressIndexWarmSearchWarning { get; set; }
    /// <summary>When true, do not warn before searching when the query names a file whose extension is
    /// currently excluded by Skip/Binary extensions or an Include/Exclude filter.</summary>
    public bool SuppressExcludedExtensionWarnings { get; set; }
    /// <summary>The default action taken for an excluded file type when the warning is suppressed
    /// (<see cref="SuppressExcludedExtensionWarnings"/> is true): true = automatically include the type in
    /// the search (as if "Include &amp; search" were clicked each time); false = search without it. Only
    /// meaningful when the warning is suppressed; ignored while the warning is shown. Defaults to false so
    /// existing "don't warn" users keep the previous behavior (searching without the excluded type).</summary>
    public bool IncludeExcludedExtensionByDefault { get; set; }
    /// <summary>When true, do not show theme/font contrast warnings.</summary>
    public bool SuppressFontContrastWarnings { get; set; }
    /// <summary>UTC time before which theme/font contrast warnings are snoozed.</summary>
    public DateTimeOffset? FontContrastReminderAfterUtc { get; set; }
    /// <summary>When true (default) and the process is not elevated, file listing skips well-known admin-protected paths (System Volume Information, $Recycle.Bin, Windows\System32\config, etc.) to speed up search.</summary>
    public bool ExcludeAdminProtectedPaths { get; set; } = true;
    /// <summary>Semicolon- or newline-separated list of path segments (e.g. <c>\Windows\System32\config</c>) treated as admin-protected. Used only when <see cref="ExcludeAdminProtectedPaths"/> is true and the process is not elevated. Empty falls back to the built-in defaults.</summary>
    public string AdminProtectedPathSegments { get; set; } = DefaultAdminProtectedPathSegments;
    public const string DefaultAdminProtectedPathSegments =
        @"\Windows\System32\config;" +
        @"\Windows\System32\LogFiles\WMI;" +
        @"\Windows\System32\Microsoft\Protect;" +
        @"\Windows\System32\sru;" +
        @"\Windows\CSC;" +
        @"\Windows\Installer;" +
        @"\Windows\ServiceProfiles;" +
        @"\Windows\security;" +
        @"\Windows\Minidump;" +
        @"\Windows\appcompat\Programs\Install;" +
        @"\Windows\PrintService;" +
        @"\Windows\WaaS;" +
        @"\Windows\ModemLogs;" +
        @"\System Volume Information;" +
        @"\$Recycle.Bin;" +
        @"\Recovery;" +
        @"\Config.Msi";
    /// <summary>Whether the first-run experience has been completed (context menu prompt, etc.).</summary>
    public bool HasCompletedFirstRun { get; set; }
    /// <summary>Whether the one-time "add a folder to the content index" onboarding prompt has been shown.</summary>
    public bool HasPromptedIndexOnboarding { get; set; }
    /// <summary>Whether the one-time first-launch "choose your window style" (launcher vs traditional) prompt has been shown.</summary>
    public bool HasPromptedWindowMode { get; set; }
    /// <summary>Whether the first file-drawer introductory tooltip has been shown.</summary>
    public bool HasShownFileDrawerIntroTip { get; set; }
    /// <summary>Whether the first expanded-drawer line-number introductory tooltip has been shown.</summary>
    public bool HasShownFileDrawerLineNumberIntroTip { get; set; }
    /// <summary>Whether the first preview-match introductory tooltip has been shown.</summary>
    public bool HasShownPreviewMatchIntroTip { get; set; }
    /// <summary>When true, do not show the "another instance is already running" dialog on startup.</summary>
    public bool SuppressMultiInstanceWarning { get; set; }
    /// <summary>When true (default), force disk-intensive content-scan, OCR-process, and content-index
    /// worker parallelism to one for HDD roots. Independent of the separately suppressible HDD warning.</summary>
    public bool LimitParallelismOnHdd { get; set; } = true;
    /// <summary>When true, do not show the HDD parallelism warning dialog before searching an HDD. Does NOT affect whether parallelism is limited (see <see cref="LimitParallelismOnHdd"/>).</summary>
    public bool SuppressHddParallelismWarnings { get; set; }
    /// <summary>When true, back up the file to .yagubak before saving in the built-in editor. Default true.</summary>
    public bool BackupBeforeSave { get; set; } = true;
    /// <summary>When true, show a brief confirmation overlay after the built-in editor successfully saves. Default true.</summary>
    public bool ShowEditorSavedOverlay { get; set; } = true;
    /// <summary>When true (default), the built-in editor applies syntax coloring based on the file name/extension.</summary>
    public bool EditorSyntaxHighlightingEnabled { get; set; } = true;
    /// <summary>When true (default), Yagu starts in the compact launcher window (single search bar,
    /// no results pane). When false, Yagu starts in the traditional full window with title bar and
    /// results pane visible.</summary>
    public bool StartInLauncherMode { get; set; } = true;
    /// <summary>Migration marker: once true, legacy installs have been split between
    /// <see cref="StartInLauncherMode"/> and <see cref="WindowFocusBehavior"/> (which previously
    /// conflated the two concepts via the deprecated value 3 = Traditional window).</summary>
    public bool StartInLauncherModeMigrated { get; set; }
    /// <summary>What the compact launcher does when it loses focus.
    /// 0 = Minimize to system tray, 1 = Stay open (default), 2 = Always on top.
    /// Value 3 (Traditional window startup) is deprecated — use <see cref="StartInLauncherMode"/>
    /// instead.</summary>
    public int WindowFocusBehavior { get; set; } = 1; // 1 = Stay open (default)
    /// <summary>Migration marker: once true, the user's <see cref="WindowFocusBehavior"/> has been
    /// rebased onto a modern default at least once. Kept for backwards compatibility with installs
    /// migrated by an earlier build; the new migration uses <see cref="StartInLauncherModeMigrated"/>.</summary>
    public bool WindowFocusBehaviorMigratedFromLegacyDefault { get; set; }
    /// <summary>One-time migration guard: existing installs persisted the old 5000 per-line-match default.
    /// On load it is flipped to 0 (unlimited) exactly once so those installs stop capping giant single
    /// lines at 5000; a value the user deliberately sets afterward is preserved.</summary>
    public bool MaxMatchesPerLineMigratedToUnlimited { get; set; }
    /// <summary>When true (default), closing the window docks to system tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Whether the user has been informed that closing docks to the system tray.</summary>
    public bool HasShownCloseToTrayNotification { get; set; }
    /// <summary>When true, maximize the window on startup. Default false.</summary>
    public bool MaximizeOnStartup { get; set; }
    /// <summary>Where the traditional (non-launcher) window is placed on screen at launch.
    /// 0 = Centered (default), 1 = Top Left, 2 = Top Middle, 3 = Top Right, 4 = Middle Left,
    /// 5 = Middle Right, 6 = Bottom Left, 7 = Bottom Middle, 8 = Bottom Right. Ignored when
    /// <see cref="MaximizeOnStartup"/> is set or while in the compact launcher (which always docks top-center).</summary>
    public int LaunchWindowPosition { get; set; }
    /// <summary>Where the compact launcher window is placed on screen at launch. Same anchor indices
    /// as <see cref="LaunchWindowPosition"/> (0 = Centered .. 8 = Bottom Right) but defaults to
    /// 2 = Top Middle, matching the launcher's classic Spotlight-style top-center dock.</summary>
    public int LauncherWindowPosition { get; set; } = 2;
    /// <summary>Legacy Advanced Options width setting. Retained for settings-file compatibility; the drawer now always uses the query-box width.</summary>
    public int AdvancedOptionsCollapsedWidthModeIndex { get; set; }
    /// <summary>Optional default working directory for the embedded terminal. Empty uses the Yagu launch directory.</summary>
    public string TerminalDefaultWorkingDirectory { get; set; } = string.Empty;
    /// <summary>Which shell backs the embedded terminal: 0 = Command Prompt (cmd.exe), 1 = PowerShell (default).</summary>
    public int TerminalShellKindIndex { get; set; } = 1;
    /// <summary>When true (default), the on-device semantic model is unloaded from memory (freeing GPU
    /// VRAM) immediately after each AI-search translation finishes; the next query reloads it. Set false
    /// to keep the model resident for the fastest repeat queries at the cost of held VRAM.</summary>
    public bool SemanticUnloadModelAfterUse { get; set; } = true;
    /// <summary>When true (default), checking a file header checkbox immediately adds it to the preview pane.</summary>
    public bool FileHeaderCheckAddsToPreview { get; set; } = true;
    /// <summary>When true (default), checking a match line checkbox immediately adds it to the preview pane.</summary>
    public bool MatchLineCheckAddsToPreview { get; set; } = true;
    /// <summary>Number of matches to auto-load when user scrolls to the end of a truncated section. 0 = disabled.</summary>
    public int PreviewAutoLoadMatches { get; set; } = 50;
    /// <summary>Built-in editor: maximum file size in MB. Files larger than this are blocked from opening.</summary>
    public int PreviewEditorMaxSizeMB { get; set; } = 32;
    /// <summary>Built-in editor: maximum total character count. Files with more characters are blocked.</summary>
    public int PreviewEditorMaxTextLength { get; set; } = 20_000_000;
    /// <summary>Built-in editor: maximum single-line length in characters. Files with a line longer than this are blocked.</summary>
    public int PreviewEditorMaxLineLength { get; set; } = 1_000_000;
    /// <summary>Pop-out editor/preview window: maximum file size in MB that can be popped out into its own
    /// window. Popping out loads the WHOLE file (not chunked), so this guards against loading an
    /// unreasonably large file. Files above this are blocked from pop-out.</summary>
    public int PreviewEditorPopOutMaxSizeMB { get; set; } = 100;
    /// <summary>How multiple pop-out editor/preview windows auto-arrange on screen.
    /// 0=Grid, 1=Columns, 2=Rows, 3=Cascade, 4=Manual (no auto-arrange).</summary>
    public int PreviewEditorPopOutArrangementIndex { get; set; }
    /// <summary>Content-search file size ceiling in MB. Files larger than this are skipped when no explicit max-size filter is set. 0 = no ceiling.</summary>
    public int ContentSearchFileSizeMB { get; set; } = 100;
    /// <summary>Absolute ceiling for max results regardless of user settings. Must be > 0.</summary>
    public int MaxResultsCeiling { get; set; } = 50_000;
    /// <summary>Maximum concurrent memory-mapped file views during search. 0 = default (16).</summary>
    public int MmfConcurrencyLimit { get; set; }
    /// <summary>Maximum concurrent native (Rust) scans. 0 = default (min(64, ProcessorCount×2)).</summary>
    public int NativeConcurrencyLimit { get; set; }

    /// <summary>Max matches to render per file section before truncating with overflow. 0 = 500 (default).</summary>
    public int MaxMatchesPerSection { get; set; }

    /// <summary>Max file sections to render per page. 0 = 50 (default).</summary>
    public int PreviewSectionPageSize { get; set; }

    /// <summary>Max checked files prepared for one multi-file preview. 0 = 1000 (default). A very large
    /// checked selection is capped to this many files so the flat WinUI preview surface stays responsive;
    /// the status bar reports when the selection was truncated.</summary>
    public int MaxSelectedFilesPerPreview { get; set; }

    /// <summary>Max match-result references prepared for one multi-file preview. 0 = 100000 (default).
    /// Prevents a few match-dense files from recreating millions of result stubs just to build a preview.</summary>
    public int MaxSelectedResultsPerPreview { get; set; }

    /// <summary>Absolute ceiling on how many matches a single preview section renders across ALL overflow
    /// expansions before it stops and directs you to the built-in editor. 0 = 4000 (default). Guards the
    /// WinUI text layout against a fail-fast on an unbounded RichTextBlock.</summary>
    public int MaxRenderedMatchesPerSection { get; set; }

    /// <summary>Max file size (MB) for full-file preview mode. 0 = 1024 (1 GB default).</summary>
    public int FullFilePreviewLimitMB { get; set; }

    /// <summary>Max lines the read-only full-file preview renders before a truncation notice. 0 = 20000
    /// (default). The built-in editor still opens the whole file (chunked); this only bounds the non-
    /// virtualized preview render so a large file cannot pin the UI thread.</summary>
    public int FullFilePreviewMaxRenderLines { get; set; }

    /// <summary>Max characters the read-only full-file preview renders before a truncation notice. 0 =
    /// 1000000 (default). Also caps a single pathologically long line.</summary>
    public int FullFilePreviewMaxRenderChars { get; set; }

    /// <summary>Max nesting depth when searching inside nested archives. 0 = 5 (default).</summary>
    public int ArchiveMaxNestingDepth { get; set; }

    /// <summary>Max individual entry size (MB) inside archives. 0 = 64 (default).</summary>
    public int ArchiveMaxEntryMB { get; set; }

    public const int MaxRecent = 20; // kept for backward compat; prefer MaxRecentItems

    // ── Semantic search (Foundry Local) settings ──
    /// <summary>When true (default), the Semantic query mode is offered in the UI and the
    /// CLI <c>--semantic-pattern</c> flag is honored. The local model is never downloaded
    /// until the first semantic search is actually run.</summary>
    public bool SemanticSearchEnabled { get; set; } = true;
    /// <summary>Optional Foundry Local model alias override. When empty, Yagu picks the
    /// smallest capable instruct model available for the current hardware.</summary>
    public string SemanticModelAlias { get; set; } = string.Empty;
    /// <summary>True once a semantic model has been downloaded at least once. Lets the UI skip the
    /// first-run model-download prompt on subsequent switches into Semantic mode.</summary>
    public bool SemanticModelDownloaded { get; set; }
    /// <summary>Persisted UI state: whether the search bar was last in Semantic mode.</summary>
    public bool LastQueryModeIsSemantic { get; set; }
    /// <summary>True once the user has explicitly picked a query mode (via the search-button chevron
    /// or the Settings override). Until then, the launch mode follows the hardware-based default
    /// (Semantic when a GPU/NPU accelerator is present, otherwise Traditional).</summary>
    public bool HasChosenQueryMode { get; set; }
    /// <summary>User override of the hardware-based default: when true, Yagu defaults to Traditional
    /// mode even on machines whose GPU/NPU could run Semantic search. Only meaningful (and editable)
    /// when an accelerator is present; ignored on machines that fall back to Traditional anyway.</summary>
    public bool DefaultToTraditionalSearchMode { get; set; }
    /// <summary>Preferred execution-device order for choosing which accelerator build of the AI model
    /// to run, as a comma-separated subset/order of GPU/NPU/CPU. Default "GPU,NPU,CPU". Invalid values
    /// fall back to the default order when parsed.</summary>
    public string SemanticDevicePreferenceOrder { get; set; } = "GPU,NPU,CPU";

    // ── First-run AI-model qualification ──
    /// <summary>True once the first-run AI-model qualification sweep has finished (whether or not any
    /// model cleared the bar, and regardless of whether the user accepted the suggestion). Gates the
    /// one-time first-run model check so it is not re-offered on every switch into Semantic mode.</summary>
    public bool SemanticModelQualificationCompleted { get; set; }
    /// <summary>The model alias the first-run qualification sweep recommended (the first candidate that
    /// cleared the accuracy/latency bar, or the best-effort fallback when none did). Empty until the
    /// sweep runs. Informational: the effective model is still <see cref="SemanticModelAlias"/>, which
    /// the first-run flow sets to this value when the user accepts the suggestion.</summary>
    public string SemanticQualifiedModelAlias { get; set; } = string.Empty;

    // ── Application update checks ──
    /// <summary>Legacy per-launch consent flag from builds before <see cref="AppUpdateCheckMode"/>. Kept
    /// for one-time migration only (a persisted opt-out maps to <see cref="Yagu.Services.AppUpdateCheckMode.Off"/>).</summary>
    public bool AppUpdateChecksEnabled { get; set; } = true;
    /// <summary>How Yagu checks GitHub for a newer version. Defaults to Prompt so a fresh install asks
    /// once (never every launch) before any network request is made.</summary>
    public AppUpdateCheckMode AppUpdateCheckMode { get; set; } = AppUpdateCheckMode.Prompt;
    /// <summary>UTC of the last GitHub release metadata check attempt (throttles automatic checks).</summary>
    public DateTimeOffset? LastAppUpdateCheckUtc { get; set; }
    /// <summary>Release version the user last skipped/acknowledged, so automatic checks don't renotify it.</summary>
    public string LastAppUpdateAlertedVersion { get; set; } = string.Empty;

    // ── Notifications ──
    /// <summary>Master switch for user-facing notifications. Default true.</summary>
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Show a native Windows notification after a successful search. Default true.</summary>
    public bool NotifySearchCompleted { get; set; } = true;
    /// <summary>Show a native Windows notification after the user cancels a search. Default true.</summary>
    public bool NotifySearchCancelled { get; set; } = true;
    /// <summary>Surface automatic application-update notices. Manual checks are unaffected. Default true.</summary>
    public bool NotifyApplicationUpdates { get; set; } = true;

    // ── Foundry model update alerts ──
    /// <summary>When true (default), Yagu checks the Foundry Local catalog about once a day and shows a
    /// one-time modal when a new, updated, or variant text-chat model becomes available. Only runs for
    /// users who have already used semantic search (so it never triggers a model/EP download by itself).</summary>
    public bool FoundryModelUpdateAlertsEnabled { get; set; } = true;
    /// <summary>Variant ids of the text-chat models seen at the last catalog check — the baseline used
    /// to detect newcomers. Empty until the first check seeds it silently.</summary>
    public List<string> KnownFoundryModelIds { get; set; } = [];
    /// <summary>UTC of the last successful Foundry catalog check (throttles checks to about once a day).</summary>
    public DateTimeOffset? LastFoundryModelCheckUtc { get; set; }
    /// <summary>UTC of the last time the new-model alert modal was shown (diagnostic/throttle aid).</summary>
    public DateTimeOffset? LastFoundryModelAlertUtc { get; set; }

    /// <summary>True once Yagu has shown the first-run "AI search will run on the CPU" warning (no
    /// GPU/NPU detected). Set when the warning modal is displayed — regardless of the user's choice —
    /// so it appears at most once.</summary>
    public bool CpuSemanticWarningShown { get; set; }

    /// <summary>True once the user ticked "Don't remind me again" on the prompt that offers to switch a
    /// natural-language Traditional query to AI (Semantic) search. When set, that suggestion modal is
    /// never shown again, regardless of whether the user accepted or declined the switch that time.</summary>
    public bool SemanticSuggestionDismissed { get; set; }

    /// <summary>True once the user ticked "Don't warn me again" on the prompt that appears when a
    /// Traditional query contains a literal "\n" escape while Multiline search is off, asking whether to
    /// switch to Multiline (and therefore Regex). When set, that prompt is never shown again, regardless
    /// of the choice made that time. Resettable from Settings → Developer Options → Reminders and
    /// Warnings.</summary>
    public bool MultilineNewlineSuggestionDismissed { get; set; }

    /// <summary>Catalog variant ids (or aliases, as a fallback when no variant id is known) for which
    /// the user ticked "Don't show this warning again for this model" on the slow-AI-interpretation
    /// prompt. The warning that offers a smaller/faster model after a long interpretation is suppressed
    /// permanently for exactly these variants. Keyed per variant so a faster build of the same family
    /// is unaffected.</summary>
    public List<string> SuppressedSlowSemanticModelKeys { get; set; } = [];

    /// <summary>Optional per-model overrides of the six Foundry Local text-generation (sampling)
    /// parameters — Temperature, TopP, MaxTokens, RandomSeed, FrequencyPenalty, PresencePenalty. Keyed by
    /// model alias (e.g. <c>phi-4-mini-reasoning</c>) or catalog variant id (matched id-first, then alias,
    /// case-insensitive). Each entry's fields are individually nullable: a null field keeps Yagu's
    /// built-in default for that model class (reasoning vs. instruct), a non-null field replaces it. Empty
    /// by default, so every model uses the tuned built-in defaults until a power user overrides one here.
    /// Editable only via this settings file (no in-app editor); applied live to the translator and the
    /// out-of-process semantic worker on startup and when settings reload.</summary>
    public Dictionary<string, Ai.SemanticModelGenerationOverride> SemanticModelParameterOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SettingsService
{
    private readonly string _path;

    public SettingsService() : this(ResolveInstanceSettingsPath()) { }

    public SettingsService(string path) { _path = path; }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yagu", "settings.json");

    /// <summary>Redirects the settings file away from the real user profile. Tests and CI MUST set this
    /// so a run can never modify the user's own configuration.</summary>
    internal const string SettingsFileOverrideEnvVar = "YAGU_SETTINGS_FILE";

    /// <summary>Path used by the parameterless constructor: the override when set, else
    /// <see cref="DefaultPath"/>, which stays pure so it always reports the real user location.</summary>
    internal static string ResolveInstanceSettingsPath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(SettingsFileOverrideEnvVar);
        return string.IsNullOrWhiteSpace(overridePath) ? DefaultPath() : overridePath;
    }

    // The pre-"unlimited by default" backstop value. A persisted AbsoluteMaxResults equal to this exact
    // legacy default is migrated to 0 (disabled) on load so existing installs stop truncating results.
    private const int LegacyDefaultAbsoluteMaxResults = 2_000_000;

    // The pre-"unlimited by default" per-line match cap. A persisted MaxMatchesPerLine equal to this
    // exact legacy default is migrated ONCE to 0 (unlimited) on load (guarded by
    // MaxMatchesPerLineMigratedToUnlimited) so existing installs stop capping giant single lines at 5000.
    private const int LegacyDefaultMaxMatchesPerLine = 5_000;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return CreateDefaultSettings();
            using var fs = File.OpenRead(_path);
            var settings = JsonSerializer.Deserialize(fs, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            // Migrate: old default was int.MaxValue which caused unbounded memory growth.
            if (settings.MaxResults > SearchOptions.MaxResultsCeiling)
                settings.MaxResults = SearchOptions.MaxResultsCeiling;
            if (settings.MaxMatchesPerLine < 0)
                settings.MaxMatchesPerLine = 0;
            else if (!settings.MaxMatchesPerLineMigratedToUnlimited && settings.MaxMatchesPerLine == LegacyDefaultMaxMatchesPerLine)
                settings.MaxMatchesPerLine = 0; // one-time flip of the old 5000 default to unlimited
            settings.MaxMatchesPerLineMigratedToUnlimited = true;
            if (settings.AbsoluteMaxResults < 0)
                settings.AbsoluteMaxResults = 0;
            // Unlimited-by-default: migrate the exact legacy 2,000,000 backstop to 0 (disabled) so
            // existing installs stop truncating large result sets. A deliberately-set value is kept.
            else if (settings.AbsoluteMaxResults == LegacyDefaultAbsoluteMaxResults)
                settings.AbsoluteMaxResults = 0;
            if (settings.SkipExtensions is null)
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.ArchiveExtensions is null)
                settings.ArchiveExtensions = AppSettings.DefaultArchiveExtensions;
            if (IsLegacyDefaultSkipExtensions(settings.SkipExtensions))
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.BinaryExtensions is null)
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
            else if (IsLegacyExpandedBinaryPrefilter(settings.BinaryExtensions))
            {
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
                settings.SkipExtensions = MergeExtensionLists(settings.SkipExtensions, AppSettings.DefaultSkipExtensions);
            }
            MigrateLegacyPreviewGutterColors(settings);
            MigrateLegacyWindowFocusBehavior(settings);
            MigrateLegacyAppUpdateChecks(settings);
            MigrateIndexMappedWorkerDefaults(settings);
            NormalizeFilterModeSettings(settings);
            NormalizeThemeSettings(settings);
            NormalizePreviewTextFontSettings(settings);
            NormalizePreviewEditorFontSettings(settings);
            NormalizeResultListMatchTextSettings(settings);
            NormalizePreviewShowMoreSettings(settings);
            settings.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(settings.ImageOcrEngine);
            settings.ImageOcrModel = AppSettings.NormalizeImageOcrModel(settings.ImageOcrModel);
            settings.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(settings.ImageOcrMaxSide);
            settings.ImageOcrWorkerParallelism = AppSettings.NormalizeImageOcrWorkerParallelism(settings.ImageOcrWorkerParallelism);
            NormalizeIndexSettings(settings);
            settings.LowDiskSpaceWarningPercent = AppSettings.NormalizeLowDiskSpaceWarningPercent(settings.LowDiskSpaceWarningPercent);
            settings.TerminalDefaultWorkingDirectory ??= string.Empty;
            settings.TerminalShellKindIndex = TerminalShell.NormalizeSettingsIndex(settings.TerminalShellKindIndex);
            settings.BugReportContactEmail ??= string.Empty;
            settings.TelemetryInstallId ??= string.Empty;
            return settings;
        }
        catch (Exception ex) { YaguLog.For("Settings").LogWarning(ex, "Failed to load settings from {Path}", _path); return CreateDefaultSettings(); }
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return CreateDefaultSettings();
            await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync(fs, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
            if (settings.MaxResults > SearchOptions.MaxResultsCeiling)
                settings.MaxResults = SearchOptions.MaxResultsCeiling;
            if (settings.AbsoluteMaxResults < 0)
                settings.AbsoluteMaxResults = 0;
            // Unlimited-by-default: migrate the exact legacy 2,000,000 backstop to 0 (disabled).
            else if (settings.AbsoluteMaxResults == LegacyDefaultAbsoluteMaxResults)
                settings.AbsoluteMaxResults = 0;
            if (settings.SkipExtensions is null)
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.ArchiveExtensions is null)
                settings.ArchiveExtensions = AppSettings.DefaultArchiveExtensions;
            if (IsLegacyDefaultSkipExtensions(settings.SkipExtensions))
                settings.SkipExtensions = AppSettings.DefaultSkipExtensions;
            if (settings.BinaryExtensions is null)
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
            else if (IsLegacyExpandedBinaryPrefilter(settings.BinaryExtensions))
            {
                settings.BinaryExtensions = AppSettings.DefaultBinaryExtensions;
                settings.SkipExtensions = MergeExtensionLists(settings.SkipExtensions, AppSettings.DefaultSkipExtensions);
            }
            MigrateLegacyPreviewGutterColors(settings);
            MigrateLegacyWindowFocusBehavior(settings);
            MigrateLegacyAppUpdateChecks(settings);
            MigrateIndexMappedWorkerDefaults(settings);
            NormalizeFilterModeSettings(settings);
            NormalizeThemeSettings(settings);
            NormalizePreviewTextFontSettings(settings);
            NormalizePreviewEditorFontSettings(settings);
            NormalizeResultListMatchTextSettings(settings);
            NormalizePreviewShowMoreSettings(settings);
            settings.ImageOcrEngine = AppSettings.NormalizeImageOcrEngine(settings.ImageOcrEngine);
            settings.ImageOcrModel = AppSettings.NormalizeImageOcrModel(settings.ImageOcrModel);
            settings.ImageOcrMaxSide = AppSettings.NormalizeImageOcrMaxSide(settings.ImageOcrMaxSide);
            settings.ImageOcrWorkerParallelism = AppSettings.NormalizeImageOcrWorkerParallelism(settings.ImageOcrWorkerParallelism);
            NormalizeIndexSettings(settings);
            settings.TerminalDefaultWorkingDirectory ??= string.Empty;
            settings.TerminalShellKindIndex = TerminalShell.NormalizeSettingsIndex(settings.TerminalShellKindIndex);
            settings.BugReportContactEmail ??= string.Empty;
            settings.TelemetryInstallId ??= string.Empty;
            return settings;
        }
        catch (Exception ex) { YaguLog.For("Settings").LogWarning(ex, "Failed to load settings from {Path}", _path); return CreateDefaultSettings(); }
    }

    private static AppSettings CreateDefaultSettings()
        => new()
        {
            IndexContinuousIntervalMigrated = true,
            IndexOneMinuteContinuousIntervalMigrated = true,
            IndexSizeDefaultsMigrated = true,
        };

    // Earlier Yagu versions stored the startup window mode (launcher vs traditional) and the
    // launcher's focus-loss behavior in a single setting (WindowFocusBehavior 0..3 where 3 meant
    // "Traditional window"). We've since split those into StartInLauncherMode + WindowFocusBehavior
    // (0=MinimizeToTray, 1=StayOpen, 2=AlwaysOnTop). Migrate legacy installs once.
    private static void MigrateLegacyAppUpdateChecks(AppSettings settings)
    {
        // Builds before AppUpdateCheckMode used a per-launch consent bool. A persisted opt-out becomes
        // Off; everyone else stays at the Prompt default so they get the improved one-time consent once.
        if (settings.AppUpdateCheckMode == AppUpdateCheckMode.Prompt && !settings.AppUpdateChecksEnabled)
            settings.AppUpdateCheckMode = AppUpdateCheckMode.Off;
    }

    private static void MigrateIndexMappedWorkerDefaults(AppSettings settings)
    {
        if (settings.IndexMappedWorkerDefaultsMigrated)
            return;

        if (!settings.IndexProduceV3QueryStructures && !settings.IndexUseWorkerQuerySessions)
        {
            settings.IndexProduceV3QueryStructures = true;
            settings.IndexUseWorkerQuerySessions = true;
        }

        settings.IndexMappedWorkerDefaultsMigrated = true;
    }

    private static void MigrateLegacyWindowFocusBehavior(AppSettings settings)
    {
        if (settings.StartInLauncherModeMigrated) return;

        switch (settings.WindowFocusBehavior)
        {
            case 3:
                // Legacy "Traditional window" → start in traditional window, stay-open in launcher when invoked manually.
                settings.StartInLauncherMode = false;
                settings.WindowFocusBehavior = 1;
                break;
            case 0 when !settings.WindowFocusBehaviorMigratedFromLegacyDefault:
                // Original Yagu default (Minimize to tray) — flip to the new Stay-open default.
                settings.WindowFocusBehavior = 1;
                settings.StartInLauncherMode = true;
                break;
            default:
                // 1 (StayOpen) and 2 (AlwaysOnTop) are still valid; keep them.
                if (settings.WindowFocusBehavior < 0 || settings.WindowFocusBehavior > 2)
                    settings.WindowFocusBehavior = 1;
                break;
        }

        settings.StartInLauncherModeMigrated = true;
        settings.WindowFocusBehaviorMigratedFromLegacyDefault = true;
    }

    private static void NormalizeFilterModeSettings(AppSettings settings)
    {
        settings.IncludeFilterModeIndex = settings.IncludeFilterModeIndex == 1 ? 1 : 0;
        settings.ExcludeFilterModeIndex = settings.ExcludeFilterModeIndex == 1 ? 1 : 0;
        settings.IncludeGlobs ??= string.Empty;
        settings.ExcludeGlobs ??= AppSettings.DefaultExcludeGlobs;
    }

    private static void NormalizeThemeSettings(AppSettings settings)
    {
        settings.ThemeModeIndex = settings.ThemeModeIndex is >= 0 and <= 2 ? settings.ThemeModeIndex : 0;
    }

    private static void NormalizePreviewTextFontSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PreviewTextFontFamily))
            settings.PreviewTextFontFamily = AppSettings.DefaultPreviewTextFontFamily;

        settings.PreviewTextFontSize = Math.Clamp(
            settings.PreviewTextFontSize <= 0 ? AppSettings.DefaultPreviewTextFontSize : settings.PreviewTextFontSize,
            6,
            72);
    }

    private static void NormalizePreviewEditorFontSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PreviewEditorFontFamily))
            settings.PreviewEditorFontFamily = AppSettings.DefaultPreviewEditorFontFamily;

        settings.PreviewEditorFontSize = Math.Clamp(
            settings.PreviewEditorFontSize <= 0 ? AppSettings.DefaultPreviewEditorFontSize : settings.PreviewEditorFontSize,
            6,
            72);
    }

    private static void NormalizeResultListMatchTextSettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ResultListMatchTextFontFamily))
            settings.ResultListMatchTextFontFamily = AppSettings.DefaultResultListMatchTextFontFamily;

        settings.ResultListMatchTextFontSize = Math.Clamp(
            settings.ResultListMatchTextFontSize <= 0 ? AppSettings.DefaultResultListMatchTextFontSize : settings.ResultListMatchTextFontSize,
            6,
            72);

        settings.ResultListMatchHighlightColor = NormalizeArgbHexString(
            settings.ResultListMatchHighlightColor,
            AppSettings.DefaultResultListMatchHighlightColor);
    }

    private static void NormalizePreviewShowMoreSettings(AppSettings settings)
    {
        settings.PreviewShowMoreEllipsisColor = NormalizeArgbHexString(
            settings.PreviewShowMoreEllipsisColor,
            AppSettings.DefaultPreviewShowMoreEllipsisColor);

        settings.PreviewShowMoreEllipsisFontSize = Math.Clamp(
            settings.PreviewShowMoreEllipsisFontSize <= 0 ? AppSettings.DefaultPreviewShowMoreEllipsisFontSize : settings.PreviewShowMoreEllipsisFontSize,
            6,
            72);
    }

    /// <summary>
    /// Lifts the index size-management defaults once. Each of these was materialized into settings.json on
    /// first run, so raising the C# default alone would never reach an existing install. Only a value still
    /// sitting on its legacy default moves; a deliberate choice (including 0 = no ceiling) is preserved.
    /// Together they were unworkable for a whole-drive index: it exceeded the budget immediately, and the
    /// coalescing caps sat below typical segment size, so nothing could reclaim it.
    /// </summary>
    private static void MigrateIndexSizeDefaults(AppSettings settings)
    {
        if (settings.IndexSizeDefaultsMigrated)
            return;

        if (settings.IndexMaxDiskSizeMB == AppSettings.LegacyDefaultIndexMaxDiskSizeMB)
            settings.IndexMaxDiskSizeMB = AppSettings.DefaultIndexMaxDiskSizeMB;
        if (settings.IndexCoalesceMaxSegmentMB == AppSettings.LegacyDefaultIndexCoalesceMaxSegmentMB)
            settings.IndexCoalesceMaxSegmentMB = AppSettings.DefaultIndexCoalesceMaxSegmentMB;
        if (settings.IndexCoalesceMaxBatchMB == AppSettings.LegacyDefaultIndexCoalesceMaxBatchMB)
            settings.IndexCoalesceMaxBatchMB = AppSettings.DefaultIndexCoalesceMaxBatchMB;

        settings.IndexSizeDefaultsMigrated = true;
    }

    /// <summary>Normalizes/validates every persisted content-index setting (plan §6.1). Bounded numeric
    /// values are clamped, enums coerced to known values, and strings trimmed. A zero (unset) numeric
    /// value falls back to its architecture-aware default. Kept in one place so Settings and the CLI
    /// <c>--index-config</c> surface share the same validation.</summary>
    private static void NormalizeIndexSettings(AppSettings settings)
    {
        settings.IndexQueryStartupBudgetMs = AppSettings.NormalizeIndexQueryStartupBudgetMs(settings.IndexQueryStartupBudgetMs);
        settings.IndexMaxCandidatePercent = AppSettings.NormalizeIndexMaxCandidatePercent(settings.IndexMaxCandidatePercent);
        settings.IndexQueryMemoryBudgetMB = AppSettings.NormalizeIndexQueryMemoryBudgetMB(settings.IndexQueryMemoryBudgetMB);
        settings.IndexQueryWorkerParallelism = AppSettings.NormalizeIndexQueryWorkerParallelism(settings.IndexQueryWorkerParallelism);
        settings.IndexMaxInProcessSizeMB = AppSettings.NormalizeIndexMaxInProcessSizeMB(settings.IndexMaxInProcessSizeMB);
        settings.IndexMaxFileSizeMB = AppSettings.NormalizeIndexMaxFileSizeMB(settings.IndexMaxFileSizeMB);
        MigrateIndexSizeDefaults(settings);
        settings.IndexMaxDiskSizeMB = AppSettings.NormalizeIndexMaxDiskSizeMB(settings.IndexMaxDiskSizeMB);
        settings.IndexMinimumFreeSpaceMB = AppSettings.NormalizeIndexMinimumFreeSpaceMB(settings.IndexMinimumFreeSpaceMB);
        settings.IndexMaxDiskUsagePercent = AppSettings.NormalizeIndexMaxDiskUsagePercent(settings.IndexMaxDiskUsagePercent);
        settings.IndexRetainedGenerationCount = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount);
        settings.IndexStaleTemporaryHours = AppSettings.NormalizeIndexStaleTemporaryHours(settings.IndexStaleTemporaryHours);
        settings.IndexQuarantineRetentionDays = AppSettings.NormalizeIndexQuarantineRetentionDays(settings.IndexQuarantineRetentionDays);
        // Builds before the cadence split stored both meanings in IndexIdleDelayMinutes. Copy that value once;
        // settings saved by this build carry the guard and preserve independently chosen values thereafter.
        if (!settings.IndexContinuousIntervalMigrated)
        {
            settings.IndexContinuousIntervalMinutes = AppSettings.NormalizeIndexContinuousIntervalMinutes(
                settings.IndexIdleDelayMinutes);
            settings.IndexContinuousIntervalMigrated = true;
        }
        if (!settings.IndexOneMinuteContinuousIntervalMigrated)
        {
            if (settings.IndexContinuousIntervalMinutes == AppSettings.FirstRunDriveIndexContinuousIntervalMinutes)
                settings.IndexContinuousIntervalMinutes = AppSettings.DefaultIndexContinuousIntervalMinutes;
            settings.IndexOneMinuteContinuousIntervalMigrated = true;
        }
        settings.IndexIdleDelayMinutes = AppSettings.NormalizeIndexIdleDelayMinutes(settings.IndexIdleDelayMinutes);
        settings.IndexContinuousIntervalMinutes = AppSettings.NormalizeIndexContinuousIntervalMinutes(
            settings.IndexContinuousIntervalMinutes);
        settings.IndexBuildMemoryBudgetMB = AppSettings.NormalizeIndexBuildMemoryBudgetMB(settings.IndexBuildMemoryBudgetMB);
        settings.IndexBuildWorkerParallelism = AppSettings.NormalizeIndexBuildWorkerParallelism(settings.IndexBuildWorkerParallelism);
        settings.IndexMaxJournalCatchupMB = AppSettings.NormalizeIndexMaxJournalCatchupMB(settings.IndexMaxJournalCatchupMB);
        settings.IndexMaxJournalCatchupRecords = AppSettings.NormalizeIndexMaxJournalCatchupRecords(settings.IndexMaxJournalCatchupRecords);
        settings.IndexPostBuildCatchUpThresholdChanges = AppSettings.NormalizeIndexPostBuildCatchUpThresholdChanges(
            settings.IndexPostBuildCatchUpThresholdChanges);
        settings.FileIoTimeoutSeconds = AppSettings.NormalizeFileIoTimeoutSeconds(settings.FileIoTimeoutSeconds);
        settings.IndexMaxDeltaSegments = AppSettings.NormalizeIndexMaxDeltaSegments(settings.IndexMaxDeltaSegments);
        settings.IndexCompactionThresholdMB = AppSettings.NormalizeIndexCompactionThresholdMB(settings.IndexCompactionThresholdMB);
        settings.IndexMaxAutoCompactionSizeMB = AppSettings.NormalizeIndexMaxAutoCompactionSizeMB(settings.IndexMaxAutoCompactionSizeMB);
        settings.IndexBuildTrigger = AppSettings.NormalizeIndexBuildTrigger(settings.IndexBuildTrigger);
        settings.IndexScheduleMode = AppSettings.NormalizeIndexScheduleMode(settings.IndexScheduleMode);
        settings.IndexScheduleIntervalMinutes = AppSettings.NormalizeIndexScheduleIntervalMinutes(settings.IndexScheduleIntervalMinutes);
        settings.IndexScheduleDaysOfWeekMask = AppSettings.NormalizeIndexScheduleDaysOfWeekMask(settings.IndexScheduleDaysOfWeekMask);
        settings.IndexScheduleTimeOfDay = AppSettings.NormalizeIndexScheduleTimeOfDay(settings.IndexScheduleTimeOfDay);
        settings.IndexUpdateMode = AppSettings.NormalizeIndexUpdateMode(settings.IndexUpdateMode);
        settings.IndexRemovableDrivePolicy = AppSettings.NormalizeIndexRemovableDrivePolicy(settings.IndexRemovableDrivePolicy);
        settings.IndexStorageDirectory = AppSettings.NormalizeIndexStorageDirectory(settings.IndexStorageDirectory);
        settings.IndexExcludedGlobs ??= string.Empty;
        settings.IndexExcludedExtensions ??= string.Empty;
        settings.IndexedRoots = Yagu.Services.Index.IndexedRootsPolicy.Normalize(settings.IndexedRoots);
        settings.ContentIndexLiveScanWarningDismissedRoots =
            AppSettings.NormalizeContentIndexLiveScanWarningDismissedRoots(
                settings.ContentIndexLiveScanWarningDismissedRoots);
        settings.IndexedRootFilters = Yagu.Services.Index.IndexedRootFilterPolicy.Normalize(settings.IndexedRootFilters);
        settings.IndexedRootSizePolicies = Yagu.Services.Index.IndexSizeManagementPolicy.Normalize(settings.IndexedRootSizePolicies);
        settings.IndexSizeManagementMode = Yagu.Services.Index.IndexSizeManagementPolicy.NormalizeMode(settings.IndexSizeManagementMode);
        settings.IndexCoalesceMaxSegmentMB = AppSettings.NormalizeIndexCoalesceMaxSegmentMB(settings.IndexCoalesceMaxSegmentMB);
        settings.IndexCoalesceMaxBatchMB = AppSettings.NormalizeIndexCoalesceMaxBatchMB(settings.IndexCoalesceMaxBatchMB);
        settings.IndexCoalesceMinRun = AppSettings.NormalizeIndexCoalesceMinRun(settings.IndexCoalesceMinRun);
        settings.IndexCoalesceMaxRunsPerPass = AppSettings.NormalizeIndexCoalesceMaxRunsPerPass(settings.IndexCoalesceMaxRunsPerPass);
    }

    private static string NormalizeArgbHexString(string? value, string fallback)
    {
        if (TryParseArgbHex(value, out var color))
            return FormatArgbHex(color);

        return TryParseArgbHex(fallback, out var fallbackColor)
            ? FormatArgbHex(fallbackColor)
            : AppSettings.DefaultPreviewMatchTextColor;
    }

    private static bool TryParseArgbHex(string? value, out uint color)
    {
        color = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = 0xFF000000 | rgb;
            return true;
        }

        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            color = argb;
            return true;
        }

        return false;
    }

    private static string FormatArgbHex(uint color)
        => "#" + color.ToString("X8", CultureInfo.InvariantCulture);

    private static bool IsLegacyDefaultSkipExtensions(string skipExtensions) =>
        string.Equals(skipExtensions, AppSettings.LegacyDefaultSkipExtensions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(skipExtensions, AppSettings.LegacyExpandedBinaryPrefilterExtensions, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(skipExtensions, AppSettings.DefaultBinaryExtensions, StringComparison.OrdinalIgnoreCase);

    private static void MigrateLegacyPreviewGutterColors(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PreviewGutterContextColor)
            || string.Equals(settings.PreviewGutterContextColor, AppSettings.LegacyDefaultPreviewGutterContextColor, StringComparison.OrdinalIgnoreCase))
        {
            settings.PreviewGutterContextColor = AppSettings.DefaultPreviewGutterContextColor;
        }

        if (string.IsNullOrWhiteSpace(settings.PreviewGutterMatchColor)
            || string.Equals(settings.PreviewGutterMatchColor, AppSettings.LegacyDefaultPreviewGutterMatchColor, StringComparison.OrdinalIgnoreCase))
        {
            settings.PreviewGutterMatchColor = AppSettings.DefaultPreviewGutterMatchColor;
        }

        if (string.IsNullOrWhiteSpace(settings.PreviewEditorGutterColor))
            settings.PreviewEditorGutterColor = AppSettings.DefaultPreviewEditorGutterColor;

        settings.PreviewEditorTextColor = NormalizeEditorTextColor(settings.PreviewEditorTextColor);
    }

    // Editor body-text color uses an empty string as an "Auto" sentinel meaning "follow the app/system
    // theme". A non-empty value is normalized to canonical ARGB hex; null/whitespace/invalid collapse to Auto.
    private static string NormalizeEditorTextColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AppSettings.DefaultPreviewEditorTextColor;

        return TryParseArgbHex(value, out var color)
            ? FormatArgbHex(color)
            : AppSettings.DefaultPreviewEditorTextColor;
    }

    private static bool IsLegacyExpandedBinaryPrefilter(string binaryExtensions) =>
        string.Equals(binaryExtensions, AppSettings.LegacyExpandedBinaryPrefilterExtensions, StringComparison.OrdinalIgnoreCase);

    private static string MergeExtensionLists(string first, string second)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        AddExtensions(first, seen, merged);
        AddExtensions(second, seen, merged);
        return string.Join(';', merged);
    }

    private static void AddExtensions(string value, HashSet<string> seen, List<string> target)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var extension in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = extension.TrimStart('.', '*');
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                target.Add(normalized);
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            settings.IndexContinuousIntervalMigrated = true;
            settings.IndexOneMinuteContinuousIntervalMigrated = true;
            settings.IndexSizeDefaultsMigrated = true;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write to a temp file then atomically replace, so a concurrent reader (e.g. the bug
            // report) never sees a half-written file and a crash mid-save can't corrupt settings.json.
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(fs, settings, AppSettingsJsonContext.Default.AppSettings);
                CommitTempFile(tmp, _path);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort cleanup */ } }
            }
        }
        catch (Exception ex) { YaguLog.For("Settings").LogWarning(ex, "Failed to save settings to {Path}", _path); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            settings.IndexContinuousIntervalMigrated = true;
            settings.IndexOneMinuteContinuousIntervalMigrated = true;
            settings.IndexSizeDefaultsMigrated = true;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write to a temp file then atomically replace, so a concurrent reader (e.g. the bug
            // report) never sees a half-written file and a crash mid-save can't corrupt settings.json.
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                    await JsonSerializer.SerializeAsync(fs, settings, AppSettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
                await CommitTempFileAsync(tmp, _path).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort cleanup */ } }
            }
        }
        catch (Exception ex) { YaguLog.For("Settings").LogWarning(ex, "Failed to save settings to {Path}", _path); }
    }

    /// <summary>Attempts the temp-file replace once; returns false for a transient lock so the caller can retry.</summary>
    internal static bool TryCommitTempFile(string tmp, string path, bool isLastAttempt, out Exception? error)
    {
        try
        {
            // A leftover read-only/hidden destination makes File.Move throw UnauthorizedAccessException
            // no matter how long we wait, so clear those attributes before the final attempt.
            if (isLastAttempt && File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                    File.SetAttributes(path, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden));
            }
            File.Move(tmp, path, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex) when (!isLastAttempt && ex is UnauthorizedAccessException or IOException)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>Backoff (ms) before retry <paramref name="attempt"/> (1-based) of the atomic replace.</summary>
    internal static int CommitRetryDelayMs(int attempt) => attempt * 25;

    internal const int CommitAttempts = 5;

    // Antivirus scanners and concurrent readers (the bug-report snapshot reads settings.json) can hold
    // the destination open for a few milliseconds, which surfaces as UnauthorizedAccessException from
    // the atomic replace and silently loses the save. Retry briefly instead of giving up immediately.
    private static void CommitTempFile(string tmp, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (TryCommitTempFile(tmp, path, attempt >= CommitAttempts, out _)) return;
            Thread.Sleep(CommitRetryDelayMs(attempt));
        }
    }

    private static async Task CommitTempFileAsync(string tmp, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (TryCommitTempFile(tmp, path, attempt >= CommitAttempts, out _)) return;
            await Task.Delay(CommitRetryDelayMs(attempt)).ConfigureAwait(false);
        }
    }

    public static void PushRecent(List<string> list, string value, int max = AppSettings.MaxRecent)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        list.RemoveAll(s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        while (list.Count > max) list.RemoveAt(list.Count - 1);
    }

    /// <summary>
    /// Pushes <paramref name="value"/> to the front of <paramref name="list"/> and records its
    /// last-used time in <paramref name="times"/>, keeping the two in sync. Re-using an existing entry
    /// moves it to the front and refreshes its timestamp rather than adding a duplicate; trimming the
    /// list past <paramref name="max"/> also drops the corresponding timestamps.
    /// </summary>
    public static void PushRecent(List<string> list, Dictionary<string, DateTimeOffset> times, string value, int max = AppSettings.MaxRecent)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        // Remove any case-insensitive duplicate from both the list and the timestamp map.
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                times.Remove(list[i]);
                list.RemoveAt(i);
            }
        }

        list.Insert(0, value);
        times[value] = DateTimeOffset.Now;

        while (list.Count > max)
        {
            string removed = list[^1];
            list.RemoveAt(list.Count - 1);
            times.Remove(removed);
        }
    }
}
