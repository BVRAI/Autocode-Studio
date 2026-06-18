using System.Windows;

namespace AutoCode.Desktop;

// Account: Firebase sign-in/out, subscription state, proxy-token provider.
public partial class MainWindow
{
    private void ShowLoginDialog()
    {
        new LoginDialog(_firebase) { Owner = this }.ShowDialog();
        _config.LoginPromptSeen = true;
        _configStore.Save(_config);
        RefreshAccountUi();
    }

    private void RefreshAccountUi()
    {
        _vm.IsSignedIn = _firebase.IsAuthenticated;
        _vm.AccountEmail = _firebase.CurrentEmail ?? "";
        _vm.AccountPhotoUrl = string.IsNullOrEmpty(_firebase.CurrentPhotoUrl) ? null : _firebase.CurrentPhotoUrl;

        _config.AccountEmail = _firebase.CurrentEmail;
        _config.AccountDisplayName = _firebase.CurrentDisplayName;
        _config.AccountPhotoUrl = _firebase.CurrentPhotoUrl;
        _configStore.Save(_config);

        if (_firebase.IsAuthenticated)
        {
            _ = RefreshSubscriptionAsync();
        }
        else
        {
            _vm.IsSubscriber = false;
        }
    }

    private async Task RefreshSubscriptionAsync()
    {
        try
        {
            var token = await _firebase.GetIdTokenAsync();
            var sub = await _subscriptions.GetStatusAsync(_firebase.CurrentUid, token);
            await Dispatcher.InvokeAsync(() => _vm.IsSubscriber = sub.IsActive);
        }
        catch
        {
            // Leave IsSubscriber as-is on transient failure.
        }
    }

    private string? ProxyTokenProvider()
        => _vm.UseProxy && _firebase.IsAuthenticated && _vm.IsSubscriber ? _firebase.CurrentIdToken : null;

    private void SignIn_Click(object sender, RoutedEventArgs e) => ShowLoginDialog();

    private void UseProxyToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.UseProxy = !_vm.UseProxy;
        _config.UseProxy = _vm.UseProxy;
        _configStore.Save(_config);
        UseProxyCheck.Visibility = _vm.UseProxy ? Visibility.Visible : Visibility.Hidden;
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = false;
        await _firebase.SignOutAsync();
        RefreshAccountUi();
    }
}
