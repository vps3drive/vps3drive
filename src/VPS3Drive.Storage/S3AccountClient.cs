using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using VPS3Drive.Core;

namespace VPS3Drive.Storage;

public static class S3AccountClient
{
    public static IAmazonS3 CreateClient(StorageAccount account, AWSCredentials credentials, int timeoutSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(credentials);
        account.Validate();

        var region = string.IsNullOrWhiteSpace(account.Region) ? "us-east-1" : account.Region;
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = account.ForcePathStyle,
            AuthenticationRegion = region,
            Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)),
            UseHttp = !account.UseSsl
        };

        var serviceUrl = account.EffectiveServiceUrl;
        if (!string.IsNullOrWhiteSpace(serviceUrl))
            config.ServiceURL = serviceUrl;

        return new AmazonS3Client(credentials, config);
    }

    public static async Task<IReadOnlyList<string>> ListBucketsAsync(
        StorageAccount account,
        AWSCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account, credentials);
        var response = await client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        return response.Buckets
            .Select(b => b.BucketName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> ListFoldersAsync(
        StorageAccount account,
        AWSCredentials credentials,
        string bucket,
        string prefix = "",
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(account, credentials);
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix)
            ? string.Empty
            : prefix.TrimStart('/').TrimEnd('/') + "/";

        var request = new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = normalizedPrefix,
            Delimiter = "/",
            MaxKeys = 200
        };

        var response = await client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
        return (response.CommonPrefixes ?? [])
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
