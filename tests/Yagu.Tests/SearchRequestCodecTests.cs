using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class SearchRequestCodecTests
{
    [Fact]
    public void RoundTrip_AllFields_Preserved()
    {
        var original = new SearchRequest("C:\\folder", "needle", RunSearch: true);
        var encoded = SearchRequestCodec.Encode(original);

        Assert.True(SearchRequestCodec.TryDecode(encoded, out var decoded));
        Assert.Equal("C:\\folder", decoded.Directory);
        Assert.Equal("needle", decoded.Query);
        Assert.True(decoded.RunSearch);
    }

    [Fact]
    public void RoundTrip_RunFalse_Preserved()
    {
        var encoded = SearchRequestCodec.Encode(new SearchRequest("C:\\d", "q", RunSearch: false));
        Assert.True(SearchRequestCodec.TryDecode(encoded, out var decoded));
        Assert.False(decoded.RunSearch);
    }

    [Fact]
    public void Decode_NullTerminatedWirePayload_PreservesRunSearch()
    {
        var encoded = SearchRequestCodec.Encode(new SearchRequest("C:\\d", "q", RunSearch: true));

        Assert.True(SearchRequestCodec.TryDecode(encoded + '\0', out var decoded));
        Assert.True(decoded.RunSearch);
    }

    [Fact]
    public void Encode_StartsWithHeader()
    {
        var encoded = SearchRequestCodec.Encode(new SearchRequest("C:\\d", null, false));
        Assert.StartsWith(SearchRequestCodec.Header, encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_NullOrEmpty_ReturnsFalse()
    {
        Assert.False(SearchRequestCodec.TryDecode(null, out _));
        Assert.False(SearchRequestCodec.TryDecode(string.Empty, out _));
        Assert.False(SearchRequestCodec.TryDecode("\0\0", out _));
    }

    [Fact]
    public void Decode_WrongHeader_ReturnsFalse()
    {
        Assert.False(SearchRequestCodec.TryDecode("some-other-protocol/9\ndir=C:\\x", out _));
    }

    [Fact]
    public void RoundTrip_NullDirectoryAndQuery_DecodeAsNull()
    {
        var encoded = SearchRequestCodec.Encode(new SearchRequest(null, null, RunSearch: false));
        Assert.True(SearchRequestCodec.TryDecode(encoded, out var decoded));
        Assert.Null(decoded.Directory);
        Assert.Null(decoded.Query);
    }

    [Fact]
    public void RoundTrip_EmptyStrings_DecodeAsNull()
    {
        // Empty values are indistinguishable from "not provided" on the wire and decode to null.
        var encoded = SearchRequestCodec.Encode(new SearchRequest(string.Empty, string.Empty, false));
        Assert.True(SearchRequestCodec.TryDecode(encoded, out var decoded));
        Assert.Null(decoded.Directory);
        Assert.Null(decoded.Query);
    }

    [Fact]
    public void Encode_StripsEmbeddedNewlines_SoValuesCannotInjectExtraKeys()
    {
        // A query containing a newline that spells another key must NOT change the decoded directory.
        var original = new SearchRequest("C:\\d", "line1\nrun=1\ndir=C:\\evil", RunSearch: false);
        var encoded = SearchRequestCodec.Encode(original);

        Assert.True(SearchRequestCodec.TryDecode(encoded, out var decoded));
        Assert.Equal("C:\\d", decoded.Directory);
        Assert.False(decoded.RunSearch);
        Assert.DoesNotContain("\n", decoded.Query);
    }

    [Fact]
    public void Decode_UnknownKeys_AreIgnored()
    {
        var payload = SearchRequestCodec.Header + "\nfuture=1\ndir=C:\\d\nquery=q\nrun=1";
        Assert.True(SearchRequestCodec.TryDecode(payload, out var decoded));
        Assert.Equal("C:\\d", decoded.Directory);
        Assert.Equal("q", decoded.Query);
        Assert.True(decoded.RunSearch);
    }
}
