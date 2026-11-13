using System.Runtime.CompilerServices;
using VPS3Drive.Core;
using VPS3Drive.Storage;
using Xunit;

namespace VPS3Drive.Core.Tests;

public sealed class S3RemoteFileSystemTests
{
    [Fact]
    public async Task GetEntryAsync_ReturnsRootDirectory()
    {
        var fs = new S3RemoteFileSystem(new FakeObjectStore());

        var entry = await fs.GetEntryAsync("/");

        Assert.NotNull(entry);
        Assert.True(entry.IsDirectory);
        Assert.Equal(string.Empty, entry.Name);
    }

    [Fact]
    public async Task GetEntryAsync_ResolvesFileFromParentListing()
    {
        var store = new FakeObjectStore(
            new ObjectEntry("docs/a.txt", false, 3, DateTimeOffset.UnixEpoch, "etag"));
        var fs = new S3RemoteFileSystem(store);

        var entry = await fs.GetEntryAsync("docs/a.txt");

        Assert.NotNull(entry);
        Assert.False(entry.IsDirectory);
        Assert.Equal("a.txt", entry.Name);
        Assert.Equal(3, entry.Length);
    }

    [Fact]
    public async Task GetEntryAsync_ReturnsNullForMissingFile()
    {
        var fs = new S3RemoteFileSystem(new FakeObjectStore());

        Assert.Null(await fs.GetEntryAsync("missing.txt"));
    }

    private sealed class FakeObjectStore(params ObjectEntry[] entries) : IObjectStore
    {
        public string? LastPrefix { get; private set; }

        public async IAsyncEnumerable<ObjectEntry> ListAsync(string prefix, string? delimiter = "/", [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastPrefix = prefix;
            foreach (var entry in entries.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry;
                await Task.Yield();
            }
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<byte[]> ReadRangeAsync(string key, long offset, int length, CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<byte>());

        public Task PutAsync(string key, Stream content, string? contentType = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
