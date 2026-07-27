namespace BTCPayServer.Plugins.Smv.Services.Tapd;

// Models for BYON self-custody minting on the merchant's own tapd node
// (RFC-PLUGIN-006, P2-1). The asset is minted here; the SMV backend later
// registers it (proof + metadata) to make it a full, verifiable BDO.

/// <summary>Input for a single BYON mint (a standalone COLLECTIBLE — the SMV
/// "collection" is an external-layer concept assigned at registration, so no
/// tapd group is needed for a single BDO; grouped batch is P3).</summary>
public sealed record TapdMintAssetRequest(
    string Name,
    string Amount,             // uint64 as string
    byte[] MetaBytes,          // raw asset metadata (hex-encoded on the wire, per tapd REST)
    string AssetType = "COLLECTIBLE",       // tapd AssetType enum name  — VERIFY-LIVE
    string MetaType = "META_TYPE_OPAQUE");  // tapd AssetMetaType enum name — VERIFY-LIVE

/// <summary>One asset inside a (pending or finalized) mint batch. AssetId/ScriptKey
/// are surfaced as lowercase hex (converted from tapd's base64 wire form).</summary>
public sealed record TapdMintedAsset(
    string? AssetId,
    string? ScriptKey,
    string? Name);

/// <summary>A tapd mint batch (the pending batch after MintAsset, or the finalized
/// batch after FinalizeBatch). BatchTxid is empty until finalized/broadcast.</summary>
public sealed record TapdMintBatch(
    string? BatchKey,
    string? BatchTxid,
    string? State,
    IReadOnlyList<TapdMintedAsset> Assets);
