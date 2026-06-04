using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AutoCode.Desktop.Auth;

/// <summary>
/// Modal WebView2 dialog that runs Firebase's JS SDK (signInWithPopup) and returns a credential.
/// Ported from Automax v6; serves the auth HTML from a localhost HttpListener (localhost is a
/// Firebase-authorized domain) and captures photoURL for the account chip.
/// </summary>
public partial class FirebaseOAuthWindow : Window
{
    private readonly string _providerId;
    private readonly TaskCompletionSource<OAuthResult> _completion = new();
    private bool _hostedPageReady;
    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;

    public FirebaseOAuthWindow(string providerId = "google.com")
    {
        _providerId = providerId;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            try { OAuthWebView?.Dispose(); } catch { }
            try { _serverCts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            if (!_completion.Task.IsCompleted)
            {
                _completion.TrySetResult(OAuthResult.Canceled);
            }
        };
    }

    public Task<OAuthResult> PromptAsync()
    {
        Show();
        return _completion.Task;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "autocode-gui", "OAuthWebView2");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await OAuthWebView.EnsureCoreWebView2Async(env);
            if (OAuthWebView.CoreWebView2 == null)
            {
                CompleteWithError("WebView2 failed to initialize.");
                return;
            }

            OAuthWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            OAuthWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            OAuthWebView.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = false;

            var port = StartLocalhostServer();
            OAuthWebView.CoreWebView2.Navigate($"http://localhost:{port}/auth.html");
        }
        catch (Exception ex)
        {
            CompleteWithError($"Sign-in initialization failed: {ex.Message}");
        }
    }

    private int StartLocalhostServer()
    {
        var temp = new TcpListener(IPAddress.Loopback, 0);
        temp.Start();
        var port = ((IPEndPoint)temp.LocalEndpoint).Port;
        temp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (path.Equals("/auth.html", StringComparison.OrdinalIgnoreCase))
                    {
                        var bytes = Encoding.UTF8.GetBytes(HtmlBody);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                }
                catch { }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }, ct);

        return port;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;

            if (root.TryGetProperty("ready", out _) && !_hostedPageReady)
            {
                _hostedPageReady = true;
                OAuthWebView.CoreWebView2?.PostWebMessageAsString(
                    JsonSerializer.Serialize(new { action = "signIn", providerId = _providerId }));
                return;
            }

            if (root.TryGetProperty("error", out var err))
            {
                CompleteWithError(err.GetString() ?? "Sign-in failed.");
                return;
            }

            if (root.TryGetProperty("success", out _))
            {
                _completion.TrySetResult(new OAuthResult(
                    Success: true,
                    IdToken: root.GetProperty("idToken").GetString() ?? "",
                    RefreshToken: root.GetProperty("refreshToken").GetString() ?? "",
                    Uid: root.GetProperty("uid").GetString() ?? "",
                    Email: root.GetProperty("email").GetString() ?? "",
                    DisplayName: root.GetProperty("displayName").GetString() ?? "",
                    PhotoUrl: root.TryGetProperty("photoUrl", out var ph) ? ph.GetString() : null,
                    ExpiresInSec: root.TryGetProperty("expiresInSec", out var exp) ? exp.GetInt32() : 3600,
                    ErrorMessage: null));
                Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        catch (Exception ex)
        {
            CompleteWithError($"Sign-in response was malformed: {ex.Message}");
        }
    }

    private void CompleteWithError(string message)
    {
        _completion.TrySetResult(new OAuthResult(false, "", "", "", "", "", null, 0, message));
        Dispatcher.BeginInvoke(new Action(Close));
    }

    private const string HtmlBody = """
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8" />
    <title>AutoCode Studio — Sign in</title>
    <style>
        html, body { margin: 0; height: 100%; background: #1c1b19; color: #b5b0a6;
            font-family: 'Segoe UI', system-ui, sans-serif; display: flex; align-items: center; justify-content: center; }
        #status { font-size: 14px; text-align: center; }
    </style>
</head>
<body>
    <div id="status">Preparing sign-in…</div>
    <script type="module">
        import { initializeApp } from "https://www.gstatic.com/firebasejs/10.12.2/firebase-app.js";
        import { getAuth, GoogleAuthProvider, OAuthProvider, signInWithPopup, browserLocalPersistence, setPersistence }
            from "https://www.gstatic.com/firebasejs/10.12.2/firebase-auth.js";

        const firebaseConfig = {
            apiKey: "AIzaSyC6Lvc_-OUKvL4j_coQIspcPIHO2k5xKpk",
            authDomain: "bvrai-prod.firebaseapp.com",
            projectId: "bvrai-prod",
            appId: "1:121998082522:web:dd366e3fd191e1554dc0e3"
        };
        const app = initializeApp(firebaseConfig);
        const auth = getAuth(app);
        const statusEl = document.getElementById('status');
        function setStatus(m) { statusEl.textContent = m; }
        function post(o) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(o)); }

        function buildProvider(id) {
            if (id === 'google.com') { const p = new GoogleAuthProvider(); p.addScope('email'); p.addScope('profile'); p.setCustomParameters({ prompt: 'select_account' }); return p; }
            if (id === 'microsoft.com') { const p = new OAuthProvider('microsoft.com'); p.addScope('email'); p.addScope('profile'); p.setCustomParameters({ prompt: 'select_account' }); return p; }
            return null;
        }
        async function beginSignIn(id) {
            const provider = buildProvider(id);
            if (!provider) { post({ error: 'Unknown provider: ' + id }); return; }
            try {
                setStatus('Opening ' + id + '…');
                await setPersistence(auth, browserLocalPersistence);
                const result = await signInWithPopup(auth, provider);
                const user = result.user;
                const idToken = await user.getIdToken();
                setStatus('Signed in. Returning to AutoCode Studio…');
                post({ success: true, idToken, refreshToken: user.refreshToken, uid: user.uid,
                    email: user.email || '', displayName: user.displayName || '', photoUrl: user.photoURL || '',
                    expiresInSec: 3600, providerId: id });
            } catch (ex) { post({ error: ex.message || String(ex), code: ex.code || null }); }
        }
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', (event) => {
                try { const d = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
                    if (d.action === 'signIn' && d.providerId) beginSignIn(d.providerId); }
                catch (e) { post({ error: 'Bad message: ' + e.message }); }
            });
        }
        post({ ready: true });
        setStatus('Ready');
    </script>
</body>
</html>
""";
}

public record OAuthResult(
    bool Success,
    string IdToken,
    string RefreshToken,
    string Uid,
    string Email,
    string DisplayName,
    string? PhotoUrl,
    int ExpiresInSec,
    string? ErrorMessage)
{
    public static OAuthResult Canceled => new(false, "", "", "", "", "", null, 0, "Sign-in was canceled.");
}
