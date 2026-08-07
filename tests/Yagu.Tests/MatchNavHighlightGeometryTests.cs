using System.Diagnostics;
using System.Text;

namespace Yagu.Tests;

public sealed class MatchNavHighlightGeometryTests : IDisposable
{
    private const int ImageWidth = 500;
    private const int ImageHeight = 200;
    private const int ExpectedTermLength = 10;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "yagu-match-nav-geometry-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StrictGeometry_AcceptsOneTermSizedBoxInsideTheTextViewport()
    {
        Directory.CreateDirectory(_directory);
        WriteBitmap("valid.bmp", [(80, 50, 84, 18)]);
        WriteManifest(["valid.bmp"]);

        var result = RunAnalyzer();

        Assert.True(result.ExitCode == 0,
            $"Analyzer exited {result.ExitCode}.\n--- STDOUT ---\n{result.Stdout}\n--- STDERR ---\n{result.Stderr}");
        Assert.Contains("GEOMETRY\tPASS", result.Stdout);
        Assert.DoesNotContain("GEOMETRY\tFAIL", result.Stdout);
    }

    [Fact]
    public void StrictGeometry_RejectsGutterEdgeOversizeAndExtraHighlightedRegions()
    {
        Directory.CreateDirectory(_directory);
        WriteBitmap("gutter.bmp", [(20, 50, 84, 18)]);
        WriteBitmap("edge.bmp", [(80, 0, 84, 18)]);
        WriteBitmap("oversized.bmp", [(80, 50, 190, 18)]);
        WriteBitmap("multiple.bmp", [(80, 50, 84, 18), (250, 90, 84, 18)]);
        WriteManifest(["gutter.bmp", "edge.bmp", "oversized.bmp", "multiple.bmp"]);

        var result = RunAnalyzer();
        string output = result.Stdout + result.Stderr;

        Assert.True(result.ExitCode == 2,
            $"Analyzer exited {result.ExitCode}.\n--- STDOUT ---\n{result.Stdout}\n--- STDERR ---\n{result.Stderr}");
        Assert.Contains("GEOMETRY\tFAIL", output);
        Assert.Contains("left preview gutter", output);
        Assert.Contains("touches a preview edge", output);
        Assert.Contains("exceeds term-only maximum", output);
        Assert.Contains("expected one highlighted term, found 2 connected components", output);
    }

    [Fact]
    public void StrictGeometry_MultilineAllowanceAcceptsTwoValidRowComponents()
    {
        Directory.CreateDirectory(_directory);
        WriteBitmap("multiline-rows.bmp", [(80, 50, 84, 18), (80, 80, 84, 18)]);
        WriteBitmap("multiline-continuation.bmp", [(80, 50, 84, 18), (82, 86, 80, 2)]);
        WriteManifest(["multiline-rows.bmp", "multiline-continuation.bmp"]);

        var result = RunAnalyzer(maximumHighlightComponents: 2);

        Assert.True(result.ExitCode == 0,
            $"Analyzer exited {result.ExitCode}.\n--- STDOUT ---\n{result.Stdout}\n--- STDERR ---\n{result.Stderr}");
        Assert.Contains("GEOMETRY\tPASS", result.Stdout);
        Assert.Contains("components=2", result.Stdout);
    }

    [Fact]
    public void StrictGeometry_MultilineAllowanceStillRejectsExtraOrInvalidComponents()
    {
        Directory.CreateDirectory(_directory);
        WriteBitmap("three.bmp", [(80, 40, 84, 18), (80, 70, 84, 18), (80, 100, 84, 18)]);
        WriteBitmap("gutter.bmp", [(80, 50, 84, 18), (20, 90, 84, 18)]);
        WriteBitmap("misaligned.bmp", [(80, 50, 84, 18), (250, 90, 84, 2)]);
        WriteManifest(["three.bmp", "gutter.bmp", "misaligned.bmp"]);

        var result = RunAnalyzer(maximumHighlightComponents: 2);
        string output = result.Stdout + result.Stderr;

        Assert.True(result.ExitCode == 2,
            $"Analyzer exited {result.ExitCode}.\n--- STDOUT ---\n{result.Stdout}\n--- STDERR ---\n{result.Stderr}");
        Assert.Contains("expected at most 2 highlighted components, found 3", output);
        Assert.Contains("enters/touches the left preview gutter", output);
        Assert.Contains("is not aligned with the primary multiline marker", output);
    }

