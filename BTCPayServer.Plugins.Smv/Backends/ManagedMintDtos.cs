using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Backends;

// Wire DTOs for the Managed Wallet API v1.2 issuance endpoints (contract §4, §5).
// Only the fields the plugin consumes/sends are modelled; unknown fields are
// ignored by System.Text.Json. v1.2 is collectibles-only: the plugin always
// sends asset_type="collectible", supply=1, divisibility=0 (RFC-PLUGIN-004 §8);
// the request DTOs still carry the fields so the wire matches the contract and a
// future v1.3 (fungible) can vary them without a shape change.

// ── GET /managed-wallet-collections (contract §4) ──────────────────────────────

/// <summary>GET /managed-wallet-collections envelope (contract §3.2, §4).</summary>
public sealed class ManagedCollectionsResponse
{
    [JsonPropertyName("items")] public List<ManagedCollectionItem> Items { get; set; } = new();

    // Always null in v1.2 (page cap = 200, no cursor). Modelled so a future
    // non-null string deserializes without error.
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

/// <summary>Per-item shape of GET /managed-wallet-collections (contract §4).</summary>
public sealed class ManagedCollectionItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("total_supply")] public long TotalSupply { get; set; }
    [JsonPropertyName("minted_count")] public long MintedCount { get; set; }
    // Server-computed = total_supply - minted_count.
    [JsonPropertyName("remaining_supply")] public long RemainingSupply { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }   // SMV-hosted or null
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
}

// ── POST /managed-wallet-mint-quote (contract §5.1) ────────────────────────────

/// <summary>Request body for POST /managed-wallet-mint-quote (contract §5.1). Stateless.</summary>
public sealed class ManagedMintQuoteRequest
{
    [JsonPropertyName("asset")] public ManagedMintQuoteAsset Asset { get; set; } = new();
}

/// <summary>The dimensions the quote is computed from (contract §5.1).</summary>
public sealed class ManagedMintQuoteAsset
{
    [JsonPropertyName("supply")] public long Supply { get; set; } = 1;
    [JsonPropertyName("divisibility")] public int Divisibility { get; set; } = 0;
    [JsonPropertyName("asset_type")] public string AssetType { get; set; } = "collectible";
}

/// <summary>Response of POST /managed-wallet-mint-quote (contract §5.1).</summary>
public sealed class ManagedMintQuoteResponse
{
    [JsonPropertyName("estimate")] public ManagedMintEstimate? Estimate { get; set; }
    // Batch anchor estimate (additive): same fee-rate call as the single
    // estimate, but the batch commit's constant 154 vB anchor — so the series
    // form can quote exactly what the commit will charge.
    [JsonPropertyName("batch")] public ManagedMintQuoteBatch? Batch { get; set; }
    [JsonPropertyName("quoted_at")] public string? QuotedAt { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>The batch anchor estimate block (additive).</summary>
public sealed class ManagedMintQuoteBatch
{
    [JsonPropertyName("anchor_vsize")] public int AnchorVsize { get; set; }
    [JsonPropertyName("onchain_fee_sats")] public long OnchainFeeSats { get; set; }
    [JsonPropertyName("platform_margin_per_unit_sats")] public long PlatformMarginPerUnitSats { get; set; }
}

/// <summary>The fee estimate block (contract §5.1). All sats are integers.</summary>
public sealed class ManagedMintEstimate
{
    [JsonPropertyName("onchain_fee_sats")] public long OnchainFeeSats { get; set; }
    [JsonPropertyName("platform_margin_sats")] public long PlatformMarginSats { get; set; }
    [JsonPropertyName("total_sats")] public long TotalSats { get; set; }
    [JsonPropertyName("fee_rate_sat_per_vb")] public long FeeRateSatPerVb { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
}

// ── POST /managed-wallet-mint (contract §5.2) ──────────────────────────────────

/// <summary>Request body for POST /managed-wallet-mint (contract §5.2).</summary>
public sealed class ManagedMintRequest
{
    [JsonPropertyName("collection")] public ManagedMintCollectionRequest Collection { get; set; } = new();
    [JsonPropertyName("asset")] public ManagedMintAssetRequest Asset { get; set; } = new();
    [JsonPropertyName("billing")] public ManagedMintBilling Billing { get; set; } = new();
}

/// <summary>The inline create-or-reuse collection block (contract §5.2).</summary>
public sealed class ManagedMintCollectionRequest
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "create_or_reuse";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("total_supply")] public long TotalSupply { get; set; }
    // Optional cover image; omitted from the wire when null.
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }
}

