using VPS3Drive.Core;
using VPS3Drive.FileSystem;
using Xunit;

namespace VPS3Drive.Core.Tests;

public sealed class ReadOnlyS3FileSystemTests
{
    [Fact]
    public void MutatingOperationsReturnWriteProtected()
    {
        var fs = new ReadOnlyS3FileSystem(new FakeRemoteFileSystem());
        var fileInfo = default(Fsp.Interop.FileInfo);
        object fileNode = new();
        object fileDesc = new();

        Assert.Equal(ReadOnlyS3FileSystem.STATUS_MEDIA_WRITE_PROTECTED,
            fs.Overwrite(fileNode, fileDesc, 0, false, 0, out fileInfo));
        Assert.Equal(ReadOnlyS3FileSystem.STATUS_MEDIA_WRITE_PROTECTED,
            fs.SetFileSize(fileNode, fileDesc, 1, false, out fileInfo));
        Assert.Equal(ReadOnlyS3FileSystem.STATUS_MEDIA_WRITE_PROTECTED,
            fs.SetBasicInfo(fileNode, fileDesc, 0, 0, 0, 0, 0, out fileInfo));
        Assert.Equal(ReadOnlyS3FileSystem.STATUS_MEDIA_WRITE_PROTECTED,
            fs.CanDelete(fileNode, fileDesc, "a.txt"));
        Assert.Equal(ReadOnlyS3FileSystem.STATUS_MEDIA_WRITE_PROTECTED,
            fs.Rename(fileNode, fileDesc, "a.txt", "b.txt", false));
    }

    private sealed class FakeRemoteFileSystem : IRemoteFileSystem
    {
        public Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileSystemEntry>>(Array.Empty<FileSystemEntry>());

        public Task<FileSystemEntry?> GetEntryAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<FileSystemEntry?>(null);

        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }
}
