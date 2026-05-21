namespace AppTunnel.Services;

/// <summary>
/// Tray/in-app notification audience. Informational can be disabled in settings;
/// promotional always shows (updates, Telegram channel, future promos).
/// </summary>
public enum AppNotificationChannel
{
    Informational,
    Promotional
}
