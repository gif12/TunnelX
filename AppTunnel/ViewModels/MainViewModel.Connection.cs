using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Application = System.Windows.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.ViewModels;

public partial class MainViewModel
{
    #region Connection Methods

    private volatile bool _applicationExitRequested;
    private Task? _activeConnectTask;

    private async Task ToggleConnectionAsync()
    {
        if (_connectionState == ConnectionState.Connecting)
        {
            await CancelConnectingAsync();
            return;
        }

        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task CancelConnectingAsync()
    {
        StatusText = "در حال لغو اتصال...";
        _connectionCts?.Cancel();

        try
        {
            await CleanupAfterFailedConnectionAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CONNECT] Cancel cleanup failed: {ex.Message}");
        }

        ConnectionProgressService.Report("split_router", ConnectionProgressPhase.Skip, "اتصال لغو شد");
        CompleteConnectionProgress(false, "اتصال لغو شد");
        ConnectionState = ConnectionState.Disconnected;
        StatusText = "اتصال لغو شد";
        IsBusy = false;
    }

    private async Task ConnectAsync()
    {
        var tunnelType = _selectedProfile?.TunnelType ?? TunnelType.L2tpIpsec;
        if (_selectedProfile != null && _currentTunnelType != _selectedProfile.TunnelType)
        {
            Logger.Warning($"[CONNECT] UI tunnel type '{_currentTunnelType}' was out of sync with selected profile '{_selectedProfile.Name}' ({_selectedProfile.TunnelType}); using profile type.");
            _currentTunnelType = _selectedProfile.TunnelType;
            OnPropertyChanged(nameof(CurrentTunnelType));
            OnPropertyChanged(nameof(ActiveProfileTypeText));
        }

        // Guard: validate required fields per tunnel type
        if (tunnelType == TunnelType.L2tpIpsec && string.IsNullOrWhiteSpace(ServerAddress))
        {
            Logger.Warning("ConnectAsync: ServerAddress is empty for L2TP");
            StatusText = "آدرس سرور را وارد کنید";
            return;
        }
        if (tunnelType == TunnelType.V2Ray && string.IsNullOrWhiteSpace(_selectedProfile?.V2RayConfig))
        {
            Logger.Warning("ConnectAsync: V2RayConfig is empty");
            StatusText = "کانفیگ V2Ray را وارد کنید";
            return;
        }
        if (tunnelType == TunnelType.OpenVpn && string.IsNullOrWhiteSpace(_selectedProfile?.OpenVpnConfig))
        {
            Logger.Warning("ConnectAsync: OpenVPN config is empty");
            StatusText = "کانفیگ OpenVPN (.ovpn) را وارد کنید";
            return;
        }
        if (tunnelType == TunnelType.WireGuard && !WireGuardConfigParser.TryParse(_selectedProfile?.WireGuardConfig ?? SelectedWireGuardConfig, out _, out var wireGuardError))
        {
            Logger.Warning($"ConnectAsync: WireGuard config invalid: {wireGuardError}");
            StatusText = wireGuardError;
            ConfigValidationText = wireGuardError;
            return;
        }
        if (tunnelType == TunnelType.SocksProxy && !ValidateProxySettings(out var proxyError))
        {
            Logger.Warning($"ConnectAsync: proxy settings invalid: {proxyError}");
            StatusText = proxyError;
            ConfigValidationText = proxyError;
            return;
        }
        if (tunnelType == TunnelType.OpenVpn && !IsOpenVpnCommunityInstalled)
        {
            RefreshOpenVpnInstallStatus();
            if (!IsOpenVpnCommunityInstalled)
            {
                Logger.Warning("ConnectAsync: OpenVPN Community openvpn.exe not found");
                StatusText = LocalizationService.Instance.T("OpenVPN نصب نیست؛ راهنمای نصب نمایش داده شد");
                ConfigValidationText = LocalizationService.Instance.T("OpenVPN Community نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید");
                ShowOpenVpnInstallGuideDialog();
                return;
            }
        }
        if (tunnelType == TunnelType.WireGuard && !IsWireGuardInstalled)
        {
            RefreshWireGuardInstallStatus();
            if (!IsWireGuardInstalled)
            {
                Logger.Warning("ConnectAsync: WireGuard for Windows wireguard.exe not found");
                StatusText = "WireGuard نصب نیست؛ راهنمای نصب نمایش داده شد";
                ConfigValidationText = StatusText;
                ShowWireGuardInstallGuideDialog();
                return;
            }
        }
        if (!ValidateMixedProxyPort(out var socksError))
        {
            StatusText = socksError;
            MixedProxyPortStatusText = socksError;
            return;
        }

        var config = _selectedProfile?.ToServerConfig() ?? new ServerConfig
        {
            ServerAddress = ServerAddress.Trim(),
            Username = Username.Trim(),
            Password = Password,
            PreSharedKey = PreSharedKey,
            TunnelType = tunnelType,
            V2RayConfig = _selectedV2RayConfig,
            OpenVpnConfig = _selectedOpenVpnConfig,
            OpenVpnUsername = OpenVpnUsername,
            OpenVpnPassword = OpenVpnPassword,
            OpenVpnPrivateKeyPassword = OpenVpnPrivateKeyPassword,
            WireGuardConfig = SelectedWireGuardConfig,
            WireGuardConfigPath = SelectedWireGuardConfigPath,
            ProxyProtocol = ProxyProtocol,
            ProxyServerAddress = ProxyServerAddress,
            ProxyPort = ProxyPort,
            ProxyUsername = ProxyUsername,
            ProxyPassword = ProxyPassword,
            AutoTuneMtu = AutoTuneMtu,
            EnableDnsOptimization = IsDnsOptimizationEnabled,
            EnableGameMode = IsGameModeEnabled
        };
        config.TunnelType = tunnelType;
        Logger.Info($"[CONNECT] Starting profile='{_selectedProfile?.Name ?? "<manual>"}' id='{_selectedProfile?.Id ?? "-"}' type={config.TunnelType} uiType={_currentTunnelType}");

        // Save current state before connecting, after the connection snapshot is
        // built so a stale UI value cannot change the provider used for this run.
        SaveCurrentProfileState();
        if (_selectedProfile != null)
        {
            _selectedProfile.LastUsedAt = DateTime.Now;
            SaveProfiles();
        }

        _applicationExitRequested = false;
        _activeConnectTask = RunConnectSessionAsync(config, tunnelType);
        try
        {
            await _activeConnectTask;
        }
        finally
        {
            _activeConnectTask = null;
        }
    }

