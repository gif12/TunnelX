using System.Windows;
using AppTunnel.Models;
using AppTunnel.Views;

namespace AppTunnel.Services;

/// <summary>
/// Branded in-app and tray notifications (Telegram-style cards) instead of Windows balloon tips.
/// Tray toasts pass Persian source keys so text follows the active UI language after toggles.
/// </summary>
public static class AppNotificationService
{
    private static MainWindow? _mainWindow;
    private static Func<bool>? _isInformationalEnabled;

    public static void Initialize(MainWindow mainWindow) => _mainWindow = mainWindow;

    /// <summary>Called from <see cref="ViewModels.MainViewModel"/> after app settings load.</summary>
    public static void Configure(Func<bool> isInformationalEnabled) =>
        _isInformationalEnabled = isInformationalEnabled;

    public static bool IsTunnelConnected =>
        _mainWindow?.DataContext is ViewModels.MainViewModel vm &&
        vm.ConnectionState == ConnectionState.Connected;

    private static bool ShouldShow(AppNotificationChannel channel) =>
        channel == AppNotificationChannel.Promotional ||
        (_isInformationalEnabled?.Invoke() ?? true);

    public static void ShowTrayPersistent(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowStickyFromKeys(
            titleKey,
            messageKey,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    public static void ShowPromotionalTrayPersistent(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info) =>
        ShowTrayPersistent(titleKey, messageKey, kind, AppNotificationChannel.Promotional);

    public static void ShowTray(
        string titleKey,
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Info,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowFromKeys(
            titleKey,
            messageKey,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    public static void ShowTrayFormat(
        string titleKey,
        string messageFormatKey,
        AppNotificationKind kind,
        AppNotificationChannel channel = AppNotificationChannel.Informational,
        params object?[] formatArgs)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowFromKeys(
            titleKey,
            messageFormatKey,
            kind,
            () => _mainWindow?.BringToForeground(),
            formatArgs);
    }

    /// <summary>Title is localized from <paramref name="titleKey"/>; body is shown as-is (e.g. dynamic status).</summary>
    public static void ShowTrayLiteralBody(
        string? titleKey,
        string messageLiteral,
        AppNotificationKind kind,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        TrayToastWindow.ShowFromLiteral(
            titleKey,
            messageLiteral,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    /// <summary>In-app toast when the window is visible; otherwise a short tray toast. <paramref name="messageKey"/> is a Persian source key.</summary>
    public static void ShowBrief(
        string messageKey,
        AppNotificationKind kind = AppNotificationKind.Success,
        AppNotificationChannel channel = AppNotificationChannel.Informational)
    {
        if (!ShouldShow(channel)) return;

        var text = LocalizationService.Instance.T(messageKey);
        if (_mainWindow != null && _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
            _mainWindow.ShowAppToast(text, kind);
        else
            TrayToastWindow.ShowFromKeys(null, messageKey, kind, () => _mainWindow?.BringToForeground());
    }
}
