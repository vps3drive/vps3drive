namespace VPS3Drive.Core;

public sealed record ObjectEntry(string Key, bool IsDirectory, long? Length, DateTimeOffset? LastModified, string? ETag);

public interface IObjectStore : IAsyncDisposable
{
    IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, string? delimiter = "/", CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task<byte[]> ReadRangeAsync(string key, long offset, int length, CancellationToken cancellationToken = default);
    Task PutAsync(string key, Stream content, string? contentType = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default);
}
