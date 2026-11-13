using System.Windows;
using System.Windows.Controls;
using VPS3Drive.Core;

namespace VPS3Drive.App;

public partial class OptionsWindow : Window
{
    private readonly AppOptions _current;
    public AppOptions? ResultOptions { get; private set; }

    public OptionsWindow(AppOptions current)
    {
        _current = current;
        InitializeComponent();

        MultipartUploadBox.IsChecked = current.EnableMultipartUploads;
        MultipartUploadSizeBox.Text = current.MultipartUploadPartSizeMb.ToString();
        MultipartDownloadBox.IsChecked = current.EnableMultipartDownloads;
        MultipartDownloadSizeBox.Text = current.MultipartDownloadPartSizeMb.ToString();
        InheritAclBox.IsChecked = current.InheritPermissionsFromParentBucket;
        PreserveAclBox.IsChecked = current.PreservePermissionsOnOverwrite;
        StartWithWindowsBox.IsChecked = current.StartWithWindows;
        AutoMountStartupBox.IsChecked = current.AutoMountDrivesOnStartup;
        CheckUpdatesBox.IsChecked = current.CheckForUpdatesOnStartup;

        foreach (ComboBoxItem item in ProxyModeBox.Items)
        {
            if (item.Tag?.ToString() == current.ProxyMode.ToString())
            {
                ProxyModeBox.SelectedItem = item;
                break;
            }
        }
        ProxyServerBox.Text = current.ProxyServer ?? string.Empty;
        ProxyPortBox.Text = current.ProxyPort.ToString();
        ProxyAuthBox.IsChecked = current.ProxyRequiresAuth;
        ProxyUserBox.Text = current.ProxyUsername ?? string.Empty;

        ThrottleUploadBox.IsChecked = current.ThrottleUploads;
        MaxUploadBox.Text = current.MaxUploadSpeedKbps.ToString();
        ThrottleDownloadBox.IsChecked = current.ThrottleDownloads;
        MaxDownloadBox.Text = current.MaxDownloadSpeedKbps.ToString();

        VerifyUploadBox.IsChecked = current.VerifyUploadIntegrity;
        VerifyDownloadBox.IsChecked = current.VerifyDownloadIntegrity;

        MaxUploadsBox.Text = current.MaxConcurrentUploads.ToString();
        MaxDownloadsBox.Text = current.MaxConcurrentDownloads.ToString();
        RetryCountBox.Text = current.RetryCount.ToString();

        MinimizeTrayBox.IsChecked = current.MinimizeToTray;
        foreach (ComboBoxItem item in LogLevelBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), current.LogLevel, StringComparison.OrdinalIgnoreCase))
            {
                LogLevelBox.SelectedItem = item;
                break;
            }
        }
        if (LogLevelBox.SelectedIndex < 0)
            LogLevelBox.SelectedIndex = 2;
        LogRetentionBox.Text = current.LogRetentionDays.ToString();

        TimeoutBox.Text = current.RequestTimeoutSeconds.ToString();
        ReadBufferBox.Text = current.ReadBufferSizeKb.ToString();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var proxyMode = ProxyMode.SystemProxy;
        if (ProxyModeBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ProxyMode>(item.Tag?.ToString(), out var parsedMode))
        {
            proxyMode = parsedMode;
        }

        ResultOptions = _current with
        {
            EnableMultipartUploads = MultipartUploadBox.IsChecked == true,
            MultipartUploadPartSizeMb = int.TryParse(MultipartUploadSizeBox.Text, out var mus) ? mus : 16,
            EnableMultipartDownloads = MultipartDownloadBox.IsChecked == true,
            MultipartDownloadPartSizeMb = int.TryParse(MultipartDownloadSizeBox.Text, out var mds) ? mds : 16,
            InheritPermissionsFromParentBucket = InheritAclBox.IsChecked == true,
            PreservePermissionsOnOverwrite = PreserveAclBox.IsChecked == true,
            StartWithWindows = StartWithWindowsBox.IsChecked == true,
            AutoMountDrivesOnStartup = AutoMountStartupBox.IsChecked == true,
            CheckForUpdatesOnStartup = CheckUpdatesBox.IsChecked == true,
            ProxyMode = proxyMode,
            ProxyServer = string.IsNullOrWhiteSpace(ProxyServerBox.Text) ? null : ProxyServerBox.Text.Trim(),
            ProxyPort = int.TryParse(ProxyPortBox.Text, out var pp) ? pp : 8080,
            ProxyRequiresAuth = ProxyAuthBox.IsChecked == true,
            ProxyUsername = string.IsNullOrWhiteSpace(ProxyUserBox.Text) ? null : ProxyUserBox.Text.Trim(),
            ThrottleUploads = ThrottleUploadBox.IsChecked == true,
            MaxUploadSpeedKbps = int.TryParse(MaxUploadBox.Text, out var mup) ? mup : 1024,
            ThrottleDownloads = ThrottleDownloadBox.IsChecked == true,
            MaxDownloadSpeedKbps = int.TryParse(MaxDownloadBox.Text, out var mdn) ? mdn : 2048,
            VerifyUploadIntegrity = VerifyUploadBox.IsChecked == true,
            VerifyDownloadIntegrity = VerifyDownloadBox.IsChecked == true,
            MaxConcurrentUploads = int.TryParse(MaxUploadsBox.Text, out var mcu) ? mcu : 4,
            MaxConcurrentDownloads = int.TryParse(MaxDownloadsBox.Text, out var mcd) ? mcd : 4,
            RetryCount = int.TryParse(RetryCountBox.Text, out var rc) ? rc : 3,
            MinimizeToTray = MinimizeTrayBox.IsChecked == true,
            LogLevel = (LogLevelBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Info",
            LogRetentionDays = int.TryParse(LogRetentionBox.Text, out var lr) ? lr : 14,
            RequestTimeoutSeconds = int.TryParse(TimeoutBox.Text, out var to) ? to : 60,
            ReadBufferSizeKb = int.TryParse(ReadBufferBox.Text, out var rb) ? rb : 256
        };

        DialogResult = true;
    }
}
