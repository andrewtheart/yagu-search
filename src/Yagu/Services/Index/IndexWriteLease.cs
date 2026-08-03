using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>Raised when another process already owns the content-index storage write lease.</summary>
public sealed class IndexWriteBusyException : IOException
{
    public IndexWriteBusyException(string indexRoot)
        : base($"Another index operation is already running for '{indexRoot}'.")
    {
        IndexRoot = indexRoot;
    }

    public string IndexRoot { get; }
}

/// <summary>
/// Proof that this process owns the non-reentrant, cross-process write lease for one normalized index
/// storage root. Holding the open <c>.writer.lock</c> stream with <see cref="FileShare.None"/> serializes
/// every generation/segment/extended-source mutation across GUI and CLI processes; process death releases
/// it automatically. Acquisition is one non-blocking attempt — contention is a first-class busy result.
/// </summary>
internal sealed class IndexMutationContext : IDisposable
{
    private FileStream? _stream;

    private IndexMutationContext(string indexRoot, FileStream stream)
    {
        IndexRoot = NormalizeRoot(indexRoot);
        _stream = stream;
    }

    public string IndexRoot { get; }

    public static bool TryAcquire(IContentIndexPathProvider paths, out IndexMutationContext? context)
        => TryAcquire(paths, OpenLease, InitializeLease, recover: true, out context);

    internal static bool TryAcquire(
        IContentIndexPathProvider paths,
        Func<string, FileStream> opener,
        out IndexMutationContext? context)
        => TryAcquire(paths, opener, InitializeLease, recover: false, out context);

    internal static bool TryAcquire(
        IContentIndexPathProvider paths,
        Func<string, FileStream> opener,
        Action<FileStream, byte[]> initializer,
        bool recover,
        out IndexMutationContext? context)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(initializer);
        context = null;
        string root = NormalizeRoot(paths.IndexRoot);
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(root);
            string lockPath = Path.Combine(root, ".writer.lock");
            stream = opener(lockPath);
            string owner = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(owner);
            initializer(stream, bytes);
            context = new IndexMutationContext(root, stream);
            stream = null; // ownership transferred to the context
            if (recover)
            {
                try { IndexStorageRecovery.RecoverUnderLease(context, paths); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    YaguLog.For("ContentIndex").LogWarning(ex,
                        "Index crash recovery failed for storage root '{IndexRoot}'; continuing with the acquired lease.", root);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            context = null;
            return false;
        }
        catch
        {
            stream?.Dispose();
            context?.Dispose();
            context = null;
            throw;
        }
    }

    private static FileStream OpenLease(string lockPath)
        => new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 256, FileOptions.WriteThrough);

    private static void InitializeLease(FileStream stream, byte[] ownerBytes)
    {
        stream.SetLength(0);
        stream.Write(ownerBytes);
        stream.Flush(flushToDisk: true);
    }

    public static IndexMutationContext Acquire(IContentIndexPathProvider paths)
        => TryAcquire(paths, out IndexMutationContext? context)
            ? context!
            : throw new IndexWriteBusyException(NormalizeRoot(paths.IndexRoot));

    public void EnsureOwns(IContentIndexPathProvider paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (!string.Equals(IndexRoot, NormalizeRoot(paths.IndexRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The mutation context belongs to a different index storage root.");
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }

    internal static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)!;
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return root;
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed;
    }
}
