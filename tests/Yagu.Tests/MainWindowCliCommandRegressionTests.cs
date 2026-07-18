namespace Yagu.Tests;

public sealed class MainWindowCliCommandRegressionTests
{
    [Fact]
    public void AdvancedOptions_GenerateCliCommandButton_IsWiredToClosableOverlay()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"GenerateCliCommandButton\"", xaml);
        Assert.Contains("Click=\"OnGenerateCliCommandClick\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml);
        Assert.Contains("VerticalAlignment=\"Top\"", xaml);
        Assert.Contains("Padding=\"8,5\"", xaml);
        Assert.Contains("<FontIcon Glyph=\"&#xE756;\" FontSize=\"11\" />", xaml);
        Assert.Contains("<TextBlock Text=\"Generate CLI command\" FontSize=\"11\" />", xaml);
        Assert.Contains("Canvas.ZIndex=\"20\"", xaml);
        Assert.Contains("x:Name=\"GeneratedCliCommandFlyout\"", xaml);
        Assert.Contains("x:Name=\"GeneratedCliCommandBubble\"", xaml);
        Assert.Contains("x:Name=\"GeneratedCliCommandText\"", xaml);
        Assert.Contains("x:Name=\"IncludeSavedSettingCliOptionsToggle\"", xaml);
        Assert.Contains("Header=\"Options already saved in settings\"", xaml);
        Assert.Contains("OnContent=\"Include\"", xaml);
        Assert.Contains("OffContent=\"Omit\"", xaml);
        Assert.Contains("Omit options that already match the current settings file", xaml);
        Assert.Contains("IsOn=\"False\"", xaml);
        Assert.DoesNotContain("IsOn=\"True\"", xaml);
        Assert.Contains("Toggled=\"OnGeneratedCliCommandSavedSettingOptionsToggled\"", xaml);
        Assert.DoesNotContain("settings-backed", xaml);
        Assert.Contains("FontFamily=\"Consolas\"", xaml);
        string commandTextSnippet = xaml.Substring(xaml.IndexOf("x:Name=\"GeneratedCliCommandText\"", StringComparison.Ordinal), 400);
        Assert.Contains("TextWrapping=\"Wrap\"", commandTextSnippet);
        Assert.DoesNotContain("TextWrapping=\"WrapWholeWords\"", commandTextSnippet);
        Assert.Contains("Click=\"OnCopyGeneratedCliCommandClick\"", xaml);
        Assert.Contains("x:Name=\"SendGeneratedCliCommandToTerminalButton\"", xaml);
        Assert.Contains("Click=\"OnSendGeneratedCliCommandToTerminalClick\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"Send command to terminal\"", xaml);
        Assert.Contains("Click=\"OnCloseGeneratedCliCommandOverlayClick\"", xaml);
        Assert.Contains("x:Name=\"AdvancedOptionsTabList\"", xaml);
        Assert.Contains("SelectionChanged=\"OnAdvancedOptionsTabSelectionChanged\"", xaml);
        Assert.Contains("x:Name=\"AdvancedOptionsSearchTabContent\"", xaml);
        Assert.Contains("x:Name=\"AdvancedOptionsFiltersTabContent\"", xaml);
        Assert.Contains("x:Name=\"AdvancedOptionsSizeTabContent\"", xaml);
        Assert.Contains("x:Name=\"AdvancedOptionsDatesTabContent\"", xaml);
        Assert.Contains("x:Name=\"AdvancedOptionsAdvancedTabContent\"", xaml);
        Assert.Contains("Click=\"OnAdvancedOptionsResetClick\"", xaml);
        Assert.Contains("Click=\"OnAdvancedOptionsApplyClick\"", xaml);
        int buttonIndex = xaml.IndexOf("x:Name=\"GenerateCliCommandButton\"", StringComparison.Ordinal);
        int tabListIndex = xaml.IndexOf("x:Name=\"AdvancedOptionsTabList\"", StringComparison.Ordinal);
        int searchTabIndex = xaml.IndexOf("x:Name=\"AdvancedOptionsSearchTabContent\"", StringComparison.Ordinal);

        Assert.True(buttonIndex > tabListIndex, "The Generate CLI command button should be in the footer below Advanced Options tabs.");
        Assert.True(tabListIndex < searchTabIndex, "The tab rail should render before the selected Advanced Options tab content.");
        Assert.Contains("<FlyoutBase.AttachedFlyout>", xaml);
        Assert.True(
            xaml.IndexOf("x:Name=\"GeneratedCliCommandFlyout\"", StringComparison.Ordinal) > buttonIndex,
            "The generated CLI command should render in an attached flyout on the Generate CLI command button.");
    }

    [Fact]
    public void CliCommandGenerator_EmitsExplicitSearchAndAdvancedOptionFlags()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.CliCommand.cs"));
        string terminalSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));
        string terminalDirectoryGuardSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "TerminalDirectoryGuard.cs"));
        string terminalHtml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Assets", "terminal.html"));

        Assert.Contains("\"--directory\"", source);
        Assert.Contains("\"--pattern\"", source);
        // Semantic mode must emit --semantic-pattern instead of --pattern.
        Assert.Contains("\"--semantic-pattern\"", source);
        Assert.Contains("ViewModel.IsSemanticQueryMode", source);
        // Hidden-files scope must be reproducible via --hidden / --no-hidden.
        Assert.Contains("\"--hidden\"", source);
        Assert.Contains("\"--no-hidden\"", source);
        Assert.Contains("ViewModel.SearchHiddenFiles == setting.SearchHiddenFiles", source);
        // Image-text (OCR) scope must be reproducible via --image-text / --no-image-text.
        Assert.Contains("\"--image-text\"", source);
        Assert.Contains("\"--no-image-text\"", source);
        Assert.Contains("ViewModel.SearchImageText == setting.SearchImageText", source);
        // PDF-text scope must be reproducible via --pdf-text / --no-pdf-text.
        Assert.Contains("\"--pdf-text\"", source);
        Assert.Contains("\"--no-pdf-text\"", source);
        Assert.Contains("ViewModel.SearchPdfText == setting.SearchPdfText", source);
        // OCR engine / recognition model / detection resolution must be reproducible when image-text
        // is on, so a UI OCR search runs with the same engine/model/resolution from the CLI.
        Assert.Contains("\"--ocr-engine\"", source);
        Assert.Contains("\"--ocr-model\"", source);
        Assert.Contains("\"--ocr-max-side\"", source);
        Assert.Contains("string.Equals(ViewModel.ImageOcrEngine, setting.ImageOcrEngine", source);
        Assert.Contains("string.Equals(ViewModel.ImageOcrModel, setting.ImageOcrModel", source);
        Assert.Contains("ViewModel.ImageOcrMaxSide == setting.ImageOcrMaxSide", source);
        // A pinned semantic model must be reproducible via --semantic-model in semantic mode.
        Assert.Contains("\"--semantic-model\"", source);
        Assert.Contains("string.Equals(ViewModel.SemanticModelAlias, setting.SemanticModelAlias", source);
        Assert.Contains("\"--regex\"", source);
        Assert.Contains("\"--no-regex\"", source);
        Assert.Contains("\"--case-sensitive\"", source);
        Assert.Contains("\"--ignore-case\"", source);
        Assert.Contains("\"--exact-match\"", source);
        Assert.Contains("\"--no-exact-match\"", source);
        Assert.Contains("\"--search-mode\"", source);
        Assert.Contains("\"--include-regex\"", source);
        Assert.Contains("\"--exclude-regex\"", source);
        Assert.Contains("\"--skip-extensions\"", source);
        Assert.Contains("\"--search-archives\"", source);
        Assert.Contains("\"--archive-extensions\"", source);
        Assert.Contains("\"--max-depth\"", source);
        Assert.Contains("SearchOptions.ResolveContentSearchParallelism", source);
        Assert.Contains("BuildEffectiveSkipExtensionsForCli", source);
        Assert.Contains("QuoteCliValue", source);
        Assert.Contains("_includeGeneratedCliCommandSavedSettingOptions", source);
        Assert.Contains("OnGeneratedCliCommandSavedSettingOptionsToggled", source);
        Assert.Contains("BuildGeneratedCliCommand(bool includeSavedSettingOptions)", source);
        Assert.Contains("private bool _includeGeneratedCliCommandSavedSettingOptions;", source);
        Assert.Contains("BuildGeneratedCliCommand(includeSavedSettingOptions: false)", source);
        Assert.DoesNotContain("private bool _includeGeneratedCliCommandSavedSettingOptions = true;", source);
        Assert.DoesNotContain("BuildGeneratedCliCommand(includeSavedSettingOptions: true)", source);
        Assert.Contains("new SettingsService().Load()", source);
        Assert.Contains("ShouldIncludeSavedSettingOption", source);
        Assert.Contains("SplitSettingsPatternsForCli", source);
        Assert.Contains("AdminProtectedPathSegmentsEqual", source);
        Assert.Contains("CloseGeneratedCliCommandOverlay", source);
        Assert.Contains("FlyoutBase.ShowAttachedFlyout(GenerateCliCommandButton);", source);
        Assert.Contains("OnSendGeneratedCliCommandToTerminalClick", source);
        Assert.Contains("CollapseAdvancedOptionsForSearch();", source);
        Assert.Contains("await SendTextToTerminalAsync(GeneratedCliCommandText.Text);", source);
        Assert.Contains("GeneratedCliCommandFlyout.Hide();", source);
        Assert.Contains("Could not send generated CLI command to terminal", source);
        Assert.Contains("EnsureGeneratedCliCommandText();", source);
        Assert.Contains("private async Task SendTextToTerminalAsync(string text)", terminalSource);
        Assert.Contains("EnsureTerminalPaneExpandedAsync", terminalSource);
        Assert.Contains("await WaitForTerminalShellReadyAsync();", terminalSource);
        Assert.Contains("await VerifyTerminalDirectoryIsYaguExecutableDirectoryAsync();", terminalSource);
        // The bare "Yagu.exe" must be adapted for the active shell before it is sent (PowerShell
        // will not run it from the CWD without a ".\" prefix).
        Assert.Contains("TerminalShell.PrefixCurrentDirectoryExecutable(commandText, _terminalActiveShellKind)", terminalSource);
        Assert.Contains("cd /d", terminalDirectoryGuardSource);
        Assert.Contains("&& echo", terminalDirectoryGuardSource);
        Assert.Contains("TerminalDirectoryGuard.BuildChangeDirectoryProbeCommand", terminalSource);
        Assert.Contains("TerminalDirectoryGuard.CreateMarker", terminalSource);
        Assert.Contains("TerminalDirectoryGuard.TryExtractPromptDirectory", terminalSource);
        Assert.Contains("TerminalDirectoryGuard.DirectoriesEqual", terminalSource);
        Assert.Contains("TerminalDirectoryGuard.RemoveMarkerLine", terminalSource);
        Assert.Contains("_terminalDirectoryProbe", terminalSource);
        Assert.Contains("Environment.ProcessPath", terminalSource);
        Assert.Contains("Path.GetDirectoryName(Environment.ProcessPath)", terminalSource);
        Assert.Contains("echoInput: false", terminalSource);
        Assert.Contains("TrimEnd('\\r', '\\n')", terminalSource);
        Assert.Contains("_terminalShellReadyCompletion", terminalSource);
        Assert.Contains("ContainsPrintableShellText", terminalSource);
        Assert.Contains("PostTerminalPasteTextToWebView(commandText);", terminalSource);
        Assert.Contains("msg.type === 'pasteText'", terminalHtml);
        Assert.Contains("PostWebMessageAsJson(\"{\\\"type\\\":\\\"focus\\\"}\")", terminalSource);
        Assert.Contains("msg.type === 'focus'", terminalHtml);

        AssertContainsInOrder(terminalSource,
            "await VerifyTerminalDirectoryIsYaguExecutableDirectoryAsync();",
            "string commandText = text.TrimEnd('\\r', '\\n');",
            "PostTerminalPasteTextToWebView(commandText);");

        string ensureTerminalPane = terminalSource.Substring(
            terminalSource.IndexOf("private async Task EnsureTerminalPaneExpandedAsync()", StringComparison.Ordinal),
            500);
        Assert.Contains("SetTerminalPaneExpanded(true);", ensureTerminalPane);
        Assert.Contains("TerminalWebView.UpdateLayout();", ensureTerminalPane);
        Assert.DoesNotContain("if (!_terminalPaneExpanded)", ensureTerminalPane);
        Assert.DoesNotContain("SettingsBacked", source);
    }

    [Fact]
    public void EmbeddedTerminal_UsesPageSideLineEditorForPromptSafeInputAndShortcuts()
    {
        string root = FindRepoRoot();
        string terminalSource = File.ReadAllText(Path.Combine(root, "src", "Yagu", "UI", "Windows", "MainWindow", "MainWindow.Terminal.cs"));
        string terminalHtml = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Assets", "terminal.html"));
        string terminalServiceSource = File.ReadAllText(Path.Combine(root, "src", "Yagu", "Services", "ConPtyTerminalService.cs"));

        Assert.Contains("let inputBuffer = '';", terminalHtml);
        Assert.Contains("let inputCursor = 0;", terminalHtml);
        Assert.Contains("let shellCommandActive = false;", terminalHtml);
        Assert.Contains("let lastPromptText = '';", terminalHtml);
        Assert.Contains("function renderInputLine()", terminalHtml);
        Assert.Contains("function insertInputText(text)", terminalHtml);
        Assert.Contains("function backspaceInput()", terminalHtml);
        Assert.Contains("function deleteInput()", terminalHtml);
        Assert.Contains("function recallHistory(direction)", terminalHtml);
        Assert.Contains("function insertPastedText(text)", terminalHtml);
        Assert.Contains("function cancelInputLine()", terminalHtml);
        Assert.Contains("function markShellIdleIfPromptVisible()", terminalHtml);
        Assert.Contains("function rememberPromptText(text)", terminalHtml);
        Assert.Contains("function rememberPromptFromOutput(text)", terminalHtml);
        Assert.Contains("function formatPromptForInput(promptText)", terminalHtml);
        Assert.Contains("function requestInputCompletion()", terminalHtml);
        Assert.Contains("function applyCompletionResult(result)", terminalHtml);

        Assert.Contains("case 'Backspace': return { kind: 'backspace' };", terminalHtml);
        Assert.Contains("case 'Tab': return { kind: 'completion' };", terminalHtml);
        Assert.Contains("case 'Delete': return { kind: 'delete' };", terminalHtml);
        Assert.Contains("case 'ArrowUp': return { kind: 'history', direction: -1 };", terminalHtml);
        Assert.Contains("case 'ArrowDown': return { kind: 'history', direction: 1 };", terminalHtml);
        Assert.Contains("case 'ArrowRight': return { kind: 'move', offset: 1 };", terminalHtml);
        Assert.Contains("case 'ArrowLeft': return { kind: 'move', offset: -1 };", terminalHtml);
        Assert.DoesNotContain("Ctrl+A..Z ->", terminalHtml);
        Assert.DoesNotContain(@"case 'Backspace': return '\x7f';", terminalHtml);
        Assert.DoesNotContain(@"case 'ArrowUp': return '\x1b[A';", terminalHtml);

        AssertContainsInOrder(terminalHtml,
            "if (event.ctrlKey && !event.altKey && !event.metaKey)",
            "if (key === 'c')",
            "copyTerminalSelection();",
            "cancelInputLine();",
            "if (key === 'a')",
            "term.selectAll();",
            "if (key === 'v')",
            "postHostMessage({ type: 'requestPaste' });");

        Assert.Contains("postHostMessage({ type: 'cancelInput' });", terminalHtml);
    Assert.Contains("type: 'completeInput'", terminalHtml);
        Assert.Contains("msg.type === 'pasteText'", terminalHtml);
        Assert.Contains("insertPastedText(msg.text || '')", terminalHtml);
    Assert.Contains("msg.type === 'completionResult'", terminalHtml);
        Assert.Contains("sendTerminalInput(line + '\\r', false);", terminalHtml);
    Assert.Contains("getCurrentPromptText()", terminalHtml);
    Assert.Contains("clearTerminalSurface(promptText);", terminalHtml);
    Assert.DoesNotContain("sendTerminalInput('cls\\r', false);", terminalHtml);

        string submitInputLine = terminalHtml.Substring(
            terminalHtml.IndexOf("function submitInputLine()", StringComparison.Ordinal),
            terminalHtml.IndexOf("function insertPastedText(text)", StringComparison.Ordinal) - terminalHtml.IndexOf("function submitInputLine()", StringComparison.Ordinal));
        AssertContainsInOrder(submitInputLine,
            "if (isClearCommand(line))",
            "var promptText = getCurrentPromptText();",
            "clearRenderedInput();",
            "clearInputState();",
            "clearTerminalSurface(promptText);",
            "return;",
            "clearInputState();",
            "term.write('\\r\\n');",
            "shellCommandActive = true;",
            "sendTerminalInput(line + '\\r', false);");

        string cancelInputLine = terminalHtml.Substring(
            terminalHtml.IndexOf("function cancelInputLine()", StringComparison.Ordinal),
            terminalHtml.IndexOf("// WebView2 frequently fails", StringComparison.Ordinal) - terminalHtml.IndexOf("function cancelInputLine()", StringComparison.Ordinal));
        AssertContainsInOrder(cancelInputLine,
            "var promptText = getCurrentPromptText();",
            "var shouldRedrawPrompt = rememberPromptText(promptText);",
            "if (!shouldRedrawPrompt && !shellCommandActive && lastPromptText.length > 0)",
            "promptText = lastPromptText;",
            "shouldRedrawPrompt = true;",
            "if (!shellCommandActive)",
            "moveRenderedCursorToEnd();",
            "term.write('^C\\r\\n');",
            "clearInputState();",
            "if (shouldRedrawPrompt)",
            "term.write(formatPromptForInput(promptText));",
            "return;",
            "clearRenderedInput();",
            "term.write('^C\\r\\n');",
            "if (shouldRedrawPrompt)",
            "term.write(formatPromptForInput(promptText));",
            "postHostMessage({ type: 'cancelInput' });");

        AssertContainsInOrder(terminalHtml,
            "if (msg && msg.type === 'output')",
            "var firstOutput = receivedOutputCount === 0;",
            "rememberPromptFromOutput(msg.data || '');",
            "term.writeSync(msg.data || '');");

        AssertContainsInOrder(terminalHtml,
            "function markShellIdleIfPromptVisible()",
            "var promptText = getCurrentPromptText();",
            "rememberPromptText(promptText);");

        AssertContainsInOrder(terminalHtml,
            "function rememberPromptText(text)",
            "lastPromptText = text;",
            "shellCommandActive = false;",
            "return true;");

        AssertContainsInOrder(terminalHtml,
            "function formatPromptForInput(promptText)",
            "if (!promptText)",
            "return '';",
            "return /\\s$/.test(promptText) ? promptText : promptText + ' ';");
        AssertContainsInOrder(terminalHtml,
            "refreshTerminalRows();",
            "markShellIdleIfPromptVisible();");

        Assert.Contains("case \"cancelInput\":", terminalSource);
        Assert.Contains("_terminalService?.CancelCurrentCommand();", terminalSource);
        Assert.Contains("case \"completeInput\":", terminalSource);
        Assert.Contains("TerminalCompletionService.Complete(", terminalSource);
        Assert.Contains("PostTerminalCompletionResultToWebView(result);", terminalSource);
        Assert.Contains("PostTerminalPasteTextToWebView(text);", terminalSource);
        Assert.Contains("PostTerminalPasteTextToWebView(commandText);", terminalSource);

        Assert.Contains("public void WriteInput(string text, bool echoInput)", terminalServiceSource);
        Assert.Contains("BuildLocalEcho(text)", terminalServiceSource);
        Assert.Contains("echoInput: false", terminalSource);
        Assert.Contains("public void CancelCurrentCommand()", terminalServiceSource);
        Assert.Contains("CreateToolhelp32Snapshot", terminalServiceSource);
        Assert.Contains("Process32First", terminalServiceSource);
        Assert.Contains("Process32Next", terminalServiceSource);
        Assert.DoesNotContain("Stop-Process", terminalServiceSource);
        Assert.DoesNotContain("taskkill", terminalServiceSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int index = 0;
        foreach (string part in expected)
        {
            int found = text.IndexOf(part, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{part}' after index {index}.");
            index = found + part.Length;
        }
    }
}