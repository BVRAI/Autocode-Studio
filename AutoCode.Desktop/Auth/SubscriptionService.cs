using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AutoCode.Desktop.Auth;

public sealed record SubscriptionStatus(bool IsActive, string? Plan, string? Reason);

/// <summary>
/// Reads the Firestore `subscriptions/{uid}` doc (REST, with the Firebase ID token) to decide
/// whether the signed-in user may use the proxy. Ported/simplified from Automax v6. 5-min cache.
/// </summary>
public sealed class SubscriptionService
{
    private const string ProjectId = "bvrai-prod";
    private const string BaseUrl = "https://firestore.googleapis.com/v1/projects/bvrai-prod/databases/(default)/documents";

    private static readonly HttpClient Http = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, (SubscriptionStatus Status, DateTime ExpiryUtc)> _cache = new();

    public async Task<SubscriptionStatus> GetStatusAsync(string? uid, string? idToken)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(idToken))
        {
            return new SubscriptionStatus(false, null, "Not signed in.");
        }

        if (_cache.TryGetValue(uid, out var cached) && DateTime.UtcNow < cached.ExpiryUtc)
        {
            return cached.Status;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/subscriptions/{uid}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            var response = await Http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Cache(uid, new SubscriptionStatus(false, null, "No subscription found for this account."));
            }

            if (!response.IsSuccessStatusCode)
            {
                if (_cache.TryGetValue(uid, out var stale) && stale.Status.IsActive)
                {
                    return stale.Status;
                }

                return new SubscriptionStatus(false, null, "Couldn't verify subscription.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return Cache(uid, Parse(doc.RootElement));
        }
        catch
        {
            if (_cache.TryGetValue(uid, out var stale))
            {
                return stale.Status;
            }

            return new SubscriptionStatus(false, null, "Couldn't verify subscription.");
        }
    }

    private SubscriptionStatus Cache(string uid, SubscriptionStatus status)
    {
        _cache[uid] = (status, DateTime.UtcNow + CacheDuration);
        return status;
    }

    private static SubscriptionStatus Parse(JsonElement root)
    {
        if (!root.TryGetProperty("fields", out var fields))
        {
            return new SubscriptionStatus(false, null, "Malformed subscription record.");
        }

        var active = fields.TryGetProperty("active", out var a) && a.TryGetProperty("booleanValue", out var av) && av.GetBoolean();
        string? plan = fields.TryGetProperty("plan", out var p) && p.TryGetProperty("stringValue", out var pv) ? pv.GetString() : null;

        DateTime? expiresAt = null;
        if (fields.TryGetProperty("expiresAt", out var e) && e.TryGetProperty("timestampValue", out var ev) && DateTime.TryParse(ev.GetString(), out var exp))
        {
            expiresAt = exp.ToUniversalTime();
        }

        if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow)
        {
            return new SubscriptionStatus(false, plan, "Your subscription has expired.");
        }

        return active
            ? new SubscriptionStatus(true, plan, null)
            : new SubscriptionStatus(false, plan, "Subscription is inactive.");
    }
}
