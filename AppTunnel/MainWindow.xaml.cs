using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using AppTunnel.Models;
using AppTunnel.ViewModels;
using AppTunnel.Helpers;
using AppTunnel.Services;
using Application = System.Windows.Application;

namespace AppTunnel;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource _loadCts = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isRealExit;
    private ConnectionState _lastNotifiedConnectionState = ConnectionState.Disconnected;
    private bool _updateNotificationShown;
    private const int WmSysCommand = 0x0112;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int ScSize = 0xF000;
    private const int ScMove = 0xF010;
    private const int ScMaximize = 0xF030;
    private const int ScKeyMenu = 0xF100;
    private const int ScRestore = 0xF120;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        LocalizationService.Instance.ApplyTo(this);
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LocalizationService.Instance.ApplyTo(this);
                RefreshTrayText();
            });
        };

        InitializeTrayIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyAdaptiveWindowSize();
        MaxWidth = Width;
        MaxHeight = Height;

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowMessageHook);
    }

    private void ApplyAdaptiveWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        const double screenMargin = 24;

        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - screenMargin));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - screenMargin));

        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand)
        {
            var command = wParam.ToInt32() & 0xFFF0;
            if (command is ScMaximize or ScSize or ScKeyMenu)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        else if (msg == WmGetMinMaxInfo)
        {
            MaxWidth = Width;
            MaxHeight = Height;
        }

        return IntPtr.Zero;
    }

    private void InitializeTrayIcon()
    {
        var iconStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/app.ico"))?.Stream;
        
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = LocalizationService.Instance.T("TunnelX — Split Traffic Per App"),
            Visible = false
        };

        if (iconStream != null)
            _trayIcon.Icon = new System.Drawing.Icon(iconStream);

        // Double-click tray icon → show window
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        // Context menu
        var menu = new System.Windows.Forms.ContextMenuStrip();
        
        var showItem = new System.Windows.Forms.ToolStripMenuItem("نمایش TunnelX");
        showItem.Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold);
        showItem.Click += (_, _) => ShowFromTray();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var statusItem = new System.Windows.Forms.ToolStripMenuItem($"{LocalizationService.Instance.T("وضعیت")}: {_viewModel.StatusText}");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);

        // Update status text dynamically
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    statusItem.Text = $"{LocalizationService.Instance.T("وضعیت")}: {_viewModel.StatusText}";
                });
            }
            else if (args.PropertyName == nameof(MainViewModel.ConnectionState))
            {
                Dispatcher.BeginInvoke(NotifyConnectionStateChanged);
            }
            else if (args.PropertyName == nameof(MainViewModel.IsUpdateAvailable))
            {
                Dispatcher.BeginInvoke(NotifyUpdateAvailable);
            }
        };

        var updateItem = new System.Windows.Forms.ToolStripMenuItem("بررسی بروزرسانی");
        updateItem.Click += (_, _) =>
        {
            BringToForeground();
            if (_viewModel.CheckForUpdatesCommand.CanExecute(null))
                _viewModel.CheckForUpdatesCommand.Execute(null);
        };
        menu.Items.Add(updateItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("خروج از برنامه");
        exitItem.Click += async (_, _) =>
        {
            if (_viewModel.IsConnected)
            {
                bool confirmed = false;
                Dispatcher.Invoke(() =>
                {
                    confirmed = DialogService.Confirm(
                        "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟",
                        "TunnelX — خروج");
                });

                if (!confirmed) return;

                try { await _viewModel.DisconnectAndCleanupAsync(); }
                catch { }
            }

            _isRealExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Dispatcher.Invoke(() => Application.Current.Shutdown());
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = menu;
        RefreshTrayText();
    }

    private void RefreshTrayText()
    {
        if (_trayIcon?.ContextMenuStrip == null) return;

        _trayIcon.Text = LocalizationService.Instance.T("TunnelX — Split Traffic Per App");
        var items = _trayIcon.ContextMenuStrip.Items;
        if (items.Count > 0 && items[0] is System.Windows.Forms.ToolStripMenuItem showItem)
            showItem.Text = LocalizationService.Instance.T("نمایش TunnelX");
        if (items.Count > 2 && items[2] is System.Windows.Forms.ToolStripMenuItem statusItem)
            statusItem.Text = $"{LocalizationService.Instance.T("وضعیت")}: {_viewModel.StatusText}";
        if (items.Count > 3 && items[3] is System.Windows.Forms.ToolStripMenuItem updateItem)
            updateItem.Text = LocalizationService.Instance.T("بررسی بروزرسانی");
        if (items.Count > 5 && items[5] is System.Windows.Forms.ToolStripMenuItem exitItem)
            exitItem.Text = LocalizationService.Instance.T("خروج از برنامه");
    }

    private void ShowFromTray()
    {
        BringToForeground();
    }

    public void BringToForeground()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            if (_trayIcon != null) _trayIcon.Visible = false;
        });
    }

    private void MinimizeToTray()
    {
        Hide();
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(
                2000,
                LocalizationService.Instance.T("TunnelX در پس‌زمینه فعال است"),
                LocalizationService.Instance.T("برای باز کردن پنجره، روی آیکن کنار ساعت دوبار کلیک کنید."),
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void NotifyConnectionStateChanged()
    {
        if (_trayIcon == null) return;

        var state = _viewModel.ConnectionState;
        if (state == _lastNotifiedConnectionState) return;
        _lastNotifiedConnectionState = state;

        switch (state)
        {
            case ConnectionState.Connected:
                ShowTrayNotification(LocalizationService.Instance.T("تونل فعال شد"), GetConnectedTrayMessage(),
                    System.Windows.Forms.ToolTipIcon.Info);
                break;
            case ConnectionState.Disconnected:
                ShowTrayNotification(LocalizationService.Instance.T("تونل خاموش شد"), LocalizationService.Instance.T("ارتباط امن متوقف شده و ترافیک دیگر از TunnelX عبور نمی‌کند."),
                    System.Windows.Forms.ToolTipIcon.Info);
                break;
            case ConnectionState.Error:
                ShowTrayNotification(LocalizationService.Instance.T("اتصال برقرار نشد"), GetErrorTrayMessage(),
                    System.Windows.Forms.ToolTipIcon.Warning);
                break;
        }
    }

    private void NotifyUpdateAvailable()
    {
        if (_trayIcon == null || _updateNotificationShown || !_viewModel.IsUpdateAvailable)
            return;

        _updateNotificationShown = true;
        ShowTrayNotification(LocalizationService.Instance.T("نسخه جدید آماده است"),
            LocalizationService.Instance.T("از منوی System Tray یا بخش بروزرسانی، صفحه دانلود TunnelX را باز کنید."),
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private string GetConnectedTrayMessage()
    {
        var profileName = _viewModel.SelectedProfileName;
        if (!string.IsNullOrWhiteSpace(profileName))
            return LocalizationService.Instance.Format("پروفایل «{0}» فعال است و ترافیک انتخاب‌شده از تونل عبور می‌کند.", profileName);

        return LocalizationService.Instance.T("ترافیک انتخاب‌شده از TunnelX عبور می‌کند.");
    }

    private string GetErrorTrayMessage()
    {
        var status = _viewModel.StatusText?.Trim();
        if (string.IsNullOrWhiteSpace(status) ||
            status == LocalizationService.Instance.T("خطا") ||
            status.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return LocalizationService.Instance.T("جزئیات خطا را در پنجره برنامه یا لاگ‌ها بررسی کنید.");

        return status.StartsWith(LocalizationService.Instance.T("خطا"), StringComparison.OrdinalIgnoreCase) ||
               status.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            ? status
            : LocalizationService.Instance.Format("جزئیات: {0}", status);
    }

    private void ShowTrayNotification(string title, string message, System.Windows.Forms.ToolTipIcon icon)
    {
        if (_trayIcon == null) return;
        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(3500, title, message, icon);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Instance.ApplyTo(this);
        UpdateLogPanelCornerState(LogPanel.Visibility == Visibility.Visible);
        RefreshTrayText();

        // Force window to foreground — borderless/transparent windows sometimes
        // start behind other windows or appear unfocused on slower machines.
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        // Dismiss the startup overlay quickly; app discovery continues in the
        // background so the first screen becomes usable immediately.
        var ct = _loadCts.Token;
        LoadingStatusText.Text = LocalizationService.Instance.T("بارگذاری لیست برنامه‌های نصب‌شده...");
        _ = DismissLoadingOverlaySoonAsync(ct);
        Task.Run(() =>
        {
            var apps = Services.AppDiscoveryService.GetInstalledApps();
            if (!ct.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(() =>
                {
                    foreach (var app in apps)
                        _viewModel.AvailableApps.Add(new AppItemViewModel(app));
                    _viewModel.SearchText = _viewModel.SearchText; // trigger filter
                });
            }
        }, ct).ContinueWith(t =>
        {
            // Safety: always dismiss overlay even if discovery throws
            if (t.IsFaulted)
                Dispatcher.Invoke(HideLoadingOverlay);
        }, TaskScheduler.Default);
    }

    private async Task DismissLoadingOverlaySoonAsync(CancellationToken ct)
    {
        try { await Task.Delay(550, ct); }
        catch (OperationCanceledException) { return; }
        if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke((Action)HideLoadingOverlay);
    }

    private void HideLoadingOverlay()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible) return;

        var fadeOut = new DoubleAnimation(1.0, 0.0,
            new Duration(TimeSpan.FromMilliseconds(260)));
        fadeOut.Completed += (_, _) =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.BeginAnimation(OpacityProperty, null);
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isRealExit)
        {
            _loadCts.Cancel();
            // Ensure VPN is disconnected even if shutdown is triggered externally
            if (_viewModel.IsConnected)
            {
                e.Cancel = true;
                try { await _viewModel.DisconnectAndCleanupAsync(); } catch { }
                _isRealExit = true;
                _trayIcon?.Dispose();
                Application.Current.Shutdown();
                return;
            }
            _viewModel.ForceSave();
            _trayIcon?.Dispose();
            return;
        }

        // X button → minimize to tray instead of closing
        e.Cancel = true;
        MinimizeToTray();
    }


    // Window control handlers
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // Minimize button → minimize to tray
        MinimizeToTray();
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // X button → show confirmation and exit
        string message = _viewModel.IsConnected 
            ? "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟"
            : "آیا می‌خواهید از TunnelX خارج شوید؟";

        if (DialogService.Confirm(message, "TunnelX — خروج", this))
        {
            if (_viewModel.IsConnected)
            {
                try { await _viewModel.DisconnectAndCleanupAsync(); }
                catch { }
            }

            _isRealExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }
    }

    private async void OnExitAppClick(object sender, RoutedEventArgs e)
    {
        // Show confirmation dialog
        string message = _viewModel.IsConnected 
            ? "اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟"
            : "آیا می‌خواهید از TunnelX خارج شوید؟";

        if (DialogService.Confirm(message, "TunnelX — خروج", this))
        {
            if (_viewModel.IsConnected)
            {
                try { await _viewModel.DisconnectAndCleanupAsync(); }
                catch { }
            }

            _isRealExit = true;
            _trayIcon?.Dispose();
            _trayIcon = null;
            Application.Current.Shutdown();
        }
    }

    private bool _logPanelLoaded;
    private bool _isLogPanelAnimating;
    private const double LogPanelWidth = 350;
    private static readonly Duration LogPanelAnimationDuration = TimeSpan.FromMilliseconds(220);
    private string _logFilter = "All";

    private void OnShowLogClick(object sender, RoutedEventArgs e)
    {
        if (_isLogPanelAnimating)
            return;

        if (!_logPanelLoaded)
        {
            _logPanelLoaded = true;
            LogTextBox.Text = ApplyLogFilter(Logger.GetAllLogs());
            LogTextBox.ScrollToEnd();
            Logger.LogAdded += OnLogAdded;
        }

        var shouldOpen = LogPanel.Visibility != Visibility.Visible;
        AnimateLogPanel(shouldOpen);
    }

    private void AnimateLogPanel(bool open)
    {
        _isLogPanelAnimating = true;
        UpdateLogPanelCornerState(open);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var currentPanelWidth = LogPanel.Visibility == Visibility.Visible ? LogPanel.ActualWidth : 0;
        var maxWindowWidth = SystemParameters.WorkArea.Width - 24;
        var availablePanelWidth = Math.Max(0, maxWindowWidth - (Width - currentPanelWidth));
        var targetPanelWidth = open ? Math.Min(LogPanelWidth, availablePanelWidth) : 0;
        var targetWindowWidth = open
            ? Math.Min(maxWindowWidth, Width + (targetPanelWidth - currentPanelWidth))
            : Math.Max(MinWidth, Width - currentPanelWidth);

        if (open)
        {
            LogPanel.Visibility = Visibility.Visible;
            LogTextBox.ScrollToEnd();
        }

        var windowAnimation = new DoubleAnimation(targetWindowWidth, LogPanelAnimationDuration)
        {
            EasingFunction = easing
        };
        var panelAnimation = new DoubleAnimation(targetPanelWidth, LogPanelAnimationDuration)
        {
            EasingFunction = easing
        };
        var opacityAnimation = new DoubleAnimation(open ? 1 : 0, LogPanelAnimationDuration)
        {
            EasingFunction = easing
        };

        panelAnimation.Completed += (_, _) =>
        {
            BeginAnimation(WidthProperty, null);
            LogPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            LogPanel.BeginAnimation(OpacityProperty, null);

            Width = targetWindowWidth;
            LogPanel.Width = targetPanelWidth;
            LogPanel.Opacity = open ? 1 : 0;

            if (!open)
                LogPanel.Visibility = Visibility.Collapsed;

            _isLogPanelAnimating = false;
        };

        BeginAnimation(WidthProperty, windowAnimation, HandoffBehavior.SnapshotAndReplace);
        LogPanel.BeginAnimation(FrameworkElement.WidthProperty, panelAnimation, HandoffBehavior.SnapshotAndReplace);
        LogPanel.BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateLogPanelCornerState(bool logOpen)
    {
        if (logOpen)
        {
            MainTitleBar.CornerRadius = new CornerRadius(12, 0, 0, 0);
            MainFooterBar.CornerRadius = new CornerRadius(0, 0, 0, 12);
        }
        else
        {
            MainTitleBar.CornerRadius = new CornerRadius(12, 12, 0, 0);
            MainFooterBar.CornerRadius = new CornerRadius(0, 0, 12, 12);
        }
    }

    private void OnLogAdded(string logEntry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!LogMatchesFilter(logEntry)) return;
            LogTextBox.AppendText(logEntry + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void OnLogClearClick(object sender, RoutedEventArgs e)
    {
        Logger.Clear();
        LogTextBox.Clear();
        ShowToast(LocalizationService.Instance.T("لاگ‌ها پاک شدند"));
    }

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogTextBox.Text))
            {
                ShowToast(LocalizationService.Instance.T("لاگی برای کپی وجود ندارد"), "ℹ");
                return;
            }

            System.Windows.Clipboard.SetText(LogTextBox.Text);
            ShowToast(LocalizationService.Instance.T("لاگ کپی شد"));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy logs failed: {ex.Message}");
            ShowToast(LocalizationService.Instance.T("کپی لاگ ناموفق بود"), "⚠");
        }
    }

    private void OnLogCopyLastErrorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = Logger.GetAllLogs()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var line = lines.Reverse().FirstOrDefault(l =>
                l.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)) ??
                       lines.Reverse().FirstOrDefault(l =>
                l.Contains("[WARN]", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(line))
            {
                ShowToast(LocalizationService.Instance.T("آخرین خطا یا هشدار پیدا نشد"), "ℹ");
                return;
            }

            System.Windows.Clipboard.SetText(line);
            ShowToast(LocalizationService.Instance.T("آخرین خطا یا هشدار کپی شد"));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Copy last error/warning failed: {ex.Message}");
            ShowToast(LocalizationService.Instance.T("کپی لاگ ناموفق بود"), "⚠");
        }
    }

    private void OnLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogFilterCombo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag)
            _logFilter = tag;

        if (_logPanelLoaded)
        {
            LogTextBox.Text = ApplyLogFilter(Logger.GetAllLogs());
            LogTextBox.ScrollToEnd();
        }
    }

    private string ApplyLogFilter(string logs)
    {
        if (_logFilter == "All") return logs;

        var lines = logs.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(LogMatchesFilter);
        return string.Join(Environment.NewLine, lines);
    }

    private bool LogMatchesFilter(string line) => _logFilter switch
    {
        "Error" => line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase),
        "Warn" => line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase),
        "Dns" => line.Contains("[DNS", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("DNS-", StringComparison.OrdinalIgnoreCase),
        "Route" => line.Contains("[ROUTE", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("[FULL-ROUTE]", StringComparison.OrdinalIgnoreCase) ||
                   line.Contains("route.exe", StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private void OnShowHelpClick(object sender, RoutedEventArgs e)
    {
        var helpWindow = new AppTunnel.Views.HelpWindow
        {
            Owner = this,
            DataContext = _viewModel
        };
        LocalizationService.Instance.ApplyTo(helpWindow);
        helpWindow.ShowDialog();
    }

    private void OnDonateClick(object sender, RoutedEventArgs e)
    {
        var donationDialog = new AppTunnel.Views.DonationDialog
        {
            Owner = this
        };
        LocalizationService.Instance.ApplyTo(donationDialog);
        donationDialog.ShowDialog();
    }

    private void OnNestedScrollPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source) return;

        var parent = FindVisualParent<ScrollViewer>(source);
        if (parent == null) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender
        });
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typed)
                return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void OnOuterBorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var border = (System.Windows.Controls.Border)sender;
        border.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 12, 12);
    }

    private CancellationTokenSource? _toastCts;

    public void ShowToast(string message, string icon = "✅", int durationMs = 3000)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastIcon.Text = icon;
        ToastMessage.Text = message;
        ToastPanel.Visibility = Visibility.Visible;
        ToastPanel.Opacity = 1;

        Task.Delay(durationMs, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                    fadeOut.Completed += (__, ___) => ToastPanel.Visibility = Visibility.Collapsed;
                    ToastPanel.BeginAnimation(OpacityProperty, fadeOut);
                });
            }
        }, TaskScheduler.Default);
    }
}
