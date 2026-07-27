namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Abstraction over the asset ownership backend. In P1 the only implementation
/// is <c>TapdAssetBackend</c> (BYON), which wraps the existing tapd client.
/// A future <c>SmvHostedAssetBackend</c> implements the same contract so the
/// controllers and views never change per backend.
///
/// Implementations may own a per-request <see cref="System.Net.Http.HttpClient"/>,
/// so callers dispose the backend (<c>using var backend = await resolver.ResolveAsync()</c>).
/// </summary>
public interface IAssetBackend : System.IDisposable
{
    /// <summary>Human label for the active connection (BYON: tapd base URL; Hosted: store/account).</summary>
    string? ConnectionLabel { get; }

    /// <summary>
    /// True when the backend custodies the keys (Hosted); false for self-custody
    /// (BYON). Drives honest custody labelling and disabling Receive under Hosted
    /// (RFC-PLUGIN-003 §4/§10). Cheap/constant — never does I/O.
    /// </summary>
    bool IsCustodial { get; }

    Task<IReadOnlyList<OwnedAsset>> ListAssetsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingIncomingAsset>> ListPendingIncomingAsync(CancellationToken cancellationToken = default);

    Task<ReceiveAddress> CreateReceiveAddressAsync(ReceiveRequest request, CancellationToken cancellationToken = default);

    Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default);

    Task<BackendInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Status of a prior send. <paramref name="transferRef"/> is the txid (BYON) or
    /// the transfer_ref (Hosted). BYON reads Bitcoin Core RPC for the confirmation
    /// count; Hosted reads the Managed Wallet transfer-status endpoint. Both fail
    /// safe to a "pending / broadcast" status rather than throwing (P3-H2, RFC §8).
    /// </summary>
    Task<SendStatus> GetSendStatusAsync(string transferRef, CancellationToken cancellationToken = default);

    // ── Issuance (RFC-PLUGIN-004 / Managed Wallet API v1.2) ────────────────────
    // Hosted-only, collectibles-only in v1.2. The Create surface renders only when
    // IsCustodial is true; BYON throws SelfCustodyMintNotAvailableException on all
    // four (Track B), never a raw 500.

    /// <summary>The merchant's collections, for the reuse-or-create picker (contract §4).</summary>
    Task<IReadOnlyList<MintCollection>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stateless cost estimate for a mint of the given dimensions (contract §5.1).</summary>
    Task<MintQuote> MintQuoteAsync(MintQuoteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit a mint (contract §5.2). Generates a fresh idempotency key per attempt
    /// and returns the inline LN fee invoice to pay. Poll <see cref="GetMintStatusAsync"/>
    /// to completion.
    /// </summary>
    Task<MintResult> MintAsync(MintRequest request, CancellationToken cancellationToken = default);

    /// <summary>Status of an in-flight or finished mint (contract §5.3).</summary>
    Task<MintStatus> GetMintStatusAsync(string mintRef, CancellationToken cancellationToken = default);

    // ── My BDOs listing Phase 2 (RFC-PLUGIN-005) ───────────────────────────────

    /// <summary>My BDOs Level 1: the merchant's held collections (owned_count vs
    /// collection_size). Hosted-only — BYON keeps the client-side grouping and throws.</summary>
    Task<IReadOnlyList<HeldCollection>> ListHeldCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>My BDOs Level 2: a collection's held units, cursor-paginated + searchable.
    /// Hosted-only. <paramref name="cursor"/> is the opaque marker from the prior page.</summary>
    Task<HeldUnitsPage> ListHeldUnitsAsync(
        string collectionId, int? limit, string? cursor, string? q, string? sort,
        CancellationToken cancellationToken = default);

    // ── Batch mint (RFC_BATCH_MINTING_V1, Modality 3 — the moat) ────────────────

    /// <summary>Submit a batch of N unique collectibles. Async: returns the aggregate LN
    /// fee invoice inline; poll <see cref="GetMintBatchStatusAsync"/> to completion.</summary>
    Task<MintBatchResult> MintBatchAsync(MintBatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Status of an in-flight or finished batch.</summary>
    Task<MintBatchStatus> GetMintBatchStatusAsync(string batchRef, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the active <see cref="IAssetBackend"/> for a specific Store (P2/C3).
/// Reads that Store's settings via <c>ISmvStoreSettingsProvider</c> and returns a
/// TapdAssetBackend, or <c>null</c> when the Store is not configured. It never
/// reads a global record and never enumerates Stores (TD §3.1, E3–E6).
/// </summary>
public interface IAssetBackendResolver
{
    /// <summary>
    /// Returns the configured backend for <paramref name="storeId"/>, or
    /// <c>null</c> if that Store is not configured. Throws when
    /// <paramref name="storeId"/> is null/empty (E6).
    /// </summary>
    Task<IAssetBackend?> ResolveAsync(string storeId, CancellationToken cancellationToken = default);
}
