using System.Net;
using System.Net.Sockets;

namespace AppTunnel.Services;

public partial class TrafficRouterService
{
    // Well-known hostnames used by connectivity checks. Resolved at
    // runtime via DNS so no hardcoded IPs remain in the codebase.
    private const string IntranetCheckHost = "isna.ir";
    private const string InternationalCheckHost = "google.com";

    private async Task RunConnectivityChecks()
    {
        try
        {
            // Give VPN a moment to fully settle.
            await Task.Delay(_vpnServerIsUdpOnly ? 3000 : 1000);

            using var ping = new System.Net.NetworkInformation.Ping();

            // 1. Reachability of the tunnel server.
            //    For TCP-based tunnels (V2Ray/Xray/OpenVPN-tcp): TCP-connect via the
            //    physical NIC. ICMP is useless because CDNs (Cloudflare, Arvancloud)
            //    never reply to pings. We connect by the original hostname so any
            //    per-resolve CDN IP rotation matches what sing-box actually uses.
            //
            //    For UDP-only tunnels (WireGuard): a TCP probe to the WG UDP port
            //    will ALWAYS time out, even when the tunnel is perfectly healthy.
            //    We do a best-effort UDP datagram instead — it cannot actually
            //    confirm WireGuard handshake success, but it at least won't lie.
            if (_vpnServerIsUdpOnly)
            {
                try
                {
                    var serverIp = await DnsResolverCache.ResolveFirstIpv4Async(_vpnServerHost, CancellationToken.None);
                    if (serverIp != null)
                    {
                        using var udp = new UdpClient(AddressFamily.InterNetwork);
                        udp.Connect(serverIp, _vpnServerPort);
                        var probe = new byte[] { 0x00 };
                        await udp.SendAsync(probe, probe.Length);
                        Logger.Info($"[CONN-CHECK] UDP tunnel server {_vpnServerHost}:{_vpnServerPort} ({serverIp}, direct): probe sent — UDP is stateless; actual tunnel health is observed via traffic flow");
                    }
                    else
                    {
                        Logger.Warning($"[CONN-CHECK] UDP tunnel server {_vpnServerHost}:{_vpnServerPort}: DNS resolution failed");
                    }
                }
                catch (Exception ex) { Logger.Warning($"[CONN-CHECK] UDP tunnel server {_vpnServerHost}:{_vpnServerPort} probe failed: {ex.Message}"); }
            }
            else
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var tcpServer = new TcpClient();
                    using var serverCts = new System.Threading.CancellationTokenSource(3000);
                    await tcpServer.ConnectAsync(_vpnServerHost, _vpnServerPort, serverCts.Token);
                    sw.Stop();
                    Logger.Info($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort} (direct): {sw.ElapsedMilliseconds}ms — reachable");
                }
                catch (OperationCanceledException) { Logger.Warning($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort}: timeout (3000ms) — server unreachable or port blocked"); }
                catch (Exception ex) { Logger.Warning($"[CONN-CHECK] TCP tunnel server {_vpnServerHost}:{_vpnServerPort} failed: {ex.Message}"); }
            }

            // 2. Resolve an Iranian intranet hostname and ping it via the
            //    default route (physical NIC). Confirms the local intranet
            //    still works while the VPN is connected.
            try
            {
                var intraIp = await DnsResolverCache.ResolveFirstIpv4Async(IntranetCheckHost, CancellationToken.None);
                if (intraIp != null)
                {
                    var reply = ping.Send(intraIp, 2000);
                    Logger.Info($"[CONN-CHECK] Ping intranet {IntranetCheckHost} ({intraIp}, default route): {reply.Status}, {reply.RoundtripTime}ms");
                }
                else
                    Logger.Warning($"[CONN-CHECK] DNS {IntranetCheckHost}: no IPv4 address");
            }
            catch (Exception ex) { Logger.Warning($"[CONN-CHECK] Ping intranet failed: {ex.Message}"); }

            // 3. Resolve an international hostname and ping it via the default
            //    route. This is diagnostic only: on some networks this can
            //    legitimately succeed without indicating full-route VPN mode.
            IPAddress? intlIp = null;
            try
            {
                intlIp = await DnsResolverCache.ResolveFirstIpv4Async(InternationalCheckHost, CancellationToken.None);
            }
            catch { }

            if (intlIp != null)
            {
                uint intlNbo = BitConverter.ToUInt32(intlIp.GetAddressBytes(), 0);
                bool routeAlreadyPresent = _addedRoutes.ContainsKey(intlNbo);

                // 3. Ping international IP via default route. Treat success as a
                //    reachability observation, not proof of a split-tunnel failure.
                //    SKIP when a /32 host route for this IP is already installed (e.g.
                //    chrome started connecting before this check ran) — that route would
                //    win over the default route and give a false "Success, 0ms" result.
                if (!routeAlreadyPresent)
                {
                    try
                    {
                        var reply = ping.Send(intlIp, 3000);
                        if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                            Logger.Info($"[CONN-CHECK] Ping {InternationalCheckHost} ({intlIp}, default route): {reply.Status}, {reply.RoundtripTime}ms — direct route is reachable; not a leak by itself");
                        else
                            Logger.Info($"[CONN-CHECK] Ping {InternationalCheckHost} ({intlIp}, default route): {reply.Status} — OK, international not reachable via default route (split-tunnel working)");
                    }
                    catch (Exception ex) { Logger.Warning($"[CONN-CHECK] Ping international (default route) failed: {ex.Message}"); }
                }
                else
                {
                    Logger.Info($"[CONN-CHECK] Ping {InternationalCheckHost} default-route check skipped — host route already present (app traffic in flight)");
                }

                // 4. TCP-connect to the same international IP via VPN — confirms the
                //    tunnel can actually reach international destinations.
                //    sing-box VLESS/VMess outbound does not forward ICMP, so a regular
                //    Ping.Send() would bounce locally in the TUN and report a fake 0 ms.
                //    A TCP handshake gives the real round-trip time through the tunnel.
                //    When the route was already installed by live traffic we don't remove
                //    it afterwards (it's in active use).
                try
                {
                    if (_vpnServerIsUdpOnly &&
                        !await WaitForWireGuardDataPlaneAsync(TimeSpan.FromSeconds(10)))
                    {
                        Logger.Info($"[CONN-CHECK] TCP {InternationalCheckHost} via VPN delayed — WireGuard has not observed inbound tunnel traffic yet");
                        return;
                    }

                    bool routeAdded = false;
                    if (!routeAlreadyPresent)
                    {
                        EnsureHostRouteViaVpn(intlNbo, intlIp);
                        routeAdded = true;
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var tcp = new System.Net.Sockets.TcpClient();
                    using var tcpCts = new System.Threading.CancellationTokenSource(3000);
                    await tcp.ConnectAsync(intlIp, 443, tcpCts.Token);
                    sw.Stop();
                    Logger.Info($"[CONN-CHECK] TCP {InternationalCheckHost} ({intlIp}:443, via VPN tunnel): {sw.ElapsedMilliseconds}ms");

                    // Do not remove the temp route immediately. TCP can still
                    // emit FIN/ACK or retransmit tail packets after ConnectAsync
                    // returns; immediate removal sends VPN-source packets toward
                    // the physical NIC, where leak-guard has to drop them.
                    // Let the normal delayed route cleanup remove it after the
                    // short grace window instead.
                    if (routeAdded && !_ipToProcess.ContainsKey(intlNbo))
                        ScheduleDelayedRouteRemoval(intlNbo);
                }
                catch (OperationCanceledException) { Logger.Warning($"[CONN-CHECK] TCP {InternationalCheckHost} via VPN: timeout (3000ms)"); }
                catch (Exception ex) { Logger.Warning($"[CONN-CHECK] TCP {InternationalCheckHost} via VPN failed: {ex.Message}"); }
            }
            else
                Logger.Warning($"[CONN-CHECK] Could not resolve {InternationalCheckHost} — skipping international checks");
        }
        catch { }
    }

    private async Task<bool> WaitForWireGuardDataPlaneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_isRunning && DateTime.UtcNow < deadline)
        {
            if (Interlocked.Read(ref _statVpnIngressSniffed) > 0 ||
                Interlocked.Read(ref _statNetInRewritten) > 0)
                return true;

            await Task.Delay(300);
        }

        return Interlocked.Read(ref _statVpnIngressSniffed) > 0 ||
               Interlocked.Read(ref _statNetInRewritten) > 0;
    }
}
