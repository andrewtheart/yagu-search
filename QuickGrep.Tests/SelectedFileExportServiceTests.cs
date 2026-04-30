using QuickGrep.Services;

namespace QuickGrep.Tests;

public sealed class SelectedFileExportServiceTests
{
    [Fact]
    public void BuildPathListText_WritesOnePathPerLine()
    {
        var text = SelectedFileExportService.BuildPathListText([
            @"C:\work\one.txt",
            @"D:\repo\two.cs",
        ]);

        Assert.Equal($@"C:\work\one.txt{Environment.NewLine}D:\repo\two.cs", text);
    }

    [Fact]
    public async Task BuildFilesWithContentTextAsync_WritesMetadataSeparatorAndContent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "qg-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var path = Path.Combine(tempRoot, "sample.txt");
            await File.WriteAllTextAsync(path, "alpha" + Environment.NewLine + "beta");

            var created = new DateTime(2026, 4, 29, 10, 11, 12);
            var modified = new DateTime(2026, 4, 30, 13, 14, 15);
            File.SetCreationTime(path, created);
            File.SetLastWriteTime(path, modified);

            var text = await SelectedFileExportService.BuildFilesWithContentTextAsync([path]);

            Assert.Contains($"sample.txt {path} 2026-04-29 10:11:12 2026-04-30 13:14:15", text);
            Assert.Contains(SelectedFileExportService.ContentSeparator, text);
            Assert.Contains("alpha" + Environment.NewLine + "beta", text);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteFilesWithContentAsync_RepeatsBlocksForEachSelectedFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "qg-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var first = Path.Combine(tempRoot, "first.txt");
            var second = Path.Combine(tempRoot, "second.txt");
            await File.WriteAllTextAsync(first, "first content");
            await File.WriteAllTextAsync(second, "second content");

            using var writer = new StringWriter();
            await SelectedFileExportService.WriteFilesWithContentAsync([first, second], writer);
            var text = writer.ToString();

            Assert.Contains($"first.txt {first}", text);
            Assert.Contains("first content", text);
            Assert.Contains($"second.txt {second}", text);
            Assert.Contains("second content", text);
            Assert.Equal(2, CountOccurrences(text, SelectedFileExportService.ContentSeparator));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}