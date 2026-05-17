# Changelog

## Unreleased

### English

- Fixed OpenVPN internal reconnect handling by detecting runtime tunnel IP, gateway, interface, or remote endpoint changes and restarting TunnelX packet routing with the new values.

### فارسی

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


