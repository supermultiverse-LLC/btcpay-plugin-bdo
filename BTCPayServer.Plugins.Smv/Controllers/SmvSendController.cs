using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("stores/{storeId}/plugins/smv")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes.Cookie)]
public sealed class SmvSendController : Controller
{
    // Fallback confirmation target for the UI response when a backend does not
    // report one. Send-status confirmation logic itself lives in the backend
    // (P3-H2): BYON reads Bitcoin Core RPC, Hosted reads transfer-status.
    private const int RequiredConfirmations = 1;

    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
    };

    private readonly IAssetBackendResolver _backends;
    private readonly ILogger<SmvSendController> _log;

    public SmvSendController(
        IAssetBackendResolver backends,
        ILogger<SmvSendController> log)
    {
        _backends = backends;
        _log = log;
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send(CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // Deserialize with System.Text.Json (honours [JsonPropertyName]). BTCPay's
        // global Newtonsoft [FromBody] binder ignores it and would drop asset_id.
        var request = await ReadJsonBodyAsync<UiSendAssetRequest>(cancellationToken);

        if (request is null || string.IsNullOrWhiteSpace(request.Address))
        {
            return JsonStj(new UiSendErrorResponse(
                "address is required.",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        if (request.Amount == 0)
        {
            return JsonStj(new UiSendErrorResponse(
                "amount must be greater than zero.",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        var address = request.Address.Trim();

        if (!TaprootAssetAddress.HasValidPrefix(address))
        {
            return JsonStj(new UiSendErrorResponse(
                "address must be a Taproot Asset address (tapbc1 / taptb1 / taprt1).",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        var assetId = request.AssetId?.Trim();
        var amount = request.Amount;

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        if (backend is null)
        {
            _log.LogWarning("ui_send_asset.not_configured");
            return JsonStj(new UiSendErrorResponse(
                "Taproot Assets wallet is not configured on this BTCPay instance.",
                "NOT_CONFIGURED"), (int)HttpStatusCode.ServiceUnavailable);
        }

        try
        {
            _log.LogInformation(
                "ui_send_asset.request asset_id={AssetId} amount={Amount} dest_prefix={Dest} dest_len={Len}",
                assetId, amount,
                address.Length >= 12 ? address.Substring(0, 12) : address,
                address.Length);

            // asset_id + amount are carried for Hosted (the Managed Wallet send body);
            // BYON ignores them (the taprt1/tapbc1 address already encodes the asset).
            var result = await backend.SendAsync(
                new SendRequest(address, AssetId: assetId, Amount: amount.ToString()),
                cancellationToken);

            _log.LogInformation(
                "ui_send_asset.ok asset_id={AssetId} amount={Amount} state={State} transfer={TransferId} anchor={AnchorTxid} invoiced={Invoiced}",
                assetId,
                amount,
                result.ProviderState,
                result.TransferRef,
                result.Txid,
                result.Payment is not null);

            var payment = result.Payment is { Bolt11: not null } inv
                ? new UiPaymentBlock(inv.Bolt11, inv.AmountSats, inv.ExpiresAt)
                : null;

            return JsonStj(new UiSendAssetResponse(
                result.ProviderState ?? "submitted",
                result.TransferRef,
                result.Txid,
                payment));
        }
        catch (HostedFeatureNotAvailableException ex)
        {
            _log.LogWarning("ui_send_asset.hosted_unavailable {Message}", ex.Message);
            return JsonStj(new UiSendErrorResponse(ex.Message, "NOT_AVAILABLE"),
                (int)HttpStatusCode.NotImplemented);
        }
        catch (ManagedWalletApiException ex)
        {
            _log.LogWarning("ui_send_asset.hosted_error code={Code} status={Status} message={Message}", ex.Code, ex.HttpStatus, ex.Message);
            var (message, httpStatus) = MapHostedSendError(ex);
            // Surface the backend's human message (contract §3.3) when present — it is
            // more specific than the generic mapping (e.g. why a send was invalid).
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? message : $"{message} — {ex.Message}";
            return JsonStj(new UiSendErrorResponse(detail, ex.Code.ToString()), httpStatus);
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("ui_send_asset.timeout");
            return JsonStj(new UiSendErrorResponse(
                "The wallet backend did not respond in time.",
                "BACKEND_ERROR"), (int)HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "ui_send_asset.network_error");
            return JsonStj(new UiSendErrorResponse(
                $"Cannot reach the wallet backend: {ex.Message}",
                "BACKEND_ERROR"), (int)HttpStatusCode.BadGateway);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ui_send_asset.unexpected");
            return JsonStj(new UiSendErrorResponse(
                "Unexpected error sending Taproot Asset.",
                "BACKEND_ERROR"), (int)HttpStatusCode.InternalServerError);
        }
    }

    // Maps a Managed Wallet send failure (contract §10.1) to a user-facing message
    // and HTTP status. Unknown codes fail closed to a generic upstream error.
    private static (string Message, int HttpStatus) MapHostedSendError(ManagedWalletApiException ex) => ex.Code switch
    {
        ManagedWalletErrorCode.InsufficientBalance => ("The wallet does not hold enough of this asset to send.", (int)HttpStatusCode.UnprocessableEntity),
        ManagedWalletErrorCode.AssetNotFound       => ("That asset was not found in the wallet.", (int)HttpStatusCode.NotFound),
        ManagedWalletErrorCode.InvalidRequest      => ("The send request was rejected as invalid (check the address and amount).", (int)HttpStatusCode.BadRequest),
        ManagedWalletErrorCode.InvalidPath         => ("The send request was rejected as invalid.", (int)HttpStatusCode.BadRequest),
        ManagedWalletErrorCode.PaymentRequired     => ("A previous send for this asset is still awaiting its fee payment. Please try again to start a fresh one.", (int)HttpStatusCode.PaymentRequired),
        ManagedWalletErrorCode.IdempotencyConflict => ("A conflicting send is already in progress. Please retry.", (int)HttpStatusCode.Conflict),
        ManagedWalletErrorCode.Unauthorized        => ("Your Supermultiverse connection is no longer valid. Reconnect your account in Settings (or re-enter a token under Advanced).", (int)HttpStatusCode.Unauthorized),
        ManagedWalletErrorCode.InsufficientScope   => ("The hosted wallet token is not allowed to send. Re-issue a token with the assets:send scope.", (int)HttpStatusCode.Forbidden),
        ManagedWalletErrorCode.RateLimited         => ("The wallet service is rate-limiting requests. Please wait and try again.", 429),
        _                                          => ("The wallet service reported an error completing the send.", (int)HttpStatusCode.BadGateway),
    };

    [HttpGet("send/status/{txid}")]
    public async Task<IActionResult> SendStatus(
        string txid,
        CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // BYON uses a 64-hex txid; Hosted uses a transfer_ref UUID. Accept either;
        // the resolved backend interprets it. This widens acceptance only — a valid
        // BYON txid is still accepted exactly as before.
        var isTxid = txid is { Length: 64 };
        if (string.IsNullOrWhiteSpace(txid) || !(isTxid || Guid.TryParse(txid, out _)))
        {
            return JsonStj(new UiSendErrorResponse(
                "a transfer reference is required.",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        // Fail safe: an unconfigured Store cannot report a confirmation count. The
        // transfer was still broadcast, so report it as pending.
        if (backend is null)
        {
            _log.LogInformation("ui_send_status.not_configured txid={Txid}", txid);
            return JsonStj(UiSendStatusResponse.Pending(
                txid,
                "Transaction broadcast. On-chain confirmation tracking is not configured."));
        }

        try
        {
            var status = await backend.GetSendStatusAsync(txid, cancellationToken);

            return JsonStj(new UiSendStatusResponse(
                status.State,
                status.Confirmations ?? 0,
                status.Required ?? RequiredConfirmations,
                status.Ref,
                status.Broadcasted,
                status.Message));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ui_send_status.unexpected txid={Txid}", txid);

            return JsonStj(UiSendStatusResponse.Pending(
                txid,
                "Transaction broadcast. Waiting for confirmation."));
        }
    }

    private static ContentResult JsonStj(object payload, int statusCode = (int)HttpStatusCode.OK)
    {
        return new ContentResult
        {
            Content = JsonSerializer.Serialize(payload, ApiJsonOptions),
            ContentType = "application/json",
            StatusCode = statusCode,
        };
    }

    // Reads and deserializes the JSON request body with System.Text.Json so
    // [JsonPropertyName] (snake_case) is honoured — the Newtonsoft [FromBody] binder
    // is not. Returns default on empty/invalid body.
    private async Task<T?> ReadJsonBodyAsync<T>(CancellationToken ct)
    {
        try
        {
            using var reader = new System.IO.StreamReader(Request.Body);
            var raw = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(raw))
                return default;
            return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return default;
        }
    }
}

// Deserialized manually with System.Text.Json (see the Send action). BTCPay's
// global Newtonsoft.Json [FromBody] binder ignores [JsonPropertyName], so the
// snake_case "asset_id" would NOT map to "AssetId" (address/amount only bind by
// case-insensitive name match) — asset_id arrived null, harmless for BYON (tapd
// ignores it) but fatal for a Hosted send. STJ honours these attributes.
public sealed class UiSendAssetRequest
{
    [JsonPropertyName("asset_id")]
    public string? AssetId { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("amount")]
    public ulong Amount { get; set; } = 1;
}

public sealed class UiSendAssetResponse
{
    [JsonPropertyName("state")]
    public string State { get; }

    [JsonPropertyName("transfer_id")]
    public string? TransferId { get; }

    [JsonPropertyName("anchor_txid")]
    public string? AnchorTxid { get; }

    // Present only for a Hosted send: the LN fee invoice the merchant must pay to
    // reserve the send. Null (and omitted) for BYON, so BYON responses are unchanged.
    [JsonPropertyName("payment")]
    public UiPaymentBlock? Payment { get; }

    public UiSendAssetResponse(string state, string? transferId, string? anchorTxid, UiPaymentBlock? payment = null)
    {
        State = state;
        TransferId = transferId;
        AnchorTxid = anchorTxid;
        Payment = payment;
    }
}

/// <summary>Hosted-only LN fee invoice block echoed to the Send UI (contract §10.1).</summary>
public sealed class UiPaymentBlock
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; }

    [JsonPropertyName("amount_sats")]
    public long AmountSats { get; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; }

    public UiPaymentBlock(string invoice, long amountSats, string? expiresAt)
    {
        Invoice = invoice;
        AmountSats = amountSats;
        ExpiresAt = expiresAt;
    }
}

public sealed class UiSendErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; }

    [JsonPropertyName("error_code")]
    public string ErrorCode { get; }

    public UiSendErrorResponse(string error, string errorCode)
    {
        Error = error;
        ErrorCode = errorCode;
    }
}

public sealed class UiSendStatusResponse
{
    [JsonPropertyName("state")]
    public string State { get; }

    [JsonPropertyName("confirmations")]
    public int Confirmations { get; }

    [JsonPropertyName("required")]
    public int Required { get; }

    [JsonPropertyName("txid")]
    public string Txid { get; }

    [JsonPropertyName("broadcasted")]
    public bool Broadcasted { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    public UiSendStatusResponse(
        string state,
        int confirmations,
        int required,
        string txid,
        bool broadcasted,
        string message)
    {
        State = state;
        Confirmations = confirmations;
        Required = required;
        Txid = txid;
        Broadcasted = broadcasted;
        Message = message;
    }

    public static UiSendStatusResponse Pending(string txid, string message)
    {
        return new UiSendStatusResponse(
            "pending",
            0,
            1,
            txid,
            broadcasted: true,
            message);
    }
}
