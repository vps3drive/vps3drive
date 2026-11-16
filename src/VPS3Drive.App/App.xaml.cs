using System.Windows;
using VPS3Drive.App.Services;

namespace VPS3Drive.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 3 && e.Args[0] == "--ctx")
        {
            if (ExplorerContextMenuService.TrySendCommand(e.Args[1], e.Args[2], out var clip) &&
                !string.IsNullOrEmpty(clip))
            {
                try { System.Windows.Clipboard.SetText(clip); } catch { }
            }
            Shutdown(0);
            return;
        }
        base.OnStartup(e);
    }
}
