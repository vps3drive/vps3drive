using VPS3Drive.Core;
using VPS3Drive.FileSystem;
using Xunit;

namespace VPS3Drive.Core.Tests;

public sealed class DriveMountManagerTests
{
    [Fact]
    public async Task MountAsync_UsesBackendAndTracksMountedProfile()
    {
        var backend = new FakeMountBackend();
        await using var manager = new DriveMountManager(backend);
        var profile = new StorageProfile { Name = "demo", Bucket = "bucket", Region = "us-east-1" };

        var result = await manager.MountAsync(profile, new FakeRemoteFileSystem());

        Assert.Equal(MountStatus.Mounted, result.Status);
        Assert.Equal("V:", manager.MountedDrive);
        Assert.Equal("demo", manager.MountedProfile);
        Assert.Equal("V:", backend.MountPoint);
    }

    [Fact]
    public async Task MountAsync_RejectsConcurrentDifferentProfile()
    {
        var backend = new FakeMountBackend();
        await using var manager = new DriveMountManager(backend);
        var first = new StorageProfile { Name = "first", Bucket = "bucket-a", Region = "us-east-1" };
        var second = new StorageProfile { Name = "second", Bucket = "bucket-b", Region = "us-east-1", DriveLetter = 'W' };
        await manager.MountAsync(first, new FakeRemoteFileSystem());

        var result = await manager.MountAsync(second, new FakeRemoteFileSystem());

        Assert.Equal(MountStatus.Mounted, result.Status);
        Assert.Contains("already mounted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("V:", manager.MountedDrive);
        Assert.Equal(1, backend.MountCalls);
    }

    [Fact]
    public async Task UnmountAsync_CallsBackendAndClearsState()
    {
        var backend = new FakeMountBackend();
        await using var manager = new DriveMountManager(backend);
        await manager.MountAsync(new StorageProfile { Name = "demo", Bucket = "bucket", Region = "us-east-1" }, new FakeRemoteFileSystem());

        var result = await manager.UnmountAsync();

        Assert.Equal(MountStatus.Stopped, result.Status);
        Assert.Null(manager.MountedDrive);
        Assert.Equal(1, backend.UnmountCalls);
    }

    [Fact]
    public async Task MountAsync_ReportsBackendFailureAndDoesNotRemainMounted()
    {
        var backend = new FakeMountBackend { Status = unchecked((int)0xC0000034) };
        await using var manager = new DriveMountManager(backend);

        var result = await manager.MountAsync(new StorageProfile { Name = "demo", Bucket = "bucket", Region = "us-east-1" }, new FakeRemoteFileSystem());

        Assert.Equal(MountStatus.Failed, result.Status);
        Assert.Null(manager.MountedDrive);
        Assert.Equal(0, backend.UnmountCalls);
    }

    [Fact]
    public async Task UnmountAsync_WhenNotMountedIsSafe()
    {
        await using var manager = new DriveMountManager();

        var result = await manager.UnmountAsync();

        Assert.Equal(MountStatus.Stopped, result.Status);
        Assert.Equal(MountStatus.Stopped, manager.Status);
    }

    private sealed class FakeMountBackend : IReadOnlyMountBackend
    {
        public int Status { get; set; }
        public string? MountPoint { get; private set; }
        public int MountCalls { get; private set; }
        public int UnmountCalls { get; private set; }

        public int Mount(string mountPoint, IRemoteFileSystem fileSystem)
        {
            MountCalls++;
            MountPoint = mountPoint;
            return Status;
        }

        public void Unmount() => UnmountCalls++;
        public void Dispose() { }
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
