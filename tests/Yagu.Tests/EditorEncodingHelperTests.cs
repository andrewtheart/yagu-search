using System.Text;
using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class EditorEncodingHelperTests
{
    [Fact]
    public void HasUnencodableCharacters_AsciiInLatin1_IsFalse()
    {
        Assert.False(EditorEncodingHelper.HasUnencodableCharacters("plain ascii text", Encoding.Latin1));
    }

    [Fact]
    public void HasUnencodableCharacters_EmojiInLatin1_IsTrue()
    {
        Assert.True(EditorEncodingHelper.HasUnencodableCharacters("hello \U0001F600 world", Encoding.Latin1));
    }

    [Fact]
    public void HasUnencodableCharacters_AnyTextInUtf8_IsFalse()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Assert.False(EditorEncodingHelper.HasUnencodableCharacters("mixed \U0001F600 \u00e9 \u4e2d text", utf8));
    }

    [Fact]
    public void HasUnencodableCharacters_AccentInLatin1_IsFalse()
    {
        Assert.False(EditorEncodingHelper.HasUnencodableCharacters("caf\u00e9", Encoding.Latin1));
    }

    [Fact]
    public void HasUnencodableCharacters_CjkInLatin1_IsTrue()
    {
        Assert.True(EditorEncodingHelper.HasUnencodableCharacters("\u4e2d\u6587", Encoding.Latin1));
    }
}
