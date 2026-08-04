using System.Collections.ObjectModel;
using Yagu.Services;
using Yagu.Services.Ai;

namespace Yagu.ViewModels;

/// <summary>
/// The skip / binary / archive extension pickers: the checkbox item collections shown in Settings,
/// their category grouping and summaries, two-way sync with the comma-separated setting strings,
/// and the glob-driven auto-enablement of archive and binary search.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Observable collection of skip-extension items for the multi-select dropdown.</summary>
    public ObservableCollection<SkipExtensionItem> SkipExtensionItems { get; } = [];

    /// <summary>Summary label for the skip-extensions dropdown button.</summary>
    public string SkipExtensionsSummary
    {
        get
        {
            int enabled = SkipExtensionItems.Count(i => i.IsEnabled);
            int total = SkipExtensionItems.Count;
            return total == 0 ? "Skip Extensions: none" : $"Skip Extensions: {enabled}/{total}";
        }
    }

    private static readonly Dictionary<string, string> ExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Binaries / Build
        ["exe"] = "Binaries", ["dll"] = "Binaries", ["pdb"] = "Binaries", ["obj"] = "Binaries",
        ["lib"] = "Binaries", ["so"] = "Binaries", ["dylib"] = "Binaries",
        ["com"] = "Binaries", ["scr"] = "Binaries", ["sys"] = "Binaries", ["drv"] = "Binaries",
        ["ocx"] = "Binaries", ["cpl"] = "Binaries", ["mui"] = "Binaries", ["winmd"] = "Binaries",
        ["pri"] = "Binaries", ["cat"] = "Binaries", ["res"] = "Binaries", ["resources"] = "Binaries",
        ["o"] = "Binaries", ["a"] = "Binaries", ["lo"] = "Binaries", ["la"] = "Binaries",
        ["ilk"] = "Binaries", ["iobj"] = "Binaries", ["ipdb"] = "Binaries", ["exp"] = "Binaries",
        ["pyc"] = "Binaries", ["pyo"] = "Binaries", ["class"] = "Binaries", ["dex"] = "Binaries",
        ["wasm"] = "Binaries",
        // Data / Dumps
        ["bin"] = "Data", ["dat"] = "Data", ["db"] = "Data", ["db3"] = "Data",
        ["sqlite"] = "Data", ["sqlite3"] = "Data", ["edb"] = "Data", ["mdb"] = "Data",
        ["accdb"] = "Data", ["ldb"] = "Data", ["sdf"] = "Data", ["cache"] = "Data",
        ["tmp"] = "Data", ["bak"] = "Data", ["etl"] = "Data", ["evtx"] = "Data",
        ["dmp"] = "Data", ["mdmp"] = "Data", ["hdmp"] = "Data", ["hprof"] = "Data",
        ["vhd"] = "Data", ["vhdx"] = "Data", ["vmdk"] = "Data", ["pak"] = "Data",
        ["usm"] = "Data", ["bundle"] = "Data", ["assets"] = "Data",
        // Images
        ["png"] = "Images", ["jpg"] = "Images", ["jpeg"] = "Images", ["gif"] = "Images",
        ["bmp"] = "Images", ["ico"] = "Images", ["tif"] = "Images", ["tiff"] = "Images",
        ["webp"] = "Images", ["svg"] = "Images", ["heic"] = "Images", ["heif"] = "Images",
        ["avif"] = "Images",
        // Audio / Video
        ["mp3"] = "Media", ["mp4"] = "Media", ["avi"] = "Media", ["mov"] = "Media",
        ["wmv"] = "Media", ["flv"] = "Media", ["mkv"] = "Media", ["wav"] = "Media",
        ["ogg"] = "Media", ["flac"] = "Media", ["m4a"] = "Media", ["webm"] = "Media",
        // Fonts
        ["woff"] = "Fonts", ["woff2"] = "Fonts", ["ttf"] = "Fonts", ["eot"] = "Fonts", ["otf"] = "Fonts",
        // Documents
        ["pdf"] = "Documents", ["doc"] = "Documents",
        ["xls"] = "Documents", ["ppt"] = "Documents",
    };

    private static string CategorizeExtension(string ext) =>
        ExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";

    private bool _suppressSkipExtensionSync;
    private bool _updatingSkipExtensionsFromItems;

    partial void OnSkipExtensionsChanged(string value)
    {
        if (_suppressSkipExtensionSync || _updatingSkipExtensionsFromItems) return;
        SyncSkipExtensionItems();
    }

    partial void OnSettingsSkipExtensionsChanged(string value)
    {
        if (_suppressSkipExtensionSync || _updatingSkipExtensionsFromItems) return;
        SyncSkipExtensionItems();
    }

    /// <summary>Rebuild the <see cref="SkipExtensionItems"/> collection from the current <see cref="SkipExtensions"/> string.</summary>
    public void SyncSkipExtensionItems()
    {
        _suppressSkipExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(SkipExtensions);
            var available = ParseExtensionSet(SettingsSkipExtensions);
            foreach (var ext in enabled)
                available.Add(ext);

            SkipExtensionItems.Clear();

            var groups = available
                .GroupBy(CategorizeExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    SkipExtensionItems.Add(new SkipExtensionItem(ext, group.Key, enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(SkipExtensionsSummary));
        }
        finally
        {
            _suppressSkipExtensionSync = false;
        }
    }

    /// <summary>Called when a skip-extension item is toggled. Rebuilds the string and persists.</summary>
    public void OnSkipExtensionToggled()
    {
        if (_suppressSkipExtensionSync) return;
        var enabled = SkipExtensionItems.Where(i => i.IsEnabled).Select(i => i.Extension);
        _updatingSkipExtensionsFromItems = true;
        try
        {
            SkipExtensions = string.Join(';', enabled);
        }
        finally
        {
            _updatingSkipExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(SkipExtensionsSummary));
    }

    // ── Binary extensions dropdown ───────────────────────────────

    public ObservableCollection<SkipExtensionItem> BinaryExtensionItems { get; } = [];

    public string BinaryExtensionsSummary
    {
        get
        {
            int enabled = BinaryExtensionItems.Count(i => i.IsEnabled);
            int total = BinaryExtensionItems.Count;
            return total == 0 ? "Binary ext: none" : $"Binary ext: {enabled}/{total}";
        }
    }

    public Microsoft.UI.Xaml.Visibility BinaryExtensionsVisibility => SearchBinary
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    private bool _suppressBinaryExtensionSync;
    private bool _updatingBinaryExtensionsFromItems;
    private bool _binaryExtensionsInitialized;

    partial void OnBinaryExtensionsChanged(string value)
    {
        if (_suppressBinaryExtensionSync || _updatingBinaryExtensionsFromItems) return;
        SyncBinaryExtensionItems();
    }

    partial void OnSettingsBinaryExtensionsChanged(string value)
    {
        if (_suppressBinaryExtensionSync || _updatingBinaryExtensionsFromItems) return;
        SyncBinaryExtensionItems();
    }

    public void SyncBinaryExtensionItems()
    {
        _suppressBinaryExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(BinaryExtensions);
            var available = ParseExtensionSet(SettingsBinaryExtensions);
            foreach (var ext in enabled)
                available.Add(ext);

            BinaryExtensionItems.Clear();

            var groups = available
                .GroupBy(CategorizeExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    // "checked = search this binary type": an item is selected when it is NOT in the
                    // skip list. (Internally BinaryExtensions stays the skip list, so the search engine,
                    // CLI generator, and excluded-extension predictor are unaffected.)
                    BinaryExtensionItems.Add(new SkipExtensionItem(ext, group.Key, !enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(BinaryExtensionsSummary));
        }
        finally
        {
            _suppressBinaryExtensionSync = false;
        }
    }

    public void OnBinaryExtensionToggled()
    {
        if (_suppressBinaryExtensionSync) return;

        _updatingBinaryExtensionsFromItems = true;
        try
        {
            // Selected items are the binary types to SEARCH; the skip list is the UNSELECTED ones.
            BinaryExtensions = string.Join(';', BinaryExtensionItems.Where(i => !i.IsEnabled).Select(i => i.Extension));
        }
        finally
        {
            _updatingBinaryExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(BinaryExtensionsSummary));
    }

    // ── Archive (ZIP-like) extensions dropdown ────────────────────

    /// <summary>Observable collection of archive-extension items for the multi-select dropdown.</summary>
    public ObservableCollection<SkipExtensionItem> ArchiveExtensionItems { get; } = [];

    /// <summary>Summary label for the archive-extensions dropdown button.</summary>
    public string ArchiveExtensionsSummary
    {
        get
        {
            int enabled = ArchiveExtensionItems.Count(i => i.IsEnabled);
            int total = ArchiveExtensionItems.Count;
            return total == 0 ? "Archive ext: none" : $"Archive ext: {enabled}/{total}";
        }
    }

    public Microsoft.UI.Xaml.Visibility ArchiveExtensionsVisibility => SearchInsideArchives
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    private static readonly Dictionary<string, string> ArchiveExtensionCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = "Archives", ["jar"] = "Java", ["war"] = "Java", ["ear"] = "Java",
        ["nupkg"] = ".NET", ["vsix"] = ".NET",
        ["apk"] = "Android", ["aab"] = "Android", ["aar"] = "Android",
        ["appx"] = "Windows", ["msix"] = "Windows", ["appxbundle"] = "Windows", ["msixbundle"] = "Windows",
        ["docx"] = "Office", ["xlsx"] = "Office", ["pptx"] = "Office",
        ["odt"] = "OpenDoc", ["ods"] = "OpenDoc", ["odp"] = "OpenDoc",
        ["epub"] = "eBooks",
        ["whl"] = "Python",
        ["gz"] = "Compressed", ["tar"] = "Compressed", ["7z"] = "Compressed",
        ["rar"] = "Compressed", ["bz2"] = "Compressed", ["xz"] = "Compressed",
        ["iso"] = "Disk Images", ["cab"] = "Installers", ["msi"] = "Installers",
        ["tgz"] = "Compressed", ["tbz2"] = "Compressed", ["txz"] = "Compressed",
        ["zst"] = "Compressed", ["zstd"] = "Compressed", ["br"] = "Compressed",
        ["lz4"] = "Compressed", ["lzma"] = "Compressed",
    };

    private static string CategorizeArchiveExtension(string ext) =>
        ArchiveExtensionCategories.TryGetValue(ext, out var cat) ? cat : "Other";

    private bool _suppressArchiveExtensionSync;
    private bool _updatingArchiveExtensionsFromItems;

    partial void OnArchiveExtensionsChanged(string value)
    {
        if (_suppressArchiveExtensionSync || _updatingArchiveExtensionsFromItems) return;
        SyncArchiveExtensionItems();
    }

    partial void OnSettingsArchiveExtensionsChanged(string value)
    {
        if (_suppressArchiveExtensionSync || _updatingArchiveExtensionsFromItems) return;
        SyncArchiveExtensionItems();
    }

    /// <summary>Rebuild the <see cref="ArchiveExtensionItems"/> collection from the current <see cref="ArchiveExtensions"/> string.</summary>
    public void SyncArchiveExtensionItems()
    {
        _suppressArchiveExtensionSync = true;
        try
        {
            var enabled = ParseExtensionSet(ArchiveExtensions);
            var available = ParseExtensionSet(SettingsArchiveExtensions);
            foreach (var ext in enabled)
                available.Add(ext);

            ArchiveExtensionItems.Clear();

            var groups = available
                .GroupBy(CategorizeArchiveExtension)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var ext in group.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    ArchiveExtensionItems.Add(new SkipExtensionItem(ext, group.Key, enabled.Contains(ext)));
                }
            }
            OnPropertyChanged(nameof(ArchiveExtensionsSummary));
        }
        finally
        {
            _suppressArchiveExtensionSync = false;
        }
    }

    /// <summary>Called when an archive-extension item is toggled. Removes unchecked items and persists.</summary>
    public void OnArchiveExtensionToggled()
    {
        if (_suppressArchiveExtensionSync) return;

        _updatingArchiveExtensionsFromItems = true;
        try
        {
            ArchiveExtensions = string.Join(';', ArchiveExtensionItems.Where(i => i.IsEnabled).Select(i => i.Extension));
        }
        finally
        {
            _updatingArchiveExtensionsFromItems = false;
        }
        OnPropertyChanged(nameof(ArchiveExtensionsSummary));
    }

    /// <summary>
    /// When a semantic plan filters to archive-container extensions (e.g. .docx/.xlsx/.pptx, which are
    /// ZIP files), turn on "Search archives" and select those extensions in the archive-extensions
    /// list so their inner text is actually searched. Asking to search a container format implies
    /// searching inside it.
    /// </summary>
    private void EnableArchiveSearchForContainerGlobs(IReadOnlyList<string>? includeGlobs)
    {
        var toEnable = SemanticPlanApplier.GetArchiveExtensionsToEnable(
            includeGlobs,
            ArchiveExtensionCategories.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (toEnable.Count == 0)
            return;

        if (!SearchInsideArchives)
            SearchInsideArchives = true;

        var enabled = ParseExtensionSet(ArchiveExtensions);
        bool changed = false;
        foreach (var ext in toEnable)
            if (enabled.Add(ext))
                changed = true;

        if (changed)
            ArchiveExtensions = string.Join(';', enabled);

        SyncArchiveExtensionItems();
    }

    /// <summary>
    /// When a semantic plan explicitly filters to known-binary extensions (e.g. ".com"/".cpl"/".exe"),
    /// make those files findable AND show the intent: enable binary search and SELECT exactly the
    /// targeted binary types in the dropdown (so only they are searched; every other binary type stays
    /// skipped). Internally <see cref="BinaryExtensions"/> is the skip list, so the selection is the
    /// universe minus the targeted extensions. Changes are session-only — visible in Advanced Options
    /// until the next search resets them, and never written to the saved defaults.
    /// </summary>
    private void EnableBinarySearchForBinaryGlobs(IReadOnlyList<string>? includeGlobs)
    {
        var toEnable = SemanticPlanApplier.GetBinaryExtensionsToEnable(
            includeGlobs,
            ParseExtensionSet(AppSettings.DefaultBinaryExtensions));
        if (toEnable.Count == 0)
            return;

        if (!SearchBinary)
            SearchBinary = true;

        // Skip every binary type in the universe EXCEPT the targeted ones, so the dropdown shows only
        // the targeted extension(s) selected (= searched).
        var targeted = new HashSet<string>(toEnable, StringComparer.OrdinalIgnoreCase);
        var universe = ParseExtensionSet(SettingsBinaryExtensions);
        foreach (var ext in ParseExtensionSet(BinaryExtensions))
            universe.Add(ext);
        foreach (var ext in targeted)
            universe.Add(ext);

        var newSkip = string.Join(';', universe.Where(e => !targeted.Contains(e)));
        if (!string.Equals(BinaryExtensions, newSkip, StringComparison.OrdinalIgnoreCase))
        {
            BinaryExtensions = newSkip;
            SyncBinaryExtensionItems();
        }
    }
}
