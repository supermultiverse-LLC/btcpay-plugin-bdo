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
/// Mint-credits top-up inside the plugin (billing GAP #1): the merchant buys a
/// credit package over Lightning without leaving BTCPay. Thin JSON proxy over
/// the Managed API's <c>managed-wallet-topup</c> — the plugin never computes
/// amounts or credits balances; settlement stays server-side
/// (reconcile-payments → settle RPC), exactly like the Studio flow.
/// </summary>
[Route("stores/{storeId}/plugins/smv/topup")]
public class SmvTopupController : Controller
{
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly SmvOAuthTokenService _oauthTokens;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly ILogger<SmvTopupController> _log;

    public SmvTopupController(
        ISmvStoreSettingsProvider storeSettings,
        SmvOAuthTokenService oauthTokens,
        ISettingsRepositoryAccessor serverSettings,
        ILogger<SmvTopupController> log)
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
            var client = new ManagedWalletClient(http);
            var info = await client.GetTopupInfoAsync(ct);

            // Plan-gate for the buy buttons: without an active plan there is
            // nothing credits can pay for (mint and registration are both
            // plan-gated). Best-effort — on failure the buttons stay enabled.
            bool? hasPlan = null;
            try
            {
                var sub = await client.GetSubscriptionInfoAsync(ct);
                hasPlan = sub.Current is not null;
            }
            catch { /* leave null → no gating */ }

            return Ok(new
            {
                connected = true,
                balanceSats = info.BalanceSats,
                hasPlan,
                packages = info.Packages.ConvertAll(p => new { id = p.Id, label = p.Label, amountSats = p.AmountSats })
            });
        }
        catch (ManagedWalletApiException ex) when (ex.HttpStatus is 401 or 403)
        {
            _log.LogInformation("topup.info_auth_expired store={StoreId}", storeId);
            return Ok(new { connected = false, authExpired = true, message = "Your connection is no longer valid — reconnect your BDO account." });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "topup.info_failed store={StoreId}", storeId);
            return Ok(new { connected = false, message = "Couldn't reach the platform. Try again." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Create(string storeId, string packageId, string clientRequestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(clientRequestId)
            || packageId.Length > 64 || clientRequestId.Length > 128)
            return BadRequest(new { message = "Invalid request." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null)
            return BadRequest(new { message = error });
        try
        {
            // Namespaced so a plugin request id can never collide with Studio's.
            var invoice = await new ManagedWalletClient(http)
                .CreateTopupInvoiceAsync(packageId, $"plugin:{clientRequestId}", ct);
            if (string.IsNullOrWhiteSpace(invoice.InvoiceBolt11))
                return BadRequest(new { message = "The platform returned no invoice." });

            _log.LogInformation("topup.invoice_created store={StoreId} intent={Intent} sats={Sats}",
                storeId, invoice.PaymentIntentId, invoice.AmountSats);
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
            _log.LogWarning("topup.create_rejected store={StoreId} code={Code}", storeId, ex.Code);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "topup.create_failed store={StoreId}", storeId);
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
            var status = await new ManagedWalletClient(http).GetTopupStatusAsync(intentId, ct);
            return Ok(new { paid = status.Paid, status = status.Status, balanceSats = status.BalanceSats });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "topup.status_failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't check the payment. Try again." });
        }
        finally { http.Dispose(); }
    }
}
