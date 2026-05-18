using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using AppTunnel.Services;

namespace AppTunnel.Views;

public partial class DonationDialog : Window
{
    public DonationDialog()
    {
        InitializeComponent();
        CryptoItemsControl.ItemsSource = CreateDonationAddresses();
        Loaded += (_, _) => LocalizationService.Instance.ApplyTo(this);
    }

    private static ObservableCollection<CryptoDonationAddress> CreateDonationAddresses()
    {
        var t = LocalizationService.Instance.T;
        return
        [
            new(t("ترون / USDT روی TRC20"), "TNWV867fQDT6zpLunHgbeMjrN6ic63LQSu"),
            new(t("بیت‌کوین"), "bc1qgx3g47c458fu6smnpqpu0l05hha82rq2xjet4y"),
            new(t("اتریوم / USDT روی ERC20"), "0x72d94Bb250E8802441a0ED05686Ee925BC99Fef5"),
            new("TON", "UQD65oL2Vu2OJDSrwQ0wLLSw3g668SREMJ3VPW9k8b6Sy-Yf"),
            new("BNB Smart Chain", "0xE2a5b01cE2b3713D435Bc16d92eAdd88A82159f0"),
            new("Dogecoin", "DSZRNY65yF679uvjAh6sUAt6YiEEQHwKGb")
        ];
    }

    private void OnPayPalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.PayPalDonateUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CopyStatusText.Text = LocalizationService.Instance.Format("باز کردن لینک ناموفق بود: {0}", ex.Message);
        }
    }

    private void OnCopyCryptoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CryptoDonationAddress item })
            return;

        try
        {
            System.Windows.Clipboard.SetText(item.Address);
            CopyStatusText.Text = LocalizationService.Instance.Format("{0} کپی شد", item.Label);
        }
        catch (Exception ex)
        {
            CopyStatusText.Text = LocalizationService.Instance.Format("کپی ناموفق بود: {0}", ex.Message);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public sealed record CryptoDonationAddress(string Label, string Address);