    private async Task RunConnectSessionAsync(ServerConfig config, TunnelType tunnelType)
    {
        if (_applicationExitRequested)
            return;

        // Create new CancellationTokenSource for this connection attempt
        _connectionCts?.Dispose();
        _connectionCts = new CancellationTokenSource();

        IsBusy = true;
        BeginConnectionProgress(tunnelType);
        ConnectionState = ConnectionState.Connecting;
        ConnectionProgressService.Report("validate", ConnectionProgressPhase.Complete, "بررسی کانفیگ و پورت‌ها");
        StatusText = tunnelType == TunnelType.OpenVpn
            ? "در حال آماده‌سازی OpenVPN..."
            : "در حال اتصال...";

        // Give WPF one dispatcher turn to render the connecting view before
        // provider startup does DNS/process/network work.
        await Task.Yield();

        try
        {
            await _trafficRouter.StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[CONNECT] Pre-connect TrafficRouter stop failed: {ex.Message}");
        }

        bool success;
        try
        {
            // Register the watchdog callback before connecting so V2RayTunnelProvider
            // can invoke it if sing-box collapses mid-session.
            _vpnService.OnTunnelFailed = () =>
                Application.Current?.Dispatcher.BeginInvoke(() => _ = HandleVpnDroppedAsync());

            var connectTask = Task.Run(
                () => _vpnService.ConnectAsync(config, _connectionCts.Token),
                _connectionCts.Token);
            var timeout = GetConnectTimeout(config.TunnelType);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout, _connectionCts.Token));
            if (completed != connectTask)
            {
                Logger.Error($"[CONNECT] Timeout after {timeout.TotalSeconds:0}s for {config.TunnelType}; cancelling and cleaning up.");
                _connectionCts.Cancel();
                await CleanupAfterFailedConnectionAsync();
                ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Fail, "اتصال بیش از حد طول کشید و متوقف شد");
                CompleteConnectionProgress(false, "اتصال بیش از حد طول کشید و متوقف شد");
                ConnectionState = ConnectionState.Error;
                StatusText = "اتصال بیش از حد طول کشید و متوقف شد";
                IsBusy = false;
                return;
            }

            success = await connectTask;
        }
        catch (OperationCanceledException)
        {
            await CleanupAfterFailedConnectionAsync();
            if (!_applicationExitRequested)
            {
                CompleteConnectionProgress(false, "اتصال لغو شد");
                ConnectionState = ConnectionState.Disconnected;
                StatusText = "اتصال لغو شد";
            }
            IsBusy = false;
            return;
        }
        catch (Exception ex)
        {
            await CleanupAfterFailedConnectionAsync();
            if (!_applicationExitRequested)
            {
                ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Fail, "خطا: {0}", "خطا: {0}", ex.Message);
                CompleteConnectionProgress(false, errorDetailKey: "خطا: {0}", errorDetailRaw: ex.Message);
                ConnectionState = ConnectionState.Error;
                StatusText = LocalizationService.Instance.Format("خطا: {0}", ex.Message);
            }
            IsBusy = false;
            return;
        }

        if (_applicationExitRequested)
        {
            IsBusy = false;
            return;
        }

        if (success)
        {
            var serverIpForRouter = _vpnService.Status.VpnServerIp ?? "";
            if (tunnelType == TunnelType.WireGuard &&
                !IPAddress.TryParse(serverIpForRouter, out _) &&
                IPAddress.TryParse(_vpnService.Status.VpnServerHost ?? "", out _))
            {
                serverIpForRouter = _vpnService.Status.VpnServerHost!;
            }

            var routerSnap = new VpnRouterSnapshot(
                _vpnService.Status.VpnInterfaceIndex,
                _vpnService.Status.VpnLocalIp ?? "",
                serverIpForRouter,
                _vpnService.Status.VpnGatewayIp ?? "",
                _vpnService.Status.VpnServerPort,
                _vpnService.Status.DnsRedirectIp ?? "");

            try
            {
                ConnectionProgressService.Report("split_router", ConnectionProgressPhase.Active, "راه‌اندازی اسپلیت‌تانلینگ");
                await StartTrafficRouterForCurrentStatusAsync(resetAppCounters: true, routerSnap);
                ConnectionProgressService.Report("split_router", ConnectionProgressPhase.Complete, "راه‌اندازی اسپلیت‌تانلینگ");

                if (_applicationExitRequested)
                {
                    IsBusy = false;
                    return;
                }

                await RunConnectionVerifyStepAsync(_connectionCts?.Token ?? CancellationToken.None);

                if (_applicationExitRequested)
                {
                    IsBusy = false;
                    return;
                }

                CompleteConnectionProgress(true);
                await PumpConnectionProgressUiAsync();

                ConnectionState = ConnectionState.Connected;
                StatusText = tunnelType == TunnelType.SocksProxy
                    ? "پراکسی متصل شد"
                    : _vpnService.Status.Message ?? "";
                VpnIp = _vpnService.Status.VpnLocalIp ?? "";
                ConnectionIpText = "در حال دریافت...";
                VpnAdapterName = ResolveInterfaceName(_vpnService.Status.VpnInterfaceIndex);
                _currentVpnInterfaceIndex = _vpnService.Status.VpnInterfaceIndex;
                _currentVpnGatewayIp = _vpnService.Status.VpnGatewayIp ?? "";
                _connectionStartTime = DateTime.Now;
                LastActiveProfileId = _selectedProfile?.Id;
                OnPropertyChanged(nameof(ConnectedProfileName));
                OnPropertyChanged(nameof(SelectedProfileSummaryText));
                RaiseHealthStatusChanged();

                _vpnHealthCheckCounter = 0;
                _timer.Start();

                var exitIpProxyPort = _vpnService.Status.SingBoxMixedPort > 0
                    ? _vpnService.Status.SingBoxMixedPort
                    : _trafficRouter.Socks5Port;
                _ = RefreshExitIpAsync(exitIpProxyPort);
                _ = RefreshGitHubInstallCountAsync(exitIpProxyPort);
            }
            catch (Exception ex)
            {
                await CleanupAfterFailedConnectionAsync();
                ConnectionProgressService.Report("split_router", ConnectionProgressPhase.Fail, "خطا در راه‌اندازی اسپلیت‌تانلینگ: {0}", "خطا در راه‌اندازی اسپلیت‌تانلینگ: {0}", ex.Message);
                CompleteConnectionProgress(false, errorDetailKey: "خطا در راه‌اندازی اسپلیت‌تانلینگ: {0}", errorDetailRaw: ex.Message);
                ConnectionState = ConnectionState.Error;
                StatusText = LocalizationService.Instance.Format("خطا در راه‌اندازی اسپلیت‌تانلینگ: {0}", ex.Message);
            }
        }
        else
        {
            var failedState = _vpnService.Status.State;
            var failedMessage = _vpnService.Status.Message;
            await CleanupAfterFailedConnectionAsync();
            ConnectionProgressService.Report("tunnel_engine", ConnectionProgressPhase.Fail, failedMessage);
            CompleteConnectionProgress(false, errorDetailRaw: failedMessage);
            if (failedState == ConnectionState.Disconnected)
            {
                ConnectionState = ConnectionState.Disconnected;
                StatusText = failedMessage;
            }
            else
            {
                ConnectionState = ConnectionState.Error;
                StatusText = failedMessage;
            }
        }

        IsBusy = false;
    }

    private static TimeSpan GetConnectTimeout(TunnelType tunnelType) => tunnelType switch
    {
        TunnelType.OpenVpn => TimeSpan.FromSeconds(190),
        TunnelType.L2tpIpsec => TimeSpan.FromSeconds(75),
        TunnelType.V2Ray or TunnelType.SocksProxy => TimeSpan.FromSeconds(35),
        TunnelType.WireGuard => TimeSpan.FromSeconds(35),
        _ => TimeSpan.FromSeconds(45)
    };

    private async Task CleanupAfterFailedConnectionAsync()
    {
        _timer.Stop();
        _pingCts?.Cancel();
        IsPinging = false;

        try { await _trafficRouter.StopAsync(); }
        catch (Exception ex) { Logger.Warning($"CleanupAfterFailedConnectionAsync router cleanup failed: {ex.Message}"); }

        try { await _vpnService.DisconnectAsync(); }
        catch (Exception ex) { Logger.Warning($"CleanupAfterFailedConnectionAsync VPN cleanup failed: {ex.Message}"); }

        VpnIp = "";
        ConnectionIpText = "-";
        VpnAdapterName = "";
        _currentVpnInterfaceIndex = -1;
        _currentVpnGatewayIp = "";
        _isFullRouteEnabled = false;
        OnPropertyChanged(nameof(IsFullRouteEnabled));
        OnPropertyChanged(nameof(FullRouteStatusText));
        RaiseHealthStatusChanged();
    }

    private readonly record struct VpnRouterSnapshot(
        int InterfaceIndex,
        string LocalIp,
        string ServerIp,
        string GatewayIp,
        int ServerPort,
        string DnsRedirectIp);

    private async Task StartTrafficRouterForCurrentStatusAsync(bool resetAppCounters, VpnRouterSnapshot? snapshot = null)
    {
        var snap = snapshot ?? new VpnRouterSnapshot(
            _vpnService.Status.VpnInterfaceIndex,
            _vpnService.Status.VpnLocalIp ?? "",
            _vpnService.Status.VpnServerIp ?? "",
            _vpnService.Status.VpnGatewayIp ?? "",
            _vpnService.Status.VpnServerPort,
            _vpnService.Status.DnsRedirectIp ?? "");

        if (snap.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(snap.LocalIp))
        {
            throw new InvalidOperationException(
                LocalizationService.Instance.Format(
                    "آداپتر VPN برای اسپلیت‌تانلینگ آماده نیست (ifIdx={0}).",
                    snap.InterfaceIndex));
        }

        if (_trafficRouter.IsRunning)
        {
            Logger.Warning("[ROUTER] TrafficRouter still running — stopping before rebind to new VPN session");
            await _trafficRouter.StopAsync(resetCounters: false);
        }

        var enabledApps = TunnelApps.Where(a => a.IsEnabled).ToList();
        _trafficRouter.ClearTargetApps();
        foreach (var app in enabledApps)
        {
            if (resetAppCounters)
            {
                app.BytesSent = 0;
                app.BytesReceived = 0;
            }
            _trafficRouter.AddTargetApp(app.ExecutableName);
        }

        _trafficRouter.Socks5Port = MixedProxyPort;
        _trafficRouter.EnableDnsOptimization = IsDnsOptimizationEnabled;
        _trafficRouter.DnsRedirectOverrideIp = CurrentTunnelType == TunnelType.WireGuard
            ? snap.DnsRedirectIp
            : "";
        _trafficRouter.EnableDnsRedirect = CurrentTunnelType != TunnelType.WireGuard ||
            !string.IsNullOrWhiteSpace(snap.DnsRedirectIp);
        _trafficRouter.EnableGameMode = IsGameModeEnabled;

        // Apply lists before Start so RefreshDestinationLists (async in Start) does not
        // hold _destinationListLock while we block connect on route.exe purge per IP.
        _trafficRouter.SetExcludedDestinations(ExcludedDestinations, resolveDomains: false);
        _trafficRouter.SetIncludedDestinations(IncludedDestinations, resolveDomains: false);

        _trafficRouter.Start(
            snap.InterfaceIndex,
            snap.LocalIp,
            snap.ServerIp,
            snap.GatewayIp,
            snap.ServerPort,
            resetCounters: resetAppCounters,
            serverIsUdpOnly: CurrentTunnelType == TunnelType.WireGuard);
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        ConnectionState = ConnectionState.Disconnecting;
        StatusText = "در حال قطع اتصال...";

        _timer.Stop();
        _pingCts?.Cancel();
        IsPinging = false;

        // Save connection to history before stopping router
        SaveConnectionToHistory();

        await _trafficRouter.StopAsync();
        await _vpnService.DisconnectAsync();

        ConnectionState = ConnectionState.Disconnected;
        StatusText = "قطع شد";
        VpnIp = "";
        ConnectionIpText = "-";
        VpnAdapterName = "";
        _currentVpnInterfaceIndex = -1;
        _currentVpnGatewayIp = "";
        _isFullRouteEnabled = false;
        OnPropertyChanged(nameof(IsFullRouteEnabled));
        OnPropertyChanged(nameof(FullRouteStatusText));
        RaiseHealthStatusChanged();
        ConnectionDuration = "--:--:--";
        TotalTraffic = "0 B";
        AppTrafficTotal = "0 B";
        OtherTunnelTraffic = "0 B";
        DirectTraffic = "0 B";
        foreach (var app in TunnelApps)
        {
            app.BytesSent = 0;
            app.BytesReceived = 0;
        }
        IsBusy = false;
    }

      /// <summary>
    /// Called from code-behind during window close to reliably disconnect.
    /// </summary>
    public async Task DisconnectAndCleanupAsync()
    {
        if (!IsConnected) return;
        await DisconnectAsync();
    }

    /// <summary>
    /// Stops an in-progress connection or active VPN before application shutdown.
    /// </summary>
    public async Task PrepareForApplicationExitAsync()
    {
        _applicationExitRequested = true;
        _connectionCts?.Cancel();

        var needsConnectDrain = IsConnectionPending || IsBusy;
        if (_activeConnectTask != null && needsConnectDrain)
        {
            try
            {
                await _activeConnectTask.WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (Exception ex)
            {
                Logger.Warning($"[EXIT] Waiting for connect task failed: {ex.Message}");
            }
        }

        try
        {
            if (IsConnected)
                await DisconnectAsync();
            else if (needsConnectDrain || _vpnService.Status.State != ConnectionState.Disconnected)
                await CleanupAfterFailedConnectionAsync();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[EXIT] VPN disconnect during shutdown failed: {ex.Message}");
        }

        try
        {
            var cleanupSeconds = IsConnected || needsConnectDrain ? 6 : 3;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cleanupSeconds));
            await TunnelXCleanupService.CleanupAllAsync("prepare application exit", cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[EXIT] Engine cleanup during shutdown failed: {ex.Message}");
        }
    }

    private async Task RunConnectionVerifyStepAsync(CancellationToken ct)
    {
        ConnectionProgressService.Report("verify", ConnectionProgressPhase.Active, "در حال بررسی سلامت اتصال...");
        await PumpConnectionProgressUiAsync();

        var parts = new List<string>();
        var ok = true;

        if (_vpnService.IsInterfaceUp())
            parts.Add(LocalizationService.Instance.T("آداپتر VPN فعال"));
        else
        {
            ok = false;
            parts.Add(LocalizationService.Instance.T("آداپتر VPN شناسایی نشد"));
        }

        if (_trafficRouter.IsRunning)
            parts.Add(LocalizationService.Instance.T("اسپلیت‌تانلینگ فعال"));
        else
        {
            ok = false;
            parts.Add(LocalizationService.Instance.T("اسپلیت‌تانلینگ غیرفعال"));
        }

        if (_trafficRouter.LeakCount == 0)
            parts.Add(LocalizationService.Instance.T("بدون نشت ترافیک"));
        else
        {
            ok = false;
            parts.Add(LocalizationService.Instance.Format("نشت ترافیک: {0}", _trafficRouter.LeakCount));
        }

        var summary = string.Join(" · ", parts);
        var detailKey = ok ? "سلامت اتصال تأیید شد — {0}" : "سلامت اتصال با هشدار — {0}";
        var titleKey = ok ? "سلامت اتصال تأیید شد" : "سلامت اتصال با هشدار";

        try { await Task.Delay(450, ct); }
        catch (OperationCanceledException) { return; }

        ConnectionProgressService.Report(
            "verify",
            ConnectionProgressPhase.Complete,
            titleKey,
            detailKey,
            summary);
        StatusText = titleKey;

        await PumpConnectionProgressUiAsync();
    }

    private static async Task PumpConnectionProgressUiAsync()
    {
        await Task.Yield();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// Called when VPN interface is detected as down. Cleans up and notifies user.
    /// </summary>
    private async Task HandleVpnDroppedAsync()
    {
        if (!IsConnected) return;

        _timer.Stop();
        _pingCts?.Cancel();
        IsPinging = false;

        ConnectionState = ConnectionState.Disconnecting;
        StatusText = "اتصال VPN قطع شد...";

        SaveConnectionToHistory();

        await _trafficRouter.StopAsync();
        // VPN is already gone — just clean up the profile
        try { await _vpnService.DisconnectAsync(); } catch { }

        ConnectionState = ConnectionState.Disconnected;
        StatusText = "اتصال VPN به‌طور غیرمنتظره قطع شد";
        VpnIp = "";
        ConnectionIpText = "-";
        VpnAdapterName = "";
        _currentVpnInterfaceIndex = -1;
        _currentVpnGatewayIp = "";
        _isFullRouteEnabled = false;
        OnPropertyChanged(nameof(IsFullRouteEnabled));
        OnPropertyChanged(nameof(FullRouteStatusText));
        RaiseHealthStatusChanged();
        ConnectionDuration = "--:--:--";
        TotalTraffic = "0 B";
        AppTrafficTotal = "0 B";
        OtherTunnelTraffic = "0 B";
        DirectTraffic = "0 B";
        foreach (var app in TunnelApps)
        {
            app.BytesSent = 0;
            app.BytesReceived = 0;
        }

        Logger.Warning("[VPN-MONITOR] Cleanup complete — UI reset to disconnected state");

        // Bring main window to front so user notices the alert
        if (Application.Current.MainWindow is { } mainWin)
        {
            if (mainWin.WindowState == WindowState.Minimized)
                mainWin.WindowState = WindowState.Normal;
            mainWin.Activate();
        }

        Helpers.DialogService.Warning(
            "اتصال VPN به‌طور غیرمنتظره قطع شد.\nلطفاً دوباره متصل شوید.",
            "قطع اتصال");
    }

    private int _vpnHealthCheckCounter;
    private bool _isRefreshingOpenVpnRouter;
    private int _currentVpnInterfaceIndex = -1;
    private string _currentVpnGatewayIp = "";

    private void UpdateTimerTick()
    {
        if (!IsConnected) return;

        if (CurrentTunnelType == TunnelType.OpenVpn && OpenVpnRuntimeEndpointChanged())
        {
            _ = RefreshOpenVpnRouterAsync();
            return;
        }

        // Check VPN interface health every 5 seconds
        if (++_vpnHealthCheckCounter >= 5)
        {
            _vpnHealthCheckCounter = 0;
            if (!_trafficRouter.IsVpnInterfaceUp())
            {
                Logger.Warning("[VPN-MONITOR] VPN interface is down — triggering auto-disconnect");
                _ = HandleVpnDroppedAsync();
                return;
            }
        }

        ConnectionDuration = _vpnService.Status.Duration;

        // Update per-app traffic stats (for the apps-tab list).
        foreach (var app in TunnelApps)
        {
            var (sent, received) = _trafficRouter.GetTraffic(app.ExecutableName);
            app.BytesSent = sent;
            app.BytesReceived = received;
        }

        // Total tunnel usage: use the authoritative VPN-interface counter.
        // Every visible "usage" counter in the app is based on tunneled bytes;
        // direct/outside-tunnel bytes are kept only as a diagnostic signal.
        var (totalSent, totalReceived) = _trafficRouter.GetTotalVpnTraffic();
        long vpnTotal = totalSent + totalReceived;
        TotalTraffic = FormatBytes(vpnTotal);

        var (trackedSent, trackedReceived) = _trafficRouter.GetTrackedAppsTraffic();
        long trackedTotal = trackedSent + trackedReceived;
        AppTrafficTotal = FormatBytes(trackedTotal);
        OtherTunnelTraffic = FormatBytes(Math.Max(0, vpnTotal - trackedTotal));

        var (directSent, directReceived) = _trafficRouter.GetDirectTraffic();
        DirectTraffic = FormatBytes(directSent + directReceived);
        RaiseHealthStatusChanged();
    }

    private bool OpenVpnRuntimeEndpointChanged()
    {
        _vpnService.IsInterfaceUp(); // lets the OpenVPN provider publish post-reconnect IP/gateway changes.
        var status = _vpnService.Status;
        if (string.IsNullOrWhiteSpace(status.VpnLocalIp) ||
            string.IsNullOrWhiteSpace(status.VpnGatewayIp) ||
            status.VpnInterfaceIndex <= 0)
            return false;

        return !string.Equals(status.VpnLocalIp, VpnIp, StringComparison.OrdinalIgnoreCase) ||
               status.VpnInterfaceIndex != _currentVpnInterfaceIndex ||
               !string.Equals(status.VpnGatewayIp, _currentVpnGatewayIp, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshOpenVpnRouterAsync()
    {
        if (_isRefreshingOpenVpnRouter) return;
        if (!IsConnected || CurrentTunnelType != TunnelType.OpenVpn) return;

        try
        {
            _isRefreshingOpenVpnRouter = true;
            var status = _vpnService.Status;
            if (string.IsNullOrWhiteSpace(status.VpnLocalIp) ||
                string.IsNullOrWhiteSpace(status.VpnGatewayIp) ||
                status.VpnInterfaceIndex <= 0)
                return;

            if (string.Equals(status.VpnLocalIp, VpnIp, StringComparison.OrdinalIgnoreCase) &&
                status.VpnInterfaceIndex == _currentVpnInterfaceIndex &&
                string.Equals(status.VpnGatewayIp, _currentVpnGatewayIp, StringComparison.OrdinalIgnoreCase))
                return;

            var wasFullRoute = IsFullRouteEnabled;
            Logger.Warning($"[OpenVPN] Runtime endpoint changed; restarting TrafficRouter. OldIP={VpnIp} NewIP={status.VpnLocalIp} Gateway={status.VpnGatewayIp} IF={status.VpnInterfaceIndex}");
            StatusText = "OpenVPN دوباره متصل شد؛ مسیرهای TunnelX در حال بروزرسانی است...";

            _timer.Stop();
            _pingCts?.Cancel();
            IsPinging = false;

            await _trafficRouter.StopAsync(resetCounters: false);

            VpnIp = status.VpnLocalIp;
            ConnectionIpText = "در حال دریافت...";
            VpnAdapterName = ResolveInterfaceName(status.VpnInterfaceIndex);
            _currentVpnInterfaceIndex = status.VpnInterfaceIndex;
            _currentVpnGatewayIp = status.VpnGatewayIp;
            _isFullRouteEnabled = false;
            OnPropertyChanged(nameof(IsFullRouteEnabled));
            OnPropertyChanged(nameof(FullRouteStatusText));

            var routerSnap = new VpnRouterSnapshot(
                status.VpnInterfaceIndex,
                status.VpnLocalIp,
                status.VpnServerIp ?? "",
                status.VpnGatewayIp ?? "",
                status.VpnServerPort,
                status.DnsRedirectIp ?? "");
            await StartTrafficRouterForCurrentStatusAsync(resetAppCounters: false, routerSnap);
            var exitIpProxyPort = _vpnService.Status.SingBoxMixedPort > 0
                ? _vpnService.Status.SingBoxMixedPort
                : _trafficRouter.Socks5Port;
            _ = RefreshExitIpAsync(exitIpProxyPort);
            _ = RefreshGitHubInstallCountAsync(exitIpProxyPort);
            if (wasFullRoute)
            {
                _isFullRouteEnabled = _trafficRouter.SetFullRouteEnabled(true);
                OnPropertyChanged(nameof(IsFullRouteEnabled));
                OnPropertyChanged(nameof(FullRouteStatusText));
            }

            _vpnHealthCheckCounter = 0;
            StatusText = "OpenVPN دوباره متصل شد و مسیرها بروزرسانی شدند";
            RaiseHealthStatusChanged();
        }
        catch (Exception ex)
        {
            Logger.Error("[OpenVPN] TrafficRouter refresh after reconnect failed", ex);
            await HandleVpnDroppedAsync();
        }
        finally
        {
            _isRefreshingOpenVpnRouter = false;
            if (IsConnected)
                _timer.Start();
        }
    }

    private void OnTrafficUpdated(string exeName, long sent, long received)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var app = TunnelApps.FirstOrDefault(a =>
                a.ExecutableName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
            if (app != null)
            {
                app.BytesSent = sent;
                app.BytesReceived = received;
            }
        });
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    private static string ResolveInterfaceName(int interfaceIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipv4 = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null && ipv4.Index == interfaceIndex)
                    return nic.Name;
            }
        }
        catch { }

        return interfaceIndex > 0 ? $"IF {interfaceIndex}" : "-";
    }

    private async Task RefreshExitIpAsync(int proxyPort)
    {
        if (proxyPort <= 0)
        {
            ConnectionIpText = "-";
            return;
        }

        var hosts = new[] { "api.ipify.org", "ipv4.icanhazip.com" };
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 4 && _connectionState == ConnectionState.Connected; attempt++)
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var ip = await QueryPublicIpViaHttpConnectProxyAsync(proxyPort, host, cts.Token);
                    if (IPAddress.TryParse(ip, out _))
                    {
                        ConnectionIpText = ip;
                        return;
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or AuthenticationException)
                {
                    lastError = ex;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1200 * attempt));
        }

        if (lastError != null)
            Logger.Warning($"[EXIT-IP] Public IP check failed after retries: {lastError.Message}");

        if (_connectionState == ConnectionState.Connected)
            ConnectionIpText = "-";
    }

    private static async Task<string> QueryPublicIpViaHttpConnectProxyAsync(int proxyPort, string host, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", proxyPort, ct);

        await using var stream = tcp.GetStream();
        var connectRequest = Encoding.ASCII.GetBytes(
            $"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n");
        await stream.WriteAsync(connectRequest, ct);

        var connectHeader = await ReadHttpHeaderAsync(stream, ct);
        if (!connectHeader.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !connectHeader.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            throw new IOException("proxy CONNECT failed");

        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(host, null, SslProtocols.Tls12 | SslProtocols.Tls13, checkCertificateRevocation: false);

        var request = Encoding.ASCII.GetBytes(
            $"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: TunnelX\r\n\r\n");
        await ssl.WriteAsync(request, ct);

        using var ms = new MemoryStream();
        var buffer = new byte[2048];
        int read;
        while ((read = await ssl.ReadAsync(buffer, ct)) > 0)
            ms.Write(buffer, 0, read);

        var response = Encoding.UTF8.GetString(ms.ToArray());
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = split >= 0 ? response[(split + 4)..] : response;
        return body.Trim();
    }

    private static async Task<string> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        while (ms.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            ms.WriteByte(buffer[0]);
            var bytes = ms.ToArray();
            if (bytes.Length >= 4 &&
                bytes[^4] == '\r' &&
                bytes[^3] == '\n' &&
                bytes[^2] == '\r' &&
                bytes[^1] == '\n')
                break;
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }

    #endregion

    #region Pre-connect server test

    private async Task TestServerPingAsync()
    {
        if (IsTestingServerPing) return;

        IsTestingServerPing = true;
        SetServerPingResult("در حال تست...");

        try
        {
            if (CurrentTunnelType == TunnelType.L2tpIpsec)
            {
                var host = ServerAddress.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    SetServerPingResult("آدرس سرور خالی است");
                    return;
                }

                using var ping = new Ping();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(host, 3000);
                sw.Stop();
                if (reply.Status == IPStatus.Success)
                    SetServerPingResult("ICMP {0} ms", reply.RoundtripTime);
                else
                    SetServerPingResult("ICMP {0}", reply.Status);
                return;
            }

            if (CurrentTunnelType == TunnelType.OpenVpn)
            {
                var openVpnEndpoints = ExtractOpenVpnRemoteEndpoints(SelectedOpenVpnConfig).ToList();
                if (openVpnEndpoints.Count == 0)
                {
                    SetServerPingResult("remote سرور در فایل .ovpn پیدا نشد");
                    return;
                }

                var tcpEndpoints = openVpnEndpoints
                    .Where(e => !e.Protocol.Contains("udp", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (tcpEndpoints.Count == 0)
                {
                    SetServerPingResult("کانفیگ UDP است؛ تست دقیق قبل از اتصال ممکن نیست");
                    return;
                }

                Exception? lastError = null;
                foreach (var endpointToTest in tcpEndpoints)
                {
                    try
                    {
                        using var ctsOpenVpn = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var msOpenVpn = await MeasureTcpConnectLatencyAsync(endpointToTest.Host, endpointToTest.Port, ctsOpenVpn.Token);
                        SetServerPingResult("TCP {0} ms", msOpenVpn);
                        return;
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or TimeoutException)
                    {
                        lastError = ex;
                    }
                }

                SetServerPingResult("هیچ remote قابل‌دسترسی نبود ({0})", lastError?.Message ?? "وقت‌تمام شد");
                return;
            }

            if (CurrentTunnelType == TunnelType.WireGuard)
            {
                if (!WireGuardConfigParser.TryParse(SelectedWireGuardConfig, out var wireGuard, out var wireGuardError))
                {
                    SetServerPingResult(wireGuardError);
                    return;
                }

                using var ctsWireGuard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var wireGuardMs = await MeasureIcmpLatencyAsync(wireGuard.EndpointHost, ctsWireGuard.Token);
                SetServerPingResult("{0} ms", wireGuardMs);
                return;
            }

            if (CurrentTunnelType == TunnelType.SocksProxy)
            {
                if (!ValidateProxySettings(out var proxyError))
                {
                    SetServerPingResult(proxyError);
                    return;
                }

                using var ctsProxy = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var proxyMs = await MeasureTcpConnectLatencyAsync(ProxyServerAddress.Trim(), ProxyPort, ctsProxy.Token);
                SetServerPingResult("TCP {0} ms", proxyMs);
                return;
            }

            var rawConfig = SelectedV2RayConfig.Trim();
            if (!TryExtractProxyEndpointDetails(rawConfig, out var endpoint, out var error))
            {
                SetServerPingResult(error);
                return;
            }

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ms2 = await MeasureEndpointLatencyAsync(endpoint, cts2.Token);
            var mode = endpoint.UseTls ? "TLS" : "TCP";
            SetServerPingResult("{0} {1} ms", mode, ms2);
        }
        catch (OperationCanceledException)
        {
            SetServerPingResult("وقت‌تمام شد");
        }
        catch (Exception ex)
        {
            SetServerPingResult("خطا: {0}", ex.Message);
        }
        finally
        {
            IsTestingServerPing = false;
        }
    }

    private async Task TestConnectedServerPingAsync()
    {
        if (IsTestingConnectedServerPing) return;

        if (IsPinging)
        {
            _pingCts?.Cancel();
            IsPinging = false;
        }

        IsTestingConnectedServerPing = true;
        SetPingResult("در حال پینگ سرور...");

        try
        {
            if (CurrentTunnelType == TunnelType.OpenVpn)
            {
                var connectedHost = _vpnService.Status.VpnServerIp;
                var connectedPort = _vpnService.Status.VpnServerPort;
                if (!string.IsNullOrWhiteSpace(connectedHost) && connectedPort > 0)
                {
                    using var ctsConnectedOpenVpn = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var connectedMs = await MeasureTcpConnectLatencyAsync(connectedHost, connectedPort, ctsConnectedOpenVpn.Token);
                    SetPingResult("TCP {0} ms", connectedMs);
                    return;
                }

                if (!TryExtractOpenVpnRemoteEndpoint(SelectedOpenVpnConfig, out var openVpnEndpoint, out var openVpnError))
                {
                    SetPingResult(openVpnError);
                    return;
                }

                if (openVpnEndpoint.Protocol.Contains("udp", StringComparison.OrdinalIgnoreCase))
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(openVpnEndpoint.Host, 3000);
                    if (reply.Status == IPStatus.Success)
                        SetPingResult("ICMP {0} ms", reply.RoundtripTime);
                    else
                        SetPingResult("ICMP {0}", reply.Status);
                    return;
                }

                using var ctsOpenVpn = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var msOpenVpn = await MeasureTcpConnectLatencyAsync(openVpnEndpoint.Host, openVpnEndpoint.Port, ctsOpenVpn.Token);
                SetPingResult("TCP {0} ms", msOpenVpn);
                return;
            }

            if (CurrentTunnelType == TunnelType.L2tpIpsec)
            {
                var host = ServerAddress.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    SetPingResult("آدرس سرور خالی است");
                    return;
                }

                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 3000);
                if (reply.Status == IPStatus.Success)
                    SetPingResult("ICMP {0} ms", reply.RoundtripTime);
                else
                    SetPingResult("ICMP {0}", reply.Status);
                return;
            }

            if (CurrentTunnelType == TunnelType.SocksProxy)
            {
                if (!ValidateProxySettings(out var proxyError))
                {
                    SetPingResult(proxyError);
                    return;
                }

                using var ctsProxy = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var proxyMs = await MeasureTcpConnectLatencyAsync(ProxyServerAddress.Trim(), ProxyPort, ctsProxy.Token);
                SetPingResult("TCP {0} ms", proxyMs);
                return;
            }

            if (CurrentTunnelType == TunnelType.WireGuard)
            {
                var connectedHost = !string.IsNullOrWhiteSpace(_vpnService.Status.VpnServerIp)
                    ? _vpnService.Status.VpnServerIp
                    : _vpnService.Status.VpnServerHost;
                if (string.IsNullOrWhiteSpace(connectedHost))
                {
                    SetPingResult("وقت‌تمام شد");
                    return;
                }

                using var ctsWireGuard = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var wireGuardMs = await MeasureIcmpLatencyAsync(connectedHost, ctsWireGuard.Token);
                SetPingResult("{0} ms", wireGuardMs);
                return;
            }

            if (!TryExtractProxyEndpointDetails(SelectedV2RayConfig.Trim(), out var endpoint, out var error))
            {
                SetPingResult(error);
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ms = await MeasureEndpointLatencyAsync(endpoint, cts.Token);
            var mode = endpoint.UseTls ? "TLS" : "TCP";
            SetPingResult("{0} {1} ms", mode, ms);
        }
        catch (OperationCanceledException)
        {
            SetPingResult("پینگ سرور timeout شد");
        }
        catch (Exception ex)
        {
            SetPingResult("خطا: {0}", ex.Message);
        }
        finally
        {
            IsTestingConnectedServerPing = false;
        }
    }

    private readonly record struct ProxyEndpoint(string Server, int Port, bool UseTls, string? Sni);
    private readonly record struct OpenVpnRemoteEndpoint(string Host, int Port, string Protocol);

    private static async Task<long> MeasureIcmpLatencyAsync(string host, CancellationToken ct)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(host, 3000).WaitAsync(ct);
        if (reply.Status != IPStatus.Success)
            throw new TimeoutException(reply.Status.ToString());
        return reply.RoundtripTime;
    }

    private static async Task<long> MeasureEndpointLatencyAsync(ProxyEndpoint endpoint, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await tcp.ConnectAsync(endpoint.Server, endpoint.Port, ct);

        if (endpoint.UseTls)
        {
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(endpoint.Sni) ? endpoint.Server : endpoint.Sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            };
            await ssl.AuthenticateAsClientAsync(options, ct);
        }

        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<long> MeasureTcpConnectLatencyAsync(string host, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await tcp.ConnectAsync(host, port, ct);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static bool TryExtractOpenVpnRemoteEndpoint(
        string config,
        out OpenVpnRemoteEndpoint endpoint,
        out string error)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(config))
        {
            error = "فایل .ovpn انتخاب نشده است";
            return false;
        }

        foreach (var endpointToTest in ExtractOpenVpnRemoteEndpoints(config))
        {
            endpoint = endpointToTest;
            return true;
        }

        error = "remote سرور در فایل .ovpn پیدا نشد";
        return false;
    }

    private static IEnumerable<OpenVpnRemoteEndpoint> ExtractOpenVpnRemoteEndpoints(string config)
    {
        if (string.IsNullOrWhiteSpace(config))
            yield break;

        foreach (var line in config.Split('\n'))
        {
            var raw = line.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#") || raw.StartsWith(";"))
                continue;
            if (!raw.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var host = parts[1];
            var port = parts.Length >= 3 && int.TryParse(parts[2], out var parsedPort)
                ? parsedPort
                : 1194;
            var protocol = parts.Length >= 4 ? parts[3] : "";

            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
                continue;

            yield return new OpenVpnRemoteEndpoint(host, port, protocol);
        }
    }

    private static bool TryExtractProxyEndpoint(string config, out string server, out int port, out string error)
    {
        if (TryExtractProxyEndpointDetails(config, out var endpoint, out error))
        {
            server = endpoint.Server;
            port = endpoint.Port;
            return true;
        }

        server = "";
        port = 443;
        return false;
    }

    private static bool TryExtractProxyEndpointDetails(string config, out ProxyEndpoint endpoint, out string error)
    {
        endpoint = default;
        error = "";

        if (string.IsNullOrWhiteSpace(config))
        {
            error = "کانفیگ خالی است";
            return false;
        }

        try
        {
            if (config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = config["vmess://".Length..].Split('#')[0];
                var json = TryBase64DecodeConfig(b64);
                var node = JsonNode.Parse(json)?.AsObject();
                var server = node?["add"]?.GetValue<string>() ?? "";
                var port = int.TryParse(node?["port"]?.ToString(), out var p) ? p : 443;
                var useTls = string.Equals(node?["tls"]?.ToString(), "tls", StringComparison.OrdinalIgnoreCase);
                var sni = node?["sni"]?.GetValue<string>() ?? node?["host"]?.GetValue<string>();
                endpoint = new ProxyEndpoint(server, port, useTls, sni);
                return ValidateEndpoint(server, port, out error);
            }

            if (config.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                config.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(config.Split('#')[0]);
                var query = ParseQuery(uri.Query);
                var useTls = query.TryGetValue("security", out var security) &&
                             security.Contains("tls", StringComparison.OrdinalIgnoreCase);
                var sni = query.TryGetValue("sni", out var sniValue) ? sniValue : null;
                endpoint = new ProxyEndpoint(uri.Host, uri.Port > 0 ? uri.Port : 443, useTls, sni);
                return ValidateEndpoint(endpoint.Server, endpoint.Port, out error);
            }

            if (config.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryExtractShadowsocksEndpoint(config, out var server, out var port, out error))
                    return false;
                endpoint = new ProxyEndpoint(server, port, false, null);
                return true;
            }

            // SOCKS5 / HTTP proxy URIs
            if (config.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
                config.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) ||
                config.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(config.Split('#')[0]);
                endpoint = new ProxyEndpoint(uri.Host, uri.Port > 0 ? uri.Port : 1080, false, null);
                return ValidateEndpoint(endpoint.Server, endpoint.Port, out error);
            }

            if (config.StartsWith("{"))
            {
                var root = JsonNode.Parse(config)?.AsObject();
                if (root?["outbounds"] is JsonArray outbounds)
                {
                    foreach (var item in outbounds.OfType<JsonObject>())
                    {
                        var server = item["server"]?.GetValue<string>() ??
                                     item["settings"]?["vnext"]?[0]?["address"]?.GetValue<string>() ??
                                     item["settings"]?["servers"]?[0]?["address"]?.GetValue<string>() ?? "";
                        var port = item["server_port"]?.GetValue<int>() ??
                                   item["settings"]?["vnext"]?[0]?["port"]?.GetValue<int>() ??
                                   item["settings"]?["servers"]?[0]?["port"]?.GetValue<int>() ?? 443;
                        if (!string.IsNullOrWhiteSpace(server))
                        {
                            var tlsNode = item["tls"]?.AsObject();
                            var streamSettings = item["streamSettings"]?.AsObject();
                            var useTls =
                                tlsNode?["enabled"]?.GetValue<bool>() == true ||
                                string.Equals(streamSettings?["security"]?.ToString(), "tls", StringComparison.OrdinalIgnoreCase);
                            var sni = tlsNode?["server_name"]?.GetValue<string>() ??
                                      streamSettings?["tlsSettings"]?["serverName"]?.GetValue<string>();
                            endpoint = new ProxyEndpoint(server, port, useTls, sni);
                            return ValidateEndpoint(server, port, out error);
                        }
                    }
                }
            }

            error = "endpoint سرور از کانفیگ تشخیص داده نشد";
            return false;
        }
        catch (Exception ex)
        {
            error = LocalizationService.Instance.Format("پارس کانفیگ ناموفق بود: {0}", ex.Message);
            return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "";
            result[key] = value;
        }
        return result;
    }

    private static bool TryExtractShadowsocksEndpoint(string config, out string server, out int port, out string error)
    {
        server = "";
        port = 443;
        error = "";

        var noFragment = config.Split('#')[0];
        var uri = new Uri(noFragment);
        if (!string.IsNullOrWhiteSpace(uri.Host))
        {
            server = uri.Host;
            port = uri.Port > 0 ? uri.Port : 443;
            return ValidateEndpoint(server, port, out error);
        }

        var encoded = noFragment["ss://".Length..];
        var decoded = TryBase64DecodeConfig(encoded);
        var atIdx = decoded.LastIndexOf('@');
        var hostPart = atIdx >= 0 ? decoded[(atIdx + 1)..] : decoded;
        var colonIdx = hostPart.LastIndexOf(':');
        if (colonIdx < 0)
        {
            error = "آدرس ss:// نامعتبر است";
            return false;
        }

        server = hostPart[..colonIdx];
        port = int.TryParse(hostPart[(colonIdx + 1)..], out var p) ? p : 443;
        return ValidateEndpoint(server, port, out error);
    }

    private static bool ValidateEndpoint(string server, int port, out string error)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            error = "سرور پیدا نشد";
            return false;
        }
        if (port <= 0 || port > 65535)
        {
            error = "پورت نامعتبر است";
            return false;
        }

        error = "";
        return true;
    }

    private static string TryBase64DecodeConfig(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    #endregion

    #region Ping

    private CancellationTokenSource? _pingCts;

    private void TogglePing()
    {
        if (IsPinging)
        {
            _pingCts?.Cancel();
            IsPinging = false;
            return;
        }

        var raw = PingTarget?.Trim() ?? "";
        // Accept IP or hostname:port — extract host for route installation
        var host = raw.Contains(':') ? raw.Split(':')[0] : raw;
        if (string.IsNullOrWhiteSpace(host))
        {
            SetPingResult("آدرس نامعتبر");
            return;
        }

        IsPinging = true;
        SetPingResult("...");
        _pingCts = new CancellationTokenSource();

        // For V2Ray mode, ping through sing-box's own mixed SOCKS5 inbound.
        // sing-box only replies CONNECT_OK after the real upstream TCP handshake
        // through the proxy chain completes → accurate end-to-end RTT.
        // For L2TP, the TunnelX SOCKS5 proxy (bound to VPN IP) gives accurate
        // RTT because L2TP routes packets through the real PPP adapter without
        // a fake local handshake.
        int pingProxyPort = _vpnService.Status.SingBoxMixedPort > 0
            ? _vpnService.Status.SingBoxMixedPort
            : _trafficRouter.Socks5Port;

        _ = RunPingLoopAsync(host, raw, pingProxyPort, _pingCts.Token);
    }

    /// <summary>
    /// TCP-connect latency loop. Uses a SOCKS5 CONNECT through the appropriate
    /// proxy to measure true end-to-end round-trip:
    ///   • V2Ray mode → sing-box mixed SOCKS5 inbound (port 2080).
    ///     sing-box only replies CONNECT_OK after the real upstream TCP handshake
    ///     through the proxy chain completes.  The TUN path is NOT used here
    ///     because the TUN's "system" stack completes the local TCP handshake
    ///     immediately (1-2 ms) before the remote connection is established.
    ///   • L2TP mode  → TunnelX built-in SOCKS5 (port 1080), bound to VPN IP.
    ///     Packets route through the real PPP adapter; no fake local handshake.
    /// Format of <paramref name="target"/>: "host" or "host:port" (default port 443).
    /// </summary>
    private async Task RunPingLoopAsync(string host, string target, int proxyPort, CancellationToken ct)
    {
        // Parse optional port from "host:port"
        int port = 443;
        if (target.Contains(':') && int.TryParse(target.Split(':')[^1], out var p))
            port = p;

        int socks5Port = proxyPort;

        int sent = 0, success = 0;

        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                sent++;
                try
                {
                    long ms = await PingViaSocks5Async(host, port, socks5Port, ct);
                    success++;
                    SetPingResult("TCP {0} ms  ({1}/{2})", ms, success, sent);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    SetPingResult("✗ timeout  ({0}/{1})", success, sent);
                }
                catch (Exception ex)
                {
                    SetPingResult("✗ {0}  ({1}/{2})", ex.Message, success, sent);
                }

                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsPinging = false;
            if (sent > 0)
                MarkPingResultDone();
        }
    }

    /// <summary>
    /// Measures the true end-to-end TCP round-trip through the SOCKS5 proxy.
    ///
    /// IMPORTANT: We CANNOT just time the SOCKS5 CONNECT reply — sing-box (and
    /// many modern SOCKS5 implementations) deliberately send the CONNECT success
    /// reply IMMEDIATELY, before dialing the upstream, as a latency optimization.
    /// On loopback this returns in 1-2 ms regardless of the real path.
    ///
    /// Instead we do CONNECT (untimed), then send a probe and time how long
    /// until the FIRST response byte from the upstream server arrives. That gives
    /// us exactly one round-trip through the entire proxy chain to the remote
    /// host. For port 443 we send a minimal TLS ClientHello (server replies with
    /// ServerHello after 1 RTT). For other ports we send an HTTP GET (server
    /// replies with response data or RST after 1 RTT). Either way, time-to-first-
    /// byte is the real RTT.
    /// </summary>
    private static async Task<long> PingViaSocks5Async(
        string host, int port, int socks5Port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);

        using var tcp = new System.Net.Sockets.TcpClient();
        tcp.NoDelay = true;
        await tcp.ConnectAsync("127.0.0.1", socks5Port, cts.Token);

        var stream = tcp.GetStream();

        // ── SOCKS5 greeting (untimed — local loopback only) ──
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
        var greet = new byte[2];
        await ReadExactlyAsync(stream, greet, cts.Token);
        if (greet[0] != 0x05 || greet[1] != 0x00)
            throw new Exception("SOCKS5 handshake rejected");

        // ── SOCKS5 CONNECT request (untimed — proxy may pre-reply) ──
        var hostBytes = System.Text.Encoding.ASCII.GetBytes(host);
        var req = new byte[7 + hostBytes.Length];
        req[0] = 0x05;                              // VER
        req[1] = 0x01;                              // CMD = CONNECT
        req[2] = 0x00;                              // RSV
        req[3] = 0x03;                              // ATYP = DOMAINNAME
        req[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(req, 5);
        req[5 + hostBytes.Length] = (byte)(port >> 8);
        req[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await stream.WriteAsync(req, cts.Token);

        // Read SOCKS5 CONNECT response header (4 fixed bytes + bound addr + port).
        var resp = new byte[4];
        await ReadExactlyAsync(stream, resp, cts.Token);
        if (resp[1] != 0x00)
            throw new Exception($"SOCKS5 connect failed (code {resp[1]})");

        switch (resp[3])
        {
            case 0x01: await ReadExactlyAsync(stream, new byte[6], cts.Token); break;
            case 0x03:
                var lenBuf = new byte[1];
                await ReadExactlyAsync(stream, lenBuf, cts.Token);
                await ReadExactlyAsync(stream, new byte[lenBuf[0] + 2], cts.Token);
                break;
            case 0x04: await ReadExactlyAsync(stream, new byte[18], cts.Token); break;
        }

        // ── Real round-trip: send probe + read first response byte ──
        // For port 443 we send a minimal TLS ClientHello so the remote server
        // replies with ServerHello in exactly 1 RTT. For other ports an HTTP
        // GET works (most servers reply with data or RST in 1 RTT).
        byte[] probe = port == 443
            ? BuildTlsClientHello(host)
            : System.Text.Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {host}\r\n\r\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await stream.WriteAsync(probe, cts.Token);

        // Wait for the first byte of any upstream response. The connection may
        // be RST/closed by the remote (returns 0 from Read) — that's still a
        // valid 1-RTT measurement.
        var oneByte = new byte[1];
        try
        {
            int got = await stream.ReadAsync(oneByte, 0, 1, cts.Token);
            sw.Stop();
            // Guard: replies arriving in <= 1 ms almost certainly came from the
            // local proxy (closed connection, refused, etc.) rather than the
            // upstream server. Treat as a failure to avoid showing fake numbers.
            if (sw.ElapsedMilliseconds <= 1 && got == 0)
                throw new Exception("upstream closed (no data)");
            return sw.ElapsedMilliseconds;
        }
        catch (System.IO.IOException) when (sw.ElapsedMilliseconds > 1)
        {
            // Remote sent RST after a real round-trip — still a valid measurement.
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Builds a minimal TLS 1.2 ClientHello with the given SNI hostname.
    /// Any compliant TLS server replies with ServerHello after one full RTT,
    /// so the time between sending this and receiving the first response byte
    /// equals the network round-trip through the proxy chain to the server.
    /// </summary>
    private static byte[] BuildTlsClientHello(string sniHost)
    {
        var sni = System.Text.Encoding.ASCII.GetBytes(sniHost);

        // ── extensions ──
        // server_name (0x0000): list_len(2) + name_type(1) + host_len(2) + host
        var sniExt = new List<byte> { 0x00, 0x00 }; // ext type
        int sniListLen = 1 + 2 + sni.Length;
        int sniExtLen  = 2 + sniListLen;
        sniExt.AddRange(new byte[] { (byte)(sniExtLen >> 8), (byte)sniExtLen });
        sniExt.AddRange(new byte[] { (byte)(sniListLen >> 8), (byte)sniListLen });
        sniExt.Add(0x00); // host_name type
        sniExt.AddRange(new byte[] { (byte)(sni.Length >> 8), (byte)sni.Length });
        sniExt.AddRange(sni);

        // supported_versions (0x002b): TLS 1.3 + TLS 1.2 — accepted by all modern servers
        var verExt = new byte[] { 0x00, 0x2b, 0x00, 0x05, 0x04, 0x03, 0x04, 0x03, 0x03 };
        // supported_groups (0x000a): x25519
        var grpExt = new byte[] { 0x00, 0x0a, 0x00, 0x04, 0x00, 0x02, 0x00, 0x1d };
        // signature_algorithms (0x000d): rsa_pss_rsae_sha256, ecdsa_secp256r1_sha256
        var sigExt = new byte[] { 0x00, 0x0d, 0x00, 0x06, 0x00, 0x04, 0x08, 0x04, 0x04, 0x03 };

        var extensions = new List<byte>();
        extensions.AddRange(sniExt);
        extensions.AddRange(verExt);
        extensions.AddRange(grpExt);
        extensions.AddRange(sigExt);

        // ── ClientHello body ──
        var body = new List<byte>();
        body.AddRange(new byte[] { 0x03, 0x03 });           // legacy_version = TLS 1.2
        for (int i = 0; i < 32; i++) body.Add(0xAA);        // random (fixed bytes are fine)
        body.Add(0x00);                                     // session_id length
        body.AddRange(new byte[] { 0x00, 0x02, 0x13, 0x01 }); // cipher_suites: TLS_AES_128_GCM_SHA256
        body.AddRange(new byte[] { 0x01, 0x00 });           // compression_methods: null
        body.AddRange(new byte[] {
            (byte)(extensions.Count >> 8), (byte)extensions.Count
        });
        body.AddRange(extensions);

        // Handshake header: type(1) + length(3)
        var handshake = new List<byte> { 0x01 };
        handshake.AddRange(new byte[] {
            0x00, (byte)(body.Count >> 8), (byte)body.Count
        });
        handshake.AddRange(body);

        // TLS record header: type(1) + version(2) + length(2)
        var record = new List<byte> {
            0x16, 0x03, 0x01,
            (byte)(handshake.Count >> 8), (byte)handshake.Count
        };
        record.AddRange(handshake);

        return record.ToArray();
    }

    private static async Task ReadExactlyAsync(
        System.Net.Sockets.NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
            if (read == 0) throw new Exception("SOCKS5 connection closed unexpectedly");
            offset += read;
        }
    }

    #endregion
}
