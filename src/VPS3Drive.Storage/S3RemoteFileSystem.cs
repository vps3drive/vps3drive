using VPS3Drive.Core;

namespace VPS3Drive.Storage;

public sealed class S3RemoteFileSystem : IWritableRemoteFileSystem, IRangeReadableFileSystem, IAsyncDisposable
{
    private readonly IObjectStore _store;
    private readonly ObjectPathMapper _paths;

    public S3RemoteFileSystem(IObjectStore store, string rootPrefix = "")
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = new ObjectPathMapper(rootPrefix);
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

    public Task<byte[]> ReadRangeAsync(string relativePath, long offset, int length, CancellationToken cancellationToken = default) =>
        _store.ReadRangeAsync(_paths.ToObjectKey(relativePath), offset, length, cancellationToken);

    public async Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath).TrimEnd('/') + "/";
        using var empty = new MemoryStream(Array.Empty<byte>());
        await _store.PutAsync(key, empty, "application/x-directory", cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string relativePath, Stream content, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath);
        await _store.PutAsync(key, content, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string relativePath, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var key = _paths.ToObjectKey(relativePath);
        if (!isDirectory)
        {
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
