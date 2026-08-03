namespace Yagu.Tests;

public sealed class EditorContextMenuRegressionTests
{
    private static readonly string FlyoutSource = Read(
        "src", "vendor", "TextControlBox-WinUI", "TextControlBox", "Helper", "FlyoutHelper.cs");

    [Fact]
    public void ContextMenu_OffersPlainAndLineNumberedCopyBeforeEditingCommands()
    {
        string create = ExtractWindow("public void CreateFlyout", 1600);

        AssertContainsInOrder(create,
            "sender.Copy(); }, \"Copy\"",
            "CopyWithLineNumbers(sender); }, \"Copy (with line numbers)\"",
            "new MenuFlyoutSeparator()",
            "sender.Paste(); }, \"Paste\"",
            "sender.Cut(); }, \"Cut\"",
            "new MenuFlyoutSeparator()",
            "sender.Undo(); }, \"Undo\"",
            "sender.Redo(); }, \"Redo\"");
        Assert.Contains("sender.Focus(FocusState.Programmatic)", create);
    }

    [Fact]
    public void CopyWithLineNumbers_HandlesSelectionBoundsAndPublishesText()
    {
        string copy = ExtractWindow("private static void CopyWithLineNumbers", 2200);

        AssertContainsInOrder(copy,
            "var sel = sender.CurrentSelectionOrdered;",
            "if (sel is null) return;",
            "int lineNumWidth = (endLine + 1).ToString().Length;",
            "for (int i = startLine; i <= endLine; i++)",
            "sender.GetLineText(i) ?? string.Empty",
            "if (i == startLine && i == endLine)",
            "lineText[startChar..Math.Min(endChar, lineText.Length)]",
            "else if (i == startLine)",
            "lineText[startChar..]",
            "else if (i == endLine)",
            "lineText[..Math.Min(endChar, lineText.Length)]",
            "PadLeft(lineNumWidth)",
            "var dataPackage = new DataPackage();",
            "dataPackage.SetText(sb.ToString());",
            "Clipboard.SetContent(dataPackage);");
    }

    [Fact]
    public void CreateItem_WiresTextIconAcceleratorAndClickAction()
    {
        string createItem = ExtractWindow("public MenuFlyoutItem CreateItem", 900);

        Assert.Contains("Text = text", createItem);
        Assert.Contains("KeyboardAcceleratorTextOverride = key", createItem);
        Assert.Contains("Icon = new SymbolIcon { Symbol = icon }", createItem);
        Assert.Contains("item.Click += delegate", createItem);
        Assert.Contains("action();", createItem);
    }

    private static string ExtractWindow(string marker, int length)
    {
        int start = FlyoutSource.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Marker '{marker}' was not found.");
        return FlyoutSource[start..Math.Min(FlyoutSource.Length, start + length)];
    }

    private static void AssertContainsInOrder(string source, params string[] expected)
    {
        int position = 0;
        foreach (string item in expected)
        {
            int found = source.IndexOf(item, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{item}' after position {position}.");
            position = found + item.Length;
        }
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Yagu.slnx.");
    }
}