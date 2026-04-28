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

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return Encoding.UTF8;
        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0 && bom[3] == 0)
            return Encoding.UTF32;
        if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }
}
