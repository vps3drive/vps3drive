namespace VPS3Drive.Core;

public sealed record FileSystemEntry(
    string Name,
    bool IsDirectory,
    long Length,
    DateTimeOffset? LastModified,
    string? ETag);
