using System;
using System.Net;

namespace AppTunnel.Services;

/// <summary>
/// Parses proxy URLs like socks5://user:pass@host:port or http://host:port.
/// </summary>
public record ParsedProxyUrl(
    string Scheme,
    string Host,
    int Port,
    string? Username,
    string? Password);

public static class ProxyUrlParser
{
    public static ParsedProxyUrl? Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var uri = new Uri(url);
            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "socks5" && scheme != "http") return null;

            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : (scheme == "http" ? 8080 : 1080);

            string? user = null, pass = null;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                user = Uri.UnescapeDataString(parts[0]);
                if (parts.Length == 2)
                    pass = Uri.UnescapeDataString(parts[1]);
            }

            return new ParsedProxyUrl(scheme, host, port, user, pass);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strips the [::ffff:] prefix and port from an IPEndPoint string, returning the IPv4 address.
    /// </summary>
    public static string? ExtractIPv4(string? endPoint)
    {
        if (string.IsNullOrWhiteSpace(endPoint)) return null;
        var s = endPoint.Trim();
        // Strip [::ffff:] prefix
        const string v6Prefix = "[::ffff:";
        if (s.StartsWith(v6Prefix, StringComparison.OrdinalIgnoreCase))
        {
            // [::ffff:1.2.3.4]:port → 1.2.3.4
            var inner = s[v6Prefix.Length..];
            var close = inner.IndexOf(']');
            if (close < 0) return null;
            inner = inner[..close];
            return IPAddress.TryParse(inner, out _) ? inner : null;
        }
        // Plain "1.2.3.4:port" or just "1.2.3.4"
        var colon = s.IndexOf(':');
        if (colon > 0) s = s[..colon];
        return IPAddress.TryParse(s, out _) ? s : null;
    }
}
