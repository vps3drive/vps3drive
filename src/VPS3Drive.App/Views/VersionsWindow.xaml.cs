using System.Windows;
using VPS3Drive.App.Services;
using VPS3Drive.Core;
using VPS3Drive.Storage;

namespace VPS3Drive.App.Views;

public partial class VersionsWindow : Window
{
    private sealed record VersionRow(
        string Key,
        string VersionId,
        long Size,
        string LastModified,
        bool IsLatest,
        bool IsDeleteMarker)
    {
        public string StateLabel => IsDeleteMarker ? "DeleteMarker" : (IsLatest ? "Latest" : "Previous");
    }

    private sealed record TrashRow(string Key, string DisplayName, string OriginalKey, long Size);

    private readonly AppStateService _state;
    private readonly MappedDrive _drive;

    public VersionsWindow(AppStateService state, MappedDrive drive, string? initialKey = null)
    {
        InitializeComponent();
        _state = state;
        _drive = drive;
        Title = $"Versions & Trash — {drive.DisplayDriveLetter} ({drive.BucketName})";
        PrefixFilterBox.Text = string.IsNullOrWhiteSpace(initialKey) ? drive.Prefix : initialKey;
        Loaded += async (_, _) => await LoadVersionsAsync();
    }

    private async void LoadVersions_Click(object sender, RoutedEventArgs e) => await LoadVersionsAsync();

    private async Task LoadVersionsAsync()
    {
        StatusText.Text = "Loading S3 object versions...";
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            var list = await store.ListVersionsAsync(PrefixFilterBox.Text.Trim());
            VersionsGrid.ItemsSource = list
                .Select(v => new VersionRow(
                    v.Key,
                    v.VersionId,
                    v.Size,
                    v.LastModified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                    v.IsLatest,
                    v.IsDeleteMarker))
                .ToList();
            StatusText.Text = $"{list.Count} version(s) loaded.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsGrid.SelectedItem is not VersionRow row)
            return;
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            if (row.IsDeleteMarker)
                await store.DeleteVersionAsync(row.Key, row.VersionId);
            else
                await store.RestoreVersionAsync(row.Key, row.VersionId);
            _state.PurgeDriveCache(_drive);
            _state.Log("Info", "Versions", $"Restored {row.Key} (version {row.VersionId}).");
            await LoadVersionsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Restore Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (VersionsGrid.SelectedItem is not VersionRow row)
            return;
        if (System.Windows.MessageBox.Show(this, $"Permanently delete version {row.VersionId} of {row.Key}?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            await store.DeleteVersionAsync(row.Key, row.VersionId);
            _state.PurgeDriveCache(_drive);
            await LoadVersionsAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string TrashPrefixKey
    {
        get
        {
            var root = _drive.Prefix.Trim().Trim('/');
            var trash = string.IsNullOrWhiteSpace(_drive.TrashPrefix) ? ".trash" : _drive.TrashPrefix.Trim().Trim('/');
            return string.IsNullOrEmpty(root) ? $"{trash}/" : $"{root}/{trash}/";
        }
    }

    private async void LoadTrash_Click(object sender, RoutedEventArgs e) => await LoadTrashAsync();

    private async Task LoadTrashAsync()
    {
        StatusText.Text = "Loading Trash...";
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            var trashPfx = TrashPrefixKey;
            var root = _drive.Prefix.Trim().Trim('/');
            var rows = new List<TrashRow>();
            await foreach (var entry in store.ListAsync(trashPfx, delimiter: null))
            {
                if (entry.IsDirectory) continue;
                var rel = entry.Key.StartsWith(trashPfx, StringComparison.Ordinal) ? entry.Key[trashPfx.Length..] : entry.Key;
                var idx = rel.IndexOf('_');
                var origRel = idx >= 0 ? rel[(idx + 1)..] : rel;
                var origKey = string.IsNullOrEmpty(root) ? origRel : $"{root}/{origRel}";
                rows.Add(new TrashRow(entry.Key, rel, origKey, entry.Length ?? 0));
            }
            TrashGrid.ItemsSource = rows;
            StatusText.Text = $"{rows.Count} item(s) in Trash.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
    }

    private async void RestoreTrash_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not TrashRow row)
            return;
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            await store.CopyAsync(row.Key, row.OriginalKey);
            await store.DeleteAsync(row.Key);
            _state.PurgeDriveCache(_drive);
            _state.Log("Info", "Trash", $"Restored {row.OriginalKey} from Trash.");
            await LoadTrashAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Restore Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteTrash_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not TrashRow row)
            return;
        if (System.Windows.MessageBox.Show(this, "Permanently delete this item from Trash?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            await store.DeleteAsync(row.Key);
            await LoadTrashAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "Permanently delete ALL items in Trash for this drive?", "Empty Trash", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await using var store = await _state.CreateObjectStoreAsync(_drive);
            var keys = new List<string>();
            await foreach (var entry in store.ListAsync(TrashPrefixKey, delimiter: null))
                keys.Add(entry.Key);
            foreach (var k in keys)
                await store.DeleteAsync(k);
            _state.Log("Info", "Trash", $"Emptied Trash ({keys.Count} item(s) deleted).");
            await LoadTrashAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Empty Trash Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
