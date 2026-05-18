using System.Windows;
using System.Windows.Input;
using AppTunnel.Services;
using AppTunnel.Helpers;
using Clipboard = System.Windows.Clipboard;

namespace AppTunnel;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LocalizationService.Instance.ApplyTo(this);
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        LoadLogs();
        Logger.LogAdded += OnLogAdded;
        Closed += (_, _) =>
        {
            Logger.LogAdded -= OnLogAdded;
            LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        LocalizationService.Instance.ApplyTo(this);
    }

    private void LoadLogs()
    {
        LogTextBox.Text = Logger.GetAllLogs();
        LogTextBox.ScrollToEnd();
    }

    private void OnLogAdded(string logEntry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText(logEntry + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        });
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Logger.Clear();
        LogTextBox.Clear();
        DialogService.Success(LocalizationService.Instance.T("لاگ‌ها پاک شدند"));
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogTextBox.Text))
            {
                DialogService.Info(LocalizationService.Instance.T("لاگی برای کپی وجود ندارد"), "TunnelX");
                return;
            }

            Clipboard.SetText(LogTextBox.Text);
            DialogService.ShowCopied(LocalizationService.Instance.T("لاگ‌ها"));
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to copy logs: {ex}");
            DialogService.Error(LocalizationService.Instance.Format("خطا در کپی کردن:\n{0}", ex.Message), "خطا");
        }
    }
}
