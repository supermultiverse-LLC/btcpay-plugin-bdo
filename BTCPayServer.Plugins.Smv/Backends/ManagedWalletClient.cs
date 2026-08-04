using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Core;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Thin HTTP+JSON client for the Managed Wallet API v1.1 (contract §5). The bearer
/// token IS the wallet identity (contract §2), so no <c>wallet_id</c>/<c>user_id</c>
/// is ever sent. Non-2xx responses are parsed from the §3.3 error envelope into a
/// typed <see cref="ManagedWalletApiException"/> (unknown codes fail closed).
///
/// Mirrors the ownership model of <c>TapdClient</c>: the caller (the backend) owns
/// and disposes the <see cref="HttpClient"/> built by <see cref="CreateHttpClient"/>.
/// Covers the v1.1 read/send endpoints and the additive v1.2 issuance endpoints
/// (mint-quote / mint / mint-status / collections, contract §4–§5).
/// </summary>
public sealed class ManagedWalletClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public ManagedWalletClient(HttpClient http) => _http = http;

    /// <summary>GET /managed-wallet-get (contract §5.1).</summary>
    public Task<ManagedWalletDto> GetWalletAsync(CancellationToken ct = default)
        => GetAsync<ManagedWalletDto>("managed-wallet-get", ct);

    /// <summary>GET /managed-wallet-assets (contract §5.2). Returns the item list.</summary>
    public async Task<IReadOnlyList<ManagedAssetItem>> ListAssetsAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync<ManagedAssetsResponse>("managed-wallet-assets", ct);
        return resp.Items;
    }

    /// <summary>GET /managed-wallet-transfer-status/&lt;ref&gt; (contract §5.5).</summary>
    public Task<ManagedTransferStatus> GetTransferStatusAsync(string transferRef, CancellationToken ct = default)
        => GetAsync<ManagedTransferStatus>($"managed-wallet-transfer-status/{Uri.EscapeDataString(transferRef)}", ct);

    /// <summary>
    /// POST /managed-wallet-send (contract §10.1). Requires an <paramref name="idempotencyKey"/>:
    /// the SAME key + body replays the cached response; a different body → idempotency_conflict.
    /// Returns the transfer-status envelope plus the LN fee <c>payment</c> block.
    /// </summary>
    public async Task<ManagedTransferStatus> SendAsync(ManagedSendRequest body, string idempotencyKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "managed-wallet-send");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            throw await ReadErrorAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<ManagedTransferStatus>(stream, JsonOptions, ct);

        if (dto is null)
        {
            throw new ManagedWalletApiException(
                ManagedWalletErrorCode.ServerError,
                (int)response.StatusCode,
                "Empty response from the Managed Wallet API.");
        }

        return dto;
    }

    // ── Mint-credits top-up (additive, post v1.2.1) ────────────────────────────

    /// <summary>GET /managed-wallet-topup — balance + active packages.</summary>
    public Task<ManagedTopupInfo> GetTopupInfoAsync(CancellationToken ct = default)
        => GetAsync<ManagedTopupInfo>("managed-wallet-topup", ct);

    /// <summary>
    /// POST /managed-wallet-topup — create (or idempotently replay) the LN
    /// top-up invoice for a package. Same <paramref name="clientRequestId"/> →
    /// same invoice; settlement credits the balance server-side only.
    /// </summary>
    public Task<ManagedTopupInvoice> CreateTopupInvoiceAsync(string packageId, string clientRequestId, CancellationToken ct = default)
        => PostJsonAsync<ManagedTopupInvoice>(
            "managed-wallet-topup",
            new { package_id = packageId, client_request_id = clientRequestId },
            idempotencyKey: null, ct);

    /// <summary>GET /managed-wallet-topup?intent_id=… — paid yes/no + fresh balance.</summary>
    public Task<ManagedTopupStatus> GetTopupStatusAsync(string intentId, CancellationToken ct = default)
        => GetAsync<ManagedTopupStatus>($"managed-wallet-topup?intent_id={Uri.EscapeDataString(intentId)}", ct);

    // ── Send-to-customer claim links (additive; journey GAP G2) ────────────────

    /// <summary>POST /managed-wallet-send-claim — turn a held BDO into a claim link.</summary>
    public Task<ManagedClaimLink> CreateClaimLinkAsync(string assetId, CancellationToken ct = default)
        => PostJsonAsync<ManagedClaimLink>("managed-wallet-send-claim", new { asset_id = assetId }, idempotencyKey: null, ct);

    /// <summary>GET /managed-wallet-send-claim — open (unclaimed) links.</summary>
    public Task<ManagedClaimLinkList> ListClaimLinksAsync(CancellationToken ct = default)
        => GetAsync<ManagedClaimLinkList>("managed-wallet-send-claim", ct);

    /// <summary>POST /managed-wallet-send-claim {cancel_code} — cancel an open link.</summary>
    public Task CancelClaimLinkAsync(string code, CancellationToken ct = default)
        => PostJsonAsync<ManagedClaimLink>("managed-wallet-send-claim", new { cancel_code = code }, idempotencyKey: null, ct);

    /// <summary>POST /managed-wallet-send-claim {redeem_code} — redeem a claim code INTO
    /// this Store's account (receive-side of a claim; requires assets:receive).</summary>
    public Task<ManagedClaimRedeemResult> RedeemClaimAsync(string code, CancellationToken ct = default)
        => PostJsonAsync<ManagedClaimRedeemResult>("managed-wallet-send-claim", new { redeem_code = code }, idempotencyKey: null, ct);

    /// <summary>POST — create a DROP: one URL/QR dispensing the given held units
    /// first come, first served (RFC-PLUGIN-010). <paramref name="rewardCreditSats"/>
    /// optionally gifts credits per claim, funded from THIS account's balance.</summary>
    public Task<ManagedCampaignCreated> CreateCampaignAsync(string name, IEnumerable<string> assetIds, long rewardCreditSats = 0, CancellationToken ct = default)
        => PostJsonAsync<ManagedCampaignCreated>(
            "managed-wallet-send-claim",
            new { campaign_name = name, campaign_asset_ids = assetIds.ToArray(), reward_credit_sats = rewardCreditSats },
            idempotencyKey: null, ct);

    /// <summary>GET ?campaigns=1 — the caller's active drops with live counters.</summary>
    public Task<ManagedCampaignList> ListCampaignsAsync(CancellationToken ct = default)
        => GetAsync<ManagedCampaignList>("managed-wallet-send-claim?campaigns=1", ct);

    /// <summary>POST {campaign_cancel} — close a drop (claimed units untouched).</summary>
    public Task CancelCampaignAsync(string campaignId, CancellationToken ct = default)
        => PostJsonAsync<ManagedClaimRedeemResult>("managed-wallet-send-claim", new { campaign_cancel = campaignId }, idempotencyKey: null, ct);

    // ── Premium subscription (additive; journey GAP G1) ────────────────────────

    /// <summary>GET /managed-wallet-subscribe — current plan + purchasable tiers.</summary>
    public Task<ManagedSubscriptionInfo> GetSubscriptionInfoAsync(CancellationToken ct = default)
        => GetAsync<ManagedSubscriptionInfo>("managed-wallet-subscribe", ct);

    /// <summary>POST /managed-wallet-subscribe — create/replay the tier's LN invoice.</summary>
    public Task<ManagedSubscriptionInvoice> CreateSubscriptionInvoiceAsync(string tierName, string clientRequestId, CancellationToken ct = default)
        => PostJsonAsync<ManagedSubscriptionInvoice>(
            "managed-wallet-subscribe",
            new { tier_name = tierName, client_request_id = clientRequestId },
            idempotencyKey: null, ct);

    /// <summary>GET /managed-wallet-subscribe?intent_id=… — paid + active tier.</summary>
    public Task<ManagedSubscriptionStatus> GetSubscriptionStatusAsync(string intentId, CancellationToken ct = default)
        => GetAsync<ManagedSubscriptionStatus>($"managed-wallet-subscribe?intent_id={Uri.EscapeDataString(intentId)}", ct);

    // ── Issuance API v1.2 (contract §4, §5) ────────────────────────────────────

    /// <summary>GET /managed-wallet-collections (contract §4). Returns the item list.</summary>
    public async Task<IReadOnlyList<ManagedCollectionItem>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync<ManagedCollectionsResponse>("managed-wallet-collections", ct);
        return resp.Items;
    }

    /// <summary>
    /// POST /managed-wallet-mint-quote (contract §5.1). Stateless cost estimate —
    /// no idempotency key, no reservation, no invoice. The returned total is an
    /// estimate; the committing quote at <see cref="MintAsync"/> may differ.
    /// </summary>
    public Task<ManagedMintQuoteResponse> MintQuoteAsync(ManagedMintQuoteRequest body, CancellationToken ct = default)
        => PostJsonAsync<ManagedMintQuoteResponse>("managed-wallet-mint-quote", body, idempotencyKey: null, ct);

    /// <summary>
    /// POST /managed-wallet-mint (contract §5.2). Requires a fresh
    /// <paramref name="idempotencyKey"/> per attempt: the SAME key + body replays
    /// the cached 202; a different body → idempotency_conflict. Returns the 202
    /// envelope with the LN fee <c>invoice</c> inline.
    /// </summary>
    public Task<ManagedMintResponse> MintAsync(ManagedMintRequest body, string idempotencyKey, CancellationToken ct = default)
        => PostJsonAsync<ManagedMintResponse>("managed-wallet-mint", body, idempotencyKey, ct);

    /// <summary>GET /managed-wallet-mint-status/&lt;ref&gt; (contract §5.3).</summary>
    public Task<ManagedMintStatus> GetMintStatusAsync(string mintRef, CancellationToken ct = default)
        => GetAsync<ManagedMintStatus>($"managed-wallet-mint-status/{Uri.EscapeDataString(mintRef)}", ct);

    // ── Receive (v1.2.1 additive) ──────────────────────────────────────────────

    /// <summary>POST /managed-wallet-receive-address. Generates a fresh single-use
    /// Taproot address for <paramref name="body"/>.AssetId on the SMV node; inbound
    /// payment lands in the token's custodial wallet. No Idempotency-Key by design
    /// (each call mints a distinct address). Requires the <c>assets:receive</c> scope.</summary>
    public Task<ManagedReceiveAddressResponse> CreateReceiveAddressAsync(ManagedReceiveAddressRequest body, CancellationToken ct = default)
        => PostJsonAsync<ManagedReceiveAddressResponse>("managed-wallet-receive-address", body, null, ct);

    // ── Batch mint (v1.2.1 additive, RFC_BATCH_MINTING_V1 — the moat) ───────────

    /// <summary>POST /managed-wallet-mint-batch. Async submit — returns 202 with a
    /// <c>batch_ref</c> + the aggregate LN fee invoice inline. Requires a fresh
    /// Idempotency-Key per attempt (same key + body replays; different body conflicts).</summary>
    public Task<ManagedMintBatchResponse> MintBatchAsync(ManagedMintBatchRequest body, string idempotencyKey, CancellationToken ct = default)
        => PostJsonAsync<ManagedMintBatchResponse>("managed-wallet-mint-batch", body, idempotencyKey, ct);

    /// <summary>GET /managed-wallet-mint-batch-status/&lt;batch_ref&gt;.</summary>
    public Task<ManagedMintBatchStatus> GetMintBatchStatusAsync(string batchRef, CancellationToken ct = default)
        => GetAsync<ManagedMintBatchStatus>($"managed-wallet-mint-batch-status/{Uri.EscapeDataString(batchRef)}", ct);

    // ── Holdings-by-collection (v1.2.1 additive, RFC-PLUGIN-005 Phase 2) ────────

    /// <summary>GET /managed-wallet-holdings-collections. The merchant's held collections
    /// with owned_count (possession) vs collection_size (§7 R1/R3). Single-page in v1.2.1.</summary>
    public async Task<IReadOnlyList<ManagedHoldingCollection>> ListHoldingsCollectionsAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync<ManagedHoldingsCollectionsResponse>("managed-wallet-holdings-collections", ct);
        return resp.Items;
    }

    /// <summary>GET /managed-wallet-holdings-units — a collection's held units, cursor-paginated
    /// (§7 R2/R4). <paramref name="cursor"/> is the opaque acquired_at marker from the prior page.</summary>
    public Task<ManagedHoldingsUnitsResponse> ListHoldingsUnitsAsync(
        string collectionId, int? limit, string? cursor, string? q, string? sort, CancellationToken ct = default)
        => ListHoldingsUnitsAsync(collectionId, limit, cursor, q, sort, null, ct);

    /// <summary>As above, optionally narrowed to ONE group — a series uuid, or
    /// "asset:{uuid}" for a BDO minted alone.</summary>
    public Task<ManagedHoldingsUnitsResponse> ListHoldingsUnitsAsync(
        string collectionId, int? limit, string? cursor, string? q, string? sort,
        string? groupId, CancellationToken ct = default)
    {
        var url = new StringBuilder("managed-wallet-holdings-units?collection_id=");
        url.Append(Uri.EscapeDataString(collectionId));
        if (limit is int l) url.Append("&limit=").Append(l);
        if (!string.IsNullOrEmpty(cursor)) url.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        if (!string.IsNullOrEmpty(q)) url.Append("&q=").Append(Uri.EscapeDataString(q));
        if (!string.IsNullOrEmpty(sort)) url.Append("&sort=").Append(Uri.EscapeDataString(sort));
        if (!string.IsNullOrEmpty(groupId)) url.Append("&group_id=").Append(Uri.EscapeDataString(groupId));
        return GetAsync<ManagedHoldingsUnitsResponse>(url.ToString(), ct);
    }

    /// <summary>GET the same collection as GROUPS — one row per series, plus each
    /// BDO minted alone. Counted over everything held, not over a page.</summary>
    public Task<ManagedHeldGroupsResponse> ListHeldGroupsAsync(string collectionId, CancellationToken ct = default)
        => GetAsync<ManagedHeldGroupsResponse>(
            "managed-wallet-holdings-units?groups=1&collection_id=" + Uri.EscapeDataString(collectionId), ct);

    // ── Event check-in (RFC-INTEGRATION-002 §5, RFC-PLUGIN-013 F4) ────────────
    // The plugin manages events and their ticket types. The door scan is NOT here:
    // an integrator runs their own scanner against the API, and a merchant-facing
    // camera scanner is a separate slice with its own problems.

    /// <summary>GET /managed-wallet-checkin?events=1 — this account's events.</summary>
    public Task<ManagedCheckinEventsResponse> ListCheckinEventsAsync(CancellationToken ct = default)
        => GetAsync<ManagedCheckinEventsResponse>("managed-wallet-checkin?events=1", ct);

    /// <summary>GET one event's live counters.</summary>
    public Task<ManagedCheckinEventResponse> GetCheckinEventAsync(string eventId, CancellationToken ct = default)
        => GetAsync<ManagedCheckinEventResponse>(
            "managed-wallet-checkin?event_id=" + Uri.EscapeDataString(eventId), ct);

    /// <summary>GET this event's ticket types AND the series still free to become one.
    /// Both in a single call — the filter of what is selectable lives server-side so
    /// the two never disagree about which series are already spoken for.</summary>
    public Task<ManagedTicketTypesResponse> ListTicketTypesAsync(string eventId, CancellationToken ct = default)
        => GetAsync<ManagedTicketTypesResponse>(
            "managed-wallet-checkin?ticket_types=1&event_id=" + Uri.EscapeDataString(eventId), ct);

    public Task<ManagedEventCreated> CreateCheckinEventAsync(
        string name, string collectionId, CancellationToken ct = default)
        => PostJsonAsync<ManagedEventCreated>("managed-wallet-checkin",
            new { event_create = new { name, collection_id = collectionId } }, null, ct);

    public Task<JsonElement> CloseCheckinEventAsync(string eventId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>("managed-wallet-checkin", new { event_close = eventId }, null, ct);

    /// <summary>Declare a group as one of the event's ticket types. groupId is a
    /// series uuid, or "asset:{uuid}" for a BDO minted on its own — which is a
    /// group of one, not a member of a "singles" bucket.</summary>
    public Task<ManagedTicketTypeCreated> AddTicketTypeAsync(
        string eventId, string groupId, string? label, CancellationToken ct = default)
        => PostJsonAsync<ManagedTicketTypeCreated>("managed-wallet-checkin",
            new { ticket_type_add = new { event_id = eventId, group_id = groupId, label } }, null, ct);

    public Task<JsonElement> RemoveTicketTypeAsync(string ticketTypeId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>("managed-wallet-checkin",
            new { ticket_type_remove = ticketTypeId }, null, ct);

    // ── Organisers (RFC-PLUGIN-012 P3) ─────────────────────────────────────
    // Who besides the issuer may run this collection's doors. All three are
    // issuer-only at the platform: a grantee works the door and cannot pass
    // that on, nor see who else holds a grant.

    public Task<ManagedOrganizersResponse> ListOrganizersAsync(string collectionId, CancellationToken ct = default)
        => GetAsync<ManagedOrganizersResponse>(
            "managed-wallet-checkin?organizers=1&collection_id=" + Uri.EscapeDataString(collectionId), ct);

    /// <summary>Grant by the email the person signs in with — the platform
    /// resolves it to an account, and answers user_not_found if there is none
    /// yet, which is something the issuer can act on.</summary>
    public Task<ManagedOrganizerGranted> GrantOrganizerAsync(
        string collectionId, string email, CancellationToken ct = default)
        => PostJsonAsync<ManagedOrganizerGranted>("managed-wallet-checkin",
            new { organizer_grant = new { collection_id = collectionId, email } }, null, ct);

    public Task<JsonElement> RevokeOrganizerAsync(string grantId, CancellationToken ct = default)
        => PostJsonAsync<JsonElement>("managed-wallet-checkin",
            new { organizer_revoke = grantId }, null, ct);

    // Shared POST helper: serialize the body, attach an optional Idempotency-Key,
    // and parse the JSON response (or the §3.3/§6 error envelope). Mirrors the
    // GetAsync<T> contract: non-2xx → typed exception, empty body → ServerError.
    private async Task<T> PostJsonAsync<T>(string relativePath, object body, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            throw await ReadErrorAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);

        if (dto is null)
        {
            throw new ManagedWalletApiException(
                ManagedWalletErrorCode.ServerError,
                (int)response.StatusCode,
                "Empty response from the Managed Wallet API.");
        }

        return dto;
    }

    private async Task<T> GetAsync<T>(string relativePath, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativePath, ct);

        if (!response.IsSuccessStatusCode)
            throw await ReadErrorAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);

        if (dto is null)
        {
            throw new ManagedWalletApiException(
                ManagedWalletErrorCode.ServerError,
                (int)response.StatusCode,
                "Empty response from the Managed Wallet API.");
        }

        return dto;
    }

    // Parse the sealed error envelope: { "error": { "code", "message" }, "retry_after" }.
    private static async Task<ManagedWalletApiException> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        string message = $"Managed Wallet API returned HTTP {(int)response.StatusCode}.";
        int? retryAfter = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code = c.GetString();
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = m.GetString() ?? message;
            }

            if (root.TryGetProperty("retry_after", out var ra) && ra.ValueKind == JsonValueKind.Number)
                retryAfter = ra.GetInt32();
        }
        catch
        {
            // Non-JSON or unexpected body: fall through with a null code, which
            // maps to ServerError (fail closed).
        }

        // Fall back to the transport Retry-After header if the body omitted it.
        if (retryAfter is null && response.Headers.RetryAfter?.Delta is TimeSpan d)
            retryAfter = (int)d.TotalSeconds;

        return new ManagedWalletApiException(
            ManagedWalletErrorCodes.Parse(code),
            (int)response.StatusCode,
            message,
            retryAfter);
    }

    /// <summary>
    /// Builds the per-request client: base URL + <c>Authorization: Bearer &lt;token&gt;</c>.
    /// TLS validation is relaxed only for local/docker hosts (dev/lab), exactly as
    /// <c>TapdClient.CreateHttpClient</c> does; the production gateway is strict HTTPS.
    /// </summary>
    public static HttpClient CreateHttpClient(string baseUrl, string token, int timeoutMs,
        Func<HttpMessageHandler, HttpMessageHandler>? wrapHandler = null)
    {
        // BaseAddress must end with '/' and request paths must be relative (no
        // leading '/') so they append under the functions path prefix.
        var uri = new Uri(baseUrl.TrimEnd('/') + "/");
        var inner = new HttpClientHandler();

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && IsLocalOrDockerHost(uri.Host))
            inner.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        // OAuth Stores wrap the transport in the reactive re-auth handler (RFC-007 §11.3).
        var handler = wrapHandler is null ? (HttpMessageHandler)inner : wrapHandler(inner);

        var client = new HttpClient(handler)
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BTCPayServer.Plugins.Smv/0.1");

        return client;
    }

    private static bool IsLocalOrDockerHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
}
