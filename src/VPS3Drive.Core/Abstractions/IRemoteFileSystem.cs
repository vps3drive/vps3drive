namespace VPS3Drive.Core;

public interface IRemoteFileSystem
{
    Task<IReadOnlyList<FileSystemEntry>> ListDirectoryAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<FileSystemEntry?> GetEntryAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);
}
