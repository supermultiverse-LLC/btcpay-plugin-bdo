using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Smv.Services.OAuth;

/// <summary>
/// OAuth Connect client (RFC-PLUGIN-007 P1). Talks to two hosts:
///   • the Supabase authorization server (<c>issuerBase</c>, e.g. <c>…/auth/v1</c>) for
///     discovery, Dynamic Client Registration, and the token endpoint;
///   • the SMV edge functions (<c>functionsBase</c>, e.g. <c>…/functions/v1</c>) for the
///     capability registration, the JWT→mwv1_ bridge, and revoke-self.
///
/// The plugin is a public client (no secret, PKCE S256). This class is request/parse only —
/// PKCE codes come from <see cref="Core.PkceCodes"/> and the controller owns state/persistence.
///
/// VERIFY-LIVE status: DCR (<see cref="RegisterClientAsync"/>) is confirmed against the live
/// server (201, public client). The token + bridge calls follow the sealed §11/§12 contract
/// but await end-to-end testing once the edge functions are deployed + Studio login honours
/// <c>next=</c>. <see cref="ExchangeMwv1Async"/> parses the token field tolerantly
/// (<c>access_token</c> or <c>mwv1_token</c>) until Lovable seals §12.2.
/// </summary>
public sealed class SmvOAuthClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _issuerBase;     // no trailing slash
    private readonly string _functionsBase;  // no trailing slash
    private readonly string? _anonKey;       // Supabase anon key (public) — GoTrue endpoints

    public SmvOAuthClient(HttpClient http, string issuerBase, string functionsBase, string? anonKey = null)
    {
        _http = http;
        _issuerBase = issuerBase.TrimEnd('/');
        _functionsBase = functionsBase.TrimEnd('/');
        _anonKey = anonKey;
    }

    /// <summary>GET the OpenID/OAuth discovery document (endpoints + PKCE methods).</summary>
    public async Task<OAuthDiscovery> DiscoverAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{_issuerBase}/.well-known/openid-configuration", ct);
        await EnsureAsync(resp, ct);
        using var doc = await ReadJsonAsync(resp, ct);
        var r = doc.RootElement;
        return new OAuthDiscovery(
            Issuer: Str(r, "issuer"),
            AuthorizationEndpoint: Str(r, "authorization_endpoint"),
            TokenEndpoint: Str(r, "token_endpoint"),
            RegistrationEndpoint: Str(r, "registration_endpoint"),
            JwksUri: Str(r, "jwks_uri"));
    }

    /// <summary>Dynamic Client Registration (RFC 7591). Public client + PKCE, one per Store.
    /// Returns the assigned <c>client_id</c>. VERIFIED live.</summary>
    public async Task<string> RegisterClientAsync(
        string clientName, string redirectUri, string? registrationEndpoint = null, CancellationToken ct = default)
    {
        var body = new
        {
            client_name = clientName,
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
            scope = "openid profile email"
        };
        var url = string.IsNullOrWhiteSpace(registrationEndpoint)
            ? $"{_issuerBase}/oauth/clients/register"
            : registrationEndpoint;

        using var resp = await _http.PostAsync(url, JsonContent(body), ct);
        await EnsureAsync(resp, ct);
        using var doc = await ReadJsonAsync(resp, ct);
        return Str(doc.RootElement, "client_id")
               ?? throw new SmvOAuthException(0, null, "Registration returned no client_id.");
    }

    /// <summary>Register this client's <c>assets:*</c> capabilities (§12.1). No auth — same
    /// public class as DCR. Upsert by <c>client_id</c>.</summary>
    public async Task RegisterCapabilitiesAsync(
        string clientId, string clientName, IEnumerable<string> capabilities, CancellationToken ct = default)
    {
        var body = new { client_id = clientId, client_name = clientName, capabilities = capabilities.ToArray() };
        using var resp = await _http.PostAsync($"{_functionsBase}/oauth-register-capabilities", JsonContent(body), ct);
        await EnsureAsync(resp, ct);
    }

    /// <summary>Exchange an authorization <paramref name="code"/> for the identity tokens
    /// (Authorization Code + PKCE). Form-encoded per the OAuth token-endpoint standard.</summary>
    public Task<OAuthTokens> ExchangeCodeAsync(
        string code, string codeVerifier, string redirectUri, string clientId,
        string? tokenEndpoint = null, CancellationToken ct = default)
        => TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier
        }, tokenEndpoint, ct);

    /// <summary>Refresh the identity tokens with a rotating refresh token.</summary>
    public Task<OAuthTokens> RefreshAsync(
        string refreshToken, string clientId, string? tokenEndpoint = null, CancellationToken ct = default)
        => TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId
        }, tokenEndpoint, ct);

    // ── Embedded identity (RFC-PLUGIN-008) — GoTrue endpoints, not the OAuth server ──

    /// <summary>Native sign-up via the <c>plugin-signup</c> edge function (RFC-008 B2):
    /// creates an ALREADY-CONFIRMED account for a registered plugin client. Typed errors
    /// surface as <see cref="SmvOAuthException"/> with codes <c>email_taken</c>,
    /// <c>client_not_registered</c>, <c>rate_limited</c>, <c>invalid_request</c>.</summary>
    public async Task SignUpAsync(string clientId, string email, string password, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_functionsBase}/plugin-signup")
        {
            Content = JsonContent(new { client_id = clientId, email, password, tos_accepted = true })
        };
        AddAnonKey(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureAsync(resp, ct);
    }

    /// <summary>Native sign-in: GoTrue password grant. Returns the same identity-token
    /// shape as the OAuth code exchange, so everything downstream (bridge, refresh,
    /// persistence) is shared. Requires the anon key.</summary>
    public async Task<OAuthTokens> PasswordSignInAsync(string email, string password, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_issuerBase}/token?grant_type=password")
        {
            Content = JsonContent(new { email, password })
        };
        AddAnonKey(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureAsync(resp, ct);
        using var doc = await ReadJsonAsync(resp, ct);
        var r = doc.RootElement;
        return new OAuthTokens(
            AccessToken: Str(r, "access_token") ?? throw new SmvOAuthException(0, null, "Sign-in returned no access_token."),
            RefreshToken: Str(r, "refresh_token"),
            ExpiresInSeconds: r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600);
    }

    /// <summary>Refresh an embedded (password-grant) session at the GoTrue refresh
    /// endpoint. SSO sessions refresh at the OAuth server's <c>/oauth/token</c> instead
    /// (<see cref="RefreshAsync"/>) — the two must not be mixed.</summary>
    public async Task<OAuthTokens> RefreshGotrueAsync(string refreshToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_issuerBase}/token?grant_type=refresh_token")
        {
            Content = JsonContent(new { refresh_token = refreshToken })
        };
        AddAnonKey(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureAsync(resp, ct);
        using var doc = await ReadJsonAsync(resp, ct);
        var r = doc.RootElement;
        return new OAuthTokens(
            AccessToken: Str(r, "access_token") ?? throw new SmvOAuthException(0, null, "Refresh returned no access_token."),
            RefreshToken: Str(r, "refresh_token"),
            ExpiresInSeconds: r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600);
    }

    /// <summary>Email-first activation step 1 (RFC-008 amendment): GoTrue <c>/otp</c> emails a
    /// one-time 6-digit code (the email also carries a magic link, but the CODE is what
    /// activates the plugin — the merchant may read mail on another device). New emails are
    /// auto-provisioned (<c>create_user</c>) with the ToS acceptance recorded in user metadata;
    /// wallet provisioning happens at the first mwv1_ exchange as always (RFC-008 B1).</summary>
    public async Task RequestEmailCodeAsync(string email, bool tosAccepted, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_issuerBase}/otp")
        {
            Content = JsonContent(new
            {
                email,
                create_user = true,
                data = new
                {
                    signup_source = "btcpay-plugin-otp",
                    tos_accepted = tosAccepted,
                    tos_accepted_at = DateTimeOffset.UtcNow.ToString("o")
                }
            })
        };
        AddAnonKey(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureAsync(resp, ct);
    }

    /// <summary>Email-first activation step 2: GoTrue <c>/verify</c> turns email + code into a
    /// session — the same identity-token shape as the password grant, so everything downstream
    /// (bridge, refresh via <see cref="RefreshGotrueAsync"/>, persistence) is shared.</summary>
    public async Task<OAuthTokens> VerifyEmailCodeAsync(string email, string code, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_issuerBase}/verify")
        {
            Content = JsonContent(new { type = "email", email, token = code })
        };
        AddAnonKey(req);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureAsync(resp, ct);
        using var doc = await ReadJsonAsync(resp, ct);
        var r = doc.RootElement;
        return new OAuthTokens(
            AccessToken: Str(r, "access_token") ?? throw new SmvOAuthException(0, null, "Verification returned no access_token."),
            RefreshToken: Str(r, "refresh_token"),
            ExpiresInSeconds: r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600);
    }

    private void AddAnonKey(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_anonKey))
            req.Headers.TryAddWithoutValidation("apikey", _anonKey);
    }

    /// <summary>Bridge the identity JWT to an <c>mwv1_</c> (§12.2). The server reads the
    /// registered capabilities by <c>client_id</c>; the plugin sends no capabilities here.
    /// 200 → partial-or-full grant; 403 <c>insufficient_entitlement</c> → nothing granted.</summary>
    public async Task<Mwv1Grant> ExchangeMwv1Async(string identityJwt, string clientId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_functionsBase}/oauth-exchange-mwv1")
        {
            Content = JsonContent(new { client_id = clientId })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identityJwt);

        using var resp = await _http.SendAsync(req, ct);
        await EnsureAsync(resp, ct);   // 403 insufficient_entitlement / 400 invalid_request → typed throw

        using var doc = await ReadJsonAsync(resp, ct);
        var r = doc.RootElement;

        // §12.2 SEALED: the token field is access_token (carries the mwv1_…); the
        // mwv1_token fallback is defensive only.
        var token = Str(r, "access_token") ?? Str(r, "mwv1_token")
            ?? throw new SmvOAuthException(0, null, "Exchange returned no token.");

        return new Mwv1Grant(
            Mwv1Token: token,
            Scopes: StrArray(r, "scopes"),
            Denied: ParseDenied(r),
            ExpiresAtUnix: ParseExpiresAt(r),
            AccountLabel: Str(r, "account_label"),
            TokenId: Str(r, "token_id"),
            WalletId: Str(r, "wallet_id"));
    }

    /// <summary>Best-effort local disconnect acceleration (§11.5) via <c>oauth-revoke-self</c>.
    /// Idempotent server-side; also swept centrally. Non-fatal on failure.</summary>
    public async Task RevokeSelfAsync(string mwv1Token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_functionsBase}/oauth-revoke-self")
        {
            Content = JsonContent(new { reason = "user_disconnected" })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mwv1Token);
        using var resp = await _http.SendAsync(req, ct);
        // Swallow — revocation is idempotent and centrally swept; this only speeds it up.
    }

    private async Task<OAuthTokens> TokenAsync(
        IDictionary<string, string> form, string? tokenEndpoint, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(tokenEndpoint) ? $"{_issuerBase}/oauth/token" : tokenEndpoint;
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        await EnsureAsync(resp, ct);
        using var doc = await ReadJsonAsync(resp, ct);
        var r = doc.RootElement;
        return new OAuthTokens(
            AccessToken: Str(r, "access_token") ?? throw new SmvOAuthException(0, null, "Token response had no access_token."),
            RefreshToken: Str(r, "refresh_token"),
            ExpiresInSeconds: r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 3600);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private static StringContent JsonContent(object body)
        => new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(s, cancellationToken: ct);
    }

    // Non-2xx → typed exception carrying the sealed error envelope's code when present.
    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string? code = null, message = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            code = Str(r, "error") ?? (r.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object ? Str(errObj, "code") : null);
            message = Str(r, "error_description") ?? Str(r, "message");
        }
        catch { /* non-JSON body → fall through */ }

        throw new SmvOAuthException((int)resp.StatusCode, code,
            message ?? $"OAuth request failed with HTTP {(int)resp.StatusCode}.");
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static IReadOnlyList<string> StrArray(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
    }

    // denied is [{capability, reason}] (§12.2 sealed). Tolerate the older {scope,…} key
    // and a bare string array too.
    private static IReadOnlyList<Mwv1Denied> ParseDenied(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("denied", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<Mwv1Denied>();
        var list = new List<Mwv1Denied>();
        foreach (var d in arr.EnumerateArray())
        {
            if (d.ValueKind == JsonValueKind.String)
                list.Add(new Mwv1Denied(d.GetString()!, null));
            else if (d.ValueKind == JsonValueKind.Object)
                list.Add(new Mwv1Denied(Str(d, "capability") ?? Str(d, "scope") ?? "", Str(d, "reason")));
        }
        return list;
    }

    private static long? ParseExpiresAt(JsonElement r)
    {
        var iso = Str(r, "expires_at");
        if (!string.IsNullOrWhiteSpace(iso) && DateTimeOffset.TryParse(iso, out var dto))
            return dto.ToUnixTimeSeconds();
        return null;
    }
}

public sealed record OAuthDiscovery(
    string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint,
    string? RegistrationEndpoint, string? JwksUri);

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, long ExpiresInSeconds);

public sealed record Mwv1Denied(string Scope, string? Reason);

public sealed record Mwv1Grant(
    string Mwv1Token,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Mwv1Denied> Denied,
    long? ExpiresAtUnix,
    string? AccountLabel,
    string? TokenId,
    string? WalletId);

/// <summary>An OAuth Connect failure. <see cref="Code"/> carries the sealed error-envelope
/// code (e.g. <c>insufficient_entitlement</c>, <c>invalid_request</c>) when the server sent one.</summary>
public sealed class SmvOAuthException : Exception
{
    public int StatusCode { get; }
    public string? Code { get; }
    public SmvOAuthException(int statusCode, string? code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }
}
