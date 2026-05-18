using System.Net.Http;
using System.Net;
using System.Text.Json;

namespace AppTunnel.Services;

public sealed record GitHubReleaseInfo(
    Version Version,
    string TagName,
    string Name,
    string Url,
    bool IsPrerelease);

public static class GitHubReleaseChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/MaxiFan/TunnelX/releases/latest";
    private const string ReleasesApi =
        "https://api.github.com/repos/MaxiFan/TunnelX/releases";

    public static async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TunnelX");
        http.Timeout = TimeSpan.FromSeconds(8);

        using var response = await http.GetAsync(LatestReleaseApi, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString() ?? ""
            : "";
        if (!TryParseVersion(tag, out var version))
            return null;

        var name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? tag
            : tag;
        var url = root.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString() ?? AppInfo.LatestReleaseUrl
            : AppInfo.LatestReleaseUrl;
        var prerelease = root.TryGetProperty("prerelease", out var preElement) &&
                         preElement.ValueKind == JsonValueKind.True;

        return new GitHubReleaseInfo(version, tag, name, url, prerelease);
    }

    public static async Task<long?> GetAppDownloadCountAsync(CancellationToken ct, int proxyPort = 0)
    {
        if (proxyPort > 0)
        {
            var proxied = await TryGetAppDownloadCountAsync(ct, proxyPort);
            if (proxied.HasValue)
                return proxied;
        }

        return await TryGetAppDownloadCountAsync(ct, proxyPort: 0);
    }

    private static async Task<long?> TryGetAppDownloadCountAsync(CancellationToken ct, int proxyPort)
    {
        using var handler = new HttpClientHandler();
        if (proxyPort > 0)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}");
            handler.UseProxy = true;
        }

        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TunnelX");

        using var response = await http.GetAsync(ReleasesApi, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        long total = 0;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? ""
                    : "";
                if (!IsAppDownloadAsset(name))
                    continue;

                if (asset.TryGetProperty("download_count", out var countElement) &&
                    countElement.TryGetInt64(out var count))
                    total += count;
            }
        }

        return total;
    }

    public static bool TryParseVersion(string value, out Version version)
    {
        value = (value ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(value, out version!);
    }

    private static bool IsAppDownloadAsset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
               !name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase);
    }
}
