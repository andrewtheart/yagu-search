namespace Yagu.Tests;

/// <summary>
/// Source-pin tests for MainWindow.QuickSearchEditor.cs — the row layout (actions inside the row border,
/// in a fixed right-hand lane), the visual icon picker that replaced the raw-codepoint text box, and the
/// Advanced Options capture that lets a quick search restore the whole drawer.
/// </summary>
public sealed class QuickSearchEditorTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string EditorSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.QuickSearchEditor.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));
    private static readonly string ViewModelSource = MainViewModelPartials.Text;

    // ══════════════════════════════════════════════════════════════════
    // Row layout: icons live inside the row, in a fixed lane
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Seeding_RunsOncePerProfileAndNeverResurrectsADeletedList()
    {
        string init = Extract(EditorSource, "private void InitializeQuickSearches");

        // The seed is gated on the one-shot flag, not on the list being empty — otherwise deleting every
        // quick search would silently restore the built-ins on the next launch.
        Assert.Contains("if (!settings.QuickSearchesInitialized)", init);
        int flagSet = init.IndexOf("settings.QuickSearchesInitialized = true;", StringComparison.Ordinal);
        int seeded = init.IndexOf("QuickSearchCatalog.Defaults()", StringComparison.Ordinal);
        Assert.True(flagSet >= 0 && seeded > flagSet, "The one-shot flag must be set before seeding.");

        // An already-initialized profile is canonicalized instead, so a hand-edited settings file cannot
        // feed blank/duplicate entries into the tab.
        Assert.Contains("QuickSearchCatalog.Normalize(settings.QuickSearches)", init);
    }

    [Fact]
    public void Seeding_LeavesAnAlreadyPopulatedListAloneOnAnUpgrade()
    {
        // A profile that predates the flag may already carry items; seeding over them would duplicate the
        // list, so the seed only fills an empty one.
        string init = Extract(EditorSource, "private void InitializeQuickSearches");
        Assert.Contains("if (settings.QuickSearches.Count == 0)", init);
    }

    [Fact]
    public void Row_PutsTheHoverActionsInsideTheRowBorder()
    {
        string row = Extract(EditorSource, "private Grid BuildQuickSearchRow");

        // The run Button IS the row border, so the actions must be siblings added AFTER it (drawn on top,
        // inside its border) rather than sitting in a second grid column beside it.
        int runAdded = row.IndexOf("grid.Children.Add(run);", StringComparison.Ordinal);
        int actionsAdded = row.IndexOf("grid.Children.Add(actions);", StringComparison.Ordinal);
        Assert.True(runAdded >= 0 && actionsAdded > runAdded,
            "The actions must be added after the run button so they render inside its border.");

        // A second top-level column would push the icons outside the border again.
        Assert.DoesNotContain("Grid.SetColumn(actions", row);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Right", row);
    }

    [Fact]
    public void Row_ReservesAFixedActionsLaneSoIconsDoNotMoveWithTheLabel()
    {
        Assert.Contains("private const double QuickSearchActionsLaneWidth", EditorSource);

        string row = Extract(EditorSource, "private Grid BuildQuickSearchRow");
        // Right padding reserves the lane inside the button's own border.
        Assert.Contains("Padding = new Thickness(11, 5, QuickSearchActionsLaneWidth, 6)", row);

        // A long label must ellipsize into the star column instead of growing the row and shoving the icons.
        Assert.Contains("HorizontalContentAlignment = HorizontalAlignment.Stretch", row);
        Assert.Contains("new GridLength(1, GridUnitType.Star)", row);
        Assert.Contains("TextTrimming = TextTrimming.CharacterEllipsis", row);
        Assert.Contains("TextWrapping = TextWrapping.NoWrap", row);
    }

    [Fact]
    public void Row_KeepsTheActionsHiddenRatherThanCollapsedSoWidthNeverShifts()
    {
        string row = Extract(EditorSource, "private Grid BuildQuickSearchRow");
        Assert.Contains("Opacity = 0", row);
        Assert.Contains("IsHitTestVisible = false", row);
        Assert.Contains("SetQuickSearchActionsVisible(actions, true)", row);
        Assert.Contains("SetQuickSearchActionsVisible(actions, false)", row);
    }

    [Fact]
    public void Row_MarksItemsThatCarryACapturedSnapshot()
    {
        string row = Extract(EditorSource, "private Grid BuildQuickSearchRow");
        Assert.Contains("item.HasOptions", row);
    }

    // ══════════════════════════════════════════════════════════════════
    // Editor: labelled fields and a visual icon picker
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Editor_ReplacedTheRawGlyphTextBoxWithAPicker()
    {
        // The old box rendered an un-typeable Segoe Fluent codepoint as tofu next to the Name field.
        Assert.DoesNotContain("var glyphBox = new TextBox", EditorSource);
        Assert.Contains("private static Flyout BuildQuickSearchGlyphPicker", EditorSource);

        string editor = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");
        Assert.Contains("glyphButton.Flyout = BuildQuickSearchGlyphPicker", editor);
        // The chosen glyph is previewed in the icon font, so it is never shown as a raw codepoint.
        Assert.Contains("glyphPreview.Glyph = chosen", editor);
    }

    [Fact]
    public void Editor_LabelsEveryFieldSoItsPurposeIsObvious()
    {
        string editor = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");
        Assert.Contains("Header = \"Name\"", editor);
        Assert.Contains("Header = \"Search pattern\"", editor);
        Assert.Contains("Header = \"Mode\"", editor);
        Assert.Contains("Header = \"Search in folder\"", editor);
        Assert.Contains("Text = \"Icon\"", editor);
    }

    // ══════════════════════════════════════════════════════════════════
    // Per-item search directory
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Editor_OffersAFolderFieldThatSaysWhatAnEmptyValueMeans()
    {
        string editor = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");

        // The consequence of leaving it blank must be stated in the UI, not just in the docs.
        Assert.Contains("PlaceholderText = \"All drives\"", editor);
        Assert.Contains("Leave empty to search every drive from its root.", editor);
        Assert.Contains("editing.Directory = directoryBox.Text;", editor);

        // A wrapping hint must not sit in a horizontal StackPanel, which measures at infinite width.
        Assert.Contains("TextWrapping = TextWrapping.Wrap", editor);
    }

    [Fact]
    public void Editor_BrowsesWithTheInAppFlyoutNotAModalDialog()
    {
        string editor = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");

        // A modal Win32 dialog would light-dismiss the Advanced Options drawer the editor lives in.
        Assert.Contains("browseButton.Flyout = UI.FolderBrowseFlyout.Create", editor);
        Assert.DoesNotContain("Win32FileDialog", EditorSource);

        // The browser opens where the field points and writes the choice straight back into it.
        AssertContainsInOrder(editor, "() => directoryBox.Text", "picked => directoryBox.Text = picked");
    }

    [Fact]
    public void Row_TooltipStatesWhereTheQuickSearchWillRun()
    {
        string describe = Extract(EditorSource, "private static string DescribeQuickSearch");
        Assert.Contains("item.SearchesAllDrives", describe);
        Assert.Contains("Searches every drive from its root.", describe);
        Assert.Contains("item.Directory", describe);
    }

    [Fact]
    public void GlyphPicker_OffersAChoiceGridAndClosesOnSelection()
    {
        Assert.Contains("private static readonly string[] QuickSearchGlyphChoices", EditorSource);

        string picker = Extract(EditorSource, "private static Flyout BuildQuickSearchGlyphPicker");
        AssertContainsInOrder(picker, "onChosen(glyph);", "flyout.Hide();");
    }

    // ══════════════════════════════════════════════════════════════════
    // Capturing the live Advanced Options
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void SaveCurrentOptions_SeedsADraftFromTheLiveDrawerNotTheSettingsFile()
    {
        string handler = Extract(EditorSource, "private void OnSaveCurrentOptionsAsQuickSearch");

        Assert.Contains("ViewModel.CaptureAdvancedOptions()", handler);
        Assert.DoesNotContain("_settingsService", handler);
        // The search box's own state is captured alongside the drawer.
        foreach (string property in new[]
        {
            "ViewModel.Query", "ViewModel.Directory", "ViewModel.IsSemanticQueryMode", "ViewModel.UseRegex",
            "ViewModel.CaseSensitive", "ViewModel.Multiline", "ViewModel.ExactMatch",
        })
        {
            Assert.Contains(property, handler);
        }

        // The seeded item goes straight into the flyout editor to be named before it is saved.
        Assert.Contains("ShowQuickSearchEditorFlyout(", handler);
    }

    [Fact]
    public void AddSaveCurrentOptionsAndEdit_AllOpenTheSameEditorFlyout()
    {
        foreach (string handler in new[]
        {
            "private void OnAddQuickSearch", "private void OnSaveCurrentOptionsAsQuickSearch",
            "private void OnEditQuickSearch",
        })
        {
            Assert.Contains("ShowQuickSearchEditorFlyout(", Extract(EditorSource, handler));
        }

        Assert.Contains("\"Add new quick search\",", Extract(EditorSource, "private void OnAddQuickSearch"));
        Assert.Contains("\"Edit quick search\"", Extract(EditorSource, "private void OnEditQuickSearch"));

        string show = Extract(EditorSource, "private void ShowQuickSearchEditorFlyout");
        Assert.Contains("FlyoutPlacementMode.Bottom", show);
        // Save and Cancel must dismiss the flyout, not just re-render the list behind it.
        Assert.Contains("BuildQuickSearchEditorPanel(draft, flyout.Hide, showOptionCaptureButtons)", show);
    }

    [Fact]
    public void AddQuickSearch_DefaultsToLiteralSearchWhileSaveCurrentKeepsLiveRegexState()
    {
        string add = Extract(EditorSource, "private void OnAddQuickSearch");
        string saveCurrent = Extract(EditorSource, "private void OnSaveCurrentOptionsAsQuickSearch");

        Assert.Contains("new QuickSearchItem { Id = QuickSearchCatalog.NewId(), UseRegex = false }", add);
        Assert.Contains("UseRegex = ViewModel.UseRegex", saveCurrent);
    }

    [Fact]
    public void EditorFlyout_ClearsTheRowAndCentersOnTheIconThatWasClicked()
    {
        string show = Extract(EditorSource, "private void ShowQuickSearchEditorFlyout");

        // Anchoring to the pencil itself would drop the flyout on top of the row it belongs to.
        Assert.Contains("FindQuickSearchRow(anchor) is { } row", show);
        Assert.Contains("flyout.ShowAt(row,", show);
        Assert.Contains("row.ActualHeight + 4", show);

        // Position is the flyout's top-left, so centering on the icon means subtracting half its width.
        Assert.Contains("anchor.ActualWidth / 2", show);
        Assert.Contains("iconCenterX - ((contentWidth + 32) / 2)", show);

        // Add / Save current options are not in a row and keep plain bottom-centered placement.
        Assert.Contains("flyout.ShowAt(anchor);", show);
    }

    [Fact]
    public void EditorFlyout_WidensThePresenterSoTheContentIsNotClipped()
    {
        string show = Extract(EditorSource, "private void ShowQuickSearchEditorFlyout");

        // The stock FlyoutPresenter caps content at 456px and would clip the editor's right edge.
        Assert.Contains("new Style(typeof(FlyoutPresenter))", show);
        Assert.Contains("FrameworkElement.MaxWidthProperty, contentWidth + 48", show);
        Assert.Contains("FrameworkElement.MinWidthProperty, 0d", show);
        // Horizontal scrolling would hide the overflow instead of fitting it.
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled", show);
    }

    [Fact]
    public void EditorFlyout_IsTitled()
    {
        string show = Extract(EditorSource, "private void ShowQuickSearchEditorFlyout");
        Assert.Contains("Text = title,", show);
        Assert.Contains("FontWeight = Microsoft.UI.Text.FontWeights.Bold", show);

        // Slightly smaller than a tab heading, which is 16.
        int tabHeading = MainWindowXaml.IndexOf("Text=\"Dates\" FontSize=\"16\"", StringComparison.Ordinal);
        Assert.True(tabHeading >= 0, "The Dates tab heading is the size this title is measured against.");
        Assert.Contains("FontSize = 14", show);
    }

    [Fact]
    public void EditingNoLongerRendersTheControlsInsideTheList()
    {
        // The inline editor and its bookkeeping were replaced wholesale by the flyout.
        Assert.DoesNotContain("_editingQuickSearchId", EditorSource);
        Assert.DoesNotContain("_pendingQuickSearchDraft", EditorSource);
        Assert.DoesNotContain("_quickSearchEditIsNew", EditorSource);
        Assert.DoesNotContain("private Border BuildQuickSearchEditor(", EditorSource);

        // The list now renders rows and nothing else.
        string refresh = Extract(EditorSource, "private void RefreshUserQuickSearches");
        Assert.Contains("BuildQuickSearchRow(items[i], i, items.Count)", refresh);
        Assert.DoesNotContain("BuildQuickSearchEditor", refresh);
    }

    [Fact]
    public void EditorPanel_HasASingleFlyoutHost()
    {
        Assert.Contains(
            "private StackPanel BuildQuickSearchEditorPanel(\r\n        QuickSearchItem item, Action? onClosed, bool showOptionCaptureButtons)",
            EditorSource);

        string panel = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");
        Assert.Equal(2, panel.Split("onClosed?.Invoke();").Length - 1);
    }

    [Fact]
    public void RecaptureAndClear_AreOfferedWhenEditingButNotWhenAdding()
    {
        // Adding a quick search has nothing to recapture or clear, so those buttons are gated.
        string panel = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");
        AssertContainsInOrder(panel,
            "if (showOptionCaptureButtons)",
            "captureRow.Children.Add(captureButton);",
            "captureRow.Children.Add(clearCaptureButton);");

        Assert.Contains("showOptionCaptureButtons: false", Extract(EditorSource, "private void OnAddQuickSearch"));
        Assert.Contains("showOptionCaptureButtons: true", Extract(EditorSource, "private void OnEditQuickSearch"));

        // The status line stays either way, so the flyout still says whether options were captured.
        Assert.Contains("captureRow.Children.Add(capturedText);", panel);
        int guard = panel.IndexOf("if (showOptionCaptureButtons)", StringComparison.Ordinal);
        int text = panel.IndexOf("captureRow.Children.Add(capturedText);", StringComparison.Ordinal);
        Assert.True(text < guard, "The captured-options status line must be added outside the guard.");
    }

    [Fact]
    public void IconLabel_MatchesTheOtherFieldHeaders()
    {
        // The other labels are TextBox Headers; a smaller, dimmed TextBlock beside them looked wrong.
        string panel = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");
        Assert.Contains("new TextBlock { Text = \"Icon\" }", panel);
        Assert.DoesNotContain("Text = \"Icon\", FontSize", panel);
    }

    [Fact]
    public void Editor_CanCaptureRecaptureAndClearTheSnapshot()
    {
        string editor = Extract(EditorSource, "private StackPanel BuildQuickSearchEditorPanel");
        Assert.Contains("editing.Options = ViewModel.CaptureAdvancedOptions();", editor);
        Assert.Contains("editing.Options = null;", editor);
    }

    // ══════════════════════════════════════════════════════════════════
    // View-model capture / apply
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Every field on the snapshot, so a newly added Advanced Option cannot be silently dropped.</summary>
    private static IEnumerable<string> SnapshotFieldNames() =>
        typeof(Yagu.Helpers.QuickSearchOptions)
            .GetProperties()
            .Select(p => p.Name);

    [Fact]
    public void CaptureAdvancedOptions_ReadsEverySnapshotFieldFromLiveState()
    {
        string capture = Extract(ViewModelSource, "public Yagu.Helpers.QuickSearchOptions CaptureAdvancedOptions()");
        foreach (string field in SnapshotFieldNames())
            Assert.Contains(field + " =", capture);
    }

    [Fact]
    public void ApplyAdvancedOptions_WritesEverySnapshotFieldBack()
    {
        string apply = Extract(ViewModelSource, "public void ApplyAdvancedOptions(");
        foreach (string field in SnapshotFieldNames())
            Assert.Contains("options." + field, apply);

        // The extension dropdowns are rebuilt from the restored lists.
        AssertContainsInOrder(apply,
            "SyncSkipExtensionItems();",
            "SyncBinaryExtensionItems();",
            "SyncArchiveExtensionItems();");

        // SearchBinary rewrites BinaryExtensions from the settings mirror, so the captured list lands after it.
        int searchBinary = apply.IndexOf("SearchBinary = options.SearchBinary;", StringComparison.Ordinal);
        int binaryExtensions = apply.IndexOf("BinaryExtensions = options.BinaryExtensions", StringComparison.Ordinal);
        Assert.True(searchBinary >= 0 && binaryExtensions > searchBinary,
            "BinaryExtensions must be restored after SearchBinary, which overwrites it.");

        // An unlimited depth is the empty box, which the view model represents as NaN.
        Assert.Contains("MaxSearchDepth = options.MaxSearchDepth ?? double.NaN;", apply);

        // The drawer no longer matches the saved defaults, so the post-search reset must restore them.
        Assert.Contains("_advancedOptionsTransientlyChanged = true;", apply);
    }

    [Fact]
    public void ApplyQuickSearchItem_RestoresTheSnapshotBeforeTheItemsOwnToggles()
    {
        string apply = Extract(ViewModelSource, "public void ApplyQuickSearchItem(");

        // The four inline toggles are authoritative for the item, so they must land after the snapshot.
        int snapshot = apply.IndexOf("ApplyAdvancedOptions(options)", StringComparison.Ordinal);
        int semantic = apply.IndexOf("IsSemanticQueryMode = item.Semantic;", StringComparison.Ordinal);
        Assert.True(snapshot >= 0 && semantic > snapshot,
            "The captured snapshot must be applied before the item's own search-box options.");

        // An item without a snapshot leaves the drawer untouched.
        Assert.Contains("if (item.Options is { } options)", apply);

        // An unset folder means every drive, so it is assigned rather than skipped.
        Assert.Contains("Directory = (item.Directory ?? string.Empty).Trim();", apply);
    }

    /// <summary>Slices from <paramref name="anchor"/> to the start of the next member, so a growing
    /// method never silently pushes an assertion out of a fixed-size window.</summary>
    private static string Extract(string source, string anchor)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Anchor not found: {anchor}");

        int end = source.Length;
        foreach (string boundary in new[] { "\n    private ", "\n    public ", "\n    internal ", "\n    /// " })
        {
            int next = source.IndexOf(boundary, start + anchor.Length, StringComparison.Ordinal);
            if (next >= 0 && next < end)
                end = next;
        }

        return source[start..end];
    }

    private static void AssertContainsInOrder(string text, params string[] parts)
    {
        int cursor = 0;
        foreach (string part in parts)
        {
            int index = text.IndexOf(part, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected '{part}' after index {cursor}.");
            cursor = index + part.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
