using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Application = System.Windows.Application;
using System.Windows.Threading;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    private readonly VpnService _vpnService = new();
    private readonly TrafficRouterService _trafficRouter = new();
    private readonly ProfileService _profileService = new();
    private readonly HistoryService _historyService = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveDebounceTimer;
    private CancellationTokenSource? _connectionCts;
    private DateTime _connectionStartTime;
    private ProfileService.AppSettings _appSettings = new();
    private bool _isLoadingProfile;

    public MainViewModel()
    {
        ConnectCommand = new RelayCommand(_ =>
        {
            // Fire-and-forget safety: catch exceptions to avoid unobserved task exceptions
            _ = ToggleConnectionAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Application.Current?.Dispatcher.Invoke(() =>
                        StatusText = LocalizationService.Instance.Format("خطا: {0}", t.Exception?.InnerException?.Message));
            }, TaskScheduler.Default);
        }, _ => !IsBusy || ConnectionState == ConnectionState.Connecting);
        AddAppCommand = new RelayCommand(_ => AddCustomApp());
        RemoveAppCommand = new RelayCommand(RemoveApp);
        ToggleAppCommand = new RelayCommand(ToggleApp);
        RefreshAppsCommand = new RelayCommand(_ => LoadInstalledApps(), _ => !IsBusy);

        // Profile commands
        NewProfileCommand = new RelayCommand(_ => CreateNewProfile(), _ => !IsConnected);
        DeleteProfileCommand = new RelayCommand(DeleteCurrentProfile, _ => !IsConnected && Profiles.Count > 1);
        DuplicateProfileCommand = new RelayCommand(DuplicateCurrentProfile, _ => !IsConnected);
        EditProfileCommand = new RelayCommand(EditProfile, _ => !IsConnected);
        SelectProfileCommand = new RelayCommand(SelectProfile, _ => !IsConnected);

        // History command
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory());

        // Exclude list commands
        AddExcludeCommand = new RelayCommand(_ => AddExcludedDestination());
        RemoveExcludeCommand = new RelayCommand(RemoveExcludedDestination);

        // Include list commands
        AddIncludeCommand = new RelayCommand(_ => AddIncludedDestination());
        RemoveIncludeCommand = new RelayCommand(RemoveIncludedDestination);

        // Ping command
        TogglePingCommand = new RelayCommand(_ => TogglePing(), _ => IsConnected);
        TestConnectedServerPingCommand = new RelayCommand(_ => _ = TestConnectedServerPingAsync(), _ => IsConnected && !IsTestingConnectedServerPing);
        TestServerPingCommand = new RelayCommand(_ => _ = TestServerPingAsync(), _ => !IsConnected && !IsTestingServerPing);
        PasteConfigCommand = new RelayCommand(_ => PasteConfigFromClipboard(), _ => !IsConnected && (CurrentTunnelType == TunnelType.V2Ray || CurrentTunnelType == TunnelType.OpenVpn));
        ClearConfigCommand = new RelayCommand(_ => ClearCurrentConfig(), _ => !IsConnected && (CurrentTunnelType == TunnelType.V2Ray || CurrentTunnelType == TunnelType.OpenVpn));
        BrowseOpenVpnConfigCommand = new RelayCommand(_ => BrowseForOpenVpnConfig(), _ => !IsConnected && CurrentTunnelType == TunnelType.OpenVpn);
        OpenOpenVpnCommunityDownloadCommand = new RelayCommand(_ => OpenExternalLink(OpenVpnCommunityDownloadUrl));
        OpenGitHubCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.GitHubUrl));
        OpenDonateCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.PayPalDonateUrl));
        OpenAdRequestCommand = new RelayCommand(_ => OpenExternalLink(AppInfo.TelegramContactUrl));
        CopyDonationInfoCommand = new RelayCommand(_ => CopyDonationInfoToClipboard());
        CheckForUpdatesCommand = new RelayCommand(_ => _ = CheckForUpdatesAsync(false), _ => !IsCheckingForUpdates);
        OpenLatestReleaseCommand = new RelayCommand(_ => OpenExternalLink(LatestReleaseUrl), _ => !string.IsNullOrWhiteSpace(LatestReleaseUrl));
        ToggleLanguageCommand = new RelayCommand(_ => ToggleLanguage());

        LocalizationService.Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        _trafficRouter.TrafficUpdated += OnTrafficUpdated;

        // Timer for updating UI (duration, traffic stats)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateTimerTick();

        // Auto-save timer with debounce (save 1 second after last change)
        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDebounceTimer.Tick += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            SaveCurrentProfileState();
            SaveProfiles();
            SaveStatusText = "ذخیره شد";
        };

        // Load saved profiles, global tunnel apps, global excludes, global includes, and history on startup
        LoadProfiles();
        LoadTunnelApps();
        LoadExcludes();
        LoadIncludes();
        LoadHistory();
        LoadAppSettings();
        RefreshOpenVpnInstallStatus();
        _ = CheckForUpdatesAsync(true);

        // Auto-connect to last active profile if enabled
        if (_appSettings.AutoConnectOnStartup && !string.IsNullOrEmpty(_appSettings.LastActiveProfileId))
        {
            var lastProfile = Profiles.FirstOrDefault(p => p.Id == _appSettings.LastActiveProfileId);
            if (lastProfile != null)
            {
                SelectedProfile = lastProfile;
                _ = ToggleConnectionAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Application.Current?.Dispatcher.Invoke(() =>
                            StatusText = LocalizationService.Instance.Format("خطای اتصال خودکار: {0}", t.Exception?.InnerException?.Message));
                }, TaskScheduler.Default);
            }
        }
    }

    #region Properties

    private string _serverAddress = "";
    public string ServerAddress
    {
        get => _serverAddress;
        set { _serverAddress = value; OnPropertyChanged(); UpdateConfigDiagnostics(); RaiseProfileCardChanged(); SaveCurrentState(); }
    }

    private string _username = "";
    public string Username
    {
        get => _username;
        set { _username = value; OnPropertyChanged(); SaveCurrentState(); }
    }

    private string _password = "";
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(); }
    }

    private string _preSharedKey = "";
    public string PreSharedKey
    {
        get => _preSharedKey;
        set { _preSharedKey = value; OnPropertyChanged(); }
    }

    private int _mixedProxyPort = 1080;
    public int MixedProxyPort
    {
        get => _mixedProxyPort;
        set
        {
            var normalized = value;
            if (_mixedProxyPort == normalized) return;
            _mixedProxyPort = normalized;
            _trafficRouter.Socks5Port = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MixedProxyPortText));
            OnPropertyChanged(nameof(MixedProxyInfo));
            UpdateMixedProxyPortStatus();
            SaveCurrentState();
        }
    }

    public string MixedProxyPortText
    {
        get => _mixedProxyPort.ToString();
        set
        {
            if (int.TryParse((value ?? "").Trim(), out var port))
            {
                if (_mixedProxyPort != port)
                {
                    _mixedProxyPort = port;
                    _trafficRouter.Socks5Port = port;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MixedProxyPort));
                    OnPropertyChanged(nameof(MixedProxyInfo));
                    UpdateMixedProxyPortStatus();
                    SaveCurrentState();
                }
                return;
            }

            MixedProxyPortStatusText = string.IsNullOrWhiteSpace(value)
                ? "پورت SOCKS5 را وارد کنید"
                : "فقط عدد مجاز است";
        }
    }

    private string _mixedProxyPortStatusText = "";
    public string MixedProxyPortStatusText
    {
        get => LocalizationService.Instance.T(_mixedProxyPortStatusText);
        set { _mixedProxyPortStatusText = value; OnPropertyChanged(); }
    }

    private bool _autoTuneMtu = true;
    public bool AutoTuneMtu
    {
        get => _autoTuneMtu;
        set
        {
            if (_autoTuneMtu == value) return;
            _autoTuneMtu = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private bool _isDnsOptimizationEnabled = true;
    public bool IsDnsOptimizationEnabled
    {
        get => _isDnsOptimizationEnabled;
        set
        {
            if (_isDnsOptimizationEnabled == value) return;
            _isDnsOptimizationEnabled = value;
            _trafficRouter.EnableDnsOptimization = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private bool _isGameModeEnabled;
    public bool IsGameModeEnabled
    {
        get => _isGameModeEnabled;
        set
        {
            if (_isGameModeEnabled == value) return;
            _isGameModeEnabled = value;
            _trafficRouter.EnableGameMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameModeStatusText));
            SaveCurrentState();
        }
    }

    public string GameModeStatusText => IsGameModeEnabled
        ? LocalizationService.Instance.T("Game Mode فعال است: Route نگهداری طولانی‌تر، DNS سریع‌تر و DSCP برای بسته‌های بازی اعمال می‌شود.")
        : LocalizationService.Instance.T("Game Mode غیرفعال است: حالت متعادل برای مصرف عمومی.");

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            _startWithWindows = value;
            OnPropertyChanged();
            UpdateStartupRegistry(value);
            _appSettings.StartWithWindows = value;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    private bool _autoConnectOnStartup;
    public bool AutoConnectOnStartup
    {
        get => _autoConnectOnStartup;
        set
        {
            if (_autoConnectOnStartup == value) return;
            _autoConnectOnStartup = value;
            OnPropertyChanged();
            _appSettings.AutoConnectOnStartup = value;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    public string? LastActiveProfileId
    {
        get => _appSettings.LastActiveProfileId;
        set
        {
            if (_appSettings.LastActiveProfileId == value) return;
            _appSettings.LastActiveProfileId = value;
            _profileService.SaveAppSettings(_appSettings);
        }
    }

    public string LanguageToggleText => LocalizationService.Instance.ToggleLanguageText;
    public bool AppIsRightToLeft => LocalizationService.Instance.IsRightToLeft;
    public string AppTitleText => LocalizationService.Instance.IsRightToLeft ? "تانلکس" : "TunnelX";
    public string AppTitleAccentText => LocalizationService.Instance.IsRightToLeft ? "س" : "X";
    public System.Windows.FlowDirection AppTitleFlowDirection => LocalizationService.Instance.FlowDirection;
    public System.Windows.HorizontalAlignment AppTitleAccentAlignment => LocalizationService.Instance.IsRightToLeft
        ? System.Windows.HorizontalAlignment.Left
        : System.Windows.HorizontalAlignment.Right;
    public System.Windows.FlowDirection AppFlowDirection => LocalizationService.Instance.FlowDirection;
    public System.Windows.TextAlignment AppTextAlignment => LocalizationService.Instance.TextAlignment;
    public System.Windows.HorizontalAlignment AppStartHorizontalAlignment => LocalizationService.Instance.StartHorizontalAlignment;
    public System.Windows.HorizontalAlignment AppEndHorizontalAlignment => LocalizationService.Instance.EndHorizontalAlignment;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        set
        {
            _connectionState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(ConnectButtonToolTip));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsOpenVpnConnectionPending));
            RaiseHealthStatusChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsConnected => _connectionState == ConnectionState.Connected;
    public bool IsOpenVpnConnectionPending =>
        _connectionState == ConnectionState.Connecting && CurrentTunnelType == TunnelType.OpenVpn;
    public const string OpenVpnCommunityDownloadUrl = "https://openvpn.net/community-downloads/";

    private bool _isOpenVpnCommunityInstalled;
    public bool IsOpenVpnCommunityInstalled
    {
        get => _isOpenVpnCommunityInstalled;
        private set
        {
            if (_isOpenVpnCommunityInstalled == value) return;
            _isOpenVpnCommunityInstalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenVpnPrerequisiteText));
            OnPropertyChanged(nameof(OpenVpnPrerequisiteColor));
        }
    }

    private string _openVpnDetectedPath = "";
    public string OpenVpnDetectedPath
    {
        get => _openVpnDetectedPath;
        private set
        {
            if (_openVpnDetectedPath == value) return;
            _openVpnDetectedPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenVpnPrerequisiteText));
        }
    }

    public string OpenVpnPrerequisiteText => IsOpenVpnCommunityInstalled
        ? LocalizationService.Instance.Format("پیش‌نیاز آماده است: نسخه Community اوپن‌وی‌پی‌ان پیدا شد: {0}", OpenVpnDetectedPath)
        : LocalizationService.Instance.T("اخطار: نسخه Community اوپن‌وی‌پی‌ان نصب نیست. برای استفاده از اسپلیت‌تانلینگ با این نوع اتصال، ابتدا آن را از لینک رسمی نصب کنید.");

    public string OpenVpnPrerequisiteColor => IsOpenVpnCommunityInstalled ? "#6CCB5F" : "#E0A020";

    /// <summary>App version read from a single app-wide source.</summary>
    public string AppVersion => AppInfo.VersionText;
    public string AppReleaseText => AppInfo.ReleaseText;
    public string AppCreatorText => AppInfo.CreatorText;
    public string AppGitHubUrl => AppInfo.GitHubUrl;
    public string AppLicenseText => AppInfo.LicenseName;
    public string AppLicenseDisplayText => LocalizationService.Instance.Format("لایسنس: {0}", AppInfo.LicenseName);
    public string AdPlaceholderTitleText => LocalizationService.Instance.T("محل تبلیغات شما");
    public string AdRequestButtonText => LocalizationService.Instance.T("درخواست تبلیغ");
    public string AdAudienceText => _githubInstallCount.HasValue
        ? LocalizationService.Instance.Format("تبلیغ شما می‌تواند در معرض دید کاربران TunnelX با بیش از {0} نصب از GitHub باشد.", GitHubInstallCountDisplay)
        : "";
    public string DonatePayPalText => LocalizationService.Instance.IsRightToLeft
        ? $"پی‌پل: {AppInfo.PayPalEmail}"
        : $"PayPal: {AppInfo.PayPalEmail}";
    public string CryptoDonationText => LocalizationService.Instance.IsRightToLeft
        ? AppInfo.CryptoDonationText
        : AppInfo.CryptoDonationTextEn;

    private bool _isCheckingForUpdates;
    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        set
        {
            if (_isCheckingForUpdates == value) return;
            _isCheckingForUpdates = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (_isUpdateAvailable == value) return;
            _isUpdateAvailable = value;
            OnPropertyChanged();
        }
    }

    private string _updateStatusText = "برای بررسی نسخه جدید، دکمه بررسی بروزرسانی را بزنید.";
    public string UpdateStatusText
    {
        get => LocalizationService.Instance.T(_updateStatusText);
        set
        {
            if (_updateStatusText == value) return;
            _updateStatusText = value;
            OnPropertyChanged();
        }
    }

    private string _latestReleaseUrl = AppInfo.LatestReleaseUrl;
    public string LatestReleaseUrl
    {
        get => _latestReleaseUrl;
        set
        {
            if (_latestReleaseUrl == value) return;
            _latestReleaseUrl = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private long? _githubInstallCount;
    private int _githubInstallCountRequestId;

    public bool HasGitHubInstallCount => _githubInstallCount.HasValue;

    public string GitHubInstallCountText => _githubInstallCount.HasValue
        ? LocalizationService.Instance.Format(
            "تعداد نصب این برنامه از گیت هاب: {0}",
            GitHubInstallCountDisplay)
        : "";

    private string GitHubInstallCountDisplay =>
        (_githubInstallCount ?? 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public string UpdateButtonText => IsCheckingForUpdates
        ? LocalizationService.Instance.T("در حال بررسی...")
        : LocalizationService.Instance.T("بررسی بروزرسانی");

    public string ConnectButtonText => _connectionState switch
    {
        ConnectionState.Disconnected => LocalizationService.Instance.T("🔌  اتصال"),
        ConnectionState.Connecting => LocalizationService.Instance.T("❌  لغو اتصال"),
        ConnectionState.Connected => LocalizationService.Instance.T("🔴  قطع اتصال"),
        ConnectionState.Disconnecting => LocalizationService.Instance.T("⏳  در حال قطع..."),
        ConnectionState.Error => LocalizationService.Instance.T("🔌  اتصال مجدد"),
        _ => LocalizationService.Instance.T("اتصال")
    };

    public string ConnectButtonToolTip => _connectionState switch
    {
        ConnectionState.Connecting => LocalizationService.Instance.T("لغو تلاش اتصال"),
        ConnectionState.Connected => LocalizationService.Instance.T("قطع اتصال فعلی"),
        ConnectionState.Disconnecting => LocalizationService.Instance.T("در حال قطع اتصال..."),
        ConnectionState.Error => LocalizationService.Instance.T("اتصال مجدد"),
        _ => LocalizationService.Instance.T("شروع اتصال با پروفایل انتخاب‌شده")
    };

    public string StatusColor => _connectionState switch
    {
        ConnectionState.Connected => "#4CAF50",
        ConnectionState.Connecting or ConnectionState.Disconnecting => "#E07820",
        ConnectionState.Error => "#E05252",
        _ => "#666666"
    };

    private string _statusText = "آماده اتصال";
    public string StatusText
    {
        get => LocalizationService.Instance.T(_statusText);
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Mirrors SelectedProfile.TunnelType as a direct ViewModel property so XAML
    /// visibility bindings update reliably when the selected profile changes.
    /// Setter also writes back to the active profile.
    /// </summary>
    private TunnelType _currentTunnelType = TunnelType.L2tpIpsec;
    public TunnelType CurrentTunnelType
    {
        get => _currentTunnelType;
        set
        {
            if (_currentTunnelType == value) return;
            _currentTunnelType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOpenVpnConnectionPending));
            OnPropertyChanged(nameof(ConnectedBadgeText));
            OnPropertyChanged(nameof(ConnectionIpLabel));
            OnPropertyChanged(nameof(ConnectedServerPingButtonText));
            if (_selectedProfile != null)
                _selectedProfile.TunnelType = value;
            if (value == TunnelType.OpenVpn)
                RefreshOpenVpnInstallStatus();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedV2RayConfig = "";
    public string SelectedV2RayConfig
    {
        get => _selectedV2RayConfig;
        set
        {
            if (_selectedV2RayConfig == value) return;
            _selectedV2RayConfig = value;
            if (_selectedProfile != null)
                _selectedProfile.V2RayConfig = value;
            OnPropertyChanged();
            TryAutoNameProfileFromConfig(value);
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedOpenVpnConfig = "";
    public string SelectedOpenVpnConfig
    {
        get => _selectedOpenVpnConfig;
        set
        {
            if (_selectedOpenVpnConfig == value) return;
            _selectedOpenVpnConfig = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnConfig = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            RaiseHealthStatusChanged();
            SaveCurrentState();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _selectedOpenVpnConfigPath = "";
    public string SelectedOpenVpnConfigPath
    {
        get => _selectedOpenVpnConfigPath;
        set
        {
            if (_selectedOpenVpnConfigPath == value) return;
            _selectedOpenVpnConfigPath = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnConfigPath = value;
            OnPropertyChanged();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _openVpnUsername = "";
    public string OpenVpnUsername
    {
        get => _openVpnUsername;
        set
        {
            if (_openVpnUsername == value) return;
            _openVpnUsername = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnUsername = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private string _openVpnPassword = "";
    public string OpenVpnPassword
    {
        get => _openVpnPassword;
        set
        {
            if (_openVpnPassword == value) return;
            _openVpnPassword = value;
            if (_selectedProfile != null)
                _selectedProfile.OpenVpnPassword = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private ProxyProtocol _proxyProtocol = ProxyProtocol.Socks5;
    public ProxyProtocol ProxyProtocol
    {
        get => _proxyProtocol;
        set
        {
            if (_proxyProtocol == value) return;
            _proxyProtocol = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyProtocol = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _proxyServerAddress = "";
    public string ProxyServerAddress
    {
        get => _proxyServerAddress;
        set
        {
            if (_proxyServerAddress == value) return;
            _proxyServerAddress = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyServerAddress = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private int _proxyPort = 1080;
    public int ProxyPort
    {
        get => _proxyPort;
        set
        {
            var normalized = value;
            if (_proxyPort == normalized) return;
            _proxyPort = normalized;
            if (_selectedProfile != null)
                _selectedProfile.ProxyPort = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProxyPortText));
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    public string ProxyPortText
    {
        get => _proxyPort.ToString();
        set
        {
            if (int.TryParse((value ?? "").Trim(), out var port))
            {
                ProxyPort = port;
                return;
            }

            ConfigValidationText = string.IsNullOrWhiteSpace(value)
                ? "پورت پراکسی را وارد کنید"
                : "پورت پراکسی باید عدد باشد";
        }
    }

    private string _proxyUsername = "";
    public string ProxyUsername
    {
        get => _proxyUsername;
        set
        {
            if (_proxyUsername == value) return;
            _proxyUsername = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyUsername = value;
            OnPropertyChanged();
            UpdateConfigDiagnostics();
            RaiseProfileCardChanged();
            SaveCurrentState();
        }
    }

    private string _proxyPassword = "";
    public string ProxyPassword
    {
        get => _proxyPassword;
        set
        {
            if (_proxyPassword == value) return;
            _proxyPassword = value;
            if (_selectedProfile != null)
                _selectedProfile.ProxyPassword = value;
            OnPropertyChanged();
            SaveCurrentState();
        }
    }

    private string _configCoreHint = "";
    public string ConfigCoreHint
    {
        get => LocalizationService.Instance.T(_configCoreHint);
        set { _configCoreHint = value; OnPropertyChanged(); }
    }

    private string _configValidationText = "";
    public string ConfigValidationText
    {
        get => LocalizationService.Instance.T(_configValidationText);
        set { _configValidationText = value; OnPropertyChanged(); }
    }

    private string _saveStatusText = "";
    public string SaveStatusText
    {
        get => LocalizationService.Instance.T(_saveStatusText);
        set { _saveStatusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProfileSaveHintText)); }
    }

    private string _connectionDuration = "--:--:--";
    public string ConnectionDuration
    {
        get => _connectionDuration;
        set { _connectionDuration = value; OnPropertyChanged(); }
    }

    private string _vpnIp = "";
    public string VpnIp
    {
        get => _vpnIp;
        set { _vpnIp = value; OnPropertyChanged(); }
    }

    private string _connectionIpText = "-";
    public string ConnectionIpText
    {
        get => LocalizationService.Instance.T(_connectionIpText);
        set { _connectionIpText = value; OnPropertyChanged(); }
    }

    public string ConnectionIpLabel => LocalizationService.Instance.T("IP خروجی");

    private string _vpnAdapterName = "";
    public string VpnAdapterName
    {
        get => _vpnAdapterName;
        set { _vpnAdapterName = value; OnPropertyChanged(); }
    }

    private bool _isFullRouteEnabled;
    public bool IsFullRouteEnabled
    {
        get => _isFullRouteEnabled;
        set
        {
            if (_isFullRouteEnabled == value) return;

            if (IsConnected)
            {
                var ok = _trafficRouter.SetFullRouteEnabled(value);
                if (!ok)
                {
                    OnPropertyChanged();
                    StatusText = "تغییر حالت Full Route ناموفق بود";
                    return;
                }
            }

            _isFullRouteEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FullRouteStatusText));
            OnPropertyChanged(nameof(RouteModeTitle));
            OnPropertyChanged(nameof(RouteModeDescription));
            RaiseHealthStatusChanged();
        }
    }

    public string FullRouteStatusText => _isFullRouteEnabled
        ? LocalizationService.Instance.T("Full Route فعال است؛ کل سیستم از تونل عبور می‌کند")
        : LocalizationService.Instance.T("Split فعال است؛ فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند");

    public string RouteModeTitle => IsFullRouteEnabled
        ? LocalizationService.Instance.T("حالت کل سیستم")
        : LocalizationService.Instance.T("حالت انتخابی");
    public string RouteModeDescription => IsFullRouteEnabled
        ? LocalizationService.Instance.T("ترافیک کل سیستم از تونل عبور خواهد کرد؛ برای وقتی مناسب است که همه برنامه‌ها باید پشت تونل باشند.")
        : LocalizationService.Instance.T("فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند؛ بقیه ترافیک مستقیم می‌ماند.");

    public string HeaderCoreText => $"Core: {ActiveCoreName}";
    public string HeaderRouteText => IsFullRouteEnabled ? "Mode: Full" : "Mode: Split";
    public string HeaderLeakText => IsConnected
        ? (_trafficRouter.LeakCount == 0
            ? (_trafficRouter.LeakBlockedCount == 0
                ? "Leak: OK"
                : $"Leak: Protected {_trafficRouter.LeakBlockedCount}")
            : $"Leak: {_trafficRouter.LeakCount}")
        : "Leak: -";
    public string HeaderLeakColor => !IsConnected
        ? "#6CCB5F"
        : _trafficRouter.LeakCount > 0
            ? "#E05252"
            : "#6CCB5F";

    public string HealthLeakText => IsConnected
        ? (_trafficRouter.LeakCount == 0
            ? (_trafficRouter.LeakBlockedCount == 0
                ? "0 leak"
                : $"0 leak / {_trafficRouter.LeakBlockedCount} protected")
            : $"{_trafficRouter.LeakCount} leak")
        : "-";
    public string HealthDnsText => IsConnected
        ? (_trafficRouter.DnsRedirectCount > 0 ? $"DNS tunnel {_trafficRouter.DnsRedirectCount}" : "DNS ready")
        : "-";
    public string HealthIpv6Text => IsConnected
        ? $"IPv6 blocked {_trafficRouter.Ipv6BlockedCount}"
        : "-";
    public string HealthRoutesText => IsConnected
        ? $"routes {_trafficRouter.ActiveRouteCount}/{_trafficRouter.RouteFailureCount} fail"
        : "-";

    public string ConnectedBadgeText => CurrentTunnelType == TunnelType.SocksProxy
        ? LocalizationService.Instance.T("متصل به پراکسی")
        : LocalizationService.Instance.T("متصل به VPN");

    public string ConnectedProfileName => string.IsNullOrWhiteSpace(SelectedProfileName)
        ? LocalizationService.Instance.T("پروفایل فعال")
        : SelectedProfileName;

    public string SelectedProfileSummaryText => LocalizationService.Instance.Format(
        "پروفایل فعال: {0}",
        ConnectedProfileName);

    private string ActiveCoreName => CurrentTunnelType switch
    {
        TunnelType.L2tpIpsec => "L2TP",
        TunnelType.V2Ray when TunnelProviderFactory.RequiresXray(SelectedV2RayConfig) => "Xray",
        TunnelType.V2Ray => "sing-box",
        TunnelType.OpenVpn => "OpenVPN",
        TunnelType.SocksProxy => ProxyProtocol == ProxyProtocol.Http ? "HTTP Proxy" : "SOCKS5",
        _ => "-"
    };

    private string _totalTraffic = "0 B";
    public string TotalTraffic
    {
        get => _totalTraffic;
        set { _totalTraffic = value; OnPropertyChanged(); }
    }

    private string _appTrafficTotal = "0 B";
    public string AppTrafficTotal
    {
        get => _appTrafficTotal;
        set { _appTrafficTotal = value; OnPropertyChanged(); }
    }

    private string _otherTunnelTraffic = "0 B";
    public string OtherTunnelTraffic
    {
        get => _otherTunnelTraffic;
        set { _otherTunnelTraffic = value; OnPropertyChanged(); }
    }

    private string _directTraffic = "0 B";
    public string DirectTraffic
    {
        get => _directTraffic;
        set { _directTraffic = value; OnPropertyChanged(); }
    }

    private string _pingTarget = "8.8.8.8";
    public string PingTarget
    {
        get => _pingTarget;
        set { _pingTarget = value; OnPropertyChanged(); }
    }

    private bool _isPinging;
    public bool IsPinging
    {
        get => _isPinging;
        set { _isPinging = value; OnPropertyChanged(); OnPropertyChanged(nameof(PingButtonText)); }
    }

    public string PingButtonText => _isPinging
        ? LocalizationService.Instance.T("توقف تست")
        : LocalizationService.Instance.T("تست مقصد");

    private bool _isTestingConnectedServerPing;
    public bool IsTestingConnectedServerPing
    {
        get => _isTestingConnectedServerPing;
        set
        {
            _isTestingConnectedServerPing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectedServerPingButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ConnectedServerPingButtonText => IsTestingConnectedServerPing
        ? LocalizationService.Instance.T("در حال پینگ...")
        : LocalizationService.Instance.T("پینگ سرور");

    private string _pingResult = "";
    public string PingResult
    {
        get => LocalizationService.Instance.T(_pingResult);
        set { _pingResult = value; OnPropertyChanged(); }
    }

    private bool _isTestingServerPing;
    public bool IsTestingServerPing
    {
        get => _isTestingServerPing;
        set
        {
            _isTestingServerPing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerPingButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ServerPingButtonText => _isTestingServerPing
        ? LocalizationService.Instance.T("در حال تست...")
        : LocalizationService.Instance.T("تست سرور");

    private string _serverPingResult = "";
    public string ServerPingResult
    {
        get => LocalizationService.Instance.T(_serverPingResult);
        set { _serverPingResult = value; OnPropertyChanged(); }
    }

    public int EnabledAppsCount => TunnelApps.Count(a => a.IsEnabled);

    public string MixedProxyInfo => $"127.0.0.1:{_trafficRouter.Socks5Port}";

    // Exclude list
    public ObservableCollection<string> ExcludedDestinations { get; } = new();

    private string _excludeInput = "";
    public string ExcludeInput
    {
        get => _excludeInput;
        set { _excludeInput = value; OnPropertyChanged(); }
    }

    // Include list
    public ObservableCollection<string> IncludedDestinations { get; } = new();

    private string _includeInput = "";
    public string IncludeInput
    {
        get => _includeInput;
        set { _includeInput = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AppItemViewModel> TunnelApps { get; } = new();

    public ObservableCollection<AppItemViewModel> AvailableApps { get; } = new();

    private AppItemViewModel? _selectedApp;
    public AppItemViewModel? SelectedApp
    {
        get => _selectedApp;
        set { _selectedApp = value; OnPropertyChanged(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            FilterAvailableApps();
        }
    }

    private string _tunnelSearchText = "";
    public string TunnelSearchText
    {
        get => _tunnelSearchText;
        set
        {
            _tunnelSearchText = value;
            OnPropertyChanged();
            FilterTunnelApps();
        }
    }

    private ObservableCollection<AppItemViewModel> _filteredAvailableApps = new();
    public ObservableCollection<AppItemViewModel> FilteredAvailableApps
    {
        get => _filteredAvailableApps;
        set { _filteredAvailableApps = value; OnPropertyChanged(); }
    }

    private ObservableCollection<AppItemViewModel> _filteredTunnelApps = new();
    public ObservableCollection<AppItemViewModel> FilteredTunnelApps
    {
        get => _filteredTunnelApps;
        set { _filteredTunnelApps = value; OnPropertyChanged(); }
    }

    // Connection History
    public ObservableCollection<ConnectionHistoryEntry> ConnectionHistory { get; } = new();

    public string TotalHistoryData
    {
        get
        {
            long total = ConnectionHistory.Sum(e => e.BytesSent + e.BytesReceived);
            return FormatBytes(total);
        }
    }

    #endregion

    #region Commands

    public ICommand ConnectCommand { get; }
    public ICommand AddAppCommand { get; }
    public ICommand RemoveAppCommand { get; }
    public ICommand ToggleAppCommand { get; }
    public ICommand RefreshAppsCommand { get; }
    public ICommand NewProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand EditProfileCommand { get; }
    public ICommand SelectProfileCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand AddExcludeCommand { get; }
    public ICommand RemoveExcludeCommand { get; }
    public ICommand AddIncludeCommand { get; }
    public ICommand RemoveIncludeCommand { get; }
    public ICommand TogglePingCommand { get; }
    public ICommand TestConnectedServerPingCommand { get; }
    public ICommand TestServerPingCommand { get; }
    public ICommand PasteConfigCommand { get; }
    public ICommand ClearConfigCommand { get; }
    public ICommand BrowseOpenVpnConfigCommand { get; }
    public ICommand OpenOpenVpnCommunityDownloadCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenDonateCommand { get; }
    public ICommand OpenAdRequestCommand { get; }
    public ICommand CopyDonationInfoCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OpenLatestReleaseCommand { get; }
    public ICommand ToggleLanguageCommand { get; }

    #endregion

    #region Config UX

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Open link failed: {url} — {ex.Message}");
        }
    }

    private void CopyDonationInfoToClipboard()
    {
        try
        {
            var text =
                $"{AppInfo.AppName} - {LocalizationService.Instance.T("حمایت از پروژه")}\n" +
                $"PayPal: {AppInfo.PayPalEmail}\n" +
                $"PayPal link: {AppInfo.PayPalDonateUrl}\n\n" +
                CryptoDonationText;
            System.Windows.Clipboard.SetText(text);
            Logger.Info("[UI] Donation info copied to clipboard");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy donation info failed: {ex.Message}");
        }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (IsCheckingForUpdates) return;

        try
        {
            IsCheckingForUpdates = true;
            if (!silent)
                UpdateStatusText = "در حال بررسی آخرین نسخه در GitHub...";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var latest = await GitHubReleaseChecker.GetLatestReleaseAsync(cts.Token);
            if (latest == null)
            {
                if (!silent)
                    UpdateStatusText = "بررسی نسخه جدید ناموفق بود. اتصال اینترنت یا GitHub را بررسی کنید.";
                Logger.Warning("[UPDATE] Latest release check failed");
                return;
            }

            LatestReleaseUrl = latest.Url;
            var currentVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version ?? new Version(0, 0, 0);
            var current = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build);

            if (latest.Version > current)
            {
                IsUpdateAvailable = true;
                UpdateStatusText = LocalizationService.Instance.Format("نسخه جدید آماده است: {0} - برای دانلود از GitHub باز کنید.", latest.TagName);
                Logger.Info($"[UPDATE] New version available: current={current} latest={latest.TagName}");
                return;
            }

            IsUpdateAvailable = false;
            UpdateStatusText = LocalizationService.Instance.Format("TunnelX به‌روز است. نسخه فعلی: {0}", AppInfo.VersionText);
            Logger.Info($"[UPDATE] App is up to date: current={current} latest={latest.TagName}");
        }
        catch (OperationCanceledException)
        {
            if (!silent)
                UpdateStatusText = "بررسی بروزرسانی به زمان مجاز نرسید.";
            Logger.Warning("[UPDATE] Latest release check timed out");
        }
        catch (Exception ex)
        {
            if (!silent)
                UpdateStatusText = LocalizationService.Instance.Format("بررسی بروزرسانی ناموفق بود: {0}", ex.Message);
            Logger.Warning($"[UPDATE] Latest release check failed: {ex.Message}");
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task RefreshGitHubInstallCountAsync(int proxyPort)
    {
        var requestId = ++_githubInstallCountRequestId;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var count = await GitHubReleaseChecker.GetAppDownloadCountAsync(cts.Token, proxyPort);
            if (requestId != _githubInstallCountRequestId || !IsConnected)
                return;

            if (count.HasValue)
            {
                SetGitHubInstallCount(count.Value, persist: true);
                Logger.Info($"[GITHUB-STATS] App downloads={count.Value}");
            }
            else
            {
                Logger.Warning("[GITHUB-STATS] App download count unavailable");
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Warning("[GITHUB-STATS] App download count timed out");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[GITHUB-STATS] App download count failed: {ex.Message}");
        }
    }

    private void SetGitHubInstallCount(long? count, bool persist = false)
    {
        if (_githubInstallCount == count)
        {
            if (persist && _appSettings.GitHubAppDownloadCount != count)
            {
                _appSettings.GitHubAppDownloadCount = count;
                _profileService.SaveAppSettings(_appSettings);
            }
            return;
        }

        _githubInstallCount = count;
        if (persist)
        {
            _appSettings.GitHubAppDownloadCount = count;
            _profileService.SaveAppSettings(_appSettings);
        }

        OnPropertyChanged(nameof(HasGitHubInstallCount));
        OnPropertyChanged(nameof(GitHubInstallCountText));
        OnPropertyChanged(nameof(AdAudienceText));
    }

    private void PasteConfigFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (CurrentTunnelType == TunnelType.OpenVpn)
                    SelectedOpenVpnConfig = text;
                else
                    SelectedV2RayConfig = text;
            }
        }
        catch (Exception ex)
        {
            ConfigValidationText = LocalizationService.Instance.Format("خواندن کلیپ‌بورد ناموفق بود: {0}", ex.Message);
        }
    }

    private void ClearCurrentConfig()
    {
        if (CurrentTunnelType == TunnelType.OpenVpn)
        {
            SelectedOpenVpnConfig = "";
            SelectedOpenVpnConfigPath = "";
        }
        else
            SelectedV2RayConfig = "";
    }

    private void BrowseForOpenVpnConfig()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Instance.T("انتخاب فایل OpenVPN"),
            Filter = LocalizationService.Instance.T("OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            SelectedOpenVpnConfigPath = dialog.FileName;
            SelectedOpenVpnConfig = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ConfigValidationText = LocalizationService.Instance.Format("خواندن فایل OpenVPN ناموفق بود: {0}", ex.Message);
        }
    }

    private void RefreshOpenVpnInstallStatus()
    {
        var path = OpenVpnTunnelProvider.FindOpenVpnExecutable();
        IsOpenVpnCommunityInstalled = !string.IsNullOrWhiteSpace(path);
        OpenVpnDetectedPath = path ?? "";
        UpdateConfigDiagnostics();
    }

    private void UpdateConfigDiagnostics()
    {
        if (CurrentTunnelType == TunnelType.L2tpIpsec)
        {
            ConfigCoreHint = "L2TP/IPsec";
            ConfigValidationText = string.IsNullOrWhiteSpace(ServerAddress)
                ? "آدرس سرور L2TP را وارد کنید"
                : "آماده تست و اتصال";
            return;
        }

        if (CurrentTunnelType == TunnelType.OpenVpn)
        {
            ConfigCoreHint = "OpenVPN";
            ConfigValidationText = !IsOpenVpnCommunityInstalled
                ? "OpenVPN Community نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید"
                : string.IsNullOrWhiteSpace(SelectedOpenVpnConfig)
                ? "فایل .ovpn را انتخاب کنید؛ TunnelX آن را در حالت split-compatible اجرا می‌کند"
                : string.IsNullOrWhiteSpace(OpenVpnUsername)
                    ? "کانفیگ انتخاب شد؛ اگر سرور احراز هویت دارد نام کاربری را وارد کنید"
                    : "کانفیگ و نام کاربری OpenVPN آماده است";
            return;
        }

        if (CurrentTunnelType == TunnelType.SocksProxy)
        {
            ConfigCoreHint = ProxyProtocol == ProxyProtocol.Http ? "HTTP Proxy" : "SOCKS5 Proxy";
            ConfigValidationText = ValidateProxySettings(out var proxyMessage)
                ? BuildProxyValidationText()
                : proxyMessage;
            return;
        }

        var config = SelectedV2RayConfig.Trim();
        if (string.IsNullOrWhiteSpace(config))
        {
            ConfigCoreHint = "منتظر کانفیگ";
            ConfigValidationText = "کانفیگ V2Ray/Xray را وارد یا پیست کنید";
            return;
        }

        ConfigCoreHint = TunnelProviderFactory.RequiresXray(config)
            ? "هسته: Xray-core"
            : "هسته: sing-box";

        ConfigValidationText = TryExtractProxyEndpoint(config, out var server, out var port, out var error)
            ? LocalizationService.Instance.Format("سرور: {0}:{1}", server, port)
            : error;
    }

    private string BuildProxyValidationText()
    {
        var endpoint = $"{ProxyServerAddress.Trim()}:{ProxyPort}";
        return IsLoopbackProxyServer()
            ? LocalizationService.Instance.Format("پراکسی آماده است: {0} — توجه: این پراکسی محلی است؛ برنامه‌هایی که خودشان مستقیم از همین پراکسی استفاده کنند خارج از لیست برنامه‌های TunnelX هم پروکسی می‌شوند.", endpoint)
            : LocalizationService.Instance.Format("پراکسی آماده است: {0}", endpoint);
    }

    private bool IsLoopbackProxyServer()
    {
        var host = ProxyServerAddress.Trim();
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               host.StartsWith("127.", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateProxySettings(out string message)
    {
        if (string.IsNullOrWhiteSpace(ProxyServerAddress))
        {
            message = "آدرس IP یا دامنه سرور پراکسی را وارد کنید";
            return false;
        }

        if (ProxyPort <= 0 || ProxyPort > 65535)
        {
            message = "پورت پراکسی باید بین 1 تا 65535 باشد";
            return false;
        }

        message = "تنظیمات پراکسی آماده است";
        return true;
    }

    private bool ValidateMixedProxyPort(out string message)
    {
        var port = _mixedProxyPort;
        if (port < 1024 || port > 65535)
        {
            message = "پورت باید بین 1024 تا 65535 باشد";
            return false;
        }

        var blocked = new HashSet<int>
        {
            1433, 1521, 1723, 1900, 2049, 2080, 2375, 2376,
            3000, 3306, 3389, 5000, 5432, 5353, 5355, 5900,
            6379, 8000, 8080, 8443, 8888, 9000, 9090, 27017
        };

        if (blocked.Contains(port))
        {
            message = "این پورت رایج/حساس است؛ یک پورت آزاد مثل 1080، 1081 یا 18080 انتخاب کنید";
            return false;
        }

        try
        {
            var used = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(p => p.Port == port);
            if (used && !IsConnected)
            {
                message = "این پورت همین حالا توسط برنامه دیگری استفاده می‌شود";
                return false;
            }
        }
        catch { }

        message = "پورت SOCKS5 داخلی آماده است";
        return true;
    }

    private void UpdateMixedProxyPortStatus()
    {
        ValidateMixedProxyPort(out var message);
        MixedProxyPortStatusText = message;
    }

    private void TryAutoNameProfileFromConfig(string config)
    {
        if (_selectedProfile == null || string.IsNullOrWhiteSpace(config))
            return;

        var currentName = _selectedProfile.Name?.Trim() ?? "";
        var canRename = string.IsNullOrWhiteSpace(currentName) ||
                        currentName.StartsWith("پروفایل ", StringComparison.OrdinalIgnoreCase) ||
                        currentName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                        currentName == "پروفایل جدید" ||
                        currentName == "پیش‌فرض" ||
                        currentName == "New Profile" ||
                        currentName == "Default";
        if (!canRename)
            return;

        var remark = ExtractConfigRemark(config);
        if (string.IsNullOrWhiteSpace(remark))
            return;

        _selectedProfile.Name = remark;
        OnPropertyChanged(nameof(SelectedProfileName));
        OnPropertyChanged(nameof(ConnectedProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummaryText));
    }

    private static string ExtractConfigRemark(string config)
    {
        try
        {
            if (config.Contains('#'))
                return Uri.UnescapeDataString(config[(config.IndexOf('#') + 1)..]).Trim();

            if (config.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var json = TryBase64DecodeConfig(config["vmess://".Length..]);
                return JsonNode.Parse(json)?["ps"]?.GetValue<string>()?.Trim() ?? "";
            }

            if (config.StartsWith("{"))
            {
                var root = JsonNode.Parse(config);
                return root?["remarks"]?.GetValue<string>()?.Trim() ??
                       root?["name"]?.GetValue<string>()?.Trim() ??
                       root?["outbounds"]?[0]?["tag"]?.GetValue<string>()?.Trim() ?? "";
            }
        }
        catch { }

        return "";
    }

    private void RaiseHealthStatusChanged()
    {
        OnPropertyChanged(nameof(HeaderCoreText));
        OnPropertyChanged(nameof(HeaderRouteText));
        OnPropertyChanged(nameof(HeaderLeakText));
        OnPropertyChanged(nameof(HeaderLeakColor));
        OnPropertyChanged(nameof(RouteModeTitle));
        OnPropertyChanged(nameof(RouteModeDescription));
        OnPropertyChanged(nameof(HealthLeakText));
        OnPropertyChanged(nameof(HealthDnsText));
        OnPropertyChanged(nameof(HealthIpv6Text));
        OnPropertyChanged(nameof(HealthRoutesText));
    }

    private void LoadAppSettings()
    {
        _appSettings = _profileService.LoadAppSettings();
        LocalizationService.Instance.Initialize(_appSettings.Language);
        _startWithWindows = _appSettings.StartWithWindows;
        _autoConnectOnStartup = _appSettings.AutoConnectOnStartup;
        _githubInstallCount = _appSettings.GitHubAppDownloadCount;
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(AutoConnectOnStartup));
        OnPropertyChanged(nameof(HasGitHubInstallCount));
        OnPropertyChanged(nameof(GitHubInstallCountText));
        OnPropertyChanged(nameof(AdAudienceText));
        OnPropertyChanged(nameof(LanguageToggleText));
        OnPropertyChanged(nameof(AppIsRightToLeft));
        OnPropertyChanged(nameof(AppFlowDirection));
        OnPropertyChanged(nameof(AppTextAlignment));
        OnPropertyChanged(nameof(AppStartHorizontalAlignment));
        OnPropertyChanged(nameof(AppEndHorizontalAlignment));
    }

    private void ToggleLanguage()
    {
        LocalizationService.Instance.ToggleLanguage();
        _appSettings.Language = LocalizationService.Instance.EffectiveLanguage;
        _profileService.SaveAppSettings(_appSettings);
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LanguageToggleText));
        OnPropertyChanged(nameof(AppIsRightToLeft));
        OnPropertyChanged(nameof(AppTitleText));
        OnPropertyChanged(nameof(AppTitleAccentText));
        OnPropertyChanged(nameof(AppTitleFlowDirection));
        OnPropertyChanged(nameof(AppTitleAccentAlignment));
        OnPropertyChanged(nameof(AppFlowDirection));
        OnPropertyChanged(nameof(AppTextAlignment));
        OnPropertyChanged(nameof(AppStartHorizontalAlignment));
        OnPropertyChanged(nameof(AppEndHorizontalAlignment));
        OnPropertyChanged(nameof(GameModeStatusText));
        OnPropertyChanged(nameof(OpenVpnPrerequisiteText));
        OnPropertyChanged(nameof(DonatePayPalText));
        OnPropertyChanged(nameof(CryptoDonationText));
        OnPropertyChanged(nameof(AppCreatorText));
        OnPropertyChanged(nameof(AdPlaceholderTitleText));
        OnPropertyChanged(nameof(AdRequestButtonText));
        OnPropertyChanged(nameof(AdAudienceText));
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(GitHubInstallCountText));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectButtonToolTip));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(MixedProxyPortStatusText));
        OnPropertyChanged(nameof(AppLicenseDisplayText));
        OnPropertyChanged(nameof(ConfigCoreHint));
        OnPropertyChanged(nameof(ConfigValidationText));
        OnPropertyChanged(nameof(SaveStatusText));
        OnPropertyChanged(nameof(ConnectionIpText));
        OnPropertyChanged(nameof(ConnectionIpLabel));
        OnPropertyChanged(nameof(FullRouteStatusText));
        OnPropertyChanged(nameof(RouteModeTitle));
        OnPropertyChanged(nameof(RouteModeDescription));
        OnPropertyChanged(nameof(HeaderCoreText));
        OnPropertyChanged(nameof(HeaderRouteText));
        OnPropertyChanged(nameof(HeaderLeakText));
        OnPropertyChanged(nameof(HealthLeakText));
        OnPropertyChanged(nameof(HealthDnsText));
        OnPropertyChanged(nameof(HealthIpv6Text));
        OnPropertyChanged(nameof(HealthRoutesText));
        OnPropertyChanged(nameof(ConnectedBadgeText));
        OnPropertyChanged(nameof(ConnectedProfileName));
        OnPropertyChanged(nameof(SelectedProfileSummaryText));
        OnPropertyChanged(nameof(PingButtonText));
        OnPropertyChanged(nameof(ConnectedServerPingButtonText));
        OnPropertyChanged(nameof(ServerPingButtonText));
        OnPropertyChanged(nameof(PingResult));
        OnPropertyChanged(nameof(ServerPingResult));
        OnPropertyChanged(nameof(ProfileCountText));
        OnPropertyChanged(nameof(ActiveProfileTypeText));
        OnPropertyChanged(nameof(ActiveProfileEndpointText));
        OnPropertyChanged(nameof(ProfileSaveHintText));
        RaiseProfileCardChanged();
        OnPropertyChanged(nameof(Profiles));
    }

    private static void UpdateStartupRegistry(bool enable)
    {
        const string runKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string appName = "TunnelX";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ??
                              Process.GetCurrentProcess().MainModule?.FileName ??
                              System.IO.Path.Combine(AppContext.BaseDirectory, "TunnelX.exe");
                key.SetValue(appName, $"\"{exePath}\"");

                System.Windows.MessageBox.Show(
                    "استارت‌آپ فعال شد.\n\n⚠️ برای کارکرد صحیح، پس از این نباید محل فایل اجرایی TunnelX را تغییر دهید.",
                    "TunnelX — استارت‌آپ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                if (key.GetValue(appName) != null)
                    key.DeleteValue(appName);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[STARTUP] Registry update failed: {ex.Message}");
        }
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    #endregion
}
