using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Plugins.Smv.Services.Tapd;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// BYON implementation of <see cref="IAssetBackend"/>. Thin wrapper over the
/// existing <see cref="TapdClient"/> (unchanged); it only maps tapd payloads to
/// the backend-neutral DTOs. Owns the per-request <see cref="HttpClient"/> and
/// disposes it.
///
/// P3-H2: send-status polling moved here from <c>SmvSendController.SendStatus</c>,
/// behaviour-preserving — same Bitcoin Core RPC <c>getrawtransaction</c> call,
/// same 1-confirmation threshold, same messages, same fail-safe to "pending".
/// </summary>
public sealed class TapdAssetBackend : IAssetBackend
{
    // Confirmation threshold. Unchanged from the former SmvSendController constant.
    private const int RequiredConfirmations = 1;

    // Bound the enrichment N+1 fan-out (Public API rate limit: 60/60s per IP).
    private const int EnrichmentConcurrency = 4;

    private readonly TapdClient _client;
    private readonly HttpClient _httpClient;
    private readonly BitcoinRpcConfig? _bitcoinRpc;
    private readonly ILogger? _log;
    private readonly SmvPublicApiClient? _publicApi;

    public TapdAssetBackend(
        TapdClient client,
        HttpClient httpClient,
        string? connectionLabel,
        BitcoinRpcConfig? bitcoinRpc = null,
        ILogger? log = null,
        SmvPublicApiClient? publicApi = null)
    {
        _client = client;
        _httpClient = httpClient;
        ConnectionLabel = connectionLabel;
        _bitcoinRpc = bitcoinRpc;
        _log = log;
        _publicApi = publicApi;
    }

    public string? ConnectionLabel { get; }

    public bool IsCustodial => false; // BYON is self-custody.

    // STAS-01 meta is hash-committed and immutable, so a SUCCESSFUL decode is safe to
    // cache process-wide (bounded by the merchant's holdings). Failures are NEVER
    // cached: a transient node error — or a backend that only recently started
    // exposing the meta route, as the production relay did — would otherwise pin
    // "no metadata" until the next BTCPay restart. Assets genuinely without meta
    // re-fetch per load; that call is cheap and bounded by the listing size.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TapdMetaInfo> MetaCache = new();

