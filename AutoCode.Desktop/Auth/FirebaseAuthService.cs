using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoCode.Engine.Auth;

namespace AutoCode.Desktop.Auth;

public sealed class FirebaseAuthException : Exception
{
    public FirebaseAuthException(string message, string? code) : base(message) => Code = code;

    public string? Code { get; }
}

/// <summary>
/// Firebase Identity Toolkit auth (email/password + external credential), ported from Automax v6.
/// Session is DPAPI-encrypted on disk; the ID token is proactively refreshed so the synchronous
/// <see cref="CurrentIdToken"/> accessor (used as the proxy Bearer) stays valid.
/// </summary>
public sealed class FirebaseAuthService
{
    // Public Firebase Web API key for project "bvrai-prod" (identifies the project, not the user).
    private const string ApiKey = "AIzaSyC6Lvc_-OUKvL4j_coQIspcPIHO2k5xKpk";

    private const string SignUpUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=";
    private const string SignInPasswordUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=";
    private const string PasswordResetUrl = "https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key=";
    private const string UpdateProfileUrl = "https://identitytoolkit.googleapis.com/v1/accounts:update?key=";
    private const string RefreshUrl = "https://securetoken.googleapis.com/v1/token?key=";

    private static readonly HttpClient Http = new();

    private readonly string _sessionFilePath;
    private Session? _session;
    private CancellationTokenSource? _refreshCts;

    public FirebaseAuthService()
    {
        var dir = Paths.DataDirectory();
        Directory.CreateDirectory(dir);
        _sessionFilePath = Path.Combine(dir, "firebase_session.dat"); // encrypted (.dat, not .json)
    }

    public event Action? StateChanged;

    public bool IsAuthenticated => _session != null;
    public string? CurrentUid => _session?.LocalId;
    public string? CurrentEmail => _session?.Email;
    public string? CurrentDisplayName => _session?.DisplayName;
    public string? CurrentPhotoUrl => _session?.PhotoUrl;
    public string? CurrentProvider => _session?.Provider;

    /// <summary>Synchronous accessor for the current ID token (kept fresh by the refresh loop).</summary>
    public string? CurrentIdToken => _session?.IdToken;

    public async Task<string?> GetIdTokenAsync()
    {
        if (_session == null)
        {
            return null;
        }

        if (DateTime.UtcNow >= _session.ExpiresAtUtc.AddMinutes(-5))
        {
            if (!await TryRefreshAsync())
            {
                return null;
            }
        }

        return _session?.IdToken;
    }

    public async Task<bool> TryRestoreSessionAsync()
    {
        _session = LoadSessionFromDisk();
        if (_session == null)
        {
            return false;
        }

        var ok = DateTime.UtcNow < _session.ExpiresAtUtc.AddMinutes(-5) || await TryRefreshAsync();
        if (ok)
        {
            StartRefreshLoop();
            StateChanged?.Invoke();
        }

        return ok;
    }

