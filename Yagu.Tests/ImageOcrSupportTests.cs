using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class ImageOcrSupportTests
{
    [Theory]
    [InlineData("photo.png")]
    [InlineData("scan.JPG")]
    [InlineData(@"C:\pics\a.jpeg")]
    [InlineData("b.bmp")]
    [InlineData("c.GIF")]
    [InlineData("d.tif")]
    [InlineData("e.tiff")]
    [InlineData("f.webp")]
    public void IsImageCandidate_ReturnsTrueForKnownImageExtensions(string path)
    {
        Assert.True(ImageOcrSupport.IsImageCandidate(path));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("code.cs")]
    [InlineData("noext")]
    [InlineData("")]
    [InlineData("trailingdot.")]
    public void IsImageCandidate_ReturnsFalseForNonImages(string path)
    {
        Assert.False(ImageOcrSupport.IsImageCandidate(path));
    }

    [Fact]
    public void IsImageCandidate_HonorsCustomExtensionSet()
    {
        var custom = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "heic" };

        Assert.True(ImageOcrSupport.IsImageCandidate("a.heic", custom));
        // A default image extension is not in the custom set, so it must be rejected.
        Assert.False(ImageOcrSupport.IsImageCandidate("a.png", custom));
    }

    [Fact]
    public void DefaultImageExtensions_AreDotlessAndLowercase()
    {
        foreach (var ext in ImageOcrSupport.DefaultImageExtensions)
        {
            Assert.False(ext.StartsWith('.'), $"'{ext}' should not start with a dot");
            Assert.Equal(ext.ToLowerInvariant(), ext);
        }
    }
}
