# Changelog

## Unreleased

## 1.2.31 - 2026-05-18

- Update release packaging and connection UX
- Update README contact and localization notes

## 1.2.30 - 2026-05-18

### English

- Added bilingual Persian/English UI switching with automatic system-language detection, persisted language selection, RTL/LTR layout handling, and localized dialogs, tray text, runtime status messages, help content, and profile/app/routing views.
- Refined the desktop UI with adaptive window sizing, disabled maximize/system-menu fullscreen paths, smoother log-panel animation, polished dialogs, refreshed TunnelX branding, updated app icon, compact app-list rows, footer donation modal, and header update notification.
- Improved V2Ray/Xray reliability by dynamically reserving free local ports for Xray SOCKS and sing-box mixed proxy inbounds instead of relying on fixed `2080/2081` ports.
- Improved connection UX with more reliable exit-IP detection retries, consistent text-field behavior, better log window direction/localization, and localized README screenshots for Persian and English documentation.

### فارسی

- تغییر زبان فارسی/انگلیسی به برنامه اضافه شد؛ شامل تشخیص زبان سیستم، ذخیره زبان انتخاب‌شده، رعایت RTL/LTR، و ترجمه دیالوگ‌ها، متن tray، وضعیت‌های runtime، راهنما، پروفایل‌ها، برنامه‌ها و قوانین مسیر.
- رابط کاربری دسکتاپ بهبود یافت؛ شامل اندازه‌گیری تطبیقی پنجره، جلوگیری از maximize/fullscreen و منوی Alt+Space، انیمیشن نرم پنل لاگ، دیالوگ‌های تمیزتر، برندینگ و آیکون جدید TunnelX، ردیف‌های فشرده‌تر برنامه‌ها، مودال حمایت مالی و دکمه اعلان بروزرسانی در هدر.
- پایداری V2Ray/Xray بهتر شد؛ پورت‌های داخلی Xray SOCKS و sing-box mixed proxy دیگر ثابت نیستند و به‌صورت آزاد از سیستم رزرو می‌شوند تا خطای اشغال بودن `2080/2081` تکرار نشود.
- تجربه اتصال بهتر شد؛ شامل تلاش دوباره برای دریافت IP خروجی، رفتار یکدست فیلدهای متنی، جهت و ترجمه بهتر پنجره لاگ، و استفاده از اسکرین‌شات‌های فارسی/انگلیسی در READMEهای مربوطه.

## 1.2.29 - 2026-05-17

### English

- Expanded the GitHub README with Russian and Simplified Chinese summaries for international users.
- Expanded the in-app Persian Help tab with fuller guidance for profiles, connection types, routing rules, logs, updates, and troubleshooting.

### فارسی

- توضیح‌های روسی و چینی ساده‌شده به README گیت‌هاب اضافه شد تا کاربران بین‌المللی سریع‌تر با کاربرد برنامه آشنا شوند.
- تب راهنمای فارسی داخل برنامه با توضیح کامل‌تر درباره پروفایل‌ها، نوع‌های اتصال، قوانین مسیر، لاگ‌ها، بروزرسانی و عیب‌یابی گسترش پیدا کرد.

## 1.2.28 - 2026-05-17

### English

- Fixed Full Route default-route installation by preferring the VPN gateway, retrying with an on-link gateway when needed, and cleaning up the pinned physical route to the tunnel server when Full Route is disabled.
- Updated English/Persian README and in-app Help content for the new SOCKS/Proxy profile flow, connection types, routing notes, local data, and troubleshooting guidance.

### فارسی

- نصب default route در حالت Full Route اصلاح شد؛ ابتدا gateway تونل استفاده می‌شود، در صورت نیاز با gateway روی‌لینک دوباره تلاش می‌شود، و route فیزیکی ثابت‌شده برای سرور تونل هنگام خاموش شدن Full Route پاک‌سازی می‌شود.
- README فارسی/انگلیسی و محتوای راهنمای داخل برنامه برای جریان جدید SOCKS/Proxy، نوع‌های اتصال، نکته‌های مسیر، داده‌های محلی و عیب‌یابی به‌روز شد.

## 1.2.27 - 2026-05-17

### English

- Added a dedicated SOCKS5/HTTP Proxy profile type with separate server, port, username, and password fields, encrypted proxy password persistence, validation hints, and proxy-specific connection handling through sing-box.
- Reworked profile management into a compact profile list with separate add/edit dialogs, clearer active profile selection, and improved Persian-first profile cards.
- Improved the connected dashboard with public exit IP detection, shorter ping results, clearer tunnel/direct traffic cards, manual proxy guidance, full-route controls, and a dedicated disconnect action.
- Refined Persian font rendering, global WPF text settings, tab headers, footer, connection controls, route rules, app selection, history, and traffic views for a cleaner desktop UI.
- Improved routing diagnostics and split-tunnel handling for V2Ray, SOCKS/Proxy, OpenVPN, include/exclude destination rules, DNS rule learning, and tunnel server health checks.
- Fixed OpenVPN internal reconnect handling by detecting runtime tunnel IP, gateway, interface, or remote endpoint changes and restarting TunnelX packet routing with the new values.

