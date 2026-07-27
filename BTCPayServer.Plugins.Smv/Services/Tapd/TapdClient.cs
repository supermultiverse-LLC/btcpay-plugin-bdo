using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BTCPayServer.Plugins.Smv.Services.Tapd;

public sealed class TapdClient : ITapdClient
{
    private readonly HttpClient _httpClient;

    public TapdClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<TapdInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        // TODO(PR-0 follow-up): this does NOT query tapd. The network value below is a
        // hardcoded placeholder and must NOT be surfaced to operators as authoritative.
        // Clean fix is one of: (a) add a Network field to SmvSettings, or
        // (b) have tapd-relay expose /info and read it here. Until then, callers must
        // treat Network as unknown.
        return Task.FromResult<TapdInfo?>(new TapdInfo
        {
            Version = "unknown",
            Network = "mainnet",
            LndIdentityPubkey = null
        });
    }

    public async Task<IReadOnlyList<TapdAsset>> ListAssetsAsync(CancellationToken cancellationToken = default)
    {
        // include_unconfirmed_mints: tapd hides mints whose anchor tx is still in
        // the mempool — the merchant's fresh mint would simply not exist in My BDOs
        // until the Bitcoin block lands. Listed here, flagged IsConfirming below.
        using var response = await _httpClient.GetAsync(
            "/v1/taproot-assets/assets?include_unconfirmed_mints=true", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TapdAsset>();
        }

        var assets = new List<TapdAsset>();

        foreach (var item in assetsElement.EnumerateArray())
        {
            var asset = item.TryGetProperty("asset", out var nestedAsset)
                ? nestedAsset
                : item;

            var genesis = asset.TryGetProperty("asset_genesis", out var genesisElement)
                ? genesisElement
                : default;

            // A mempool-anchored mint has an all-zeros (or empty) anchor_block_hash;
            // a confirmed asset always carries the real block hash. This is the ONLY
            // reliable discriminator across tapd generations: block_height reads 0
            // for EVERYTHING on tapd 0.3.x (field existed but was never populated),
            // which would badge every asset as confirming. A missing chain_anchor
            // object means an older payload shape — treat as confirmed (no badge).
            var anchor = asset.TryGetProperty("chain_anchor", out var anchorElement)
                ? anchorElement
                : default;
            var anchorBlockHash = anchor.ValueKind == JsonValueKind.Object
                ? TryGetString(anchor, "anchor_block_hash")
                : null;
            var isConfirming = anchor.ValueKind == JsonValueKind.Object
                && (string.IsNullOrEmpty(anchorBlockHash) || anchorBlockHash.TrimStart('0').Length == 0);

            // Recency key for the listing (newest mints first). 0 on tapd 0.3.x,
            // where the field is never populated — the sort degrades to tapd's
            // native order there, which is acceptable.
            long anchorHeight = 0;
            if (anchor.ValueKind == JsonValueKind.Object)
                _ = long.TryParse(TryGetString(anchor, "block_height"), out anchorHeight);

            assets.Add(new TapdAsset
            {
                IsConfirming = isConfirming,
                AnchorBlockHeight = anchorHeight,
                AssetId =
                    TryGetString(asset, "asset_id")
                    ?? (genesis.ValueKind == JsonValueKind.Object ? TryGetString(genesis, "asset_id") : null)
                    ?? TryGetString(asset, "id"),
                Name = genesis.ValueKind == JsonValueKind.Object ? TryGetString(genesis, "name") : null,
                Type =
                    TryGetString(asset, "asset_type")
                    ?? (genesis.ValueKind == JsonValueKind.Object ? TryGetString(genesis, "asset_type") : null),
                Amount = TryGetString(asset, "amount"),
                GenesisPoint = genesis.ValueKind == JsonValueKind.Object ? TryGetString(genesis, "genesis_point") : null,
                ScriptKey = TryGetString(asset, "script_key")
            });
        }

        return assets;
    }

    public async Task<IReadOnlyList<TapdReceiveEvent>> ListReceiveEventsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("/v1/taproot-assets/addrs/receives", cancellationToken);

        // Older relays / tapd builds may not expose this endpoint. Treat as "no
        // pending events" rather than an error so My Assets still renders.
        if (response.StatusCode == HttpStatusCode.NotImplemented || response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<TapdReceiveEvent>();
        }

        response.EnsureSuccessStatusCode();

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(rawJson);

        if (!document.RootElement.TryGetProperty("events", out var eventsElement) ||
            eventsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TapdReceiveEvent>();
        }

        var events = new List<TapdReceiveEvent>();

        foreach (var item in eventsElement.EnumerateArray())
        {
            var addr = item.TryGetProperty("addr", out var addrElement)
                ? addrElement
                : default;

            var receiveEvent = new TapdReceiveEvent
            {
                CreatedAtUnix = TryGetString(item, "creation_time_unix_seconds"),
                Encoded = addr.ValueKind == JsonValueKind.Object ? TryGetString(addr, "encoded") : null,
                AssetId = addr.ValueKind == JsonValueKind.Object ? TryGetString(addr, "asset_id") : null,
                AssetType = addr.ValueKind == JsonValueKind.Object ? TryGetString(addr, "asset_type") : null,
                Amount = addr.ValueKind == JsonValueKind.Object ? TryGetString(addr, "amount") : null,
                Status = TryGetString(item, "status"),
                Outpoint = TryGetString(item, "outpoint"),
                ConfirmationHeight = TryGetString(item, "confirmation_height"),
                HasProof = TryGetBool(item, "has_proof")
            };

            if (receiveEvent.IsPendingIncoming)
            {
                events.Add(receiveEvent);
            }
        }

        return events
            .OrderByDescending(e => e.CreatedAtUnix)
            .ToArray();
    }

    public async Task<TapdReceiveAddress> CreateAddressAsync(
        string assetId,
        string amount,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            asset_id = assetId,
            amt = amount
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "/v1/taproot-assets/addrs",
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;

        return new TapdReceiveAddress
        {
            Encoded = TryGetString(root, "encoded"),
            AssetId = TryGetString(root, "asset_id"),
            AssetType = TryGetString(root, "asset_type"),
            Amount = TryGetString(root, "amount"),
            ProofCourierAddr = TryGetString(root, "proof_courier_addr"),
            AssetVersion = TryGetString(root, "asset_version")
        };
    }

    public async Task<TapdSendResult> SendAsync(
        string taprootAssetAddress,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            tap_addrs = new[] { taprootAssetAddress }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "/v1/taproot-assets/send",
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        var transferId =
            TryGetString(root, "transfer_id")
            ?? TryGetString(root, "transfer_txid")
            ?? TryGetString(root, "txid")
            ?? TryGetNestedString(root, "transfer", "transfer_id")
            ?? TryGetNestedString(root, "transfer", "transfer_txid")
            ?? TryGetNestedString(root, "transfer", "txid");

        var anchorTxid =
            TryGetString(root, "anchor_txid")
            ?? TryGetString(root, "anchor_tx_hash")
            ?? TryGetString(root, "transfer_txid")
            ?? TryGetString(root, "txid")
            ?? TryGetNestedString(root, "transfer", "anchor_txid")
            ?? TryGetNestedString(root, "transfer", "anchor_tx_hash")
            ?? TryGetNestedString(root, "transfer", "transfer_txid")
            ?? TryGetNestedString(root, "transfer", "txid");

        var state =
            TryGetString(root, "state")
            ?? TryGetString(root, "status")
            ?? TryGetNestedString(root, "transfer", "state")
            ?? TryGetNestedString(root, "transfer", "status");

        return new TapdSendResult
        {
            TransferId = transferId,
            AnchorTxid = anchorTxid,
            State = state,
            RawJson = rawJson
        };
    }

    // ── BYON minting (RFC-PLUGIN-006 P2-1) ──────────────────────────────────────
    // Direct tapd REST. VERIFIED against the lab (tapd v0.3.3-alpha.rc1): byte fields
    // (asset_meta.data, asset_id, script_key, raw_proof_file) are lowercase HEX on the
    // wire in BOTH directions — NOT base64. Finalize does not return per-asset asset_id
    // (assigned at genesis); the backend resolves it via ListAssets by name afterwards.

    public async Task<TapdMintBatch> MintAssetAsync(TapdMintAssetRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            asset = new
            {
                asset_type = request.AssetType,
                name = request.Name,
                amount = request.Amount,
                asset_meta = new
                {
                    data = Convert.ToHexString(request.MetaBytes).ToLowerInvariant(),   // tapd REST bytes = hex
                    type = request.MetaType
                }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/v1/taproot-assets/assets", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseBatch(document.RootElement, "pending_batch");
    }

    public async Task<TapdMintBatch> FinalizeBatchAsync(int? feeRateSatPerVb = null, CancellationToken cancellationToken = default)
    {
        object payload = feeRateSatPerVb is int fr ? new { fee_rate = fr } : new { };

        using var response = await _httpClient.PostAsJsonAsync("/v1/taproot-assets/assets/mint/finalize", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseBatch(document.RootElement, "batch");
    }

    public async Task<string> ExportProofAsync(string assetIdHex, string scriptKeyHex, CancellationToken cancellationToken = default)
    {
        var payload = new { asset_id = assetIdHex, script_key = scriptKeyHex };   // tapd REST bytes = hex

        using var response = await _httpClient.PostAsJsonAsync("/v1/taproot-assets/proofs/export", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        // The proof file is returned hex-encoded under raw_proof_file. The SMV register
        // client base64-encodes it for the register-external-asset endpoint (P2-2).
        return TryGetString(document.RootElement, "raw_proof_file") ?? string.Empty;
    }

    public async Task<string?> FetchAssetMetaJsonAsync(string assetIdHex, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"/v1/taproot-assets/assets/meta/asset-id/{Uri.EscapeDataString(assetIdHex)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;   // no meta / older node — enrichment is best-effort

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var data = TryGetString(document.RootElement, "data");
            if (string.IsNullOrWhiteSpace(data))
                return null;

            // tapd REST encodes bytes as hex on this build (like proofs/export); newer
            // gateways emit base64. Try hex first, fall back to base64; accept only
            // payloads that decode to a JSON object (STAS-01 canonical metadata).
            foreach (var bytes in DecodeCandidates(data))
            {
                try
                {
                    var text = System.Text.Encoding.UTF8.GetString(bytes);
                    if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
                        return text;

                    // Double-encoded tolerance: relay mints before 2026-07-26 stored the
                    // hex TEXT itself as the on-chain metadata (the relay passed the wire
                    // hex straight to tapcli --meta_bytes). Those assets are immutable —
                    // unwrap one extra hex layer so they still render.
                    var trimmed = text.Trim();
                    if (trimmed.Length % 2 == 0 && trimmed.Length >= 2
                        && System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^[0-9a-fA-F]+$"))
                    {
                        var inner = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(trimmed));
                        if (inner.TrimStart().StartsWith("{", StringComparison.Ordinal))
                            return inner;
                    }
                }
                catch { /* try next decoding */ }
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<byte[]> DecodeCandidates(string data)
    {
        if (data.Length % 2 == 0 && System.Text.RegularExpressions.Regex.IsMatch(data, "^[0-9a-fA-F]+$"))
        {
            byte[]? hex = null;
            try { hex = Convert.FromHexString(data); } catch { /* not hex after all */ }
            if (hex is not null) yield return hex;
        }
        byte[]? b64 = null;
        try { b64 = Convert.FromBase64String(data); } catch { /* not base64 */ }
        if (b64 is not null) yield return b64;
    }

    // Parse a tapd MintingBatch (under wrapperKey) → TapdMintBatch. Byte fields are hex.
    // asset_id/script_key are absent from pending/finalize responses (assigned at genesis);
    // the backend resolves them via ListAssets by name after finalize.
    private TapdMintBatch ParseBatch(JsonElement root, string wrapperKey)
    {
        var batch = root.TryGetProperty(wrapperKey, out var b) ? b : root;
        var assets = new List<TapdMintedAsset>();
        if (batch.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                assets.Add(new TapdMintedAsset(
                    AssetId: TryGetString(a, "asset_id"),
                    ScriptKey: TryGetString(a, "script_key"),
                    Name: TryGetString(a, "name") ?? TryGetNestedString(a, "asset_genesis", "name")));
            }
        }
        return new TapdMintBatch(
            BatchKey: TryGetString(batch, "batch_key"),
            BatchTxid: TryGetString(batch, "batch_txid"),
            State: TryGetString(batch, "state"),
            Assets: assets);
    }

    public static HttpClient CreateHttpClient(string baseUrl, string macaroonHex, int timeoutMs)
    {
        var uri = new Uri(baseUrl.TrimEnd('/'));
        var handler = new HttpClientHandler();

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
            IsLocalOrDockerHost(uri.Host))
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", macaroonHex);
        client.DefaultRequestHeaders.Add("Grpc-Metadata-macaroon", macaroonHex);

        return client;
    }

    private static bool IsLocalOrDockerHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || host.Equals("tapd-qa", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryGetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static string? TryGetNestedString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(objectName, out var obj))
            return null;

        return TryGetString(obj, propertyName);
    }
}
