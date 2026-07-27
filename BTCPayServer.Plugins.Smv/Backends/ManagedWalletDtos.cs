using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Backends;

// Wire DTOs for the Managed Wallet API v1.1 read endpoints (contract §5.1, §5.2).
// Only the fields the plugin consumes are modelled; unknown fields are ignored by
// System.Text.Json. Internal projections the contract withholds (script_key,
// custody_mode, …) are intentionally absent.

/// <summary>GET /managed-wallet-get (contract §5.1).</summary>
public sealed class ManagedWalletDto
{
    [JsonPropertyName("wallet_id")] public string? WalletId { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("custody")] public string? Custody { get; set; }         // "custodial"
    [JsonPropertyName("custodian")] public string? Custodian { get; set; }     // "Supermultiverse"
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; } // null in v1.1
    [JsonPropertyName("status")] public string? Status { get; set; }

    // v1.2 additive (contract §3). Spendable mint credit (excludes reserved_sats);
    // absent on a v1.1 backend, in which case it deserializes to 0. Never netted
    // against a mint invoice in v1.2 — display only.
    [JsonPropertyName("credit_balance_sats")] public long CreditBalanceSats { get; set; }
}

// ── Mint-credits top-up (additive, post v1.2.1) ────────────────────────────────

/// <summary>GET /managed-wallet-topup — balance + active packages.</summary>
public sealed class ManagedTopupInfo
{
    [JsonPropertyName("balance_sats")] public long BalanceSats { get; set; }
    [JsonPropertyName("packages")] public List<ManagedTopupPackage> Packages { get; set; } = new();
}

public sealed class ManagedTopupPackage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("amount_sats")] public long AmountSats { get; set; }
}

/// <summary>POST /managed-wallet-topup — the LN invoice for a package.</summary>
public sealed class ManagedTopupInvoice
{
    [JsonPropertyName("payment_intent_id")] public string? PaymentIntentId { get; set; }
    [JsonPropertyName("invoice_bolt11")] public string? InvoiceBolt11 { get; set; }
    [JsonPropertyName("payment_hash")] public string? PaymentHash { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("amount_sats")] public long AmountSats { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

/// <summary>GET /managed-wallet-topup?intent_id=… — settlement status.</summary>
public sealed class ManagedTopupStatus
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("paid")] public bool Paid { get; set; }
    [JsonPropertyName("amount_sats")] public long AmountSats { get; set; }
    [JsonPropertyName("balance_sats")] public long BalanceSats { get; set; }
}

// ── Premium subscription (additive; journey GAP G1) ────────────────────────────

/// <summary>GET /managed-wallet-subscribe — current plan + purchasable tiers.</summary>
public sealed class ManagedSubscriptionInfo
{
    [JsonPropertyName("current")] public ManagedSubscriptionCurrent? Current { get; set; }
    [JsonPropertyName("tiers")] public List<ManagedSubscriptionTier> Tiers { get; set; } = new();
    [JsonPropertyName("lifetime")] public ManagedLifetimePass? Lifetime { get; set; }
}

/// <summary>The one-time Lifetime (Founders) Pass with live scarcity.</summary>
public sealed class ManagedLifetimePass
{
    [JsonPropertyName("sold_out")] public bool SoldOut { get; set; }
    [JsonPropertyName("price_sats")] public long PriceSats { get; set; }
    [JsonPropertyName("tier_order")] public int TierOrder { get; set; }
    [JsonPropertyName("total_sold")] public int TotalSold { get; set; }
    [JsonPropertyName("total_cap")] public int TotalCap { get; set; }
    [JsonPropertyName("units_remaining_tier")] public int UnitsRemainingTier { get; set; }
    [JsonPropertyName("units_in_tier_max")] public int UnitsInTierMax { get; set; }
    [JsonPropertyName("next_price_sats")] public long? NextPriceSats { get; set; }
    [JsonPropertyName("credit_grant_sats")] public long CreditGrantSats { get; set; }
    [JsonPropertyName("mint_fee_sats")] public long MintFeeSats { get; set; }
    [JsonPropertyName("already_owned")] public bool AlreadyOwned { get; set; }
}

public sealed class ManagedSubscriptionCurrent
{
    [JsonPropertyName("tier")] public string? Tier { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

public sealed class ManagedSubscriptionTier
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("price_sats")] public long PriceSats { get; set; }
    [JsonPropertyName("duration_days")] public int DurationDays { get; set; }
    // Annual mint-credit grant included with the tier (served by the backend
    // so client copy can never drift from the settle RPC's actual grants).
    [JsonPropertyName("credit_grant_sats")] public long CreditGrantSats { get; set; }
    // Per-minted-BDO platform fee for this tier (tier-differentiated pricing,
    // 2026-07-27) — same config map the mint paths charge from. 0 = not served.
    [JsonPropertyName("mint_fee_sats")] public long MintFeeSats { get; set; }
}

