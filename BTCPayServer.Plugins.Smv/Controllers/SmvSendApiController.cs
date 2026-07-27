using System.ComponentModel.DataAnnotations;
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

/// <summary>
/// Machine-to-machine API endpoint.
/// Sends a Taproot Asset using a recipient Taproot Asset address.
/// 
/// Authentication: BTCPay Greenfield API key with CanModifyStoreSettings on the
/// target Store. The caller must include the key as a Bearer token:
///   Authorization: token <api_key>
///
/// Flow:
///   Client
///     POST /stores/{storeId}/plugins/smv/api/send  { address }
///     ← { state, transfer_id, anchor_txid, raw_json }
/// </summary>
[Route("stores/{storeId}/plugins/smv/api")]
[Authorize(
    Policy = Policies.CanModifyStoreSettings,
    AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes.Greenfield)]
public sealed class SmvSendApiController : Controller
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
    };

    private readonly IAssetBackendResolver _backends;
    private readonly ILogger<SmvSendApiController> _log;

    public SmvSendApiController(
        IAssetBackendResolver backends,
        ILogger<SmvSendApiController> log)
    {
        _backends = backends;
        _log = log;
    }

    /// <summary>
    /// Send a Taproot Asset to the provided Taproot Asset address.
    /// </summary>
    /// <remarks>
    /// Request body:
    /// {
    ///   "address": "taprt1..."
    /// }
    ///
    /// Success response (200):
    /// {
    ///   "state": "submitted",
    ///   "transfer_id": "...",
    ///   "anchor_txid": "...",
    ///   "raw_json": "{...}"
    /// }
    ///
    /// Error response:
    /// {
    ///   "error": "human-readable message",
    ///   "error_code": "INVALID_REQUEST" | "NOT_CONFIGURED" | "TAPD_ERROR"
    /// }
    /// </remarks>
    [HttpPost("send")]
    public async Task<IActionResult> Send(
        [FromBody] SendAssetRequest? request,
        CancellationToken cancellationToken)
    {
        // Store bound by the framework from the {storeId} route value; never from the body.
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        if (request is null || string.IsNullOrWhiteSpace(request.Address))
        {
            return JsonStj(new SendErrorResponse(
                "address is required.",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        var address = request.Address.Trim();

        if (!TaprootAssetAddress.HasValidPrefix(address))
        {
            return JsonStj(new SendErrorResponse(
                "address must be a Taproot Asset address (tapbc1 / taptb1 / taprt1).",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        if (backend is null)
        {
            _log.LogWarning("send_asset.not_configured");
            return JsonStj(new SendErrorResponse(
                "Taproot Assets wallet is not configured on this BTCPay instance.",
                "NOT_CONFIGURED"), (int)HttpStatusCode.ServiceUnavailable);
        }

        try
        {
            // asset_id + amount are required for a Hosted send (the Managed Wallet
            // body); BYON ignores them (the address encodes the asset).
            var result = await backend.SendAsync(
                new SendRequest(
                    address,
                    AssetId: string.IsNullOrWhiteSpace(request.AssetId) ? null : request.AssetId.Trim(),
                    Amount: string.IsNullOrWhiteSpace(request.Amount) ? null : request.Amount.Trim()),
                cancellationToken);

            _log.LogInformation(
                "send_asset.ok state={State} transfer={TransferId} anchor={AnchorTxid} invoiced={Invoiced}",
                result.ProviderState,
                result.TransferRef,
                result.Txid,
                result.Payment is not null);

            var payment = result.Payment is { Bolt11: not null } inv
                ? new ApiPaymentBlock(inv.Bolt11, inv.AmountSats, inv.ExpiresAt)
                : null;

            return JsonStj(new SendAssetResponse(
                result.ProviderState ?? "submitted",
                result.TransferRef,
                result.Txid,
                result.RawJson,
                payment));
        }
        catch (HostedFeatureNotAvailableException ex)
        {
            _log.LogWarning("send_asset.hosted_unavailable {Message}", ex.Message);
            return JsonStj(new SendErrorResponse(ex.Message, "NOT_AVAILABLE"),
                (int)HttpStatusCode.NotImplemented);
        }
        catch (ManagedWalletApiException ex)
        {
            _log.LogWarning("send_asset.hosted_error code={Code} status={Status}", ex.Code, ex.HttpStatus);
            var (message, code, http) = MapHostedSendError(ex);
            return JsonStj(new SendErrorResponse(message, code), http);
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("send_asset.timeout");
            return JsonStj(new SendErrorResponse(
                "tapd did not respond in time.",
                "TAPD_ERROR"), (int)HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "send_asset.network_error");
            return JsonStj(new SendErrorResponse(
                $"Cannot reach tapd: {ex.Message}",
                "TAPD_ERROR"), (int)HttpStatusCode.BadGateway);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "send_asset.unexpected");
            return JsonStj(new SendErrorResponse(
                "Unexpected error sending Taproot Asset.",
                "TAPD_ERROR"), (int)HttpStatusCode.InternalServerError);
        }
    }

    // Maps a Managed Wallet send failure (contract §10.1) to a machine-API error
    // code + message + HTTP status. Codes are additive (Hosted-only); the existing
    // BYON codes (TAPD_ERROR / NOT_CONFIGURED / INVALID_REQUEST) are unchanged.
    private static (string Message, string Code, int Http) MapHostedSendError(ManagedWalletApiException ex) => ex.Code switch
    {
        ManagedWalletErrorCode.InsufficientBalance => ("The wallet does not hold enough of this asset to send.", "INSUFFICIENT_BALANCE", (int)HttpStatusCode.UnprocessableEntity),
        ManagedWalletErrorCode.AssetNotFound       => ("That asset was not found in the wallet.", "ASSET_NOT_FOUND", (int)HttpStatusCode.NotFound),
        ManagedWalletErrorCode.InvalidRequest      => ("The send request was rejected as invalid (check asset_id, amount and address).", "INVALID_REQUEST", (int)HttpStatusCode.BadRequest),
        ManagedWalletErrorCode.InvalidPath         => ("The send request was rejected as invalid.", "INVALID_REQUEST", (int)HttpStatusCode.BadRequest),
        ManagedWalletErrorCode.PaymentRequired     => ("A previous send for this asset is awaiting its fee payment. Retry to start a fresh one.", "PAYMENT_REQUIRED", (int)HttpStatusCode.PaymentRequired),
        ManagedWalletErrorCode.IdempotencyConflict => ("A conflicting send is already in progress. Retry.", "IDEMPOTENCY_CONFLICT", (int)HttpStatusCode.Conflict),
        ManagedWalletErrorCode.Unauthorized        => ("The hosted wallet token is missing or invalid.", "UNAUTHORIZED", (int)HttpStatusCode.Unauthorized),
        ManagedWalletErrorCode.InsufficientScope   => ("The hosted wallet token lacks the assets:send scope.", "INSUFFICIENT_SCOPE", (int)HttpStatusCode.Forbidden),
        ManagedWalletErrorCode.RateLimited         => ("The wallet service is rate-limiting requests. Retry later.", "RATE_LIMITED", 429),
        _                                          => ("The wallet service reported an error completing the send.", "BACKEND_ERROR", (int)HttpStatusCode.BadGateway),
    };

    private static ContentResult JsonStj(object payload, int statusCode = (int)HttpStatusCode.OK)
    {
        return new ContentResult
        {
            Content = JsonSerializer.Serialize(payload, ApiJsonOptions),
            ContentType = "application/json",
            StatusCode = statusCode,
        };
    }
}

public sealed class SendAssetRequest
{
    [Required]
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    // Required for a Hosted send (the Managed Wallet body); ignored by BYON, so
    // existing BYON callers that omit them are unaffected.
    [JsonPropertyName("asset_id")]
    public string? AssetId { get; set; }

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }
}

public sealed class SendAssetResponse
{
    [JsonPropertyName("state")]
    public string State { get; }

    [JsonPropertyName("transfer_id")]
    public string? TransferId { get; }

    [JsonPropertyName("anchor_txid")]
    public string? AnchorTxid { get; }

    [JsonPropertyName("raw_json")]
    public string? RawJson { get; }

    // Present only for a Hosted send: the LN fee invoice to pay. Null (omitted) for
    // BYON, so BYON responses are byte-identical.
    [JsonPropertyName("payment")]
    public ApiPaymentBlock? Payment { get; }

    public SendAssetResponse(string state, string? transferId, string? anchorTxid, string? rawJson, ApiPaymentBlock? payment = null)
    {
        State = state;
        TransferId = transferId;
        AnchorTxid = anchorTxid;
        RawJson = rawJson;
        Payment = payment;
    }
}

/// <summary>Hosted-only LN fee invoice block (contract §10.1).</summary>
public sealed class ApiPaymentBlock
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; }

    [JsonPropertyName("amount_sats")]
    public long AmountSats { get; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; }

    public ApiPaymentBlock(string invoice, long amountSats, string? expiresAt)
    {
        Invoice = invoice;
        AmountSats = amountSats;
        ExpiresAt = expiresAt;
    }
}

public sealed class SendErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; }

    [JsonPropertyName("error_code")]
    public string ErrorCode { get; }

    public SendErrorResponse(string error, string errorCode)
    {
        Error = error;
        ErrorCode = errorCode;
    }
}