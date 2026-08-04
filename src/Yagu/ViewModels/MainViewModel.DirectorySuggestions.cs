using Yagu.Models;

namespace Yagu.ViewModels;

/// <summary>
/// Directory autocomplete: asynchronously listing sibling/child folders for the directory box and
/// applying the resulting suggestions on the UI thread.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Called when the directory text changes. Debounces and fetches subdirectory suggestions.
    /// </summary>
    internal async Task UpdateDirectorySuggestionsAsync(string text)
    {
        // Cancel any previous in-flight lookup.
        _dirAutoCompleteCts?.Cancel();
        _dirAutoCompleteCts = new CancellationTokenSource();
        var ct = _dirAutoCompleteCts.Token;

        try
        {
            // Debounce: wait 250ms before querying.
            await Task.Delay(250, ct).ConfigureAwait(false);

            var suggestions = await _dirAutoComplete.GetSuggestionsAsync(text, ct).ConfigureAwait(false);

            // If no subdirectory suggestions, show recent directories as fallback.
            if (suggestions.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                await ApplyDirectorySuggestionsAsync(_settings.RecentDirectories).ConfigureAwait(false);
                return;
            }

            await ApplyDirectorySuggestionsAsync(suggestions).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when user keeps typing.
        }
    }

    internal async Task<int> UpdateDirectorySuggestionsForSelectedDirectoryAsync(string directory)
    {
        _dirAutoCompleteCts?.Cancel();
        _dirAutoCompleteCts = new CancellationTokenSource();
        var ct = _dirAutoCompleteCts.Token;

        try
        {
            var suggestions = await _dirAutoComplete.GetChildDirectorySuggestionsAsync(directory, ct).ConfigureAwait(false);
            await ApplyDirectorySuggestionsAsync(suggestions).ConfigureAwait(false);
            return suggestions.Count;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    private Task ApplyDirectorySuggestionsAsync(IEnumerable<string> suggestions)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            DirectorySuggestions.Clear();
            foreach (var suggestion in suggestions)
                DirectorySuggestions.Add(new HistorySuggestion(suggestion, LookupRecentDirectoryTimestamp(suggestion)));
            completion.SetResult();
        }))
        {
            completion.SetResult();
        }

        return completion.Task;
    }
}
