using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AppTunnel.Services;

/// <summary>
/// Logs WireGuard for Windows handshake state via <c>wg show</c> when the adapter is up
/// but TunnelX has not observed inbound tunnel traffic yet.
/// </summary>
public static class WireGuardHandshakeDiagnostics
{
    public static void LogStatus(string tunnelName = WireGuardTunnelProvider.TunnelServiceName)
    {
        try
        {
            var wgExe = ResolveWgExecutable();
            if (string.IsNullOrWhiteSpace(wgExe))
            {
                Logger.Warning("[WG-DIAG] wg.exe not found next to WireGuard install; handshake details unavailable");
                return;
            }

            var dump = RunWg(wgExe, $"show {tunnelName} dump", TimeSpan.FromSeconds(5));
            if (!string.IsNullOrWhiteSpace(dump.Error) && dump.ExitCode != 0)
            {
                Logger.Warning($"[WG-DIAG] wg show dump failed exit={dump.ExitCode}: {FirstLine(dump.Error, dump.Output)}");
                var text = RunWg(wgExe, $"show {tunnelName}", TimeSpan.FromSeconds(5));
                if (text.ExitCode == 0 && !string.IsNullOrWhiteSpace(text.Output))
                {
                    LogHumanReadableShow(text.Output);
                    return;
                }

                Logger.Warning("[WG-DIAG] wg show returned no data — tunnel service may not be running");
                return;
            }

            if (TryParseDump(dump.Output, out var peer))
            {
                LogParsedPeer(tunnelName, peer);
                return;
            }

            if (!string.IsNullOrWhiteSpace(dump.Output))
                LogHumanReadableShow(dump.Output);
            else
                Logger.Warning("[WG-DIAG] wg show dump produced empty output");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[WG-DIAG] Failed to query WireGuard status: {ex.Message}");
        }
    }

    private static void LogParsedPeer(string tunnelName, WgPeerDump peer)
    {
        Logger.Info($"[WG-DIAG] tunnel='{tunnelName}' endpoint={peer.Endpoint ?? "(none)"} allowedIPs={peer.AllowedIps ?? "(none)"}");

        if (peer.LatestHandshakeUnix <= 0)
        {
            Logger.Warning(
                $"[WG-DIAG] latest handshake=never rx={FormatBytes(peer.TransferRx)} tx={FormatBytes(peer.TransferTx)} " +
                "— server did not complete WireGuard handshake (expired config, wrong keys, UDP blocked, or server down)");
            return;
        }

        var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(peer.LatestHandshakeUnix);
        var agoText = ago.TotalSeconds < 60
            ? $"{(int)ago.TotalSeconds}s ago"
            : $"{(int)ago.TotalMinutes}m ago";

        Logger.Warning(
            $"[WG-DIAG] latest handshake={agoText} rx={FormatBytes(peer.TransferRx)} tx={FormatBytes(peer.TransferTx)} " +
            "— WireGuard reports a handshake but TunnelX saw no VPN ingress (routing/DNS/app-target issue)");
    }

    private static void LogHumanReadableShow(string output)
    {
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("private key", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("interface:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("peer:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("handshake", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("allowed ips", StringComparison.OrdinalIgnoreCase))
                Logger.Info($"[WG-DIAG] {line.Trim()}");
        }
    }

    private static bool TryParseDump(string output, out WgPeerDump peer)
    {
        peer = default;
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.Contains('\t'))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 8)
                continue;

            // Peer line: public-key, psk, endpoint, allowed-ips, latest-handshake, rx, tx, keepalive
            if (parts.Length >= 8 && parts[0].Length >= 40)
            {
                peer = new WgPeerDump
                {
                    Endpoint = NullIfEmpty(parts[2]),
                    AllowedIps = NullIfEmpty(parts[3]),
                    LatestHandshakeUnix = ParseLong(parts[4]),
                    TransferRx = ParseLong(parts[5]),
                    TransferTx = ParseLong(parts[6])
                };
                return true;
            }
        }

        return false;
    }

    private static string? ResolveWgExecutable()
    {
        var wireGuardExe = WireGuardTunnelProvider.FindWireGuardExecutable();
        if (string.IsNullOrWhiteSpace(wireGuardExe))
            return null;

        var dir = Path.GetDirectoryName(wireGuardExe);
        if (string.IsNullOrWhiteSpace(dir))
            return null;

        var wg = Path.Combine(dir, "wg.exe");
        return File.Exists(wg) ? wg : null;
    }

    private static (int ExitCode, string Output, string Error) RunWg(string wgExe, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = wgExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("wg.exe did not start");
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "wg.exe timed out");
        }

        return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
    }

    private static long ParseLong(string value)
        => long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KiB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MiB";
    }

    private static string FirstLine(params string[] values)
    {
        foreach (var value in values)
        {
            var line = value
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }

        return "unknown error";
    }

    private readonly struct WgPeerDump
    {
        public string? Endpoint { get; init; }
        public string? AllowedIps { get; init; }
        public long LatestHandshakeUnix { get; init; }
        public long TransferRx { get; init; }
        public long TransferTx { get; init; }
    }
}
