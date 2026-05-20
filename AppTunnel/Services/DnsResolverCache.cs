using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace AppTunnel.Services;

/// <summary>
/// Lightweight in-memory DNS cache for IPv4 lookups used by routing and SOCKS.
/// Keeps short TTLs to avoid stale CDN mappings while preventing repeated
/// resolver calls during bursty traffic.
/// </summary>
internal static class DnsResolverCache
{
    private sealed class CacheEntry
    {
        public required IPAddress[] Addresses { get; init; }
        public required DateTime ExpiresAtUtc { get; init; }
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(20);
    private static readonly HttpClient DohHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    internal static readonly IPAddress[] DohBootstrapResolvers =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("1.0.0.1")
    ];

    public static async Task<IPAddress[]> ResolveIpv4Async(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl = null)
    {
        if (TryParseIpv4(hostOrIp, out var literal))
            return new[] { literal };

        var key = NormalizeKey(hostOrIp);
        if (string.IsNullOrEmpty(key))
            return Array.Empty<IPAddress>();

        if (TryGetValidEntry(key, out var cached))
            return cached;
        var stale = TryGetStaleEntry(key, out var staleCached) ? staleCached : Array.Empty<IPAddress>();

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(hostOrIp, ct);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (v4.Length == 0)
                v4 = await ResolveIpv4ViaDohAsync(hostOrIp, ct).ConfigureAwait(false);
            if (v4.Length == 0 && stale.Length > 0)
            {
                PutEntry(key, stale, TimeSpan.FromSeconds(45));
                Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}'");
                return stale;
            }
            PutEntry(key, v4, v4.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return v4;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var doh = await ResolveIpv4ViaDohAsync(hostOrIp, ct).ConfigureAwait(false);
            if (doh.Length == 0 && stale.Length > 0)
            {
                PutEntry(key, stale, TimeSpan.FromSeconds(45));
                Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}'");
                return stale;
            }
            PutEntry(key, doh, doh.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return doh;
        }
    }

    public static IPAddress[] ResolveIpv4(string hostOrIp, TimeSpan? ttl = null)
    {
        if (TryParseIpv4(hostOrIp, out var literal))
            return new[] { literal };

        var key = NormalizeKey(hostOrIp);
        if (string.IsNullOrEmpty(key))
            return Array.Empty<IPAddress>();

        if (TryGetValidEntry(key, out var cached))
            return cached;
        var stale = TryGetStaleEntry(key, out var staleCached) ? staleCached : Array.Empty<IPAddress>();

        try
        {
            var addrs = Dns.GetHostAddresses(hostOrIp);
            var v4 = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (v4.Length == 0)
                v4 = ResolveIpv4ViaDohBlocking(hostOrIp);
            if (v4.Length == 0 && stale.Length > 0)
            {
                PutEntry(key, stale, TimeSpan.FromSeconds(45));
                Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}'");
                return stale;
            }
            PutEntry(key, v4, v4.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return v4;
        }
        catch
        {
            var doh = ResolveIpv4ViaDohBlocking(hostOrIp);
            if (doh.Length == 0 && stale.Length > 0)
            {
                PutEntry(key, stale, TimeSpan.FromSeconds(45));
                Logger.Warning($"[DNS-CACHE] Using stale IPv4 cache for '{hostOrIp}'");
                return stale;
            }
            PutEntry(key, doh, doh.Length == 0 ? NegativeTtl : (ttl ?? DefaultTtl));
            return doh;
        }
    }

    private static IPAddress[] ResolveIpv4ViaDohBlocking(string hostOrIp)
        => Task.Run(() => ResolveIpv4ViaDohAsync(hostOrIp, CancellationToken.None)).GetAwaiter().GetResult();

    private static async Task<IPAddress[]> ResolveIpv4ViaDohAsync(string host, CancellationToken ct)
    {
        var queryHost = NormalizeKey(host);
        if (string.IsNullOrWhiteSpace(queryHost))
            return Array.Empty<IPAddress>();

        foreach (var resolver in DohBootstrapResolvers)
        {
            try
            {
                var url = $"https://{resolver}/dns-query?name={Uri.EscapeDataString(queryHost)}&type=A";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/dns-json");
                using var resp = await DohHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    continue;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("Answer", out var answers) ||
                    answers.ValueKind != JsonValueKind.Array)
                    continue;

                var result = new List<IPAddress>();
                foreach (var answer in answers.EnumerateArray())
                {
                    if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1)
                        continue;
                    if (!answer.TryGetProperty("data", out var data))
                        continue;
                    if (IPAddress.TryParse(data.GetString(), out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(ip);
                }

                if (result.Count > 0)
                {
                    Logger.Info($"[DNS-DOH] {queryHost} -> {string.Join(", ", result.Select(x => x.ToString()))} via {resolver}");
                    return result.ToArray();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning($"[DNS-DOH] {queryHost} via {resolver} failed: {ex.Message}");
            }
        }

        return Array.Empty<IPAddress>();
    }

    public static async Task<IPAddress?> ResolveFirstIpv4Async(
        string hostOrIp,
        CancellationToken ct,
        TimeSpan? ttl = null)
    {
        var addrs = await ResolveIpv4Async(hostOrIp, ct, ttl);
        return addrs.Length > 0 ? addrs[0] : null;
    }

    public static IPAddress? ResolveFirstIpv4(string hostOrIp, TimeSpan? ttl = null)
    {
        var addrs = ResolveIpv4(hostOrIp, ttl);
        return addrs.Length > 0 ? addrs[0] : null;
    }

    private static bool TryParseIpv4(string value, out IPAddress ip)
    {
        if (IPAddress.TryParse(value?.Trim(), out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            ip = parsed;
            return true;
        }

        ip = IPAddress.None;
        return false;
    }

    private static string NormalizeKey(string? hostOrIp)
        => (hostOrIp ?? string.Empty).Trim().ToLowerInvariant();

    private static bool TryGetValidEntry(string key, out IPAddress[] addresses)
    {
        if (Cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc > DateTime.UtcNow)
            {
                addresses = entry.Addresses;
                return true;
            }

            Cache.TryRemove(key, out _);
        }

        addresses = Array.Empty<IPAddress>();
        return false;
    }

    private static bool TryGetStaleEntry(string key, out IPAddress[] addresses)
    {
        if (Cache.TryGetValue(key, out var entry) &&
            entry.ExpiresAtUtc <= DateTime.UtcNow &&
            entry.Addresses.Length > 0)
        {
            addresses = entry.Addresses;
            return true;
        }

        addresses = Array.Empty<IPAddress>();
        return false;
    }

    private static void PutEntry(string key, IPAddress[] addresses, TimeSpan ttl)
    {
        Cache[key] = new CacheEntry
        {
            Addresses = addresses,
            ExpiresAtUtc = DateTime.UtcNow.Add(ttl)
        };
    }
}

