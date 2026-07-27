using System;
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
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Embedded identity (RFC-PLUGIN-008 §4): native Sign in / Create account for the
/// plugin, so a BTCPay merchant works without ever leaving BTCPay. Credentials go
/// server-side from this controller to the platform (GoTrue password grant /
/// plugin-signup) and are NEVER persisted or logged — only the resulting session
/// tokens are stored, in exactly the per-Store fields the SSO flow uses.
///
/// Pipeline (both actions): ensure the Store's OAuth client + capabilities are
/// registered (same helpers as the SSO flow) → obtain an identity JWT → bridge to
/// mwv1_ via oauth-exchange-mwv1 → persist with OAuthSessionKind = "embedded"
/// (refreshes at the GoTrue /token endpoint — see SmvOAuthTokenService).
/// </summary>
[Route("stores/{storeId}/plugins/smv/account")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvAccountController : Controller
{
    private static readonly TimeSpan AccountTimeout = TimeSpan.FromSeconds(15);

    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SmvAccountController> _log;

    public SmvAccountController(
        ISmvStoreSettingsProvider storeSettings,
        ISettingsRepositoryAccessor serverSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<SmvAccountController> log)
    {
        _storeSettings = storeSettings;
        _serverSettings = serverSettings;
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    [HttpPost("sign-in")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SignIn(string email, string password, CancellationToken ct)
        => RunAsync(email, password, signUp: false, tosAccepted: true, ct);

    [HttpPost("sign-up")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SignUp(string email, string password, bool tosAccepted, CancellationToken ct)
        => RunAsync(email, password, signUp: true, tosAccepted, ct);

    /// <summary>Email-first activation step 1 (RFC-008 amendment): send the one-time code.
    /// ToS acceptance is required up front because a new email is auto-provisioned.</summary>
    [HttpPost("email-code")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestEmailCode(string email, bool tosAccepted, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email))
            return Back(store.Id, "Enter your email.");
        if (!tosAccepted)
            return Back(store.Id, "Please accept the Terms of Service to activate your account.");

        var server = await _serverSettings.GetAsync();
        var client = BuildClient(server);
        try
        {
            await client.RequestEmailCodeAsync(email, tosAccepted, ct);
            TempData["SmvOtpEmail"] = email;
            return Back(store.Id, $"Code sent to {email} — check your inbox and enter the 6-digit code below.");
        }
        catch (SmvOAuthException ex)
        {
            _log.LogInformation("account.otp_request_failed store={StoreId} code={Code} status={Status}", store.Id, ex.Code, ex.StatusCode);
            return Back(store.Id, ex.StatusCode == 429
                ? "Too many codes requested. Please wait a minute and try again."
                : "Could not send the code. " + (ex.Message ?? "Please try again."));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "account.otp_request_unexpected store={StoreId}", store.Id);
            return Back(store.Id, "Something went wrong talking to the platform. Please try again.");
        }
    }

    /// <summary>Email-first activation step 2: verify the code → session → the same
    /// client-registration + mwv1_ bridge + persistence pipeline as password sign-in.</summary>
    [HttpPost("email-verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyEmailCode(string email, string code, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        email = (email ?? "").Trim();
        code = (code ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
        {
            TempData["SmvOtpEmail"] = email;
            return Back(store.Id, "Enter the 6-digit code from the email.");
        }

        var settings = await _storeSettings.GetAsync(store.Id, ct);
        var server = await _serverSettings.GetAsync();
        var mode = settings?.BackendMode ?? SmvBackendMode.Byon;
        var client = BuildClient(server);
        try
        {
            var clientId = await EnsureClientAsync(store, settings, mode, client, ct);
            var tokens = await client.VerifyEmailCodeAsync(email, code, ct);
            return await CompleteAsync(store.Id, mode, client, clientId, tokens, email, ct);
        }
        catch (SmvOAuthException ex)
        {
            _log.LogInformation("account.otp_verify_failed store={StoreId} code={Code} status={Status}", store.Id, ex.Code, ex.StatusCode);
            TempData["SmvOtpEmail"] = email;   // keep the code form on screen for a retry
            return Back(store.Id, ex.StatusCode is 400 or 401 or 403
                ? "That code is wrong or has expired — enter it again, or request a new one."
                : MapError(ex, signUp: false));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "account.otp_verify_unexpected store={StoreId}", store.Id);
            return Back(store.Id, "Something went wrong talking to the platform. Please try again.");
        }
    }

    private async Task<IActionResult> RunAsync(string email, string password, bool signUp, bool tosAccepted, CancellationToken ct)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return Back(store.Id, "Enter your email and password.");
        if (signUp && password.Length < 8)
            return Back(store.Id, "The password must be at least 8 characters.");
        if (signUp && !tosAccepted)
            return Back(store.Id, "Please accept the Terms of Service to create your account.");

        var settings = await _storeSettings.GetAsync(store.Id, ct);
        var server = await _serverSettings.GetAsync();
        var mode = settings?.BackendMode ?? SmvBackendMode.Byon;
        var client = BuildClient(server);

        try
        {
            var clientId = await EnsureClientAsync(store, settings, mode, client, ct);

            if (signUp)
                await client.SignUpAsync(clientId, email, password, ct);

            var tokens = await client.PasswordSignInAsync(email, password, ct);
            return await CompleteAsync(store.Id, mode, client, clientId, tokens, email, ct);
        }
        catch (SmvOAuthException ex)
        {
            _log.LogInformation("account.failed store={StoreId} code={Code} status={Status}", store.Id, ex.Code, ex.StatusCode);
            return Back(store.Id, MapError(ex, signUp));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "account.unexpected store={StoreId}", store.Id);
            return Back(store.Id, "Something went wrong talking to the platform. Please try again.");
        }
    }

    /// <summary>Same per-Store client registration the SSO flow uses (§11.7 R1 / §12.1) —
    /// shared by every embedded path (password sign-in/up, email code).</summary>
    private async Task<string> EnsureClientAsync(
        BTCPayServer.Data.StoreData store, SmvStoreSettings? settings, SmvBackendMode mode, SmvOAuthClient client, CancellationToken ct)
    {
        var clientId = settings?.OAuthClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            var redirectUri = Url.Action("Callback", "SmvOAuth", new { storeId = store.Id }, Request.Scheme)!;
            clientId = await client.RegisterClientAsync(SmvOAuthController.ClientNameFor(store), redirectUri, ct: ct);
        }
        await client.RegisterCapabilitiesAsync(
            clientId!, SmvOAuthController.ClientNameFor(store), SmvOAuthController.CapabilitiesFor(mode), ct);
        return clientId!;
    }

    /// <summary>Identity tokens → mwv1_ bridge → per-Store persistence (kind "embedded").
    /// The tail every embedded path shares, regardless of how the session was obtained.</summary>
    private async Task<IActionResult> CompleteAsync(
        string storeId, SmvBackendMode mode, SmvOAuthClient client, string clientId,
        OAuthTokens tokens, string email, CancellationToken ct)
    {
        var grant = await client.ExchangeMwv1Async(tokens.AccessToken, clientId, ct);

        var fresh = await _storeSettings.GetAsync(storeId, ct) ?? new SmvStoreSettings();
        fresh.OAuthClientId = clientId;
        fresh.HostedApiToken = grant.Mwv1Token;
        fresh.OAuthRefreshToken = tokens.RefreshToken;
        fresh.OAuthScopes = string.Join(',', grant.Scopes);
        fresh.OAuthTokenExpiresAtUnix = grant.ExpiresAtUnix;
        fresh.OAuthConnectedAccount = grant.AccountLabel ?? email;
        fresh.OAuthTokenId = grant.TokenId;
        fresh.OAuthWalletId = grant.WalletId;
        fresh.OAuthConnectedMode = mode;
        fresh.OAuthSessionKind = "embedded";
        await _storeSettings.SetAsync(storeId, fresh, ct);

        _log.LogInformation("account.connected store={StoreId}", storeId);
        return Back(storeId, SmvOAuthCopy.ConnectedMessage(grant.AccountLabel ?? email, grant.Denied));
    }

    private static string MapError(SmvOAuthException ex, bool signUp) => ex.Code switch
    {
        "email_taken" => "An account with this email already exists — use Sign in instead.",
        "client_not_registered" => "This Store isn't registered with the platform yet. Please try again.",
        "rate_limited" => "Too many attempts from this server. Please wait a while and try again.",
        "insufficient_entitlement" => "Signed in, but none of the requested capabilities are available for this account yet.",
        "invalid_grant" => "Wrong email or password.",
        // GoTrue wraps bad credentials in HTTP 400 with assorted codes; be forgiving.
        _ when ex.StatusCode == 400 && !signUp => "Wrong email or password.",
        _ when ex.StatusCode == 429 => "Too many attempts. Please wait a moment and try again.",
        _ => (signUp ? "Could not create the account. " : "Could not sign in. ") + (ex.Message ?? "Please try again."),
    };

    private SmvOAuthClient BuildClient(SmvServerSettings server)
    {
        var http = _httpClientFactory.CreateClient("smv-oauth");
        http.Timeout = AccountTimeout;
        return new SmvOAuthClient(http, server.OAuthIssuerBase, server.HostedApiBase, server.SupabaseAnonKey);
    }

    private IActionResult Back(string storeId, string message)
    {
        TempData["StatusMessage"] = message;
        return RedirectToAction("Index", "SmvSettings", new { storeId });
    }
}
