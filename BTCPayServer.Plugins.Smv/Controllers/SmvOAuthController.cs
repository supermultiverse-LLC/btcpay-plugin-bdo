using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Core;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Plugins.Smv.Services.OAuth;
using BTCPayServer.Plugins.Smv.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// OAuth Connect flow (RFC-PLUGIN-007 P1). Replaces the manual <c>mwv1_</c> paste for
/// BOTH Hosted and BYON: the merchant clicks Connect, the plugin self-registers an OAuth
/// client for the Store (DCR), declares its capabilities, runs Authorization Code + PKCE,
/// exchanges the identity JWT for an <c>mwv1_</c>, and stores it per-Store.
///
/// <see cref="Connect"/> → redirect to <c>/authorize</c>; <see cref="Callback"/> → token +
/// bridge + persist; <see cref="Disconnect"/> → clear + best-effort revoke. Transient PKCE
/// state lives in <see cref="IMemoryCache"/> (connect + callback hit the same instance);
/// only the resulting tokens are persisted. No views — outcomes redirect to Settings with a
/// status message (the connected panel is rendered by the Settings view, P1 part 5).
/// </summary>
[Route("stores/{storeId}/plugins/smv/oauth")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvOAuthController : Controller
{
    private const string CachePrefix = "smv-oauth-connect:";
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan OAuthTimeout = TimeSpan.FromSeconds(15);

    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SmvOAuthController> _log;

    public SmvOAuthController(
        ISmvStoreSettingsProvider storeSettings,
        ISettingsRepositoryAccessor serverSettings,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<SmvOAuthController> log)
    {
        _storeSettings = storeSettings;
        _serverSettings = serverSettings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _log = log;
    }

    // Begin the flow: (register the client if new) → declare capabilities → 302 to /authorize.
    [HttpGet("connect")]
    public async Task<IActionResult> Connect(bool popup, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        var settings = await _storeSettings.GetAsync(store.Id, ct);
        var server = await _serverSettings.GetAsync();
        var mode = settings?.BackendMode ?? SmvBackendMode.Byon;
        var capabilities = CapabilitiesFor(mode);
        var clientName = ClientNameFor(store);
        var redirectUri = Url.Action(nameof(Callback), "SmvOAuth", new { storeId = store.Id }, Request.Scheme)!;
        var client = BuildClient(server);

        try
        {
            // Register the client once per Store (§11.7 R1); reuse a stored client_id.
            var clientId = settings?.OAuthClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                clientId = await client.RegisterClientAsync(clientName, redirectUri, ct: ct);
                await PersistAsync(store.Id, settings, s => s.OAuthClientId = clientId, ct);
            }

            // Declare/refresh the capabilities the consent screen + bridge read (§12.1, upsert).
            await client.RegisterCapabilitiesAsync(clientId!, clientName, capabilities, ct);

            var verifier = PkceCodes.NewCodeVerifier();
            var challenge = PkceCodes.Challenge(verifier);
            var state = PkceCodes.NewStateToken();
            _cache.Set(CachePrefix + state, new PendingConnect(store.Id, verifier, clientId!, redirectUri, mode, popup), PendingTtl);

            return Redirect(BuildAuthorizeUrl(server.OAuthIssuerBase, clientId!, redirectUri, challenge, state));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "oauth_connect.begin_failed store={StoreId}", store.Id);
            return Finish(popup, store.Id, "Couldn't start the Supermultiverse connection. Please try again.");
        }
    }

    // Return from the authorization server: exchange the code, bridge to mwv1_, persist.
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? code, string? state, string? error, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        // Retrieve the pending connect FIRST (by state) so we know the response style
        // (popup close vs full-page redirect) even on the cancel/error branches. The
        // state also binds the callback to a connect WE started, for THIS Store (CSRF).
        PendingConnect? pending = null;
        if (!string.IsNullOrWhiteSpace(state))
            _cache.TryGetValue(CachePrefix + state, out pending);
        var popup = pending?.Popup ?? false;
        if (pending is not null) _cache.Remove(CachePrefix + state);

        if (!string.IsNullOrWhiteSpace(error))
            return Finish(popup, store.Id, $"Supermultiverse connection was cancelled ({error}).");
        if (string.IsNullOrWhiteSpace(code) || pending is null || pending.StoreId != store.Id)
            return Finish(popup, store.Id, "The connection link expired or was invalid. Please start again.");

        var server = await _serverSettings.GetAsync();
        var client = BuildClient(server);

        try
        {
            var tokens = await client.ExchangeCodeAsync(code, pending.CodeVerifier, pending.RedirectUri, pending.ClientId, ct: ct);
            var grant = await client.ExchangeMwv1Async(tokens.AccessToken, pending.ClientId, ct);

            var settings = await _storeSettings.GetAsync(store.Id, ct);
            await PersistAsync(store.Id, settings, s =>
            {
                s.OAuthClientId = pending.ClientId;
                s.HostedApiToken = grant.Mwv1Token;                       // reused as the mwv1_ bearer
                s.OAuthRefreshToken = tokens.RefreshToken;
                s.OAuthScopes = string.Join(',', grant.Scopes);
                s.OAuthTokenExpiresAtUnix = grant.ExpiresAtUnix;
                s.OAuthConnectedAccount = grant.AccountLabel;
                s.OAuthTokenId = grant.TokenId;
                s.OAuthWalletId = grant.WalletId;
                s.OAuthConnectedMode = pending.Mode;   // capabilities are for THIS mode
                s.OAuthSessionKind = "sso";            // refreshes at the OAuth server (RFC-008)
            }, ct);

            var msg = $"Connected to Supermultiverse{(string.IsNullOrWhiteSpace(grant.AccountLabel) ? "" : $" as {grant.AccountLabel}")}.";
            if (grant.Denied.Count > 0)
                msg += " Some capabilities weren't granted: " + DescribeDenied(grant.Denied) + ".";
            return Finish(popup, store.Id, msg);
        }
        catch (SmvOAuthException ex) when (ex.Code == "insufficient_entitlement")
        {
            _log.LogInformation("oauth_callback.no_entitlement store={StoreId}", store.Id);
            return Finish(popup, store.Id, "Connected, but none of the requested capabilities are available for your account yet.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "oauth_callback.failed store={StoreId}", store.Id);
            return Finish(popup, store.Id, "Couldn't complete the Supermultiverse connection. Please try again.");
        }
    }

    // Clear the connection locally; best-effort accelerate revocation server-side.
    [HttpPost("disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        var settings = await _storeSettings.GetAsync(store.Id, ct);
        if (settings is not null && settings.HasOAuthConnection && !string.IsNullOrWhiteSpace(settings.HostedApiToken))
        {
            try { await BuildClient(await _serverSettings.GetAsync()).RevokeSelfAsync(settings.HostedApiToken, ct); }
            catch (Exception ex) { _log.LogInformation(ex, "oauth_disconnect.revoke_self_failed store={StoreId}", store.Id); }
        }

        await PersistAsync(store.Id, settings, s =>
        {
            s.OAuthClientId = null;
            s.OAuthRefreshToken = null;
            s.HostedApiToken = null;
            s.OAuthScopes = null;
            s.OAuthTokenExpiresAtUnix = null;
            s.OAuthConnectedAccount = null;
            s.OAuthTokenId = null;
            s.OAuthWalletId = null;
            s.OAuthConnectedMode = null;
            s.OAuthSessionKind = null;
        }, ct);

        return BackToSettings(store.Id, "Disconnected from Supermultiverse.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private SmvOAuthClient BuildClient(SmvServerSettings server)
    {
        var http = _httpClientFactory.CreateClient("smv-oauth");
        http.Timeout = OAuthTimeout;
        return new SmvOAuthClient(http, server.OAuthIssuerBase, server.HostedApiBase, server.SupabaseAnonKey);
    }

    // Mutate the Store's settings in place (starting from the stored plaintext, or a
    // fresh record) and persist — the provider re-protects credentials on write.
    private Task PersistAsync(string storeId, SmvStoreSettings? current, Action<SmvStoreSettings> mutate, CancellationToken ct)
    {
        var s = current ?? new SmvStoreSettings();
        mutate(s);
        return _storeSettings.SetAsync(storeId, s, ct);
    }

    internal static IReadOnlyList<string> CapabilitiesFor(SmvBackendMode mode) => mode == SmvBackendMode.Hosted
        ? new[] { "assets:read", "assets:mint", "assets:receive", "assets:send" }
        : new[] { "assets:read", "assets:register_external" };

    internal static string ClientNameFor(BTCPayServer.Data.StoreData store)
    {
        var name = string.IsNullOrWhiteSpace(store.StoreName) ? "Store" : store.StoreName.Trim();
        var label = $"BTCPay – {name}";
        return label.Length > 100 ? label[..100] : label;   // §11.7 R1: ≤100 chars
    }

    private static string BuildAuthorizeUrl(string issuerBase, string clientId, string redirectUri, string challenge, string state)
        => $"{issuerBase.TrimEnd('/')}/oauth/authorize" +
           $"?response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
           $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
           $"&scope={Uri.EscapeDataString("openid profile email")}" +
           $"&code_challenge={challenge}&code_challenge_method=S256" +   // base64url — URL-safe
           $"&state={state}";

    // RFC §11.8 R3 copy lives in SmvOAuthCopy (shared with the embedded account flow).
    private static string DescribeDenied(IReadOnlyList<Mwv1Denied> denied)
        => SmvOAuthCopy.DescribeDenied(denied);

    private IActionResult BackToSettings(string storeId, string message)
    {
        TempData["StatusMessage"] = message;
        return RedirectToAction("Index", "SmvSettings", new { storeId });
    }

    // Popup flow: the connect ran in a popup window, so close it — the opener (Settings)
    // reloads itself to reflect the new state. Full-page flow: redirect to Settings with
    // the status message. The close page pulls the plugin's own (same-origin, CSP-safe)
    // oauth-popup.js rather than an inline script.
    private IActionResult Finish(bool popup, string storeId, string message)
        => popup ? ClosePopup() : BackToSettings(storeId, message);

    private ContentResult ClosePopup()
        => Content(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Supermultiverse</title></head>" +
            "<body data-smv-oauth-close style=\"font-family:system-ui,sans-serif;background:#0d1117;color:#adbac7;text-align:center;padding:3rem\">" +
            "<p>You can close this window.</p>" +
            "<script src=\"/plugins/smv/oauth-popup.js\"></script></body></html>",
            "text/html");

    private sealed record PendingConnect(string StoreId, string CodeVerifier, string ClientId, string RedirectUri, SmvBackendMode Mode, bool Popup);
}
