using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Models;
using Yagu.Services;
using System.Diagnostics;

namespace Yagu;

public sealed partial class MainWindow
{
    private static readonly char[] CliListSeparators = [',', ';'];
    private bool _includeGeneratedCliCommandSavedSettingOptions;

    private void OnGenerateCliCommandClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Query))
        {
            ShowCliCommandWarning("Specify a search pattern before generating the CLI command.");
            return;
        }

        // The generated command shows in a flyout anchored to the Generate button (inside the
        // Advanced Options drawer), so it materializes right next to the button and the drawer
        // stays open behind it.
        GeneratedCliCommandText.Text = BuildGeneratedCliCommand(_includeGeneratedCliCommandSavedSettingOptions);
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(GenerateCliCommandButton);
    }

    /// <summary>
    /// Surfaces a CLI-command warning as a warning TeachingTip anchored to the search-pattern box
    /// (<c>QueryBox</c>) so it is clearly associated with the empty field the user must fill. The
    /// Advanced Options drawer is left open.
    /// </summary>
    private void ShowCliCommandWarning(string message)
    {
        CliCommandWarningTip.Target = QueryBox;
        CliCommandWarningTip.PreferredPlacement = TeachingTipPlacementMode.Bottom;
        CliCommandWarningTip.Subtitle = message;
        CliCommandWarningTip.IsOpen = true;
    }

    private void OnGeneratedCliCommandSavedSettingOptionsToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
            _includeGeneratedCliCommandSavedSettingOptions = toggle.IsOn;

        GeneratedCliCommandText.Text = BuildGeneratedCliCommand(_includeGeneratedCliCommandSavedSettingOptions);
    }

    private void OnCloseGeneratedCliCommandOverlayClick(object sender, RoutedEventArgs e)
        => GeneratedCliCommandFlyout.Hide();

    private void OnCopyGeneratedCliCommandClick(object sender, RoutedEventArgs e)
    {
        EnsureGeneratedCliCommandText();
        SetClipboardText(GeneratedCliCommandText.Text, "generated CLI command");
        GeneratedCliCommandFlyout.Hide();
    }

    private async void OnSendGeneratedCliCommandToTerminalClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Query))
        {
            GeneratedCliCommandFlyout.Hide();
            ShowCliCommandWarning("Specify a search pattern before sending the CLI command to the terminal.");
            return;
        }

        EnsureGeneratedCliCommandText();
        if (string.IsNullOrWhiteSpace(GeneratedCliCommandText.Text))
            return;

        try
        {
            CollapseAdvancedOptionsForSearch();
            await SendTextToTerminalAsync(GeneratedCliCommandText.Text);
            GeneratedCliCommandFlyout.Hide();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not send generated CLI command to terminal: {ex.Message}";
            LogService.Instance.Warning("Terminal", "Failed to send generated CLI command to terminal", ex);
            Debug.WriteLine($"Failed to send generated CLI command to terminal: {ex}");
        }
    }

    private void EnsureGeneratedCliCommandText()
    {
        if (string.IsNullOrWhiteSpace(GeneratedCliCommandText.Text))
            GeneratedCliCommandText.Text = BuildGeneratedCliCommand(_includeGeneratedCliCommandSavedSettingOptions);
    }

    private string BuildGeneratedCliCommand() => BuildGeneratedCliCommand(includeSavedSettingOptions: false);

    private string BuildGeneratedCliCommand(bool includeSavedSettingOptions)
    {
        AppSettings? settings = includeSavedSettingOptions ? null : new SettingsService().Load();
        var parts = new List<string>
        {
            "Yagu.exe",
            "--cli",
        };

        // An empty directory means "search all drives" — omit --directory so the CLI does the same.
        if (!string.IsNullOrWhiteSpace(ViewModel.Directory))
            AddValue(parts, "--directory", ViewModel.Directory);
        // Semantic mode sends the natural-language query to the on-device model via --semantic-pattern;
        // Traditional mode passes the literal search term via --pattern.
        if (ViewModel.IsSemanticQueryMode)
        {
            AddValue(parts, "--semantic-pattern", ViewModel.Query);
            // A pinned semantic model (chosen/downloaded in Settings) must be reproduced so the CLI
            // uses that exact on-device model instead of auto-selecting one.
            if (!string.IsNullOrWhiteSpace(ViewModel.SemanticModelAlias)
                && ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => string.Equals(ViewModel.SemanticModelAlias, setting.SemanticModelAlias, StringComparison.Ordinal)))
                AddValue(parts, "--semantic-model", ViewModel.SemanticModelAlias);
        }
        else
            AddValue(parts, "--pattern", ViewModel.Query);

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.UseRegex == setting.UseRegex))
            parts.Add(ViewModel.UseRegex ? "--regex" : "--no-regex");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.CaseSensitive == setting.CaseSensitive))
            parts.Add(ViewModel.CaseSensitive ? "--case-sensitive" : "--ignore-case");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.ExactMatch == setting.ExactMatch))
            parts.Add(ViewModel.ExactMatch ? "--exact-match" : "--no-exact-match");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.Multiline == setting.MultilineSearchDefault))
            parts.Add(ViewModel.Multiline ? "--multiline" : "--no-multiline");
        if (ViewModel.Multiline && ViewModel.MultilineDotAll)
            parts.Add("--multiline-dotall");
        // Multiline engine is a global Setting (not a per-search toggle). Emit it only when
        // multiline is on and the non-default (grep) engine is selected.
        if (ViewModel.Multiline && settings.MultilineEngine == 1)
            AddValue(parts, "--multiline-engine", "grep", quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.ContextLines) == Math.Max(0, setting.ContextLines)))
            AddValue(parts, "--context", Math.Max(0, ViewModel.ContextLines).ToString(CultureInfo.InvariantCulture), quote: false);

        AddValue(parts, "--search-mode", FormatSearchMode(ViewModel.SearchModeIndex), quote: false);

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => NormalizeFilterModeIndex(ViewModel.IncludeFilterModeIndex) == NormalizeFilterModeIndex(setting.IncludeFilterModeIndex)))
            parts.Add(ViewModel.IncludeFilterMode == FilterPatternMode.Regex ? "--include-regex" : "--include-glob");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => CliListsEqual(SplitFilterPatternsForCli(ViewModel.IncludeGlobs, ViewModel.IncludeFilterMode), SplitSettingsPatternsForCli(setting.IncludeGlobs))))
        {
            foreach (var pattern in SplitFilterPatternsForCli(ViewModel.IncludeGlobs, ViewModel.IncludeFilterMode))
                AddValue(parts, "--glob", pattern);
        }

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => NormalizeFilterModeIndex(ViewModel.ExcludeFilterModeIndex) == NormalizeFilterModeIndex(setting.ExcludeFilterModeIndex)))
            parts.Add(ViewModel.ExcludeFilterMode == FilterPatternMode.Regex ? "--exclude-regex" : "--exclude-glob-mode");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => CliListsEqual(SplitFilterPatternsForCli(ViewModel.ExcludeGlobs, ViewModel.ExcludeFilterMode), SplitSettingsPatternsForCli(setting.ExcludeGlobs))))
        {
            foreach (var pattern in SplitFilterPatternsForCli(ViewModel.ExcludeGlobs, ViewModel.ExcludeFilterMode))
                AddValue(parts, "--exclude-glob", pattern);
        }

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.ObeyGitignore == setting.ObeyGitignore))
            parts.Add(ViewModel.ObeyGitignore ? "--obey-gitignore" : "--no-obey-gitignore");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.GitignoreTakesPrecedence == setting.GitignoreTakesPrecedence))
            parts.Add(ViewModel.GitignoreTakesPrecedence ? "--gitignore-precedence" : "--no-gitignore-precedence");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.MinFileSizeBytes == setting.DefaultMinFileSizeBytes))
            AddValue(parts, "--min-filesize", FormatFileSizeArgument(ViewModel.MinFileSizeBytes), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.MaxFileSizeBytes == setting.DefaultMaxFileSizeBytes))
            AddValue(parts, "--max-filesize", FormatFileSizeArgument(ViewModel.MaxFileSizeBytes), quote: false);

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => DateArgumentsEqual(ViewModel.CreatedAfterDate, setting.DefaultCreatedAfterDate)))
            AddDateValue(parts, "--created-after", ViewModel.CreatedAfterDate);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => DateArgumentsEqual(ViewModel.CreatedBeforeDate, setting.DefaultCreatedBeforeDate)))
            AddDateValue(parts, "--created-before", ViewModel.CreatedBeforeDate);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => DateArgumentsEqual(ViewModel.ModifiedAfterDate, setting.DefaultModifiedAfterDate)))
            AddDateValue(parts, "--modified-after", ViewModel.ModifiedAfterDate);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => DateArgumentsEqual(ViewModel.ModifiedBeforeDate, setting.DefaultModifiedBeforeDate)))
            AddDateValue(parts, "--modified-before", ViewModel.ModifiedBeforeDate);

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.SkipBinary == setting.SkipBinary))
            parts.Add(ViewModel.SearchBinary ? "--binary" : "--no-binary");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.SearchHiddenFiles == setting.SearchHiddenFiles))
            parts.Add(ViewModel.SearchHiddenFiles ? "--hidden" : "--no-hidden");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.SearchImageText == setting.SearchImageText))
            parts.Add(ViewModel.SearchImageText ? "--image-text" : "--no-image-text");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.SearchPdfText == setting.SearchPdfText))
            parts.Add(ViewModel.SearchPdfText ? "--pdf-text" : "--no-pdf-text");
        // OCR engine / recognition model / detection resolution only affect image-text search, so emit
        // them only when it is on. Each is a persisted Setting, gated so it drops out when it matches
        // the saved settings file (unless "include saved options" is on).
        if (ViewModel.SearchImageText)
        {
            if (!string.IsNullOrWhiteSpace(ViewModel.ImageOcrEngine)
                && ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => string.Equals(ViewModel.ImageOcrEngine, setting.ImageOcrEngine, StringComparison.OrdinalIgnoreCase)))
                AddValue(parts, "--ocr-engine", ViewModel.ImageOcrEngine, quote: false);
            if (!string.IsNullOrWhiteSpace(ViewModel.ImageOcrModel)
                && ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => string.Equals(ViewModel.ImageOcrModel, setting.ImageOcrModel, StringComparison.OrdinalIgnoreCase)))
                AddValue(parts, "--ocr-model", ViewModel.ImageOcrModel, quote: false);
            if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.ImageOcrMaxSide == setting.ImageOcrMaxSide))
                AddValue(parts, "--ocr-max-side", ViewModel.ImageOcrMaxSide.ToString(CultureInfo.InvariantCulture), quote: false);
        }
        var skipExtensions = FormatExtensionList(BuildEffectiveSkipExtensionsForCli());
        if (!string.IsNullOrWhiteSpace(skipExtensions)
            && ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ExtensionSetsEqual(ParseExtensionSetForCli(skipExtensions), ParseExtensionSetForCli(setting.SkipExtensions))))
        {
            AddValue(parts, "--skip-extensions", skipExtensions);
        }

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.SearchInsideArchives == setting.SearchInsideArchives))
            parts.Add(ViewModel.SearchInsideArchives ? "--search-archives" : "--no-search-archives");
        var archiveExtensions = FormatExtensionList(ParseExtensionSetForCli(ViewModel.ArchiveExtensions));
        if (!string.IsNullOrWhiteSpace(archiveExtensions)
            && ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ExtensionSetsEqual(ParseExtensionSetForCli(archiveExtensions), ParseExtensionSetForCli(setting.ArchiveExtensions))))
        {
            AddValue(parts, "--archive-extensions", archiveExtensions);
        }

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.MaxResults) == Math.Max(0, setting.MaxResults)))
            AddValue(parts, "--max-results", Math.Max(0, ViewModel.MaxResults).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.MaxMatchesPerFile) == Math.Max(0, setting.MaxMatchesPerFile)))
            AddValue(parts, "--max-matches-per-file", Math.Max(0, ViewModel.MaxMatchesPerFile).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.MaxMatchesPerLine) == Math.Max(0, setting.MaxMatchesPerLine)))
            AddValue(parts, "--max-matches-per-line", Math.Max(0, ViewModel.MaxMatchesPerLine).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.AbsoluteMaxResults) == Math.Max(0, setting.AbsoluteMaxResults)))
            AddValue(parts, "--absolute-max-results", Math.Max(0, ViewModel.AbsoluteMaxResults).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => FormatMaxDepth(ViewModel.MaxSearchDepth) == Math.Max(0, setting.MaxSearchDepth).ToString(CultureInfo.InvariantCulture)))
            AddValue(parts, "--max-depth", FormatMaxDepth(ViewModel.MaxSearchDepth), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => SearchOptions.ResolveContentSearchParallelism(ViewModel.ParallelismIndex, Environment.ProcessorCount) == SearchOptions.ResolveContentSearchParallelism(setting.ParallelismIndex, Environment.ProcessorCount)))
            AddValue(parts, "--threads", SearchOptions.ResolveContentSearchParallelism(ViewModel.ParallelismIndex, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.MemoryLimitMB) == Math.Max(0, setting.MemoryLimitMB)))
            AddValue(parts, "--memory-limit", Math.Max(0, ViewModel.MemoryLimitMB).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Clamp(ViewModel.MemoryPressurePercent, 0, 100) == Math.Clamp(setting.MemoryPressurePercent, 0, 100)))
            AddValue(parts, "--memory-pressure", Math.Clamp(ViewModel.MemoryPressurePercent, 0, 100).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.SdkChannelBufferSize) == Math.Max(0, setting.SdkChannelBufferSize)))
            AddValue(parts, "--sdk-channel-buffer", Math.Max(0, ViewModel.SdkChannelBufferSize).ToString(CultureInfo.InvariantCulture), quote: false);
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => Math.Max(0, ViewModel.FileListerBackendIndex) == Math.Max(0, setting.FileListerBackendIndex)))
            AddValue(parts, "--file-lister-backend", Math.Max(0, ViewModel.FileListerBackendIndex).ToString(CultureInfo.InvariantCulture), quote: false);

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.ExcludeAdminProtectedPaths == setting.ExcludeAdminProtectedPaths))
            parts.Add(ViewModel.ExcludeAdminProtectedPaths ? "--exclude-admin-paths" : "--no-exclude-admin-paths");
        if (!string.IsNullOrWhiteSpace(ViewModel.AdminProtectedPathSegments)
            && ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => AdminProtectedPathSegmentsEqual(ViewModel.AdminProtectedPathSegments, setting.AdminProtectedPathSegments)))
        {
            AddValue(parts, "--admin-protected-paths", ViewModel.AdminProtectedPathSegments);
        }

        return string.Join(' ', parts);
    }

    private static bool ShouldIncludeSavedSettingOption(bool includeSavedSettingOptions, AppSettings? settings, Func<AppSettings, bool> matchesSettings)
        => includeSavedSettingOptions || settings is null || !matchesSettings(settings);

    private static void AddValue(List<string> parts, string flag, string value, bool quote = true)
    {
        parts.Add(flag);
        parts.Add(quote ? QuoteCliValue(value) : value);
    }

    private static void AddDateValue(List<string> parts, string flag, DateTimeOffset? value)
    {
        if (!value.HasValue)
            return;

        AddValue(parts, flag, FormatDateArgument(value.Value), quote: false);
    }

    private static string FormatDateArgument(DateTimeOffset value)
        => value.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool DateArgumentsEqual(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (!first.HasValue || !second.HasValue)
            return first.HasValue == second.HasValue;

        return string.Equals(FormatDateArgument(first.Value), FormatDateArgument(second.Value), StringComparison.Ordinal);
    }

    private static string FormatSearchMode(int searchModeIndex) => searchModeIndex switch
    {
        1 => "content",
        2 => "filenames",
        3 => "filename-then-content",
        _ => "both",
    };

    private static string FormatMaxDepth(double value)
        => (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            ? "0"
            : ((int)value).ToString(CultureInfo.InvariantCulture);

    private static string FormatFileSizeArgument(long bytes)
    {
        if (bytes <= 0)
            return "0B";

        const long kib = 1024;
        const long mib = kib * 1024;
        const long gib = mib * 1024;

        if (bytes % gib == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / gib}G");
        if (bytes % mib == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / mib}M");
        if (bytes % kib == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / kib}K");

        return string.Create(CultureInfo.InvariantCulture, $"{bytes}B");
    }

    private static List<string> SplitFilterPatternsForCli(string value, FilterPatternMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        if (mode == FilterPatternMode.Regex)
            return [value.Trim()];

        return value
            .Split(CliListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static List<string> SplitSettingsPatternsForCli(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static int NormalizeFilterModeIndex(int value) => value == 1 ? 1 : 0;

    private static bool CliListsEqual(List<string> first, List<string> second)
    {
        if (first.Count != second.Count)
            return false;

        for (int i = 0; i < first.Count; i++)
        {
            if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private HashSet<string> BuildEffectiveSkipExtensionsForCli()
    {
        var effective = ParseExtensionSetForCli(ViewModel.SkipExtensions);
        foreach (var extension in ParseExtensionSetForCli(ViewModel.BinaryExtensions))
            effective.Add(extension);
        return effective;
    }

    private static HashSet<string> ParseExtensionSetForCli(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(
            value.Split(CliListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static item => item.TrimStart('.', '*'))
                .Where(static item => !string.IsNullOrWhiteSpace(item)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool ExtensionSetsEqual(HashSet<string> first, HashSet<string> second)
        => first.SetEquals(second);

    private static bool AdminProtectedPathSegmentsEqual(string? first, string? second)
        => StringSetsEqual(
            FileLister.ParseAdminProtectedSegments(first ?? string.Empty),
            FileLister.ParseAdminProtectedSegments(second ?? string.Empty));

    private static bool StringSetsEqual(IEnumerable<string> first, IEnumerable<string> second)
        => new HashSet<string>(first, StringComparer.OrdinalIgnoreCase)
            .SetEquals(second);

    private static string FormatExtensionList(IEnumerable<string> extensions)
        => string.Join(';', extensions
            .Select(static extension => extension.TrimStart('.', '*'))
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase));

    private static string QuoteCliValue(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
            return "\"\"";

        bool needsQuotes = value[0] == '-' || ContainsCliCharacterRequiringQuotes(value);
        if (!needsQuotes)
            return value;

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        int pendingBackslashes = 0;
        foreach (char current in value)
        {
            if (current == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (current == '"')
            {
                builder.Append('\\', pendingBackslashes * 2 + 1);
                builder.Append('"');
                pendingBackslashes = 0;
                continue;
            }

            if (pendingBackslashes > 0)
            {
                builder.Append('\\', pendingBackslashes);
                pendingBackslashes = 0;
            }
            builder.Append(current);
        }

        if (pendingBackslashes > 0)
            builder.Append('\\', pendingBackslashes * 2);

        builder.Append('"');
        return builder.ToString();
    }

    private static bool ContainsCliCharacterRequiringQuotes(string value)
    {
        foreach (char current in value)
        {
            if (char.IsWhiteSpace(current) || current is '"' or '&' or '|' or '<' or '>' or '^' or ';')
                return true;
        }

        return false;
    }
}
