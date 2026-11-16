using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace VPS3Drive.App.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? DownloadUrl,
    string? ErrorMessage = null);

public static class UpdateService
{
    public const string RepoSlug = "vps3drive/vps3drive";

    public static Version CurrentAssemblyVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public static string CurrentVersionString =>
        $"{CurrentAssemblyVersion.Major}.{CurrentAssemblyVersion.Minor}.{CurrentAssemblyVersion.Build}";

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VPS3Drive", CurrentVersionString));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var url = $"https://api.github.com/repos/{RepoSlug}/releases/latest";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, CurrentVersionString, null, null, null,
                    $"GitHub API returned {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tagName))
                return new UpdateCheckResult(false, CurrentVersionString, null, htmlUrl, null);

            var normalized = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(normalized, out var latest))
                return new UpdateCheckResult(false, CurrentVersionString, tagName, htmlUrl, null);

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var dlProp))
                    {
                        var candidate = dlProp.GetString();
                        downloadUrl ??= candidate;
                        if (candidate is not null && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = candidate;
                            break;
                        }
                    }
                }
            }

            var current3 = new Version(CurrentAssemblyVersion.Major, CurrentAssemblyVersion.Minor, Math.Max(0, CurrentAssemblyVersion.Build));
            var latest3 = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            return new UpdateCheckResult(latest3 > current3, CurrentVersionString, tagName, htmlUrl, downloadUrl ?? htmlUrl);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, CurrentVersionString, null, null, null, ex.Message);
        }
    }
}
