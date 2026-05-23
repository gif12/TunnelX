using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AppTunnel.Helpers;
using AppTunnel.Services;
using Application = System.Windows.Application;
using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Views;

public partial class ReleaseNotesWindow : Window
{
    private Action? _onDownload;
    private string _rawBody = "";

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedChrome();
        RenderBody();
    }

    private void OnClosed(object? sender, EventArgs e)
        => LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedChrome();
        RenderBody();
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
        _rawBody = body ?? "";
        TitleText.Text = title;
        Title = title;
        DownloadButton.Visibility = onDownload != null ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded)
        {
            RefreshLocalizedChrome();
            RenderBody();
        }
    }

    private void RefreshLocalizedChrome()
    {
        var loc = LocalizationService.Instance;
        loc.ApplyTo(this);
        CloseButton.Content = loc.T("بستن");
        DownloadButton.Content = loc.T("دانلود این نسخه");
    }

    private void RenderBody()
    {
        BodyPanel.Children.Clear();

        var loc = LocalizationService.Instance;
        var body = string.IsNullOrWhiteSpace(_rawBody)
            ? loc.T("یادداشت این نسخه در دسترس نیست. CHANGELOG یا صفحه Release در GitHub را ببینید.")
            : _rawBody;

        var preferPersianFirst = loc.IsRightToLeft;
        var blocks = ChangelogService.ParseDisplayBlocks(body, preferPersianFirst);
        var localizedBlocks = FilterBlocksForCurrentLanguage(blocks, loc.IsRightToLeft);

        if (localizedBlocks.Count == 0)
        {
            BodyPanel.Children.Add(CreateBodyTextBlock(
                body,
                loc.FlowDirection,
                loc.TextAlignment));
            return;
        }

        foreach (var block in localizedBlocks)
        {
            BodyPanel.Children.Add(CreateBodyTextBlock(
                block.Text,
                block.FlowDirection,
                block.TextAlignment,
                isSection: block.IsSectionHeader));
        }
    }

    private static IReadOnlyList<ReleaseNotesBlock> FilterBlocksForCurrentLanguage(
        IReadOnlyList<ReleaseNotesBlock> blocks,
        bool isRightToLeft)
    {
        var preferredKind = isRightToLeft
            ? ReleaseNotesLanguageKind.Farsi
            : ReleaseNotesLanguageKind.English;

        var preferred = blocks
            .Where(block => block.LanguageKind == preferredKind ||
                            block.LanguageKind == ReleaseNotesLanguageKind.Neutral)
            .ToList();

        return preferred.Count > 0 ? preferred : blocks;
    }

    private static TextBlock CreateBodyTextBlock(
        string text,
        FlowDirection flowDirection,
        TextAlignment textAlignment,
        bool isSection = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = isSection ? 13 : 12.5,
            FontWeight = isSection ? FontWeights.SemiBold : FontWeights.Normal,
            LineHeight = 20,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = flowDirection,
            TextAlignment = textAlignment,
            Margin = new Thickness(0, isSection ? 10 : 0, 0, 8)
        };
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        _onDownload?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