    public async Task<IReadOnlyList<OwnedAsset>> ListAssetsAsync(CancellationToken cancellationToken = default)
    {
        var assets = await _client.ListAssetsAsync(cancellationToken);

        // Two-layer enrichment, both best-effort:
        //  • LOCAL (sovereign): decode the asset's own STAS-01 metadata from the node
        //    (image/description/attributes/external link) — works for every asset,
        //    registered with the platform or not.
        //  • PLATFORM: the Public API adds the SMV-hosted image, IPFS permanence and
        //    collection identity for registered assets; it wins for the thumbnail.
        // tapd stays the source of truth for name/type/amount.
        using var gate = new SemaphoreSlim(EnrichmentConcurrency);
        var tasks = assets.Select(async a =>
        {
            var owned = new OwnedAsset(
                AssetId: a.AssetId,
                SmvId: null,
                Name: a.Name,
                Type: a.Type,
                Amount: a.Amount,
                IsConfirming: a.IsConfirming);

            if (string.IsNullOrWhiteSpace(a.AssetId))
                return owned;

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!MetaCache.TryGetValue(a.AssetId!, out var meta))
                {
                    meta = TapdMetaInfo.Parse(await _client.FetchAssetMetaJsonAsync(a.AssetId!, cancellationToken));
                    if (meta is not null) MetaCache.TryAdd(a.AssetId!, meta);
                }
                if (meta is not null)
                {
                    owned = owned with
                    {
                        ImageUrl = meta.ImageUrl,
                        Description = meta.Description,
                        ExternalUrl = meta.ExternalUrl,
                        Attributes = meta.Attributes
                    };
                }

                // A confirming mint cannot be registered with the platform yet
                // (registration needs the confirmed anchor's proof), so the Public
                // API lookup would only 404 — skip it until the block lands.
                if (_publicApi is not null && !a.IsConfirming)
                {
                    var c = await _publicApi.GetCollectibleAsync(a.AssetId!, cancellationToken);
                    owned = owned with
                    {
                        ImageUrl = c.ImageUrl ?? owned.ImageUrl,
                        Collection = c.Collection?.Name,
                        CollectionSlug = c.Collection?.Slug,
                        ImageIpfsUrl = c.ImageIpfsUrl,
                        ImageIpfsCid = c.ImageIpfsCid
                    };
                }
                return owned;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return owned; // best-effort: whatever enrichment succeeded, keep the holding
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlyList<PendingIncomingAsset>> ListPendingIncomingAsync(CancellationToken cancellationToken = default)
    {
        var events = await _client.ListReceiveEventsAsync(cancellationToken);
        var result = new List<PendingIncomingAsset>(events.Count);
        foreach (var e in events)
        {
            result.Add(new PendingIncomingAsset(
                Encoded: e.Encoded,
                AssetId: e.AssetId,
                AssetType: e.AssetType,
                Amount: e.Amount,
                Status: e.Status,
                Outpoint: e.Outpoint,
                ConfirmationHeight: e.ConfirmationHeight,
                HasProof: e.HasProof,
                CreatedAtUnix: e.CreatedAtUnix));
        }
        return result;
    }

    public async Task<ReceiveAddress> CreateReceiveAddressAsync(ReceiveRequest request, CancellationToken cancellationToken = default)
    {
        var addr = await _client.CreateAddressAsync(request.AssetId, request.Amount, cancellationToken);
        return new ReceiveAddress(
            Encoded: addr.Encoded,
            AssetId: addr.AssetId,
            Amount: addr.Amount,
            AssetType: addr.AssetType,
            ProofCourierAddr: addr.ProofCourierAddr,
            AssetVersion: addr.AssetVersion);
    }

    public async Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _client.SendAsync(request.DestinationAddress, cancellationToken);
        return new SendResult(
            TransferRef: result.TransferId,
            State: SendState.Submitted,
            Txid: result.AnchorTxid,
            Payment: null,
            ProviderState: result.State,
            RawJson: result.RawJson);
    }

    public async Task<BackendInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = await _client.GetInfoAsync(cancellationToken);
        return new BackendInfo(
            Network: info?.Network,
            Version: info?.Version,
            Connected: info is not null);
    }

    // Moved verbatim from SmvSendController.SendStatus (P3-H2). transferRef is the
    // on-chain txid. With no Bitcoin RPC configured we cannot read the confirmation
    // count, so we fail safe to "pending" — the transfer was still broadcast.
    public async Task<SendStatus> GetSendStatusAsync(string transferRef, CancellationToken cancellationToken = default)
    {
        if (_bitcoinRpc is null)
        {
            _log?.LogInformation("ui_send_status.rpc_not_configured txid={Txid}", transferRef);
            return Pending(transferRef, "Transaction broadcast. On-chain confirmation tracking is not configured.");
        }

        try
        {
            using var rpcClient = new HttpClient { BaseAddress = new Uri(_bitcoinRpc.Url) };

            var rpcAuth = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_bitcoinRpc.User}:{_bitcoinRpc.Password}"));
            rpcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", rpcAuth);

            var request = new BitcoinRpcRequest("getrawtransaction", new object[] { transferRef, true });

            using var response = await rpcClient.PostAsJsonAsync("/", request, cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _log?.LogWarning(
                    "ui_send_status.bitcoin_rpc_error status={StatusCode} body={Body}",
                    response.StatusCode, rawJson);
                return Pending(transferRef, "Transaction broadcast. Waiting for Bitcoin Core to index it.");
            }

            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind != JsonValueKind.Null)
            {
                return Pending(transferRef, "Transaction broadcast. Waiting for first confirmation.");
            }

