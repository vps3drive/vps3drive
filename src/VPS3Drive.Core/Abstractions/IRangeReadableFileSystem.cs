namespace VPS3Drive.Core;

public interface IRangeReadableFileSystem
{
    Task<byte[]> ReadRangeAsync(string relativePath, long offset, int length, CancellationToken cancellationToken = default);
}
