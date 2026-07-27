using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Client for <c>managed-wallet-register-external-asset</c> (v1.2.1, BYON Create —
/// RFC-PLUGIN-006 P2-2c). Projects a BYON-minted Taproot asset into a full SMV BDO:
/// verifies the creator envelope, charges the sync <c>byon_register_service_fee</c>, pins
/// the image, and indexes it. Contract SEALED by Lovable 2026-07-22.
///
/// Auth is the OAuth-obtained <c>mwv1_</c> (scope <c>assets:register_external</c>) carried
/// by the injected <see cref="HttpClient"/> (built via <see cref="ManagedWalletClient.CreateHttpClient"/>).
/// A duplicate <c>external_asset_id_hex</c> is idempotent → 200 <c>already_registered:true</c>,
/// no re-charge. Verification is async: the response is always <c>pending</c>; the plugin reads
/// <c>onchain_verification_status</c> from the Public API to see <c>confirmed</c>/<c>failed_deindexed</c>.
/// </summary>
public sealed class ExternalAssetRegistrationClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public ExternalAssetRegistrationClient(HttpClient http) => _http = http;

    /// <summary>POST the registration. <paramref name="idempotencyKey"/> is a required uuid-v4;
    /// the same key + a duplicate asset replays idempotently. Both 201 (new) and 200
    /// (already registered) are success.</summary>
    public async Task<RegisterExternalAssetResponse> RegisterAsync(
        RegisterExternalAssetRequest body, string idempotencyKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "managed-wallet-register-external-asset");
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw await ReadErrorAsync(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<RegisterExternalAssetResponse>(stream, Json, ct);
        return dto ?? throw new RegisterExternalAssetException((int)resp.StatusCode, null, null, "Empty registration response.");
    }

    /// <summary>Pin the STAS-01 canonical metadata to IPFS (§12.3, sealed 2026-07-22) and get
    /// its <c>ipfs://</c> URI for the register call. The server decodes <c>content_base64</c>,
    /// verifies <c>sha256(bytes) == metadata_hash</c> (422 on mismatch, before pinning), then
    /// pins the bytes verbatim — so the CID addresses the exact bytes the envelope hashed.
    /// Idempotent by key and by content-addressing.</summary>
    public async Task<PinMetadataResponse> PinMetadataAsync(
        PinMetadataRequest body, string idempotencyKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "managed-wallet-pin-metadata");
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw await ReadErrorAsync(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<PinMetadataResponse>(stream, Json, ct);
        return dto ?? throw new RegisterExternalAssetException((int)resp.StatusCode, null, null, "Empty pin response.");
    }

    // Sealed error surface: 400 invalid_request (+ code duplicate is N/A — dup is 200),
    // 401, 402 insufficient_balance, 403 insufficient_scope (detail not_allowlisted[...]).
    private static async Task<RegisterExternalAssetException> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string? code = null, detail = null;
        string message = $"Registration failed with HTTP {(int)resp.StatusCode}.";
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                code = Str(err, "code");
                message = Str(err, "message") ?? message;
                detail = Str(err, "detail");
            }
            else
            {
                code = Str(r, "code") ?? Str(r, "error");
                message = Str(r, "message") ?? message;
                detail = Str(r, "detail");
            }
        }
        catch { /* non-JSON body → fall through */ }

        return new RegisterExternalAssetException((int)resp.StatusCode, code, detail, message);
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;
}

// ── Request (SEALED shape). Snake_case via the naming policy; the envelope signers are
//    the verbatim StasEnvelope dictionaries so their keys pass through untouched. ────────
public sealed class RegisterExternalAssetRequest
{
    public string ExternalAssetIdHex { get; set; } = "";
    public RegisterAssetInfo Asset { get; set; } = new();
    public RegisterCollectionInfo Collection { get; set; } = new();
    public string MetadataHash { get; set; } = "";
    public string MetadataUri { get; set; } = "";
    public RegisterEnvelope Envelope { get; set; } = new();
    public string ProofBlobBase64 { get; set; } = "";
    // ADDITIVE (post v1.2.1): the exact canonical metadata bytes (the ones this
    // flow pinned via pin-metadata) so the backend can index asset_metadata
    // without a gateway round-trip. Optional — older backends ignore it, and
    // the backend falls back to fetching the pinned CID when absent.
    public string? CanonicalMetadataBase64 { get; set; }
}

public sealed class RegisterAssetInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class RegisterCollectionInfo
{
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
}

public sealed class RegisterEnvelope
{
    // stas-01-envelope v2 (B4 as-built): schema+version make the stored envelope
    // self-describing for every platform reader, not just parseSigners (which only
    // needs a non-empty signers array to select the v2 path).
    public string Schema { get; set; } = "stas-01-envelope";
    public int Version { get; set; } = 2;
    public string MetadataHash { get; set; } = "";
    // Each signer is a StasEnvelope.CreatorSignerNostr dictionary (keys emitted verbatim).
    public List<Dictionary<string, object>> Signers { get; set; } = new();
}

// ── Response (201 new / 200 already_registered). ────────────────────────────────────────
public sealed class RegisterExternalAssetResponse
{
    public string? AssetId { get; set; }
    public string? ExternalAssetIdHex { get; set; }
    public string? CollectionId { get; set; }
    public bool AlreadyRegistered { get; set; }
    public RegisterFee? Fee { get; set; }
    public RegisterVerification? Verification { get; set; }
    public string? MetadataHash { get; set; }
    public string? MetadataUri { get; set; }
}

public sealed class RegisterFee
{
    public long ChargedSats { get; set; }
    public long BalanceAfterSats { get; set; }
    public string? Currency { get; set; }
}

public sealed class RegisterVerification
{
    public string? Status { get; set; }   // "pending" on the immediate response
    public string? Note { get; set; }
}

// ── Pin metadata (§12.3). content_base64 = base64 of the EXACT canonical bytes. ────────
public sealed class PinMetadataRequest
{
    public string ContentBase64 { get; set; } = "";
    public string MetadataHash { get; set; } = "";
    public string ContentType { get; set; } = "application/json";
}

public sealed class PinMetadataResponse
{
    public string? MetadataUri { get; set; }   // ipfs://bafk…
    public string? Cid { get; set; }
    public long SizeBytes { get; set; }
    public string? MetadataHash { get; set; }
    public string? ContentType { get; set; }
    public bool AlreadyPinned { get; set; }
}

/// <summary>A registration failure. <see cref="Code"/> is the sealed error code
/// (<c>insufficient_balance</c>, <c>insufficient_scope</c>, <c>invalid_request</c>);
/// <see cref="Detail"/> carries extras like <c>not_allowlisted[...]</c>.</summary>
public sealed class RegisterExternalAssetException : Exception
{
    public int StatusCode { get; }
    public string? Code { get; }
    public string? Detail { get; }
    public RegisterExternalAssetException(int statusCode, string? code, string? detail, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = detail;
    }
}
