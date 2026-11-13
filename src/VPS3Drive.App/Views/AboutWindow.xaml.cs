using System.Windows;
using VPS3Drive.App.Services;

namespace VPS3Drive.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateService.CurrentVersionString} — Open Source Windows S3 Drive Client";
    }
}
