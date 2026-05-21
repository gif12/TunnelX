using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using AppTunnel.Services;
using AppTunnel.Views.Controls;
using Application = System.Windows.Application;

namespace AppTunnel.Views;

public partial class TrayToastWindow : Window
{
    private const int ShowDurationMs = 4200;
    private const int SlideMs = 280;
    private const int FadeMs = 220;
    /// <summary>Vertical gap between stacked tray toasts (Telegram-style column).</summary>
    private const double StackGap = 8;
    private const double ToastShadowPadding = 10;
    private const double FallbackToastHeight = 100;

    private static readonly List<TrayToastWindow> ActiveToasts = [];
    private static readonly object ToastLock = new();
    private static bool _languageHookRegistered;

    private Action? _onClick;
    private Action? _onDismissed;
    private CancellationTokenSource? _dismissCts;
    private bool _isClosing;
    private bool _isPersistent;
    private bool _promoMode;
    private bool _stickyTrayMode;

    private string? _titleKey;
    private string? _messageKey;
    private object?[]? _messageFormatArgs;
    private string? _messageLiteral;
    private AppNotificationKind _kind;

    private string? _promoTitleKey;
    private string? _promoMessageKey;
    private string? _promoActionKey;
    private string? _promoSecondaryActionKey;
    private Action? _promoOpenChannel;
    private Action? _promoSecondaryAction;
    private bool _updateDualPromoMode;

    public TrayToastWindow()
    {
        InitializeComponent();
        RegisterLanguageRefreshHook();
        Loaded += (_, _) =>
        {
            UpdateLayout();
            RepositionAll();
        };
        SizeChanged += (_, _) => RepositionAll();
    }

