namespace AppTunnel.Services;

/// <summary>Detected OpenVPN .ovpn credential/layout scenario for UI and validation.</summary>
public enum OpenVpnProfileScenario
{
    NoConfig,
    NoRemote,
    InlineAuthInConfig,
    CertificateOnly,
    UserPassOnly,
    CertificateAndUserPass,
    EncryptedPrivateKey,
    CertificateAndEncryptedKey,
    FullCredentials
}

public sealed record OpenVpnProfileAnalysis(
    OpenVpnProfileScenario Scenario,
    bool HasRemote,
    int RemoteCount,
    bool HasInlineCertificates,
    bool HasInlineAuthCredentials,
    bool RequiresTunnelXUsername,
    bool RequiresTunnelXPassword,
    bool RequiresPrivateKeyPassphrase,
    string ScenarioHintKey,
    string ScenarioTitleKey);

/// <summary>Parses .ovpn content to decide required profile fields and localized hints.</summary>
public static class OpenVpnProfileAnalyzer
{
    public static OpenVpnProfileAnalysis Analyze(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return new OpenVpnProfileAnalysis(
                OpenVpnProfileScenario.NoConfig,
                HasRemote: false,
                RemoteCount: 0,
                HasInlineCertificates: false,
                HasInlineAuthCredentials: false,
                RequiresTunnelXUsername: false,
                RequiresTunnelXPassword: false,
                RequiresPrivateKeyPassphrase: false,
                ScenarioHintKey: "فایل OpenVPN (.ovpn) را انتخاب کنید",
                ScenarioTitleKey: "نوع کانفیگ OpenVPN: انتخاب نشده");
        }

        var remoteCount = CountRemotes(config);
        var hasRemote = remoteCount > 0;
        var hasInlineAuth = HasInlineAuthUserPassBlock(config);
        var hasAuthDirective = HasAuthUserPassDirective(config);
        var hasInlineCert = HasInlineClientCertificate(config);
        var needsPrivateKeyPass = ConfigLikelyNeedsPrivateKeyPassphrase(config);

        if (!hasRemote)
        {
            return new OpenVpnProfileAnalysis(
                OpenVpnProfileScenario.NoRemote,
                hasRemote,
                remoteCount,
                hasInlineCert,
                hasInlineAuth,
                RequiresTunnelXUsername: false,
                RequiresTunnelXPassword: false,
                needsPrivateKeyPass,
                "این فایل .ovpn هیچ خط remote ندارد؛ فایل را از ارائه‌دهنده دوباره بگیرید",
                "نوع کانفیگ OpenVPN: بدون remote");
        }

        if (hasInlineAuth)
        {
            return new OpenVpnProfileAnalysis(
                needsPrivateKeyPass ? OpenVpnProfileScenario.FullCredentials : OpenVpnProfileScenario.InlineAuthInConfig,
                hasRemote,
                remoteCount,
                hasInlineCert,
                true,
                RequiresTunnelXUsername: false,
                RequiresTunnelXPassword: false,
                needsPrivateKeyPass,
                needsPrivateKeyPass
                    ? "نام کاربری/رمز داخل فایل .ovpn است؛ فقط Secret (رمز کلید) را در TunnelX وارد کنید"
                    : "نام کاربری و رمز داخل خود فایل .ovpn است؛ فقط فایل را انتخاب کنید",
                "نوع کانفیگ OpenVPN: احراز هویت داخل فایل");
        }

        var needsUserPass = hasAuthDirective;
        var needsUser = needsUserPass;
        var needsPass = false; // username required; password optional unless server needs it

        OpenVpnProfileScenario scenario;
        string hintKey;
        string titleKey;

