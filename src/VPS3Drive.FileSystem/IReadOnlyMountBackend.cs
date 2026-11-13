using VPS3Drive.Core;

namespace VPS3Drive.FileSystem;

public interface IReadOnlyMountBackend : IDisposable
{
    int Mount(string mountPoint, IRemoteFileSystem fileSystem);
    void Unmount();
}
