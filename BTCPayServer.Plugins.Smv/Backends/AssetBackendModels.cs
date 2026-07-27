namespace BTCPayServer.Plugins.Smv.Backends;

// Backend-neutral DTOs. Both TapdAssetBackend (BYON) and, later,
// SmvHostedAssetBackend map their native payloads into these shapes.
// Views bind to the existing Services.Tapd view models in P1 (controllers
// adapt these DTOs back), so introducing this layer is UX-neutral.

/// <summary>One STAS-01 attribute (trait/value pair) from the asset's minted metadata.</summary>
public sealed record AssetAttribute(string TraitType, string Value);

public sealed record OwnedAsset(
    string? AssetId,            // 64-hex Taproot Asset id
    string? SmvId,              // SMV UUID (Hosted); null in BYON
    string? Name,
    string? Type,
    string? Amount,             // decimal string
    string? AnchorOutpoint = null,
    string? ImageUrl = null,        // Hosted: Public API; BYON: decoded from the node's own asset_meta
    string? Collection = null,      // collection display name; Hosted enriches; null in BYON
    string? CollectionSlug = null,  // collection slug — the grouping key for the My BDOs listing (RFC-PLUGIN-005)
    string? ImageIpfsUrl = null,    // IPFS gateway URL (image permanence); enriched from the Public API
    string? ImageIpfsCid = null,    // IPFS CID; enriched from the Public API
    // BYON local enrichment (RFC-PLUGIN-006): decoded from the asset's own on-chain
    // STAS-01 metadata (asset_meta.data) — sovereign, no platform dependency.
    string? Description = null,
    string? ExternalUrl = null,
    IReadOnlyList<AssetAttribute>? Attributes = null,
    // Mint anchor tx still in the mempool (BYON): listed so the merchant sees the
    // mint immediately, but not sendable/registerable until the block confirms.
    bool IsConfirming = false,
    // Anchor confirmation height (BYON) — recency key for the listing; 0 while
    // confirming or when the node doesn't populate it.
    long AnchorBlockHeight = 0);

public sealed record PendingIncomingAsset(
    string? Encoded,
    string? AssetId,
    string? AssetType,
    string? Amount,
    string? Status,
    string? Outpoint,
    string? ConfirmationHeight,
    bool HasProof,
    string? CreatedAtUnix);

public sealed record ReceiveAddress(
    string? Encoded,
    string? AssetId,
    string? Amount,
    string? AssetType = null,        // tapd extras; null in Hosted
    string? ProofCourierAddr = null,
    string? AssetVersion = null);

// BYON uses AssetId (64-hex). Hosted will use SmvId when it lands.
public sealed record ReceiveRequest(string AssetId, string? SmvId, string Amount);

// BYON: DestinationAddress is the taprt1 address (which encodes the asset).
// Hosted: SmvId identifies the held asset + DestinationAddress is the recipient.
public sealed record SendRequest(
    string DestinationAddress,
    string? AssetId = null,
    string? SmvId = null,
    string? Amount = null);

public enum SendState
{
    Submitted,        // BYON: broadcast to tapd
    PaymentRequired,  // Hosted: LN fee invoice must be paid first
    Pending,
    Fulfilled,
    Failed
}

public sealed record LnInvoice(string Bolt11, string PaymentHash, long AmountSats, string? ExpiresAt);

public sealed record SendResult(
    string? TransferRef,          // tapd transfer_id (BYON) / withdrawal_id (Hosted)
    SendState State,
    string? Txid = null,          // anchor/on-chain txid
    LnInvoice? Payment = null,    // Hosted paid-withdraw; null in BYON
    string? ProviderState = null, // raw backend state string (passthrough for API compat)
    string? RawJson = null);      // raw backend payload (passthrough for API compat)

// CreditBalanceSats is the Hosted spendable mint credit (contract v1.2 §3); null
// for BYON (no such concept) and treated as 0 by a v1.1 Hosted backend. Display
// only — never netted against a mint invoice in v1.2 (RFC-PLUGIN-004 §13).
public sealed record BackendInfo(string? Network, string? Version, bool Connected, long? CreditBalanceSats = null);

