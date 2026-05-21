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
    private Action? _onSecondaryAction;
    private Action? _onClose;
    private Action? _onPersistentBodyClick;
    private bool _promoMode;
    private bool _dualPromoMode;
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
        ApplyCompactLayout();
        ConfigureActions(null, secondaryActionText: null, showClose: false);
        ApplyContent(title, message, kind);
    }

    public void SetPromoContent(string? title, string message, string actionText, AppNotificationKind kind)
    {
        _promoMode = true;
        _dualPromoMode = false;
        _persistentInfoMode = false;
        _onPersistentBodyClick = null;
        ApplyActionLayout();
        ConfigureActions(actionText, secondaryActionText: null, showClose: true, fullWidthPrimary: true);
        ApplyContent(title, message, kind);
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    /// <summary>Update notification: download + release notes buttons.</summary>
    public void SetDualPromoContent(
        string? title,
        string message,
        string primaryActionText,
        string secondaryActionText,
        AppNotificationKind kind)
    {
        _promoMode = true;
        _dualPromoMode = true;
        _persistentInfoMode = false;
        _onPersistentBodyClick = null;
        ApplyActionLayout();
        ConfigureActions(primaryActionText, secondaryActionText, showClose: true);
        ApplyContent(title, message, kind);
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    /// <summary>Tray-only: stays until user closes (✕). Optional body click (e.g. bring app to front).</summary>
    public void SetPersistentInfoContent(string? title, string message, AppNotificationKind kind, Action? onBodyClick)
    {
        _promoMode = false;
        _persistentInfoMode = true;
        _onPersistentBodyClick = onBodyClick;
        ApplyCompactLayout();
        ConfigureActions(null, secondaryActionText: null, showClose: true);
        ApplyContent(title, message, kind);
        Cursor = onBodyClick != null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
    }

    private void ApplyCompactLayout()
    {
        ActionRow.Height = GridLength.Auto;
        ActionButtonsHost.Visibility = Visibility.Collapsed;
        ActionButtonsHost.MinHeight = 0;
        ActionButtonsHost.Margin = new Thickness(0);
        ApplyTextDisplay(compact: true);
    }

    private void ApplyActionLayout()
    {
        ActionRow.Height = GridLength.Auto;
        ActionButtonsHost.Visibility = Visibility.Visible;
        ActionButtonsHost.MinHeight = 38;
        ActionButtonsHost.Margin = new Thickness(0, 6, 0, 0);
        ApplyTextDisplay(compact: false);
    }

    private void ApplyTextDisplay(bool compact)
    {
        TitleText.TextWrapping = TextWrapping.Wrap;
        TitleText.TextTrimming = TextTrimming.None;
        TitleText.ClearValue(FrameworkElement.MaxHeightProperty);
        TitleText.Margin = new Thickness(0, 0, 0, 2);

        MessageText.TextWrapping = TextWrapping.Wrap;
        MessageText.TextTrimming = TextTrimming.None;
        MessageText.ClearValue(FrameworkElement.MaxHeightProperty);
        MessageText.Margin = new Thickness(0, 0, 0, compact ? 0 : 2);
    }

    public void SetCallbacks(Action? onAction, Action? onClose, Action? onSecondaryAction = null)
    {
        _onAction = onAction;
        _onSecondaryAction = onSecondaryAction;
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

    private void ConfigureActions(
        string? actionText,
        string? secondaryActionText,
        bool showClose,
        bool fullWidthPrimary = false)
    {
        var hasPrimary = !string.IsNullOrWhiteSpace(actionText);
        ActionButton.Content = actionText ?? string.Empty;
        ActionButton.Visibility = hasPrimary ? Visibility.Visible : Visibility.Collapsed;
        if (hasPrimary)
            ActionButton.Style = Application.Current.TryFindResource("TelegramChannelButton") as Style;
        else
            ActionButton.ClearValue(FrameworkElement.StyleProperty);

        var hasSecondary = !string.IsNullOrWhiteSpace(secondaryActionText);
        SecondaryActionButton.Content = secondaryActionText ?? string.Empty;
        SecondaryActionButton.Visibility = hasSecondary ? Visibility.Visible : Visibility.Collapsed;
        if (hasSecondary)
            SecondaryActionButton.Style = Application.Current.TryFindResource("SecondaryButton") as Style;
        else
            SecondaryActionButton.ClearValue(FrameworkElement.StyleProperty);

        ActionButtonsHost.Visibility = hasPrimary || hasSecondary
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (fullWidthPrimary && hasPrimary && !hasSecondary)
        {
            Grid.SetColumn(ActionButton, 0);
            Grid.SetColumnSpan(ActionButton, 3);
            ActionButton.Padding = new Thickness(14, 8, 14, 8);
            ActionButton.FontSize = 12;
            ActionButton.FontWeight = FontWeights.SemiBold;
            ActionButton.MinHeight = 32;
            ActionButton.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            Grid.SetColumn(ActionButton, 0);
            Grid.SetColumnSpan(ActionButton, 1);
            ActionButton.Padding = new Thickness(10, 6, 10, 6);
            ActionButton.FontSize = 11;
            ActionButton.ClearValue(TextBlock.FontWeightProperty);
            ActionButton.MinHeight = 30;
            ActionButton.VerticalAlignment = VerticalAlignment.Stretch;
        }

        if (hasSecondary)
        {
            SecondaryActionButton.MinHeight = 30;
            SecondaryActionButton.VerticalAlignment = VerticalAlignment.Stretch;
        }

        CloseButton.Visibility = showClose ? Visibility.Visible : Visibility.Collapsed;
        if (!_persistentInfoMode && !_dualPromoMode && !fullWidthPrimary)
            Cursor = hasPrimary ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        else
            Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void OnCardAreaClick(object sender, MouseButtonEventArgs e)
    {
        // Do not walk the visual tree from OriginalSource — text inlines (Run) are not Visuals.
        if (CloseButton.IsVisible && CloseButton.IsMouseOver)
            return;

        if (ActionButton.IsVisible && ActionButton.IsMouseOver)
            return;

        if (SecondaryActionButton.IsVisible && SecondaryActionButton.IsMouseOver)
            return;

        if (_persistentInfoMode)
        {
            _onPersistentBodyClick?.Invoke();
            return;
        }

        if (_promoMode)
            return;

        if (ActionButton.Visibility == Visibility.Visible)
            _onAction?.Invoke();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _onAction?.Invoke();
    }

    private void OnSecondaryActionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _onSecondaryAction?.Invoke();
    }

    private void OnClosePreviewClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _onClose?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => e.Handled = true;
}
