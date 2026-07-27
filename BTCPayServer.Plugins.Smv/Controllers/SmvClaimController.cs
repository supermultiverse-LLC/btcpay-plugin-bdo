using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Plugins.Smv.Services.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Send-to-customer claim links (journey GAP G2): the merchant hands a held
/// BDO to a customer with a LINK — the customer opens it, signs up with an
/// email if needed, and the BDO moves to their collection by accounting
/// transfer. No taproot addresses, no wallet software. Thin JSON proxy over
/// managed-wallet-send-claim; the transfer itself happens server-side,
/// atomically, at redemption.
/// </summary>
[Route("stores/{storeId}/plugins/smv/claim-link")]
public class SmvClaimController : Controller
{
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly SmvOAuthTokenService _oauthTokens;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly ILogger<SmvClaimController> _log;

    public SmvClaimController(
        ISmvStoreSettingsProvider storeSettings,
        SmvOAuthTokenService oauthTokens,
        ISettingsRepositoryAccessor serverSettings,
        ILogger<SmvClaimController> log)
    {
        _storeSettings = storeSettings;
        _oauthTokens = oauthTokens;
        _serverSettings = serverSettings;
        _log = log;
    }

    private async Task<(System.Net.Http.HttpClient? Http, string? Error)> BuildHttpAsync(string storeId, CancellationToken ct)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        if (settings is null)
            return (null, "This Store isn't configured.");
        var token = await _oauthTokens.EnsureFreshTokenAsync(storeId, settings, ct);
        if (string.IsNullOrWhiteSpace(token))
            return (null, "Activate your BDO account in Settings first.");
        var server = await _serverSettings.GetAsync();
        return (ManagedWalletClient.CreateHttpClient(server.HostedApiBase, token, Math.Max(server.SmvHttpTimeoutMs, 20000)), null);
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Create(string storeId, string assetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assetId) || assetId.Length > 64)
            return BadRequest(new { message = "Invalid request." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return BadRequest(new { message = error });
        try
        {
            var link = await new ManagedWalletClient(http).CreateClaimLinkAsync(assetId, ct);
            if (string.IsNullOrWhiteSpace(link.ClaimUrl))
                return BadRequest(new { message = "The platform returned no claim link." });

            _log.LogInformation("claim_link.created store={StoreId} asset={AssetId} code={Code}",
                storeId, assetId, link.Code);
            // RFC-PLUGIN-009: advertise the MERCHANT-DOMAIN claim page — the end
            // customer knows the merchant's brand, not the platform's. The platform
            // URL in link.ClaimUrl stays valid for old links; we just stop promoting it.
            return Ok(new { code = link.Code, claimUrl = MerchantClaimUrl(storeId, link.Code), assetName = link.AssetName });
        }
        catch (ManagedWalletApiException ex)
        {
            _log.LogWarning("claim_link.create_rejected store={StoreId} code={Code}", storeId, ex.Code);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "claim_link.create_failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't create the claim link. Try again." });
        }
        finally { http.Dispose(); }
    }

    [HttpGet("list")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> List(string storeId, CancellationToken ct)
    {
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return Ok(new { connected = false, message = error });
        try
        {
            var list = await new ManagedWalletClient(http).ListClaimLinksAsync(ct);
            return Ok(new
            {
                connected = true,
                pending = list.Pending.ConvertAll(l => new
                {
                    code = l.Code,
                    claimUrl = MerchantClaimUrl(storeId, l.Code),
                    assetId = l.AssetId,
                    tapdAssetId = l.TapdAssetId,
                    assetName = l.AssetName,
                    createdAt = l.CreatedAt
                })
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "claim_link.list_failed store={StoreId}", storeId);
            return Ok(new { connected = false, message = "Couldn't reach the platform." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("cancel")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Cancel(string storeId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            return BadRequest(new { message = "Invalid request." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return BadRequest(new { message = error });
        try
        {
            await new ManagedWalletClient(http).CancelClaimLinkAsync(code, ct);
            _log.LogInformation("claim_link.cancelled store={StoreId} code={Code}", storeId, code);
            return Ok(new { cancelled = true });
        }
        catch (ManagedWalletApiException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "claim_link.cancel_failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't cancel the link. Try again." });
        }
        finally { http.Dispose(); }
    }

    /// <summary>Receive-side of a claim: redeem a code INTO this Store's account
    /// (full-page POST from the Receive tab — CSP-safe, no JS). The BDO moves the
    /// moment the platform confirms; back to Receive with a status banner.</summary>
    [HttpPost("redeem")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Redeem(string storeId, string code, CancellationToken ct)
    {
        code = (code ?? "").Trim();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
        {
            TempData["StatusMessage"] = "Error: Enter the claim code.";
            return RedirectToAction("Index", "SmvReceive", new { storeId });
        }

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
        {
            TempData["StatusMessage"] = $"Error: {error}";
            return RedirectToAction("Index", "SmvReceive", new { storeId });
        }
        try
        {
            var result = await new ManagedWalletClient(http).RedeemClaimAsync(code, ct);
            _log.LogInformation("claim_redeem.ok store={StoreId}", storeId);
            // Success lands on My BDOs — where the redeemed BDO is actually
            // visible — with a dedicated dismissible confirmation (the Receive
            // banner went unnoticed, 2026-07-27).
            TempData["SmvRedeemSuccess"] = result.AssetName is { Length: > 0 } name
                ? $"“{name}” has been redeemed — it's now in this Store's wallet."
                : "Claim redeemed — the BDO is now in this Store's wallet.";
            return RedirectToAction("Index", "SmvMyAssets", new { storeId });
        }
        catch (ManagedWalletApiException ex)
        {
            _log.LogInformation("claim_redeem.rejected store={StoreId} code={Code}", storeId, ex.Code);
            TempData["StatusMessage"] = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "claim_redeem.failed store={StoreId}", storeId);
            TempData["StatusMessage"] = "Error: Couldn't redeem the code. Try again.";
        }
        finally { http.Dispose(); }
        return RedirectToAction("Index", "SmvReceive", new { storeId });
    }

    /// <summary>The white-label claim URL on THIS BTCPay instance (RFC-PLUGIN-009).</summary>
    private string MerchantClaimUrl(string storeId, string? code)
        => $"{Request.Scheme}://{Request.Host}/plugins/smv/claim/{Uri.EscapeDataString(storeId)}?code={Uri.EscapeDataString(code ?? "")}";
}
