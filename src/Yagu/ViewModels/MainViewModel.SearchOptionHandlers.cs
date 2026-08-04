using Yagu.Models;
using Yagu.Services;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;
using YaguLogLevel = Yagu.Services.LogLevel;

namespace Yagu.ViewModels;

/// <summary>
/// Generated property-changed hooks for the search options — sort, group, date-range, glob and
/// file-size inputs, and the log-level / file-lister backend settings — that re-apply, re-sort or
/// persist when the user changes an Advanced Option.
/// </summary>
public sealed partial class MainViewModel
{
    partial void OnSortModeIndexChanged(int value)
    {
        if (_updatingSortCriteria) return;
        SetSingleSortCriterion(value, SortDirectionIndex);
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }

    partial void OnSortDirectionIndexChanged(int value)
    {
        if (_updatingSortCriteria) return;
        SetSingleSortCriterion(SortModeIndex, value);
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }
    partial void OnGroupModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GroupMode));
        OnPropertyChanged(nameof(GroupModeLabel));
        OnPropertyChanged(nameof(GroupSortDirectionLabel));
        ApplySortAndFilter();
    }
    partial void OnGroupSortDirectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(GroupSortDirectionLabel));
        ApplySortAndFilter();
    }
    partial void OnDateRangeFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DateRangeFilter));
        OnPropertyChanged(nameof(DateRangeFilterLabel));
        ApplySortAndFilter();
    }
    partial void OnSearchInsideArchivesChanged(bool value) => OnPropertyChanged(nameof(ArchiveExtensionsVisibility));
    partial void OnIncludeGlobsChanged(string value) => ApplySortAndFilter();
    partial void OnExcludeGlobsChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _clearedDefaultExcludeForRegexMode = false;
        ApplySortAndFilter();
    }
    partial void OnIncludeFilterModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IncludeFilterMode));
        OnPropertyChanged(nameof(IncludeFilterPlaceholder));
        ApplySortAndFilter();
    }
    partial void OnExcludeFilterModeIndexChanged(int value)
    {
        if (ExcludeFilterMode == FilterPatternMode.Regex && IsDefaultExcludeGlobs(ExcludeGlobs))
        {
            _clearedDefaultExcludeForRegexMode = true;
            ExcludeGlobs = string.Empty;
        }
        else if (ExcludeFilterMode == FilterPatternMode.GlobPath
            && _clearedDefaultExcludeForRegexMode
            && string.IsNullOrWhiteSpace(ExcludeGlobs))
        {
            ExcludeGlobs = AppSettings.DefaultExcludeGlobs;
        }

        OnPropertyChanged(nameof(ExcludeFilterMode));
        OnPropertyChanged(nameof(ExcludeFilterPlaceholder));
        ApplySortAndFilter();
    }
    partial void OnMinFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MinFileSizeMB));
    }
    partial void OnMaxFileSizeBytesChanged(long value)
    {
        OnPropertyChanged(nameof(MaxFileSizeMB));
    }
    partial void OnDefaultMinFileSizeBytesChanged(long value) => OnPropertyChanged(nameof(DefaultMinFileSizeMB));
    partial void OnDefaultMaxFileSizeBytesChanged(long value) => OnPropertyChanged(nameof(DefaultMaxFileSizeMB));
    partial void OnFileLogLevelIndexChanged(int value)
    {
        LogService.Instance.FileLevel = (YaguLogLevel)value;
        YaguLog.For("Settings").LogInformation("File log level changed to {Level}", (YaguLogLevel)value);
    }
    partial void OnConsoleLogLevelIndexChanged(int value)
    {
        LogService.Instance.ConsoleLevel = (YaguLogLevel)value;
        YaguLog.For("Settings").LogInformation("Console log level changed to {Level}", (YaguLogLevel)value);
    }

    partial void OnFileListerBackendIndexChanged(int value)
    {
        var backend = (FileListerBackend)value;
        FileLister.Backend = backend;
        YaguLog.For("Settings").LogInformation("FileLister backend set to {Backend}", backend);
    }
}