/// <summary>POST /managed-wallet-subscribe — the LN invoice for a tier.</summary>
public sealed class ManagedSubscriptionInvoice
{
    [JsonPropertyName("payment_intent_id")] public string? PaymentIntentId { get; set; }
    [JsonPropertyName("invoice_bolt11")] public string? InvoiceBolt11 { get; set; }
    [JsonPropertyName("payment_hash")] public string? PaymentHash { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("amount_sats")] public long AmountSats { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

// ── Send-to-customer claim links (additive; journey GAP G2) ────────────────────

/// <summary>A pending merchant claim link (accounting transfer offer).</summary>
public sealed class ManagedClaimLink
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("claim_url")] public string? ClaimUrl { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    // 64-hex BDO id (additive) — what the Send panel keys its rows by, so an
    // already-open link can be surfaced in place instead of a bare error.
    [JsonPropertyName("tapd_asset_id")] public string? TapdAssetId { get; set; }
    [JsonPropertyName("asset_name")] public string? AssetName { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
}

/// <summary>GET /managed-wallet-send-claim — the caller's open links.</summary>
public sealed class ManagedClaimLinkList
{
    [JsonPropertyName("pending")] public List<ManagedClaimLink> Pending { get; set; } = new();
}

/// <summary>POST /managed-wallet-send-claim {redeem_code} — redemption outcome.</summary>
public sealed class ManagedClaimRedeemResult
{
    [JsonPropertyName("claimed")] public bool Claimed { get; set; }
    [JsonPropertyName("asset_name")] public string? AssetName { get; set; }
}

/// <summary>GET /managed-wallet-subscribe?intent_id=… — activation status.</summary>
public sealed class ManagedSubscriptionStatus
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("paid")] public bool Paid { get; set; }
    [JsonPropertyName("active_tier")] public string? ActiveTier { get; set; }
}

/// <summary>GET /managed-wallet-assets envelope (contract §3.2, §5.2).</summary>
public sealed class ManagedAssetsResponse
{
    [JsonPropertyName("items")] public List<ManagedAssetItem> Items { get; set; } = new();

    // Always null in v1.1 (page cap = 200, no cursor). Modelled so a future
    // non-null string deserializes without error; not consumed in H1a.
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

/// <summary>Per-item shape of GET /managed-wallet-assets (contract §5.2).</summary>
public sealed class ManagedAssetItem
{
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }             // 64-hex tapd id
    [JsonPropertyName("smv_id")] public string? SmvId { get; set; }                 // internal uuid
    [JsonPropertyName("amount")] public string? Amount { get; set; }                // decimal string
    [JsonPropertyName("holding_status")] public string? HoldingStatus { get; set; } // "confirmed"
    [JsonPropertyName("anchor_outpoint")] public string? AnchorOutpoint { get; set; }
    [JsonPropertyName("acquired_at")] public string? AcquiredAt { get; set; }
}

/// <summary>
/// GET /managed-wallet-transfer-status/&lt;ref&gt; (contract §5.5) and the body of
/// POST /managed-wallet-send (contract §10.1), which is the same envelope plus the
/// <see cref="Payment"/> block (null on the read endpoint).
/// </summary>
public sealed class ManagedTransferStatus
{
    [JsonPropertyName("transfer_ref")] public string? TransferRef { get; set; }
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("smv_id")] public string? SmvId { get; set; }
    [JsonPropertyName("amount")] public string? Amount { get; set; }
    // pending_payment | paid | broadcasting | fulfilled | failed | cancelled
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("destination_address")] public string? DestinationAddress { get; set; }
    [JsonPropertyName("txid")] public string? Txid { get; set; }
    [JsonPropertyName("anchor_outpoint")] public string? AnchorOutpoint { get; set; }
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }

    // Present only on the send response (§10.1); the LN fee invoice to reserve the send.
    [JsonPropertyName("payment")] public ManagedPayment? Payment { get; set; }
}

/// <summary>The LN fee invoice returned by POST /managed-wallet-send (contract §10.1).</summary>
public sealed class ManagedPayment
{
    [JsonPropertyName("invoice")] public string? Invoice { get; set; }        // lnbc…
    [JsonPropertyName("amount_sats")] public long AmountSats { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

/// <summary>Request body for POST /managed-wallet-send (contract §10.1).</summary>
public sealed class ManagedSendRequest
{
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("amount")] public string? Amount { get; set; }
    [JsonPropertyName("destination_address")] public string? DestinationAddress { get; set; }
}

/// <summary>Request body for POST /managed-wallet-receive-address (v1.2.1 additive).
/// A fresh single-use address is minted per call by design (no Idempotency-Key).</summary>
public sealed class ManagedReceiveAddressRequest
{
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }   // 64-hex
    [JsonPropertyName("amount")] public int Amount { get; set; } = 1;      // >= 1, default 1
}

/// <summary>Response of POST /managed-wallet-receive-address. The SMV node is the
/// source of the address; inbound payment lands in the token's custodial wallet.</summary>
public sealed class ManagedReceiveAddressResponse
{
    [JsonPropertyName("address")] public string? Address { get; set; }     // tapbc1…
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("amount")] public int Amount { get; set; }
    [JsonPropertyName("expiry")] public string? Expiry { get; set; }       // always null in v1.2.1
}