    private (int ExitCode, string Stdout, string Stderr) RunAnalyzer(int maximumHighlightComponents = 1)
    {
        string script = Path.Combine(FindSolutionRoot(), "scripts", "count-red-pixels.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" " +
                $"-Directory \"{_directory}\" -Pattern \"*.bmp\" " +
                $"-Manifest \"{Path.Combine(_directory, "navigation.tsv")}\" " +
                $"-ExpectedTermLength {ExpectedTermLength} " +
                $"-MaximumHighlightComponents {maximumHighlightComponents} -StrictGeometry",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start strict geometry analyzer.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Strict geometry analyzer timed out.");
        return (process.ExitCode, stdout, stderr);
    }

    private void WriteManifest(IEnumerable<string> fileNames)
    {
        var lines = new List<string>
        {
            "Screenshot\tOccurrence\tTotal\tFiles\tViewportX\tViewportY\tViewportWidth\tViewportHeight\tLabel"
                .Replace("\\t", "\t", StringComparison.Ordinal),
        };
        int occurrence = 2;
        foreach (string fileName in fileNames)
        {
            lines.Add(string.Join('\t',
                fileName,
                occurrence,
                20,
                4,
                0,
                0,
                ImageWidth,
                ImageHeight,
                $"Occurrence {occurrence}/20 (4 files)"));
            occurrence++;
        }
        File.WriteAllLines(Path.Combine(_directory, "navigation.tsv"), lines, new UTF8Encoding(false));
    }

    private void WriteBitmap(string fileName, IReadOnlyList<(int X, int Y, int Width, int Height)> boxes)
    {
        const int headerSize = 54;
        int pixelBytes = ImageWidth * ImageHeight * 4;
        byte[] bitmap = new byte[headerSize + pixelBytes];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        WriteInt32(bitmap, 2, bitmap.Length);
        WriteInt32(bitmap, 10, headerSize);
        WriteInt32(bitmap, 14, 40);
        WriteInt32(bitmap, 18, ImageWidth);
        WriteInt32(bitmap, 22, ImageHeight);
        bitmap[26] = 1;
        bitmap[28] = 32;
        WriteInt32(bitmap, 34, pixelBytes);

        for (int index = headerSize; index < bitmap.Length; index += 4)
        {
            bitmap[index] = 32;
            bitmap[index + 1] = 32;
            bitmap[index + 2] = 32;
            bitmap[index + 3] = 255;
        }

        foreach (var box in boxes)
        {
            for (int x = box.X; x < box.X + box.Width; x++)
            {
                SetPixel(bitmap, x, box.Y);
                SetPixel(bitmap, x, box.Y + box.Height - 1);
            }
            for (int y = box.Y; y < box.Y + box.Height; y++)
            {
                SetPixel(bitmap, box.X, y);
                SetPixel(bitmap, box.X + box.Width - 1, y);
            }
        }

        File.WriteAllBytes(Path.Combine(_directory, fileName), bitmap);
    }

    private static void SetPixel(byte[] bitmap, int x, int y)
    {
        int bottomUpY = ImageHeight - 1 - y;
        int offset = 54 + ((bottomUpY * ImageWidth + x) * 4);
        bitmap[offset] = 0;
        bitmap[offset + 1] = 69;
        bitmap[offset + 2] = 255;
        bitmap[offset + 3] = 255;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find Yagu.slnx.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}