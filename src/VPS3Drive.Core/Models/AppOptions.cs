namespace VPS3Drive.Core;

public enum ProxyMode
{
    NoProxy,
    SystemProxy,
    ManualProxy
}

public sealed record HttpHeaderRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string BucketPattern { get; init; } = string.Empty;
    public string FileMask { get; init; } = string.Empty;
    public required string HeaderName { get; init; }
    public string HeaderValue { get; init; } = string.Empty;
}

public sealed record AppOptions
{
    // General & File Transfer
    public bool EnableMultipartUploads { get; init; } = true;
    public int MultipartUploadPartSizeMb { get; init; } = 16;
    public bool EnableMultipartDownloads { get; init; } = true;
    public int MultipartDownloadPartSizeMb { get; init; } = 16;
    public bool InheritPermissionsFromParentBucket { get; init; }
    public bool PreservePermissionsOnOverwrite { get; init; }
    public bool AutoMountDrivesOnStartup { get; init; } = true;
    public bool StartWithWindows { get; init; } = true;
    public bool StartMinimizedOnWindowsStartup { get; init; } = true;
    public bool CheckForUpdatesOnStartup { get; init; } = true;

    // Proxy
    public ProxyMode ProxyMode { get; init; } = ProxyMode.SystemProxy;
    public string? ProxyServer { get; init; }
    public int ProxyPort { get; init; } = 8080;
    public bool ProxyRequiresAuth { get; init; }
    public string? ProxyUsername { get; init; }

    // Bandwidth Throttling
    public bool ThrottleUploads { get; init; }
    public int MaxUploadSpeedKbps { get; init; } = 1024;
    public bool ThrottleDownloads { get; init; }
    public int MaxDownloadSpeedKbps { get; init; } = 2048;

    // Data Integrity
    public bool VerifyUploadIntegrity { get; init; } = true;
    public bool VerifyDownloadIntegrity { get; init; } = true;

    // Queueing
    public int MaxConcurrentUploads { get; init; } = 4;
    public int MaxConcurrentDownloads { get; init; } = 4;
    public int RetryCount { get; init; } = 3;

    // Interface & Logging
    public string Theme { get; init; } = "Light";
    public bool MinimizeToTray { get; init; } = true;
    public string LogLevel { get; init; } = "Info";
    public int LogRetentionDays { get; init; } = 14;

    // Advanced & Performance
    public int RequestTimeoutSeconds { get; init; } = 60;
    public int ReadBufferSizeKb { get; init; } = 256;
    public bool DiskCacheEnabled { get; init; } = true;
    public int DiskCacheMaxMb { get; init; } = 2048;
    public bool WriteBackEnabled { get; init; } = true;
    public int PrefetchChunks { get; init; } = 2;
    public bool ContextMenuEnabled { get; init; } = true;

    // Default HTTP Headers
    public List<HttpHeaderRule> DefaultHttpHeaders { get; init; } = [];
}
