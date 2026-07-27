using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Backends;

// Wire DTOs for the v1.2.1 additive batch-mint endpoints (RFC_BATCH_MINTING_V1,
// Modality 3 — the moat). A batch is N unique collectibles anchored in ~1 tx.
// The request/response mirror the single-mint shapes (ManagedMintDtos) plus
// unit_count, a per-unit template, a batch_ref, and progress. Shapes are mirrored
// from the single-mint contract + Lovable's B2 spec; confirmed against the deployed
// endpoint at integration. Async by design: submit → 202 → poll status.

// ── POST /managed-wallet-mint-batch (submit) ───────────────────────────────────

/// <summary>Request body for POST /managed-wallet-mint-batch. Collection + a per-unit
/// template + unit_count + the aggregate fee cap. Each unit is a collectible (supply 1).</summary>
public sealed class ManagedMintBatchRequest
{
    [JsonPropertyName("collection")] public ManagedMintCollectionRequest Collection { get; set; } = new();
    [JsonPropertyName("template")] public ManagedMintBatchTemplate Template { get; set; } = new();
    [JsonPropertyName("unit_count")] public long UnitCount { get; set; }
    [JsonPropertyName("billing")] public ManagedMintBilling Billing { get; set; } = new();
}

/// <summary>The per-unit template: the backend applies the base name + a 1-based index
/// per unit; description/image/attributes are shared across the series.</summary>
public sealed class ManagedMintBatchTemplate
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ManagedMintAttribute>? Attributes { get; set; }
}

/// <summary>Response (202) of POST /managed-wallet-mint-batch. LN fee invoice inline;
/// the invoice covers the aggregate quote (Layer A on-chain ~constant + Layer B × N).</summary>
public sealed class ManagedMintBatchResponse
{
    [JsonPropertyName("batch_ref")] public string? BatchRef { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("collection")] public ManagedMintCollectionCreated? Collection { get; set; }
    [JsonPropertyName("invoice")] public ManagedMintInvoice? Invoice { get; set; }
    // Credits-first (§13 amendment): when the balance covered the batch quote,
    // invoice is null and this block carries the receipt.
    [JsonPropertyName("payment")] public ManagedMintPayment? Payment { get; set; }
    [JsonPropertyName("poll_url")] public string? PollUrl { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
}

// ── GET /managed-wallet-mint-batch-status/<batch_ref> ──────────────────────────

/// <summary>Response of GET /managed-wallet-mint-batch-status/&lt;ref&gt;. Live progress
/// runs through <c>status</c> (draft → invoiced → paid → minting → broadcasting →
/// confirmed | failed | refunded); <c>progress.minted</c> jumps 0 → total atomically on
/// completion. <c>units</c> is populated only when confirmed (grid via holdings-units).</summary>
public sealed class ManagedMintBatchStatus
{
    [JsonPropertyName("batch_ref")] public string? BatchRef { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("progress")] public ManagedMintBatchProgress? Progress { get; set; }
    [JsonPropertyName("collection")] public ManagedMintStatusCollection? Collection { get; set; }
    [JsonPropertyName("invoice_status")] public string? InvoiceStatus { get; set; }
    [JsonPropertyName("units")] public List<ManagedMintBatchUnit>? Units { get; set; }
    [JsonPropertyName("error")] public ManagedMintErrorInfo? Error { get; set; }
    [JsonPropertyName("refund")] public ManagedMintRefund? Refund { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("confirmed_at")] public string? ConfirmedAt { get; set; }
}

/// <summary>Cheap progress summary — the plugin's live poll reads this, not the units array.</summary>
public sealed class ManagedMintBatchProgress
{
    [JsonPropertyName("minted")] public long Minted { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
}

/// <summary>A single minted unit; present only when the batch is confirmed.</summary>
public sealed class ManagedMintBatchUnit
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }              // smv uuid
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }   // tapd 64-hex
    [JsonPropertyName("bdo_id")] public string? BdoId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}
