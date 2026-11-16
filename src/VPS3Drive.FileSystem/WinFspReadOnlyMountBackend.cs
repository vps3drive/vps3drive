using VPS3Drive.Core;

namespace VPS3Drive.FileSystem;

public sealed class WinFspReadOnlyMountBackend : IReadOnlyMountBackend
{
    private ReadOnlyS3FileSystem? _filesystem;
    private readonly bool _readOnly;
    private readonly string _volumeLabel;

    public WinFspReadOnlyMountBackend(bool readOnly = true, string volumeLabel = "VPS3Drive S3")
    {
        _readOnly = readOnly;
        _volumeLabel = volumeLabel;
    }

    public int Mount(string mountPoint, IRemoteFileSystem fileSystem)
    {
        if (_filesystem is not null)
            throw new InvalidOperationException("This backend already owns a mounted filesystem.");

        var filesystem = new ReadOnlyS3FileSystem(fileSystem, _readOnly, _volumeLabel);
        try
        {
            var status = filesystem.Mount(mountPoint);
            if (status != 0)
            {
                filesystem.Unmount();
                return status;
            }

            _filesystem = filesystem;
            return status;
        }
        catch
        {
            filesystem.Unmount();
            throw;
        }
    }

    public void Unmount()
    {
        var filesystem = _filesystem;
        _filesystem = null;
        filesystem?.Unmount();
    }

    public void Dispose() => Unmount();
}