    public Task SignOutAsync()
    {
        _refreshCts?.Cancel();
        _session = null;
        try { if (File.Exists(_sessionFilePath)) File.Delete(_sessionFilePath); } catch { /* best-effort */ }
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public async Task SignInWithEmailPasswordAsync(string email, string password)
    {
        var json = await PostAsync(SignInPasswordUrl, JsonSerializer.Serialize(new { email, password, returnSecureToken = true }));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _session = new Session
        {
            IdToken = root.GetProperty("idToken").GetString()!,
            RefreshToken = root.GetProperty("refreshToken").GetString()!,
            LocalId = root.GetProperty("localId").GetString()!,
            Email = TryGetString(root, "email") ?? email,
            DisplayName = TryGetString(root, "displayName") ?? "",
            Provider = "password",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(int.Parse(root.GetProperty("expiresIn").GetString()!)),
        };
        SaveSessionToDisk(_session);
        StartRefreshLoop();
        StateChanged?.Invoke();
    }

    public async Task SignUpWithEmailPasswordAsync(string email, string password)
    {
        var json = await PostAsync(SignUpUrl, JsonSerializer.Serialize(new { email, password, returnSecureToken = true }));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _session = new Session
        {
            IdToken = root.GetProperty("idToken").GetString()!,
            RefreshToken = root.GetProperty("refreshToken").GetString()!,
            LocalId = root.GetProperty("localId").GetString()!,
            Email = TryGetString(root, "email") ?? email,
            DisplayName = "",
            Provider = "password",
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(int.Parse(root.GetProperty("expiresIn").GetString()!)),
        };
        SaveSessionToDisk(_session);
        StartRefreshLoop();
        StateChanged?.Invoke();
    }

    public async Task SendPasswordResetEmailAsync(string email)
        => await PostAsync(PasswordResetUrl, JsonSerializer.Serialize(new { requestType = "PASSWORD_RESET", email }));

    /// <summary>Accept a credential obtained from the external (Google) WebView2 sign-in.</summary>
    public Task AcceptExternalCredentialAsync(string idToken, string refreshToken, string uid, string email, string displayName, string? photoUrl, int expiresInSec, string provider)
    {
        _session = new Session
        {
            IdToken = idToken,
            RefreshToken = refreshToken,
            LocalId = uid,
            Email = email,
            DisplayName = displayName,
            PhotoUrl = photoUrl,
            Provider = provider,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresInSec),
        };
        SaveSessionToDisk(_session);
        StartRefreshLoop();
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    private void StartRefreshLoop()
    {
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && _session != null)
            {
                var delay = _session.ExpiresAtUtc.AddMinutes(-5) - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) { delay = TimeSpan.Zero; }
                try { await Task.Delay(delay, ct); } catch { return; }
                if (ct.IsCancellationRequested) { return; }
                await TryRefreshAsync();
            }
        }, ct);
    }

    private async Task<bool> TryRefreshAsync()
    {
        var session = _session;
        if (session == null)
        {
            return false;
        }

        try
        {
            var content = new StringContent(
                $"grant_type=refresh_token&refresh_token={session.RefreshToken}",
                Encoding.UTF8, "application/x-www-form-urlencoded");
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response = await Http.PostAsync(RefreshUrl + ApiKey, content, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                await SignOutAsync();
                return false;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            session.IdToken = root.GetProperty("id_token").GetString()!;
            session.RefreshToken = root.GetProperty("refresh_token").GetString()!;
            session.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(int.Parse(root.GetProperty("expires_in").GetString()!));
            SaveSessionToDisk(session);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> PostAsync(string urlWithoutKey, string jsonBody)
    {
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(urlWithoutKey + ApiKey, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var code = ExtractFirebaseError(body);
            throw new FirebaseAuthException(Humanize(code), code);
        }

        return body;
    }

    private Session? LoadSessionFromDisk()
    {
        if (!File.Exists(_sessionFilePath))
        {
            return null;
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(_sessionFilePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Session>(Encoding.UTF8.GetString(decrypted));
        }
        catch
        {
            try { File.Delete(_sessionFilePath); } catch { }
            return null;
        }
    }

    private void SaveSessionToDisk(Session session)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session));
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_sessionFilePath, encrypted);
    }

    private static string? ExtractFirebaseError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                return TryGetString(err, "message");
            }
        }
        catch { }
        return null;
    }

    private static string Humanize(string? code) => code switch
    {
        "EMAIL_EXISTS" => "An account already exists with this email.",
        "EMAIL_NOT_FOUND" => "No account found with this email.",
        "INVALID_PASSWORD" => "Incorrect password.",
        "INVALID_LOGIN_CREDENTIALS" => "Email or password is incorrect.",
        "USER_DISABLED" => "This account has been disabled.",
        "INVALID_EMAIL" => "That doesn't look like a valid email address.",
        "MISSING_EMAIL" => "Please enter your email.",
        "MISSING_PASSWORD" => "Please enter your password.",
        "TOO_MANY_ATTEMPTS_TRY_LATER" => "Too many failed attempts. Please wait a few minutes and try again.",
        null => "Authentication failed. Please try again.",
        _ when code.StartsWith("WEAK_PASSWORD", StringComparison.Ordinal) => "Password must be at least 6 characters.",
        _ => $"Authentication failed ({code}).",
    };

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Session
    {
        public string IdToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string LocalId { get; set; } = "";
        public string Email { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? PhotoUrl { get; set; }
        public string Provider { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
    }
}
