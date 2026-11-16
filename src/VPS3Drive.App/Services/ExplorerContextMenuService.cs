using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VPS3Drive.App.Services;

public static class ExplorerContextMenuService
{
    private static readonly (string Key, string Label, string Action)[] Items =
    [
        ("01_presign_1h", "Copy Presigned URL (1 hour)", "presign_1h"),
        ("02_presign_24h", "Copy Presigned URL (24 hours)", "presign_24h"),
        ("03_presign_7d", "Copy Presigned URL (7 days)", "presign_7d"),
        ("04_s3_uri", "Copy S3 URI (s3://...)", "copy_s3_uri"),
        ("05_https_url", "Copy HTTPS URL", "copy_https_url"),
        ("06_purge_cache", "Purge Cache / Force Refresh", "purge_cache"),
        ("07_versions", "Versions & Trash Recovery...", "open_versions"),
    ];

    public static string PortFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VPS3Drive",
        "dotnet-ipc.port");

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\VPS3Drive", writable: false);
        return key is not null;
    }

    public static void Register(string exePath)
    {
        foreach (var target in new[] { "*", "Directory" })
        {
            var rootPath = $@"Software\Classes\{target}\shell\VPS3Drive";
            using var root = Registry.CurrentUser.CreateSubKey(rootPath, writable: true);
            root.SetValue("MUIVerb", "VPS3Drive");
            root.SetValue("SubCommands", "");
            root.SetValue("Icon", $"\"{exePath}\",0");

            foreach (var (itemKey, label, action) in Items)
            {
                if (target == "Directory" && action.StartsWith("presign_", StringComparison.Ordinal))
                    continue;
                using var sub = root.CreateSubKey($@"shell\{itemKey}", writable: true);
                sub.SetValue("MUIVerb", label);
                using var cmd = sub.CreateSubKey("command", writable: true);
                cmd.SetValue("", $"\"{exePath}\" --ctx {action} \"%1\"");
            }
        }
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\VPS3Drive", throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\VPS3Drive", throwOnMissingSubKey: false); } catch { }
    }

    public static void NotifyExplorerRefresh(string path)
    {
        const uint SHCNE_UPDATEDIR = 0x00001000;
        const uint SHCNF_PATHW = 0x0005;
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, path, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string dwItem1, IntPtr dwItem2);

    public static bool TrySendCommand(string action, string path, out string? clipboardText)
    {
        clipboardText = null;
        try
        {
            if (!File.Exists(PortFilePath))
                return false;
            var portStr = File.ReadAllText(PortFilePath).Trim();
            if (!int.TryParse(portStr, out var port))
                return false;

            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            using var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            var payload = JsonSerializer.Serialize(new { action, path }) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var line = reader.ReadLine();
            if (!string.IsNullOrWhiteSpace(line))
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("clipboard", out var clip))
                    clipboardText = clip.GetString();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static CancellationTokenSource StartIpcServer(Func<string, string, (bool Ok, string? Clipboard)> handler)
    {
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Directory.CreateDirectory(Path.GetDirectoryName(PortFilePath)!);
        File.WriteAllText(PortFilePath, port.ToString());

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var tcp = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (tcp)
                            using (var stream = tcp.GetStream())
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                            {
                                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (string.IsNullOrWhiteSpace(line))
                                    return;
                                using var doc = JsonDocument.Parse(line);
                                var action = doc.RootElement.GetProperty("action").GetString() ?? "";
                                var path = doc.RootElement.GetProperty("path").GetString() ?? "";
                                var (ok, clip) = handler(action, path);
                                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok, clipboard = clip })).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
            finally
            {
                try { listener.Stop(); } catch { }
                try { File.Delete(PortFilePath); } catch { }
            }
        });

        return cts;
    }
}
