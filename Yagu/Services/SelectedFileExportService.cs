using System.Diagnostics.CodeAnalysis;
using System.Text;
using Yagu.Helpers;

namespace Yagu.Services;

public static class SelectedFileExportService
{
    public const string ContentSeparator = "---------------------------------------";

    public static string BuildPathListText(IEnumerable<string> filePaths)
    {
        return string.Join(Environment.NewLine, NormalizeFilePaths(filePaths));
    }

    public static async Task<string> BuildFilesWithContentTextAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        using var writer = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        await WriteFilesWithContentAsync(filePaths, writer, cancellationToken).ConfigureAwait(false);
        return writer.ToString();
    }

    public static async Task WritePathListAsync(IEnumerable<string> filePaths, TextWriter writer, CancellationToken cancellationToken = default)
    {
        bool first = true;
        foreach (var path in NormalizeFilePaths(filePaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                await writer.WriteLineAsync().ConfigureAwait(false);
            first = false;
            await writer.WriteAsync(path.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task WriteFilesWithContentAsync(IEnumerable<string> filePaths, TextWriter writer, CancellationToken cancellationToken = default)
    {
        bool first = true;
        foreach (var path in NormalizeFilePaths(filePaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!first)
                await writer.WriteLineAsync().ConfigureAwait(false);
            first = false;

            await WriteFileWithContentAsync(path, writer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteFileWithContentAsync(string filePath, TextWriter writer, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(filePath);
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(BuildFallbackHeader(filePath)).ConfigureAwait(false);
            await writer.WriteLineAsync(ContentSeparator).ConfigureAwait(false);
            await writer.WriteLineAsync($"[Could not inspect file: {ex.Message}]").ConfigureAwait(false);
            return;
        }

        await writer.WriteLineAsync(BuildHeader(info)).ConfigureAwait(false);
        await writer.WriteLineAsync(ContentSeparator).ConfigureAwait(false);

        if (!info.Exists)
        {
            await writer.WriteLineAsync("[File no longer exists.]").ConfigureAwait(false);
            return;
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int probeSize = (int)Math.Min(BinaryDetector.SampleBytes, Math.Max(0, info.Length));
            byte[] probe = probeSize > 0 ? new byte[probeSize] : [];
            int read = 0;
            if (probe.Length > 0)
                read = await stream.ReadAsync(probe.AsMemory(0, probe.Length), cancellationToken).ConfigureAwait(false);

            if (read > 0 && BinaryDetector.IsBinary(probe.AsSpan(0, read)))
            {
                await writer.WriteLineAsync("[Binary file skipped.]").ConfigureAwait(false);
                return;
            }

            var encoding = EncodingDetector.DetectEncoding(probe.AsSpan(0, Math.Min(read, 4)));
            stream.Position = 0;

            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024, leaveOpen: false);
            var buffer = new char[64 * 1024];
            while (true)
            {
                int charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (charsRead == 0)
                    break;

                await writer.WriteAsync(buffer.AsMemory(0, charsRead), cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteLineAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DecoderFallbackException)
        {
            await writer.WriteLineAsync($"[Could not read file content: {ex.Message}]").ConfigureAwait(false);
        }
    }

    private static string BuildHeader(FileInfo info)
    {
        var created = info.Exists ? FormatDateTime(info.CreationTime) : string.Empty;
        var modified = info.Exists ? FormatDateTime(info.LastWriteTime) : string.Empty;
        return $"{info.Name} {info.FullName} {created} {modified}";
    }

    private static string BuildFallbackHeader(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return $"{fileName} {filePath}".Trim();
    }

    private static string FormatDateTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<string> NormalizeFilePaths(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }
    }
}