using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials;
using Microsoft.Win32;
using VPS3Drive.Core;
using VPS3Drive.FileSystem;
using VPS3Drive.Storage;

namespace VPS3Drive.App.Services;

public sealed record EventLogEntry(DateTime Timestamp, string Level, string Source, string Message)
{
    public string FormattedTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed record TransferTaskEntry(
    string Id,
    string FileName,
    string Direction,
    string Target,
    string SizeText,
    string ProgressText,
    string Status);

public sealed record MappedDriveRow(
    MappedDrive Drive,
    StorageAccount? Account,
    bool IsMounted,
    string StatusText)
{
    public string Id => Drive.Id;
    public string DriveLetter => Drive.DisplayDriveLetter;
    public string Bucket => Drive.BucketPath;
    public string AccountName => Account is null ? "(Missing Account)" : $"{Account.Name} ({Account.DisplayType})";
    public string DriveType => Drive.DisplayType;
    public string VolumeLabel => Drive.EffectiveVolumeLabel;
}

public sealed class AppStateService : IDisposable
{
    private readonly AccountStore _accountStore = new();
    private readonly MappedDriveStore _driveStore = new();
    private readonly OptionsStore _optionsStore = new();
    private readonly DpapiSecretStore _secretStore = new();
    private readonly Dictionary<char, DriveMountManager> _mounts = [];
    private readonly Dictionary<char, S3RemoteFileSystem> _mountedFs = [];
    private readonly DiskLruCache _diskCache = new();
    private WriteBackUploadQueue? _writeBackQueue;
    private CancellationTokenSource? _ipcCts;

    public event Action<MappedDrive, string>? OpenVersionsRequested;

    public List<StorageAccount> Accounts { get; } = [];
    public List<MappedDrive> Drives { get; } = [];
    public AppOptions Options { get; private set; } = new();

    public ObservableCollection<MappedDriveRow> DriveRows { get; } = [];
    public ObservableCollection<EventLogEntry> EventLogs { get; } = [];
    public ObservableCollection<TransferTaskEntry> Transfers { get; } = [];

    public bool IsServiceRunning { get; private set; } = true;

    public bool IsWinFspAvailable
    {
        get
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return File.Exists(Path.Combine(pf86, "WinFsp", "bin", "winfsp-x64.dll")) ||
                   File.Exists(Path.Combine(pf, "WinFsp", "bin", "winfsp-x64.dll"));
        }
    }

    public async Task InitializeAsync()
    {
        Options = await _optionsStore.LoadAsync();
        _diskCache.SetLimits(Options.DiskCacheMaxMb, Options.DiskCacheEnabled);
        _writeBackQueue = new WriteBackUploadQueue(Options.MaxConcurrentUploads);
        _writeBackQueue.TransferUpdated += OnTransferUpdated;
        _ipcCts = ExplorerContextMenuService.StartIpcServer(HandleContextMenuAction);
        Accounts.Clear();
        Accounts.AddRange(await _accountStore.LoadAsync());
        Drives.Clear();
        Drives.AddRange(await _driveStore.LoadAsync());

        RefreshDriveRows();
        SyncWindowsStartupRegistration(Options.StartWithWindows, Options.StartMinimizedOnWindowsStartup);
        SyncExplorerContextMenu(Options.ContextMenuEnabled);
        Log("Info", "System", IsWinFspAvailable
            ? "VPS3Drive initialized. WinFsp runtime detected."
            : "VPS3Drive initialized. WinFsp runtime not detected (install WinFsp to mount drives).");

        if (Options.AutoMountDrivesOnStartup && IsServiceRunning && IsWinFspAvailable)
        {
            await AutoMountDrivesAsync();
        }
    }

    public async Task AutoMountDrivesAsync()
    {
        foreach (var drive in Drives.Where(d => d.AutoMount).ToList())
        {
            try
            {
                await MountDriveAsync(drive);
            }
            catch (Exception ex)
            {
                Log("Error", "AutoMount", $"Failed to auto-mount {drive.DisplayDriveLetter}: {ex.Message}");
            }
        }
    }

