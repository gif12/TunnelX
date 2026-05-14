using AppTunnel.Models;

namespace AppTunnel.Services;

/// <summary>
/// Dispatcher: selects the correct ITunnelProvider based on ServerConfig.TunnelType
/// and delegates all connection operations to it.
/// </summary>
public class VpnService
{
    private ITunnelProvider? _activeProvider;
    private readonly ConnectionStatus _defaultStatus = new();

    public Action? OnTunnelFailed { get; set; }

    /// <summary>Live status, forwarded from the active provider.</summary>
    public ConnectionStatus Status => _activeProvider?.Status ?? _defaultStatus;

    public async Task<bool> ConnectAsync(ServerConfig config, CancellationToken ct = default)
    {
        _activeProvider = config.TunnelType switch
        {
            TunnelType.L2tpIpsec => new L2tpTunnelProvider(),
            TunnelType.V2Ray     => TunnelProviderFactory.Create(config.V2RayConfig),
            _                    => throw new NotImplementedException($"نوع تانل ناشناخته: {config.TunnelType}")
        };

        // Wire up the tunnel-failure watchdog for V2Ray connections.
        if (_activeProvider is V2RayTunnelProvider v2r)
            v2r.OnTunnelFailed = OnTunnelFailed;

        return await _activeProvider.ConnectAsync(config, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_activeProvider == null) return;
        await _activeProvider.DisconnectAsync();
    }

    public bool IsInterfaceUp() => _activeProvider?.IsInterfaceUp() ?? false;
}
