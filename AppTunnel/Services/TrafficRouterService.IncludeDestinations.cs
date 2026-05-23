using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Linq;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    #region Included Destinations

    /// <summary>
    /// Replace the entire include list. Resolves domains to IPs.
    /// </summary>
    public void SetIncludedDestinations(IEnumerable<string> entries, bool resolveDomains = true)
    {
        lock (_destinationListLock)
        {
            _includedIps.Clear();
            _includedEntries.Clear();
            _includedDomainRules.Clear();
            foreach (var entry in entries)
                AddIncludedDestinationCore(entry, installRoutes: _isRunning && resolveDomains, resolveDomains);
        }
    }

    /// <summary>
    /// Add a single domain or IP to the include list.
    /// Included destinations will be forced through the VPN tunnel regardless of
    /// whether the source application is in the target tunnel apps list.
    /// </summary>
    public void AddIncludedDestination(string entry)
    {
        lock (_destinationListLock)
            AddIncludedDestinationCore(entry, installRoutes: _isRunning, resolveDomains: _isRunning);
    }

    private void AddIncludedDestinationCore(string entry, bool installRoutes, bool resolveDomains = true)
    {
        var originalEntry = entry.Trim();
        entry = NormalizeDestinationEntry(originalEntry);
        if (string.IsNullOrEmpty(entry)) return;
        if (_includedEntries.ContainsKey(entry)) return;

        HashSet<uint> ips;
        if (!resolveDomains && !IPAddress.TryParse(entry, out _))
        {
            _includedDomainRules[entry] = true;
            ips = new HashSet<uint>();
            Logger.Info($"[INCLUDE] Queued domain '*.{entry}' (DNS resolve deferred until router is up)");
            _includedEntries[entry] = ips;
            return;
        }

        ips = ResolveDestinationEntry(entry, "[INCLUDE]", DestinationDnsPolicy.TunnelRequired, out var unsupportedIp);
        if (unsupportedIp)
        {
            _includedEntries[entry] = ips;
            return;
        }

        if (IPAddress.TryParse(entry, out var ip))
        {
            Logger.Info($"[INCLUDE] Added IP {entry}");
        }
        else
        {
            _includedDomainRules[entry] = true;
            var normalizedSuffix = originalEntry.Equals(entry, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $" (from '{originalEntry}')";
            Logger.Info($"[INCLUDE] Added domain rule '*.{entry}'{normalizedSuffix} → {ips.Count} current IPs");
        }

        foreach (var nbo in ips)
            _includedIps[nbo] = true;
        _includedEntries[entry] = ips;

        if (installRoutes)
            InstallIncludedRoutes(ips);
    }

    /// <summary>
    /// Remove a single domain or IP from the include list.
    /// </summary>
    public void RemoveIncludedDestination(string entry)
    {
        entry = NormalizeDestinationEntry(entry);
        lock (_destinationListLock)
        {
            if (_includedEntries.TryRemove(entry, out var ips))
            {
                foreach (var nbo in ips)
                {
                    if (IsIpPresentInEntries(_includedEntries, nbo))
                        continue;

                    _includedIps.TryRemove(nbo, out _);
                    if (_ipToProcess.TryGetValue(nbo, out var owner) &&
                        string.Equals(owner, "[INCLUDE]", StringComparison.OrdinalIgnoreCase))
                    {
                        _ipToProcess.TryRemove(nbo, out _);
                        _ipRefCount.TryRemove(nbo, out _);
                        TryRemoveHostRoute(nbo);
                    }
                }
                _includedDomainRules.TryRemove(entry, out _);
                Logger.Info($"[INCLUDE] Removed '{entry}'");
            }
        }
    }

    private bool IsIncludedDestination(uint dstIpNbo)
        => _includedIps.ContainsKey(dstIpNbo);

    private bool IsIncludedDomain(string host)
        => IsDomainRuleMatch(_includedDomainRules, host);

    private void RefreshIncludedDestinations(bool installRoutes)
    {
        lock (_destinationListLock)
        {
            foreach (var entry in _includedEntries.Keys.ToList())
            {
                var oldIps = _includedEntries.TryGetValue(entry, out var existing)
                    ? existing
                    : new HashSet<uint>();
                var newIps = ResolveDestinationEntry(entry, "[INCLUDE]", DestinationDnsPolicy.TunnelRequired, out _);
                if (newIps.Count == 0 &&
                    oldIps.Count > 0 &&
                    !IPAddress.TryParse(entry, out _))
                {
                    // Resolver timeouts must not delete still-valid CDN routes.
                    // In filtered networks this creates app-visible cutoffs for
                    // hosts such as githubusercontent.com.
                    _includedEntries[entry] = oldIps;
                    if (installRoutes)
                        InstallIncludedRoutes(oldIps);
                    Logger.Warning($"[INCLUDE] Keeping previous IPs for '{entry}' because refresh returned no IPv4 addresses");
                    continue;
                }

                foreach (var nbo in oldIps.Except(newIps).ToList())
                {
                    _includedEntries[entry] = newIps;
                    if (IsIpPresentInEntries(_includedEntries, nbo))
                        continue;

                    _includedIps.TryRemove(nbo, out _);
                    if (_ipToProcess.TryGetValue(nbo, out var owner) &&
                        string.Equals(owner, "[INCLUDE]", StringComparison.OrdinalIgnoreCase))
                    {
                        _ipToProcess.TryRemove(nbo, out _);
                        _ipRefCount.TryRemove(nbo, out _);
                        TryRemoveHostRoute(nbo);
                    }
                }

                _includedEntries[entry] = newIps;
                foreach (var nbo in newIps)
                    _includedIps[nbo] = true;

                if (installRoutes)
                    InstallIncludedRoutes(newIps);
            }
        }
    }

    private void InstallIncludedRoutes(IEnumerable<uint> ips)
    {
        if (!_isRunning)
            return;

        foreach (var nbo in ips)
        {
            if (IsExcludedDestination(nbo))
                continue;

            var ip = new IPAddress(BitConverter.GetBytes(nbo));
            _ipToProcess[nbo] = "[INCLUDE]";
            EnsureHostRouteViaVpn(nbo, ip);
        }
    }

    private HashSet<uint> ResolveDestinationEntry(
        string entry,
        string logPrefix,
        DestinationDnsPolicy policy,
        out bool unsupportedIp)
    {
        unsupportedIp = false;
        var ips = new HashSet<uint>();

        if (IPAddress.TryParse(entry, out var ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
            {
                unsupportedIp = true;
                Logger.Warning($"{logPrefix} IPv6 destination '{entry}' is not supported by IPv4 route rules yet");
                return ips;
            }

            ips.Add(BitConverter.ToUInt32(ip.GetAddressBytes(), 0));
            return ips;
        }

        try
        {
            var tunnelDns = CreateTunnelDnsResolveOptions();

            foreach (var addr in DnsResolverCache.ResolveIpv4ForRoutingList(entry, policy, tunnelDns))
            {
                if (!IsUsableRouteIpv4(addr))
                    continue;
                ips.Add(BitConverter.ToUInt32(addr.GetAddressBytes(), 0));
            }

            if (ips.Count > 0 && policy == DestinationDnsPolicy.TunnelRequired)
                Logger.Info($"{logPrefix} Resolved '{entry}' via tunnel → {string.Join(", ", ips.Select(nbo => new IPAddress(BitConverter.GetBytes(nbo))))}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"{logPrefix} Could not resolve '{entry}': {ex.Message}");
        }

        return ips;
    }

    private static bool IsIpPresentInEntries(
        ConcurrentDictionary<string, HashSet<uint>> entries,
        uint ip)
        => entries.Values.Any(set => set.Contains(ip));

    private static bool IsUsableRouteIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = ip.GetAddressBytes();
        return b[0] != 0 &&
               b[0] != 10 &&
               b[0] != 127 &&
               b[0] < 224 &&
               !(b[0] == 169 && b[1] == 254) &&
               !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
               !(b[0] == 192 && b[1] == 168);
    }

    private static bool IsDomainRuleMatch(
        ConcurrentDictionary<string, bool> domainRules,
        string host)
    {
        host = NormalizeDomainForRule(host);
        if (string.IsNullOrEmpty(host))
            return false;

        foreach (var rule in domainRules.Keys)
        {
            var normalizedRule = NormalizeDomainForRule(rule);
            if (host.Equals(normalizedRule, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + normalizedRule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDomainForRule(string value)
        => NormalizeDestinationEntry(value).TrimStart('.').ToLowerInvariant();

    #endregion
}
