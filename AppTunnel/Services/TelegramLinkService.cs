using System.Diagnostics;

namespace AppTunnel.Services;

public static class TelegramLinkService
{
    public static void OpenChannel()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.TelegramChannelDeepLink,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Warning($"[UI] Telegram deep link failed, falling back to web URL: {ex.Message}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppInfo.TelegramChannelUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception fallbackEx)
            {
                Logger.Warning($"[UI] Telegram web URL failed: {fallbackEx.Message}");
            }
        }
    }
}
