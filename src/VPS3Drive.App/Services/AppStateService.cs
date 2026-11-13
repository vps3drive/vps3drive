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
        Accounts.Clear();
        Accounts.AddRange(await _accountStore.LoadAsync());
        Drives.Clear();
        Drives.AddRange(await _driveStore.LoadAsync());

        RefreshDriveRows();
        SyncWindowsStartupRegistration(Options.StartWithWindows);
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

    private void SyncWindowsStartupRegistration(bool enabled)
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
                    key.SetValue(valueName, $"\"{exePath}\" --minimized");
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

    public async Task SaveDrivesAsync()
    {
        await _driveStore.SaveAllAsync(Drives);
        RefreshDriveRows();
    }

    public async Task SaveOptionsAsync(AppOptions options)
    {
        Options = options;
        await _optionsStore.SaveAsync(options);
        SyncWindowsStartupRegistration(options.StartWithWindows);
        Log("Info", "Options", "Application options saved.");
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
        var store = new S3ObjectStore(profile, credentials, drive.DefaultStorageClass, drive.UseServerSideEncryption);
        var fs = new S3RemoteFileSystem(store, drive.Prefix);

        var manager = new DriveMountManager(new WinFspReadOnlyMountBackend(drive.ReadOnly, drive.EffectiveVolumeLabel));
        var result = await manager.MountAsync(profile, fs);
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
        foreach (var mgr in _mounts.Values)
            mgr.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _mounts.Clear();
    }

    private sealed record StoredKeys(string AccessKey, string SecretKey);
}
