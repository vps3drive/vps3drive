using VPS3Drive.Core;

namespace VPS3Drive.Storage;

public sealed class S3RemoteFileSystem : IWritableRemoteFileSystem, IRangeReadableFileSystem, IAsyncDisposable
{
    private readonly IObjectStore _store;
    private readonly ObjectPathMapper _paths;
    private readonly string _rootPrefix;
    private readonly string _bucketName;
    private readonly DiskLruCache? _diskCache;
    private readonly WriteBackUploadQueue? _writeBackQueue;
    private readonly int _prefetchChunks;
    private readonly bool _softDelete;
    private readonly string _trashPrefix;

    public S3RemoteFileSystem(
        IObjectStore store,
        string rootPrefix = "",
        string bucketName = "",
        DiskLruCache? diskCache = null,
        WriteBackUploadQueue? writeBackQueue = null,
        int prefetchChunks = 2,
        bool softDelete = false,
        string trashPrefix = ".trash")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rootPrefix = rootPrefix.Trim().Trim('/');
        _paths = new ObjectPathMapper(rootPrefix);
        _bucketName = string.IsNullOrWhiteSpace(bucketName)
            ? (store is S3ObjectStore s3 ? s3.BucketName : "bucket")
            : bucketName;
        _diskCache = diskCache;
        _writeBackQueue = writeBackQueue;
        _prefetchChunks = Math.Clamp(prefetchChunks, 0, 8);
        _softDelete = softDelete;
        var cleanTrash = trashPrefix.Trim().Trim('/');
        _trashPrefix = string.IsNullOrWhiteSpace(cleanTrash) ? ".trash" : cleanTrash;
    }

    public IObjectStore Store => _store;
    public string BucketName => _bucketName;
    public string RootPrefix => _rootPrefix;
    public string TrashPrefix => _trashPrefix;

    public string ResolveObjectKey(string relativePath) => _paths.ToObjectKey(relativePath);

    public void PurgeCache(string relativePath)
    {
        var key = _paths.ToObjectKey(relativePath);
        _diskCache?.InvalidatePrefix(_bucketName, key);
    }

    private string TrashRootKey =>
        string.IsNullOrEmpty(_rootPrefix) ? _trashPrefix : $"{_rootPrefix}/{_trashPrefix}";

    private bool IsInTrash(string key)
    {
        var root = TrashRootKey;
        return key == root || key.StartsWith(root + "/", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var prefix = _paths.ToObjectKey(relativePath);
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";

        var entries = new List<FileSystemEntry>();
        await foreach (var entry in _store.ListAsync(prefix, "/", cancellationToken).ConfigureAwait(false))
        {
            if (entry.IsDirectory)
            {
                var childPrefix = entry.Key.TrimEnd('/');
                var relativeDirectory = _paths.ToRelativePath(childPrefix);
                var name = relativeDirectory.Split('/').LastOrDefault();
                if (!string.IsNullOrEmpty(name))
                    entries.Add(new FileSystemEntry(name, true, 0, entry.LastModified, entry.ETag));
                continue;
            }

            var relative = _paths.ToRelativePath(entry.Key);
            var nameOnly = relative.TrimEnd('/').Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(nameOnly))
                entries.Add(new FileSystemEntry(nameOnly, false, entry.Length ?? 0, entry.LastModified, entry.ETag));
        }

        return entries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<FileSystemEntry?> GetEntryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath is "." or "/" or "\\")
            return new FileSystemEntry(string.Empty, true, 0, null, null);

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var parent = Path.GetDirectoryName(normalized.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        var name = normalized.Split('/').LastOrDefault() ?? string.Empty;
        var entries = await ListDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        _store.OpenReadAsync(_paths.ToObjectKey(relativePath), cancellationToken);

    public async Task<byte[]> ReadRangeAsync(string relativePath, long offset, int length, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath);
        if (_diskCache is null || !_diskCache.IsEnabled || length <= 0)
            return await _store.ReadRangeAsync(key, offset, length, cancellationToken).ConfigureAwait(false);

        using var output = new MemoryStream(length);
        var remaining = length;
        var curOffset = offset;

        while (remaining > 0)
        {
            var chunkIndex = curOffset / DiskLruCache.ChunkSize;
            var inChunk = (int)(curOffset % DiskLruCache.ChunkSize);

            var chunk = _diskCache.GetChunk(_bucketName, key, chunkIndex);
            if (chunk is null)
            {
                var chunkStart = chunkIndex * DiskLruCache.ChunkSize;
                chunk = await _store.ReadRangeAsync(key, chunkStart, DiskLruCache.ChunkSize, cancellationToken).ConfigureAwait(false);
                if (chunk.Length > 0)
                    _diskCache.PutChunk(_bucketName, key, chunkIndex, chunk);
            }

            if (inChunk >= chunk.Length)
                break;

            var avail = chunk.Length - inChunk;
            var take = Math.Min(avail, remaining);
            output.Write(chunk, inChunk, take);
            remaining -= take;
            curOffset += take;

            if (_prefetchChunks > 0)
            {
                for (var ahead = 1; ahead <= _prefetchChunks; ahead++)
                {
                    var nextIdx = chunkIndex + ahead;
                    if (_diskCache.TryBeginPrefetch(_bucketName, key, nextIdx))
                    {
                        var nextStart = nextIdx * DiskLruCache.ChunkSize;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var data = await _store.ReadRangeAsync(key, nextStart, DiskLruCache.ChunkSize, CancellationToken.None).ConfigureAwait(false);
                                if (data.Length > 0)
                                    _diskCache.PutChunk(_bucketName, key, nextIdx, data);
                            }
                            catch { }
                            finally
                            {
                                _diskCache.EndPrefetch(_bucketName, key, nextIdx);
                            }
                        });
                    }
                }
            }

            if (chunk.Length < DiskLruCache.ChunkSize)
                break;
        }

        return output.ToArray();
    }

    public async Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath).TrimEnd('/') + "/";
        using var empty = new MemoryStream(Array.Empty<byte>());
        await _store.PutAsync(key, empty, "application/x-directory", cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string relativePath, Stream content, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath);
        _diskCache?.InvalidateKey(_bucketName, key);
        if (_writeBackQueue is not null)
            await _writeBackQueue.EnqueueAsync(_store, _bucketName, key, content, cancellationToken).ConfigureAwait(false);
        else
            await _store.PutAsync(key, content, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string relativePath, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath);
        _diskCache?.InvalidatePrefix(_bucketName, key);
        if (!isDirectory)
        {
            if (_softDelete && !IsInTrash(key))
            {
                var rel = _paths.ToRelativePath(key).TrimStart('/');
                var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
                var trashKey = $"{TrashRootKey}/{ts}_{rel}";
                await _store.CopyAsync(key, trashKey, cancellationToken).ConfigureAwait(false);
            }
            await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            return;
        }

        var prefix = key.TrimEnd('/') + "/";
        var keys = new List<string>();
        await foreach (var entry in _store.ListAsync(prefix, delimiter: null, cancellationToken).ConfigureAwait(false))
            keys.Add(entry.Key);
        keys.Add(prefix);

        foreach (var itemKey in keys.Distinct(StringComparer.Ordinal))
            await _store.DeleteAsync(itemKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(string oldRelativePath, string newRelativePath, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var oldKey = _paths.ToObjectKey(oldRelativePath);
        var newKey = _paths.ToObjectKey(newRelativePath);
        _diskCache?.InvalidatePrefix(_bucketName, oldKey);
        _diskCache?.InvalidatePrefix(_bucketName, newKey);
        if (!isDirectory)
        {
            await _store.CopyAsync(oldKey, newKey, cancellationToken).ConfigureAwait(false);
            await _store.DeleteAsync(oldKey, cancellationToken).ConfigureAwait(false);
            return;
        }

        var oldPrefix = oldKey.TrimEnd('/') + "/";
        var newPrefix = newKey.TrimEnd('/') + "/";
        var keys = new List<string>();
        await foreach (var entry in _store.ListAsync(oldPrefix, delimiter: null, cancellationToken).ConfigureAwait(false))
            keys.Add(entry.Key);

        foreach (var itemKey in keys)
        {
            var suffix = itemKey[oldPrefix.Length..];
            await _store.CopyAsync(itemKey, newPrefix + suffix, cancellationToken).ConfigureAwait(false);
            await _store.DeleteAsync(itemKey, cancellationToken).ConfigureAwait(false);
        }
        await _store.DeleteAsync(oldPrefix, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();
}
