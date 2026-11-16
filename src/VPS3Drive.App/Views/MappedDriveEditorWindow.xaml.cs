using System.IO;
using System.Windows;
using System.Windows.Controls;
using VPS3Drive.App.Services;
using VPS3Drive.Core;

namespace VPS3Drive.App;

public partial class MappedDriveEditorWindow : Window
{
    private readonly AppStateService _state;
    private readonly MappedDrive? _existing;

    public MappedDrive? ResultDrive { get; private set; }

    public MappedDriveEditorWindow(AppStateService state, MappedDrive? existing = null)
    {
        _state = state;
        _existing = existing;
        InitializeComponent();

        RefreshAccounts(existing?.AccountId);
        PopulateDriveLetters(existing?.DriveLetter);

        if (existing is not null)
        {
            Title = "Edit Mapped Drive";
            BucketBox.Text = existing.BucketPath;
            NetworkDriveRadio.IsChecked = existing.MountType == DriveMountType.NetworkDrive;
            RemovableDriveRadio.IsChecked = existing.MountType == DriveMountType.RemovableDrive;
            ReadOnlyBox.IsChecked = existing.ReadOnly;
            AutoMountBox.IsChecked = existing.AutoMount;
            VolumeLabelBox.Text = existing.VolumeLabel ?? string.Empty;

            SelectStorageClass(existing.DefaultStorageClass);
            SseAes256Box.IsChecked = existing.UseServerSideEncryption;
            TransferAccelBox.IsChecked = existing.EnableTransferAcceleration;
            RequesterPaysBox.IsChecked = existing.RequesterPays;
            CacheFileInfoBox.IsChecked = existing.CacheFileInfo;
            CacheDirBox.IsChecked = existing.CacheDirectoryListings;
            CacheReadBox.IsChecked = existing.CacheReadRequests;
            CacheReadMaxBox.Text = existing.CacheReadMaxFileSizeMb.ToString();
            PrefetchBox.IsChecked = existing.PrefetchSmallFiles;
            PrefetchMaxBox.Text = existing.PrefetchMaxFileSizeMb.ToString();
            DiskCacheLimitBox.Text = existing.DiskCacheLimitMb.ToString();
            CacheTtlBox.Text = existing.CacheTtlSeconds.ToString();
            SoftDeleteBox.IsChecked = existing.SoftDelete;
            TrashPrefixBox.Text = string.IsNullOrWhiteSpace(existing.TrashPrefix) ? ".trash" : existing.TrashPrefix;
        }
        else
        {
            Title = "Add New Mapped Drive";
        }
    }

    private void RefreshAccounts(string? selectId)
    {
        AccountBox.ItemsSource = null;
        AccountBox.ItemsSource = _state.Accounts;
        if (selectId is not null)
            AccountBox.SelectedItem = _state.Accounts.FirstOrDefault(a => a.Id == selectId);
        if (AccountBox.SelectedItem is null && _state.Accounts.Count > 0)
            AccountBox.SelectedIndex = 0;
    }

    private void PopulateDriveLetters(char? currentLetter)
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        foreach (var drive in _state.Drives)
        {
            if (currentLetter is null || char.ToUpperInvariant(drive.DriveLetter) != char.ToUpperInvariant(currentLetter.Value))
                used.Add(char.ToUpperInvariant(drive.DriveLetter));
        }

        DriveLetterBox.Items.Clear();
        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c) || (currentLetter is not null && char.ToUpperInvariant(currentLetter.Value) == c))
                DriveLetterBox.Items.Add($"{c}:");
        }

        if (currentLetter is not null)
            DriveLetterBox.SelectedItem = $"{char.ToUpperInvariant(currentLetter.Value)}:";
        else if (DriveLetterBox.Items.Count > 0)
            DriveLetterBox.SelectedIndex = 0;
    }

    private void SelectStorageClass(string value)
    {
        foreach (ComboBoxItem item in StorageClassBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                StorageClassBox.SelectedItem = item;
                return;
            }
        }
        StorageClassBox.Text = value;
    }

    private void EditAccounts_Click(object sender, RoutedEventArgs e)
    {
        var currentId = (AccountBox.SelectedItem as StorageAccount)?.Id;
        var win = new AccountsWindow(_state) { Owner = this };
        win.ShowDialog();
        RefreshAccounts(currentId);
    }

    private void BrowseBuckets_Click(object sender, RoutedEventArgs e)
    {
        if (AccountBox.SelectedItem is not StorageAccount account)
        {
            MessageBox.Show(this, "Please select or add a storage account first.", "Browse Buckets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var browser = new BucketBrowserWindow(_state, account) { Owner = this };
        if (browser.ShowDialog() == true && !string.IsNullOrWhiteSpace(browser.SelectedPath))
            BucketBox.Text = browser.SelectedPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AccountBox.SelectedItem is not StorageAccount account)
                throw new ArgumentException("Please select a storage account.");

            var letterText = DriveLetterBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(letterText))
                throw new ArgumentException("Please select a drive letter.");

            var storageClass = string.IsNullOrWhiteSpace(StorageClassBox.Text)
                ? ((StorageClassBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "STANDARD")
                : StorageClassBox.Text.Trim();

            var drive = new MappedDrive
            {
                Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
                AccountId = account.Id,
                BucketPath = BucketBox.Text.Trim(),
                DriveLetter = char.ToUpperInvariant(letterText[0]),
                MountType = RemovableDriveRadio.IsChecked == true ? DriveMountType.RemovableDrive : DriveMountType.NetworkDrive,
                ReadOnly = ReadOnlyBox.IsChecked == true,
                AutoMount = AutoMountBox.IsChecked == true,
                VolumeLabel = string.IsNullOrWhiteSpace(VolumeLabelBox.Text) ? null : VolumeLabelBox.Text.Trim(),
                DefaultStorageClass = storageClass,
                UseServerSideEncryption = SseAes256Box.IsChecked == true,
                EnableTransferAcceleration = TransferAccelBox.IsChecked == true,
                RequesterPays = RequesterPaysBox.IsChecked == true,
                CacheFileInfo = CacheFileInfoBox.IsChecked == true,
                CacheDirectoryListings = CacheDirBox.IsChecked == true,
                CacheReadRequests = CacheReadBox.IsChecked == true,
                CacheReadMaxFileSizeMb = int.TryParse(CacheReadMaxBox.Text, out var crm) ? crm : 32,
                PrefetchSmallFiles = PrefetchBox.IsChecked == true,
                PrefetchMaxFileSizeMb = int.TryParse(PrefetchMaxBox.Text, out var pm) ? pm : 8,
                DiskCacheLimitMb = int.TryParse(DiskCacheLimitBox.Text, out var dcl) ? dcl : 512,
                CacheTtlSeconds = int.TryParse(CacheTtlBox.Text, out var ttl) ? ttl : 300,
                SoftDelete = SoftDeleteBox.IsChecked == true,
                TrashPrefix = string.IsNullOrWhiteSpace(TrashPrefixBox.Text) ? ".trash" : TrashPrefixBox.Text.Trim()
            };

            drive.Validate();
            ResultDrive = drive;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid Mapped Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
