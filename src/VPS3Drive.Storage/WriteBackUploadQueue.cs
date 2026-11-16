using System.Threading.Channels;
using VPS3Drive.Core;

namespace VPS3Drive.Storage;

public sealed record UploadProgressInfo(
    string Id,
    string Bucket,
    string Key,
    long BytesDone,
    long BytesTotal,
    string Status,
    string? Error);

public sealed class WriteBackUploadQueue : IAsyncDisposable
{
    private sealed record UploadJob(
        string Id,
        IObjectStore Store,
        string Bucket,
        string Key,
        string StagingPath,
        long Size);

    private readonly Channel<UploadJob> _channel = Channel.CreateUnbounded<UploadJob>();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workers = [];
    private readonly string _stagingDir;

    public event Action<UploadProgressInfo>? TransferUpdated;

    public WriteBackUploadQueue(int workerCount = 4)
    {
        _stagingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VPS3Drive",
            "dotnet-staging");
        Directory.CreateDirectory(_stagingDir);

        var count = Math.Clamp(workerCount, 1, 8);
        for (var i = 0; i < count; i++)
            _workers.Add(Task.Run(WorkerLoopAsync));
    }

    public async Task EnqueueAsync(IObjectStore store, string bucket, string key, Stream content, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var stagingPath = Path.Combine(_stagingDir, $"wb-{id}.bin");
        await using (var fs = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }
        var size = new FileInfo(stagingPath).Length;
        TransferUpdated?.Invoke(new UploadProgressInfo(id, bucket, key, 0, size, "Queued", null));
        await _channel.Writer.WriteAsync(new UploadJob(id, store, bucket, key, stagingPath, size), cancellationToken).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
        {
            TransferUpdated?.Invoke(new UploadProgressInfo(job.Id, job.Bucket, job.Key, 0, job.Size, "Uploading", null));
            try
            {
                await using var fs = new FileStream(job.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await job.Store.PutAsync(job.Key, fs, null, _cts.Token).ConfigureAwait(false);
                TransferUpdated?.Invoke(new UploadProgressInfo(job.Id, job.Bucket, job.Key, job.Size, job.Size, "Done", null));
            }
            catch (Exception ex)
            {
                TransferUpdated?.Invoke(new UploadProgressInfo(job.Id, job.Bucket, job.Key, 0, job.Size, "Error", ex.Message));
            }
            finally
            {
                try { File.Delete(job.StagingPath); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
