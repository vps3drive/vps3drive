using System.Runtime.InteropServices;
using Fsp;
using Fsp.Interop;
using VPS3Drive.Core;
using WinFileInfo = Fsp.Interop.FileInfo;

namespace VPS3Drive.FileSystem;

/// <summary>Read-only WinFsp view over a remote object prefix.</summary>
public sealed class ReadOnlyS3FileSystem : FileSystemBase
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileDirectoryFile = 0x00000001;
    private readonly IRemoteFileSystem _remote;
    private readonly bool _readOnly;
    private readonly string _volumeLabel;
    private readonly DateTime _created = DateTime.UtcNow;
    private FileSystemHost? _host;

    public ReadOnlyS3FileSystem(IRemoteFileSystem remote, bool readOnly = true, string volumeLabel = "VPS3Drive S3")
    {
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _readOnly = readOnly;
        _volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "VPS3Drive S3" : volumeLabel.Trim();
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = new VolumeInfo
        {
            TotalSize = ulong.MaxValue,
            FreeSize = ulong.MaxValue
        };
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        try
        {
            var entry = _remote.GetEntryAsync(NormalizePath(fileName)).GetAwaiter().GetResult();
            if (entry is null)
            {
                fileAttributes = 0;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            fileAttributes = GetAttributes(entry);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            fileAttributes = 0;
            return ToNtStatus(ex);
        }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out WinFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = fileName;
        try
        {
            var isReadOnly = _readOnly || _remote is not IWritableRemoteFileSystem;
            if (isReadOnly && RequestsMutation(grantedAccess))
                return STATUS_MEDIA_WRITE_PROTECTED;
            var path = NormalizePath(fileName);
            var entry = _remote.GetEntryAsync(path).GetAwaiter().GetResult();
            if (entry is null)
                return STATUS_OBJECT_NAME_NOT_FOUND;
            fileNode = path;
            if (entry.IsDirectory)
            {
                fileDesc = new DirectoryCursor();
            }
            else if (!isReadOnly && RequestsMutationOrMax(grantedAccess))
            {
                fileDesc = new OpenFileBuffer(path, isDirectory: false, existingLength: entry.Length);
            }
            else
            {
                fileDesc = path;
            }
            fileInfo = CreateFileInfo(entry);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return ToNtStatus(ex);
        }
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        string path;
        if (fileDesc is OpenFileBuffer openBuffer)
        {
            lock (openBuffer.SyncRoot)
            {
                if (openBuffer.LoadedFromRemote || openBuffer.Dirty)
                {
                    if (offset >= (ulong)openBuffer.Stream.Length)
                        return STATUS_SUCCESS;
                    openBuffer.Stream.Seek((long)offset, SeekOrigin.Begin);
                    var count = (int)Math.Min(Math.Min(length, 1024 * 1024), (ulong)openBuffer.Stream.Length - offset);
                    var buf = new byte[count];
                    var read = openBuffer.Stream.Read(buf, 0, count);
                    Marshal.Copy(buf, 0, buffer, read);
                    bytesTransferred = (uint)read;
                    return STATUS_SUCCESS;
                }
                path = openBuffer.Path;
            }
        }
        else if (fileNode is string nodePath && fileDesc is string)
        {
            path = nodePath;
        }
        else
        {
            return STATUS_INVALID_HANDLE;
        }

        if (length == 0)
            return STATUS_SUCCESS;
        try
        {
            byte[] response;
            int capacity;
            if (_remote is IRangeReadableFileSystem rangeReadable)
            {
                capacity = checked((int)Math.Min(length, 1024 * 1024));
                response = rangeReadable.ReadRangeAsync(path, checked((long)offset), capacity).GetAwaiter().GetResult();
            }
            else
            {
                using var stream = _remote.OpenReadAsync(path).GetAwaiter().GetResult();
                if (offset >= (ulong)stream.Length)
                    return STATUS_SUCCESS;
                capacity = (int)Math.Min(Math.Min(length, 1024 * 1024), (ulong)stream.Length - offset);
                response = ReadFromStream(stream, checked((long)offset), capacity);
            }
            if (response.Length > capacity)
                return STATUS_UNEXPECTED_IO_ERROR;
            Marshal.Copy(response, 0, buffer, response.Length);
            bytesTransferred = (uint)response.Length;
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return ToNtStatus(ex);
        }
    }

    private static byte[] ReadFromStream(Stream stream, long offset, int count)
    {
        if (!stream.CanSeek && offset != 0)
            throw new NotSupportedException("The remote stream must support ranged reads for nonzero offsets.");
        if (stream.CanSeek)
            stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[count];
        var read = 0;
        while (read < bytes.Length)
        {
            var current = stream.Read(bytes, read, bytes.Length - read);
            if (current == 0)
                break;
            read += current;
        }
        return read == bytes.Length ? bytes : bytes[..read];
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out WinFileInfo fileInfo)
    {
        context = fileDesc;
        fileName = string.Empty;
        fileInfo = default;
        if (fileNode is not string directoryPath || fileDesc is not DirectoryCursor cursor)
            return false;

        try
        {
            if (cursor.Entries is null)
            {
                var listed = _remote.ListDirectoryAsync(directoryPath).GetAwaiter().GetResult();
                cursor.Entries = new[]
                {
                    new FileSystemEntry(".", true, 0, null, null),
                    new FileSystemEntry("..", true, 0, null, null)
                }.Concat(listed).ToArray();
            }

            var entries = cursor.Entries.ToArray();
            var start = cursor.Index;
            if (!string.IsNullOrEmpty(marker))
            {
                var markerIndex = Array.FindIndex(entries, candidate => string.Equals(candidate.Name, marker, StringComparison.OrdinalIgnoreCase));
                start = markerIndex >= 0 ? markerIndex + 1 : Array.FindIndex(entries, candidate => string.Compare(candidate.Name, marker, StringComparison.OrdinalIgnoreCase) > 0);
                if (start < 0)
                    start = entries.Length;
            }

            for (var index = start; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (!string.IsNullOrEmpty(pattern) && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, entry.Name, ignoreCase: true))
                    continue;
                fileName = entry.Name;
                fileInfo = CreateFileInfo(entry);
                cursor.Index = index + 1;
                return true;
            }
        }
        catch (Exception ex)
        {
            _lastEnumerationError = ex;
            return false;
        }
        return false;
    }

    private Exception? _lastEnumerationError;
    public Exception? LastEnumerationError => _lastEnumerationError;

    public override int GetFileInfo(object fileNode, object fileDesc, out WinFileInfo fileInfo)
    {
        fileInfo = default;
        if (fileDesc is OpenFileBuffer openBuffer)
        {
            lock (openBuffer.SyncRoot)
            {
                var size = (openBuffer.LoadedFromRemote || openBuffer.Dirty)
                    ? openBuffer.Stream.Length
                    : openBuffer.ExistingLength;
                fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(openBuffer.Path), false, size, DateTimeOffset.UtcNow, null));
                return STATUS_SUCCESS;
            }
        }
        if (fileNode is not string path)
            return STATUS_INVALID_HANDLE;

        try
        {
            var entry = _remote.GetEntryAsync(path).GetAwaiter().GetResult();
            if (entry is null)
                return STATUS_OBJECT_NAME_NOT_FOUND;
            fileInfo = CreateFileInfo(entry);
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return ToNtStatus(ex);
        }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out WinFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = fileName;
        if (_readOnly || _remote is not IWritableRemoteFileSystem writable)
            return STATUS_MEDIA_WRITE_PROTECTED;

        try
        {
            var path = NormalizePath(fileName);
            var isDir = (createOptions & FileDirectoryFile) != 0;
            if (isDir)
            {
                writable.CreateDirectoryAsync(path).GetAwaiter().GetResult();
                fileNode = path;
                fileDesc = new DirectoryCursor();
                fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(path), true, 0, DateTimeOffset.UtcNow, null));
                return STATUS_SUCCESS;
            }

            var buffer = new OpenFileBuffer(path, isDirectory: false, existingLength: 0)
            {
                LoadedFromRemote = true,
                Dirty = true
            };
            fileNode = path;
            fileDesc = buffer;
            fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(path), false, 0, DateTimeOffset.UtcNow, null));
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return ToNtStatus(ex);
        }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out WinFileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly || _remote is not IWritableRemoteFileSystem)
            return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileDesc is not OpenFileBuffer buffer)
            return STATUS_INVALID_HANDLE;

        lock (buffer.SyncRoot)
        {
            buffer.LoadedFromRemote = true;
            buffer.Stream.SetLength(0);
            buffer.Dirty = true;
            fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(buffer.Path), false, 0, DateTimeOffset.UtcNow, null));
            return STATUS_SUCCESS;
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out WinFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        if (_readOnly || _remote is not IWritableRemoteFileSystem)
            return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileDesc is not OpenFileBuffer openBuffer)
            return STATUS_INVALID_HANDLE;

        lock (openBuffer.SyncRoot)
        {
            EnsureLoadedForMutation(openBuffer);
            var targetOffset = writeToEndOfFile ? openBuffer.Stream.Length : checked((long)offset);
            if (constrainedIo && targetOffset >= openBuffer.Stream.Length)
                return STATUS_SUCCESS;

            var writeLen = constrainedIo
                ? (int)Math.Min(length, openBuffer.Stream.Length - targetOffset)
                : checked((int)length);

            if (writeLen > 0)
            {
                var managed = new byte[writeLen];
                Marshal.Copy(buffer, managed, 0, writeLen);
                openBuffer.Stream.Seek(targetOffset, SeekOrigin.Begin);
                openBuffer.Stream.Write(managed, 0, writeLen);
                openBuffer.Dirty = true;
                bytesTransferred = (uint)writeLen;
            }

            fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(openBuffer.Path), false, openBuffer.Stream.Length, DateTimeOffset.UtcNow, null));
            return STATUS_SUCCESS;
        }
    }

    public override int Flush(object fileNode, object fileDesc, out WinFileInfo fileInfo)
    {
        fileInfo = default;
        if (fileDesc is OpenFileBuffer openBuffer && !_readOnly && _remote is IWritableRemoteFileSystem writable)
        {
            try
            {
                FlushBuffer(openBuffer, writable);
                fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(openBuffer.Path), false, openBuffer.Stream.Length, DateTimeOffset.UtcNow, null));
                return STATUS_SUCCESS;
            }
            catch (Exception ex)
            {
                return ToNtStatus(ex);
            }
        }
        return GetFileInfo(fileNode, fileDesc, out fileInfo);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out WinFileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly || _remote is not IWritableRemoteFileSystem)
            return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileDesc is not OpenFileBuffer openBuffer)
            return STATUS_INVALID_HANDLE;

        lock (openBuffer.SyncRoot)
        {
            EnsureLoadedForMutation(openBuffer);
            if (!setAllocationSize)
            {
                openBuffer.Stream.SetLength(checked((long)newSize));
                openBuffer.Dirty = true;
            }
            fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(openBuffer.Path), false, openBuffer.Stream.Length, DateTimeOffset.UtcNow, null));
            return STATUS_SUCCESS;
        }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out WinFileInfo fileInfo)
    {
        fileInfo = default;
        if (_readOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;
        if (fileDesc is OpenFileBuffer openBuffer)
        {
            fileInfo = CreateFileInfo(new FileSystemEntry(Path.GetFileName(openBuffer.Path), false, openBuffer.Stream.Length, DateTimeOffset.UtcNow, null));
            return STATUS_SUCCESS;
        }
        return GetFileInfo(fileNode, fileDesc, out fileInfo);
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) =>
        _readOnly || _remote is not IWritableRemoteFileSystem ? STATUS_MEDIA_WRITE_PROTECTED : STATUS_SUCCESS;

    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
    {
        if (_readOnly || _remote is not IWritableRemoteFileSystem)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (fileDesc is OpenFileBuffer openBuffer)
            openBuffer.PendingDelete = deleteFile;
        else if (fileDesc is DirectoryCursor cursor)
            cursor.PendingDelete = deleteFile;
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (_readOnly || _remote is not IWritableRemoteFileSystem writable)
            return;

        try
        {
            if (fileDesc is OpenFileBuffer openBuffer)
            {
                if (openBuffer.PendingDelete)
                {
                    writable.DeleteAsync(openBuffer.Path, false).GetAwaiter().GetResult();
                    openBuffer.Dirty = false;
                }
                else
                {
                    FlushBuffer(openBuffer, writable);
                }
            }
            else if (fileDesc is DirectoryCursor cursor && cursor.PendingDelete && fileNode is string dirPath)
            {
                writable.DeleteAsync(dirPath, true).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        if (_readOnly || _remote is not IWritableRemoteFileSystem writable)
            return STATUS_MEDIA_WRITE_PROTECTED;

        try
        {
            var oldPath = NormalizePath(fileName);
            var newPath = NormalizePath(newFileName);
            var isDir = fileDesc is DirectoryCursor;
            writable.RenameAsync(oldPath, newPath, isDir).GetAwaiter().GetResult();
            return STATUS_SUCCESS;
        }
        catch (Exception ex)
        {
            return ToNtStatus(ex);
        }
    }

    public override int SetSecurity(object fileNode, object fileDesc, System.Security.AccessControl.AccessControlSections sections, byte[] securityDescriptor) =>
        _readOnly ? STATUS_MEDIA_WRITE_PROTECTED : STATUS_SUCCESS;

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileDesc is DirectoryCursor cursor)
            cursor.Dispose();
        else if (fileDesc is OpenFileBuffer openBuffer)
            openBuffer.Dispose();
    }

    private static void FlushBuffer(OpenFileBuffer openBuffer, IWritableRemoteFileSystem writable)
    {
        lock (openBuffer.SyncRoot)
        {
            if (!openBuffer.Dirty)
                return;
            openBuffer.Stream.Position = 0;
            writable.WriteFileAsync(openBuffer.Path, openBuffer.Stream).GetAwaiter().GetResult();
            openBuffer.Dirty = false;
        }
    }

    public int Mount(string mountPoint)
    {
        if (_host is not null)
            throw new InvalidOperationException("This filesystem is already mounted.");

        var host = new FileSystemHost(this)
        {
            FileSystemName = "VPS3Drive",
            CaseSensitiveSearch = false,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            PersistentAcls = false,
            NamedStreams = false,
            ReparsePoints = false,
            SectorSize = 4096,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength = 255,
            VolumeInfoTimeout = 1000,
            FileInfoTimeout = 1000,
            DirInfoTimeout = 1000
        };

        var status = host.Mount(mountPoint, null, false, 0);
        if (status != STATUS_SUCCESS)
        {
            host.Dispose();
            return status;
        }

        _host = host;
        return status;
    }

    public void Unmount()
    {
        var host = _host;
        _host = null;
        if (host is null)
            return;
        host.Unmount();
        host.Dispose();
    }

    private static string NormalizePath(string path) => path.TrimStart('\\', '/').Replace('\\', '/');

    private void EnsureLoadedForMutation(OpenFileBuffer openBuffer)
    {
        if (openBuffer.LoadedFromRemote)
            return;
        if (openBuffer.ExistingLength > 0)
        {
            using var stream = _remote.OpenReadAsync(openBuffer.Path).GetAwaiter().GetResult();
            stream.CopyTo(openBuffer.Stream);
            openBuffer.Stream.Position = 0;
        }
        openBuffer.LoadedFromRemote = true;
    }

    private static bool RequestsMutation(uint grantedAccess)
    {
        const uint writeMask = 0x00000116; // WRITE_DATA, APPEND_DATA, WRITE_EA, WRITE_ATTRIBUTES
        const uint delete = 0x00010000;
        const uint genericWrite = 0x40000000;
        const uint genericAll = 0x10000000;
        return (grantedAccess & (writeMask | delete | genericWrite | genericAll)) != 0;
    }

    private static bool RequestsMutationOrMax(uint grantedAccess)
    {
        const uint maximumAllowed = 0x02000000;
        return RequestsMutation(grantedAccess) || (grantedAccess & maximumAllowed) != 0;
    }

    private static uint GetAttributes(FileSystemEntry entry) =>
        entry.IsDirectory ? FileAttributeDirectory : FileAttributeNormal;

    private Fsp.Interop.FileInfo CreateFileInfo(FileSystemEntry entry)
    {
        var timestamp = entry.LastModified?.UtcDateTime ?? _created;
        var ticks = (ulong)timestamp.ToFileTimeUtc();
        return new WinFileInfo
        {
            FileAttributes = GetAttributes(entry),
            FileSize = entry.IsDirectory ? 0UL : (ulong)Math.Max(entry.Length, 0),
            AllocationSize = entry.IsDirectory ? 0UL : (ulong)Math.Max(entry.Length, 0),
            CreationTime = ticks,
            LastAccessTime = ticks,
            LastWriteTime = ticks,
            ChangeTime = ticks,
            IndexNumber = unchecked((ulong)(uint)(entry.ETag?.GetHashCode(StringComparison.Ordinal) ?? entry.Name.GetHashCode(StringComparison.Ordinal)))
        };
    }

    private static int ToNtStatus(Exception exception) => exception switch
    {
        FileNotFoundException => STATUS_OBJECT_NAME_NOT_FOUND,
        DirectoryNotFoundException => STATUS_OBJECT_PATH_NOT_FOUND,
        UnauthorizedAccessException => STATUS_ACCESS_DENIED,
        ArgumentException => STATUS_OBJECT_NAME_INVALID,
        _ => STATUS_UNEXPECTED_IO_ERROR
    };

    private sealed class DirectoryCursor : IDisposable
    {
        public IReadOnlyList<FileSystemEntry>? Entries { get; set; }
        public int Index { get; set; }
        public bool PendingDelete { get; set; }
        public void Dispose() => Entries = null;
    }

    private sealed class OpenFileBuffer : IDisposable
    {
        public string Path { get; }
        public bool IsDirectory { get; }
        public long ExistingLength { get; }
        public FileStream Stream { get; }
        public object SyncRoot { get; } = new();
        public bool LoadedFromRemote { get; set; }
        public bool Dirty { get; set; }
        public bool PendingDelete { get; set; }

        public OpenFileBuffer(string path, bool isDirectory, long existingLength)
        {
            Path = path;
            IsDirectory = isDirectory;
            ExistingLength = existingLength;
            var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpdrive-stage-" + Guid.NewGuid().ToString("N") + ".tmp");
            Stream = new FileStream(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.DeleteOnClose);
        }

        public void Dispose() => Stream.Dispose();
    }
}
