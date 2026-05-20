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

    public static void Initialize(MainWindow mainWindow) => _mainWindow = mainWindow;

    public static bool IsTunnelConnected =>
        _mainWindow?.DataContext is ViewModels.MainViewModel vm &&
        vm.ConnectionState == ConnectionState.Connected;

    public static void ShowTrayPersistent(string titleKey, string messageKey, AppNotificationKind kind = AppNotificationKind.Info)
    {
        TrayToastWindow.ShowStickyFromKeys(
            titleKey,
            messageKey,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    public static void ShowTray(string titleKey, string messageKey, AppNotificationKind kind = AppNotificationKind.Info)
    {
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
        params object?[] formatArgs)
    {
        TrayToastWindow.ShowFromKeys(
            titleKey,
            messageFormatKey,
            kind,
            () => _mainWindow?.BringToForeground(),
            formatArgs);
    }

    /// <summary>Title is localized from <paramref name="titleKey"/>; body is shown as-is (e.g. dynamic status).</summary>
    public static void ShowTrayLiteralBody(string? titleKey, string messageLiteral, AppNotificationKind kind)
    {
        TrayToastWindow.ShowFromLiteral(
            titleKey,
            messageLiteral,
            kind,
            () => _mainWindow?.BringToForeground());
    }

    /// <summary>In-app toast when the window is visible; otherwise a short tray toast. <paramref name="messageKey"/> is a Persian source key.</summary>
    public static void ShowBrief(string messageKey, AppNotificationKind kind = AppNotificationKind.Success)
    {
        var text = LocalizationService.Instance.T(messageKey);
        if (_mainWindow != null && _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
            _mainWindow.ShowAppToast(text, kind);
        else
            TrayToastWindow.ShowFromKeys(null, messageKey, kind, () => _mainWindow?.BringToForeground());
    }
}