// ── Issuance (RFC-PLUGIN-004 / Managed Wallet API v1.2) ────────────────────────
// Backend-neutral mint DTOs. Only SmvHostedAssetBackend maps real payloads into
// these (v1.2 is Hosted-only, collectibles-only); TapdAssetBackend throws
// SelfCustodyMintNotAvailableException (Track B). v1.2 fixes supply=1,
// divisibility=0, asset_type="collectible" — carried as defaults so a future
// v1.3 (fungible) can vary them without a shape change.

/// <summary>A collection the merchant owns, for the reuse-or-create picker (contract §4).</summary>
public sealed record MintCollection(
    string? Id,
    string? Name,
    string? Slug,
    long TotalSupply,
    long MintedCount,
    long RemainingSupply,
    string? ImageUrl);

/// <summary>Dimensions the stateless cost quote is computed from (contract §5.1).</summary>
public sealed record MintQuoteRequest(
    long Supply = 1,
    int Divisibility = 0,
    string AssetType = "collectible");

/// <summary>A cost estimate (contract §5.1). All sats are integers; the total is an estimate.
/// BatchOnchainFeeSats (additive) is the SERIES anchor fee — the constant-vsize batch
/// commit's exact math at quote time; null on older backends.</summary>
public sealed record MintQuote(
    long OnchainFeeSats,
    long PlatformMarginSats,
    long TotalSats,
    long FeeRateSatPerVb,
    string? Network,
    string? Note,
    long? BatchOnchainFeeSats = null);

/// <summary>A single trait/value attribute on a minted asset (contract §5.2).</summary>
public sealed record MintAttribute(string TraitType, string Value);

/// <summary>
/// Everything needed to commit a mint (contract §5.2). The collection fields drive
/// the inline create_or_reuse (looked up by slug); <see cref="AcceptFeeQuoteUpToSats"/>
/// is the pre-invoice fee cap (RFC-PLUGIN-004 §7).
/// </summary>
public sealed record MintRequest(
    string CollectionName,
    string CollectionSlug,
    long CollectionTotalSupply,
    string AssetName,
    long AcceptFeeQuoteUpToSats,
    string? CollectionImageUrl = null,
    string? AssetImageUrl = null,
    string? Description = null,
    IReadOnlyList<MintAttribute>? Attributes = null,
    string? ExternalReference = null,
    long Supply = 1,
    int Divisibility = 0,
    string AssetType = "collectible",
    // BYON only (RFC-PLUGIN-006 P2-2c): the exact STAS-01 canonical metadata bytes
    // to mint into asset_meta.data. The controller computes these ONCE so that
    // sha256(asset_meta.data) == the metadata_hash the creator signed — no drift.
    // Null on the Hosted path (SMV builds the meta server-side).
    byte[]? CanonicalMetaBytes = null);

// Collapsed mint lifecycle (contract §7). The finer provider states
// (paying/preparing/broadcasting/confirming) all map to Minting; ProviderState on
// the result/status carries the raw string for a progress label.
public enum MintState
{
    AwaitingPayment, // quote_pending, awaiting_payment — the LN fee invoice is unpaid
    Minting,         // paying, preparing, broadcasting, confirming
    Minted,          // terminal success
    Failed,          // terminal failure, no credit (typically invoice never paid)
    RefundedCredit   // terminal: paid but mint failed → full fee returned as credit
}

/// <summary>Result of committing a mint (contract §5.2). The LN fee invoice is inline —
/// or null when the mint was paid with credits (§13 amendment), in which case
/// CreditsCharged/CreditsBalanceAfter carry the receipt.</summary>
public sealed record MintResult(
    string? MintRef,
    MintState State,
    LnInvoice? Invoice,
    string? CollectionId = null,
    bool CollectionCreated = false,
    string? PollUrl = null,
    string? ProviderState = null,
    long? CreditsCharged = null,
    long? CreditsBalanceAfter = null);

/// <summary>Status of an in-flight or finished mint (contract §5.3).</summary>
public sealed record MintStatus(
    MintState State,
    string MintRef,
    string Message,
    string? InvoiceStatus = null,   // unpaid | paid | expired | null
    string? BdoId = null,           // 64-hex, populated when Minted
    string? SmvId = null,
    string? AnchorOutpoint = null,
    string? ProofUrl = null,
    string? CollectionName = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    long RefundCreditSats = 0,
    string? ProviderState = null);

