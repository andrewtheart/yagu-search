using System.Text;

namespace QuickGrep.Helpers;

/// <summary>
/// BOM sniffing with UTF-8 fallback.
/// </summary>
public static class EncodingDetector
{
    public static Encoding DetectEncoding(Stream stream)
    {
        if (!stream.CanSeek) return Encoding.UTF8;

        Span<byte> bom = stackalloc byte[4];
        long origin = stream.Position;
        int read = stream.Read(bom);
        stream.Position = origin;

        return DetectEncoding(bom[..read]);
    }

    /// <summary>
    /// Detect encoding from the first few bytes of a file header (BOM sniffing).
    /// Allows callers to share a single peek buffer for binary + encoding detection
    /// without re-reading the file.
    /// </summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            return Encoding.UTF8;
        if (header.Length >= 4 && header[0] == 0xFF && header[1] == 0xFE && header[2] == 0 && header[3] == 0)
            return Encoding.UTF32;
        if (header.Length >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 0xFE && header[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE)
            return Encoding.Unicode;
        if (header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }
}
