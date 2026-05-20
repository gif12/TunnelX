using System.Windows;
using AppTunnel.ViewModels;

namespace AppTunnel.Views;

public partial class ConnectionTabView : System.Windows.Controls.UserControl
{
    public ConnectionTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Wire up PasswordBox (can't bind directly in WPF)
        PasswordField.PasswordChanged += OnPasswordFieldChanged;
        PskField.PasswordChanged += OnPskFieldChanged;
        OpenVpnPasswordField.PasswordChanged += OnOpenVpnPasswordFieldChanged;
        ProxyPasswordField.PasswordChanged += OnProxyPasswordFieldChanged;

        // When profile changes, update PasswordBox fields
        vm.PasswordChanged += OnViewModelPasswordChanged;
        vm.OpenVpnPasswordChanged += OnViewModelOpenVpnPasswordChanged;
        vm.ProxyPasswordChanged += OnViewModelProxyPasswordChanged;

        // Load initial values
        PasswordField.Password = vm.Password;
        PskField.Password = vm.PreSharedKey;
        OpenVpnPasswordField.Password = vm.OpenVpnPassword;
        ProxyPasswordField.Password = vm.ProxyPassword;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PasswordField.PasswordChanged -= OnPasswordFieldChanged;
        PskField.PasswordChanged -= OnPskFieldChanged;
        OpenVpnPasswordField.PasswordChanged -= OnOpenVpnPasswordFieldChanged;
        ProxyPasswordField.PasswordChanged -= OnProxyPasswordFieldChanged;

        if (DataContext is MainViewModel vm)
        {
            vm.PasswordChanged -= OnViewModelPasswordChanged;
            vm.OpenVpnPasswordChanged -= OnViewModelOpenVpnPasswordChanged;
            vm.ProxyPasswordChanged -= OnViewModelProxyPasswordChanged;
        }
    }

    private void OnPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.Password != PasswordField.Password)
        {
            vm.Password = PasswordField.Password;
            vm.SaveCurrentState();
        }
    }

    private void OnPskFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.PreSharedKey != PskField.Password)
        {
            vm.PreSharedKey = PskField.Password;
            vm.SaveCurrentState();
        }
    }

    private void OnOpenVpnPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.OpenVpnPassword != OpenVpnPasswordField.Password)
            vm.OpenVpnPassword = OpenVpnPasswordField.Password;
    }

    private void OnProxyPasswordFieldChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.ProxyPassword != ProxyPasswordField.Password)
            vm.ProxyPassword = ProxyPasswordField.Password;
    }

    private void OnViewModelPasswordChanged(string password, string psk)
    {
        Dispatcher.Invoke(() =>
        {
            PasswordField.Password = password;
            PskField.Password = psk;
        });
    }

    private void OnViewModelOpenVpnPasswordChanged(string password)
    {
        Dispatcher.Invoke(() => OpenVpnPasswordField.Password = password);
    }

    private void OnViewModelProxyPasswordChanged(string password)
    {
        Dispatcher.Invoke(() => ProxyPasswordField.Password = password);
    }

    private void OnProfileNameChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SaveCurrentState();
    }

    private static void OnProfileItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // ListBox auto-scroll on selection causes visible jumps while wheel-scrolling.
        e.Handled = true;
    }

}
