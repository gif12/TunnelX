using Application = System.Windows.Application;

namespace AppTunnel.Services;

/// <summary>
/// Schedules GitHub update checks 20s after each successful tunnel connect (tunnel egress only).
/// </summary>
public static class UpdateCheckSchedulerService
{
    private const int DelayAfterConnectMs = 20_000;

    private static CancellationTokenSource? _scheduleCts;

    public static void ScheduleAfterSuccessfulConnection(Action checkWhenReady)
    {
        Cancel();
        _scheduleCts = new CancellationTokenSource();
        var token = _scheduleCts.Token;

        Task.Delay(DelayAfterConnectMs, token).ContinueWith(task =>
        {
            if (task.IsCanceled)
                return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (AppNotificationService.IsTunnelConnected)
                    checkWhenReady();
            });
        }, TaskScheduler.Default);
    }

    public static void OnDisconnected() => Cancel();

    private static void Cancel()
    {
        _scheduleCts?.Cancel();
        _scheduleCts?.Dispose();
        _scheduleCts = null;
    }
}
