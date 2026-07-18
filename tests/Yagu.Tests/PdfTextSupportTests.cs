using Yagu.Services.Pdf;

namespace Yagu.Tests;

public class PdfTextSupportTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("REPORT.PDF")]
    [InlineData(@"C:\docs\Invoice.Pdf")]
    [InlineData("a.b.pdf")]
    public void IsPdfCandidate_TrueForPdfExtensions(string path)
    {
        Assert.True(PdfTextSupport.IsPdfCandidate(path));
    }

    [Theory]
    [InlineData("report.txt")]
    [InlineData("image.png")]
    [InlineData("archive.zip")]
    [InlineData("noext")]
    [InlineData("")]
    [InlineData("trailingdot.")]
    public void IsPdfCandidate_FalseForNonPdf(string path)
    {
        Assert.False(PdfTextSupport.IsPdfCandidate(path));
    }

    [Fact]
    public void IsPdfCandidate_UsesSuppliedExtensionSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xps" };
        Assert.True(PdfTextSupport.IsPdfCandidate("book.xps", set));
        Assert.False(PdfTextSupport.IsPdfCandidate("book.pdf", set));
    }

    [Fact]
    public void DefaultPdfExtensions_ContainsPdf_CaseInsensitive()
    {
        Assert.Contains("pdf", PdfTextSupport.DefaultPdfExtensions);
        Assert.Contains("PDF", PdfTextSupport.DefaultPdfExtensions);
    }
}
