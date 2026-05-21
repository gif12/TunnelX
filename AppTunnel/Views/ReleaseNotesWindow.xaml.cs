using System.Windows;
using AppTunnel.Services;
using Application = System.Windows.Application;

namespace AppTunnel.Views;

public partial class ReleaseNotesWindow : Window
{
    private Action? _onDownload;

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LocalizationService.Instance.ApplyTo(this);
    }

    public static void Show(Window? owner, string title, string body, Action? onDownload = null)
    {
        var window = new ReleaseNotesWindow();
        window.Owner = owner ?? Application.Current?.MainWindow;
        window.SetContent(title, body, onDownload);
        window.ShowDialog();
    }

    public void SetContent(string title, string body, Action? onDownload = null)
    {
        _onDownload = onDownload;
        TitleText.Text = title;
        BodyText.Text = string.IsNullOrWhiteSpace(body)
            ? LocalizationService.Instance.T("یادداشت این نسخه در دسترس نیست. CHANGELOG یا صفحه Release در GitHub را ببینید.")
            : body;
        Title = title;
        DownloadButton.Visibility = onDownload != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        _onDownload?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