### فارسی

- نوع پروفایل اختصاصی SOCKS5/HTTP Proxy اضافه شد؛ شامل فیلدهای جداگانه سرور، پورت، نام کاربری و رمز عبور، ذخیره امن رمز پراکسی، راهنمای اعتبارسنجی و اتصال از طریق sing-box.
- مدیریت پروفایل‌ها به لیست فشرده کانفیگ‌ها با پنجره جدا برای افزودن/ویرایش، انتخاب واضح پروفایل فعال و کارت‌های فارسی‌محور بهتر بازطراحی شد.
- داشبورد بعد از اتصال بهبود یافت؛ نمایش IP خروجی عمومی، نتیجه کوتاه پینگ، کارت‌های واضح‌تر مصرف تونل/خارج تونل، راهنمای پراکسی دستی، کنترل Full Route و دکمه اختصاصی قطع اتصال اضافه شد.
- رندر فونت فارسی، تنظیمات عمومی متن در WPF، تب‌ها، فوتر، کنترل‌های اتصال، قوانین مسیر، انتخاب برنامه‌ها، تاریخچه و نمای مصرف ترافیک برای رابط کاربری تمیزتر اصلاح شد.
- عیب‌یابی مسیر و Split Tunneling برای V2Ray، SOCKS/Proxy، OpenVPN، قوانین include/exclude، یادگیری قوانین DNS و health check سرور تونل بهبود پیدا کرد.
- مشکل reconnect داخلی OpenVPN اصلاح شد؛ اگر هنگام اتصال طولانی IP تونل، gateway، interface یا سرور مقصد عوض شود، TunnelX مسیر‌دهی ترافیک را با مقادیر جدید دوباره راه‌اندازی می‌کند.

## 1.2.26 - 2026-05-17

### English

- Added OpenVPN Community support as an external tunnel provider for split tunneling.
- Added `.ovpn` file selection, OpenVPN username/password fields, install detection, and clearer Persian guidance in the connection and help screens.
- Added split-compatible OpenVPN config preparation with route/DNS push filtering, credential file handling without UTF-8 BOM, remote candidate filtering, and faster retry behavior.
- Fixed OpenVPN split routing by capturing the real connected remote, assigned tunnel IP, and route gateway before starting packet routing.
- Added OpenVPN stale-process cleanup for TunnelX-started OpenVPN processes and prevented stale TAP adapters from being treated as a fresh connection.
- Improved server testing and post-connect ping behavior for OpenVPN profiles.

### فارسی

- پشتیبانی از OpenVPN Community به‌عنوان ارائه‌دهنده خارجی تونل برای Split Tunneling اضافه شد.
- انتخاب فایل `.ovpn`، فیلدهای نام کاربری و رمز عبور OpenVPN، تشخیص نصب بودن OpenVPN Community و راهنمای فارسی واضح‌تر در صفحه اتصال و راهنما اضافه شد.
- آماده‌سازی کانفیگ OpenVPN سازگار با Split Tunnel اضافه شد؛ شامل نادیده گرفتن route/DNSهای push شده، ذخیره فایل credential بدون UTF-8 BOM، فیلتر کردن remoteهای نامعتبر و retry سریع‌تر.
- مسیر‌دهی Split Tunnel در OpenVPN با ثبت remote واقعی متصل‌شده، IP اختصاص داده‌شده به تونل و route gateway قبل از شروع packet routing اصلاح شد.
- پاک‌سازی پردازش‌های قدیمی OpenVPN که توسط TunnelX اجرا شده‌اند اضافه شد و از شناسایی آداپترهای TAP خراب یا قدیمی به‌عنوان اتصال جدید جلوگیری شد.
- تست سرور و پینگ بعد از اتصال برای پروفایل‌های OpenVPN بهبود پیدا کرد.

## 1.2.25 - 2026-05-16

- Merge pull request #13 from BlacKSnowDot0/pr-clean
- Merge pull request #16 from mohammad-parvizi-dev/main
- Improve tab headers, theme styling, and tray notifications
- Add startup and auto-connect app settings
- feat(proxy): SOCKS5/HTTP via V2Ray/sing-box, add MixedProxyServer, remove standalone proxy types and local auth

## 1.2.24 - 2026-05-12

- Added README screenshots in English and Persian.
- Added automated GitHub Actions release publishing with version bumping, changelog-based release notes, checksums, and build provenance.

## 1.2.23

- Added GitHub release checking from the Help tab.
- Added automatic tray notification when a newer release is available.
- Added tray notifications for connection, disconnection, and connection errors.
- Added a tray menu action for checking updates.
- Moved remaining future VPN-manager improvements into the public roadmap.

## 1.2.22

- Fixed Help page data binding so GitHub and donation buttons work.
- Localized Help page action buttons.
- Added in-app copy action for donation information.
- Removed internal privacy review and publishing checklist documents from the public repository history.

## 1.2.21

- Prepared open-source repository documentation and release guidance.
- Added in-app GitHub and donation links.
- Added project metadata for MaxFan and GPL-3.0-or-later licensing.
- Improved leak logging and traffic accounting in recent internal builds.




