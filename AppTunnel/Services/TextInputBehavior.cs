using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace AppTunnel.Services;

public static class TextInputBehavior
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        _registered = true;

        EventManager.RegisterClassHandler(
            typeof(WpfTextBox),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnTextBoxPreviewMouseLeftButtonDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(WpfTextBox),
            UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnTextBoxGotKeyboardFocus));
        EventManager.RegisterClassHandler(
            typeof(WpfTextBox),
            WpfControl.MouseDoubleClickEvent,
            new MouseButtonEventHandler(OnTextBoxDoubleClick));

        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPasswordBoxPreviewMouseLeftButtonDown),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnPasswordBoxGotKeyboardFocus));
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            WpfControl.MouseDoubleClickEvent,
            new MouseButtonEventHandler(OnPasswordBoxDoubleClick));
    }

    private static void OnTextBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfTextBox textBox || textBox.IsReadOnly || e.ClickCount > 1)
            return;

        textBox.Focus();
        textBox.CaretIndex = textBox.Text.Length;
        e.Handled = true;
    }

    private static void OnTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not WpfTextBox textBox || textBox.IsReadOnly)
            return;

        textBox.CaretIndex = textBox.Text.Length;
    }

    private static void OnTextBoxDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not WpfTextBox textBox || textBox.IsReadOnly)
            return;

        textBox.SelectAll();
        e.Handled = true;
    }

    private static void OnPasswordBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            passwordBox.GetType().GetMethod("Select", [typeof(int), typeof(int)])
                ?.Invoke(passwordBox, [passwordBox.Password.Length, 0]);
    }

    private static void OnPasswordBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not PasswordBox passwordBox || e.ClickCount > 1)
            return;

        passwordBox.Focus();
        passwordBox.GetType().GetMethod("Select", [typeof(int), typeof(int)])
            ?.Invoke(passwordBox, [passwordBox.Password.Length, 0]);
        e.Handled = true;
    }

    private static void OnPasswordBoxDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
            return;

        passwordBox.SelectAll();
        e.Handled = true;
    }
}
