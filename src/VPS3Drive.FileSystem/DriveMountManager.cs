using VPS3Drive.Core;

namespace VPS3Drive.FileSystem;

public sealed class DriveMountManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlyMountBackend _backend;
    private bool _backendMounted;
    private string? _mountedDrive;
    private string? _mountedProfile;

    public DriveMountManager(IReadOnlyMountBackend? backend = null) =>
        _backend = backend ?? new WinFspReadOnlyMountBackend();

    public MountStatus Status { get; private set; } = MountStatus.Stopped;
    public string? MountedDrive => _mountedDrive;
    public string? MountedProfile => _mountedProfile;

    public async Task<MountResult> MountAsync(StorageProfile profile, IRemoteFileSystem fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(fileSystem);
        profile.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status == MountStatus.Mounted)
            {
                if (string.Equals(_mountedProfile, profile.Name, StringComparison.OrdinalIgnoreCase))
                    return new MountResult(Status, $"Profile '{profile.Name}' is already mounted on {_mountedDrive}.");
                return new MountResult(Status, $"Another profile is already mounted on {_mountedDrive}.");
            }

            Status = MountStatus.Starting;
            var mountPoint = char.ToUpperInvariant(profile.DriveLetter) + ":";
            var nativeStatus = _backend.Mount(mountPoint, fileSystem);
            if (nativeStatus != 0)
            {
                Status = MountStatus.Failed;
                return new MountResult(Status, $"WinFsp mount failed on {mountPoint} (NTSTATUS 0x{nativeStatus:X8}). Verify WinFsp is installed and the drive letter is available.");
            }

            _backendMounted = true;
            _mountedDrive = mountPoint;
            _mountedProfile = profile.Name;
            Status = MountStatus.Mounted;
            return new MountResult(Status, $"Mounted bucket '{profile.Bucket}' read-only on {mountPoint}.");
        }
        catch (OperationCanceledException)
        {
            Status = MountStatus.Stopped;
            throw;
        }
        catch (Exception ex)
        {
            if (_backendMounted)
            {
                try { _backend.Unmount(); } catch { }
            }
            _backendMounted = false;
            _mountedDrive = null;
            _mountedProfile = null;
            Status = MountStatus.Failed;
            return new MountResult(Status, "Mount could not start: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MountResult> UnmountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status != MountStatus.Mounted || !_backendMounted)
                return new MountResult(Status, "No VPS3Drive volume is mounted.");

            Status = MountStatus.Stopping;
            _backend.Unmount();
            _backendMounted = false;
            var drive = _mountedDrive;
            _mountedDrive = null;
            _mountedProfile = null;
            Status = MountStatus.Stopped;
            return new MountResult(Status, $"Unmounted {drive}.");
        }
        catch (Exception ex)
        {
            _mountedDrive = null;
            _mountedProfile = null;
            _backendMounted = false;
            try { _backend.Unmount(); } catch { }
            Status = MountStatus.Failed;
            return new MountResult(Status, "Unmount failed: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnmountAsync().ConfigureAwait(false);
        _backend.Dispose();
        _gate.Dispose();
    }
}
