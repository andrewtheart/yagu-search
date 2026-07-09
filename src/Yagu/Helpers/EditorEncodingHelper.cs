using System.Text;

namespace Yagu.Helpers;

/// <summary>
/// Pure helpers shared by the in-pane preview editor and the popped-out
/// <see cref="PreviewEditorWindow"/> for deciding whether edited text can be
/// written back with a file's original encoding.
/// </summary>
internal static class EditorEncodingHelper
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="text"/> contains characters that
    /// cannot be represented in <paramref name="encoding"/> (so a save would lose
    /// data). Mirrors the strict encoder-fallback probe used by the in-pane editor.
    /// </summary>
    public static bool HasUnencodableCharacters(string text, Encoding encoding)
    {
        try
        {
            var strict = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            strict.GetByteCount(text);
            return false;
        }
        catch (EncoderFallbackException)
        {
            return true;
        }
    }
}