    private static void SyncExplorerContextMenu(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                    ExplorerContextMenuService.Register(exePath);
            }
            else
            {
                ExplorerContextMenuService.Unregister();
            }
        }
        catch { }
    }

    private void SyncWindowsStartupRegistration(bool enabled, bool startMinimized)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "VPS3Drive";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                    key.SetValue(valueName, startMinimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log("Warning", "Startup", $"Could not update Windows startup registration: {ex.Message}");
        }
    }

    public void RefreshDriveRows()
    {
        DriveRows.Clear();
        foreach (var drive in Drives)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == drive.AccountId);
            var letter = char.ToUpperInvariant(drive.DriveLetter);
            var mounted = _mounts.TryGetValue(letter, out var mgr) && mgr.Status == MountStatus.Mounted;
            var status = mounted ? "Mounted" : "Stopped";
            DriveRows.Add(new MappedDriveRow(drive, account, mounted, status));
        }
    }

    public void Log(string level, string source, string message)
    {
        EventLogs.Insert(0, new EventLogEntry(DateTime.Now, level, source, message));
        while (EventLogs.Count > 1000)
            EventLogs.RemoveAt(EventLogs.Count - 1);
    }

    public async Task SaveAccountsAsync()
    {
        await _accountStore.SaveAllAsync(Accounts);
        RefreshDriveRows();
    }

    public async Task SaveOptionsAsync(AppOptions options)
    {
        Options = options;
        await _optionsStore.SaveAsync(options);
        _diskCache.SetLimits(options.DiskCacheMaxMb, options.DiskCacheEnabled);
        SyncWindowsStartupRegistration(options.StartWithWindows, options.StartMinimizedOnWindowsStartup);
        SyncExplorerContextMenu(options.ContextMenuEnabled);
        Log("Info", "Options", "Application options saved.");
    }

    public (long TotalBytes, int ChunkCount) GetDiskCacheStats() => _diskCache.GetStats();

    public void ClearDiskCache()
    {
        _diskCache.Clear();
        Log("Info", "Cache", "Disk LRU read cache cleared.");
    }

    public async Task<S3ObjectStore> CreateObjectStoreAsync(MappedDrive drive)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == drive.AccountId)
            ?? throw new InvalidOperationException("Storage account for this mapped drive no longer exists.");
        var credentials = await ResolveCredentialsAsync(account);
        var profile = drive.ToStorageProfile(account);
        return new S3ObjectStore(
            profile,
            credentials,
            drive.DefaultStorageClass,
            drive.UseServerSideEncryption,
            Options.MultipartUploadPartSizeMb,
            Options.MaxConcurrentUploads);
    }

    public void PurgeDriveCache(MappedDrive drive, string relativePath = "")
    {
        var letter = char.ToUpperInvariant(drive.DriveLetter);
        if (_mountedFs.TryGetValue(letter, out var fs))
            fs.PurgeCache(relativePath);
    }

    private void OnTransferUpdated(UploadProgressInfo info)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        dispatcher.BeginInvoke(() =>
        {
            var pct = info.BytesTotal > 0 ? (int)(info.BytesDone * 100 / info.BytesTotal) : (info.Status == "Done" ? 100 : 0);
            var sizeText = $"{info.BytesDone / 1024} / {info.BytesTotal / 1024} KB";
            var entry = new TransferTaskEntry(
                info.Id,
                info.Key,
                "Upload",
                info.Bucket,
                sizeText,
                $"{pct}%",
                info.Error is null ? info.Status : $"Error: {info.Error}");
            var existingIdx = -1;
            for (var i = 0; i < Transfers.Count; i++)
            {
                if (Transfers[i].Id == info.Id) { existingIdx = i; break; }
            }
            if (existingIdx >= 0)
                Transfers[existingIdx] = entry;
            else
            {
                Transfers.Insert(0, entry);
                while (Transfers.Count > 50)
                    Transfers.RemoveAt(Transfers.Count - 1);
            }
        });
    }

    private (bool Ok, string? Clipboard) HandleContextMenuAction(string action, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':')
                return (false, null);
            var letter = char.ToUpperInvariant(path[0]);
            var drive = Drives.FirstOrDefault(d => char.ToUpperInvariant(d.DriveLetter) == letter);
            if (drive is null || !_mountedFs.TryGetValue(letter, out var fs))
                return (false, null);

            var relPath = path.Length > 2 ? path[2..].TrimStart('\\', '/').Replace('\\', '/') : "";
            var s3Key = fs.ResolveObjectKey(relPath);
            var store = fs.Store as S3ObjectStore;

            switch (action)
            {
                case "presign_1h":
                case "presign_24h":
                case "presign_7d":
                {
                    if (store is null) return (false, null);
                    var expires = action switch
                    {
                        "presign_1h" => TimeSpan.FromHours(1),
                        "presign_24h" => TimeSpan.FromDays(1),
                        _ => TimeSpan.FromDays(7)
                    };
                    var url = store.GeneratePresignedUrl(s3Key, expires);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Log("Info", "ContextMenu", $"Copied Presigned URL ({action}) for s3://{fs.BucketName}/{s3Key}"));
                    return (true, url);
                }
                case "copy_s3_uri":
                {
                    var uri = $"s3://{fs.BucketName}/{s3Key}";
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Log("Info", "ContextMenu", $"Copied S3 URI: {uri}"));
                    return (true, uri);
                }
                case "copy_https_url":
                {
                    if (store is null) return (false, null);
                    var url = store.GetPublicHttpsUrl(s3Key);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Log("Info", "ContextMenu", $"Copied HTTPS URL: {url}"));
                    return (true, url);
                }
                case "purge_cache":
                {
                    fs.PurgeCache(relPath);
                    ExplorerContextMenuService.NotifyExplorerRefresh(path);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Log("Info", "ContextMenu", $"Purged cache & refreshed Explorer for {s3Key}"));
                    return (true, null);
                }
                case "open_versions":
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        OpenVersionsRequested?.Invoke(drive, s3Key));
                    return (true, null);
                }
                default:
                    return (false, null);
            }
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                Log("Error", "ContextMenu", $"Action '{action}' failed: {ex.Message}"));
            return (false, null);
        }
    }

    public async Task SaveDrivesAsync()
    {
        await _driveStore.SaveAllAsync(Drives);
        RefreshDriveRows();
    }

    public async Task SaveAccountSecretAsync(string reference, string accessKey, string secretKey)
    {
        var payload = JsonSerializer.Serialize(new StoredKeys(accessKey, secretKey));
        await _secretStore.SaveAsync(reference, payload);
    }

    public async Task DeleteAccountSecretAsync(string? reference)
    {
        if (!string.IsNullOrWhiteSpace(reference))
            await _secretStore.DeleteAsync(reference);
    }

    public async Task<AWSCredentials> ResolveCredentialsAsync(StorageAccount account)
    {
        if (account.AccountType == AccountType.AwsConfigFile)
        {
            if (!string.IsNullOrWhiteSpace(account.ConfigFilePath) && File.Exists(account.ConfigFilePath))
            {
                var shared = new SharedCredentialsFile(account.ConfigFilePath);
                var profileName = string.IsNullOrWhiteSpace(account.CredentialProfile) ? "default" : account.CredentialProfile;
                if (shared.TryGetProfile(profileName, out var profile) &&
                    AWSCredentialsFactory.TryGetAWSCredentials(profile, shared, out var creds))
                {
                    return creds;
                }
                throw new InvalidOperationException($"Profile '{profileName}' was not found in '{account.ConfigFilePath}'.");
            }

            var chain = new CredentialProfileStoreChain();
            if (!string.IsNullOrWhiteSpace(account.CredentialProfile))
            {
                if (chain.TryGetAWSCredentials(account.CredentialProfile, out var profileCreds))
                    return profileCreds;
                throw new InvalidOperationException($"AWS profile '{account.CredentialProfile}' was not found.");
            }
            return DefaultAWSCredentialsIdentityResolver.GetCredentials();
        }

        if (string.IsNullOrWhiteSpace(account.CredentialReference))
            throw new InvalidOperationException("No saved Access Key / Secret Access Key found for this account.");

        var json = await _secretStore.GetAsync(account.CredentialReference)
            ?? throw new InvalidOperationException("Saved credentials could not be decrypted or are missing.");

        var keys = JsonSerializer.Deserialize<StoredKeys>(json)
            ?? throw new InvalidOperationException("Stored credential payload is invalid.");

        return new BasicAWSCredentials(keys.AccessKey, keys.SecretKey);
    }

    public async Task<(bool Ok, string Message)> TestAccountAsync(StorageAccount account, string? overrideAccessKey = null, string? overrideSecretKey = null)
    {
        try
        {
            AWSCredentials credentials;
            if (!string.IsNullOrWhiteSpace(overrideAccessKey) && !string.IsNullOrWhiteSpace(overrideSecretKey))
                credentials = new BasicAWSCredentials(overrideAccessKey, overrideSecretKey);
            else
                credentials = await ResolveCredentialsAsync(account);

            var buckets = await S3AccountClient.ListBucketsAsync(account, credentials);
            Log("Info", "Accounts", $"Connection test succeeded for '{account.Name}' ({buckets.Count} buckets visible).");
            return (true, $"Connection succeeded. {buckets.Count} bucket(s) found.");
        }
        catch (Exception ex)
        {
            Log("Error", "Accounts", $"Connection test failed for '{account.Name}': {ex.Message}");
            return (false, ex.Message);
        }
    }

    public async Task MountDriveAsync(MappedDrive drive)
    {
        if (!IsServiceRunning)
            throw new InvalidOperationException("VPS3Drive Service is currently stopped. Start the service first.");

        EnsureWinFspInstalled();

        var account = Accounts.FirstOrDefault(a => a.Id == drive.AccountId)
            ?? throw new InvalidOperationException("Storage account for this mapped drive no longer exists.");

        var letter = char.ToUpperInvariant(drive.DriveLetter);
        if (_mounts.TryGetValue(letter, out var existing) && existing.Status == MountStatus.Mounted)
            return;

        var credentials = await ResolveCredentialsAsync(account);
        var profile = drive.ToStorageProfile(account);
        var store = new S3ObjectStore(
            profile,
            credentials,
            drive.DefaultStorageClass,
            drive.UseServerSideEncryption,
            Options.MultipartUploadPartSizeMb,
            Options.MaxConcurrentUploads);
        var fs = new S3RemoteFileSystem(
            store,
            drive.Prefix,
            drive.BucketName,
            _diskCache,
            Options.WriteBackEnabled ? _writeBackQueue : null,
            Options.PrefetchChunks,
            drive.SoftDelete,
            drive.TrashPrefix);

        var manager = new DriveMountManager(new WinFspReadOnlyMountBackend(drive.ReadOnly, drive.EffectiveVolumeLabel));
        var result = await manager.MountAsync(profile, fs);
        if (result.Status == MountStatus.Mounted)
            _mountedFs[letter] = fs;
        if (result.Status != MountStatus.Mounted)
        {
            await manager.DisposeAsync();
            throw new InvalidOperationException(result.Message);
        }

        _mounts[letter] = manager;
        Log("Info", "Drives", $"Mounted {drive.DisplayDriveLetter} -> {drive.BucketPath} ({drive.DisplayType}).");
        RefreshDriveRows();
    }

    private static bool CheckWinFspInstalled()
    {
        using var wowKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp");
        if (wowKey is not null)
            return true;
        using var nativeKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
        if (nativeKey is not null)
            return true;
        var dllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll");
        return File.Exists(dllPath);
    }

    private static void EnsureWinFspInstalled()
    {
        if (CheckWinFspInstalled())
            return;

        var bundledMsi = Path.Combine(AppContext.BaseDirectory, "winfsp.msi");
        if (File.Exists(bundledMsi))
        {
            var install = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show(
                    "WinFsp filesystem driver is required to mount virtual drives.\n\nInstall the bundled WinFsp driver now?",
                    "WinFsp Required",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information));

            if (install == System.Windows.MessageBoxResult.Yes)
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{bundledMsi}\"",
                    UseShellExecute = true
                });
                proc?.WaitForExit();
                if (CheckWinFspInstalled())
                    return;
            }
        }
        else
        {
            var openUrl = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show(
                    "WinFsp filesystem driver is required to mount virtual drives.\n\nWould you like to open the WinFsp download page now?",
                    "WinFsp Required",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning));

            if (openUrl == System.Windows.MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo("https://winfsp.dev/rel/") { UseShellExecute = true });
            }
        }

        throw new InvalidOperationException("WinFsp runtime is not installed. Please install WinFsp and try mounting again.");
    }

    public async Task UnmountDriveAsync(MappedDrive drive)
    {
        var letter = char.ToUpperInvariant(drive.DriveLetter);
        _mountedFs.Remove(letter);
        if (_mounts.Remove(letter, out var manager))
        {
            await manager.UnmountAsync();
            await manager.DisposeAsync();
            Log("Info", "Drives", $"Unmounted {drive.DisplayDriveLetter}.");
        }
        RefreshDriveRows();
    }

    public async Task ToggleServiceAsync()
    {
        if (IsServiceRunning)
        {
            foreach (var kvp in _mounts.ToList())
            {
                await kvp.Value.UnmountAsync();
                await kvp.Value.DisposeAsync();
            }
            _mounts.Clear();
            _mountedFs.Clear();
            IsServiceRunning = false;
            Log("Info", "Service", "VPS3Drive Service stopped; all drives unmounted.");
        }
        else
        {
            IsServiceRunning = true;
            Log("Info", "Service", "VPS3Drive Service started.");
        }
        RefreshDriveRows();
    }

    public void Dispose()
    {
        _ipcCts?.Cancel();
        _ipcCts?.Dispose();
        foreach (var mgr in _mounts.Values)
            mgr.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _mounts.Clear();
        _mountedFs.Clear();
        if (_writeBackQueue is not null)
            _writeBackQueue.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _diskCache.Dispose();
    }

    private sealed record StoredKeys(string AccessKey, string SecretKey);
}
