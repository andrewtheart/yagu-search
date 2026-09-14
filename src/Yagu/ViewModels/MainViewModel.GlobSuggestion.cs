using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.ViewModels;

/// <summary>
/// The one-click recovery offered when a search fails on a query that is really a file glob.
/// </summary>
/// <remarks>
/// A glob is only applied automatically in file-name mode with the regex toggle off, because that is
/// the one combination where matching it as text could not have meant anything. Everywhere else the
/// query is left alone and this offers the fix instead of guessing.
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>The glob a failed query looked like, or null when the failure was an ordinary one.</summary>
    [ObservableProperty] public partial string? GlobFileNameSuggestion { get; set; }

    public bool HasGlobFileNameSuggestion => !string.IsNullOrEmpty(GlobFileNameSuggestion);

    public string GlobFileNameSuggestionLabel =>
        $"Search file names for {GlobFileNameSuggestion}";

    partial void OnGlobFileNameSuggestionChanged(string? value)
    {
        OnPropertyChanged(nameof(HasGlobFileNameSuggestion));
        OnPropertyChanged(nameof(GlobFileNameSuggestionLabel));
    }

    /// <summary>Offers the fix when <paramref name="query"/> was a glob rather than a pattern.</summary>
    /// <remarks>
    /// Suppressed while an include filter is set: the file-name routing this offers stands down in that
    /// case, so the button would promise a search it cannot actually run.
    /// </remarks>
    internal void UpdateGlobFileNameSuggestion(string? query)
    {
        GlobFileNameSuggestion = FileNameGlobQuery.LooksLikeGlob(query) && string.IsNullOrWhiteSpace(IncludeGlobs)
            ? query!.Trim()
            : null;
    }

    /// <summary>Reruns the failed query as the file-name search it was asking for.</summary>
    [RelayCommand]
    private async Task UseGlobAsFileNameSearchAsync()
    {
        // Only the two toggles move. The query text stays as typed and is routed to an include filter
        // at search time, so the box never fills with a synthesized match-all pattern.
        UseRegex = false;
        SearchModeIndex = (int)SearchMode.FileNames;
        GlobFileNameSuggestion = null;

        await SubmitSearchAsync();
    }
}
