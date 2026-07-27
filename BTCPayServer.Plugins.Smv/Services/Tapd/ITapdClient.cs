namespace BTCPayServer.Plugins.Smv.Services.Tapd;

public interface ITapdClient
{
    Task<TapdInfo?> GetInfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TapdAsset>> ListAssetsAsync(CancellationToken cancellationToken = default);

    Task<TapdReceiveAddress> CreateAddressAsync(
        string assetId,
        string amount,
        CancellationToken cancellationToken = default);

    Task<TapdSendResult> SendAsync(string taprootAssetAddress, CancellationToken cancellationToken = default);

    // ── BYON minting (self-custody issuance, RFC-PLUGIN-006 P2-1) ────────────────

    /// <summary>Add a seedling to tapd's pending mint batch (POST /v1/taproot-assets/assets).
    /// Requires a mint-capable macaroon.</summary>
    Task<TapdMintBatch> MintAssetAsync(TapdMintAssetRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finalize (seal + broadcast) the pending batch
    /// (POST /v1/taproot-assets/assets/mint/finalize). Returns the batch with its
    /// assets' asset_id/script_key populated.</summary>
    Task<TapdMintBatch> FinalizeBatchAsync(int? feeRateSatPerVb = null, CancellationToken cancellationToken = default);

    /// <summary>Export the universe proof for a minted asset
    /// (POST /v1/taproot-assets/proofs/export), returned as the raw base64 proof blob
    /// for SMV registration.</summary>
    Task<string> ExportProofAsync(string assetIdHex, string scriptKeyHex, CancellationToken cancellationToken = default);

    /// <summary>Fetch the asset's minted metadata (GET /v1/taproot-assets/assets/meta/asset-id/{id})
    /// decoded to its UTF-8 JSON string. Null when the asset has no meta, the node
    /// doesn't expose it, or the bytes aren't decodable — enrichment is best-effort.</summary>
    Task<string?> FetchAssetMetaJsonAsync(string assetIdHex, CancellationToken cancellationToken = default);
}