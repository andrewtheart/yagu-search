using System.Text;
using Yagu.Services.Pdf;

namespace Yagu.Tests;

public class PdfTextExtractorTests
{
    [Fact]
    public void Id_IsPdftotext()
    {
        Assert.Equal("pdftotext", new PdfTextExtractor().Id);
        Assert.Equal("pdftotext", PdfTextExtractor.EngineId);
    }

    [Fact]
    public void ResolveToolPath_Override_ReturnsPathWhenItExists()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"yagu-fake-pdftotext-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        try
        {
            var extractor = new PdfTextExtractor(tmp);
            Assert.True(extractor.EnsureAvailable());
            Assert.Equal(tmp, extractor.ResolveToolPath());
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    [Fact]
    public void ResolveToolPath_Override_ReturnsNullWhenMissing()
    {
        var extractor = new PdfTextExtractor(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.exe"));
        Assert.Null(extractor.ResolveToolPath());
        Assert.False(extractor.EnsureAvailable());
    }

    [Fact]
    public void ResolveToolPath_EnvOverride_IsHonored()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"yagu-env-pdftotext-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        string? prev = Environment.GetEnvironmentVariable(PdfTextExtractor.ToolPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PdfTextExtractor.ToolPathEnvVar, tmp);
            var extractor = new PdfTextExtractor(); // no ctor override → reads the env var
            Assert.Equal(tmp, extractor.ResolveToolPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PdfTextExtractor.ToolPathEnvVar, prev);
            TryDelete(tmp);
        }
    }

    [Fact]
    public async Task ExtractAsync_MissingTool_FailsGracefully()
    {
        var extractor = new PdfTextExtractor(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.exe"));
        var result = await extractor.ExtractAsync("whatever.pdf", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Text);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExtractAsync_ReadsEmbeddedTextLayer_WithBundledPdftotext()
    {
        string? tool = LocateBundledPdftotext();
        if (tool is null) return; // environment-gated: bundled pdftotext.exe not present in this checkout

        string tmp = Path.Combine(Path.GetTempPath(), $"yagu-pdftest-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(tmp, BuildSimpleTextPdf("HELLO PDF WORLD"));
        try
        {
            var extractor = new PdfTextExtractor(tool);
            Assert.True(extractor.EnsureAvailable());

            var result = await extractor.ExtractAsync(tmp, CancellationToken.None);

            Assert.True(result.Success, result.Error);
            Assert.Contains("HELLO PDF WORLD", result.Text);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    [Fact]
    public async Task ExtractAsync_NonPdfInput_ReturnsFailure()
    {
        string? tool = LocateBundledPdftotext();
        if (tool is null) return; // environment-gated

        // A plain text file is not a valid PDF: pdftotext exits non-zero with no extractable text,
        // exercising the "exited with code N" failure branch.
        string notPdf = Path.Combine(Path.GetTempPath(), $"yagu-notpdf-{Guid.NewGuid():N}.pdf");
        await File.WriteAllTextAsync(notPdf, "this is plainly not a PDF file at all");
        try
        {
            var extractor = new PdfTextExtractor(tool);
            var result = await extractor.ExtractAsync(notPdf, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(string.Empty, result.Text);
            Assert.NotNull(result.Error);
        }
        finally
        {
            TryDelete(notPdf);
        }
    }

    [Fact]
    public async Task ExtractAsync_ToolPathIsNotExecutable_ReturnsStartFailure()
    {
        // A file that exists but is not a runnable executable: ResolveToolPath returns it, but
        // Process.Start throws, exercising the "failed to start" catch branch.
        string fakeTool = Path.Combine(Path.GetTempPath(), $"yagu-notexe-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(fakeTool, "not a real executable");
        try
        {
            var extractor = new PdfTextExtractor(fakeTool);
            var result = await extractor.ExtractAsync("whatever.pdf", CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
        }
        finally
        {
            TryDelete(fakeTool);
        }
    }

    [Fact]
    public async Task ExtractAsync_CallerCancellation_Throws()
    {
        string? tool = LocateBundledPdftotext();
        if (tool is null) return; // environment-gated

        string tmp = Path.Combine(Path.GetTempPath(), $"yagu-pdfcancel-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(tmp, BuildSimpleTextPdf("CANCEL ME"));
        try
        {
            var extractor = new PdfTextExtractor(tool);
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // already cancelled before the read

            // Caller cancellation propagates as an OperationCanceledException (not a Fail result).
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => extractor.ExtractAsync(tmp, cts.Token));
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static string? LocateBundledPdftotext()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Yagu", "NativeTools", "pdftotext", "pdftotext.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        string beside = Path.Combine(AppContext.BaseDirectory, "pdftotext", "pdftotext.exe");
        return File.Exists(beside) ? beside : null;
    }

    // Builds a minimal but structurally-valid single-page PDF whose only content is <paramref name="text"/>
    // rendered with the standard Helvetica font. xref offsets are computed exactly so pdftotext reads it
    // without needing its repair path.
    private static byte[] BuildSimpleTextPdf(string text)
    {
        string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        string streamData = $"BT /F1 24 Tf 72 700 Td ({Escape(text)}) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamData)} >>\nstream\n{streamData}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var ascii = Encoding.ASCII;
        using var ms = new MemoryStream();
        void W(string s) { byte[] b = ascii.GetBytes(s); ms.Write(b, 0, b.Length); }

        W("%PDF-1.4\n");
        var offsets = new long[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = ms.Length;
            W($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        long xref = ms.Length;
        W("xref\n");
        W($"0 {bodies.Length + 1}\n");
        W("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W("trailer\n");
        W($"<< /Size {bodies.Length + 1} /Root 1 0 R >>\n");
        W("startxref\n");
        W($"{xref}\n");
        W("%%EOF");

        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>
/// Source-pin over <c>PdfTextExtractor</c>: the pdftotext tool must be resolved only from an explicit
/// override or the app's own install directory — NEVER from a per-user-writable path such as
/// <c>%LOCALAPPDATA%</c>. Auto-executing a planted exe from a user-writable location would let
/// non-admin malware run inside Yagu's process tree (binary planting). Mirrors the OCR/semantic worker
/// probe-policy pin.
/// </summary>
public sealed class PdfTextExtractorSecuritySourceTests
{
    private static readonly string Source = File.ReadAllText(
        Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Pdf", "PdfTextExtractor.cs"));

    [Fact]
    public void ResolveToolPath_ProbesOverrideThenEnvThenBesideApp_AndNeverAUserWritablePath()
    {
        AssertContainsInOrder(Source,
            "if (_hasToolPathOverride)",
            "Environment.GetEnvironmentVariable(ToolPathEnvVar)",
            "AppContext.BaseDirectory, \"pdftotext\", \"pdftotext.exe\"");
        Assert.DoesNotContain("SpecialFolder.LocalApplicationData", Source);
        Assert.DoesNotContain("SpecialFolder.ApplicationData", Source);
    }

    [Fact]
    public void Extraction_UsesArgumentListNotStringConcatenation()
    {
        // ArgumentList quotes each token, avoiding a command-injection surface from crafted file names.
        Assert.Contains("psi.ArgumentList.Add(pdfPath);", Source);
        Assert.Contains("psi.ArgumentList.Add(\"-\");", Source);
        Assert.Contains("UseShellExecute = false", Source);
    }

    private static void AssertContainsInOrder(string haystack, params string[] needles)
    {
        int index = 0;
        foreach (string needle in needles)
        {
            int found = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Expected to find '{needle}' after index {index}.");
            index = found + needle.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
