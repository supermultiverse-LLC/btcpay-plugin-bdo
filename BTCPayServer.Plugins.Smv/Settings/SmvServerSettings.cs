namespace BTCPayServer.Plugins.Smv.Settings;

/// <summary>
/// Server-global, public-surface settings (P2/C4, F1). This is the reduced form
/// of the legacy <see cref="SmvSettings"/> record: only the fields the public
/// Verify / proof surfaces consume. Persisted under the unchanged key
/// <c>Smv.Settings</c> via <c>ISettingsRepository</c>.
///
/// Newtonsoft (BTCPay's settings serializer) ignores unknown fields, so the old
/// full record deserializes into this subset unchanged — Verify keeps working
/// both before the migration (full record) and after it (reduced record).
/// No credential is ever part of this record.
/// </summary>
public class SmvServerSettings
{
    public const string DefaultApiBase = "https://api.supermultiverse.io/v1/public";
    public const string DefaultProofDecodeEndpoint = "https://rrpcyevnteqkmvtdbnkk.supabase.co/functions/v1/tapd-decode-proof";

    // Managed Wallet API v1.1 base (Hosted backend, P3). Server-global default,
    // overridable for dev/lab. Derived from the same Supabase project the proof-
    // decode endpoint already uses; the exact canonical gateway host is confirmed
    // at H1 certification before any live Hosted call ships.
    public const string DefaultHostedApiBase = "https://rrpcyevnteqkmvtdbnkk.supabase.co/functions/v1";

    // OAuth Connect authorization server (RFC-PLUGIN-007). This is the SEALED, permanent
    // OAuth issuer (§11.7 R2) — the `iss` claim + JWKS host. It is DECOUPLED from
    // HostedApiBase (the edge-functions host): the gateway may move, the issuer may not,
    // or every connected Store's JWT validation breaks. Overridable for dev/lab only.
    public const string DefaultOAuthIssuerBase = "https://rrpcyevnteqkmvtdbnkk.supabase.co/auth/v1";

    // Supabase anon (publishable) key — required by the GoTrue password/refresh
    // endpoints and sent as `apikey` on plugin-signup (RFC-PLUGIN-008). This is the
    // PUBLIC key already shipped in the SMV web bundle; it is not a credential.
    public const string DefaultSupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InJycGN5ZXZudGVxa212dGRibmtrIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzEyNjkyOTksImV4cCI6MjA4Njg0NTI5OX0.Va4v012swpI_RqVB97kQ3WVPVIAOmgIiTQT-dPUjF78";

    public string SmvPublicApiBase { get; set; } = DefaultApiBase;
    public string SupabaseAnonKey { get; set; } = DefaultSupabaseAnonKey;
    public string? StasProofDecodeEndpoint { get; set; } = DefaultProofDecodeEndpoint;
    public string HostedApiBase { get; set; } = DefaultHostedApiBase;
    public string OAuthIssuerBase { get; set; } = DefaultOAuthIssuerBase;
    public int SmvHttpTimeoutMs { get; set; } = 8000;
    public int SmvCacheTtlSeconds { get; set; } = 86400;
    public int SmvProofMaxBytes { get; set; } = 262144;

    public static SmvServerSettings Defaults() => new();
}
