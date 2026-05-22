using System.Text.Json.Nodes;

namespace AppTunnel.Services;

/// <summary>
/// Shared WebSocket early-data parsing (v2rayNG <c>?ed=NNNN</c> and <c>?edNNNN</c>).
/// </summary>
internal static class V2RayWebSocketHelper
{
    public static string NormalizeFingerprint(string? fp)
    {
        if (string.IsNullOrWhiteSpace(fp))
            return "";

        // sing-box uTLS does not accept Xray's "randomized"; use a stable browser profile.
        if (fp.Equals("randomized", StringComparison.OrdinalIgnoreCase) ||
            fp.Equals("random", StringComparison.OrdinalIgnoreCase))
            return "chrome";

        return fp;
    }

    /// <summary>sing-box transport object: path, max_early_data, early_data_header_name.</summary>
    public static void ApplyEarlyDataToSingBox(JsonObject wsTransport, string rawPath)
    {
        var (pathOnly, maxEarlyData) = ParseEarlyDataFromPath(rawPath);
        wsTransport["path"] = pathOnly;
        if (maxEarlyData == 0)
            return;

        wsTransport["max_early_data"] = maxEarlyData;
        wsTransport["early_data_header_name"] = "Sec-WebSocket-Protocol";
    }

    /// <summary>Xray wsSettings (Xray 26+: use top-level <c>host</c>, not headers.Host).</summary>
    public static JsonObject CreateXrayWsSettings(string wsHost, string rawPath)
    {
        var wsSettings = new JsonObject();
        if (!string.IsNullOrWhiteSpace(wsHost))
            wsSettings["host"] = wsHost;

        ApplyEarlyDataToXray(wsSettings, rawPath);
        return wsSettings;
    }

    /// <summary>Xray wsSettings: path and optional maxEarlyData.</summary>
    public static void ApplyEarlyDataToXray(JsonObject wsSettings, string rawPath)
    {
        var (pathOnly, maxEarlyData) = ParseEarlyDataFromPath(rawPath);
        wsSettings["path"] = pathOnly;
        if (maxEarlyData > 0)
            wsSettings["maxEarlyData"] = maxEarlyData;
    }

    public static (string Path, uint MaxEarlyData) ParseEarlyDataFromPath(string? rawPath)
    {
        rawPath = Uri.UnescapeDataString(rawPath ?? "/");
        if (string.IsNullOrWhiteSpace(rawPath))
            rawPath = "/";

        var pathOnly = rawPath;
        uint maxEarlyData = 0;

        var queryIndex = rawPath.IndexOf('?');
        if (queryIndex < 0)
            return (pathOnly, maxEarlyData);

        pathOnly = rawPath[..queryIndex];
        if (string.IsNullOrEmpty(pathOnly))
            pathOnly = "/";

        foreach (var part in rawPath[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseEarlyDataParam(part, out var ed))
                maxEarlyData = ed;
        }

        return (pathOnly, maxEarlyData);
    }

    private static bool TryParseEarlyDataParam(ReadOnlySpan<char> part, out uint value)
    {
        value = 0;
        if (part.StartsWith("ed=", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(part[3..], out value) && value > 0;

        if (part.Length > 2 &&
            part.StartsWith("ed", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(part[2..], out value) &&
            value > 0)
        {
            return true;
        }

        return false;
    }
}
