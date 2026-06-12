using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

public sealed partial class MainWindow
{
    private static readonly char[] CliListSeparators = [',', ';'];
    private bool _includeGeneratedCliCommandSavedSettingOptions;

    private void OnGenerateCliCommandClick(object sender, RoutedEventArgs e)
    {
        if (TryGetGeneratedCliCommandControls(out var commandText, out var commandOverlay))
        {
            commandText.Text = BuildGeneratedCliCommand(_includeGeneratedCliCommandSavedSettingOptions);
            commandOverlay.Visibility = Visibility.Visible;
        }
    }

    private void OnGeneratedCliCommandSavedSettingOptionsToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch toggle)
            _includeGeneratedCliCommandSavedSettingOptions = toggle.IsOn;

        if (TryGetGeneratedCliCommandControls(out var commandText, out var commandOverlay)
            && commandOverlay.Visibility == Visibility.Visible)
        {
            commandText.Text = BuildGeneratedCliCommand(_includeGeneratedCliCommandSavedSettingOptions);
        }
    }

    private void OnCloseGeneratedCliCommandOverlayClick(object sender, RoutedEventArgs e)
        => CloseGeneratedCliCommandOverlay();

    private void OnCopyGeneratedCliCommandClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetGeneratedCliCommandControls(out var commandText, out _))
            return;

        EnsureGeneratedCliCommandText(commandText);

        SetClipboardText(commandText.Text, "generated CLI command");
        CloseGeneratedCliCommandOverlay();
    }

    private async void OnSendGeneratedCliCommandToTerminalClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetGeneratedCliCommandControls(out var commandText, out _))
            return;

        EnsureGeneratedCliCommandText(commandText);
        if (string.IsNullOrWhiteSpace(commandText.Text))
            return;

        CloseGeneratedCliCommandOverlay();
        CollapseAdvancedOptionsForSearch();

        try
        {
            await SendTextToTerminalAsync(commandText.Text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to send generated CLI command to terminal: {ex}");
        }
    }

    private void EnsureGeneratedCliCommandText(TextBlock commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText.Text))
            commandText.Text = BuildGeneratedCliCommand(_includeGeneratedCliCommandSavedSettingOptions);
    }

    private void CloseGeneratedCliCommandOverlay()
    {
        if (FindGeneratedCliCommandElement("GeneratedCliCommandOverlay") is FrameworkElement commandOverlay)
            commandOverlay.Visibility = Visibility.Collapsed;
    }

    private bool TryGetGeneratedCliCommandControls(out TextBlock commandText, out FrameworkElement commandOverlay)
    {
        commandText = FindGeneratedCliCommandElement("GeneratedCliCommandText") as TextBlock ?? null!;
        commandOverlay = FindGeneratedCliCommandElement("GeneratedCliCommandOverlay") as FrameworkElement ?? null!;
        return commandText is not null && commandOverlay is not null;
    }

    private object? FindGeneratedCliCommandElement(string name)
        => (Content as FrameworkElement)?.FindName(name);

    private string BuildGeneratedCliCommand() => BuildGeneratedCliCommand(includeSavedSettingOptions: false);

    private string BuildGeneratedCliCommand(bool includeSavedSettingOptions)
    {
        AppSettings? settings = includeSavedSettingOptions ? null : new SettingsService().Load();
        var parts = new List<string>
        {
            "Yagu.exe",
            "--cli",
        };

        AddValue(parts, "--directory", ViewModel.Directory);
        AddValue(parts, "--pattern", ViewModel.Query);

        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.UseRegex == setting.UseRegex))
            parts.Add(ViewModel.UseRegex ? "--regex" : "--no-regex");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.CaseSensitive == setting.CaseSensitive))
            parts.Add(ViewModel.CaseSensitive ? "--case-sensitive" : "--ignore-case");
        if (ShouldIncludeSavedSettingOption(includeSavedSettingOptions, settings, setting => ViewModel.ExactMatch == setting.ExactMatch))
            parts.Add(ViewModel.ExactMatch ? "--exact-match" : "--no-exact-match");
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