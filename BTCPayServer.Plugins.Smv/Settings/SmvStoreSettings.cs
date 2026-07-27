using BTCPayServer.Plugins.Smv.Core;

namespace BTCPayServer.Plugins.Smv.Settings;

/// <summary>
/// Store-scoped backend configuration (P2). This is the per-Store subset of the
/// former global <see cref="SmvSettings"/>: only the fields needed to reach a
/// Store's own tapd/Bitcoin backend, plus the Hosted backend selection (P3).
///
/// Persisted per Store via <c>IStoreRepository</c> under <see cref="SettingName"/>.
/// Credential fields (<see cref="TapdMacaroonHex"/>, <see cref="BitcoinRpcPassword"/>,
/// <see cref="HostedApiToken"/>) are protected at rest by
/// <c>ISmvStoreSettingsProvider</c>; plaintext is never written to storage
/// (RFC §9.2/§9.3, TD §3.3).
/// </summary>
public class SmvStoreSettings
{
    /// <summary>
    /// Per-Store settings key. Defined once here; call sites must never inline the literal.
    /// </summary>
    public const string SettingName = "BTCPayServer.Plugins.Smv.Settings.v1";

    /// <summary>
    /// Which backend this Store targets (P3). Defaults to
    /// <see cref="SmvBackendMode.Byon"/>; a record persisted before P3 (no such
    /// field) deserializes to Byon, so no existing Store changes behaviour. In
    /// P3-H0 this is persisted but not yet consumed by the resolver (inert).
    /// </summary>
    public SmvBackendMode BackendMode { get; set; } = SmvBackendMode.Byon;

    // tapd connection (BYON). TapdMacaroonHex is a credential and is protected at rest.
    public string? TapdBaseUrl { get; set; }
    public string? TapdMacaroonHex { get; set; }
    public string? TapdTlsCert { get; set; }
    public int TapdHttpTimeoutMs { get; set; } = 8000;

    // Optional Bitcoin Core RPC connection, used only to report on-chain
    // confirmation count for an already-broadcast transfer. BitcoinRpcPassword is
    // a credential and is protected at rest. Null/empty = not configured.
    public string? BitcoinRpcUrl { get; set; }
    public string? BitcoinRpcUser { get; set; }
    public string? BitcoinRpcPassword { get; set; }

    /// <summary>True only when all three Bitcoin RPC fields are present.</summary>
    public bool HasBitcoinRpc =>
        !string.IsNullOrWhiteSpace(BitcoinRpcUrl) &&
        !string.IsNullOrWhiteSpace(BitcoinRpcUser) &&
        !string.IsNullOrWhiteSpace(BitcoinRpcPassword);

    // Hosted backend (P3). HostedApiToken is the mwv1_ bearer token — a credential
    // protected at rest exactly like TapdMacaroonHex. Null/empty = not configured.
    // Per the Managed Wallet API contract §2, the token alone is the wallet
    // identity; the plugin never stores or sends wallet_id/user_id.
    //
    // OAuth Connect (RFC-PLUGIN-007) reuses this same field to hold the mwv1_ obtained
    // via the flow — for BOTH Hosted and BYON — so downstream code is unchanged.
    public string? HostedApiToken { get; set; }

    // ── OAuth Connect (RFC-PLUGIN-007). Per-Store. OAuthRefreshToken is a credential
    // (protected at rest like the others); the rest are non-secret bookkeeping. The
    // mwv1_ bearer itself lives in HostedApiToken above. ──────────────────────────────
    /// <summary>This Store's registered OAuth client_id (DCR, once per Store — §11.7 R1).</summary>
    public string? OAuthClientId { get; set; }
    /// <summary>Rotating OAuth refresh token (credential, protected at rest).</summary>
    public string? OAuthRefreshToken { get; set; }
    /// <summary>Granted capabilities from the exchange, comma-joined (display + preflight).</summary>
    public string? OAuthScopes { get; set; }
    /// <summary>mwv1_ expiry as unix seconds — drives proactive refresh. Null = unknown.</summary>
    public long? OAuthTokenExpiresAtUnix { get; set; }
    /// <summary>Display label of the connected SMV account (account_label, e.g. email).</summary>
    public string? OAuthConnectedAccount { get; set; }
    /// <summary>token_id from the exchange — for revoke-self + support.</summary>
    public string? OAuthTokenId { get; set; }
    /// <summary>wallet_id from the exchange — for support/audit.</summary>
    public string? OAuthWalletId { get; set; }

    /// <summary>The backend mode the OAuth connection's capabilities were granted for
    /// (capabilities are mode-specific — Hosted requests mint, BYON requests
    /// register_external). If this differs from <see cref="BackendMode"/> the merchant
    /// switched backend and must reconnect to update permissions (§11.8 R4). Null when
    /// not connected.</summary>
    public SmvBackendMode? OAuthConnectedMode { get; set; }

    /// <summary>BYON (RFC-006 B4): JSON array of signed mints whose SMV registration is
    /// still pending (the proof isn't exportable until the anchor confirms). Completed
    /// automatically by ByonRegistrationService from the create-page status poll and
    /// My BDOs loads — the merchant never has to act. Each entry carries the exact
    /// canonical metadata bytes + the signed event, so completion never recomputes.</summary>
    public string? PendingByonRegistrationsJson { get; set; }

    /// <summary>How this Store's session was established (RFC-PLUGIN-008): "sso" = the
    /// OAuth authorize/consent flow (refreshes at the OAuth server's /oauth/token);
    /// "embedded" = the native sign-in/sign-up form (password grant; refreshes at the
    /// GoTrue /token endpoint). Null on records persisted before RFC-008 = "sso".</summary>
    public string? OAuthSessionKind { get; set; }

    /// <summary>True when the session came from the embedded account form.</summary>
    public bool IsEmbeddedSession => OAuthSessionKind == "embedded";

    /// <summary>True when an OAuth Connect session is established (refresh token present).</summary>
    public bool HasOAuthConnection => !string.IsNullOrWhiteSpace(OAuthRefreshToken);

    /// <summary>True when connected but the backend mode changed since — the token's
    /// capabilities are for the old mode, so a reconnect is needed.</summary>
    public bool OAuthModeMismatch =>
        HasOAuthConnection && OAuthConnectedMode is { } m && m != BackendMode;

    /// <summary>True when this Store is configured to use the Hosted backend.</summary>
    public bool IsHosted => BackendMode == SmvBackendMode.Hosted;

    /// <summary>True only when Hosted is selected and a token is present.</summary>
    public bool HasHostedToken =>
        BackendMode == SmvBackendMode.Hosted && !string.IsNullOrWhiteSpace(HostedApiToken);

    /// <summary>Gating matrix (2026-07-26): Hosted mode with NO account/token — in
    /// Hosted the account IS the wallet, so every wallet surface (My BDOs, Create,
    /// Receive, Send) gates on sign-in instead of failing mid-flow. Verify and
    /// Settings stay open. BYON never gates on the account for node surfaces.</summary>
    public bool IsHostedNotConnected => IsHosted && string.IsNullOrWhiteSpace(HostedApiToken);

    /// <summary>Whether the connection's granted capabilities include <paramref name="scope"/>.
    /// True/false when the grant is known (OAuth connections store it); NULL when unknown
    /// (manual tokens don't carry scopes) — callers treat null as "let the API decide",
    /// never as a denial.</summary>
    public bool? HasGrantedScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(OAuthScopes)) return null;
        foreach (var s in OAuthScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(s, scope, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