        if (hasInlineCert && needsUserPass && needsPrivateKeyPass)
        {
            scenario = OpenVpnProfileScenario.FullCredentials;
            hintKey = "گواهی در فایل است؛ نام کاربری و رمز (auth-user-pass) و Secret (رمز کلید) را در TunnelX وارد کنید";
            titleKey = "نوع کانفیگ OpenVPN: گواهی + user/pass + Secret";
        }
        else if (hasInlineCert && needsUserPass)
        {
            scenario = OpenVpnProfileScenario.CertificateAndUserPass;
            hintKey = "گواهی کلاینت داخل فایل است؛ نام کاربری و رمز همان حساب OpenVPN را در TunnelX وارد کنید (مثل برنامه OpenVPN)";
            titleKey = "نوع کانفیگ OpenVPN: گواهی + نام کاربری/رمز";
        }
        else if (hasInlineCert && needsPrivateKeyPass)
        {
            scenario = OpenVpnProfileScenario.CertificateAndEncryptedKey;
            hintKey = "گواهی در فایل است؛ Secret (رمز عبور کلید خصوصی رمزگذاری‌شده) را وارد کنید";
            titleKey = "نوع کانفیگ OpenVPN: گواهی + Secret";
        }
        else if (hasInlineCert)
        {
            scenario = OpenVpnProfileScenario.CertificateOnly;
            hintKey = "فقط گواهی/کلید داخل فایل است؛ نام کاربری، رمز و Secret لازم نیست";
            titleKey = "نوع کانفیگ OpenVPN: فقط گواهی";
        }
        else if (needsUserPass && needsPrivateKeyPass)
        {
            scenario = OpenVpnProfileScenario.FullCredentials;
            hintKey = "نام کاربری و رمز (auth-user-pass) و Secret را در TunnelX وارد کنید";
            titleKey = "نوع کانفیگ OpenVPN: user/pass + Secret";
        }
        else if (needsUserPass)
        {
            scenario = OpenVpnProfileScenario.UserPassOnly;
            hintKey = "این فایل auth-user-pass دارد؛ نام کاربری اجباری است. رمز را در صورت نیاز سرور وارد کنید";
            titleKey = "نوع کانفیگ OpenVPN: نام کاربری/رمز";
        }
        else if (needsPrivateKeyPass)
        {
            scenario = OpenVpnProfileScenario.EncryptedPrivateKey;
            hintKey = "کلید خصوصی در فایل رمزگذاری شده؛ Secret (رمز کلید) اجباری است";
            titleKey = "نوع کانفیگ OpenVPN: Secret";
        }
        else
        {
            scenario = OpenVpnProfileScenario.CertificateOnly;
            hintKey = "فایل .ovpn آماده است؛ در صورت خطای اتصال نام کاربری/رمز را از ارائه‌دهنده بپرسید";
            titleKey = "نوع کانفیگ OpenVPN: عمومی";
        }

