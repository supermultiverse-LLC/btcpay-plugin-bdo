using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Core;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Smv.Settings;

[Route("stores/{storeId}/plugins/smv/settings")]
public class SmvSettingsController : Controller
{
    private readonly ISmvStoreSettingsProvider _storeSettings;

    public SmvSettingsController(ISmvStoreSettingsProvider storeSettings)
    {
        _storeSettings = storeSettings;
    }

    [HttpGet("")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        var settings = await _storeSettings.GetAsync(store.Id, cancellationToken);

        // Never place a credential in the model that renders. Only report whether
        // each secret is configured (test 7 / RFC §9.2).
        var vm = new SmvStoreSettingsViewModel
        {
            BackendMode = settings?.BackendMode ?? SmvBackendMode.Byon,
            TapdBaseUrl = settings?.TapdBaseUrl,
            TapdTlsCert = settings?.TapdTlsCert,
            TapdHttpTimeoutMs = settings?.TapdHttpTimeoutMs ?? 8000,
            TapdMacaroonConfigured = !string.IsNullOrWhiteSpace(settings?.TapdMacaroonHex),
            BitcoinRpcUrl = settings?.BitcoinRpcUrl,
            BitcoinRpcUser = settings?.BitcoinRpcUser,
            BitcoinRpcPasswordConfigured = !string.IsNullOrWhiteSpace(settings?.BitcoinRpcPassword),
            HostedApiTokenConfigured = !string.IsNullOrWhiteSpace(settings?.HostedApiToken),
            OAuthConnected = settings?.HasOAuthConnection ?? false,
            OAuthConnectedAccount = settings?.OAuthConnectedAccount,
            OAuthScopes = settings?.OAuthScopes,
            OAuthModeMismatch = settings?.OAuthModeMismatch ?? false
        };

        return View(vm);
    }

    [HttpPost("")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Index(SmvStoreSettingsViewModel model, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // The existing record is the source for any secret the operator did not
        // re-enter (fields are replacement-only) and for the configured badges on
        // re-render. GetAsync returns plaintext in memory; it is never rendered.
        var existing = await _storeSettings.GetAsync(store.Id, cancellationToken);

        // Defensive bounds. Never let obviously wrong values brick the backend.
        if (model.TapdHttpTimeoutMs is < 1000 or > 60000)
            model.TapdHttpTimeoutMs = 8000;

        // The tapd base URL is only meaningful for BYON; only its form section is
        // rendered in that mode, so only validate it then.
        var tapdBaseUrl = string.IsNullOrWhiteSpace(model.TapdBaseUrl) ? null : model.TapdBaseUrl.Trim();
        if (model.BackendMode == SmvBackendMode.Byon &&
            tapdBaseUrl is not null &&
            (!Uri.TryCreate(tapdBaseUrl, UriKind.Absolute, out var tapdUri) ||
             tapdUri.Scheme is not ("http" or "https")))
        {
            ModelState.AddModelError(nameof(model.TapdBaseUrl), "Tapd base URL must be a valid http or https URL.");
        }

        if (!ModelState.IsValid)
        {
            // Recompute the configured badges from storage; never echo a secret.
            model.TapdMacaroonConfigured = !string.IsNullOrWhiteSpace(existing?.TapdMacaroonHex);
            model.BitcoinRpcPasswordConfigured = !string.IsNullOrWhiteSpace(existing?.BitcoinRpcPassword);
            model.HostedApiTokenConfigured = !string.IsNullOrWhiteSpace(existing?.HostedApiToken);
            model.OAuthConnected = existing?.HasOAuthConnection ?? false;
            model.OAuthConnectedAccount = existing?.OAuthConnectedAccount;
            model.OAuthScopes = existing?.OAuthScopes;
            model.OAuthModeMismatch = existing?.OAuthModeMismatch ?? false;
            return View(model);
        }

        // MUTATE the stored record instead of rebuilding it field-by-field: everything
        // the form doesn't own (OAuth session, pending registrations, the other mode's
        // config, any FUTURE field) survives automatically. The rebuild pattern
        // silently dropped OAuthSessionKind + PendingByonRegistrationsJson — added
        // after this controller was written — breaking embedded sessions on every
        // Save (refresh sent to the wrong endpoint once the session kind was lost).
        // Secrets stay replacement-only: a blank field keeps the stored value.
        var toStore = existing ?? new SmvStoreSettings();
        toStore.BackendMode = model.BackendMode;

        if (model.BackendMode == SmvBackendMode.Hosted)
        {
            // Only the manual token is form-owned in Hosted mode; BYON config rides along.
            if (!string.IsNullOrWhiteSpace(model.HostedApiToken))
                toStore.HostedApiToken = model.HostedApiToken.Trim();
        }
        else
        {
            toStore.TapdBaseUrl = tapdBaseUrl;
            if (!string.IsNullOrWhiteSpace(model.TapdMacaroonHex))
                toStore.TapdMacaroonHex = model.TapdMacaroonHex.Trim();
            toStore.TapdTlsCert = string.IsNullOrWhiteSpace(model.TapdTlsCert) ? null : model.TapdTlsCert.Trim();
            toStore.TapdHttpTimeoutMs = model.TapdHttpTimeoutMs;
            toStore.BitcoinRpcUrl = string.IsNullOrWhiteSpace(model.BitcoinRpcUrl) ? null : model.BitcoinRpcUrl.Trim();
            toStore.BitcoinRpcUser = string.IsNullOrWhiteSpace(model.BitcoinRpcUser) ? null : model.BitcoinRpcUser.Trim();
            if (!string.IsNullOrWhiteSpace(model.BitcoinRpcPassword))
                toStore.BitcoinRpcPassword = model.BitcoinRpcPassword;
        }

        await _storeSettings.SetAsync(store.Id, toStore, cancellationToken);
        TempData["StatusMessage"] = "SMV backend settings saved.";
        return RedirectToAction(nameof(Index), new { storeId = store.Id });
    }
}

/// <summary>
/// Store settings form model. Credential fields (<see cref="TapdMacaroonHex"/>,
/// <see cref="BitcoinRpcPassword"/>, <see cref="HostedApiToken"/>) are write-only
/// and replacement-only: they are never populated on GET, and a blank value on
/// POST keeps the stored secret. The *Configured booleans drive the
/// "configured / not configured" display. <see cref="BackendMode"/> selects which
/// backend (and which config section) is active.
/// </summary>
public class SmvStoreSettingsViewModel
{
    public SmvBackendMode BackendMode { get; set; } = SmvBackendMode.Byon;

    public string? TapdBaseUrl { get; set; }
    public string? TapdTlsCert { get; set; }
    public int TapdHttpTimeoutMs { get; set; } = 8000;

    public bool TapdMacaroonConfigured { get; set; }
    public string? TapdMacaroonHex { get; set; }

    public string? BitcoinRpcUrl { get; set; }
    public string? BitcoinRpcUser { get; set; }

    public bool BitcoinRpcPasswordConfigured { get; set; }
    public string? BitcoinRpcPassword { get; set; }

    // Hosted backend (P3). Write-only, replacement-only, like the tapd macaroon.
    public bool HostedApiTokenConfigured { get; set; }
    public string? HostedApiToken { get; set; }

    // OAuth Connect (RFC-PLUGIN-007) — read-only display state; never a credential.
    public bool OAuthConnected { get; set; }
    public string? OAuthConnectedAccount { get; set; }
    public string? OAuthScopes { get; set; }
    /// <summary>Connected, but the backend mode changed since — reconnect needed (§11.8 R4).</summary>
    public bool OAuthModeMismatch { get; set; }
}
