using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VPS3Drive.App.Services;
using Forms = System.Windows.Forms;

namespace VPS3Drive.App;

public partial class MainWindow : Window
{
    private readonly AppStateService _state = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _forceExit;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        DrivesGrid.ItemsSource = _state.DriveRows;
        TransfersGrid.ItemsSource = _state.Transfers;
        EventLogGrid.ItemsSource = _state.EventLogs;

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = (!string.IsNullOrEmpty(Environment.ProcessPath) ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) : null) ?? System.Drawing.SystemIcons.Application,
            Text = "VPS3Drive",
            Visible = true
        };
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open VPS3Drive", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("Exit", null, (_, _) =>
        {
            _forceExit = true;
            Close();
        });
        _trayIcon.ContextMenuStrip = trayMenu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _state.Dispose();
        };
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceExit && _state.Options.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(2000, "VPS3Drive", "VPS3Drive is still running in the system tray.", Forms.ToolTipIcon.Info);
            }
            return;
        }
        base.OnClosing(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            Hide();
        }
        await _state.InitializeAsync();
        UpdateUiState();
        if (_state.Options.CheckForUpdatesOnStartup)
            _ = CheckUpdatesInternalAsync(silentIfUpToDate: true);
    }

    private void UpdateUiState()
    {
        var selected = DrivesGrid.SelectedItem as MappedDriveRow;
        var hasSelection = selected is not null;
        var isMounted = selected?.IsMounted == true;

        BtnEditDrive.IsEnabled = hasSelection;
        BtnDeleteDrive.IsEnabled = hasSelection;
        BtnMountDrive.IsEnabled = hasSelection && !isMounted && _state.IsServiceRunning;
        BtnUnmountDrive.IsEnabled = hasSelection && isMounted;
        BtnOpenExplorer.IsEnabled = hasSelection && isMounted;

        MenuEditDrive.IsEnabled = hasSelection;
        MenuDeleteDrive.IsEnabled = hasSelection;
        MenuMountDrive.IsEnabled = BtnMountDrive.IsEnabled;
        MenuUnmountDrive.IsEnabled = BtnUnmountDrive.IsEnabled;
        MenuOpenExplorer.IsEnabled = BtnOpenExplorer.IsEnabled;

        DriveSummaryText.Text = _state.DriveRows.Count == 1
            ? "1 mapped drive"
            : $"{_state.DriveRows.Count} mapped drives";

        ServiceStatusText.Text = _state.IsServiceRunning ? "Running" : "Stopped (click to start)";
        ServiceStatusText.Foreground = _state.IsServiceRunning
            ? new SolidColorBrush(Color.FromRgb(14, 122, 13))
            : new SolidColorBrush(Color.FromRgb(180, 35, 24));

        MenuToggleService.Header = _state.IsServiceRunning ? "Stop VPS3Drive Service" : "Start VPS3Drive Service";
        WinFspStatusText.Text = _state.IsWinFspAvailable
            ? "WinFsp Runtime: Ready"
            : "WinFsp Runtime: Not Installed";
    }

    private void DrivesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUiState();

    private void DrivesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditDrive_Click(sender, e);

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var editor = new AccountEditorWindow(_state) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultAccount is not null)
        {
            _state.Accounts.Add(editor.ResultAccount);
            await _state.SaveAccountsAsync();
            _state.Log("Info", "Accounts", $"Added account '{editor.ResultAccount.Name}'.");
            UpdateUiState();
        }
    }

    private void ManageAccounts_Click(object sender, RoutedEventArgs e)
    {
        var win = new AccountsWindow(_state) { Owner = this };
        win.ShowDialog();
        _state.RefreshDriveRows();
        UpdateUiState();
    }

    private async void AddDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Accounts.Count == 0)
        {
            var res = MessageBox.Show(this, "No storage accounts configured yet. Would you like to add one now?",
                "Add Storage Account", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
                AddAccount_Click(sender, e);
            if (_state.Accounts.Count == 0)
                return;
        }

        var editor = new MappedDriveEditorWindow(_state) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultDrive is not null)
        {
            _state.Drives.Add(editor.ResultDrive);
            await _state.SaveDrivesAsync();
            _state.Log("Info", "Drives", $"Added mapped drive {editor.ResultDrive.DisplayDriveLetter} -> {editor.ResultDrive.BucketPath}.");
            UpdateUiState();
        }
    }

    private async void EditDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DrivesGrid.SelectedItem is not MappedDriveRow selected)
            return;

        if (selected.IsMounted)
        {
            MessageBox.Show(this, "Unmount the drive before editing its properties.", "Drive Mounted",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var idx = _state.Drives.FindIndex(d => d.Id == selected.Id);
        if (idx < 0)
            return;

        var editor = new MappedDriveEditorWindow(_state, selected.Drive) { Owner = this };
        if (editor.ShowDialog() == true && editor.ResultDrive is not null)
        {
            _state.Drives[idx] = editor.ResultDrive;
            await _state.SaveDrivesAsync();
            _state.Log("Info", "Drives", $"Updated mapped drive {editor.ResultDrive.DisplayDriveLetter}.");
            UpdateUiState();
        }
    }

    private async void DeleteDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DrivesGrid.SelectedItem is not MappedDriveRow selected)
            return;

        if (MessageBox.Show(this, $"Delete mapped drive {selected.DriveLetter} ({selected.Bucket})?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (selected.IsMounted)
            await _state.UnmountDriveAsync(selected.Drive);

        _state.Drives.RemoveAll(d => d.Id == selected.Id);
        await _state.SaveDrivesAsync();
        _state.Log("Info", "Drives", $"Deleted mapped drive {selected.DriveLetter}.");
        UpdateUiState();
    }

    private async void MountDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DrivesGrid.SelectedItem is not MappedDriveRow selected)
            return;

        try
        {
            BtnMountDrive.IsEnabled = false;
            await _state.MountDriveAsync(selected.Drive);
        }
        catch (Exception ex)
        {
            _state.Log("Error", "Mount", ex.Message);
            MessageBox.Show(this, ex.Message, "Mount Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateUiState();
        }
    }

    private async void UnmountDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DrivesGrid.SelectedItem is not MappedDriveRow selected)
            return;

        try
        {
            await _state.UnmountDriveAsync(selected.Drive);
        }
        catch (Exception ex)
        {
            _state.Log("Error", "Unmount", ex.Message);
            MessageBox.Show(this, ex.Message, "Unmount Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateUiState();
        }
    }

    private void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (DrivesGrid.SelectedItem is not MappedDriveRow selected || !selected.IsMounted)
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", selected.DriveLetter) { UseShellExecute = true });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _state.RefreshDriveRows();
        UpdateUiState();
    }

    private async void ToggleService_Click(object sender, RoutedEventArgs e)
    {
        await _state.ToggleServiceAsync();
        UpdateUiState();
    }

    private async void ServiceStatus_Click(object sender, MouseButtonEventArgs e) =>
        await _state.ToggleServiceAsync().ContinueWith(_ => Dispatcher.Invoke(UpdateUiState));

    private void ShowEventLog_Click(object sender, RoutedEventArgs e) =>
        BottomTabs.SelectedItem = EventLogTab;

    private void ClearLog_Click(object sender, RoutedEventArgs e) =>
        _state.EventLogs.Clear();

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Event Log",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt",
            FileName = $"vps3drive-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var lines = _state.EventLogs.Select(x => $"[{x.FormattedTime}] [{x.Level}] [{x.Source}] {x.Message}");
        File.WriteAllLines(dialog.FileName, lines);
    }

    private async void HttpHeaders_Click(object sender, RoutedEventArgs e)
    {
        var win = new HttpHeadersWindow(_state.Options.DefaultHttpHeaders) { Owner = this };
        if (win.ShowDialog() == true)
        {
            await _state.SaveOptionsAsync(_state.Options with { DefaultHttpHeaders = win.Rules.ToList() });
        }
    }

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        var win = new OptionsWindow(_state.Options) { Owner = this };
        if (win.ShowDialog() == true && win.ResultOptions is not null)
        {
            await _state.SaveOptionsAsync(win.ResultOptions);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdatesInternalAsync(silentIfUpToDate: false);

    private async Task CheckUpdatesInternalAsync(bool silentIfUpToDate)
    {
        var result = await UpdateService.CheckForUpdatesAsync();
        if (result.ErrorMessage is not null)
        {
            if (!silentIfUpToDate)
                MessageBox.Show(this, $"Could not check for updates: {result.ErrorMessage}", "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (result.UpdateAvailable)
        {
            var answer = MessageBox.Show(
                this,
                $"A new version of VPS3Drive is available ({result.LatestVersion}, current {result.CurrentVersion}).\n\nOpen download page?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(result.DownloadUrl))
                Process.Start(new ProcessStartInfo(result.DownloadUrl) { UseShellExecute = true });
        }
        else if (!silentIfUpToDate)
        {
            MessageBox.Show(this, $"You are running the latest version ({result.CurrentVersion}).", "Check for Updates", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnlineHelp_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://docs.aws.amazon.com/AmazonS3/latest/userguide/Welcome.html") { UseShellExecute = true });
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _forceExit = true;
        Close();
    }
}
