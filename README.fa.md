<div dir="rtl" align="right">

# TunnelX

فارسی | <span dir="ltr">[English](README.md)</span>

<span dir="ltr">TunnelX</span> یک نرم‌افزار آزاد و رایگان برای ویندوز است که توسط **<span dir="ltr">MaxFan</span>** ساخته شده و برای مدیریت تونل، وی‌پی‌ان و <span dir="ltr">Split Tunneling</span> استفاده می‌شود. این برنامه می‌تواند ترافیک برنامه‌های انتخاب‌شده، مقصدهای مشخص، یا کل سیستم را از تونل عبور دهد و هم‌زمان مسیر عادی شبکه را برای مقصدهای محلی یا مستثنی‌شده حفظ کند.

## کاربرد برنامه

<span dir="ltr">TunnelX</span> برای زمانی ساخته شده که کاربر نمی‌خواهد تمام ترافیک سیستم از وی‌پی‌ان عبور کند. با این برنامه می‌توان فقط برنامه‌هایی مثل مرورگر، تلگرام، ابزارهای توسعه یا برنامه‌های مشخص دیگر را وارد تونل کرد و بقیه ترافیک سیستم را روی اینترنت عادی نگه داشت. همچنین در صورت نیاز، حالت <span dir="ltr">Full-route</span> برای عبور کل سیستم از تونل در دسترس است.

## قابلیت‌ها

- <span dir="ltr">Split tunneling</span> بر اساس برنامه‌های انتخاب‌شده در ویندوز
- حالت <span dir="ltr">Full-route</span> برای تونل کردن کل سیستم
- پشتیبانی از جریان‌های <span dir="ltr">V2Ray</span> بر پایه <span dir="ltr">Xray-core</span> و <span dir="ltr">sing-box</span>
- پشتیبانی از <span dir="ltr">OpenVPN Community</span> با فایل‌های <span dir="ltr">`.ovpn`</span> برای <span dir="ltr">Split tunneling</span> برنامه‌های انتخاب‌شده
- پروکسی <span dir="ltr">SOCKS5</span> محلی روی <span dir="ltr">`127.0.0.1`</span> برای ابزارهایی که تنظیم پروکسی داخلی دارند
- تغییر مسیر <span dir="ltr">DNS</span>، مسدودسازی <span dir="ltr">IPv6</span>، محافظ نشت، عیب‌یابی <span dir="ltr">route</span> و تاریخچه مصرف تونل
- رابط کاربری فارسی‌محور برای ویندوز

## پشتیبانی از <span dir="ltr">OpenVPN</span>

<span dir="ltr">TunnelX</span> می‌تواند نسخه نصب‌شده <span dir="ltr">OpenVPN Community</span> و فایل انتخابی <span dir="ltr">`.ovpn`</span> کاربر را اجرا کند و سپس سیاست <span dir="ltr">Split tunneling</span> خودش را اعمال کند؛ یعنی فقط برنامه‌ها و مقصدهای انتخاب‌شده از تونل <span dir="ltr">OpenVPN</span> عبور می‌کنند.

<span dir="ltr">OpenVPN</span> همراه <span dir="ltr">TunnelX</span> توزیع نمی‌شود. برای این حالت باید <span dir="ltr">OpenVPN Community</span> را جداگانه نصب کنید، فایل <span dir="ltr">`.ovpn`</span> را در <span dir="ltr">TunnelX</span> انتخاب کنید و در صورت نیاز نام کاربری و رمز عبور <span dir="ltr">OpenVPN</span> را داخل برنامه وارد کنید. نصب بودن <span dir="ltr">OpenVPN Connect</span> به‌تنهایی برای این حالت کافی نیست، چون آن برنامه مسیرها و <span dir="ltr">DNS</span> را با کلاینت خودش مدیریت می‌کند.

## نکته‌های مسیر و دامنه

قانون‌های <span dir="ltr">Include</span> و <span dir="ltr">Exclude</span> هم خود دامنه واردشده و هم زیردامنه‌های آن را پوشش می‌دهند. برای نمونه، افزودن <span dir="ltr">`githubusercontent.com`</span> پس از resolve شدن <span dir="ltr">DNS</span> شامل <span dir="ltr">`raw.githubusercontent.com`</span> هم می‌شود. اگر یک کلاینت <span dir="ltr">HTTPS</span> در مرحله بررسی <span dir="ltr">certificate revocation</span> خطا داد، ممکن است میزبان‌های <span dir="ltr">OCSP/CRL</span> آن از مسیر انتخابی قابل دسترسی نباشند؛ در این حالت خود برنامه دانلودکننده یا دامنه‌های revocation مربوطه را هم در لیست لزومی قرار دهید.

## تصاویر برنامه

