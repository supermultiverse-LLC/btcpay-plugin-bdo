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
/// Premium subscription inside the plugin (journey GAP G1): the merchant buys
/// the plan that UNLOCKS minting without leaving BTCPay. Thin JSON proxy over
/// managed-wallet-subscribe; activation stays server-side. When the poll sees
/// the payment settled, the controller force-refreshes the store's token so
/// the freshly-earned <c>assets:mint</c> scope applies immediately — no
/// manual reconnect.
/// </summary>
[Route("stores/{storeId}/plugins/smv/subscription")]
public class SmvSubscriptionController : Controller
{
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly SmvOAuthTokenService _oauthTokens;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly ILogger<SmvSubscriptionController> _log;

    public SmvSubscriptionController(
        ISmvStoreSettingsProvider storeSettings,
        SmvOAuthTokenService oauthTokens,
        ISettingsRepositoryAccessor serverSettings,
        ILogger<SmvSubscriptionController> log)
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

    [HttpGet("info")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Info(string storeId, CancellationToken ct)
    {
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return Ok(new { connected = false, message = error });
        try
        {
            var info = await new ManagedWalletClient(http).GetSubscriptionInfoAsync(ct);
            return Ok(new
            {
                connected = true,
                currentTier = info.Current?.Tier,
                currentExpiresAt = info.Current?.ExpiresAt,
                tiers = info.Tiers.ConvertAll(t => new
                {
                    name = t.Name,
                    priceSats = t.PriceSats,
                    durationDays = t.DurationDays,
                    creditGrantSats = t.CreditGrantSats,
                    mintFeeSats = t.MintFeeSats
                }),
                lifetime = info.Lifetime is null ? null : new
                {
                    soldOut = info.Lifetime.SoldOut,
                    priceSats = info.Lifetime.PriceSats,
                    totalSold = info.Lifetime.TotalSold,
                    totalCap = info.Lifetime.TotalCap,
                    unitsRemainingTier = info.Lifetime.UnitsRemainingTier,
                    nextPriceSats = info.Lifetime.NextPriceSats,
                    creditGrantSats = info.Lifetime.CreditGrantSats,
                    mintFeeSats = info.Lifetime.MintFeeSats,
                    alreadyOwned = info.Lifetime.AlreadyOwned
                }
            });
        }
        catch (ManagedWalletApiException ex) when (ex.HttpStatus is 401 or 403)
        {
            // The stored connection is dead (revoked/expired refresh chain) — say
            // so instead of a generic "couldn't reach", so Settings can't claim
            // "Connected" while My BDOs shows a reconnect wall (2026-07-27 find).
            _log.LogInformation("subscription.info_auth_expired store={StoreId}", storeId);
            return Ok(new { connected = false, authExpired = true, message = "Your connection is no longer valid — reconnect your BDO account." });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "subscription.info_failed store={StoreId}", storeId);
            return Ok(new { connected = false, message = "Couldn't reach the platform. Try again." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Create(string storeId, string tierName, string clientRequestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tierName) || string.IsNullOrWhiteSpace(clientRequestId)
            || tierName.Length > 64 || clientRequestId.Length > 128)
            return BadRequest(new { message = "Invalid request." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return BadRequest(new { message = error });
        try
        {
            var invoice = await new ManagedWalletClient(http)
                .CreateSubscriptionInvoiceAsync(tierName, $"plugin:{clientRequestId}", ct);
            if (string.IsNullOrWhiteSpace(invoice.InvoiceBolt11))
                return BadRequest(new { message = "The platform returned no invoice." });

            _log.LogInformation("subscription.invoice_created store={StoreId} tier={Tier} intent={Intent} sats={Sats}",
                storeId, tierName, invoice.PaymentIntentId, invoice.AmountSats);
            return Ok(new
            {
                intentId = invoice.PaymentIntentId,
                bolt11 = invoice.InvoiceBolt11,
                amountSats = invoice.AmountSats,
                expiresAt = invoice.ExpiresAt
            });
        }
        catch (ManagedWalletApiException ex)
        {
            _log.LogWarning("subscription.create_rejected store={StoreId} code={Code}", storeId, ex.Code);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "subscription.create_failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't create the invoice. Try again." });
        }
        finally { http.Dispose(); }
    }

    [HttpGet("status")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Status(string storeId, string intentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(intentId) || intentId.Length > 64)
            return BadRequest(new { message = "Invalid request." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return BadRequest(new { message = error });
        try
        {
            var status = await new ManagedWalletClient(http).GetSubscriptionStatusAsync(intentId, ct);

            var scopesRefreshed = false;
            if (status.Paid)
            {
                // The subscription just unlocked new entitlements; the current
                // token still carries the OLD grant. Re-exchange now so
                // assets:mint (and its UI) light up without a manual reconnect.
                try
                {
                    var refreshed = await _oauthTokens.ForceRefreshAsync(storeId, rejectedToken: null, ct);
                    scopesRefreshed = !string.IsNullOrWhiteSpace(refreshed);
                    _log.LogInformation("subscription.paid store={StoreId} tier={Tier} scopes_refreshed={Refreshed}",
                        storeId, status.ActiveTier, scopesRefreshed);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "subscription.scope_refresh_failed store={StoreId}", storeId);
                }
            }

            return Ok(new { paid = status.Paid, status = status.Status, activeTier = status.ActiveTier, scopesRefreshed });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "subscription.status_failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't check the payment. Try again." });
        }
        finally { http.Dispose(); }
    }
}
