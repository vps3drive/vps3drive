using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using VPS3Drive.Core;

namespace VPS3Drive.Storage;

public sealed class S3ObjectStore : IObjectStore
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string? _storageClass;
    private readonly bool _useAes256;

    public S3ObjectStore(StorageProfile profile, AWSCredentials credentials, string? storageClass = null, bool useAes256 = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);
        profile.Validate();
        _bucket = profile.Bucket;
        _storageClass = string.IsNullOrWhiteSpace(storageClass) ? null : storageClass.Trim();
        _useAes256 = useAes256;

        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(profile.Region),
            ForcePathStyle = profile.ForcePathStyle,
            AuthenticationRegion = profile.Region
        };
        if (!string.IsNullOrWhiteSpace(profile.ServiceUrl))
            config.ServiceURL = profile.ServiceUrl;

        _client = new AmazonS3Client(credentials, config);
    }

    public async IAsyncEnumerable<ObjectEntry> ListAsync(
        string prefix,
        string? delimiter = "/",
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = prefix,
            Delimiter = delimiter
        };

        ListObjectsV2Response response;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);

            if (response.CommonPrefixes is not null)
            {
                foreach (var commonPrefix in response.CommonPrefixes)
                    yield return new ObjectEntry(commonPrefix, true, null, null, null);
            }

            if (response.S3Objects is not null)
            {
                foreach (var item in response.S3Objects)
                {
                    if (item.Key == prefix && item.Key.EndsWith('/'))
                        continue;
                    yield return new ObjectEntry(item.Key, item.Key.EndsWith('/'), item.Size, item.LastModified, item.ETag);
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var response = await _client.GetObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
        return new ResponseStream(response);
    }

    public async Task<byte[]> ReadRangeAsync(string key, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var request = new GetObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ByteRange = new ByteRange(offset, checked(offset + length - 1))
        };
        using var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(length);
        await response.ResponseStream.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public async Task PutAsync(string key, Stream content, string? contentType = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType ?? "application/octet-stream"
        };
        if (!string.IsNullOrWhiteSpace(_storageClass))
            request.StorageClass = S3StorageClass.FindValue(_storageClass);
        if (_useAes256)
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;

        using var transfer = new TransferUtility(_client);
        await transfer.UploadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        await _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            DestinationBucket = _bucket,
            DestinationKey = destinationKey
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class ResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        public ResponseStream(GetObjectResponse response) => _response = response;
        public override bool CanRead => _response.ResponseStream.CanRead;
        public override bool CanSeek => _response.ResponseStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _response.ResponseStream.Length;
        public override long Position { get => _response.ResponseStream.Position; set => _response.ResponseStream.Position = value; }
        public override void Flush() => _response.ResponseStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _response.ResponseStream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _response.ResponseStream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _response.ResponseStream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _response.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync()
        {
            _response.Dispose();
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
