using System.Windows;
using System.Windows.Controls;

namespace AppTunnel.Helpers;

/// <summary>
/// Attached flags for TextBlocks that must bypass the global Persian font (e.g. country flag emoji).
/// </summary>
public static class TextBlockFlags
{
    public static readonly DependencyProperty UseEmojiFontProperty =
        DependencyProperty.RegisterAttached(
            "UseEmojiFont",
            typeof(bool),
            typeof(TextBlockFlags),
            new PropertyMetadata(false));

    public static void SetUseEmojiFont(DependencyObject element, bool value)
        => element.SetValue(UseEmojiFontProperty, value);

    public static bool GetUseEmojiFont(DependencyObject element)
        => (bool)element.GetValue(UseEmojiFontProperty);
}
