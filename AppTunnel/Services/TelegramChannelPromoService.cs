using AppTunnel.Views;
using Application = System.Windows.Application;

namespace AppTunnel.Services;

/// <summary>
/// Promotional tray card (always shown): Telegram channel ~15s after each successful connect.
/// Not gated by informational-notification settings.
/// </summary>
public static class TelegramChannelPromoService
{
    private const int DelayAfterConnectMs = 15_000;

    private static CancellationTokenSource? _scheduleCts;
    private static TrayToastWindow? _activePromo;

    public static void ScheduleAfterSuccessfulConnection()
    {
        CancelSchedule();
        _scheduleCts = new CancellationTokenSource();
        var token = _scheduleCts.Token;

        Task.Delay(DelayAfterConnectMs, token).ContinueWith(task =>
        {
            if (task.IsCanceled)
                return;

            Application.Current.Dispatcher.BeginInvoke(ShowPromoIfStillConnected);
        }, TaskScheduler.Default);
    }

    public static void OnDisconnected()
    {
        CancelSchedule();
        Application.Current.Dispatcher.BeginInvoke(DismissActivePromo);
    }

    private static void ShowPromoIfStillConnected()
    {
        if (!AppNotificationService.IsTunnelConnected)
            return;

        _activePromo?.DismissSilently();
        _activePromo = null;

        const string titleKey = "عضویت در کانال تلگرام";
        const string messageKey = "📢 برای دریافت اخبار آپدیت و اطلاع‌رسانی، در کانال تلگرام TunnelX عضو شوید";
        const string actionKey = "عضویت در کانال تلگرام";

        _activePromo = TrayToastWindow.ShowPersistent(
            titleKey,
            messageKey,
            actionKey,
            AppNotificationKind.Info,
            onOpenChannel: TelegramLinkService.OpenChannel,
            onDismissed: DismissActivePromo);
    }

    private static void DismissActivePromo()
    {
        _activePromo?.DismissSilently();
        _activePromo = null;
    }

    private static void CancelSchedule()
    {
        _scheduleCts?.Cancel();
        _scheduleCts = null;
    }
}