/// <summary>The asset block (contract §5.2). v1.2: type/supply/divisibility fixed.</summary>
public sealed class ManagedMintAssetRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("supply")] public long Supply { get; set; } = 1;
    [JsonPropertyName("divisibility")] public int Divisibility { get; set; } = 0;
    [JsonPropertyName("asset_type")] public string AssetType { get; set; } = "collectible";
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManagedMintAttribute>? Attributes { get; set; }

    [JsonPropertyName("external_reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalReference { get; set; }
}

/// <summary>A single trait/value attribute (contract §5.2).</summary>
public sealed class ManagedMintAttribute
{
    [JsonPropertyName("trait_type")] public string? TraitType { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

/// <summary>The fee-cap block (contract §5.2). Real fee &gt; cap → fee_too_high pre-invoice.</summary>
public sealed class ManagedMintBilling
{
    [JsonPropertyName("accept_fee_quote_up_to_sats")] public long AcceptFeeQuoteUpToSats { get; set; }
}

/// <summary>Response (202) of POST /managed-wallet-mint (contract §5.2). Invoice is inline —
/// OR null when the mint was paid with credits (§13 amendment): status arrives
/// already at "preparing" with the <c>payment</c> block instead.</summary>
public sealed class ManagedMintResponse
{
    [JsonPropertyName("mint_ref")] public string? MintRef { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }              // "awaiting_payment" | "preparing"
    [JsonPropertyName("collection")] public ManagedMintCollectionCreated? Collection { get; set; }
    [JsonPropertyName("invoice")] public ManagedMintInvoice? Invoice { get; set; }
    [JsonPropertyName("payment")] public ManagedMintPayment? Payment { get; set; }
    [JsonPropertyName("poll_url")] public string? PollUrl { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
}

/// <summary>How the mint was paid when no invoice was needed (credits-first).</summary>
public sealed class ManagedMintPayment
{
    [JsonPropertyName("method")] public string? Method { get; set; }              // "credits"
    [JsonPropertyName("charged_sats")] public long ChargedSats { get; set; }
    [JsonPropertyName("balance_after_sats")] public long BalanceAfterSats { get; set; }
}

/// <summary>The {id, created} collection stub on the mint 202 (contract §5.2).</summary>
public sealed class ManagedMintCollectionCreated
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("created")] public bool Created { get; set; }
}

/// <summary>The LN fee invoice returned inline on the mint 202 (contract §5.2).</summary>
public sealed class ManagedMintInvoice
{
    [JsonPropertyName("bolt11")] public string? Bolt11 { get; set; }
    [JsonPropertyName("amount_sats")] public long AmountSats { get; set; }
    [JsonPropertyName("breakdown")] public ManagedMintInvoiceBreakdown? Breakdown { get; set; }
    [JsonPropertyName("payment_hash")] public string? PaymentHash { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }       // quote TTL = 5 min
}

/// <summary>The invoice fee breakdown (contract §5.2).</summary>
public sealed class ManagedMintInvoiceBreakdown
{
    [JsonPropertyName("onchain_fee_sats")] public long OnchainFeeSats { get; set; }
    [JsonPropertyName("platform_margin_sats")] public long PlatformMarginSats { get; set; }
}

// ── GET /managed-wallet-mint-status/<ref> (contract §5.3) ──────────────────────

/// <summary>Response of GET /managed-wallet-mint-status/&lt;ref&gt; (contract §5.3).</summary>
public sealed class ManagedMintStatus
{
    [JsonPropertyName("mint_ref")] public string? MintRef { get; set; }
    // quote_pending | awaiting_payment | paying | preparing | broadcasting | confirming | minted | failed | refunded_credit
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("collection")] public ManagedMintStatusCollection? Collection { get; set; }
    [JsonPropertyName("asset")] public ManagedMintStatusAsset? Asset { get; set; }
    // unpaid | paid | expired | null
    [JsonPropertyName("invoice_status")] public string? InvoiceStatus { get; set; }
    [JsonPropertyName("error")] public ManagedMintErrorInfo? Error { get; set; }
    [JsonPropertyName("refund")] public ManagedMintRefund? Refund { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("minted_at")] public string? MintedAt { get; set; }
}

/// <summary>The {id, name, slug} collection block on mint-status (contract §5.3).</summary>
public sealed class ManagedMintStatusCollection
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
}

/// <summary>The minted-asset block on mint-status; populated only when minted (contract §5.3).</summary>
public sealed class ManagedMintStatusAsset
{
    [JsonPropertyName("bdo_id")] public string? BdoId { get; set; }               // 64-hex tapd id
    [JsonPropertyName("smv_id")] public string? SmvId { get; set; }               // internal uuid
    [JsonPropertyName("anchor_outpoint")] public string? AnchorOutpoint { get; set; }
    [JsonPropertyName("proof_url")] public string? ProofUrl { get; set; }
}

/// <summary>The error block on mint-status (contract §5.3); null unless failed.</summary>
public sealed class ManagedMintErrorInfo
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>The refund block on mint-status (contract §5.3); credit &gt; 0 on refunded_credit.</summary>
public sealed class ManagedMintRefund
{
    [JsonPropertyName("credit_sats")] public long CreditSats { get; set; }
    [JsonPropertyName("ledger_ref")] public string? LedgerRef { get; set; }
}
