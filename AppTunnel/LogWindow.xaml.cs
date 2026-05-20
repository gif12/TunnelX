using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppTunnel.Services;
using Clipboard = System.Windows.Clipboard;

namespace AppTunnel;

public partial class LogWindow : Window
{
    private CancellationTokenSource? _logToastCts;

    private enum LogToastKind
    {
        Success,
        Info,
        Warning
    }

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
        ShowLogToast(LocalizationService.Instance.T("لاگ‌ها پاک شدند"), LogToastKind.Success);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogTextBox.Text))
            {
                ShowLogToast(LocalizationService.Instance.T("لاگی برای کپی وجود ندارد"), LogToastKind.Info);
                return;
            }

            Clipboard.SetText(LogTextBox.Text);
            ShowLogToast(LocalizationService.Instance.T("لاگ کپی شد"), LogToastKind.Success);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to copy logs: {ex}");
            ShowLogToast(LocalizationService.Instance.T("کپی لاگ ناموفق بود"), LogToastKind.Warning);
        }
    }

    private void ShowLogToast(string message, LogToastKind kind = LogToastKind.Success, int durationMs = 2800)
    {
        _logToastCts?.Cancel();
        _logToastCts = new CancellationTokenSource();
        var token = _logToastCts.Token;

        ApplyLogToastStyle(kind);
        LogToastIcon.Text = kind switch
        {
            LogToastKind.Success => "✓",
            LogToastKind.Info => "ℹ",
            LogToastKind.Warning => "⚠",
            _ => "•"
        };
        LogToastMessage.Text = message;
        LogToastPanel.Visibility = Visibility.Visible;
        LogToastPanel.Opacity = 1;
        LogToastPanel.BeginAnimation(OpacityProperty, null);

        Task.Delay(durationMs, token).ContinueWith(_ =>
        {
            if (token.IsCancellationRequested)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
                fadeOut.Completed += (__, ___) => LogToastPanel.Visibility = Visibility.Collapsed;
                LogToastPanel.BeginAnimation(OpacityProperty, fadeOut);
            });
        }, TaskScheduler.Default);
    }

    private void ApplyLogToastStyle(LogToastKind kind)
    {
        LogToastPanel.Background = TryFindResource("CardBrush") as System.Windows.Media.Brush
            ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D));
        LogToastMessage.Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;

        var accent = kind switch
        {
            LogToastKind.Info => TryFindResource("AccentBrush"),
            LogToastKind.Warning => TryFindResource("WarningBrush"),
            _ => TryFindResource("SuccessBrush")
        } as System.Windows.Media.Brush ?? TryFindResource("AccentBrush") as System.Windows.Media.Brush;

        LogToastPanel.BorderBrush = accent ?? System.Windows.Media.Brushes.Orange;
        LogToastIcon.Foreground = accent ?? LogToastMessage.Foreground;
    }
}
