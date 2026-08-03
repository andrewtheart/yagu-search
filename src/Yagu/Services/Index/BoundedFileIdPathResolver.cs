namespace Yagu.Services.Index;

/// <summary>Bounds volume-hint creation and every file-id path resolution on one replaceable I/O lane.</summary>
internal sealed class BoundedFileIdPathResolver : IFileIdPathResolver
{
    private readonly BoundedSynchronousIo<IFileIdPathResolver?> _factoryIo;
    private readonly BoundedSynchronousIo<string?> _resolveIo;
    private readonly IFileIdPathResolver _inner;
    private readonly CancellationToken _operationToken;

    private BoundedFileIdPathResolver(
        BoundedSynchronousIo<IFileIdPathResolver?> factoryIo,
        BoundedSynchronousIo<string?> resolveIo,
        IFileIdPathResolver inner,
        CancellationToken operationToken)
    {
        _factoryIo = factoryIo;
        _resolveIo = resolveIo;
        _inner = inner;
        _operationToken = operationToken;
    }

    public static BoundedFileIdPathResolver? ForRoot(
        string root,
        TimeSpan timeout,
        CancellationToken operationToken)
        => ForRoot(root, timeout, operationToken, FileIdPathResolver.ForRoot);

    internal static BoundedFileIdPathResolver? ForRoot(
        string root,
        TimeSpan timeout,
        CancellationToken operationToken,
        Func<string, IFileIdPathResolver?> resolverFactory)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);
        var factoryIo = new BoundedSynchronousIo<IFileIdPathResolver?>(timeout);
        try
        {
            if (!factoryIo.TryExecute(_ => resolverFactory(root), operationToken, out IFileIdPathResolver? inner)
                || inner is null)
            {
                factoryIo.Dispose();
                return null;
            }
            return new BoundedFileIdPathResolver(
                factoryIo,
                new BoundedSynchronousIo<string?>(timeout),
                inner,
                operationToken);
        }
        catch
        {
            factoryIo.Dispose();
            throw;
        }
    }

    public string? TryResolvePath(UsnFileIdentity identity)
        => _resolveIo.TryExecute(_ => _inner.TryResolvePath(identity), _operationToken, out string? path)
            ? path
            : null;

    public void Dispose()
    {
        _resolveIo.Dispose();
        if (_inner is IDisposable disposable)
            disposable.Dispose();
        _factoryIo.Dispose();
    }
}
