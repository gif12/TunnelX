using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using AppTunnel.Models;
using AppTunnel.Services;

namespace AppTunnel.Views;

public partial class DonationDialog : Window
{
    public DonationDialog()
    {
        InitializeComponent();
        CryptoItemsControl.ItemsSource =
            new ObservableCollection<CryptoDonationAddress>(CryptoDonationAddressList.GetAll(LocalizationService.Instance));
        Loaded += (_, _) => LocalizationService.Instance.ApplyTo(this);
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
