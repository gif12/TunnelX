namespace AppTunnel.Services;

public static class AppInfo
{
    public const string AppName = "TunnelX";
    public const string CreatorName = "MaxFan";
    public const string GitHubUrl = "https://github.com/MaxiFan/TunnelX";
    public const string LatestReleaseUrl = GitHubUrl + "/releases/latest";
    public const string TelegramContactUrl = "https://t.me/maxifaan";
    public const string TelegramChannelUrl = "https://t.me/tunnelxx";
    public const string TelegramChannelDeepLink = "tg://resolve?domain=tunnelxx";
    public const string TelegramChannelHandle = "@tunnelxx";
    public const string LicenseName = "GPL-3.0-or-later";
    public const string PayPalEmail = "gallafan@gmail.com";
    /// <summary>
    /// Personal-account support link (cmd=_xclick). Recipient is gallafan@gmail.com; supporter enters amount on PayPal.
    /// Avoids cmd=_donations which requires a nonprofit/organization donation profile.
    /// </summary>
    public const string PayPalDonateUrl =
        "https://www.paypal.com/cgi-bin/webscr?cmd=_xclick&business=gallafan%40gmail.com&item_name=TunnelX%20support&currency_code=USD&no_shipping=1";
    /// <summary>Optional: set your PayPal.Me username at paypal.com/paypalme and use this instead if preferred.</summary>
    public const string PayPalMeUrl = "https://paypal.me/gallafan";
    public const string CryptoDonationText =
        "ترون / USDT روی TRC20: TNWV867fQDT6zpLunHgbeMjrN6ic63LQSu\n" +
        "بیت‌کوین: bc1qgx3g47c458fu6smnpqpu0l05hha82rq2xjet4y\n" +
        "اتریوم / USDT روی ERC20: 0x72d94Bb250E8802441a0ED05686Ee925BC99Fef5\n" +
        "TON: UQD65oL2Vu2OJDSrwQ0wLLSw3g668SREMJ3VPW9k8b6Sy-Yf\n" +
        "BNB Smart Chain: 0xE2a5b01cE2b3713D435Bc16d92eAdd88A82159f0\n" +
        "Dogecoin: DSZRNY65yF679uvjAh6sUAt6YiEEQHwKGb";
    public const string CryptoDonationTextEn =
        "Tron / USDT on TRC20: TNWV867fQDT6zpLunHgbeMjrN6ic63LQSu\n" +
        "Bitcoin: bc1qgx3g47c458fu6smnpqpu0l05hha82rq2xjet4y\n" +
        "Ethereum / USDT on ERC20: 0x72d94Bb250E8802441a0ED05686Ee925BC99Fef5\n" +
        "TON: UQD65oL2Vu2OJDSrwQ0wLLSw3g668SREMJ3VPW9k8b6Sy-Yf\n" +
        "BNB Smart Chain: 0xE2a5b01cE2b3713D435Bc16d92eAdd88A82159f0\n" +
        "Dogecoin: DSZRNY65yF679uvjAh6sUAt6YiEEQHwKGb";

    public static string VersionText =>
        "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                   .GetName().Version?.ToString(3) ?? "1.0.0");

    public static string ReleaseText => $"{AppName} {VersionText}";
    public static string CreatorText => LocalizationService.Instance.IsRightToLeft
        ? $"ساخته شده توسط {CreatorName}"
        : $"Made by {CreatorName}";
}