            var confirmations = 0;
            if (root.TryGetProperty("result", out var resultElement) &&
                resultElement.TryGetProperty("confirmations", out var confirmationsElement) &&
                confirmationsElement.TryGetInt32(out var parsedConfirmations))
            {
                confirmations = parsedConfirmations;
            }

            var confirmed = confirmations >= RequiredConfirmations;
            return new SendStatus(
                State: confirmed ? "confirmed" : "pending",
                Ref: transferRef,
                Broadcasted: true,
                Message: confirmed ? "Transaction confirmed." : "Waiting for blockchain confirmation.",
                Confirmations: confirmations,
                Required: RequiredConfirmations);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "ui_send_status.unexpected txid={Txid}", transferRef);
            return Pending(transferRef, "Transaction broadcast. Waiting for confirmation.");
        }
    }

    private static SendStatus Pending(string txid, string message)
        => new(State: "pending", Ref: txid, Broadcasted: true, Message: message, Confirmations: 0, Required: RequiredConfirmations);

    // Collection listing stays Hosted-only (BYON's collection is created at SMV
    // registration, RFC-PLUGIN-006 P2-2).
    public Task<IReadOnlyList<MintCollection>> ListCollectionsAsync(CancellationToken cancellationToken = default)
        => throw new SelfCustodyMintNotAvailableException();

    // ── BYON single-asset issuance (RFC-PLUGIN-006 P2-1) ────────────────────────
    // The asset is minted on the merchant's OWN node: there is NO LN fee invoice
    // (the node pays the on-chain fee), so the flow skips AwaitingPayment. The SMV
    // service fee (platform margin) is charged later, at registration (P2-2).

    public Task<MintQuote> MintQuoteAsync(MintQuoteRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new MintQuote(
            OnchainFeeSats: 0,
            PlatformMarginSats: 0,
            TotalSats: 0,
            FeeRateSatPerVb: 0,
            Network: null,
            Note: "Self-custody: minted on your own node (you pay the on-chain fee). The SMV service fee applies at registration."));

    public async Task<MintResult> MintAsync(MintRequest request, CancellationToken cancellationToken = default)
    {
        // Add the seedling, then finalize (seal + broadcast) on the merchant's node.
        // P2-2c: when the controller supplies the STAS-01 canonical bytes, mint those
        // EXACTLY — so sha256(asset_meta.data) equals the metadata_hash the creator
        // signed and the SMV register step can bind the signature to the asset. The
        // legacy BuildMetaBytes remains only as a fallback for a canonical-less call.
        await _client.MintAssetAsync(
            new TapdMintAssetRequest(
                Name: request.AssetName,
                Amount: request.Supply.ToString(),
                MetaBytes: request.CanonicalMetaBytes ?? BuildMetaBytes(request)),
            cancellationToken);
        var batch = await _client.FinalizeBatchAsync(cancellationToken: cancellationToken);

        // Finalize does not echo the per-asset id — resolve it by name from the node.
        var assetId = await ResolveAssetIdByNameAsync(request.AssetName, cancellationToken);

        return new MintResult(
            MintRef: assetId ?? request.AssetName,
            State: MintState.Minting,
            Invoice: null,                 // BYON: no LN fee invoice — minted on the node
            ProviderState: batch.State);
    }

    public async Task<MintStatus> GetMintStatusAsync(string mintRef, CancellationToken cancellationToken = default)
    {
        var assets = await _client.ListAssetsAsync(cancellationToken);
        var asset = assets.FirstOrDefault(a =>
            string.Equals(a.AssetId, mintRef, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Name, mintRef, StringComparison.Ordinal));

        if (asset is null)
            return new MintStatus(MintState.Minting, mintRef, "Minting on your node…");

        // P2-1: the raw asset now exists on the node → minted. SMV registration and
        // the on-chain verification lifecycle (pending→confirmed) are P2-2.
        return new MintStatus(
            MintState.Minted,
            mintRef,
            "Minted on your node.",
            BdoId: asset.AssetId);
    }

    // Build the collectible metadata blob minted into asset_meta.data.
    private static byte[] BuildMetaBytes(MintRequest request)
    {
        var meta = new Dictionary<string, object?> { ["name"] = request.AssetName };
        if (!string.IsNullOrWhiteSpace(request.Description)) meta["description"] = request.Description;
        if (!string.IsNullOrWhiteSpace(request.AssetImageUrl)) meta["image"] = request.AssetImageUrl;
        if (request.Attributes is { Count: > 0 })
            meta["attributes"] = request.Attributes.Select(a => new { trait_type = a.TraitType, value = a.Value }).ToArray();
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta));
    }

    private async Task<string?> ResolveAssetIdByNameAsync(string name, CancellationToken cancellationToken)
    {
        var assets = await _client.ListAssetsAsync(cancellationToken);
        return assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))?.AssetId;
    }

    // BYON register (RFC-PLUGIN-006 P2-2c): export the minted asset's proof, base64-encoded
    // for managed-wallet-register-external-asset. tapd needs both the asset_id and its
    // script_key to export, and returns the proof as raw_proof_file HEX; we resolve the
    // script_key from the node by asset_id, then re-encode the proof to base64. Returns
    // null when the asset/script_key/proof isn't available yet (e.g. pre-anchor).
    public async Task<string?> ExportProofBase64Async(string assetId, CancellationToken cancellationToken = default)
    {
        var assets = await _client.ListAssetsAsync(cancellationToken);
        var asset = assets.FirstOrDefault(a => string.Equals(a.AssetId, assetId, StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.ScriptKey))
            return null;

        var proofHex = await _client.ExportProofAsync(assetId, asset.ScriptKey!, cancellationToken);
        if (string.IsNullOrWhiteSpace(proofHex))
            return null;

        return Convert.ToBase64String(Convert.FromHexString(proofHex));
    }

    // Batch mint is Hosted-only (Modality 3, RFC_BATCH_MINTING_V1); self-custody batch is Track B.
    public Task<MintBatchResult> MintBatchAsync(MintBatchRequest request, CancellationToken cancellationToken = default)
        => throw new SelfCustodyMintNotAvailableException();

    public Task<MintBatchStatus> GetMintBatchStatusAsync(string batchRef, CancellationToken cancellationToken = default)
        => throw new SelfCustodyMintNotAvailableException();

    // The holdings-by-collection endpoints are Hosted-only (RFC-PLUGIN-005 Phase 2);
    // BYON uses the client-side grouping in My BDOs. The controller branches on
    // IsCustodial, so these are a defensive guard and are never reached for BYON.
    public Task<IReadOnlyList<HeldCollection>> ListHeldCollectionsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Collection-grouped listing is a Hosted feature; BYON groups client-side.");

    public Task<HeldUnitsPage> ListHeldUnitsAsync(
        string collectionId, int? limit, string? cursor, string? q, string? sort,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Collection-grouped listing is a Hosted feature; BYON groups client-side.");

    public Task<IReadOnlyList<HeldGroup>> ListHeldGroupsAsync(
        string collectionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Collection-grouped listing is a Hosted feature; BYON groups client-side.");

    public Task<HeldUnitsPage> ListHeldUnitsInGroupAsync(
        string collectionId, string groupId, int? limit, string? cursor, string? q, string? sort,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Collection-grouped listing is a Hosted feature; BYON groups client-side.");

    public void Dispose() => _httpClient.Dispose();
}

/// <summary>Bitcoin Core RPC connection for BYON send-status confirmation reads.</summary>
public sealed record BitcoinRpcConfig(string Url, string User, string Password);

// Minimal JSON-RPC 1.0 envelope for getrawtransaction. Moved from SmvSendController.
internal sealed class BitcoinRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; } = "1.0";
    [JsonPropertyName("id")] public string Id { get; } = "smv-send-status";
    [JsonPropertyName("method")] public string Method { get; }
    [JsonPropertyName("params")] public object[] Params { get; }

    public BitcoinRpcRequest(string method, object[] parameters)
    {
        Method = method;
        Params = parameters;
    }
}
