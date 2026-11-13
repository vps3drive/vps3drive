namespace VPS3Drive.Core;

public interface IWritableRemoteFileSystem : IRemoteFileSystem
{
    Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);
    Task WriteFileAsync(string relativePath, Stream content, CancellationToken cancellationToken = default);
    Task DeleteAsync(string relativePath, bool isDirectory, CancellationToken cancellationToken = default);
    Task RenameAsync(string oldRelativePath, string newRelativePath, bool isDirectory, CancellationToken cancellationToken = default);
}
