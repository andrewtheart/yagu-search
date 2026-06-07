using System.ComponentModel;

namespace Yagu.Models;

public sealed class ResultGroupHeaderRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public ResultGroupHeaderRow(string key, string title, int fileCount, int matchCount, bool isExpanded)
    {
        Key = key;
        Title = title;
        FileCount = fileCount;
        MatchCount = matchCount;
        _isExpanded = isExpanded;
    }

    public string Key { get; }
    public string Title { get; }
    public int FileCount { get; }
    public int MatchCount { get; }
    public string SummaryText => $"{FileCount:N0} {(FileCount == 1 ? "file" : "files")} | {MatchCount:N0} {(MatchCount == 1 ? "match" : "matches")}";
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}