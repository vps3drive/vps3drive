namespace VPS3Drive.Core;

public enum AccountType
{
    AmazonS3,
    S3Compatible,
    AwsConfigFile
}

public enum SignatureVersion
{
    V4,
    V2
}

public sealed record StorageAccount
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public AccountType AccountType { get; init; } = AccountType.AmazonS3;
    public string Region { get; init; } = "us-east-1";
    public string? RestEndpoint { get; init; }
    public string? RegionSpecificEndpoint { get; init; }
    public bool UseSsl { get; init; } = true;
    public bool ForcePathStyle { get; init; }
    public SignatureVersion SignatureVersion { get; init; } = SignatureVersion.V4;
    public string? CredentialProfile { get; init; }
    public string? ConfigFilePath { get; init; }
    public string? CredentialReference { get; init; }

    public string DisplayType => AccountType switch
    {
        AccountType.AmazonS3 => "Amazon S3",
        AccountType.S3Compatible => "S3 Compatible Storage",
        AccountType.AwsConfigFile => "AWS Config / Credentials File",
        _ => "Unknown"
    };

    public string? EffectiveServiceUrl
    {
        get
        {
            var raw = !string.IsNullOrWhiteSpace(RestEndpoint)
                ? RestEndpoint.Trim()
                : (!string.IsNullOrWhiteSpace(RegionSpecificEndpoint)
                    ? RegionSpecificEndpoint.Trim().Replace("{region-code}", Region, StringComparison.OrdinalIgnoreCase)
                    : null);

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                raw = (UseSsl ? "https://" : "http://") + raw;
            }
            return raw;
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Account name is required.", nameof(Name));

        if (AccountType == AccountType.S3Compatible && string.IsNullOrWhiteSpace(RestEndpoint))
            throw new ArgumentException("REST Endpoint is required for S3 Compatible accounts.", nameof(RestEndpoint));

        var url = EffectiveServiceUrl;
        if (url is not null &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("REST Endpoint must be a valid HTTP or HTTPS URL.", nameof(RestEndpoint));
        }

        if (AccountType != AccountType.AwsConfigFile && string.IsNullOrWhiteSpace(Region))
            throw new ArgumentException("Region is required.", nameof(Region));
    }
}
