using System.Text.Json;

namespace AppTunnel.Services;

public sealed record GitHubReleaseInfo(
    Version Version,
    string TagName,
    string Name,
    string Url,
    bool IsPrerelease,
    string ReleaseNotes);

public static class GitHubReleaseChecker
{
    private const string LatestReleasePath = "/repos/MaxiFan/TunnelX/releases/latest";
    private const string ReleasesPath = "/repos/MaxiFan/TunnelX/releases";
    private const string GitHubApiHost = "api.github.com";

    /// <summary>Latest release via local mixed proxy (tunnel egress only). Returns null when <paramref name="proxyPort"/> is invalid.</summary>
    public static Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct, int proxyPort)
    {
        if (proxyPort <= 0)
            return Task.FromResult<GitHubReleaseInfo?>(null);

        return TryGetLatestReleaseAsync(ct, proxyPort);
    }

    private static async Task<GitHubReleaseInfo?> TryGetLatestReleaseAsync(CancellationToken ct, int proxyPort)
    {
        var body = await TunnelProxyHttpService.GetAsync(
            proxyPort,
            GitHubApiHost,
            443,
            LatestReleasePath,
            useTls: true,
            ct);

        if (string.IsNullOrWhiteSpace(body))
        {
            Logger.Warning("[UPDATE] GitHub latest-release request via tunnel returned no body");
            return null;
        }

        return ParseLatestReleaseJson(body);
    }

    private static GitHubReleaseInfo? ParseLatestReleaseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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
            var releaseNotes = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? ""
                : "";

            return new GitHubReleaseInfo(version, tag, name, url, prerelease, releaseNotes);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UPDATE] Failed to parse GitHub release JSON: {ex.Message}");
            return null;
        }
    }

    public static async Task<long?> GetAppDownloadCountAsync(CancellationToken ct, int proxyPort = 0)
    {
        if (proxyPort > 0)
        {
            var proxied = await TryGetAppDownloadCountViaTunnelAsync(ct, proxyPort);
            if (proxied.HasValue)
                return proxied;
        }

        return await TryGetAppDownloadCountDirectAsync(ct);
    }

    private static async Task<long?> TryGetAppDownloadCountViaTunnelAsync(CancellationToken ct, int proxyPort)
    {
        var body = await TunnelProxyHttpService.GetAsync(
            proxyPort,
            GitHubApiHost,
            443,
            ReleasesPath,
            useTls: true,
            ct);

        if (string.IsNullOrWhiteSpace(body))
            return null;

        return ParseDownloadCountFromReleasesJson(body);
    }

    private static async Task<long?> TryGetAppDownloadCountDirectAsync(CancellationToken ct)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TunnelX");
            using var response = await http.GetAsync($"https://{GitHubApiHost}{ReleasesPath}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseDownloadCountFromReleasesJson(body);
        }
        catch
        {
            return null;
        }
    }

    private static long? ParseDownloadCountFromReleasesJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            long total = 0;
            foreach (var release in doc.RootElement.EnumerateArray())
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
        catch
        {
            return null;
        }
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
