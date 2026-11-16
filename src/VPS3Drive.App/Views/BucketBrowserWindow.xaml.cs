using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VPS3Drive.App.Services;
using VPS3Drive.Core;
using VPS3Drive.Storage;

namespace VPS3Drive.App;

public partial class BucketBrowserWindow : Window
{
    private readonly AppStateService _state;
    private readonly StorageAccount _account;
    private string? _currentBucket;
    private string _currentPrefix = string.Empty;

    public string SelectedPath { get; private set; } = string.Empty;

    public BucketBrowserWindow(AppStateService state, StorageAccount account)
    {
        _state = state;
        _account = account;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        ItemsList.Items.Clear();
        UpButton.IsEnabled = _currentBucket is not null;
        CurrentPathBox.Text = _currentBucket is null
            ? "/"
            : string.IsNullOrEmpty(_currentPrefix) ? $"{_currentBucket}/" : $"{_currentBucket}/{_currentPrefix}";
        StatusText.Text = "Loading...";

        try
        {
            var creds = await _state.ResolveCredentialsAsync(_account);
            if (_currentBucket is null)
            {
                var buckets = await S3AccountClient.ListBucketsAsync(_account, creds);
                foreach (var b in buckets)
                    ItemsList.Items.Add(b);
                StatusText.Text = $"{buckets.Count} bucket(s) found.";
            }
            else
            {
                var folders = await S3AccountClient.ListFoldersAsync(_account, creds, _currentBucket, _currentPrefix);
                foreach (var f in folders)
                {
                    var name = f.TrimEnd('/');
                    var lastSlash = name.LastIndexOf('/');
                    ItemsList.Items.Add(lastSlash >= 0 ? name[(lastSlash + 1)..] + "/" : name + "/");
                }
                StatusText.Text = $"{folders.Count} folder(s) found.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not string item)
            return;

        if (_currentBucket is null)
            SelectedPath = item;
        else
            SelectedPath = $"{_currentBucket}/{_currentPrefix}{item}";
    }

    private async void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not string item)
            return;

        if (_currentBucket is null)
        {
            _currentBucket = item;
            _currentPrefix = string.Empty;
        }
        else
        {
            _currentPrefix += item;
        }

        SelectedPath = string.IsNullOrEmpty(_currentPrefix) ? _currentBucket : $"{_currentBucket}/{_currentPrefix}";
        await LoadAsync();
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBucket is null)
            return;

        if (string.IsNullOrEmpty(_currentPrefix))
        {
            _currentBucket = null;
        }
        else
        {
            var trimmed = _currentPrefix.TrimEnd('/');
            var idx = trimmed.LastIndexOf('/');
            _currentPrefix = idx >= 0 ? trimmed[..(idx + 1)] : string.Empty;
        }

        await LoadAsync();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedPath))
            SelectedPath = CurrentPathBox.Text.TrimStart('/');

        if (string.IsNullOrWhiteSpace(SelectedPath))
        {
            MessageBox.Show(this, "Select a bucket or folder.", "Browse Buckets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
