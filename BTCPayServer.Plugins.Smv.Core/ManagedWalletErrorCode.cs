namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// The closed set of Managed Wallet API error codes: the sealed v1.1 set
/// (contract v1.1 §3.3) plus the additive v1.2 mint-specific codes
/// (contract v1.2 §6). Free-text codes are forbidden by the contract; any
/// unrecognised or missing code MUST map to <see cref="ServerError"/> (fail
/// closed) — the plugin never treats an unknown upstream failure as success.
///
/// Lives in Core (host-independent) so the wire-code mapping is unit-testable
/// without compiling the BTCPay host.
/// </summary>
public enum ManagedWalletErrorCode
{
    // v1.1 (contract §3.3)
    Unauthorized,
    InsufficientScope,
    NotFound,
    InvalidRequest,
    InvalidPath,
    AssetNotFound,
    InsufficientBalance,
    DepositExpired,
    RateLimited,
    PaymentRequired,
    IdempotencyConflict,
    // Same Idempotency-Key, SAME body, but the first request is still being
    // processed (concurrent duplicate). Distinct from IdempotencyConflict
    // (same key, different body). Transient/retriable — never terminal.
    IdempotencyInFlight,
    ServerError,

    // v1.2 mint-specific (contract §6). InsufficientCredit is reserved for the
    // v1.3 credit-only mint (not raised in v1.2) but modelled so its wire code
    // maps to a distinct value rather than failing closed to ServerError.
    QuoteExpired,
    QuoteNotFound,
    FeeTooHigh,
    ImageFetchFailed,
    ImageTooLarge,
    CollectionFull,
    SupplyExceeded,
    InsufficientCredit,
    MintFailed,
    TapdUnavailable
}

public static class ManagedWalletErrorCodes
{
    /// <summary>
    /// Maps a wire error-code string (contract §3.3) to the closed enum. Any
    /// unrecognised or null code returns <see cref="ManagedWalletErrorCode.ServerError"/>
    /// (fail closed).
    /// </summary>
    public static ManagedWalletErrorCode Parse(string? code) => code switch
    {
        // v1.1 (contract §3.3)
        "unauthorized"         => ManagedWalletErrorCode.Unauthorized,
        "insufficient_scope"   => ManagedWalletErrorCode.InsufficientScope,
        "not_found"            => ManagedWalletErrorCode.NotFound,
        "invalid_request"      => ManagedWalletErrorCode.InvalidRequest,
        "invalid_path"         => ManagedWalletErrorCode.InvalidPath,
        "asset_not_found"      => ManagedWalletErrorCode.AssetNotFound,
        "insufficient_balance" => ManagedWalletErrorCode.InsufficientBalance,
        "deposit_expired"      => ManagedWalletErrorCode.DepositExpired,
        "rate_limited"         => ManagedWalletErrorCode.RateLimited,
        "payment_required"     => ManagedWalletErrorCode.PaymentRequired,
        "idempotency_conflict"  => ManagedWalletErrorCode.IdempotencyConflict,
        "idempotency_in_flight" => ManagedWalletErrorCode.IdempotencyInFlight,
        "server_error"         => ManagedWalletErrorCode.ServerError,

        // v1.2 mint-specific (contract §6)
        "quote_expired"        => ManagedWalletErrorCode.QuoteExpired,
        "quote_not_found"      => ManagedWalletErrorCode.QuoteNotFound,
        "fee_too_high"         => ManagedWalletErrorCode.FeeTooHigh,
        "image_fetch_failed"   => ManagedWalletErrorCode.ImageFetchFailed,
        "image_too_large"      => ManagedWalletErrorCode.ImageTooLarge,
        "collection_full"      => ManagedWalletErrorCode.CollectionFull,
        "supply_exceeded"      => ManagedWalletErrorCode.SupplyExceeded,
        "insufficient_credit"  => ManagedWalletErrorCode.InsufficientCredit,
        "mint_failed"          => ManagedWalletErrorCode.MintFailed,
        "tapd_unavailable"     => ManagedWalletErrorCode.TapdUnavailable,

        _                      => ManagedWalletErrorCode.ServerError
    };
}
