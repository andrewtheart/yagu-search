using Yagu.Models;

namespace Yagu.ViewModels;

/// <summary>
/// Multi-key result sorting and the extension filter applied on top of it: applying/removing a
/// sort selection, keeping the primary sort dropdowns in sync with the criteria list, and the
/// extension-filter option list.
/// </summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<SortCriterion> SortCriteria => _sortCriteria;

    public int? GetSortDirectionIndex(int sortModeIndex)
    {
        int index = _sortCriteria.FindIndex(criterion => criterion.SortModeIndex == sortModeIndex);
        return index >= 0 ? _sortCriteria[index].SortDirectionIndex : null;
    }

    public void ApplySortSelection(int sortModeIndex, int sortDirectionIndex)
    {
        if (sortModeIndex <= 0)
        {
            SetSingleSortCriterion(0, sortDirectionIndex);
        }
        else
        {
            int direction = sortDirectionIndex == 1 ? 1 : 0;
            int index = _sortCriteria.FindIndex(criterion => criterion.SortModeIndex == sortModeIndex);
            var criterion = new SortCriterion(sortModeIndex, direction);
            if (index >= 0)
                _sortCriteria[index] = criterion;
            else
                _sortCriteria.Add(criterion);
        }

        SyncPrimarySortPropertiesFromCriteria();
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }

    public void RemoveSortSelection(int sortModeIndex)
    {
        int index = _sortCriteria.FindIndex(criterion => criterion.SortModeIndex == sortModeIndex);
        if (index < 0)
            return;

        _sortCriteria.RemoveAt(index);
        SyncPrimarySortPropertiesFromCriteria();
        OnPropertyChanged(nameof(SortCriteria));
        ApplySortAndFilter();
    }

    public IReadOnlyList<ExtensionFilterOption> GetExtensionFilterOptions() =>
        _resultCollection.GetExtensionFilterOptions();

    public void SetExtensionFilter(IEnumerable<string> extensions)
    {
        _selectedExtensionFilters.Clear();
        foreach (string extension in extensions)
        {
            string normalized = SearchResultCollection.NormalizeExtensionFilter(extension);
            if (!string.IsNullOrWhiteSpace(normalized))
                _selectedExtensionFilters.Add(normalized);
        }

        OnPropertyChanged(nameof(HasExtensionFilter));
        OnPropertyChanged(nameof(ExtensionFilterLabel));
        ApplySortAndFilter();
    }

    public void ClearExtensionFilter() => SetExtensionFilter([]);

    private void SetSingleSortCriterion(int sortModeIndex, int sortDirectionIndex)
    {
        _sortCriteria.Clear();
        if (sortModeIndex > 0)
            _sortCriteria.Add(new SortCriterion(sortModeIndex, sortDirectionIndex == 1 ? 1 : 0));
    }

    private void SyncPrimarySortPropertiesFromCriteria()
    {
        _updatingSortCriteria = true;
        try
        {
            if (_sortCriteria.Count > 0)
            {
                SortModeIndex = _sortCriteria[0].SortModeIndex;
                SortDirectionIndex = _sortCriteria[0].SortDirectionIndex;
            }
            else
            {
                SortModeIndex = 0;
                SortDirectionIndex = 0;
            }
        }
        finally
        {
            _updatingSortCriteria = false;
        }
    }
}
