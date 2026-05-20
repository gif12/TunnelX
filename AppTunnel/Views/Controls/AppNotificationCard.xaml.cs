using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppTunnel.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace AppTunnel.Views.Controls;

public partial class AppNotificationCard : UserControl
{
    private Action? _onAction;
    private Action? _onClose;
    private Action? _onPersistentBodyClick;
    private bool _promoMode;
    private bool _persistentInfoMode;

    public TranslateTransform SlideTransformElement => SlideTransform;

    public AppNotificationCard()
    {
        InitializeComponent();
        Loaded += (_, _) => FlowDirection = LocalizationService.Instance.FlowDirection;
        MouseLeftButtonUp += OnCardAreaClick;
    }

    public void SetContent(string? title, string message, AppNotificationKind kind)
    {
        _promoMode = false;
        _persistentInfoMode = false;
        _onPersistentBodyClick = null;
        MessageText.ClearValue(FrameworkElement.MaxHeightProperty);
        ConfigureActions(null, showClose: false);
        ApplyContent(title, message, kind);
    }

    public void SetPromoContent(string? title, string message, string actionText, AppNotificationKind kind)
    {
        _promoMode = true;
        _persistentInfoMode = false;
        _onPersistentBodyClick = null;
        MessageText.ClearValue(FrameworkElement.MaxHeightProperty);
        ConfigureActions(actionText, showClose: true);
        ApplyContent(title, message, kind);
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    /// <summary>Tray-only: stays until user closes (✕). Optional body click (e.g. bring app to front).</summary>
    public void SetPersistentInfoContent(string? title, string message, AppNotificationKind kind, Action? onBodyClick)
    {
        _promoMode = false;
        _persistentInfoMode = true;
        _onPersistentBodyClick = onBodyClick;
        MessageText.MaxHeight = 120;
        ConfigureActions(null, showClose: true);
        ApplyContent(title, message, kind);
        Cursor = onBodyClick != null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
    }

    public void SetCallbacks(Action? onAction, Action? onClose)
    {
        _onAction = onAction;
        _onClose = onClose;
    }

    private void ApplyContent(string? title, string message, AppNotificationKind kind)
    {
        FlowDirection = LocalizationService.Instance.FlowDirection;
        AppNameText.Text = LocalizationService.Instance.T("TunnelX");

        var hasTitle = !string.IsNullOrWhiteSpace(title);
        TitleText.Text = hasTitle ? title! : string.Empty;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;

        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;

        AccentBar.Background = kind switch
        {
            AppNotificationKind.Success => Application.Current.TryFindResource("SuccessBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F)),
            AppNotificationKind.Warning => Application.Current.TryFindResource("WarningBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xFC, 0xE1, 0x00)),
            AppNotificationKind.Error => Application.Current.TryFindResource("ErrorBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0xA4)),
            _ => Application.Current.TryFindResource("TelegramBlueBrush") as Brush
                 ?? Application.Current.TryFindResource("AccentBrush") as Brush
                 ?? new SolidColorBrush(Color.FromRgb(0x24, 0xA1, 0xDE))
        };
    }

    private void ConfigureActions(string? actionText, bool showClose)
    {
        var hasAction = !string.IsNullOrWhiteSpace(actionText);
        ActionButton.Content = actionText ?? string.Empty;
        ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        if (hasAction)
            ActionButton.Style = Application.Current.TryFindResource("TelegramChannelButton") as Style;
        else
            ActionButton.ClearValue(FrameworkElement.StyleProperty);

        CloseButton.Visibility = showClose ? Visibility.Visible : Visibility.Collapsed;
        if (!_persistentInfoMode)
            Cursor = hasAction ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
    }

    private void OnCardAreaClick(object sender, MouseButtonEventArgs e)
    {
        // Do not walk the visual tree from OriginalSource — text inlines (Run) are not Visuals.
        if (CloseButton.IsVisible && CloseButton.IsMouseOver)
            return;

        if (ActionButton.IsVisible && ActionButton.IsMouseOver)
            return;

        if (_persistentInfoMode)
        {
            _onPersistentBodyClick?.Invoke();
            return;
        }

        if (_promoMode)
        {
            _onAction?.Invoke();
            return;
        }

        if (ActionButton.Visibility == Visibility.Visible)
            _onAction?.Invoke();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _onAction?.Invoke();
    }

    private void OnClosePreviewClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _onClose?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => e.Handled = true;
}
