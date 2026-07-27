using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Settings;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// RFC-PLUGIN-009: the public claim page's data client. Talks to the platform's
/// PostgREST surface with exactly the credentials the public PWA uses — the anon
/// key for the code lookup, the RECIPIENT's own JWT (obtained via the v0.14.0
/// email-code flow) for the claim itself. The JWT lives only for the duration of
/// the verify-and-claim request; nothing is persisted in BTCPay.
/// </summary>
public sealed class SmvClaimPublicClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _restBase;   // …/rest/v1, no trailing slash
    private readonly string _anonKey;

    public SmvClaimPublicClient(HttpClient http, SmvServerSettings server)
    {
        _http = http;
        // PostgREST lives on the same Supabase project as GoTrue — derive it from
        // the sealed issuer base rather than adding yet another server setting.
        _restBase = server.OAuthIssuerBase.TrimEnd('/')
            .Replace("/auth/v1", "/rest/v1", StringComparison.OrdinalIgnoreCase);
        _anonKey = server.SupabaseAnonKey;
    }

    public sealed record ClaimLookup(
        string? EntryId,
        string? AssetId,
        string? Status,
        string? AssetName,
        string? AssetDescription,
        string? AssetImageUrl,
        string? CollectionName,
        string? IssuerName);

    /// <summary>RPC lookup_claim(code) — anon, same as the PWA. Null = code unknown.</summary>
    public async Task<ClaimLookup?> LookupAsync(string code, CancellationToken ct)
    {
        using var doc = await RpcAsync("lookup_claim", new { code }, bearer: _anonKey, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;
        var r = doc.RootElement[0];
        return new ClaimLookup(
            Str(r, "entry_id"), Str(r, "asset_id"), Str(r, "status"),
            Str(r, "asset_name"), Str(r, "asset_description"), Str(r, "asset_image_url"),
            Str(r, "collection_name"), Str(r, "issuer_name"));
    }

    public sealed record ClaimOutcome(bool Success, string? ErrorCode, string? ErrorMessage);

    /// <summary>RPC execute_claim(code, _wallet_id) with the recipient's JWT.
    /// The personal wallet id is looked up best-effort first (RLS-scoped),
    /// mirroring the PWA — the claim proceeds without it.</summary>
    public async Task<ClaimOutcome> ExecuteAsync(string code, string recipientJwt, CancellationToken ct)
    {
        string? walletId = null;
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"{_restBase}/wallets?select=id&wallet_type=eq.personal&limit=1");
            Decorate(req, recipientJwt);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    walletId = Str(doc.RootElement[0], "id");
            }
        }
        catch { /* non-fatal: claim proceeds without wallet binding */ }

        using var result = await RpcAsync("execute_claim", new { code, _wallet_id = walletId }, recipientJwt, ct);
        if (result is null || result.RootElement.ValueKind != JsonValueKind.Array || result.RootElement.GetArrayLength() == 0)
            return new ClaimOutcome(false, "empty_response", null);
        var row = result.RootElement[0];
        var success = row.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        return new ClaimOutcome(success, Str(row, "error_code"), Str(row, "error_message"));
    }

    public sealed record CampaignInfo(
        string? CampaignId, string? Name, string? Status,
        long Total, long Claimed,
        string? AssetName, string? AssetImageUrl, string? CollectionName, string? IssuerName);

    /// <summary>RPC lookup_campaign — anon read for the public drop page. Null = unknown.</summary>
    public async Task<CampaignInfo?> LookupCampaignAsync(string campaignId, CancellationToken ct)
    {
        using var doc = await RpcAsync("lookup_campaign", new { p_campaign = campaignId }, bearer: _anonKey, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;
        var r = doc.RootElement[0];
        return new CampaignInfo(
            Str(r, "campaign_id"), Str(r, "name"), Str(r, "status"),
            Long(r, "total"), Long(r, "claimed"),
            Str(r, "asset_name"), Str(r, "asset_image_url"), Str(r, "collection_name"), Str(r, "issuer_name"));
    }

    public sealed record DropClaimOutcome(bool Success, string? ErrorCode, string? ErrorMessage, string? AssetName);

    /// <summary>RPC claim_next_from_campaign with the recipient's JWT — atomically
    /// assigns the next available unit of the drop to the caller (RFC-PLUGIN-010).</summary>
    public async Task<DropClaimOutcome> ClaimNextAsync(string campaignId, string recipientJwt, CancellationToken ct)
    {
        string? walletId = null;
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"{_restBase}/wallets?select=id&wallet_type=eq.personal&limit=1");
            Decorate(req, recipientJwt);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    walletId = Str(doc.RootElement[0], "id");
            }
        }
        catch { /* non-fatal */ }

        using var result = await RpcAsync("claim_next_from_campaign",
            new { p_campaign = campaignId, p_wallet_id = walletId }, recipientJwt, ct);
        if (result is null || result.RootElement.ValueKind != JsonValueKind.Array || result.RootElement.GetArrayLength() == 0)
            return new DropClaimOutcome(false, "empty_response", null, null);
        var row = result.RootElement[0];
        var success = row.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        return new DropClaimOutcome(success, Str(row, "error_code"), Str(row, "error_message"), Str(row, "asset_name"));
    }

    private static long Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.TryGetInt64(out var v) ? v : 0;

    private async Task<JsonDocument?> RpcAsync(string fn, object body, string bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_restBase}/rpc/{fn}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json")
        };
        Decorate(req, bearer);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    private void Decorate(HttpRequestMessage req, string bearer)
    {
        req.Headers.TryAddWithoutValidation("apikey", _anonKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;
}
