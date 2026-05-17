using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    #region Included Destinations

    /// <summary>
    /// Replace the entire include list. Resolves domains to IPs.
    /// </summary>
    public void SetIncludedDestinations(IEnumerable<string> entries)
    {
        lock (_destinationListLock)
        {
            _includedIps.Clear();
            _includedEntries.Clear();
            _includedDomainRules.Clear();
            foreach (var entry in entries)
                AddIncludedDestinationCore(entry, installRoutes: _isRunning);
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
            AddIncludedDestinationCore(entry, installRoutes: _isRunning);
    }

    private void AddIncludedDestinationCore(string entry, bool installRoutes)
    {
        var originalEntry = entry.Trim();
        entry = NormalizeDestinationEntry(originalEntry);
        if (string.IsNullOrEmpty(entry)) return;
        if (_includedEntries.ContainsKey(entry)) return;

        var ips = ResolveDestinationEntry(entry, "[INCLUDE]", out var unsupportedIp);
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
                var newIps = ResolveDestinationEntry(entry, "[INCLUDE]", out _);

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

    private static HashSet<uint> ResolveDestinationEntry(string entry, string logPrefix, out bool unsupportedIp)
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
            foreach (var addr in DnsResolverCache.ResolveIpv4(entry))
                ips.Add(BitConverter.ToUInt32(addr.GetAddressBytes(), 0));
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
