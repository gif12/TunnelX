using System.IO;
using System.Windows;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.Views;

public partial class ProfileEditorDialog : Window
{
    private readonly ConnectionProfile _profile;

    public ProfileEditorDialog(ConnectionProfile profile, string title)
    {
        _profile = profile;
        InitializeComponent();
        Loaded += (_, _) => LocalizationService.Instance.ApplyTo(this);
        DataContext = profile;
        DialogTitleText.Text = LocalizationService.Instance.T(title);
        Loaded += OnLoaded;
    }

    public static bool? Show(ConnectionProfile profile, string title, Window? owner)
    {
        var dialog = new ProfileEditorDialog(profile, title)
        {
            Owner = owner
        };
        return dialog.ShowDialog();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        L2tpPasswordField.Password = _profile.Password;
        PskField.Password = _profile.PreSharedKey;
        OpenVpnPasswordField.Password = _profile.OpenVpnPassword;
        ProxyPasswordField.Password = _profile.ProxyPassword;
    }

    private void OnBrowseOpenVpnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationService.Instance.T("انتخاب فایل OpenVPN"),
            Filter = LocalizationService.Instance.T("OpenVPN config (*.ovpn)|*.ovpn|All files (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _profile.OpenVpnConfigPath = dialog.FileName;
            _profile.OpenVpnConfig = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            ValidationText.Text = LocalizationService.Instance.Format("خواندن فایل OpenVPN ناموفق بود: {0}", ex.Message);
        }
    }

    private void OnPasteV2RayClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
                _profile.V2RayConfig = System.Windows.Clipboard.GetText().Trim();
        }
        catch (Exception ex)
        {
            ValidationText.Text = LocalizationService.Instance.Format("خواندن کلیپ‌بورد ناموفق بود: {0}", ex.Message);
        }
    }

    private void OnClearV2RayClick(object sender, RoutedEventArgs e)
    {
        _profile.V2RayConfig = "";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _profile.Password = L2tpPasswordField.Password;
        _profile.PreSharedKey = PskField.Password;
        _profile.OpenVpnPassword = OpenVpnPasswordField.Password;
        _profile.ProxyPassword = ProxyPasswordField.Password;

        if (!ValidateProfile(out var message))
        {
            ValidationText.Text = message;
            return;
        }

        DialogResult = true;
        Close();
    }

    private bool ValidateProfile(out string message)
    {
        if (string.IsNullOrWhiteSpace(_profile.Name))
        {
            message = LocalizationService.Instance.T("نام پروفایل را وارد کنید");
            return false;
        }

        switch (_profile.TunnelType)
        {
            case TunnelType.L2tpIpsec when string.IsNullOrWhiteSpace(_profile.ServerAddress):
                message = LocalizationService.Instance.T("آدرس سرور L2TP را وارد کنید");
                return false;
            case TunnelType.V2Ray when string.IsNullOrWhiteSpace(_profile.V2RayConfig):
                message = LocalizationService.Instance.T("کانفیگ V2Ray/Xray را وارد کنید");
                return false;
            case TunnelType.OpenVpn when string.IsNullOrWhiteSpace(_profile.OpenVpnConfig):
                message = LocalizationService.Instance.T("فایل OpenVPN (.ovpn) را انتخاب کنید");
                return false;
            case TunnelType.SocksProxy when string.IsNullOrWhiteSpace(_profile.ProxyServerAddress):
                message = LocalizationService.Instance.T("آدرس سرور پراکسی را وارد کنید");
                return false;
            case TunnelType.SocksProxy when _profile.ProxyPort <= 0 || _profile.ProxyPort > 65535:
                message = LocalizationService.Instance.T("پورت پراکسی باید بین 1 تا 65535 باشد");
                return false;
        }

        message = "";
        return true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
