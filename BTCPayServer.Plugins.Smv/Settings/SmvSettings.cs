namespace BTCPayServer.Plugins.Smv.Settings;

/// <summary>
/// Persisted plugin settings. Stored via BTCPayServer.Abstractions.Contracts.ISettingsRepository.
/// Track A stores only public endpoint configuration.
/// Track B adds tapd connection settings. A read-only macaroon suffices for list/receive/send;
/// BYON Create (RFC-PLUGIN-006) requires a mint-capable macaroon.
/// </summary>
public class SmvSettings
{
    public const string DefaultApiBase = "https://api.supermultiverse.io/v1/public";
    public const string DefaultProofDecodeEndpoint = "https://rrpcyevnteqkmvtdbnkk.supabase.co/functions/v1/tapd-decode-proof";

    public string SmvPublicApiBase { get; set; } = DefaultApiBase;
    public string? StasProofDecodeEndpoint { get; set; } = DefaultProofDecodeEndpoint;
    public int SmvHttpTimeoutMs { get; set; } = 8000;
    public int SmvCacheTtlSeconds { get; set; } = 86400;
    public int SmvProofMaxBytes { get; set; } = 262144;

    // Track B v0.1.5 Read-Only Wallet settings.
    // No private keys are ever stored. TapdMacaroonHex is read-only for list/receive/send;
    // BYON Create needs a mint-capable macaroon (RFC-PLUGIN-006 P2-1).
    public string? TapdBaseUrl { get; set; }
    public string? TapdMacaroonHex { get; set; }
    public string? TapdTlsCert { get; set; }
    public int TapdHttpTimeoutMs { get; set; } = 8000;

    // Optional Bitcoin Core RPC connection, used only to report on-chain
    // confirmation count for an already-broadcast transfer (send status polling).
    // Null/empty = not configured. When not configured, confirmation tracking
    // degrades safely (the transfer is reported as broadcast/pending) instead of
    // failing or relying on credentials baked into source. Never hardcode these.
    public string? BitcoinRpcUrl { get; set; }
    public string? BitcoinRpcUser { get; set; }
    public string? BitcoinRpcPassword { get; set; }

    /// <summary>True only when all three Bitcoin RPC fields are present.</summary>
    public bool HasBitcoinRpc =>
        !string.IsNullOrWhiteSpace(BitcoinRpcUrl) &&
        !string.IsNullOrWhiteSpace(BitcoinRpcUser) &&
        !string.IsNullOrWhiteSpace(BitcoinRpcPassword);

    public static SmvSettings Defaults() => new();
}