        return new OpenVpnProfileAnalysis(
            scenario,
            hasRemote,
            remoteCount,
            hasInlineCert,
            false,
            needsUser,
            needsPass,
            needsPrivateKeyPass,
            hintKey,
            titleKey);
    }

    public static bool IsProfileReady(string? config, string? username, string? password, string? privateKeyPassword)
    {
        var analysis = Analyze(config);
        if (analysis.Scenario is OpenVpnProfileScenario.NoConfig or OpenVpnProfileScenario.NoRemote)
            return false;

        if (analysis.RequiresTunnelXUsername && string.IsNullOrWhiteSpace(username))
            return false;

        if (analysis.RequiresPrivateKeyPassphrase && string.IsNullOrWhiteSpace(privateKeyPassword))
            return false;

        return true;
    }

    public static bool TryGetProfileValidationError(
        string? config,
        string? username,
        string? password,
        string? privateKeyPassword,
        out string message)
    {
        message = "";
        var analysis = Analyze(config);

        if (analysis.Scenario == OpenVpnProfileScenario.NoConfig)
        {
            message = LocalizationService.Instance.T("فایل OpenVPN (.ovpn) را انتخاب کنید");
            return true;
        }

        if (analysis.Scenario == OpenVpnProfileScenario.NoRemote)
        {
            message = LocalizationService.Instance.T(analysis.ScenarioHintKey);
            return true;
        }

        if (analysis.RequiresTunnelXUsername && string.IsNullOrWhiteSpace(username))
        {
            message = LocalizationService.Instance.T("نام کاربری OpenVPN را وارد کنید (این فایل auth-user-pass دارد)");
            return true;
        }

        if (analysis.RequiresPrivateKeyPassphrase && string.IsNullOrWhiteSpace(privateKeyPassword))
        {
            message = LocalizationService.Instance.T("این کانفیگ به Secret (رمز کلید خصوصی) نیاز دارد؛ فیلد Secret را پر کنید");
            return true;
        }

        return false;
    }

    public static string GetScenarioHint(string? config) =>
        LocalizationService.Instance.T(Analyze(config).ScenarioHintKey);

    public static string GetScenarioTitle(string? config) =>
        LocalizationService.Instance.T(Analyze(config).ScenarioTitleKey);

    public static string GetUsernameFieldLabel(string? config)
    {
        var analysis = Analyze(config);
        if (analysis.RequiresTunnelXUsername)
            return LocalizationService.Instance.T("نام کاربری *");
        return LocalizationService.Instance.T("نام کاربری (اختیاری)");
    }

    public static string GetPasswordFieldLabel(string? config)
    {
        var analysis = Analyze(config);
        if (analysis.Scenario is OpenVpnProfileScenario.InlineAuthInConfig)
            return LocalizationService.Instance.T("رمز عبور (در فایل)");
        if (analysis.RequiresTunnelXUsername)
            return LocalizationService.Instance.T("رمز عبور (در صورت نیاز سرور)");
        return LocalizationService.Instance.T("رمز عبور (اختیاری)");
    }

    public static string GetSecretFieldLabel(string? config)
    {
        var analysis = Analyze(config);
        if (analysis.RequiresPrivateKeyPassphrase)
            return LocalizationService.Instance.T("Secret (رمز کلید) *");
        return LocalizationService.Instance.T("Secret (رمز کلید)");
    }

    public static bool ConfigRequiresAuthUserPass(string? config) =>
        Analyze(config).RequiresTunnelXUsername;

    public static bool ConfigLikelyNeedsPrivateKeyPassphrase(string? config) =>
        !string.IsNullOrWhiteSpace(config) &&
        (config.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
         config.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase) ||
         (config.Contains("askpass", StringComparison.OrdinalIgnoreCase) &&
          !config.Contains("<auth-user-pass>", StringComparison.OrdinalIgnoreCase)));

    private static int CountRemotes(string config)
    {
        var count = 0;
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("remote ", StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static bool HasInlineAuthUserPassBlock(string config) =>
        config.Contains("<auth-user-pass>", StringComparison.OrdinalIgnoreCase);

    private static bool HasAuthUserPassDirective(string config)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("auth-user-pass", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool HasInlineClientCertificate(string config) =>
        config.Contains("<cert>", StringComparison.OrdinalIgnoreCase) ||
        config.Contains("<key>", StringComparison.OrdinalIgnoreCase) ||
        config.Contains("cert ", StringComparison.OrdinalIgnoreCase) ||
        config.Contains("key ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether TunnelX should write auth-user-pass from profile fields (not inline in .ovpn).</summary>
    public static bool ShouldInjectAuthUserPass(string? config, string? username) =>
        !string.IsNullOrWhiteSpace(username) &&
        !Analyze(config).HasInlineAuthCredentials;

    /// <summary>Whether TunnelX should write askpass from profile Secret field.</summary>
    public static bool ShouldInjectAskpass(string? config, string? privateKeyPassword) =>
        !string.IsNullOrWhiteSpace(privateKeyPassword) &&
        Analyze(config).RequiresPrivateKeyPassphrase;

    /// <summary>
    /// True when the profile uses UDP transport (proto udp, udp-client, udp4, …).
    /// Connectivity checks should not TCP-probe the server port on these tunnels.
    /// </summary>
    public static bool ConfigUsesUdpTransport(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return false;

        var sawUdp = false;
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;
            if (!trimmed.StartsWith("proto ", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var proto = parts[1];
            if (proto.Contains("tcp", StringComparison.OrdinalIgnoreCase))
                return false;
            if (proto.Contains("udp", StringComparison.OrdinalIgnoreCase))
                sawUdp = true;
        }

        return sawUdp;
    }

    /// <summary>
    /// OpenVPN 2.6+ ignores legacy cipher= unless data-ciphers is set. Returns cipher names to add, or empty.
    /// </summary>
    public static IReadOnlyList<string> GetDataCipherCompatLines(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return Array.Empty<string>();

        if (HasDirective(config, "data-ciphers"))
            return Array.Empty<string>();

        var cipher = GetDirectiveValue(config, "cipher");
        if (string.IsNullOrWhiteSpace(cipher))
            return Array.Empty<string>();

        return new[] { $"data-ciphers {cipher}", $"data-ciphers-fallback {cipher}" };
    }

    private static bool HasDirective(string config, string name)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;
            if (trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == name.Length || char.IsWhiteSpace(trimmed[name.Length])))
                return true;
        }
        return false;
    }

    private static string? GetDirectiveValue(string config, string name)
    {
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;
            if (!trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }
        return null;
    }
}