    private static void RegisterLanguageRefreshHook()
    {
        if (_languageHookRegistered)
            return;
        _languageHookRegistered = true;
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            if (Application.Current?.Dispatcher is null)
                return;
            Application.Current.Dispatcher.BeginInvoke(RefreshAllActiveToasts);
        };
    }

    private static void RefreshAllActiveToasts()
    {
        List<TrayToastWindow> snapshot;
        lock (ToastLock)
            snapshot = [..ActiveToasts];

        foreach (var toast in snapshot)
        {
            if (toast._isClosing)
                continue;
            toast.ApplyLocalizedContent();
        }

        RepositionAll();
    }

    /// <summary>Auto-dismiss tray toast; <paramref name="titleKey"/> null hides the title row.</summary>
    public static void ShowFromKeys(
        string? titleKey,
        string messageKey,
        AppNotificationKind kind,
        Action? onClick = null,
        object?[]? messageFormatArgs = null)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var toast = new TrayToastWindow();
            toast.PresentFromKeys(titleKey, messageKey, kind, onClick, messageFormatArgs, messageLiteral: null);
        });
    }

    /// <summary>Tray toast with a body that is not re-localized (e.g. dynamic error text).</summary>
    public static void ShowFromLiteral(
        string? titleKey,
        string messageLiteral,
        AppNotificationKind kind,
        Action? onClick = null)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var toast = new TrayToastWindow();
            toast.PresentFromKeys(titleKey, messageKey: null, kind, onClick, messageFormatArgs: null, messageLiteral);
        });
    }

    /// <summary>Stays until the user closes (✕). Clicking the card runs <paramref name="onClick"/> (e.g. bring main window forward).</summary>
    public static void ShowStickyFromKeys(
        string? titleKey,
        string messageKey,
        AppNotificationKind kind,
        Action? onClick = null)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var toast = new TrayToastWindow();
            toast.PresentStickyFromKeys(titleKey, messageKey, kind, onClick);
        });
    }

    public static TrayToastWindow ShowPersistent(
        string titleKey,
        string messageKey,
        string actionKey,
        AppNotificationKind kind,
        Action onOpenChannel,
        Action onDismissed)
    {
        TrayToastWindow? toast = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            toast = new TrayToastWindow();
            toast.PresentPromoFromKeys(titleKey, messageKey, actionKey, kind, onOpenChannel, onDismissed);
        });
        return toast!;
    }

    public static TrayToastWindow ShowPersistentDualAction(
        string titleKey,
        string messageKey,
        string primaryActionKey,
        string secondaryActionKey,
        AppNotificationKind kind,
        Action onPrimary,
        Action onSecondary,
        Action onDismissed)
    {
        TrayToastWindow? toast = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            toast = new TrayToastWindow();
            toast.PresentDualPromoFromKeys(
                titleKey,
                messageKey,
                primaryActionKey,
                secondaryActionKey,
                kind,
                onPrimary,
                onSecondary,
                onDismissed);
        });
        return toast!;
    }

    private void PresentFromKeys(
        string? titleKey,
        string? messageKey,
        AppNotificationKind kind,
        Action? onClick,
        object?[]? messageFormatArgs,
        string? messageLiteral)
    {
        _onClick = onClick;
        _isPersistent = false;
        _promoMode = false;
        _stickyTrayMode = false;
        _titleKey = titleKey;
        _messageKey = messageKey;
        _messageFormatArgs = messageFormatArgs;
        _messageLiteral = messageLiteral;
        _kind = kind;

        lock (ToastLock)
            ActiveToasts.Add(this);

        ApplyLocalizedContent();
        AnimateShow();
        ScheduleAutoDismissIfNeeded();
    }

    private void PresentStickyFromKeys(string? titleKey, string messageKey, AppNotificationKind kind, Action? onClick)
    {
        _onClick = onClick;
        _isPersistent = true;
        _promoMode = false;
        _stickyTrayMode = true;
        _titleKey = titleKey;
        _messageKey = messageKey;
        _messageFormatArgs = null;
        _messageLiteral = null;
        _kind = kind;

        lock (ToastLock)
            ActiveToasts.Add(this);

        ApplyLocalizedContent();
        AnimateShow();
    }

    private void PresentPromoFromKeys(
        string titleKey,
        string messageKey,
        string actionKey,
        AppNotificationKind kind,
        Action onOpenChannel,
        Action onDismissed)
    {
        _isPersistent = true;
        _promoMode = true;
        _updateDualPromoMode = false;
        _stickyTrayMode = false;
        _onDismissed = onDismissed;
        _kind = kind;

        _promoTitleKey = titleKey;
        _promoMessageKey = messageKey;
        _promoActionKey = actionKey;
        _promoSecondaryActionKey = null;
        _promoOpenChannel = onOpenChannel;
        _promoSecondaryAction = null;

        lock (ToastLock)
            ActiveToasts.Add(this);

        ApplyLocalizedContent();

        AnimateShow();
    }

    private void PresentDualPromoFromKeys(
        string titleKey,
        string messageKey,
        string primaryActionKey,
        string secondaryActionKey,
        AppNotificationKind kind,
        Action onPrimary,
        Action onSecondary,
        Action onDismissed)
    {
        _isPersistent = true;
        _promoMode = true;
        _updateDualPromoMode = true;
        _stickyTrayMode = false;
        _onDismissed = onDismissed;
        _kind = kind;

        _promoTitleKey = titleKey;
        _promoMessageKey = messageKey;
        _promoActionKey = primaryActionKey;
        _promoSecondaryActionKey = secondaryActionKey;
        _promoOpenChannel = onPrimary;
        _promoSecondaryAction = onSecondary;

        lock (ToastLock)
            ActiveToasts.Add(this);

        ApplyLocalizedContent();

        AnimateShow();
    }

    private void ApplyLocalizedContent()
    {
        FlowDirection = LocalizationService.Instance.FlowDirection;

        if (_promoMode && _updateDualPromoMode)
        {
            var loc = LocalizationService.Instance;
            var title = loc.T(_promoTitleKey ?? string.Empty);
            var message = loc.T(_promoMessageKey ?? string.Empty);
            var primary = loc.T(_promoActionKey ?? string.Empty);
            var secondary = loc.T(_promoSecondaryActionKey ?? string.Empty);
            NotificationCard.SetDualPromoContent(title, message, primary, secondary, _kind);

            NotificationCard.SetCallbacks(
                onAction: () =>
                {
                    _promoOpenChannel?.Invoke();
                    Dismiss(markDismissed: false);
                },
                onClose: () => Dismiss(markDismissed: true),
                onSecondaryAction: () => _promoSecondaryAction?.Invoke());
        }
        else if (_promoMode)
        {
            var loc = LocalizationService.Instance;
            var title = loc.T(_promoTitleKey ?? string.Empty);
            var message = loc.T(_promoMessageKey ?? string.Empty);
            var action = loc.T(_promoActionKey ?? string.Empty);
            NotificationCard.SetPromoContent(title, message, action, _kind);

            NotificationCard.SetCallbacks(
                onAction: () =>
                {
                    _promoOpenChannel?.Invoke();
                    Dismiss(markDismissed: false);
                },
                onClose: () => Dismiss(markDismissed: true));
        }
        else if (_stickyTrayMode)
        {
            var loc = LocalizationService.Instance;
            var title = string.IsNullOrWhiteSpace(_titleKey) ? null : loc.T(_titleKey!);
            var message = BuildStandardMessage(loc);
            NotificationCard.SetPersistentInfoContent(title, message, _kind, () => _onClick?.Invoke());
            NotificationCard.SetCallbacks(
                onAction: null,
                onClose: () => Dismiss(markDismissed: false));
        }
        else
        {
            var loc = LocalizationService.Instance;
            var title = string.IsNullOrWhiteSpace(_titleKey) ? null : loc.T(_titleKey!);
            var message = BuildStandardMessage(loc);
            NotificationCard.SetContent(title, message, _kind);
        }
    }

    private string BuildStandardMessage(LocalizationService loc)
    {
        if (!string.IsNullOrEmpty(_messageKey))
        {
            return _messageFormatArgs is { Length: > 0 }
                ? loc.Format(_messageKey!, _messageFormatArgs)
                : loc.T(_messageKey!);
        }

        return _messageLiteral ?? string.Empty;
    }

    private void AnimateShow()
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        RepositionAll();

        var slideIn = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(SlideMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        NotificationCard.SlideTransformElement.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty, slideIn);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ScheduleAutoDismissIfNeeded()
    {
        if (_isPersistent)
            return;

        _dismissCts = new CancellationTokenSource();
        var token = _dismissCts.Token;
        Task.Delay(ShowDurationMs, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Application.Current.Dispatcher.BeginInvoke(() => Dismiss(markDismissed: false));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Stack all active toasts bottom-up (newest nearest the taskbar corner), like Telegram:
    /// each toast's top is the sum of heights + gaps of all toasts below it.
    /// </summary>
    private static void RepositionAll()
    {
        var workArea = SystemParameters.WorkArea;
        var margin = 12d;
        var bottom = workArea.Bottom - margin;
        var right = workArea.Right - margin;

        lock (ToastLock)
        {
            // Newest is last in list — place from bottom upward using cumulative heights.
            var y = bottom;
            for (var i = ActiveToasts.Count - 1; i >= 0; i--)
            {
                var toast = ActiveToasts[i];
                toast.UpdateLayout();
                toast.NotificationCard.UpdateLayout();
                var cardHeight = toast.NotificationCard.ActualHeight;
                var h = toast.ActualHeight > 0
                    ? toast.ActualHeight
                    : cardHeight > 0
                        ? cardHeight + ToastShadowPadding
                        : FallbackToastHeight;
                if (!toast.IsLoaded)
                    h = FallbackToastHeight;

                y -= h;
                var left = right - toast.ActualWidth;
                if (left < workArea.Left + margin)
                    left = workArea.Left + margin;

                toast.Left = left;
                toast.Top = y;
                y -= StackGap;
            }
        }
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (_isPersistent)
            return;

        _onClick?.Invoke();
        Dismiss(markDismissed: false);
    }

    public void DismissSilently() => Dismiss(markDismissed: false);

    private void Dismiss(bool markDismissed)
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _dismissCts?.Cancel();

        if (markDismissed)
            _onDismissed?.Invoke();

        var slideOut = new DoubleAnimation(0, 16, TimeSpan.FromMilliseconds(FadeMs));
        NotificationCard.SlideTransformElement.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty, slideOut);

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(FadeMs));
        fadeOut.Completed += (_, _) =>
        {
            lock (ToastLock)
                ActiveToasts.Remove(this);

            Close();
            RepositionAll();
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    protected override void OnClosed(EventArgs e)
    {
        lock (ToastLock)
            ActiveToasts.Remove(this);
        base.OnClosed(e);
    }
}
