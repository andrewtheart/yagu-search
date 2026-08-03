using System.Security.Cryptography;
using Yagu.Services.Pdf;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Builds the <see cref="ExtractorFingerprint"/> for the bundled <c>pdftotext.exe</c> PDF-text extractor
/// (plan §7 Phase 4). The fingerprint is the exact identity of the extractor configuration that produced a
/// PDF-text posting namespace: the content hash of the <c>pdftotext.exe</c> binary plus the fixed
/// output-affecting options. Because it is compared by digest, upgrading or swapping the tool (a
/// byte-different binary that can produce byte-different text) yields a different digest, so every persisted
/// PDF source falls back to live extraction instead of being trusted for pruning. The SAME helper is used at
/// build time (to stamp the namespace) and at query time (to decide whether the stored namespace still
/// applies), so a match is exact.
/// </summary>
public static class PdfExtractorFingerprint
{
    // The output-affecting pdftotext options Yagu always passes (kept in lock-step with
    // PdfTextExtractor.ExtractAsync's ArgumentList: -enc UTF-8 -eol unix). Any change here or there that
    // affects the extracted text MUST change this set so the fingerprint (and thus trust) invalidates.
    private static readonly KeyValuePair<string, string>[] FixedOptions =
    [
        new("enc", "UTF-8"),
        new("eol", "unix"),
    ];

    /// <summary>
    /// Computes the current PDF-text extractor fingerprint from <paramref name="extractor"/>, or <c>null</c>
    /// when the tool cannot be located or hashed (in which case PDF extended-source pruning must not engage).
    /// </summary>
    public static ExtractorFingerprint? TryCompute(PdfTextExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        string? toolPath = extractor.ResolveToolPath();
        if (string.IsNullOrEmpty(toolPath))
            return null;

        string? exeHash = TryHashFile(toolPath);
        if (exeHash is null)
            return null;

        return new ExtractorFingerprint(
            SpecialSourceKind.PdfText,
            engineId: PdfTextExtractor.EngineId,
            engineVersion: string.Empty, // the exe content hash is the authoritative version identity
            runtime: "cpu",
            binaryHashes: [new ExtractorFileHash("exe", exeHash)],
            options: FixedOptions);
    }

    private static string? TryHashFile(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "PDF extended-source: could not hash pdftotext.exe at '{Path}' for the extractor fingerprint.", path);
            return null;
        }
    }
}
