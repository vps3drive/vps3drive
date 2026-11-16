namespace VPS3Drive.Core;

public enum DriveMountType
{
    NetworkDrive,
    RemovableDrive
}

public sealed record MappedDrive
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string AccountId { get; init; }
    public required string BucketPath { get; init; }
    public char DriveLetter { get; init; } = 'Z';
    public DriveMountType MountType { get; init; } = DriveMountType.NetworkDrive;
    public bool ReadOnly { get; init; }
    public string? VolumeLabel { get; init; }
    public bool AutoMount { get; init; } = true;

    // Advanced Drive Properties
    public string DefaultStorageClass { get; init; } = "STANDARD";
    public bool UseServerSideEncryption { get; init; }
    public bool EnableTransferAcceleration { get; init; }
    public bool RequesterPays { get; init; }

    // Caching settings
    public bool CacheFileInfo { get; init; } = true;
    public bool CacheDirectoryListings { get; init; } = true;
    public bool CacheReadRequests { get; init; } = true;
    public int CacheReadMaxFileSizeMb { get; init; } = 32;
    public bool PrefetchSmallFiles { get; init; }
    public int PrefetchMaxFileSizeMb { get; init; } = 8;
    public int DiskCacheLimitMb { get; init; } = 512;
    public int CacheTtlSeconds { get; init; } = 300;
    public bool SoftDelete { get; init; }
    public string TrashPrefix { get; init; } = ".trash";

    public string BucketName
    {
        get
        {
            var trimmed = BucketPath.Trim().TrimStart('/');
            var slash = trimmed.IndexOf('/');
            return slash < 0 ? trimmed : trimmed[..slash];
        }
    }

    public string Prefix
    {
        get
        {
            var trimmed = BucketPath.Trim().TrimStart('/');
            var slash = trimmed.IndexOf('/');
            if (slash < 0)
                return string.Empty;
            return trimmed[(slash + 1)..].TrimStart('/');
        }
    }

    public string EffectiveVolumeLabel =>
        string.IsNullOrWhiteSpace(VolumeLabel) ? BucketName : VolumeLabel.Trim();

    public string DisplayType =>
        (MountType == DriveMountType.NetworkDrive ? "Network Drive" : "Removable Drive") +
        (ReadOnly ? " (Read-Only)" : string.Empty);

    public string DisplayDriveLetter => $"{char.ToUpperInvariant(DriveLetter)}:\\";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountId))
            throw new ArgumentException("Storage account is required.", nameof(AccountId));
        if (string.IsNullOrWhiteSpace(BucketName))
            throw new ArgumentException("Amazon S3 bucket is required.", nameof(BucketPath));
        if (!char.IsAsciiLetter(DriveLetter))
            throw new ArgumentException("Mapped drive letter must be an ASCII letter.", nameof(DriveLetter));
        if (CacheTtlSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(CacheTtlSeconds));
        if (DiskCacheLimitMb < 0)
            throw new ArgumentOutOfRangeException(nameof(DiskCacheLimitMb));
    }

    public StorageProfile ToStorageProfile(StorageAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new StorageProfile
        {
            Name = $"{account.Name} ({BucketPath})",
            Bucket = BucketName,
            Region = account.Region,
            ServiceUrl = account.EffectiveServiceUrl,
            CredentialProfile = account.AccountType == AccountType.AwsConfigFile ? account.CredentialProfile : null,
            CredentialReference = account.CredentialReference,
            ForcePathStyle = account.ForcePathStyle,
            DriveLetter = char.ToUpperInvariant(DriveLetter)
        };
    }
}
