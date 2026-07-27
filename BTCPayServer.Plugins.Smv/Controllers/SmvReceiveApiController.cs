using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Machine-to-machine API endpoint.
/// Called by Supermultiverse backend to obtain a Taproot Asset receive address
/// for a given asset, before executing sendasset on the user's behalf.
///
/// Authentication: BTCPay Greenfield API key with CanModifyStoreSettings on the
/// target Store. The caller (SMV backend) must include the key as a Bearer token:
///   Authorization: token <api_key>
///
/// Flow:
///   SMV backend
///     POST /stores/{storeId}/plugins/smv/api/receive-address  { asset_id, amount }
///     ← { encoded: "taprt1...", asset_id, asset_type, amount }
///   SMV backend calls tapd sendasset using the returned encoded address.
/// </summary>
[Route("stores/{storeId}/plugins/smv/api")]
[Authorize(
    Policy = Policies.CanModifyStoreSettings,
    AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes.Greenfield)]
public sealed class SmvReceiveApiController : Controller
{
    // BTCPay's MVC host uses Newtonsoft.Json globally (AddNewtonsoftJson in Startup.cs).
    // Newtonsoft does not honor [JsonPropertyName] from System.Text.Json, so Controller.Json(...)
    // would serialize with PascalCase names. We serialize explicitly with System.Text.Json
    // (same fix applied to SmvProofInspectorController) so the API response uses snake_case
    // consistently, matching the conventions of the tapd REST API and SMV backend expectations.
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null, // [JsonPropertyName] on response DTOs is the source of truth
    };

    private readonly IAssetBackendResolver _backends;
    private readonly ILogger<SmvReceiveApiController> _log;

    public SmvReceiveApiController(
        IAssetBackendResolver backends,
        ILogger<SmvReceiveApiController> log)
    {
        _backends = backends;
        _log = log;
    }

    /// <summary>
    /// Generate a Taproot Asset receive address for the given asset.
    /// </summary>
    /// <remarks>
    /// Request body:
    /// {
    ///   "asset_id": "abc123...",   // hex asset ID (required)
    ///   "amount":   "1"            // string amount (optional, defaults to "1")
    /// }
    ///
    /// Success response (200):
    /// {
    ///   "encoded":    "taprt1...",
    ///   "asset_id":   "abc123...",
    ///   "asset_type": "NORMAL" | "COLLECTIBLE",
    ///   "amount":     "1"
    /// }
    ///
    /// Error response (4xx/5xx):
    /// {
    ///   "error": "human-readable message",
    ///   "error_code": "NOT_CONFIGURED" | "INVALID_REQUEST" | "TAPD_ERROR"
    /// }
    /// </remarks>
    [HttpPost("receive-address")]
    public async Task<IActionResult> CreateReceiveAddress(
        [FromBody] ReceiveAddressRequest? request,
        CancellationToken cancellationToken)
    {
        // Store bound by the framework from the {storeId} route value; never from the body.
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // ── Validate request ──────────────────────────────────────────────────
        if (request is null || string.IsNullOrWhiteSpace(request.AssetId))
        {
            return JsonStj(new ErrorResponse(
                "asset_id is required.",
                "INVALID_REQUEST"), (int)HttpStatusCode.BadRequest);
        }

        var amount = string.IsNullOrWhiteSpace(request.Amount) ? "1" : request.Amount.Trim();

        // ── Resolve backend ───────────────────────────────────────────────────
        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        if (backend is null)
        {
            _log.LogWarning("receive_address.not_configured asset={Asset}", request.AssetId);
            return JsonStj(new ErrorResponse(
                "Taproot Assets wallet is not configured on this BTCPay instance.",
                "NOT_CONFIGURED"), (int)HttpStatusCode.ServiceUnavailable);
        }

        // Receiving into a hosted (custodial) wallet is deferred to Managed Wallet
        // API v1.2 (RFC-PLUGIN-003 §10). Return a clean typed response, not a 500.
        if (backend.IsCustodial)
        {
            _log.LogInformation("receive_address.hosted_unavailable asset={Asset}", request.AssetId);
            return JsonStj(new ErrorResponse(
                "Receiving into a hosted wallet is not available yet (arrives with Managed Wallet API v1.2). Use a self-custody (BYON) Store to receive.",
                "NOT_AVAILABLE"), (int)HttpStatusCode.NotImplemented);
        }

        // ── Call backend ──────────────────────────────────────────────────────
        try
        {
            var addr = await backend.CreateReceiveAddressAsync(
                new ReceiveRequest(request.AssetId.Trim(), null, amount),
                cancellationToken);

            if (string.IsNullOrWhiteSpace(addr.Encoded))
            {
                _log.LogError(
                    "receive_address.empty_encoded asset={Asset} amount={Amount}",
                    request.AssetId, amount);

                return JsonStj(new ErrorResponse(
                    "tapd returned an empty address. Check asset ID and tapd connectivity.",
                    "TAPD_ERROR"), (int)HttpStatusCode.BadGateway);
            }

            _log.LogInformation(
                "receive_address.ok asset={Asset} amount={Amount} encoded={Encoded}",
                request.AssetId, amount, addr.Encoded);

            return JsonStj(new ReceiveAddressResponse(
                addr.Encoded,
                addr.AssetId,
                addr.AssetType,
                addr.Amount));
        }
        catch (TaskCanceledException)
        {
            _log.LogWarning("receive_address.timeout asset={Asset}", request.AssetId);
            return JsonStj(new ErrorResponse(
                "tapd did not respond in time.",
                "TAPD_ERROR"), (int)HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "receive_address.network_error asset={Asset}", request.AssetId);
            return JsonStj(new ErrorResponse(
                $"Cannot reach tapd: {ex.Message}",
                "TAPD_ERROR"), (int)HttpStatusCode.BadGateway);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "receive_address.unexpected asset={Asset}", request.AssetId);
            return JsonStj(new ErrorResponse(
                "Unexpected error generating receive address.",
                "TAPD_ERROR"), (int)HttpStatusCode.InternalServerError);
        }
    }

    // ── Serialization helper (System.Text.Json, bypasses Newtonsoft) ──────────

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

// ── Request / Response DTOs ───────────────────────────────────────────────────

/// <summary>Request body for POST /stores/{storeId}/plugins/smv/api/receive-address.</summary>
public sealed class ReceiveAddressRequest
{
    [Required]
    [JsonPropertyName("asset_id")]
    public string? AssetId { get; set; }

    /// <summary>Defaults to "1" if omitted. Must be "1" for collectibles.</summary>
    [JsonPropertyName("amount")]
    public string? Amount { get; set; }
}

/// <summary>Success response body.</summary>
public sealed class ReceiveAddressResponse
{
    [JsonPropertyName("encoded")]
    public string Encoded { get; }

    [JsonPropertyName("asset_id")]
    public string? AssetId { get; }

    [JsonPropertyName("asset_type")]
    public string? AssetType { get; }

    [JsonPropertyName("amount")]
    public string? Amount { get; }

    public ReceiveAddressResponse(string encoded, string? assetId, string? assetType, string? amount)
    {
        Encoded = encoded;
        AssetId = assetId;
        AssetType = assetType;
        Amount = amount;
    }
}

/// <summary>Error response body.</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; }

    [JsonPropertyName("error_code")]
    public string ErrorCode { get; }

    public ErrorResponse(string error, string errorCode)
    {
        Error = error;
        ErrorCode = errorCode;
    }
}
