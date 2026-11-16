using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VPS3Drive.Storage;

public sealed class DiskLruCache : IDisposable
{
    public const int ChunkSize = 4 * 1024 * 1024; // 4 MB

    private sealed class CacheEntry
    {
        public required string Path { get; init; }
        public long Size { get; init; }
        public long Sequence { get; set; }
    }

    private readonly string _rootDir;
    private readonly object _lock = new();
    private readonly Dictionary<(string ObjectId, long ChunkIndex), CacheEntry> _entries = [];
    private readonly ConcurrentDictionary<(string ObjectId, long ChunkIndex), byte> _inflightPrefetch = new();
    private long _totalBytes;
    private long _seqCounter;
    private long _maxBytes;
    private bool _enabled;

    public DiskLruCache(string? rootDir = null, int maxMb = 2048, bool enabled = true)
    {
        _rootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPS3Drive",
            "dotnet-cache");
        Directory.CreateDirectory(_rootDir);
        CleanStaleFiles();
        _maxBytes = Math.Max(0, (long)maxMb) * 1024L * 1024L;
        _enabled = enabled;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_lock)
                return _enabled && _maxBytes > 0;
        }
    }

    public void SetLimits(int maxMb, bool enabled)
    {
        lock (_lock)
        {
            _maxBytes = Math.Max(0, (long)maxMb) * 1024L * 1024L;
            _enabled = enabled;
            if (!enabled)
                ClearInternal();
            else
                EvictToLimit();
        }
    }

    public (long TotalBytes, int ChunkCount) GetStats()
    {
        lock (_lock)
            return (_totalBytes, _entries.Count);
    }

    public byte[]? GetChunk(string bucket, string key, long chunkIndex)
    {
        var objId = $"{bucket}/{key}";
        string? path;
        lock (_lock)
        {
            if (!_enabled || !_entries.TryGetValue((objId, chunkIndex), out var entry))
                return null;
            entry.Sequence = ++_seqCounter;
            path = entry.Path;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch
        {
            lock (_lock)
            {
                if (_entries.Remove((objId, chunkIndex), out var removed))
                    _totalBytes = Math.Max(0, _totalBytes - removed.Size);
            }
            return null;
        }
    }

    public void PutChunk(string bucket, string key, long chunkIndex, byte[] data)
    {
        var objId = $"{bucket}/{key}";
        lock (_lock)
        {
            if (!_enabled || _maxBytes <= 0 || data.LongLength > _maxBytes)
                return;
        }

        var path = ChunkPath(objId, chunkIndex);
        try
        {
            File.WriteAllBytes(path, data);
        }
        catch
        {
            return;
        }

        lock (_lock)
        {
            if (!_enabled)
            {
                try { File.Delete(path); } catch { }
                return;
            }
            if (_entries.TryGetValue((objId, chunkIndex), out var old))
                _totalBytes = Math.Max(0, _totalBytes - old.Size);

            _entries[(objId, chunkIndex)] = new CacheEntry
            {
                Path = path,
                Size = data.LongLength,
                Sequence = ++_seqCounter
            };
            _totalBytes += data.LongLength;
            EvictToLimit();
        }
    }

    public void InvalidateKey(string bucket, string key)
    {
        var objId = $"{bucket}/{key}";
        lock (_lock)
        {
            var toRemove = _entries.Keys.Where(k => k.ObjectId == objId).ToList();
            foreach (var k in toRemove)
            {
                if (_entries.Remove(k, out var entry))
                {
                    _totalBytes = Math.Max(0, _totalBytes - entry.Size);
                    try { File.Delete(entry.Path); } catch { }
                }
            }
        }
    }

    public void InvalidatePrefix(string bucket, string prefix)
    {
        var objPrefix = $"{bucket}/{prefix.TrimEnd('/')}";
        var slashPrefix = objPrefix + "/";
        lock (_lock)
        {
            var toRemove = _entries.Keys
                .Where(k => k.ObjectId == objPrefix || k.ObjectId.StartsWith(slashPrefix, StringComparison.Ordinal))
                .ToList();
            foreach (var k in toRemove)
            {
                if (_entries.Remove(k, out var entry))
                {
                    _totalBytes = Math.Max(0, _totalBytes - entry.Size);
                    try { File.Delete(entry.Path); } catch { }
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
            ClearInternal();
    }

    public bool TryBeginPrefetch(string bucket, string key, long chunkIndex)
    {
        var objId = $"{bucket}/{key}";
        lock (_lock)
        {
            if (!_enabled || _entries.ContainsKey((objId, chunkIndex)))
                return false;
        }
        return _inflightPrefetch.TryAdd((objId, chunkIndex), 0);
    }

    public void EndPrefetch(string bucket, string key, long chunkIndex) =>
        _inflightPrefetch.TryRemove(($"{bucket}/{key}", chunkIndex), out _);

    private string ChunkPath(string objId, long chunkIndex)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(objId)))[..24];
        return Path.Combine(_rootDir, $"{hash}_{chunkIndex}.chk");
    }

    private void ClearInternal()
    {
        foreach (var entry in _entries.Values)
        {
            try { File.Delete(entry.Path); } catch { }
        }
        _entries.Clear();
        _totalBytes = 0;
    }

    private void EvictToLimit()
    {
        while (_totalBytes > _maxBytes && _entries.Count > 0)
        {
            var lru = _entries.MinBy(kv => kv.Value.Sequence);
            if (_entries.Remove(lru.Key, out var entry))
            {
                _totalBytes = Math.Max(0, _totalBytes - entry.Size);
                try { File.Delete(entry.Path); } catch { }
            }
            else
            {
                break;
            }
        }
    }

    private void CleanStaleFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_rootDir, "*.chk"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    public void Dispose() => Clear();
}
