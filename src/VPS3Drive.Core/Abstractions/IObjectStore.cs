namespace VPS3Drive.Core;

public sealed record ObjectEntry(string Key, bool IsDirectory, long? Length, DateTimeOffset? LastModified, string? ETag);

public sealed record ObjectVersionEntry(
    string Key,
    string VersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    DateTimeOffset? LastModified,
    long Size);

public interface IVersionedObjectStore
{
    Task<IReadOnlyList<ObjectVersionEntry>> ListVersionsAsync(string prefix, CancellationToken cancellationToken = default);
    Task RestoreVersionAsync(string key, string versionId, CancellationToken cancellationToken = default);
    Task DeleteVersionAsync(string key, string versionId, CancellationToken cancellationToken = default);
}

public interface IPresignableObjectStore
{
    string GeneratePresignedUrl(string key, TimeSpan expires);
    string GetPublicHttpsUrl(string key);
}

public interface IObjectStore : IAsyncDisposable
{
    IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, string? delimiter = "/", CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task<byte[]> ReadRangeAsync(string key, long offset, int length, CancellationToken cancellationToken = default);
    Task PutAsync(string key, Stream content, string? contentType = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default);
}