// Backend-neutral send/transfer status. BYON reports an on-chain confirmation
// count (Confirmations/Required set, State "confirmed"|"pending"); Hosted reports
// the Managed Wallet transfer status string (Confirmations/Required null). Ref is
// the txid (BYON) or transfer_ref (Hosted). Broadcasted stays true once the send
// has left the plugin.
public sealed record SendStatus(
    string State,
    string Ref,
    bool Broadcasted,
    string Message,
    int? Confirmations = null,
    int? Required = null);

// ── My BDOs listing Phase 2 (RFC-PLUGIN-005) — Hosted collection endpoints ──────
// Backend-neutral shapes for the collection-first listing. Hosted maps the v1.2.1
// holdings endpoints into these; BYON keeps the Phase 1 client-side grouping and
// does not implement these (throws).

/// <summary>A collection the merchant holds BDOs in (My BDOs Level 1). OwnedCount is
/// possession (live holdings, reflects sends); CollectionSize is total_supply — ALWAYS
/// distinct, never derived from each other (contract §7 R3).</summary>
public sealed record HeldCollection(
    string? CollectionId,
    string? Slug,
    string? Name,
    string? CoverImageUrl,
    long OwnedCount,
    long CollectionSize,
    string? Modality,       // unique_bdo | fungible_edition | unique_series
    string? GroupKey,
    string? IssuerName);

/// <summary>A held unit within a collection (My BDOs Level 2). AssetId (tapd 64-hex) is
/// null during the pre-anchor window (batch M3, between broadcast and proof backfill) →
/// the UI shows "confirming on-chain" and disables Send.</summary>
public sealed record HeldUnit(
    string? Id,          // asset uuid (smv_id) — Verify link
    string? AssetId,     // tapd 64-hex — Send + BDO ID display; null pre-anchor
    string? Name,
    string? ImageUrl,
    int? BatchIndex,     // null for Modality 1/2
    string? AcquiredAt);

/// <summary>A page of held units + the opaque cursor for the next page (null = last page).</summary>
public sealed record HeldUnitsPage(
    IReadOnlyList<HeldUnit> Items,
    string? NextCursor);

// ── Batch mint (RFC_BATCH_MINTING_V1, Modality 3 — the moat) ────────────────────
// N unique collectibles anchored in ~1 tx. Mirrors the single-mint DTOs with a
// per-unit template + UnitCount. Async: submit → inline aggregate invoice → poll.

/// <summary>Everything needed to submit a batch (Modality 3). The template's name is the
/// base; the backend applies a 1-based index per unit. Billing is the aggregate fee cap.</summary>
public sealed record MintBatchRequest(
    string CollectionName,
    string CollectionSlug,
    long CollectionTotalSupply,
    long UnitCount,
    string TemplateName,
    long AcceptFeeQuoteUpToSats,
    string? CollectionImageUrl = null,
    string? ImageUrl = null,
    string? Description = null,
    IReadOnlyList<MintAttribute>? Attributes = null);

/// <summary>Result of submitting a batch (202). The aggregate LN fee invoice is inline —
/// or null when paid with credits (§13 amendment).</summary>
public sealed record MintBatchResult(
    string? BatchRef,
    MintState State,
    LnInvoice? Invoice,
    string? CollectionId = null,
    bool CollectionCreated = false,
    string? ProviderState = null,
    long? CreditsCharged = null,
    long? CreditsBalanceAfter = null);

/// <summary>Status of an in-flight or finished batch. <c>Minted</c> jumps 0 → <c>Total</c>
/// atomically on completion; the live progress is the <c>State</c> phase (like the single mint).</summary>
public sealed record MintBatchStatus(
    MintState State,
    string BatchRef,
    string Message,
    long Minted,
    long Total,
    string? InvoiceStatus = null,
    string? CollectionName = null,
    string? CollectionSlug = null,
    string? CollectionId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    long RefundCreditSats = 0,
    string? ProviderState = null);
