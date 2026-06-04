using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutoCode.Desktop.Auth;

namespace AutoCode.Desktop;

/// <summary>
/// Optional sign-in dialog (built on ModalWindow). Email/password + Google, or skip → guest.
/// DialogResult is true when the user signs in, false if they skip/close.
/// </summary>
public sealed class LoginDialog : ModalWindow
{
    private readonly FirebaseAuthService _firebase;
    private readonly TextBox _email;
    private readonly PasswordBox _password;
    private readonly TextBlock _error;
    private readonly Button _signIn;
    private readonly Button _create;
    private readonly Button _google;

    public LoginDialog(FirebaseAuthService firebase)
        : base("Sign in to AutoCode Studio", "Optional — sign in to use the Automax proxy, or continue as a guest with your own API keys.", 420)
    {
        _firebase = firebase;

        _error = new TextBlock
        {
            Foreground = Res<Brush>("RedBrush"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed,
        };
        Body.Children.Add(_error);

        _email = MakeTextBox(null);
        _password = MakePasswordBox(null);
        AddField("Email", null, _email);
        AddField("Password", null, _password);

        _signIn = FullButton("Sign in", primary: true);
        _signIn.Click += async (_, _) => await DoAsync(() => _firebase.SignInWithEmailPasswordAsync(_email.Text.Trim(), _password.Password));
        Body.Children.Add(_signIn);

        _google = FullButton("Continue with Google", primary: false);
        _google.Margin = new Thickness(0, 8, 0, 0);
        _google.Click += async (_, _) => await DoGoogleAsync();
        Body.Children.Add(_google);

        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _create = LinkButton("Create account");
        _create.Click += async (_, _) => await DoAsync(() => _firebase.SignUpWithEmailPasswordAsync(_email.Text.Trim(), _password.Password));
        var forgot = LinkButton("Forgot password?");
        forgot.Margin = new Thickness(16, 0, 0, 0);
        forgot.Click += async (_, _) => await DoForgotAsync();
        links.Children.Add(_create);
        links.Children.Add(forgot);
        Body.Children.Add(links);

        Body.Children.Add(new Border { Height = 1, Background = Res<Brush>("BorderBrush2"), Margin = new Thickness(0, 16, 0, 12) });

        var guest = LinkButton("Continue as guest");
        guest.HorizontalAlignment = HorizontalAlignment.Center;
        guest.Click += (_, _) => { DialogResult = false; Close(); };
        Body.Children.Add(guest);
    }

    private Button FullButton(string text, bool primary) => new()
    {
        Content = text,
        Style = Res<Style>(primary ? "PrimaryButtonStyle" : "GhostButtonStyle"),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private Button LinkButton(string text) => new()
    {
        Content = new TextBlock { Text = text, Foreground = Res<Brush>("AccentBrush"), FontSize = 12.5 },
        Style = Res<Style>("LinkButtonStyle"),
    };

    private async Task DoAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
            DialogResult = true;
            Close();
        }
        catch (FirebaseAuthException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DoGoogleAsync()
    {
        SetBusy(true);
        try
        {
            var oauth = new FirebaseOAuthWindow("google.com") { Owner = this };
            var result = await oauth.PromptAsync();
            if (result.Success)
            {
                await _firebase.AcceptExternalCredentialAsync(
                    result.IdToken, result.RefreshToken, result.Uid, result.Email, result.DisplayName, result.PhotoUrl, result.ExpiresInSec, "google.com");
                DialogResult = true;
                Close();
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage) && result.ErrorMessage != "Sign-in was canceled.")
            {
                ShowError(result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            ShowError($"Google sign-in failed: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DoForgotAsync()
    {
        var email = _email.Text.Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            ShowError("Enter your email first, then click Forgot password.");
            return;
        }

        try
        {
            await _firebase.SendPasswordResetEmailAsync(email);
            _error.Foreground = Res<Brush>("GreenBrush");
            ShowError("Password reset email sent.");
        }
        catch (FirebaseAuthException ex)
        {
            _error.Foreground = Res<Brush>("RedBrush");
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _signIn.IsEnabled = !busy;
        _create.IsEnabled = !busy;
        _google.IsEnabled = !busy;
    }
}
