using System.Net;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    #region Excluded Destinations

    /// <summary>
    /// Replace the entire exclude list. Resolves domains to IPs.
    /// </summary>
    public void SetExcludedDestinations(IEnumerable<string> entries, bool resolveDomains = true)
    {
        List<(uint nbo, IPAddress ip)>? deferredPurge = resolveDomains ? null : new();
        lock (_destinationListLock)
        {
            _excludedIps.Clear();
            _excludedEntries.Clear();
            _excludedDomainRules.Clear();
            foreach (var entry in entries)
                AddExcludedDestinationCore(entry, resolveDomains, deferredPurge);
        }

        if (deferredPurge is { Count: > 0 })
        {
            var batch = deferredPurge;
            _ = Task.Run(() =>
            {
                foreach (var (nbo, ip) in batch)
                    PurgeRouteForExcludedIp(nbo, ip);
            });
        }
    }

    /// <summary>
    /// Add a single domain or IP to the exclude list.
    /// If the tunnel is already running and a host route for any of the resolved
    /// IPs was previously installed (e.g. the user browsed there before excluding
    /// it), the route is removed immediately so that subsequent connections bypass
    /// the VPN rather than continuing to use the stale route.
    /// Also removes stale routes left over from a previously crashed session
    /// (even if _isRunning is false at this point), since those routes are not
    /// tracked in _addedRoutes but can still silently redirect traffic to the VPN.
    /// </summary>
    public void AddExcludedDestination(string entry)
    {
        lock (_destinationListLock)
            AddExcludedDestinationCore(entry, resolveDomains: _isRunning);
    }

    private void AddExcludedDestinationCore(string entry, bool resolveDomains = true, List<(uint nbo, IPAddress ip)>? deferredPurge = null)
    {
        var originalEntry = entry.Trim();
        entry = NormalizeDestinationEntry(originalEntry);
        if (string.IsNullOrEmpty(entry)) return;
        if (_excludedEntries.ContainsKey(entry)) return;

        HashSet<uint> ips;
        if (!resolveDomains && !IPAddress.TryParse(entry, out _))
        {
            _excludedDomainRules[entry] = true;
            ips = new HashSet<uint>();
            Logger.Info($"[EXCLUDE] Queued domain '*.{entry}' (DNS resolve deferred until router is up)");
            _excludedEntries[entry] = ips;
            return;
        }

        ips = ResolveDestinationEntry(entry, "[EXCLUDE]", out var unsupportedIp);
        if (unsupportedIp)
        {
            _excludedEntries[entry] = ips;
            return;
        }

        if (IPAddress.TryParse(entry, out var ip))
        {
            Logger.Info($"[EXCLUDE] Added IP {entry}");
        }
        else
        {
            _excludedDomainRules[entry] = true;
            var normalizedSuffix = originalEntry.Equals(entry, StringComparison.OrdinalIgnoreCase)
                ? ""
                : $" (from '{originalEntry}')";
            Logger.Info($"[EXCLUDE] Added domain rule '*.{entry}'{normalizedSuffix} → {ips.Count} current IPs");
        }

        foreach (var nbo in ips)
        {
            _excludedIps[nbo] = true;
            var ipAddr = new IPAddress(BitConverter.GetBytes(nbo));
            if (deferredPurge != null)
                deferredPurge.Add((nbo, ipAddr));
            else
                PurgeRouteForExcludedIp(nbo, ipAddr);
        }
        _excludedEntries[entry] = ips;
    }

    /// <summary>
    /// Removes any /32 host route for <paramref name="nbo"/> that may exist in
    /// the Windows routing table — regardless of whether TunnelX added it in this
    /// session (tracked via <see cref="_addedRoutes"/>) or it is a stale route
    /// left behind by a previous session that crashed without cleaning up.
    /// Also cancels any pending delayed-removal timer and clears the per-session
    /// flow-tracking state for the IP, so the egress/ingress sniff loops stop
    /// attributing bytes and the NAT table stops matching replies.
    /// </summary>
    private void PurgeRouteForExcludedIp(uint nbo, IPAddress ipForLog)
    {
        // Cancel any pending delayed removal (superseded by this explicit remove).
        if (_pendingRouteRemoval.TryRemove(nbo, out var pendingCts))
            try { pendingCts.Cancel(); } catch { }

        // Clear in-session tracking state.
        _addedRoutes.TryRemove(nbo, out _);
        _ipToProcess.TryRemove(nbo, out _);
        _ipRefCount.TryRemove(nbo, out _);

        // Force-delete from the Windows routing table using route.exe.
        // This covers:
        //   1. Routes that TunnelX added in the current session.
        //   2. Stale routes from a previous session that crashed before StopAsync
        //      could call RemoveAllHostRoutes().
        // We do NOT gate this on _isRunning — stale routes can be present even
        // before the tunnel starts, and we must clean them up proactively.
        ForceDeleteRouteFromWindows(ipForLog);
        AddExcludedDirectRoute(nbo, ipForLog);
    }

    /// <summary>
    /// Unconditionally removes the /32 host route for <paramref name="ip"/> from
    /// the Windows routing table via route.exe delete. Does not touch _addedRoutes.
    /// </summary>
    internal void ForceDeleteRouteFromWindows(IPAddress ip)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = $"delete {ip} mask 255.255.255.255",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(400);
        }
        catch { }
    }

    /// <summary>
    /// Remove a single domain or IP from the exclude list.
    /// </summary>
    public void RemoveExcludedDestination(string entry)
    {
        entry = NormalizeDestinationEntry(entry);
        lock (_destinationListLock)
        {
            if (_excludedEntries.TryRemove(entry, out var ips))
            {
                foreach (var nbo in ips)
                {
                    if (!IsIpPresentInEntries(_excludedEntries, nbo))
                    {
                        _excludedIps.TryRemove(nbo, out _);
                        RemoveExcludedDirectRoute(nbo);
                    }
                }
                _excludedDomainRules.TryRemove(entry, out _);
                Logger.Info($"[EXCLUDE] Removed '{entry}'");
            }
        }
    }

    private bool IsExcludedDestination(uint dstIpNbo)
        => _excludedIps.ContainsKey(dstIpNbo);

    private bool IsExcludedDomain(string host)
        => IsDomainRuleMatch(_excludedDomainRules, host);

    private void RefreshExcludedDestinations()
    {
        lock (_destinationListLock)
        {
            foreach (var entry in _excludedEntries.Keys.ToList())
            {
                var oldIps = _excludedEntries.TryGetValue(entry, out var existing)
                    ? existing
                    : new HashSet<uint>();
                var newIps = ResolveDestinationEntry(entry, "[EXCLUDE]", out _);

                _excludedEntries[entry] = newIps;
                foreach (var nbo in oldIps.Except(newIps).ToList())
                {
                    if (!IsIpPresentInEntries(_excludedEntries, nbo))
                    {
                        _excludedIps.TryRemove(nbo, out _);
                        RemoveExcludedDirectRoute(nbo);
                    }
                }

                foreach (var nbo in newIps)
                {
                    _excludedIps[nbo] = true;
                    PurgeRouteForExcludedIp(nbo, new IPAddress(BitConverter.GetBytes(nbo)));
                }
            }
        }
    }

    private void RefreshDestinationLists(bool installIncludedRoutes)
    {
        RefreshExcludedDestinations();
        RefreshIncludedDestinations(installIncludedRoutes);
    }

    private void RefreshExcludedDirectRoutes()
    {
        if (!_fullRouteEnabled)
            return;

        foreach (var nbo in _excludedIps.Keys)
            AddExcludedDirectRoute(nbo, new IPAddress(BitConverter.GetBytes(nbo)));
    }

    private void AddExcludedDirectRoute(uint nbo, IPAddress ip)
    {
        if (!_fullRouteEnabled)
            return;
        if (string.IsNullOrWhiteSpace(_physicalGatewayIp) || _physicalInterfaceIndex <= 0)
            return;

        TryRunRouteCommand($"delete {ip}", out _);
        if (TryRunRouteCommand(
            $"add {ip} mask 255.255.255.255 {_physicalGatewayIp} IF {_physicalInterfaceIndex} METRIC 1",
            out var stderr))
        {
            _excludedDirectRoutes[nbo] = true;
            return;
        }

        Logger.Warning($"[EXCLUDE] Failed to add direct full-route bypass for {ip}: {stderr.Trim()}");
    }

    private void RemoveExcludedDirectRoute(uint nbo)
    {
        if (!_excludedDirectRoutes.TryRemove(nbo, out _))
            return;

        TryRunRouteCommand($"delete {new IPAddress(BitConverter.GetBytes(nbo))}", out _);
    }

    private void RemoveExcludedDirectRoutes()
    {
        foreach (var nbo in _excludedDirectRoutes.Keys.ToList())
            RemoveExcludedDirectRoute(nbo);
        _excludedDirectRoutes.Clear();
    }

    #endregion
}
