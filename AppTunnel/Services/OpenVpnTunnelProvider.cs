using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// ITunnelProvider implementation for OpenVPN.
/// Launches user-installed OpenVPN Community with a split-compatible temporary
/// config and waits for its network adapter to come Up. TunnelX does not bundle
/// OpenVPN.
/// </summary>
public class OpenVpnTunnelProvider : ITunnelProvider
{
    /// <summary>True when the .ovpn likely needs an OpenVPN <c>askpass</c> passphrase (encrypted private key).</summary>
    public static bool ConfigLikelyNeedsPrivateKeyPassphrase(string? config) =>
        !string.IsNullOrWhiteSpace(config) &&
        (config.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
         config.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase) ||
         config.Contains("askpass", StringComparison.OrdinalIgnoreCase));

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string OpenVpnWorkDir => Path.Combine(AppTunnel.App.AppDataDir, "openvpn");
    private static string TunnelXOpenVpnPidPath => Path.Combine(OpenVpnWorkDir, "tunnelx-openvpn.pid");
    private Process? _process;
    private int _vpnInterfaceIndex = -1;
    private string _routeGatewayIp = "";
    private string _connectedRemoteIp = "";
    private int _connectedRemotePort;
    private string _assignedLocalIp = "";
    private bool _initSequenceCompleted;
    private string _fallbackRemoteHost = "";
    private int _fallbackRemotePort;
    private readonly ConcurrentQueue<string> _recentOpenVpnOutput = new();

    public ConnectionStatus Status { get; } = new();

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct)
    {
        _vpnInterfaceIndex = -1;
        _routeGatewayIp = "";
        _connectedRemoteIp = "";
        _connectedRemotePort = 0;
        _assignedLocalIp = "";
        _initSequenceCompleted = false;
        _fallbackRemoteHost = "";
        _fallbackRemotePort = 0;
        while (_recentOpenVpnOutput.TryDequeue(out _)) { }
        Status.State = ConnectionState.Connecting;
        Status.Message = LocalizationService.Instance.T("در حال اجرای OpenVPN در حالت Split...");
        Logger.Info("[OpenVPN] ConnectAsync started");

        try
        {
            var openVpnExe = ResolveOpenVpnExecutable(config);
            if (string.IsNullOrWhiteSpace(openVpnExe))
            {
                Status.State = ConnectionState.Error;
                Status.Message = IsOpenVpnConnectInstalled()
                    ? LocalizationService.Instance.T("فقط OpenVPN Connect پیدا شد. برای Split Tunneling باید OpenVPN Community (openvpn.exe) هم نصب باشد.")
                    : LocalizationService.Instance.T("OpenVPN Community پیدا نشد. برای Split Tunneling باید openvpn.exe نصب باشد.");
                Logger.Error("[OpenVPN] Executable not found. Searched:");
                foreach (var p in GetCandidatePaths())
                    Logger.Error($"  '{p}' → {(File.Exists(p) ? "FOUND" : "not found")}");
                foreach (var p in GetOpenVpnConnectPaths())
                    Logger.Warning($"[OpenVPN] OpenVPN Connect check: '{p}' → {(Directory.Exists(p) || File.Exists(p) ? "FOUND (GUI only, not split-compatible)" : "not found")}");
                return false;
            }
            if (string.IsNullOrWhiteSpace(config.OpenVpnConfig))
            {
                Status.State = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.T("کانفیگ OpenVPN (.ovpn) وارد نشده است.");
                return false;
            }

            await KillStaleTunnelXOpenVpnProcessAsync();

            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Active, "راه‌اندازی OpenVPN");

            var preparedConfigPath = PrepareSplitCompatibleConfig(
                config.OpenVpnConfig,
                config.OpenVpnUsername,
                config.OpenVpnPassword,
                config.OpenVpnPrivateKeyPassword);
            var preparedRemotes = ExtractRemoteCandidates(File.ReadAllText(preparedConfigPath)).ToList();
            if (preparedRemotes.Count == 0)
            {
                Status.State = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.T("هیچ سرور remote قابل استفاده در فایل .ovpn باقی نمانده است. آدرس سرور، DNS یا نصب OpenVPN Community را بررسی کنید.");
                ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Fail, Status.Message);
                Logger.Error("[OpenVPN] Prepared config has no remote lines (all may have been skipped as private/local).");
                return false;
            }

            var firstRemote = preparedRemotes[0];
            _fallbackRemoteHost = firstRemote.host;
            _fallbackRemotePort = int.TryParse(firstRemote.port, out var fallbackPort) ? fallbackPort : 1194;

            var remoteHost = TryExtractRemoteHost(config.OpenVpnConfig);
            LogRemoteCandidates(config.OpenVpnConfig);
            Logger.Info($"[OpenVPN] Launching: {openVpnExe}");
            Logger.Info($"[OpenVPN] Prepared split config: {preparedConfigPath} (remotes={preparedRemotes.Count})");

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = openVpnExe,
                    Arguments = $"--config \"{preparedConfigPath}\" --verb 3",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            _process.Start();
            WriteTunnelXOpenVpnPid(_process.Id);
            _ = Task.Run(() => PumpOpenVpnOutputAsync(_process.StandardOutput, ct));
            _ = Task.Run(() => PumpOpenVpnOutputAsync(_process.StandardError, ct));
            Logger.Info($"[OpenVPN] Process started PID={_process.Id}");
            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Complete, "راه‌اندازی OpenVPN");

            Status.Message = LocalizationService.Instance.T("OpenVPN در حال اتصال است؛ مسیرهای پیش‌فرض آن برای Split Tunnel نادیده گرفته می‌شوند...");
            Logger.Info("[OpenVPN] Waiting up to 180s for VPN adapter to come Up...");

            var deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds);
                ConnectionProgressService.Report(
                    "tun_interface",
                    ConnectionProgressPhase.Active,
                    "منتظر بالا آمدن آداپتر OpenVPN... ({0}s)",
                    detailFormatArg: remaining.ToString());

                if (TryResolveVpnInterface(out var idx))
                {
                    Logger.Info($"[OpenVPN] Adapter ready: index={idx} init={_initSequenceCompleted} local={_assignedLocalIp}");
                    _vpnInterfaceIndex = idx;
                    break;
                }

                if (_process.HasExited)
                {
                    LogRecentOpenVpnOutput();
                    Status.State = ConnectionState.Error;
                    Status.Message = LocalizationService.Instance.Format("OpenVPN زودتر از اتصال بسته شد (exit={0})", _process.ExitCode);
                    ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Fail, Status.Message);
                    return false;
                }

                Status.Message = LocalizationService.Instance.Format("منتظر بالا آمدن آداپتر OpenVPN... ({0}s)", remaining);
                await Task.Delay(500, ct);
            }

            if (_vpnInterfaceIndex <= 0)
            {
                LogRecentOpenVpnOutput();
                Logger.Error("[OpenVPN] Adapter not found after timeout. Current NICs:");
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    Logger.Error($"  name='{nic.Name}' desc='{nic.Description}' status={nic.OperationalStatus}");
                Status.State = ConnectionState.Error;
                Status.Message = LocalizationService.Instance.T("آداپتور OpenVPN بالا نیامد. لاگ OpenVPN را بررسی کنید؛ ممکن است ریموت اول پاسخ ندهد یا احراز هویت/شبکه مشکل داشته باشد.");
                ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Fail, Status.Message);
                await KillProcessAsync();
                return false;
            }

            ConnectionProgressService.Report("tun_interface", ConnectionProgressPhase.Complete, "آداپتر OpenVPN آماده است");

            Status.State = ConnectionState.Connected;
            Status.ConnectedSince = DateTime.Now;
            Status.VpnInterfaceIndex = _vpnInterfaceIndex;
            Status.VpnLocalIp = GetInterfaceIpv4(_vpnInterfaceIndex);
            Status.VpnServerIp = !string.IsNullOrWhiteSpace(_connectedRemoteIp)
                ? _connectedRemoteIp
                : ResolveRemoteForRouting(remoteHost);
            Status.VpnServerPort = _connectedRemotePort;
            Status.VpnGatewayIp = _routeGatewayIp;
            Status.Message = LocalizationService.Instance.T("OpenVPN متصل شد (Split Tunnel)");
            Logger.Info($"[OpenVPN] Connected. LocalIP={Status.VpnLocalIp} Gateway={Status.VpnGatewayIp} Remote={Status.VpnServerIp}:{Status.VpnServerPort}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Status.State = ConnectionState.Disconnected;
            Status.Message = LocalizationService.Instance.T("اتصال لغو شد");
            await KillProcessAsync();
            return false;
        }
        catch (Exception ex)
        {
            Status.State = ConnectionState.Error;
            Status.Message = LocalizationService.Instance.Format("خطا: {0}", ex.Message);
            Logger.Error("OpenVpnTunnelProvider.ConnectAsync failed", ex);
            await KillProcessAsync();
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        Status.State = ConnectionState.Disconnecting;
        Status.Message = LocalizationService.Instance.T("در حال قطع اتصال OpenVPN...");
        await KillProcessAsync();
        _vpnInterfaceIndex = -1;
        Status.State = ConnectionState.Disconnected;
        Status.ConnectedSince = null;
        Status.VpnLocalIp = string.Empty;
        Status.VpnServerIp = string.Empty;
        Status.VpnServerPort = 0;
        Status.VpnGatewayIp = string.Empty;
        Status.VpnInterfaceIndex = -1;
        Status.Message = LocalizationService.Instance.T("قطع شد");
    }

    public bool IsInterfaceUp()
    {
        TryUpdateConnectedStatusFromCapturedState();

        if (_vpnInterfaceIndex < 0) return false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null && ipv4.Index == _vpnInterfaceIndex)
                    return nic.OperationalStatus == OperationalStatus.Up;
            }
        }
        catch { }
        return false;
    }

    private bool TryUpdateConnectedStatusFromCapturedState()
    {
        if (Status.State != ConnectionState.Connected)
            return false;
        if (string.IsNullOrWhiteSpace(_assignedLocalIp) ||
            string.IsNullOrWhiteSpace(_routeGatewayIp) ||
            string.IsNullOrWhiteSpace(_connectedRemoteIp) ||
            _connectedRemotePort <= 0)
            return false;

        var interfaceIndex = FindOpenVpnInterfaceIndex(_assignedLocalIp);
        if (interfaceIndex <= 0)
            return false;

        var changed =
            Status.VpnInterfaceIndex != interfaceIndex ||
            !string.Equals(Status.VpnLocalIp, _assignedLocalIp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Status.VpnGatewayIp, _routeGatewayIp, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Status.VpnServerIp, _connectedRemoteIp, StringComparison.OrdinalIgnoreCase) ||
            Status.VpnServerPort != _connectedRemotePort;

        if (!changed)
            return true;

        _vpnInterfaceIndex = interfaceIndex;
        Status.VpnInterfaceIndex = interfaceIndex;
        Status.VpnLocalIp = _assignedLocalIp;
        Status.VpnGatewayIp = _routeGatewayIp;
        Status.VpnServerIp = _connectedRemoteIp;
        Status.VpnServerPort = _connectedRemotePort;
        Status.Message = LocalizationService.Instance.T("OpenVPN متصل شد (Split Tunnel)");
        Logger.Warning($"[OpenVPN] Runtime endpoint changed. LocalIP={Status.VpnLocalIp} Gateway={Status.VpnGatewayIp} Remote={Status.VpnServerIp}:{Status.VpnServerPort} IF={Status.VpnInterfaceIndex}");
        return true;
    }

    private async Task KillProcessAsync()
    {
        var processId = _process?.Id;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch { }
        finally
        {
            try { _process?.Dispose(); } catch { }
            _process = null;
            DeleteTunnelXOpenVpnPid(processId);
        }
    }

    private static async Task KillStaleTunnelXOpenVpnProcessAsync()
    {
        try
        {
            if (!File.Exists(TunnelXOpenVpnPidPath))
                return;

            var raw = await File.ReadAllTextAsync(TunnelXOpenVpnPidPath);
            if (!int.TryParse(raw.Trim(), out var pid))
            {
                DeleteTunnelXOpenVpnPid(null);
                return;
            }

            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                DeleteTunnelXOpenVpnPid(pid);
                return;
            }

            if (!process.ProcessName.Equals("openvpn", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"[OpenVPN] Stale pid file ignored; PID {pid} is '{process.ProcessName}', not openvpn.");
                DeleteTunnelXOpenVpnPid(pid);
                return;
            }

            Logger.Warning($"[OpenVPN] Cleaning up stale TunnelX OpenVPN process PID={pid}");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            DeleteTunnelXOpenVpnPid(pid);
        }
        catch (ArgumentException)
        {
            DeleteTunnelXOpenVpnPid(null);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[OpenVPN] Stale process cleanup failed: {ex.Message}");
        }
    }

    private static void WriteTunnelXOpenVpnPid(int pid)
    {
        try
        {
            Directory.CreateDirectory(OpenVpnWorkDir);
            File.WriteAllText(TunnelXOpenVpnPidPath, pid.ToString(), Utf8NoBom);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[OpenVPN] Could not write pid file: {ex.Message}");
        }
    }

    private static void DeleteTunnelXOpenVpnPid(int? processId)
    {
        try
        {
            if (!File.Exists(TunnelXOpenVpnPidPath))
                return;

            if (processId.HasValue)
            {
                var raw = File.ReadAllText(TunnelXOpenVpnPidPath).Trim();
                if (int.TryParse(raw, out var pid) && pid != processId.Value)
                    return;
            }

            File.Delete(TunnelXOpenVpnPidPath);
        }
        catch { }
    }

    private static string? ResolveOpenVpnExecutable(ServerConfig config)
    {
        foreach (var c in GetCandidatePaths())
        {
            Logger.Debug($"[OpenVPN] Checking: '{c}' -> {(File.Exists(c) ? "FOUND" : "not found")}");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static string? FindOpenVpnExecutable()
    {
        foreach (var c in GetCandidatePaths())
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(pf,    "OpenVPN", "bin", "openvpn.exe");
        yield return Path.Combine(pfx86, "OpenVPN", "bin", "openvpn.exe");
        yield return Path.Combine(local, "Programs", "OpenVPN", "bin", "openvpn.exe");
    }

    private static IEnumerable<string> GetOpenVpnConnectPaths()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(pf, "OpenVPN Connect");
        yield return Path.Combine(pfx86, "OpenVPN Connect");
        yield return Path.Combine(local, "Programs", "OpenVPN Connect");
    }

    private static bool IsOpenVpnConnectInstalled() =>
        GetOpenVpnConnectPaths().Any(p => Directory.Exists(p) || File.Exists(Path.Combine(p, "OpenVPNConnect.exe")));

    private static string PrepareSplitCompatibleConfig(
        string originalConfig,
        string username,
        string password,
        string privateKeyPassword)
    {
        var dir = OpenVpnWorkDir;
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "tunnelx-split.ovpn");
        var authPath = Path.Combine(dir, "tunnelx-auth.txt");
        var askpassPath = Path.Combine(dir, "tunnelx-askpass.txt");
        var builder = new StringBuilder();
        var splitOptionsInserted = false;
        var lines = originalConfig.Split('\n');

        void AppendTunnelXOptions()
        {
            if (splitOptionsInserted) return;
            splitOptionsInserted = true;
            builder.AppendLine();
            builder.AppendLine("# Added by TunnelX for split tunneling:");
            builder.AppendLine("route-nopull");
            builder.AppendLine("pull-filter ignore redirect-gateway");
            builder.AppendLine("pull-filter ignore block-outside-dns");
            builder.AppendLine("pull-filter ignore dhcp-option");
            builder.AppendLine("connect-timeout 10");
            builder.AppendLine("server-poll-timeout 10");
            builder.AppendLine("connect-retry 2 5");
            builder.AppendLine("auth-nocache");
            if (!string.IsNullOrWhiteSpace(username))
            {
                File.WriteAllText(authPath, $"{username.Trim()}{Environment.NewLine}{password}", Utf8NoBom);
                builder.AppendLine($"auth-user-pass {QuoteOpenVpnPath(authPath)}");
            }

            if (!string.IsNullOrWhiteSpace(privateKeyPassword))
            {
                File.WriteAllText(askpassPath, privateKeyPassword.Trim(), Utf8NoBom);
                builder.AppendLine($"askpass {QuoteOpenVpnPath(askpassPath)}");
            }

            builder.AppendLine();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("auth-user-pass", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("askpass", StringComparison.OrdinalIgnoreCase))
                continue;

            if (trimmed.StartsWith("<connection>", StringComparison.OrdinalIgnoreCase))
            {
                var block = new List<string> { raw };
                while (++i < lines.Length)
                {
                    var blockLine = lines[i].TrimEnd('\r');
                    block.Add(blockLine);
                    if (blockLine.Trim().Equals("</connection>", StringComparison.OrdinalIgnoreCase))
                        break;
                }

                var remote = ExtractRemoteFromLines(block);
                if (remote.HasValue && ShouldSkipRemote(remote.Value.host))
                {
                    Logger.Warning($"[OpenVPN] Skipping unreachable/private remote block {remote.Value.host}:{remote.Value.port}");
                    continue;
                }

                AppendTunnelXOptions();
                foreach (var blockLine in block)
                    builder.AppendLine(blockLine);
                continue;
            }

            if (trimmed.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
            {
                var remote = ExtractRemoteFromLines(new[] { raw });
                if (remote.HasValue && ShouldSkipRemote(remote.Value.host))
                {
                    Logger.Warning($"[OpenVPN] Skipping unreachable/private remote {remote.Value.host}:{remote.Value.port}");
                    continue;
                }

                AppendTunnelXOptions();
            }

            builder.AppendLine(raw);
        }

        AppendTunnelXOptions();

        File.WriteAllText(path, builder.ToString(), Utf8NoBom);
        return path;
    }

    private static bool IsOpenVpnExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path)) return false;
        return string.Equals(Path.GetFileName(path), "openvpn.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteOpenVpnPath(string path) => $"\"{path.Replace('\\', '/')}\"";

    private static (string host, string port, string proto)? ExtractRemoteFromLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var raw = line.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#") || raw.StartsWith(";"))
                continue;
            if (!raw.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[1], parts.Length >= 3 ? parts[2] : "1194", parts.Length >= 4 ? parts[3] : "");
        }
        return null;
    }

    /// <summary>
    /// Skip only literal private/local IPs in the .ovpn file. Hostnames are kept even if DNS
    /// resolves to RFC1918 addresses (common on some networks) so OpenVPN can still try them.
    /// </summary>
    private static bool ShouldSkipRemote(string host)
    {
        return IPAddress.TryParse(host, out var ip) && IsPrivateIpv4(ip);
    }

    private bool TryResolveVpnInterface(out int interfaceIndex)
    {
        interfaceIndex = -1;

        if (!string.IsNullOrWhiteSpace(_assignedLocalIp))
        {
            var byIp = FindOpenVpnInterfaceIndex(_assignedLocalIp);
            if (byIp > 0 && HasMinimumSessionMetadata())
            {
                interfaceIndex = byIp;
                return true;
            }
        }

        var hasSessionSignal = _initSequenceCompleted ||
            !string.IsNullOrWhiteSpace(_assignedLocalIp) ||
            !string.IsNullOrWhiteSpace(_routeGatewayIp) ||
            !string.IsNullOrWhiteSpace(_connectedRemoteIp);

        if (hasSessionSignal &&
            TryFindReadyOpenVpnAdapter(out interfaceIndex, out var localIp))
        {
            if (string.IsNullOrWhiteSpace(_assignedLocalIp))
                _assignedLocalIp = localIp;
            EnsureSessionRoutingMetadata(interfaceIndex);
            return true;
        }

        return false;
    }

    private bool HasMinimumSessionMetadata() =>
        !string.IsNullOrWhiteSpace(_routeGatewayIp) &&
        !string.IsNullOrWhiteSpace(_connectedRemoteIp) &&
        _connectedRemotePort > 0;

    private void EnsureSessionRoutingMetadata(int interfaceIndex)
    {
        if (_connectedRemotePort <= 0 && _fallbackRemotePort > 0)
            _connectedRemotePort = _fallbackRemotePort;

        if (string.IsNullOrWhiteSpace(_connectedRemoteIp) && !string.IsNullOrWhiteSpace(_fallbackRemoteHost))
            _connectedRemoteIp = ResolveRemoteForRouting(_fallbackRemoteHost);

        if (string.IsNullOrWhiteSpace(_routeGatewayIp))
        {
            _routeGatewayIp = TryGetInterfaceGateway(interfaceIndex)
                ?? DeriveLikelyGateway(_assignedLocalIp)
                ?? _connectedRemoteIp;
            if (!string.IsNullOrWhiteSpace(_routeGatewayIp))
                Logger.Info($"[OpenVPN] Inferred route-gateway {_routeGatewayIp}");
        }
    }

    private static string? TryGetInterfaceGateway(int interfaceIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 == null || ipv4.Index != interfaceIndex)
                    continue;

                var gateway = nic.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return gateway?.ToString();
            }
        }
        catch { }

        return null;
    }

    private static string? DeriveLikelyGateway(string assignedLocalIp)
    {
        if (!IPAddress.TryParse(assignedLocalIp, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return null;

        bytes[3] = 1;
        return new IPAddress(bytes).ToString();
    }

    private static bool TryFindReadyOpenVpnAdapter(out int interfaceIndex, out string localIp)
    {
        interfaceIndex = -1;
        localIp = "";

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            var match =
                nic.Name.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Data Channel Offload", StringComparison.OrdinalIgnoreCase);

            if (!match)
                continue;

            var address = nic.GetIPProperties().UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a =>
                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a) &&
                    !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));

            if (address == null)
                continue;

            var ipv4 = nic.GetIPProperties().GetIPv4Properties();
            if (ipv4 == null)
                continue;

            interfaceIndex = ipv4.Index;
            localIp = address.ToString();
            return true;
        }

        return false;
    }

    private async Task PumpOpenVpnOutputAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _recentOpenVpnOutput.Enqueue(line);
                    while (_recentOpenVpnOutput.Count > 40 && _recentOpenVpnOutput.TryDequeue(out _)) { }
                    if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
                        _initSequenceCompleted = true;

                    TryCaptureRouteGateway(line);
                    TryCaptureConnectedRemote(line);
                    TryCaptureAssignedLocalIp(line);
                    Logger.Debug($"[OpenVPN] {line}");
                }
            }
        }
        catch { }
    }

    private void TryCaptureRouteGateway(string line)
    {
        const string token = "route-gateway ";
        var idx = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var start = idx + token.Length;
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ',')
            end++;

        var gateway = line[start..end].Trim();
        if (IPAddress.TryParse(gateway, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            _routeGatewayIp = gateway;
            Logger.Info($"[OpenVPN] Captured route-gateway {gateway}");
            TryUpdateConnectedStatusFromCapturedState();
        }
    }

    private void TryCaptureConnectedRemote(string line)
    {
        var isConnectedLine =
            line.Contains("TCP connection established with", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Peer Connection Initiated with", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("UDP link remote:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("link remote:", StringComparison.OrdinalIgnoreCase);
        if (!isConnectedLine)
            return;

        if (!line.Contains("[AF_INET]", StringComparison.OrdinalIgnoreCase))
        {
            TryCaptureConnectedRemoteEndpoint(line);
            return;
        }

        var marker = "[AF_INET]";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        var start = idx + marker.Length;
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ',' && line[end] != ']')
            end++;

        var endpoint = line[start..end].Trim();
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || colon == endpoint.Length - 1)
            return;

        var host = endpoint[..colon];
        if (!int.TryParse(endpoint[(colon + 1)..], out var port))
            return;
        if (!IPAddress.TryParse(host, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return;

        ApplyConnectedRemote(host, port);
    }

    private void TryCaptureConnectedRemoteEndpoint(string line)
    {
        var colon = line.LastIndexOf(':');
        if (colon <= 0 || colon >= line.Length - 1)
            return;

        var hostStart = colon - 1;
        while (hostStart >= 0 && (char.IsDigit(line[hostStart]) || line[hostStart] == '.'))
            hostStart--;
        hostStart++;

        var host = line[hostStart..colon].Trim();
        if (!int.TryParse(line[(colon + 1)..].Trim().TrimEnd(']', ')'), out var port))
            return;

        if (!IPAddress.TryParse(host, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return;

        ApplyConnectedRemote(host, port);
    }

    private void ApplyConnectedRemote(string host, int port)
    {
        _connectedRemoteIp = host;
        _connectedRemotePort = port;
        Logger.Info($"[OpenVPN] Captured connected remote {host}:{port}");
        TryUpdateConnectedStatusFromCapturedState();
    }

    private void TryCaptureAssignedLocalIp(string line)
    {
        const string token = "ifconfig ";
        var idx = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return;

        var start = idx + token.Length;
        var end = start;
        while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ',')
            end++;

        var localIp = line[start..end].Trim();
        if (IPAddress.TryParse(localIp, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            _assignedLocalIp = localIp;
            Logger.Info($"[OpenVPN] Captured assigned local IP {localIp}");
            TryUpdateConnectedStatusFromCapturedState();
        }
    }

    private void LogRecentOpenVpnOutput()
    {
        var lines = _recentOpenVpnOutput.ToArray();
        if (lines.Length == 0)
        {
            Logger.Warning("[OpenVPN] No recent OpenVPN output captured before timeout.");
            return;
        }

        Logger.Warning("[OpenVPN] Recent OpenVPN output before timeout:");
        foreach (var line in lines.TakeLast(20))
            Logger.Warning($"[OpenVPN][recent] {line}");
    }

    private static void LogRemoteCandidates(string config)
    {
        var remotes = ExtractRemoteCandidates(config).ToList();
        Logger.Info($"[OpenVPN] Remote candidates found: {remotes.Count}");
        foreach (var remote in remotes.Take(20))
        {
            Logger.Info($"[OpenVPN] remote {remote.host}:{remote.port} {remote.proto}");
            if (IPAddress.TryParse(remote.host, out var ip))
            {
                if (IsPrivateIpv4(ip))
                    Logger.Warning($"[OpenVPN] remote {remote.host} is private/local; it may not be reachable from this network.");
                continue;
            }

            try
            {
                var resolved = Dns.GetHostAddresses(remote.host)
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
                Logger.Info($"[OpenVPN] remote {remote.host} resolves to: {(resolved.Count == 0 ? "no IPv4" : string.Join(", ", resolved))}");
                foreach (var resolvedIp in resolved)
                {
                    if (IPAddress.TryParse(resolvedIp, out var resolvedAddress) && IsPrivateIpv4(resolvedAddress))
                        Logger.Warning($"[OpenVPN] remote {remote.host} resolved to private/local IP {resolvedIp}; OpenVPN may hang until trying the next remote.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[OpenVPN] DNS resolve failed for remote {remote.host}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<(string host, string port, string proto)> ExtractRemoteCandidates(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var raw = line.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#") || raw.StartsWith(";"))
                continue;
            if (!raw.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                yield return (parts[1], parts.Length >= 3 ? parts[2] : "1194", parts.Length >= 4 ? parts[3] : "");
        }
    }

    private static bool IsPrivateIpv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 &&
            (b[0] == 10 ||
             (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
             (b[0] == 192 && b[1] == 168) ||
             b[0] == 127 ||
             (b[0] == 169 && b[1] == 254));
    }

    private static string TryExtractRemoteHost(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var raw = line.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#") || raw.StartsWith(";"))
                continue;
            if (!raw.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return parts[1];
        }
        return "";
    }

    private static string ResolveRemoteForRouting(string remoteHost)
    {
        if (string.IsNullOrWhiteSpace(remoteHost))
            return "0.0.0.0";

        if (IPAddress.TryParse(remoteHost, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? remoteHost : "0.0.0.0";

        try
        {
            var ipv4 = Dns.GetHostAddresses(remoteHost)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            return ipv4?.ToString() ?? remoteHost;
        }
        catch
        {
            return remoteHost;
        }
    }

    private static int FindOpenVpnInterfaceIndex(string expectedLocalIp)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var match =
                nic.Name.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                nic.Description.Contains("Data Channel Offload", StringComparison.OrdinalIgnoreCase);

            if (!match) continue;

            var hasExpectedIp = nic.GetIPProperties().UnicastAddresses.Any(a =>
                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                string.Equals(a.Address.ToString(), expectedLocalIp, StringComparison.OrdinalIgnoreCase));
            if (!hasExpectedIp) continue;

            var ipv4 = nic.GetIPProperties().GetIPv4Properties();
            if (ipv4 != null) return ipv4.Index;
        }
        return -1;
    }

    private static string GetInterfaceIpv4(int interfaceIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = nic.GetIPProperties();
                var ipv4 = props.GetIPv4Properties();
                if (ipv4 == null || ipv4.Index != interfaceIndex) continue;
                return props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.Address.ToString() ?? "N/A";
            }
        }
        catch { }
        return "N/A";
    }
}
