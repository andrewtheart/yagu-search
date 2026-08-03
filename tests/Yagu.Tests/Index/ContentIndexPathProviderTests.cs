using System;
using System.IO;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="DefaultContentIndexPathProvider"/> (plan §3.4/§9.2): default LocalAppData
/// resolution, custom-directory override with normalization, and scope-directory composition.
/// </summary>
public sealed class ContentIndexPathProviderTests
{
    [Fact]
    public void Default_UsesLocalAppDataFallback_WhenNoCustomDirectory()
    {
        var provider = new DefaultContentIndexPathProvider(configuredDirectory: null, localAppDataRoot: @"C:\LocalAppData");
        Assert.Equal(
            Path.Combine(@"C:\LocalAppData", DefaultContentIndexPathProvider.VendorDirectory, DefaultContentIndexPathProvider.DefaultSubdirectory),
            provider.IndexRoot);
    }

    [Fact]
    public void Default_WhitespaceCustomDirectory_FallsBack()
    {
        var provider = new DefaultContentIndexPathProvider("   ", @"C:\LocalAppData");
        Assert.EndsWith(DefaultContentIndexPathProvider.DefaultSubdirectory, provider.IndexRoot);
    }

    [Fact]
    public void Default_CustomDirectory_IsTrimmedAndUsed()
    {
        var provider = new DefaultContentIndexPathProvider(@"  D:\index-store  ", @"C:\LocalAppData");
        Assert.Equal(@"D:\index-store", provider.IndexRoot);
    }

    [Fact]
    public void GetScopeDirectory_CombinesRootAndScopeId()
    {
        var provider = new DefaultContentIndexPathProvider(@"D:\ix", @"C:\LocalAppData");
        Assert.Equal(@"D:\ix\abc123", provider.GetScopeDirectory("abc123"));
    }

    [Fact]
    public void GetScopeDirectory_RejectsEmptyScopeId()
    {
        var provider = new DefaultContentIndexPathProvider(@"D:\ix", @"C:\LocalAppData");
        Assert.Throws<ArgumentException>(() => provider.GetScopeDirectory(""));
    }

    [Fact]
    public void Constructor_RejectsEmptyLocalAppDataRoot()
        => Assert.Throws<ArgumentException>(() => new DefaultContentIndexPathProvider(null, ""));

    [Fact]
    public void Create_UsesRealLocalAppData()
    {
        var provider = DefaultContentIndexPathProvider.Create(null);
        Assert.Contains(DefaultContentIndexPathProvider.DefaultSubdirectory, provider.IndexRoot);
        Assert.True(Path.IsPathRooted(provider.IndexRoot));
    }
}
