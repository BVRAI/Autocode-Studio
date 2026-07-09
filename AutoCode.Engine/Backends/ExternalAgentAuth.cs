namespace AutoCode.Engine.Backends;

/// <summary>
/// Resolved auth for an external CLI agent backend. <c>Mode</c> is <c>"subscription"</c> (default —
/// the child env is scrubbed of API keys so the CLI uses its own login session) or <c>"api-key"</c>
/// (the given key is injected into the child env). Each backend owns its env-var names; this record
/// carries only the resolved decision, not configuration.
/// </summary>
public sealed record ExternalAgentAuth(string Mode, string? ApiKey)
{
    public const string SubscriptionMode = "subscription";
    public const string ApiKeyMode = "api-key";

    public static readonly ExternalAgentAuth Subscription = new(SubscriptionMode, null);

    public bool UsesApiKey => Mode == ApiKeyMode && !string.IsNullOrWhiteSpace(ApiKey);
}
