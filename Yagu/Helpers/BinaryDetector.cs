namespace Yagu.Helpers;

using Yagu.Services;

/// <summary>
/// Detects binary files via known magic numbers, NUL byte presence, and a
/// suspicious-control-byte ratio in the first <see cref="SampleBytes"/> bytes.
/// </summary>
public static class BinaryDetector
{
    public const int SampleBytes = 8192;

    public static bool IsBinary(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return IsBinary(fs);
        }
        catch (Exception ex)
        {
            LogService.Instance.Verbose("BinaryDetector", $"Cannot read file, treating as binary: {filePath}", ex);
            return true;
        }
    }

    public static bool IsBinary(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[SampleBytes];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0) break;
            read += n;
        }
        if (read <= 0) return false;
        return IsBinary(buffer[..read]);
    }

    public static bool IsBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) return false;

        // 1. Cheap, unambiguous magic-number checks for common binary formats.
        if (HasBinaryMagic(sample)) return true;

        // 2. Any embedded NUL byte → binary.
        if (sample.IndexOf((byte)0) >= 0) return true;

        // 3. Heuristic: high ratio of control bytes outside tab/CR/LF.
        if (sample.Length >= 512)
        {
            int suspicious = 0;
            foreach (var b in sample)
            {
                if (b == 0x09 || b == 0x0A || b == 0x0D) continue;     // tab, LF, CR
                if (b >= 0x20 && b < 0x7F) continue;                    // printable ASCII
                if (b >= 0x80) continue;                                // possibly UTF-8 — don't penalize
                suspicious++;                                           // 0x01-0x08, 0x0B, 0x0C, 0x0E-0x1F, 0x7F
            }
            if (suspicious * 100 / sample.Length > 5) return true;
        }

        return false;
    }

    /// <summary>
    /// Detects if the given sample bytes start with a ZIP magic number (PK\x03\x04, PK\x05\x06, PK\x07\x08).
    /// Used to identify ZIP archives for search-inside-archive support.
    /// </summary>
    public static bool IsZipMagic(ReadOnlySpan<byte> s)
    {
        if (s.Length < 4) return false;
        return s[0] == 0x50 && s[1] == 0x4B && (s[2] == 0x03 || s[2] == 0x05 || s[2] == 0x07);
    }

    private static bool HasBinaryMagic(ReadOnlySpan<byte> s)
    {
        if (s.Length < 4) return false;

        // Gzip
        if (s[0] == 0x1F && s[1] == 0x8B) return true;
        // ZIP / JAR / docx / xlsx / odt (PK\x03\x04, PK\x05\x06, PK\x07\x08)
        if (s[0] == 0x50 && s[1] == 0x4B && (s[2] == 0x03 || s[2] == 0x05 || s[2] == 0x07)) return true;
        // PNG
        if (s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47) return true;
        // JPEG
        if (s[0] == 0xFF && s[1] == 0xD8 && s[2] == 0xFF) return true;
        // PDF "%PDF"
        if (s[0] == 0x25 && s[1] == 0x50 && s[2] == 0x44 && s[3] == 0x46) return true;
        // ELF "\x7FELF"
        if (s[0] == 0x7F && s[1] == 0x45 && s[2] == 0x4C && s[3] == 0x46) return true;
        // PE/DOS "MZ"
        if (s[0] == 0x4D && s[1] == 0x5A) return true;
        // 7z
        if (s.Length >= 6 && s[0] == 0x37 && s[1] == 0x7A && s[2] == 0xBC && s[3] == 0xAF && s[4] == 0x27 && s[5] == 0x1C) return true;
        // Zstandard
        if (s[0] == 0x28 && s[1] == 0xB5 && s[2] == 0x2F && s[3] == 0xFD) return true;
        // Mach-O 32-bit LE
        if (s[0] == 0xCE && s[1] == 0xFA && s[2] == 0xED && s[3] == 0xFE) return true;
        // Mach-O 64-bit LE
        if (s[0] == 0xCF && s[1] == 0xFA && s[2] == 0xED && s[3] == 0xFE) return true;
        // Mach-O fat / Java class
        if (s[0] == 0xCA && s[1] == 0xFE && s[2] == 0xBA && s[3] == 0xBE) return true;
        // SQLite (header begins with "SQLite f")
        if (s.Length >= 6 && s[0] == 0x53 && s[1] == 0x51 && s[2] == 0x4C && s[3] == 0x69 && s[4] == 0x74 && s[5] == 0x65) return true;
        // Bzip2 "BZh"
        if (s[0] == 0x42 && s[1] == 0x5A && s[2] == 0x68) return true;
        // XZ
        if (s.Length >= 6 && s[0] == 0xFD && s[1] == 0x37 && s[2] == 0x7A && s[3] == 0x58 && s[4] == 0x5A && s[5] == 0x00) return true;
        // RAR
        if (s.Length >= 7 && s[0] == 0x52 && s[1] == 0x61 && s[2] == 0x72 && s[3] == 0x21 && s[4] == 0x1A && s[5] == 0x07) return true;

        return false;
    }
}
