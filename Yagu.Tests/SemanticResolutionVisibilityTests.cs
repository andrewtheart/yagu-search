using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin coverage for two coupled UX behaviors that live in WinUI view-model code (which depends
/// on WindowsAppSDK and can't run headless):
/// 1. The binary-extensions dropdown reads "checked = search this binary type" — flipped purely in the
///    display layer, so the internal <c>BinaryExtensions</c> stays the skip list (engine/CLI/predictor
///    unchanged).
/// 2. A semantic search's resolved settings stay VISIBLE in Advanced Options after the run, are reset
///    to the saved defaults at the start of the next search, and are never written to settings.json.
/// </summary>
public sealed class SemanticResolutionVisibilityTests
{
    private static readonly string MainViewModelSource = ReadSource("Yagu", "ViewModels", "MainViewModel.cs");

    [Fact]
    public void BinaryDropdown_DisplayFlipped_ToCheckedEqualsSearch()
    {
        // Display layer: an item is checked when it is NOT in the skip list.
        string sync = Method("SyncBinaryExtensionItems", 1200);
        Assert.Contains("new SkipExtensionItem(ext, group.Key, !enabled.Contains(ext))", sync);

        // Toggle-back: the skip list is the UNSELECTED items.
        string toggled = Method("OnBinaryExtensionToggled", 700);
        Assert.Contains("BinaryExtensionItems.Where(i => !i.IsEnabled)", toggled);

        // The semantic enable selects exactly the targeted binary types (skip = universe minus targeted).
        string enable = Method("EnableBinarySearchForBinaryGlobs", 1500);
        Assert.Contains("universe.Where(e => !targeted.Contains(e))", enable);
        Assert.Contains("SearchBinary = true", enable);
    }

    [Fact]
    public void SemanticResolution_StaysVisible_ResetsNextSearch_AndIsNotPersisted()
    {
        // The next search clears a previous resolution back to the saved defaults before running.
        string submit = Method("SubmitSearchAsync", 3400);
        Assert.Contains("ResetVisibleSemanticResolution();", submit);
        // A committed search is left visible on purpose — only an uncommitted run reverts in the finally.
        Assert.Contains("_semanticDefaultsSnapshot is { } leftover && !_semanticResolutionVisible", submit);

        // StartSearchAsync no longer reverts the plan; it marks the resolution visible and persists.
        string start = Method("StartSearchAsync", 16000);
        Assert.Contains("_semanticResolutionVisible = true;", start);
        Assert.DoesNotContain("RestoreSearchDefaults(restoreDefaults)", start);

        // The reset helper restores the captured defaults and clears the flag.
        string reset = Method("ResetVisibleSemanticResolution", 600);
        Assert.Contains("RestoreSearchDefaults(snapshot)", reset);
        Assert.Contains("_semanticResolutionVisible = false;", reset);

        // The Search-mode dropdown is part of the snapshot, so it resets to the user's default (e.g.
        // "File names + content") each search instead of keeping a previous plan's mode.
        string restore = Method("RestoreSearchDefaults", 3200);
        Assert.Contains("SearchModeIndex = s.SearchModeIndex;", restore);
        // The ENTIRE filter surface is captured/restored — including the active skip list, the persisted
        // Settings* extension mirrors, and the OCR toggle — so a transient "Include & search" un-skip or
        // any future resolution path can never leave a resolved value behind (or leak it to disk).
        Assert.Contains("SkipExtensions = s.SkipExtensions;", restore);
        Assert.Contains("SettingsSkipExtensions = s.SettingsSkipExtensions;", restore);
        Assert.Contains("SettingsBinaryExtensions = s.SettingsBinaryExtensions;", restore);
        Assert.Contains("SettingsArchiveExtensions = s.SettingsArchiveExtensions;", restore);
        Assert.Contains("SearchImageText = s.SearchImageText;", restore);

        // Persist guard: while a resolution is visible, the saved defaults (snapshot) reach disk, not
        // the resolved values — for representative leak-prone fields.
        string persist = Method("PersistSettingsAsync", 16000);
        Assert.Contains("var d = _semanticResolutionVisible ? _semanticDefaultsSnapshot : null;", persist);
        Assert.Contains("_settings.IncludeGlobs = d is null ? IncludeGlobs : d.IncludeGlobs;", persist);
        // The extension lists (the historical leak path) are guarded by the same snapshot, not written raw.
        Assert.Contains("_settings.SkipExtensions = d is null ? SettingsSkipExtensions : d.SettingsSkipExtensions;", persist);
        Assert.Contains("_settings.BinaryExtensions = d is null ? SettingsBinaryExtensions : d.SettingsBinaryExtensions;", persist);
        Assert.Contains("_settings.ArchiveExtensions = d is null ? SettingsArchiveExtensions : d.SettingsArchiveExtensions;", persist);
        Assert.Contains("_settings.SearchImageText = d is null ? SearchImageText : d.SearchImageText;", persist);
        // The resolved directory is the deliberate exception that DOES persist.
        Assert.Contains("_settings.LastDirectory = Directory;", persist);
    }

    private static string Method(string name, int window)
    {
        int idx = MainViewModelSource.IndexOf(name + "(", StringComparison.Ordinal);
        // Skip past call sites to the definition line (one that declares an access modifier).
        while (idx >= 0)
        {
            int lineStart = MainViewModelSource.LastIndexOf('\n', idx) + 1;
            string line = MainViewModelSource[lineStart..MainViewModelSource.IndexOf('\n', idx)];
            if (line.Contains("private ", StringComparison.Ordinal) || line.Contains("public ", StringComparison.Ordinal)
                || line.Contains("internal ", StringComparison.Ordinal))
                return MainViewModelSource.Substring(idx, Math.Min(window, MainViewModelSource.Length - idx));
            idx = MainViewModelSource.IndexOf(name + "(", idx + name.Length + 1, StringComparison.Ordinal);
        }
        throw new Xunit.Sdk.XunitException($"Definition of {name} not found in MainViewModel.cs");
    }

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
