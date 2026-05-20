using System.Net;

namespace AppTunnel.Services;

public sealed record WireGuardProfile(
    string PrivateKey,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> DnsServers,
    string PeerPublicKey,
    string? PreSharedKey,
    IReadOnlyList<int> PeerReserved,
    string EndpointHost,
    int EndpointPort,
    IReadOnlyList<string> AllowedIps,
    int? PersistentKeepalive,
    int? Mtu);

public static class WireGuardConfigParser
{
    public static bool TryParse(string config, out WireGuardProfile profile, out string error)
    {
        try
        {
            profile = Parse(config);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            profile = Empty;
            error = ex.Message;
            return false;
        }
    }

    public static WireGuardProfile Parse(string config)
    {
        if (string.IsNullOrWhiteSpace(config))
            throw new FormatException(LocalizationService.Instance.T("کانفیگ WireGuard را وارد کنید"));

        var interfaceValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var peers = new List<Dictionary<string, List<string>>>();
        Dictionary<string, List<string>>? current = null;
        var currentSection = "";

        foreach (var rawLine in config.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (currentSection.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                {
                    current = interfaceValues;
                    continue;
                }

                if (currentSection.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                {
                    current = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    peers.Add(current);
                    continue;
                }

                current = null;
                continue;
            }

            if (current == null)
                throw new FormatException(LocalizationService.Instance.Format("بخش WireGuard ناشناخته یا پشتیبانی‌نشده است: {0}", currentSection));

            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new FormatException(LocalizationService.Instance.Format("خط WireGuard نامعتبر است: {0}", line));

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (!current.TryGetValue(key, out var values))
            {
                values = new List<string>();
                current[key] = values;
            }
            values.Add(value);
        }

        if (peers.Count == 0)
            throw new FormatException(LocalizationService.Instance.T("کانفیگ WireGuard باید یک بخش [Peer] داشته باشد"));
        if (peers.Count > 1)
            throw new FormatException(LocalizationService.Instance.T("در این نسخه فقط کانفیگ WireGuard تک-peer پشتیبانی می‌شود"));

        var peer = peers[0];
        var privateKey = Required(interfaceValues, "PrivateKey", "کلید خصوصی WireGuard وارد نشده است");
        var addresses = SplitCsv(Required(interfaceValues, "Address", "آدرس Interface در کانفیگ WireGuard وارد نشده است"));
        var peerPublicKey = Required(peer, "PublicKey", "کلید عمومی Peer در کانفیگ WireGuard وارد نشده است");
        var endpoint = Required(peer, "Endpoint", "Endpoint در کانفیگ WireGuard وارد نشده است");
        var allowedIps = SplitCsv(Required(peer, "AllowedIPs", "AllowedIPs در کانفیگ WireGuard وارد نشده است"));

        if (!TryParseEndpoint(endpoint, out var endpointHost, out var endpointPort))
            throw new FormatException(LocalizationService.Instance.Format("Endpoint WireGuard نامعتبر است: {0}", endpoint));

        var dnsServers = Optional(interfaceValues, "DNS") is { Length: > 0 } dns
            ? SplitCsv(dns)
            : Array.Empty<string>();
        var peerReserved = Optional(peer, "Reserved") is { Length: > 0 } reserved
            ? ParseReservedBytes(reserved)
            : Array.Empty<int>();

        int? keepalive = null;
        if (Optional(peer, "PersistentKeepalive") is { Length: > 0 } keepaliveRaw)
        {
            if (!int.TryParse(keepaliveRaw, out var parsed) || parsed < 0)
                throw new FormatException(LocalizationService.Instance.T("PersistentKeepalive باید عدد مثبت باشد"));
            keepalive = parsed;
        }

        int? mtu = null;
        if (Optional(interfaceValues, "MTU") is { Length: > 0 } mtuRaw)
        {
            if (!int.TryParse(mtuRaw, out var parsed) || parsed < 576 || parsed > 9000)
                throw new FormatException(LocalizationService.Instance.T("MTU WireGuard باید بین 576 تا 9000 باشد"));
            mtu = parsed;
        }

        return new WireGuardProfile(
            privateKey,
            addresses,
            dnsServers,
            peerPublicKey,
            Optional(peer, "PresharedKey"),
            peerReserved,
            endpointHost,
            endpointPort,
            allowedIps,
            keepalive,
            mtu);
    }

    private static WireGuardProfile Empty { get; } = new(
        "",
        Array.Empty<string>(),
        Array.Empty<string>(),
        "",
        null,
        Array.Empty<int>(),
        "",
        0,
        Array.Empty<string>(),
        null,
        null);

    private static string Required(Dictionary<string, List<string>> values, string key, string error)
    {
        var value = Optional(values, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException(LocalizationService.Instance.T(error));
        return value.Trim();
    }

    private static string Optional(Dictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var matches) ? matches.LastOrDefault() ?? "" : "";

    private static string[] SplitCsv(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int[] ParseReservedBytes(string value)
    {
        var parts = SplitCsv(value);
        if (parts.Length != 3)
            throw new FormatException("Reserved WireGuard باید سه عدد بین 0 تا 255 باشد");

        var result = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var parsed) || parsed < 0 || parsed > 255)
                throw new FormatException("Reserved WireGuard باید سه عدد بین 0 تا 255 باشد");
            result[i] = parsed;
        }

        return result;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        var semicolon = line.IndexOf(';');
        var cut = hash < 0 ? semicolon : semicolon < 0 ? hash : Math.Min(hash, semicolon);
        return cut < 0 ? line : line[..cut];
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = "";
        port = 0;

        endpoint = endpoint.Trim();
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            if (close <= 1 || close + 2 > endpoint.Length || endpoint[close + 1] != ':')
                return false;
            host = endpoint[1..close];
            return int.TryParse(endpoint[(close + 2)..], out port) && IsValidPort(port);
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == endpoint.Length - 1)
            return false;

        host = endpoint[..lastColon].Trim();
        if (host.Count(c => c == ':') > 0)
            return false;

        return host.Length > 0 &&
               (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) != UriHostNameType.Unknown) &&
               int.TryParse(endpoint[(lastColon + 1)..], out port) &&
               IsValidPort(port);
    }

    private static bool IsValidPort(int port) => port is > 0 and <= 65535;
}
