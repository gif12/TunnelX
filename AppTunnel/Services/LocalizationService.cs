using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppTunnel.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string AutoLanguage = "auto";
    public const string PersianLanguage = "fa-IR";
    public const string EnglishLanguage = "en-US";

    public static LocalizationService Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _translations = new(StringComparer.Ordinal)
    {
        [EnglishLanguage] = EnglishTranslations()
    };

    private string _languageSetting = AutoLanguage;
    private string _effectiveLanguage = PersianLanguage;

    private LocalizationService()
    {
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnFrameworkElementLoaded),
            handledEventsToo: true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string LanguageSetting => _languageSetting;
    public string EffectiveLanguage => _effectiveLanguage;
    public bool IsRightToLeft => _effectiveLanguage.StartsWith("fa", StringComparison.OrdinalIgnoreCase);
    public System.Windows.FlowDirection FlowDirection => IsRightToLeft
        ? System.Windows.FlowDirection.RightToLeft
        : System.Windows.FlowDirection.LeftToRight;
    public System.Windows.TextAlignment TextAlignment => IsRightToLeft
        ? System.Windows.TextAlignment.Right
        : System.Windows.TextAlignment.Left;
    public System.Windows.HorizontalAlignment StartHorizontalAlignment => IsRightToLeft
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;
    public System.Windows.HorizontalAlignment EndHorizontalAlignment => IsRightToLeft
        ? System.Windows.HorizontalAlignment.Left
        : System.Windows.HorizontalAlignment.Right;
    public string ToggleLanguageText => IsRightToLeft ? "English" : "فارسی";

    public void Initialize(string? savedLanguage)
    {
        SetLanguageInternal(string.IsNullOrWhiteSpace(savedLanguage) ? AutoLanguage : savedLanguage!, raiseChanged: false);
    }

    public void ToggleLanguage()
    {
        SetLanguage(IsRightToLeft ? EnglishLanguage : PersianLanguage);
    }

    public void SetLanguage(string language)
    {
        SetLanguageInternal(language, raiseChanged: true);
    }

    public string T(string source)
    {
        if (string.IsNullOrEmpty(source) || IsRightToLeft)
            return source;

        return _translations.TryGetValue(_effectiveLanguage, out var table) &&
               table.TryGetValue(source, out var translated)
            ? translated
            : source;
    }

    public string Format(string sourceFormat, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, T(sourceFormat), args);

    public void ApplyToOpenWindows()
    {
        foreach (Window window in System.Windows.Application.Current.Windows)
            ApplyTo(window);
    }

    public void ApplyTo(DependencyObject root)
    {
        ApplyTo(root, new HashSet<DependencyObject>());
    }

    private void ApplyTo(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root))
            return;

        ApplyFlowDirection(root);
        ApplyText(root);

        try
        {
            var visualCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < visualCount; i++)
                ApplyTo(VisualTreeHelper.GetChild(root, i), visited);
        }
        catch (InvalidOperationException)
        {
            // Some logical children, such as Run, are not visual children.
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            ApplyTo(child, visited);
    }

    private void OnFrameworkElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element)
            return;

        ApplyTo(element);
    }

    private void SetLanguageInternal(string language, bool raiseChanged)
    {
        _languageSetting = NormalizeLanguageSetting(language);
        _effectiveLanguage = ResolveEffectiveLanguage(_languageSetting);

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(_effectiveLanguage);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(_effectiveLanguage);

        OnPropertyChanged(nameof(LanguageSetting));
        OnPropertyChanged(nameof(EffectiveLanguage));
        OnPropertyChanged(nameof(IsRightToLeft));
        OnPropertyChanged(nameof(FlowDirection));
        OnPropertyChanged(nameof(TextAlignment));
        OnPropertyChanged(nameof(StartHorizontalAlignment));
        OnPropertyChanged(nameof(EndHorizontalAlignment));
        OnPropertyChanged(nameof(ToggleLanguageText));

        if (!raiseChanged) return;

        LanguageChanged?.Invoke(this, EventArgs.Empty);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(ApplyToOpenWindows, DispatcherPriority.Loaded);
        dispatcher?.BeginInvoke(ApplyToOpenWindows, DispatcherPriority.ContextIdle);
    }

    private static string NormalizeLanguageSetting(string language)
    {
        if (string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase))
            return EnglishLanguage;
        if (string.Equals(language, PersianLanguage, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(language, "fa", StringComparison.OrdinalIgnoreCase))
            return PersianLanguage;
        return AutoLanguage;
    }

    private static string ResolveEffectiveLanguage(string setting)
    {
        if (setting != AutoLanguage)
            return setting;

        var ui = CultureInfo.CurrentUICulture;
        return ui.TwoLetterISOLanguageName.Equals("fa", StringComparison.OrdinalIgnoreCase)
            ? PersianLanguage
            : EnglishLanguage;
    }

    private static bool HasBinding(DependencyObject element, DependencyProperty property)
        => BindingOperations.GetBindingExpressionBase(element, property) != null;

    private static readonly DependencyProperty OriginalTextProperty =
        DependencyProperty.RegisterAttached("OriginalText", typeof(string), typeof(LocalizationService));

    private static readonly DependencyProperty OriginalContentProperty =
        DependencyProperty.RegisterAttached("OriginalContent", typeof(string), typeof(LocalizationService));

    private static readonly DependencyProperty OriginalHeaderProperty =
        DependencyProperty.RegisterAttached("OriginalHeader", typeof(string), typeof(LocalizationService));

    private static readonly DependencyProperty OriginalToolTipProperty =
        DependencyProperty.RegisterAttached("OriginalToolTip", typeof(string), typeof(LocalizationService));

    private static readonly DependencyProperty OriginalTagProperty =
        DependencyProperty.RegisterAttached("OriginalTag", typeof(string), typeof(LocalizationService));

    private void ApplyFlowDirection(DependencyObject element)
    {
        if (element is not FrameworkElement fe)
            return;

        if (HasBinding(fe, FrameworkElement.FlowDirectionProperty))
            return;

        var local = fe.ReadLocalValue(FrameworkElement.FlowDirectionProperty);
        if (local == DependencyProperty.UnsetValue ||
            local is System.Windows.FlowDirection.LeftToRight)
            return;

        // Explicit RTL follows the selected language. Explicit LTR is reserved
        // for technical fields: IP, path, port, config and logs.
        fe.FlowDirection = FlowDirection;
    }

    private void ApplyText(DependencyObject element)
    {
        switch (element)
        {
            case Window window when !HasBinding(window, Window.TitleProperty):
                window.Title = TranslateProperty(window, Window.TitleProperty, OriginalTextProperty, window.Title);
                break;

            case TextBlock textBlock when !HasBinding(textBlock, TextBlock.TextProperty) && !textBlock.Inlines.Any():
                textBlock.Text = TranslateProperty(textBlock, TextBlock.TextProperty, OriginalTextProperty, textBlock.Text);
                if (textBlock.ReadLocalValue(TextBlock.TextAlignmentProperty) == DependencyProperty.UnsetValue)
                    textBlock.TextAlignment = textBlock.FlowDirection == System.Windows.FlowDirection.LeftToRight
                        ? System.Windows.TextAlignment.Left
                        : TextAlignment;
                break;

            case Run run:
                run.Text = TranslateProperty(run, Run.TextProperty, OriginalTextProperty, run.Text);
                break;

            case HeaderedContentControl headered when headered.Header is string header && !HasBinding(headered, HeaderedContentControl.HeaderProperty):
                headered.Header = TranslateProperty(headered, HeaderedContentControl.HeaderProperty, OriginalHeaderProperty, header);
                break;

            case ContentControl contentControl when contentControl.Content is string content && !HasBinding(contentControl, ContentControl.ContentProperty):
                contentControl.Content = TranslateProperty(contentControl, ContentControl.ContentProperty, OriginalContentProperty, content);
                break;

            case System.Windows.Controls.TextBox textBox when textBox.Tag is string tag && !HasBinding(textBox, FrameworkElement.TagProperty):
                textBox.Tag = TranslateProperty(textBox, FrameworkElement.TagProperty, OriginalTagProperty, tag);
                ApplyTextBoxAlignment(textBox);
                break;

            case System.Windows.Controls.TextBox textBox:
                ApplyTextBoxAlignment(textBox);
                break;
        }

        if (element is FrameworkElement fe
            && fe.ToolTip is string toolTip
            && !string.IsNullOrEmpty(toolTip)
            && !HasBinding(fe, FrameworkElement.ToolTipProperty))
            fe.ToolTip = TranslateProperty(fe, FrameworkElement.ToolTipProperty, OriginalToolTipProperty, toolTip);
    }

    private void ApplyTextBoxAlignment(System.Windows.Controls.TextBox textBox)
    {
        if (textBox.ReadLocalValue(System.Windows.Controls.TextBox.TextAlignmentProperty) != DependencyProperty.UnsetValue)
            return;

        textBox.TextAlignment = textBox.FlowDirection == System.Windows.FlowDirection.LeftToRight
            ? TextAlignment.Left
            : TextAlignment;
    }

    private string TranslateProperty(DependencyObject owner, DependencyProperty property, DependencyProperty originalProperty, string current)
    {
        var original = owner.GetValue(originalProperty) as string;
        var resolvedCurrent = ResolveSourceText(current);
        if (original == null || !string.Equals(original, ResolveSourceText(original), StringComparison.Ordinal))
        {
            original = resolvedCurrent;
            owner.SetValue(originalProperty, original);
        }

        return T(original);
    }

    private string ResolveSourceText(string current)
    {
        if (string.IsNullOrEmpty(current))
            return current;

        foreach (var table in _translations.Values)
        {
            if (table.ContainsKey(current))
                return current;

            foreach (var pair in table)
            {
                if (string.Equals(pair.Value, current, StringComparison.Ordinal))
                    return pair.Key;
            }
        }

        return current;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static Dictionary<string, string> EnglishTranslations() => new(StringComparer.Ordinal)
    {
        ["TunnelX — Split Traffic Per App"] = "TunnelX - Split Traffic Per App",
        ["Per-app split tunneling"] = "Per-app split tunneling",
        ["تانلکس"] = "TunnelX",
        ["تفکیک ترافیک برنامه ها"] = "Per-app traffic splitting",
        ["جزئیات"] = "Details",
        ["کوچک کردن به System Tray"] = "Minimize to system tray",
        ["خروج از برنامه"] = "Exit app",
        ["وضعیت اتصال VPN و کنترل اتصال"] = "VPN connection status and controls",
        ["اتصال VPN"] = "VPN Connection",
        ["انتخاب برنامه‌هایی که باید از تونل عبور کنند"] = "Select apps that should use the tunnel",
        ["برنامه‌ها"] = "Apps",
        ["تنظیمات عمومی تونل و DNS"] = "General tunnel and DNS settings",
        ["تنظیمات"] = "Settings",
        ["قوانین Include و Exclude مسیرها"] = "Include and exclude routing rules",
        ["قوانین مسیر"] = "Routing Rules",
        ["مقصدهای مستقیم و مقصدهای اجباری تونل را اینجا مدیریت کنید."] = "Manage direct and forced tunnel destinations here.",
        ["🚫 مستقیم بماند"] = "🚫 Stay Direct",
        ["این مقصدها از تونل عبور نمی‌کنند."] = "These destinations do not use the tunnel.",
        ["نمونه کاربرد"] = "Example",
        ["سایت‌های داخلی یا سرورهای بازی را مستقیم نگه دارید."] = "Keep internal sites or game servers direct.",
        ["دامنه یا آی‌پی (مثلاً google.com یا 1.2.3.4)"] = "Domain or IP, e.g. google.com or 1.2.3.4",
        ["افزودن"] = "Add",
        ["آدرس‌های مستقیم"] = "Direct Addresses",
        ["حذف"] = "Delete",
        ["✅ از تونل عبور کند"] = "✅ Use Tunnel",
        ["این مقصدها همیشه از تونل عبور می‌کنند."] = "These destinations always use the tunnel.",
        ["دامنه یا آی‌پی (مثلاً example.com یا 1.2.3.4)"] = "Domain or IP, e.g. example.com or 1.2.3.4",
        ["آدرس‌های اجباری"] = "Forced Addresses",
        ["نمایش ترافیک، تاریخچه و آمار اتصال"] = "Traffic, history, and connection stats",
        ["ترافیک/تاریخچه"] = "Traffic/History",
        ["ترافیک و تاریخچه"] = "Traffic and History",
        ["⏱ مدت"] = "⏱ Duration",
        ["🌐 IP"] = "🌐 IP",
        ["📊 تونل"] = "📊 Tunnel",
        ["📡 خارج تونل"] = "📡 Direct",
        ["📜 تاریخچه اتصالات"] = "📜 Connection History",
        ["مصرف تونل به تفکیک برنامه"] = "Tunnel Usage by App",
        ["اپ‌های تونل: "] = "Tunnel apps: ",
        ["سایر تونل: "] = "Other tunnel: ",
        ["هنوز برنامه‌ای اضافه نشده. از تب «برنامه‌ها» اضافه کنید."] = "No apps added yet. Add apps from the Apps tab.",
        ["توسط Maxifan"] = "By Maxifan",
        ["بروزرسانی"] = "Update",
        ["دانلود نسخه جدید از صفحه Releases در GitHub"] = "Download the new version from GitHub Releases",
        ["راهنما"] = "Help",
        ["راهنما و عیب‌یابی"] = "Help and troubleshooting",
        ["باز کردن صفحه GitHub پروژه TunnelX"] = "Open the TunnelX GitHub project",
        ["🔍 جزئیات عملکرد"] = "🔍 Runtime Details",
        ["همه"] = "All",
        ["خطا"] = "Error",
        ["هشدار"] = "Warning",
        ["پاک کردن"] = "Clear",
        ["کپی خطا"] = "Copy error",
        ["کپی همه"] = "Copy all",
        ["پاک کردن همه لاگ‌ها"] = "Clear all logs",
        ["کپی آخرین خطا یا هشدار"] = "Copy latest error or warning",
        ["کپی کردن همه لاگ‌ها"] = "Copy all logs",
        ["کپی کردن"] = "Copy",
        ["در حال بارگذاری..."] = "Loading...",

        ["در حال اتصال OpenVPN"] = "Connecting OpenVPN",
        ["تا قبل از بالا آمدن آداپتر، مسیرهای سیستم تغییر داده نمی‌شود. اگر اتصال طولانی شد، فایل .ovpn، نام کاربری/رمز یا نصب OpenVPN Community را بررسی کنید."] = "System routes are not changed until the adapter is ready. If connection takes too long, check the .ovpn file, credentials, or OpenVPN Community installation.",
        ["کانفیگ‌ها و پروفایل‌ها"] = "Configs and Profiles",
        ["یک کانفیگ را انتخاب کنید، ویرایش کنید یا کانفیگ جدید بسازید."] = "Select, edit, or create a connection profile.",
        ["افزودن کانفیگ جدید"] = "Add New Config",
        ["فعال"] = "Use",
        ["استفاده از این کانفیگ برای اتصال"] = "Use this config for connection",
        ["ویرایش"] = "Edit",
        ["کپی"] = "Copy",
        ["پروفایل فعال: "] = "Active profile: ",
        ["شروع اتصال با پروفایل انتخاب‌شده"] = "Start connection with the selected profile",
        ["🌐 تنظیمات سرور"] = "🌐 Server Settings",
        ["نوع اتصال"] = "Connection Type",
        ["آدرس سرور"] = "Server Address",
        ["نام کاربری"] = "Username",
        ["رمز عبور"] = "Password",
        ["کانفیگ V2Ray"] = "V2Ray Config",
        ["پیست"] = "Paste",
        ["خواندن کانفیگ از کلیپ‌بورد"] = "Read config from clipboard",
        ["فایل OpenVPN (.ovpn)"] = "OpenVPN File (.ovpn)",
        ["انتخاب فایل"] = "Choose File",
        ["حذف فایل"] = "Remove File",
        ["دانلود OpenVPN"] = "Download OpenVPN",
        ["دانلود WireGuard"] = "Download WireGuard",
        ["نوع پراکسی"] = "Proxy Type",
        ["پورت"] = "Port",
        ["آدرس IP یا دامنه سرور"] = "Server IP or Domain",

        ["✅ برنامه‌های داخل تونل"] = "✅ Tunneled Apps",
        ["برنامه‌های فعال‌شده از مسیر تونل عبور می‌کنند"] = "Enabled apps use the tunnel route",
        ["افزودن دستی"] = "Add Manually",
        ["افزودن دستی فایل exe"] = "Manually add an exe file",
        ["فیلتر برنامه‌های تونل..."] = "Filter tunneled apps...",
        ["🔍 برنامه‌های نصب‌شده"] = "🔍 Installed Apps",
        ["روی برنامه کلیک کنید تا به لیست تونل اضافه شود"] = "Click an app to add it to the tunnel list",
        ["بارگذاری مجدد لیست برنامه‌ها"] = "Reload the app list",
        ["جستجوی برنامه..."] = "Search apps...",

        ["🧦 پروکسی محلی"] = "🧦 Local Proxy",
        ["پورت پروکسی محلی (SOCKS5/HTTP)"] = "Local proxy port (SOCKS5/HTTP)",
        ["پورت داخلی 127.0.0.1 برای پروکسی SOCKS5 و HTTP"] = "Internal 127.0.0.1 port for SOCKS5 and HTTP proxy",
        ["پورت‌های زیر 1024 و چند پورت رایج مثل 2080، 3000، 3389، 8080 و 9090 مجاز نیستند تا با سرویس‌های سیستم یا ابزارهای توسعه تداخل نداشته باشند."] = "Ports below 1024 and common ports like 2080, 3000, 3389, 8080, and 9090 are blocked to avoid conflicts with system services or developer tools.",
        ["🚀 بهینه‌سازی تونل"] = "🚀 Tunnel Optimization",
        ["MTU خودکار"] = "Automatic MTU",
        ["در زمان اتصال، MTU مناسب بر اساس شبکه فعلی انتخاب می‌شود."] = "At connection time, the best MTU is selected based on the current network.",
        ["برای Resolveها از cache کوتاه‌مدت استفاده می‌شود و DNS redirect مسیر پایدارتر می‌گیرد."] = "Uses a short-lived cache for resolves and keeps DNS redirects more stable.",
        ["Game Mode فعال است: Route نگهداری طولانی‌تر، DNS سریع‌تر و DSCP برای بسته‌های بازی اعمال می‌شود."] = "Game Mode is enabled: longer route retention, faster DNS, and DSCP for game packets.",
        ["Game Mode غیرفعال است: حالت متعادل برای مصرف عمومی."] = "Game Mode is disabled: balanced mode for general use.",
        ["🖥️ اجرای خودکار و اتصال خودکار"] = "🖥️ Startup and Auto-Connect",
        ["اجرای TunnelX همراه با ویندوز"] = "Start TunnelX with Windows",
        ["در ورود به ویندوز، TunnelX از مسیر فعلی فایل اجرا می‌شود. اگر فایل را جابه‌جا کردید، گزینه را خاموش و روشن کنید."] = "When you sign in to Windows, TunnelX starts from the current executable path. If you move the file, turn this option off and on again.",
        ["اتصال خودکار هنگام اجرای TunnelX"] = "Auto-connect when TunnelX starts",
        ["پس از باز شدن برنامه، به آخرین پروفایلی که با موفقیت وصل شده بود متصل می‌شود."] = "After the app opens, it connects to the last profile that connected successfully.",

        ["راهنمای TunnelX"] = "TunnelX Help",
        ["شروع سریع، پروفایل‌ها، بخش‌های اپ و عیب‌یابی در یک صفحه ساده."] = "Quick start, profiles, app areas, and troubleshooting in one simple page.",
        ["پروژه و بروزرسانی"] = "Project and Updates",
        ["TunnelX اسپلیت‌تانلینگ برنامه‌ای را برای ویندوز فراهم می‌کند: فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند و بقیه ترافیک مستقیم می‌ماند."] = "TunnelX brings per-app split tunneling to Windows: only selected apps and destinations use the tunnel; the rest of your traffic stays direct.",
        ["با حمایت شما توسعه TunnelX ادامه می‌یابد؛ رفع باگ، پشتیبانی از پروتکل‌های جدید و قابلیت‌های بیشتر در راه است."] = "Your support keeps TunnelX moving forward — bug fixes, new protocol support, and more features on the way.",
        ["GitHub پروژه TunnelX"] = "TunnelX on GitHub",
        ["کپی آدرس کیف پول"] = "Copy wallet address",
        ["GitHub"] = "GitHub",
        ["صفحه انتشار"] = "Release Page",
        ["شروع سریع"] = "Quick Start",
        ["۱. پروفایل"] = "1. Profile",
        ["کانفیگ را بسازید و نوع اتصال را انتخاب کنید."] = "Create a config and select its connection type.",
        ["۲. برنامه‌ها"] = "2. Apps",
        ["برنامه‌های داخل تونل را انتخاب یا دستی اضافه کنید."] = "Select tunneled apps or add them manually.",
        ["۳. قوانین"] = "3. Rules",
        ["مقصدهای مستقیم یا اجباری را مشخص کنید."] = "Set direct or forced destinations.",
        ["۴. اتصال"] = "4. Connect",
        ["وصل شوید و سلامت، IP و مصرف را بررسی کنید."] = "Connect and check health, IP, and usage.",
        ["نوع پروفایل"] = "Profile Type",
        ["فقط فیلدهای مربوط به نوع انتخاب‌شده را پر کنید. هر پروفایل برنامه‌ها و قوانین مسیر خودش را نگه می‌دارد."] = "Only fill the fields for the selected type. Each profile keeps its own apps and routing rules.",
        ["بخش‌های اپ"] = "App Areas",
        ["پروفایل فعال، تست سرور، اتصال/قطع اتصال، IP خروجی، پینگ، مصرف و راهنمای پراکسی دستی اینجاست."] = "Active profile, server test, connect/disconnect, exit IP, ping, usage, and manual proxy guidance are here.",
        ["نکات مهم"] = "Essentials",
        ["حالت عادی: فقط برنامه‌های انتخاب‌شده و مقصدهای لزومی وارد تونل می‌شوند."] = "Normal mode: only selected apps and included destinations use the tunnel.",
        ["Full Route: کل سیستم وارد تونل می‌شود؛ استثناها می‌توانند مستقیم بمانند."] = "Full Route: the whole system uses the tunnel; exclusions can stay direct.",
        ["پراکسی داخلی"] = "Local Proxy",
        ["برای ابزارهایی که آدرس محلی می‌خواهند:"] = "For tools that need a local address:",
        ["سلامت: Leak باید صفر باشد. DNS، IPv6 و Route را بعد از اتصال بررسی کنید."] = "Health: Leak should be zero. Check DNS, IPv6, and Route after connecting.",
        ["عیب‌یابی سریع"] = "Quick Troubleshooting",
        ["حمایت و تماس"] = "Support and Contact",
        ["حمایت با پی‌پل"] = "Donate with PayPal",
        ["حمایت"] = "Donate",
        ["محل تبلیغات شما"] = "Your ad space",
        ["درخواست تبلیغ"] = "Request advertising",
        ["تبلیغ شما می‌تواند در معرض دید کاربران TunnelX با بیش از {0} نصب از GitHub باشد."] = "Your ad can reach TunnelX users backed by more than {0} GitHub installs.",
        ["📢 برای دریافت اخبار آپدیت و اطلاع‌رسانی، در کانال تلگرام TunnelX عضو شوید"] = "📢 Join the TunnelX Telegram channel for updates and release alerts",
        ["عضویت در کانال تلگرام"] = "Join Telegram channel",
        ["کانال تلگرام TunnelX — {0}"] = "TunnelX Telegram channel — {0}",
        ["📢 تلگرام"] = "📢 Telegram",
        ["کپی اطلاعات حمایت"] = "Copy donation info",
        ["اگر TunnelX برایتان مفید بوده، می‌توانید با PayPal یا کریپتو از توسعه آن حمایت کنید."] = "If TunnelX has been useful, you can support its development with PayPal or crypto.",
        ["حمایت با PayPal"] = "Donate with PayPal",
        ["حمایت داوطلبانه؛ خرید یا پرداخت تجاری نیست."] = "Voluntary support only; not a purchase or commercial payment.",
        ["پرداخت با کریپتو"] = "Pay with crypto",
        ["ترون / USDT روی TRC20"] = "Tron / USDT on TRC20",
        ["بیت‌کوین"] = "Bitcoin",
        ["اتریوم / USDT روی ERC20"] = "Ethereum / USDT on ERC20",
        ["کپی"] = "Copy",

        ["ویرایش پروفایل"] = "Edit Profile",
        ["تنظیمات این کانفیگ بعد از ذخیره در لیست پروفایل‌ها نمایش داده می‌شود."] = "This config appears in the profile list after saving.",
        ["اطلاعات پروفایل"] = "Profile Info",
        ["نام پروفایل"] = "Profile Name",
        ["نام پروفایل *"] = "Profile Name *",
        ["مثلاً کار، تلگرام، گیمینگ..."] = "e.g. Work, Telegram, Gaming...",
        ["تنظیمات اتصال"] = "Connection Settings",
        ["کانفیگ V2Ray / Xray"] = "V2Ray / Xray Config",
        ["پیست از کلیپ‌بورد"] = "Paste from Clipboard",
        ["فایل .ovpn و اطلاعات احراز هویت OpenVPN را وارد کنید. اگر سرور رمز نمی‌خواهد، فیلد رمز را خالی بگذارید."] = "Enter the .ovpn file and OpenVPN credentials. Leave password empty if the server does not require one.",
        ["برای SOCKS5 یا HTTP Proxy، اطلاعات سرور را جداگانه وارد کنید."] = "For SOCKS5 or HTTP Proxy, enter the server details separately.",
        ["لغو"] = "Cancel",
        ["ذخیره"] = "Save",

        ["تاریخچه اتصالات"] = "Connection History",
        ["سوابق اتصال و مصرف تونل"] = "Connection records and tunnel usage",
        ["مجموع مصرف تونل: "] = "Total tunnel usage: ",
        ["هنوز اتصالی ثبت نشده است."] = "No connection has been recorded yet.",

        ["آماده اتصال"] = "Ready to connect",
        ["نیاز به تکمیل"] = "Needs setup",
        ["نامشخص"] = "Unknown",
        ["آدرس سرور وارد نشده"] = "Server address is missing",
        ["کانفیگ وارد نشده"] = "Config is missing",
        ["کانفیگ آماده"] = "Config ready",
        ["فایل .ovpn انتخاب نشده"] = ".ovpn file not selected",
        ["آدرس پراکسی وارد نشده"] = "Proxy address is missing"
        ,
        ["کانفیگ WireGuard وارد نشده"] = "WireGuard config is missing",
        ["کانفیگ WireGuard آماده"] = "WireGuard config ready",
        ["کانفیگ WireGuard آماده نمایش نیست"] = "WireGuard config is not ready to display",
        ["فایل .conf وایرگارد را وارد کنید. در این نسخه فقط کانفیگ‌های تک-peer پشتیبانی می‌شوند و مسیرها توسط TunnelX مدیریت می‌شوند."] = "Enter the WireGuard .conf file. This version supports single-peer configs only and TunnelX manages routes.",
        ["فایل WireGuard (.conf)"] = "WireGuard File (.conf)",
        ["کانفیگ WireGuard"] = "WireGuard Config",
        ["انتخاب فایل WireGuard"] = "Choose WireGuard File",
        ["WireGuard config (*.conf)|*.conf|All files (*.*)|*.*"] = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
        ["خواندن فایل WireGuard ناموفق بود: {0}"] = "Failed to read WireGuard file: {0}",
        ["کانفیگ WireGuard را وارد کنید"] = "Enter the WireGuard config",
        ["بخش WireGuard ناشناخته یا پشتیبانی‌نشده است: {0}"] = "Unknown or unsupported WireGuard section: {0}",
        ["خط WireGuard نامعتبر است: {0}"] = "Invalid WireGuard line: {0}",
        ["کانفیگ WireGuard باید یک بخش [Peer] داشته باشد"] = "WireGuard config must contain one [Peer] section",
        ["در این نسخه فقط کانفیگ WireGuard تک-peer پشتیبانی می‌شود"] = "This version supports single-peer WireGuard configs only",
        ["کلید خصوصی WireGuard وارد نشده است"] = "WireGuard private key is missing",
        ["آدرس Interface در کانفیگ WireGuard وارد نشده است"] = "WireGuard interface address is missing",
        ["کلید عمومی Peer در کانفیگ WireGuard وارد نشده است"] = "WireGuard peer public key is missing",
        ["Endpoint در کانفیگ WireGuard وارد نشده است"] = "WireGuard endpoint is missing",
        ["AllowedIPs در کانفیگ WireGuard وارد نشده است"] = "WireGuard AllowedIPs is missing",
        ["Endpoint WireGuard نامعتبر است: {0}"] = "Invalid WireGuard endpoint: {0}",
        ["PersistentKeepalive باید عدد مثبت باشد"] = "PersistentKeepalive must be a positive number",
        ["MTU WireGuard باید بین 576 تا 9000 باشد"] = "WireGuard MTU must be between 576 and 9000",
        ["در حال آماده‌سازی WireGuard..."] = "Preparing WireGuard...",
        ["در حال اتصال WireGuard"] = "Connecting WireGuard",
        ["TunnelX در حال آماده‌سازی سرویس WireGuard و آداپتر ویندوز است. می‌توانید با دکمه لغو اتصال تلاش فعلی را متوقف کنید."] = "TunnelX is preparing the WireGuard service and Windows adapter. You can stop the current attempt with the cancel button.",
        ["WireGuard for Windows نصب نیست. برای استفاده از WireGuard Split Tunnel، ابتدا WireGuard رسمی ویندوز را نصب کنید."] = "WireGuard for Windows is not installed. Install the official WireGuard for Windows first to use WireGuard Split Tunnel.",
        ["در حال اجرای سرویس WireGuard..."] = "Starting WireGuard service...",
        ["WireGuard service اجرا نشد: {0}"] = "WireGuard service did not start: {0}",
        ["آداپتر WireGuard بالا نیامد (timeout)"] = "WireGuard adapter did not come up (timeout)",
        ["در حال انتظار برای interface TunnelX-WireGuard..."] = "Waiting for TunnelX-WireGuard interface...",
        ["interface TunnelX-WireGuard ظاهر نشد (timeout 10s)"] = "TunnelX-WireGuard interface did not appear (timeout 10s)",
        ["WireGuard متصل شد (Split Tunnel)"] = "WireGuard connected (Split Tunnel)",
        ["در حال قطع اتصال WireGuard..."] = "Disconnecting WireGuard...",
        ["Endpoint WireGuard: {0}:{1}"] = "WireGuard endpoint: {0}:{1}",
        ["Endpoint معتبر است: {0}:{1} (UDP)"] = "Endpoint is valid: {0}:{1} (UDP)",
        ["Endpoint فعال: {0}:{1} (UDP)"] = "Active endpoint: {0}:{1} (UDP)",
        ["Endpoint WireGuard در وضعیت اتصال پیدا نشد"] = "WireGuard endpoint was not found in connection status",
        ["TunnelX کانفیگ WireGuard را با WireGuard رسمی ویندوز به‌صورت adapter واقعی اجرا می‌کند و سپس اسپلیت‌تانلینگ برنامه‌ها را مثل OpenVPN/L2TP مدیریت می‌کند. نسخه فعلی فقط کانفیگ تک-peer را پشتیبانی می‌کند."] = "TunnelX runs WireGuard configs through the official WireGuard for Windows adapter, then manages app split-tunneling like OpenVPN/L2TP. The current version supports single-peer configs only.",
        ["می‌توانید فایل .conf را همین حالا اضافه کنید، اما اتصال WireGuard فقط بعد از نصب WireGuard رسمی ویندوز انجام می‌شود. اگر نصب نیست، دکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید."] = "You can add the .conf file now, but WireGuard connections work only after the official WireGuard for Windows is installed. If it is not installed, click Download, complete the installation, then return to TunnelX and connect.",
        ["کانفیگ WireGuard اضافه شد، اما WireGuard رسمی ویندوز نصب نیست. از دکمه دانلود WireGuard در همین تب استفاده کنید و بعد از نصب دوباره اتصال را بزنید."] = "WireGuard config was added, but the official WireGuard for Windows is not installed. Use the Download WireGuard button in this tab, then try connecting again after installation.",
        ["کانفیگ WireGuard اضافه شد، اما برای اتصال باید WireGuard رسمی ویندوز را نصب کنید.\nاز دکمه «دانلود WireGuard» در همین تب استفاده کنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید."] = "WireGuard config was added, but you must install the official WireGuard for Windows before connecting.\nUse the \"Download WireGuard\" button in this tab, complete the installation, then return to TunnelX and connect.",
        ["کانفیگ WireGuard اضافه شد، اما برای اتصال باید WireGuard رسمی ویندوز را نصب کنید.\nدکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید."] = "WireGuard config was added, but you must install the official WireGuard for Windows before connecting.\nClick Download, complete the installation, then return to TunnelX and connect.",
        ["برای اتصال WireGuard باید WireGuard رسمی ویندوز نصب باشد.\n\nلینک رسمی نصب:\nhttps://www.wireguard.com/install/\n\nبعد از نصب، به TunnelX برگردید و دوباره اتصال را بزنید."] = "To connect with WireGuard, the official WireGuard for Windows must be installed.\n\nOfficial install link:\nhttps://www.wireguard.com/install/\n\nAfter installing it, return to TunnelX and try connecting again.",
        ["راهنمای نصب WireGuard"] = "WireGuard installation guide",
        ["می‌توانید فایل .ovpn را همین حالا اضافه کنید، اما اتصال OpenVPN فقط بعد از نصب OpenVPN Community انجام می‌شود. اگر نصب نیست، دکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید."] = "You can add the .ovpn file now, but OpenVPN connections work only after OpenVPN Community is installed. If it is not installed, click Download, complete the installation, then return to TunnelX and connect.",
        ["کانفیگ OpenVPN اضافه شد، اما OpenVPN Community نصب نیست. از دکمه دانلود OpenVPN در همین تب استفاده کنید و بعد از نصب دوباره اتصال را بزنید."] = "OpenVPN config was added, but OpenVPN Community is not installed. Use the Download OpenVPN button in this tab, then try connecting again after installation.",
        ["کانفیگ OpenVPN اضافه شد، اما برای اتصال باید OpenVPN Community را نصب کنید.\nدکمه دانلود را بزنید، نصب را کامل کنید، سپس به TunnelX برگردید و اتصال را بزنید."] = "OpenVPN config was added, but you must install OpenVPN Community before connecting.\nClick Download, complete the installation, then return to TunnelX and connect.",
        ["راهنمای نصب OpenVPN"] = "OpenVPN installation guide",
        ["OpenVPN نصب نیست؛ راهنمای نصب نمایش داده شد"] = "OpenVPN is not installed; installation guide was shown",
        ["بعداً نصب می‌کنم"] = "Install later",
        ["وضعیت"] = "Status",
        ["نمایش TunnelX"] = "Show TunnelX",
        ["بررسی بروزرسانی"] = "Check for Updates",
        ["اتصال VPN فعال است. با خروج، اتصال قطع خواهد شد.\nآیا مطمئن هستید؟"] = "A VPN connection is active. Exiting will disconnect it.\nAre you sure?",
        ["اتصال در جریان است. با خروج، تلاش اتصال قطع می‌شود.\nآیا مطمئن هستید؟"] = "A connection attempt is in progress. Exiting will cancel it.\nAre you sure?",
        ["TunnelX — خروج"] = "TunnelX - Exit",
        ["آیا می‌خواهید از TunnelX خارج شوید؟"] = "Do you want to exit TunnelX?",
        ["TunnelX"] = "TunnelX",
        ["TunnelX در پس‌زمینه فعال است"] = "TunnelX is running in the background",
        ["برای باز کردن پنجره، روی آیکن کنار ساعت دوبار کلیک کنید."] = "Double-click the tray icon to open the window.",
        ["تونل فعال شد"] = "Tunnel enabled",
        ["تونل خاموش شد"] = "Tunnel disabled",
        ["ارتباط امن متوقف شده و ترافیک دیگر از TunnelX عبور نمی‌کند."] = "The secure connection stopped and traffic no longer passes through TunnelX.",
        ["نسخه جدید آماده است"] = "New version available",
        ["از منوی System Tray یا بخش بروزرسانی، صفحه دانلود TunnelX را باز کنید."] = "Open the TunnelX download page from the system tray menu or the update section.",
        ["پروفایل «{0}» فعال است و ترافیک انتخاب‌شده از تونل عبور می‌کند."] = "Profile \"{0}\" is active and selected traffic uses the tunnel.",
        ["ترافیک انتخاب‌شده از TunnelX عبور می‌کند."] = "Selected traffic uses TunnelX.",
        ["جزئیات خطا را در پنجره برنامه یا لاگ‌ها بررسی کنید."] = "Check the app window or logs for error details.",
        ["راهنمای پس از خطای اتصال"] = "Open the Connection tab to see which step failed and the full error text. Check the profile (server address, credentials, config file), tunnel type, and prerequisites (OpenVPN Community / WireGuard). Use in-app Logs for technical details.",
        ["جزئیات: {0}"] = "Details: {0}",
        ["بارگذاری لیست برنامه‌های نصب‌شده..."] = "Loading installed apps...",
        ["تاییدیه"] = "Confirmation",
        ["اطلاعات"] = "Information",
        ["موفقیت"] = "Success",
        ["بله"] = "Yes",
        ["خیر"] = "No",
        ["متوجه شدم"] = "OK",
        ["عالی"] = "Great",
        ["{0} کپی شد"] = "{0} copied",
        ["متن"] = "Text",
        ["انتخاب فایل OpenVPN"] = "Choose OpenVPN File",
        ["OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*"] = "OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*",
        ["خواندن فایل OpenVPN ناموفق بود: {0}"] = "Failed to read OpenVPN file: {0}",
        ["خواندن کلیپ‌بورد ناموفق بود: {0}"] = "Failed to read clipboard: {0}",
        ["نام پروفایل را وارد کنید"] = "Enter a profile name",
        ["آدرس سرور L2TP را وارد کنید"] = "Enter the L2TP server address",
        ["کانفیگ V2Ray/Xray را وارد کنید"] = "Enter the V2Ray/Xray config",
        ["فایل OpenVPN (.ovpn) را انتخاب کنید"] = "Choose an OpenVPN (.ovpn) file",
        ["آدرس سرور پراکسی را وارد کنید"] = "Enter the proxy server address",
        ["پورت پراکسی باید بین 1 تا 65535 باشد"] = "Proxy port must be between 1 and 65535",
        ["ذخیره شد"] = "Saved",
        ["پورت SOCKS5 را وارد کنید"] = "Enter the SOCKS5 port",
        ["فقط عدد مجاز است"] = "Only numbers are allowed",
        ["پیش‌نیاز آماده است: نسخه Community اوپن‌وی‌پی‌ان پیدا شد: {0}"] = "Prerequisite ready: OpenVPN Community found: {0}",
        ["اخطار: نسخه Community اوپن‌وی‌پی‌ان نصب نیست. برای استفاده از اسپلیت‌تانلینگ با این نوع اتصال، ابتدا آن را از لینک رسمی نصب کنید."] = "Warning: OpenVPN Community is not installed. Install it from the official link before using split tunneling with this connection type.",
        ["پیش‌نیاز آماده است: WireGuard رسمی ویندوز پیدا شد: {0}"] = "Prerequisite ready: official WireGuard for Windows found: {0}",
        ["اخطار: WireGuard رسمی ویندوز نصب نیست. برای استفاده از اسپلیت‌تانلینگ WireGuard، ابتدا آن را از لینک رسمی نصب کنید."] = "Warning: official WireGuard for Windows is not installed. Install it from the official link before using WireGuard split tunneling.",
        ["برای بررسی نسخه جدید، دکمه بررسی بروزرسانی را بزنید."] = "Click Check for Updates to look for a new version.",
        ["در حال بررسی..."] = "Checking...",
        ["اتصال بیش از حد طول کشید و متوقف شد"] = "Connection took too long and was stopped",
        ["در حال اتصال OpenVPN"] = "Connecting OpenVPN",
        ["در حال اتصال V2Ray/Xray"] = "Connecting V2Ray/Xray",
        ["در حال اتصال Proxy"] = "Connecting Proxy",
        ["در حال اتصال L2TP/IPsec"] = "Connecting L2TP/IPsec",
        ["در حال اتصال"] = "Connecting",
        ["مراحل اتصال"] = "Connection stages",
        ["زمان سپری‌شده"] = "Elapsed",
        ["زمان سپری‌شده: {0}"] = "Elapsed: {0}",
        ["در حال بستن پروسس‌ها و خروج..."] = "Closing processes and exiting...",
        ["در حال بررسی سلامت اتصال..."] = "Running connection health checks...",
        ["سلامت اتصال تأیید شد"] = "Connection health OK",
        ["سلامت اتصال با هشدار"] = "Connection health warning",
        ["سلامت اتصال تأیید شد — {0}"] = "Connection health OK — {0}",
        ["سلامت اتصال با هشدار — {0}"] = "Connection health warning — {0}",
        ["آداپتر VPN فعال"] = "VPN adapter up",
        ["آداپتر VPN شناسایی نشد"] = "VPN adapter not detected",
        ["اسپلیت‌تانلینگ فعال"] = "Split tunneling active",
        ["اسپلیت‌تانلینگ غیرفعال"] = "Split tunneling inactive",
        ["بدون نشت ترافیک"] = "No traffic leaks",
        ["نشت ترافیک: {0}"] = "Traffic leaks: {0}",
        ["اتصال برقرار نشد"] = "Connection failed",
        ["لغو اتصال"] = "Cancel connection",
        ["بررسی کانفیگ و پورت‌ها"] = "Checking config and ports",
        ["پاکسازی processهای قبلی TunnelX"] = "Cleaning up previous TunnelX processes",
        ["راه‌اندازی هسته تونل (Xray/V2Ray)"] = "Starting tunnel core (Xray/V2Ray)",
        ["راه‌اندازی پل TUN (sing-box)"] = "Starting TUN bridge (sing-box)",
        ["شناسایی آداپتر مجازی"] = "Detecting virtual adapter",
        ["راه‌اندازی اسپلیت‌تانلینگ"] = "Starting split tunneling",
        ["بررسی سلامت اتصال"] = "Running connection health checks",
        ["بررسی کانفیگ و پیش‌نیاز OpenVPN"] = "Checking config and OpenVPN prerequisites",
        ["راه‌اندازی OpenVPN"] = "Starting OpenVPN",
        ["انتظار برای آداپتر VPN"] = "Waiting for VPN adapter",
        ["بررسی کانفیگ WireGuard"] = "Checking WireGuard config",
        ["پاکسازی سرویس WireGuard قبلی"] = "Cleaning up previous WireGuard service",
        ["راه‌اندازی سرویس WireGuard"] = "Starting WireGuard service",
        ["انتظار برای آداپتر WireGuard"] = "Waiting for WireGuard adapter",
        ["بررسی تنظیمات اتصال"] = "Checking connection settings",
        ["برقراری اتصال L2TP/IPsec"] = "Establishing L2TP/IPsec connection",
        ["آداپتر TunnelX-V2Ray آماده شد (شماره {0})"] = "TunnelX-V2Ray adapter ready (index {0})",
        ["آداپتر WireGuard آماده شد (شماره {0})"] = "WireGuard adapter ready (index {0})",
        ["خطا در راه‌اندازی اسپلیت‌تانلینگ: {0}"] = "Split tunneling startup failed: {0}",
        ["تا قبل از بالا آمدن آداپتر، مسیرهای سیستم تغییر داده نمی‌شود. اگر اتصال طولانی شد، فایل .ovpn، نام کاربری/رمز یا نصب OpenVPN Community را بررسی کنید."] = "System routes are not changed until the adapter is up. If connecting takes too long, check the .ovpn file, credentials, or OpenVPN Community installation.",
        ["TunnelX در حال راه‌اندازی اتصال و آماده‌سازی مسیرهای اسپلیت‌تانلینگ است. می‌توانید با دکمه لغو اتصال تلاش فعلی را متوقف کنید."] = "TunnelX is starting the connection and preparing split-tunneling routes. You can stop the current attempt with the cancel button.",
        ["🔌  اتصال"] = "🔌  Connect",
        ["❌  لغو اتصال"] = "❌  Cancel",
        ["🔴  قطع اتصال"] = "🔴  Disconnect",
        ["⏳  در حال قطع..."] = "⏳  Disconnecting...",
        ["🔌  اتصال مجدد"] = "🔌  Reconnect",
        ["اتصال"] = "Connect",
        ["لغو تلاش اتصال"] = "Cancel connection attempt",
        ["اتصال مجدد"] = "Reconnect",
        ["IP خروجی"] = "Exit IP",
        ["تغییر حالت Full Route ناموفق بود"] = "Failed to change Full Route mode",
        ["Full Route فعال است؛ کل سیستم از تونل عبور می‌کند"] = "Full Route is enabled; the whole system uses the tunnel",
        ["Split فعال است؛ فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند"] = "Split is enabled; only selected apps and destinations use the tunnel",
        ["حالت کل سیستم"] = "Full-system Mode",
        ["حالت انتخابی"] = "Selected Mode",
        ["ترافیک کل سیستم از تونل عبور خواهد کرد؛ برای وقتی مناسب است که همه برنامه‌ها باید پشت تونل باشند."] = "All system traffic will use the tunnel; useful when every app must be behind the tunnel.",
        ["فقط برنامه‌ها و مقصدهای انتخابی از تونل عبور می‌کنند؛ بقیه ترافیک مستقیم می‌ماند."] = "Only selected apps and destinations use the tunnel; the rest stays direct.",
        ["پروفایل فعال"] = "Active profile",
        ["پروفایل فعال: {0}"] = "Active profile: {0}",
        ["متصل به پراکسی"] = "Proxy",
        ["متصل به VPN"] = "Connected",
        ["وضعیت اتصال: {0} — {1}"] = "Connection status: {0} — {1}",
        ["توقف تست"] = "Stop test",
        ["توقف"] = "Stop",
        ["تست مقصد"] = "Test target",
        ["در حال پینگ..."] = "Pinging...",
        ["پینگ سرور"] = "Ping server",
        ["در حال تست..."] = "Testing...",
        ["تست سرور"] = "Test server",
        ["در حال بررسی آخرین نسخه در GitHub..."] = "Checking the latest version on GitHub...",
        ["بررسی نسخه جدید ناموفق بود. اتصال اینترنت یا GitHub را بررسی کنید."] = "Version check failed. Check your internet connection or GitHub access.",
        ["نسخه جدید آماده است: {0} - برای دانلود از GitHub باز کنید."] = "New version available: {0} - open GitHub to download.",
        ["TunnelX به‌روز است. نسخه فعلی: {0}"] = "TunnelX is up to date. Current version: {0}",
        ["تعداد نصب این برنامه از گیت هاب: {0}"] = "Installations from GitHub: {0}",
        ["بررسی بروزرسانی به زمان مجاز نرسید."] = "Update check timed out.",
        ["بررسی بروزرسانی ناموفق بود: {0}"] = "Update check failed: {0}",
        ["آماده تست و اتصال"] = "Ready to test and connect",
        ["OpenVPN Community نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید"] = "OpenVPN Community is not installed; install it from the official link first",
        ["WireGuard رسمی ویندوز نصب نیست؛ ابتدا آن را از لینک رسمی نصب کنید"] = "Official WireGuard for Windows is not installed; install it from the official link first",
        ["WireGuard نصب نیست؛ راهنمای نصب نمایش داده شد"] = "WireGuard is not installed; installation guide was shown",
        ["فایل .ovpn را انتخاب کنید؛ TunnelX آن را در حالت split-compatible اجرا می‌کند"] = "Choose a .ovpn file; TunnelX runs it in split-compatible mode",
        ["کانفیگ انتخاب شد؛ اگر سرور احراز هویت دارد نام کاربری را وارد کنید"] = "Config selected; enter username if the server requires authentication",
        ["کانفیگ و نام کاربری OpenVPN آماده است"] = "OpenVPN config and username are ready",
        ["منتظر کانفیگ"] = "Waiting for config",
        ["کانفیگ V2Ray/Xray را وارد یا پیست کنید"] = "Enter or paste the V2Ray/Xray config",
        ["هسته: Xray-core"] = "Core: Xray-core",
        ["هسته: sing-box"] = "Core: sing-box",
        ["سرور: {0}:{1}"] = "Server: {0}:{1}",
        ["پراکسی آماده است: {0}"] = "Proxy is ready: {0}",
        ["پراکسی آماده است: {0} — توجه: این پراکسی محلی است؛ برنامه‌هایی که خودشان مستقیم از همین پراکسی استفاده کنند خارج از لیست برنامه‌های TunnelX هم پروکسی می‌شوند."] = "Proxy is ready: {0} - note: this is a local proxy; apps that directly use it will be proxied even outside TunnelX's app list.",
        ["آدرس IP یا دامنه سرور پراکسی را وارد کنید"] = "Enter the proxy server IP or domain",
        ["تنظیمات پراکسی آماده است"] = "Proxy settings are ready",
        ["پورت باید بین 1024 تا 65535 باشد"] = "Port must be between 1024 and 65535",
        ["این پورت رایج/حساس است؛ یک پورت آزاد مثل 1080، 1081 یا 18080 انتخاب کنید"] = "This port is common/sensitive; choose a free port like 1080, 1081, or 18080",
        ["این پورت همین حالا توسط برنامه دیگری استفاده می‌شود"] = "This port is currently used by another app",
        ["پورت SOCKS5 داخلی آماده است"] = "Internal SOCKS5 port is ready"
        ,
        ["خطای اتصال خودکار: {0}"] = "Auto-connect error: {0}",
        ["حمایت از پروژه"] = "Support the project",
        ["تغییر زبان"] = "Change language",
        ["پورت پراکسی را وارد کنید"] = "Enter the proxy port",
        ["پورت پراکسی باید عدد باشد"] = "Proxy port must be a number",
        ["در حال آماده‌سازی V2Ray..."] = "Preparing V2Ray...",
        ["فایل sing-box.exe پیدا نشد: {0}"] = "sing-box.exe was not found: {0}",
        ["خطا در پارس کانفیگ: {0}"] = "Config parse error: {0}",
        ["در حال انتظار برای interface TunnelX-V2Ray..."] = "Waiting for TunnelX-V2Ray interface...",
        ["sing-box زودتر خارج شد (exit code {0}) — کانفیگ را بررسی کنید"] = "sing-box exited early (exit code {0}) - check the config",
        ["interface TunnelX-V2Ray ظاهر نشد (timeout 10s)"] = "TunnelX-V2Ray interface did not appear (timeout 10s)",
        ["در حال قطع اتصال V2Ray..."] = "Disconnecting V2Ray...",
        ["در حال آماده سازی Xray..."] = "Preparing Xray...",
        ["فایل xray.exe پیدا نشد: {0}"] = "xray.exe was not found: {0}",
        ["در حال قطع اتصال Xray..."] = "Disconnecting Xray...",
        ["در حال ایجاد اتصال VPN..."] = "Creating VPN connection...",
        ["خطا در ایجاد VPN: {0}"] = "VPN creation failed: {0}",
        ["در حال اتصال به سرور..."] = "Connecting to server...",
        ["متصل — IP: {0}"] = "Connected - IP: {0}",
        ["نام کاربری یا رمز عبور اشتباه است"] = "The username or password is incorrect",
        ["پورت یا دستگاه اشغال است"] = "The port or device is busy",
        ["انتظار برای پاسخ سرور به اتمام رسید (زمان‌بر)"] = "Timed out waiting for the server response",
        ["پروتکل PPP بین کلاینت و سرور مطابقت ندارد"] = "PPP protocol does not match between client and server",
        ["پروتکل Link Control قطع شد"] = "Link Control Protocol terminated",
        ["آدرس درخواستی توسط سرور رد شد"] = "The requested address was rejected by the server",
        ["رایانه به اینترنت متصل نیست"] = "The computer is not connected to the internet",
        ["رمزنگاری L2TP/IPsec شکست خورد - PSK یا تنظیمات سرور را بررسی کنید"] = "L2TP/IPsec encryption failed - check the PSK or server settings",
        ["مقصد در دسترس نیست (سرور خاموش یا آدرس اشتباه)"] = "Destination is unreachable (server is down or address is wrong)",
        ["Pre-Shared Key (PSK) اشتباه است"] = "Pre-Shared Key (PSK) is incorrect",
        ["رمزنگاری L2TP شکست خورد"] = "L2TP encryption failed",
        ["سرور VPN در دسترس نیست"] = "VPN server is not reachable",
        ["نوع شبکه را نمی‌توان مشخص کرد (فایروال مسدود کرده)"] = "Network type cannot be determined (firewall may be blocking it)",
        ["اتصال قبلی باعث تضاد شده"] = "A previous connection caused a conflict",
        ["خطای ناشناخته (exit code: {0})"] = "Unknown error (exit code: {0})",
        ["xray زودتر خارج شد (exit code {0})"] = "xray exited early (exit code {0})",
        ["sing-box bridge زودتر خارج شد (exit code {0})"] = "sing-box bridge exited early (exit code {0})",
        ["نوع تانل ناشناخته: {0}"] = "Unknown tunnel type: {0}",
        ["در حال لغو اتصال..."] = "Canceling connection...",
        ["آدرس سرور را وارد کنید"] = "Enter the server address",
        ["کانفیگ V2Ray را وارد کنید"] = "Enter the V2Ray config",
        ["کانفیگ OpenVPN (.ovpn) را وارد کنید"] = "Enter the OpenVPN (.ovpn) config",
        ["در حال آماده‌سازی OpenVPN..."] = "Preparing OpenVPN...",
        ["در حال اتصال..."] = "Connecting...",
        ["اتصال لغو شد"] = "Connection canceled",
        ["پراکسی متصل شد"] = "Proxy connected",
        ["در حال دریافت..."] = "Fetching...",
        ["در حال قطع اتصال..."] = "Disconnecting...",
        ["قطع شد"] = "Disconnected",
        ["اتصال VPN قطع شد..."] = "VPN connection dropped...",
        ["اتصال VPN به‌طور غیرمنتظره قطع شد"] = "VPN connection dropped unexpectedly",
        ["اتصال VPN به‌طور غیرمنتظره قطع شد.\nلطفاً دوباره متصل شوید."] = "VPN connection dropped unexpectedly.\nPlease reconnect.",
        ["قطع اتصال"] = "Disconnect",
        ["OpenVPN دوباره متصل شد؛ مسیرهای TunnelX در حال بروزرسانی است..."] = "OpenVPN reconnected; TunnelX routes are being updated...",
        ["OpenVPN دوباره متصل شد و مسیرها بروزرسانی شدند"] = "OpenVPN reconnected and routes were updated",
        ["آدرس سرور خالی است"] = "Server address is empty",
        ["remote سرور در فایل .ovpn پیدا نشد"] = "Server remote was not found in the .ovpn file",
        ["کانفیگ UDP است؛ تست دقیق قبل از اتصال ممکن نیست"] = "The config uses UDP; accurate pre-connect testing is not possible",
        ["هیچ remote قابل‌دسترسی نبود ({0})"] = "No reachable remote was found ({0})",
        ["خطا: {0}"] = "Error: {0}",
        ["در حال پینگ سرور..."] = "Pinging server...",
        ["پینگ سرور timeout شد"] = "Server ping timed out",
        ["فایل .ovpn انتخاب نشده است"] = ".ovpn file is not selected",
        ["کانفیگ خالی است"] = "Config is empty",
        ["endpoint سرور از کانفیگ تشخیص داده نشد"] = "Server endpoint could not be detected from the config",
        ["پارس کانفیگ ناموفق بود: {0}"] = "Failed to parse config: {0}",
        ["آدرس ss:// نامعتبر است"] = "Invalid ss:// address",
        ["سرور پیدا نشد"] = "Server not found",
        ["پورت نامعتبر است"] = "Invalid port",
        ["آدرس نامعتبر"] = "Invalid address",
        ["  [پایان]"] = "  [done]",
        ["TCP {0} ms"] = "TCP {0} ms",
        ["TCP {0} ms  ({1}/{2})"] = "TCP {0} ms  ({1}/{2})",
        ["✗ timeout  ({0}/{1})"] = "✗ timeout  ({0}/{1})",
        ["✗ {0}  ({1}/{2})"] = "✗ {0}  ({1}/{2})",
        ["ICMP {0} ms"] = "ICMP {0} ms",
        ["ICMP {0}"] = "ICMP {0}",
        ["{0} ms"] = "{0} ms",
        ["{0} {1} ms"] = "{0} {1} ms",
        ["وقت‌تمام شد"] = "Timed out",
        ["timeout"] = "Timed out",
        ["🏓 سرور"] = "🏓 Server",
        ["۱ پروفایل ذخیره‌شده"] = "1 saved profile",
        ["{0} پروفایل ذخیره‌شده"] = "{0} saved profiles",
        ["نوع اتصال نامشخص"] = "Unknown connection type",
        ["آدرس سرور هنوز وارد نشده"] = "Server address not entered yet",
        ["کانفیگ V2Ray/Xray آماده نمایش نیست"] = "V2Ray/Xray config is not ready to display",
        ["فایل OpenVPN انتخاب نشده"] = "OpenVPN file not selected",
        ["آدرس پراکسی هنوز وارد نشده"] = "Proxy address not entered yet",
        ["تغییرات این پروفایل به‌صورت خودکار ذخیره می‌شود"] = "Profile changes are saved automatically",
        ["در حال ذخیره..."] = "Saving...",
        ["پیش‌فرض"] = "Default",
        ["پروفایل {0}"] = "Profile {0}",
        ["{0} (کپی)"] = "{0} (copy)",
        ["کپی پروفایل"] = "Copy Profile",
        ["پروفایل «{0}» حذف شود؟"] = "Delete profile \"{0}\"?",
        ["حذف پروفایل"] = "Delete Profile",
        ["اجرای خودکار ویندوز فعال شد. اگر فایل TunnelX را جابه‌جا کردید، این گزینه را یک بار خاموش و روشن کنید."] = "Windows startup is enabled. If you move the TunnelX file, turn this option off and on again.",
        ["TunnelX — استارت‌آپ"] = "TunnelX - Startup",
        ["تغییر تنظیم اجرای خودکار ویندوز ناموفق بود"] = "Failed to change the Windows startup setting",
        ["دسترسی به تنظیمات اجرای خودکار ویندوز ممکن نیست"] = "Cannot access the Windows startup settings",
        ["مسیر فایل اجرایی TunnelX پیدا نشد"] = "The TunnelX executable path was not found",
        ["تغییر تنظیم اجرای خودکار ویندوز ناموفق بود: {0}"] = "Failed to change the Windows startup setting: {0}",
        ["اتصال خودکار فعال است، اما اتصال موفق قبلی پیدا نشد"] = "Auto-connect is enabled, but no previous successful connection was found",
        ["اتصال خودکار به «{0}»..."] = "Auto-connecting to \"{0}\"..."
        ,
        ["برای سرورهای SOCKS5 یا HTTP Proxy، اطلاعات را جداگانه وارد کنید. TunnelX از همین پراکسی یک TUN داخلی می‌سازد تا اسپلیت‌تانلینگ برنامه‌های انتخابی مثل سایر نوع‌های اتصال کار کند."] = "For SOCKS5 or HTTP Proxy servers, enter the server details separately. TunnelX builds an internal TUN from this proxy so selected-app split tunneling works like other connection types.",
        ["اگر پراکسی شما نام کاربری یا رمز ندارد، فیلدهای احراز هویت را خالی بگذارید."] = "If your proxy has no username or password, leave the authentication fields empty.",
        ["🏓 سرور"] = "🏓 Server",
        ["قبل از اتصال، دسترسی و latency سرور را تست می‌کند"] = "Tests server reachability and latency before connecting",
        ["وضعیت و پروفایل فعال"] = "Status and active profile",
        ["مدت زمان اتصال فعلی از لحظه برقراری اتصال"] = "Current connection duration since connection started",
        ["مدت"] = "Duration",
        ["IP عمومی‌ای که مقصدهای اینترنتی شما را با آن می‌بینند"] = "The public IP seen by internet destinations",
        ["مجموع ترافیک ارسال و دریافت عبوری از تونل VPN (کل تونل)"] = "Total sent and received traffic through the VPN tunnel",
        ["تونل"] = "Tunnel",
        ["نمایش تشخیصی ترافیک خارج از تونل. این عدد در مصرف تونل و تاریخچه ثبت نمی‌شود."] = "Diagnostic view of traffic outside the tunnel. This number is not recorded as tunnel usage or history.",
        ["خارج تونل"] = "Direct",
        ["عبور کل سیستم"] = "Full Route",
        ["پروکسی دستی"] = "Manual Proxy",
        ["تست مسیر"] = "Route test",
        ["🌐 عبور کل سیستم"] = "🌐 Full Route",
        ["روشن: کل ویندوز از تونل. خاموش: فقط برنامه‌ها و قوانین انتخابی."] = "On: all Windows traffic uses the tunnel. Off: only selected apps and rules.",
        ["اگر برنامه‌ای خودکار وارد تونل نشد، این آدرس را در تنظیمات Proxy همان برنامه وارد کنید."] = "If an app does not enter the tunnel automatically, enter this address in that app's proxy settings.",
        ["🧦 پروکسی دستی"] = "🧦 Manual Proxy",
        ["این آدرس داخلی را در برنامه‌هایی وارد کنید که تنظیم Proxy جداگانه دارند یا خودکار وارد تونل نمی‌شوند."] = "Use this internal address in apps with separate proxy settings or apps that do not enter the tunnel automatically.",
        ["این آدرس داخلی را در برنامه‌هایی وارد کنید که تنظیم Proxy جداگانه دارند یا خودکار وارد تونل نمی‌شوند. پورت این پراکسی از تب تنظیمات قابل تغییر است."] = "Use this internal address in apps with separate proxy settings or apps that do not enter the tunnel automatically. You can change this proxy port from the Settings tab.",
        ["پینگ"] = "Ping",
        ["🏓 تست مسیر"] = "🏓 Route Test",
        ["یک دامنه یا IP را از داخل تونل تست کنید."] = "Test a domain or IP from inside the tunnel.",
        ["IP یا دامنه مقصد برای تست از داخل تونل"] = "Destination IP or domain to test through the tunnel",
        ["تست همین مقصد از داخل مسیر تونل"] = "Test this destination through the tunnel route",
        ["دسترسی به سرور همین اتصال را تست می‌کند"] = "Tests reachability to this connection's server",
        ["قطع اتصال فعلی"] = "Disconnect current connection",
        ["💡 در حالت انتخابی، فقط برنامه‌های فعال در تب "] = "💡 In selected mode, only apps enabled in the ",
        ["«برنامه‌ها»"] = "\"Apps\"",
        [" از تونل عبور می‌کنند؛ بقیه مستقیم می‌مانند."] = " tab use the tunnel; the rest stays direct.",
        ["📌 برای تلگرام، واتس‌اپ و برنامه‌های Store، "] = "📌 For Telegram, WhatsApp, and Store apps, also add ",
        [" را هم به لیست تونل اضافه کنید."] = " to the tunnel list."
        ,
        ["TunnelX — جزئیات عملکرد"] = "TunnelX - Runtime Details",
        ["🔍 جزئیات عملکرد — TunnelX"] = "🔍 Runtime Details - TunnelX",
        ["🗑 پاک کردن"] = "🗑 Clear",
        ["📋 کپی"] = "📋 Copy",
        ["لاگ‌ها"] = "Logs",
        ["خطا در کپی کردن:\n{0}"] = "Copy failed:\n{0}",
        ["لاگ‌ها پاک شدند"] = "Logs cleared",
        ["لاگی برای کپی وجود ندارد"] = "There are no logs to copy",
        ["لاگ کپی شد"] = "Log copied",
        ["آخرین خطا یا هشدار پیدا نشد"] = "No recent error or warning found",
        ["آخرین خطا یا هشدار کپی شد"] = "Latest error or warning copied",
        ["کپی لاگ ناموفق بود"] = "Failed to copy logs",
        ["باز کردن لینک ناموفق بود: {0}"] = "Failed to open link: {0}",
        ["کپی ناموفق بود: {0}"] = "Copy failed: {0}",
        ["آیا مطمئن هستید؟"] = "Are you sure?",
        ["در حال اجرای OpenVPN در حالت Split..."] = "Running OpenVPN in split mode...",
        ["فقط OpenVPN Connect پیدا شد. برای Split Tunneling باید OpenVPN Community (openvpn.exe) هم نصب باشد."] = "Only OpenVPN Connect was found. Split tunneling requires OpenVPN Community (openvpn.exe).",
        ["OpenVPN Community پیدا نشد. برای Split Tunneling باید openvpn.exe نصب باشد."] = "OpenVPN Community was not found. Split tunneling requires openvpn.exe.",
        ["کانفیگ OpenVPN (.ovpn) وارد نشده است."] = "OpenVPN (.ovpn) config is missing.",
        ["OpenVPN Community نصب نیست؛ ابتدا از لینک رسمی نصب کنید"] = "OpenVPN Community is not installed; install it from the official link first",
        ["OpenVPN در حال اتصال است؛ مسیرهای پیش‌فرض آن برای Split Tunnel نادیده گرفته می‌شوند..."] = "OpenVPN is connecting; its default routes are ignored for split tunneling...",
        ["OpenVPN زودتر از اتصال بسته شد (exit={0})"] = "OpenVPN exited before connecting (exit={0})",
        ["منتظر بالا آمدن آداپتر OpenVPN... ({0}s)"] = "Waiting for the OpenVPN adapter... ({0}s)",
        ["آداپتور OpenVPN بالا نیامد. لاگ OpenVPN را بررسی کنید؛ ممکن است ریموت اول پاسخ ندهد یا احراز هویت/شبکه مشکل داشته باشد."] = "The OpenVPN adapter did not come up. Check the OpenVPN log; the first remote may not respond or authentication/network may be failing.",
        ["OpenVPN متصل شد (Split Tunnel)"] = "OpenVPN connected (Split Tunnel)",
        ["در حال قطع اتصال OpenVPN..."] = "Disconnecting OpenVPN...",
        ["  •  📊 تونل "] = "  •  📊 Tunnel ",
        ["برای سرورهای SOCKS5 یا HTTP Proxy، اطلاعات سرور را جداگانه وارد کنید."] = "For SOCKS5 or HTTP Proxy servers, enter the server details separately.",
        ["TunnelX فایل .ovpn را با OpenVPN Community اجرا می‌کند و مسیر/DNS پیش‌فرض OpenVPN را کنترل می‌کند تا فقط برنامه‌های انتخابی از تونل عبور کنند."] = "TunnelX runs the .ovpn file with OpenVPN Community and controls OpenVPN's default route/DNS so only selected apps use the tunnel.",
        ["OpenVPN Connect به‌تنهایی کافی نیست؛ اگر Community نصب نباشد، از دکمه دانلود پایین استفاده کنید."] = "OpenVPN Connect alone is not enough; if Community is not installed, use the download button below.",
        ["انتخاب برنامه"] = "Select App",
        ["لایسنس: {0}"] = "License: {0}",
        ["برای اتصال VPN ویندوز. آدرس سرور، نام کاربری، رمز عبور و Pre-Shared Key لازم است. اگر وصل نشد، PSK، فایروال و تنظیمات VPN ویندوز را بررسی کنید."] = "For Windows VPN connection. Server address, username, password, and Pre-Shared Key are required. If it does not connect, check the PSK, firewall, and Windows VPN settings.",
        ["لینک یا JSON کانفیگ را وارد کنید یا از کلیپ‌بورد پیست کنید. TunnelX معمولاً sing-box را اجرا می‌کند و برای قابلیت‌هایی مثل xhttp از Xray-core استفاده می‌کند."] = "Enter a config link or JSON, or paste it from the clipboard. TunnelX usually runs sing-box and uses Xray-core for features such as xhttp.",
        ["برای پراکسی خارجی آماده. نوع پراکسی، آدرس، پورت و در صورت نیاز نام کاربری/رمز را وارد کنید. این با پراکسی داخلی 127.0.0.1 فرق دارد."] = "For an external ready proxy. Enter protocol, address, port, and credentials if needed. This is different from the internal 127.0.0.1 proxy.",
        ["فایل ovpn را انتخاب کنید. OpenVPN Community باید جداگانه نصب باشد؛ OpenVPN Connect برای Split Tunneling مناسب نیست. اگر سرور رمز می‌خواهد، نام کاربری و رمز را در TunnelX وارد کنید."] = "Choose the ovpn file. OpenVPN Community must be installed separately; OpenVPN Connect is not suitable for split tunneling. If the server requires credentials, enter them in TunnelX.",
        ["از لیست برنامه‌های پیدا شده انتخاب کنید یا فایل exe را دستی اضافه کنید. برای Store/MSIX/WebView2 برنامه را باز نگه دارید و بروزرسانی لیست را بزنید."] = "Select from discovered apps or manually add an exe file. For Store/MSIX/WebView2 apps, keep the app open and refresh the list.",
        ["«مستقیم بماند» یعنی مقصد از تونل عبور نکند. «از تونل عبور کند» یعنی مقصد حتی بدون انتخاب برنامه وارد تونل شود. دامنه‌ها زیردامنه‌ها را هم پوشش می‌دهند."] = "\"Stay Direct\" means the destination bypasses the tunnel. \"Use Tunnel\" means the destination enters the tunnel even without selecting an app. Domains also cover subdomains.",
        ["پورت پراکسی محلی، MTU خودکار، DNS Optimization، Game Mode، اجرای خودکار ویندوز و اتصال خودکار اینجاست."] = "Local proxy port, automatic MTU, DNS Optimization, Game Mode, Windows startup, and auto-connect are here.",
        ["مدت اتصال، IP، مصرف تونل، مصرف خارج تونل، سلامت Split Tunnel، مصرف برنامه‌ها و تاریخچه اتصال‌ها را نشان می‌دهد."] = "Shows duration, IP, tunnel usage, direct usage, split tunnel health, app usage, and connection history.",
        ["جزئیات و لاگ‌ها"] = "Details and Logs",
        ["از دکمه جزئیات، لاگ‌ها را با فیلتر خطا، هشدار، DNS یا Route ببینید. قبل از ارسال عمومی لاگ، رمزها، کلیدها، UUID و endpoint خصوصی را حذف کنید."] = "Use Details to view logs filtered by error, warning, DNS, or route. Before sharing logs publicly, remove passwords, keys, UUIDs, and private endpoints.",
        ["اتصال برقرار نمی‌شود"] = "Connection Does Not Start",
        ["برنامه را با Administrator اجرا کنید. فایروال، آنتی‌ویروس، آدرس سرور، پورت، رمزها، PSK، نصب OpenVPN Community و اعتبار کانفیگ را بررسی کنید."] = "Run the app as Administrator. Check firewall, antivirus, server address, port, credentials, PSK, OpenVPN Community installation, and config validity.",
        ["ترافیک برنامه از تونل عبور نمی‌کند"] = "App Traffic Does Not Use Tunnel",
        ["برنامه را در تب برنامه‌ها فعال کنید. اگر چندپردازشی است، برنامه را باز نگه دارید و لیست برنامه‌ها را دوباره بارگذاری کنید."] = "Enable the app in the Apps tab. If it is multi-process, keep it open and reload the app list.",
        ["پراکسی کار نمی‌کند"] = "Proxy Does Not Work",
        ["برای پروفایل پراکسی، آدرس، پورت، نوع و اطلاعات ورود را بررسی کنید. برای ابزارهای محلی، آدرس 127.0.0.1 و پورت تنظیمات را وارد کنید."] = "For proxy profiles, check address, port, protocol, and credentials. For local tools, enter 127.0.0.1 and the configured port.",
        ["DNS، IPv6 یا Leak غیرعادی است"] = "DNS, IPv6, or Leak Looks Wrong",
        ["یک بار قطع و وصل کنید تا مسیرها و قوانین DNS دوباره ساخته شوند. اگر مشکل ماند، لاگ‌های DNS و Route را بررسی کنید."] = "Disconnect and reconnect once so routes and DNS rules are rebuilt. If it persists, check DNS and Route logs."
    };
}