| داشبورد اتصال | تنظیم پروفایل و سرور |
| --- | --- |
| <img src="docs/ScreenShots/Screenshot%202026-05-12%20115349.png" alt="داشبورد اتصال TunnelX"> | <img src="docs/ScreenShots/Screenshot%202026-05-12%20115544.png" alt="تنظیم پروفایل و سرور TunnelX"> |

| انتخاب برنامه‌ها برای تونل | تنظیمات تونل |
| --- | --- |
| <img src="docs/ScreenShots/Screenshot%202026-05-12%20115646.png" alt="انتخاب برنامه‌ها برای تونل در TunnelX"> | <img src="docs/ScreenShots/Screenshot%202026-05-12%20115718.png" alt="تنظیمات تونل در TunnelX"> |

## دانلود

فایل‌های آماده اجرا از بخش <span dir="ltr">Releases</span> پروژه منتشر می‌شوند:

<span dir="ltr">[دانلود آخرین نسخه از GitHub Releases](https://github.com/MaxiFan/TunnelX/releases/latest)</span>

فایل‌های منتشرشده توسط <span dir="ltr">GitHub Actions</span> ساخته و آپلود می‌شوند. برای هر فایل اجرایی <span dir="ltr">standalone</span>، فایل checksum با پسوند <span dir="ltr">`.sha256`</span> هم منتشر می‌شود و در متن هر <span dir="ltr">Release</span> لینک اجرای workflow قرار می‌گیرد.

نسخه پیشنهادی برای کاربران، فایل <span dir="ltr">standalone</span> و <span dir="ltr">self-contained</span> است. این نسخه به نصب جداگانه <span dir="ltr">.NET Runtime</span> نیاز ندارد.

## نیازمندی‌های اجرا

- ویندوز <span dir="ltr">10/11</span>
- ویندوز ۶۴ بیتی: <span dir="ltr">`win-x64`</span>
- دسترسی <span dir="ltr">Administrator</span> هنگام اجرا، چون مدیریت <span dir="ltr">route</span> و <span dir="ltr">packet interception</span> به سطح دسترسی بالا نیاز دارد
- نسخه‌های ۳۲ بیتی ویندوز در حال حاضر پشتیبانی نمی‌شوند

## ساخت از سورس

برای توسعه یا ساخت دستی، <span dir="ltr">.NET 8 SDK</span> لازم است:

</div>

```powershell
dotnet build AppTunnel.sln -c Release
dotnet publish AppTunnel\AppTunnel.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

<div dir="rtl" align="right">

جزئیات بیشتر در <span dir="ltr">`docs/BUILD.md`</span> آمده است. ایده‌ها و برنامه‌های آینده در <span dir="ltr">`docs/ROADMAP.md`</span> نگهداری می‌شوند.

## مجوز

<span dir="ltr">TunnelX</span> تحت مجوز **<span dir="ltr">GPL-3.0-or-later</span>** منتشر شده است. استفاده تجاری با رعایت شرایط <span dir="ltr">GPL</span> مجاز است. اجزای شخص ثالث همراه پروژه مجوزهای خودشان را دارند. برای جزئیات بیشتر:

- <span dir="ltr">`LICENSE`</span>
- <span dir="ltr">`THIRD_PARTY_NOTICES.md`</span>
- <span dir="ltr">`docs/LEGAL.md`</span>

## پشتیبانی، سفارشی‌سازی و حمایت مالی

<span dir="ltr">TunnelX</span> آزاد و رایگان است. حمایت مالی کاملا اختیاری است و فقط به نگهداری و توسعه پروژه کمک می‌کند.

خدمات پولی می‌تواند به صورت جداگانه برای پشتیبانی خصوصی، راه‌اندازی، بیلد اختصاصی، سفارشی‌سازی برای شرکت‌ها، یا توسعه برنامه‌ای مشابه ارائه شود. این خدمات پولی حقوقی را که مجوز <span dir="ltr">GPL</span> به کاربران می‌دهد محدود نمی‌کند.

گزینه‌های حمایت و راه‌های تماس از طریق <span dir="ltr">GitHub Sponsors/Funding</span> یا فایل <span dir="ltr">`docs/DONATE.md`</span> در دسترس هستند.

## نکته ایمنی و سلب مسئولیت

<span dir="ltr">TunnelX</span> یک ابزار شبکه، تونل و مدیریت مسیر است. فقط در محیط‌هایی از آن استفاده کنید که اجازه استفاده از وی‌پی‌ان، پروکسی، <span dir="ltr">packet capture</span> و تغییر <span dir="ltr">route</span> را دارید. این پروژه مشاوره حقوقی ارائه نمی‌دهد.

این نرم‌افزار همان‌گونه که هست ارائه می‌شود، بدون هیچ‌گونه ضمانت، و نگهدارنده پروژه تعهدی برای ارائه بروزرسانی، رفع اشکال، پشتیبانی یا ادامه دسترسی دائمی ندارد.

</div>
