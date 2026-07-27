using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Settings;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Services.OAuth;

/// <summary>
/// Keeps a Store's OAuth-obtained <c>mwv1_</c> fresh (RFC-PLUGIN-007 §11.3). The bearer
/// lives 1h, so before a backend uses it the resolver asks this service for a current
/// token: if it is near/at expiry, refresh the Supabase JWT with the rotating refresh
/// token, re-exchange for a new <c>mwv1_</c>, and persist.
///
/// Rotating refresh tokens make concurrency dangerous — two parallel refreshes would each
/// rotate the refresh and invalidate the other. A per-Store async lock serialises refresh
/// so only one runs; late arrivals re-read the freshly-persisted token instead of rotating.
/// </summary>
public sealed class SmvOAuthTokenService
{
    // Static so the lock is shared across this scoped service's per-request instances.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private const long RefreshSkewSeconds = 120;   // refresh when ≤2 min remain
    private static readonly TimeSpan OAuthTimeout = TimeSpan.FromSeconds(15);

    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SmvOAuthTokenService> _log;

    public SmvOAuthTokenService(
        ISmvStoreSettingsProvider storeSettings,
        ISettingsRepositoryAccessor serverSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<SmvOAuthTokenService> log)
    {
        _storeSettings = storeSettings;
        _serverSettings = serverSettings;
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    /// <summary>The current usable <c>mwv1_</c> for the Store, refreshing first if needed.
    /// Returns the manual token unchanged for a non-OAuth Store, or null when there is no
    /// usable token. On a refresh failure it returns the (stale) token so the API call —
    /// not this method — surfaces the failure; a genuinely revoked grant then prompts a
    /// reconnect via the normal 401 path.</summary>
    public async Task<string?> EnsureFreshTokenAsync(string storeId, SmvStoreSettings settings, CancellationToken ct)
    {
        // Not an OAuth connection: hand back whatever manual token is configured.
        if (!settings.HasOAuthConnection)
            return string.IsNullOrWhiteSpace(settings.HostedApiToken) ? null : settings.HostedApiToken;

        if (!NeedsRefresh(settings))
            return settings.HostedApiToken;

        return await RefreshLockedAsync(storeId, staleToken: settings.HostedApiToken, force: false, ct);
    }

    /// <summary>Reactive refresh (RFC §11.3 "or a call returns 401"): the mwv1_ was
    /// rejected mid-lifetime (server-side rotation or sweeper revocation), so refresh the
    /// JWT and re-exchange NOW regardless of the stored expiry. Returns the fresh token,
    /// or null when the Store is not OAuth-connected or the refresh itself fails
    /// (grant revoked → the caller surfaces the 401 and the UI offers Reconnect).</summary>
    public async Task<string?> ForceRefreshAsync(string storeId, string? rejectedToken, CancellationToken ct)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        if (settings is null || !settings.HasOAuthConnection)
            return null;

        // Another request may have already rotated past the token that just 401'd —
        // if the stored token differs from the rejected one, use it without refreshing.
        if (!string.IsNullOrWhiteSpace(rejectedToken) &&
            !string.Equals(settings.HostedApiToken, rejectedToken, StringComparison.Ordinal))
            return settings.HostedApiToken;

        return await RefreshLockedAsync(storeId, staleToken: null, force: true, ct);
    }

    private async Task<string?> RefreshLockedAsync(string storeId, string? staleToken, bool force, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-read under the lock: another request may have just refreshed.
            var fresh = await _storeSettings.GetAsync(storeId, ct);
            if (fresh is null || !fresh.HasOAuthConnection)
                return force ? null : fresh?.HostedApiToken;
            if (!force && !NeedsRefresh(fresh))
                return fresh.HostedApiToken;

            var server = await _serverSettings.GetAsync();
            var client = BuildClient(server);

            // RFC-008: embedded (password-grant) sessions refresh at the GoTrue /token
            // endpoint; SSO sessions at the OAuth server's /oauth/token. Never mix.
            var tokens = fresh.IsEmbeddedSession
                ? await client.RefreshGotrueAsync(fresh.OAuthRefreshToken!, ct)
                : await client.RefreshAsync(fresh.OAuthRefreshToken!, fresh.OAuthClientId!, ct: ct);
            var grant = await client.ExchangeMwv1Async(tokens.AccessToken, fresh.OAuthClientId!, ct);

            fresh.HostedApiToken = grant.Mwv1Token;
            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) fresh.OAuthRefreshToken = tokens.RefreshToken;
            fresh.OAuthScopes = string.Join(',', grant.Scopes);
            fresh.OAuthTokenExpiresAtUnix = grant.ExpiresAtUnix;
            if (!string.IsNullOrWhiteSpace(grant.AccountLabel)) fresh.OAuthConnectedAccount = grant.AccountLabel;
            if (!string.IsNullOrWhiteSpace(grant.TokenId)) fresh.OAuthTokenId = grant.TokenId;
            if (!string.IsNullOrWhiteSpace(grant.WalletId)) fresh.OAuthWalletId = grant.WalletId;

            await _storeSettings.SetAsync(storeId, fresh, ct);
            _log.LogInformation("oauth_refresh.rotated store={StoreId} force={Force}", storeId, force);
            return fresh.HostedApiToken;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "oauth_refresh.failed store={StoreId} force={Force}", storeId, force);
            // Proactive: hand back the stale token so the API call surfaces the failure.
            // Reactive (force): the token already 401'd — null tells the caller to give up.
            return force ? null : staleToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool NeedsRefresh(SmvStoreSettings s)
    {
        // Can't refresh without both halves — treat as "don't refresh" (use as-is).
        if (string.IsNullOrWhiteSpace(s.OAuthRefreshToken) || string.IsNullOrWhiteSpace(s.OAuthClientId))
            return false;
        if (s.OAuthTokenExpiresAtUnix is not long exp)
            return true;   // unknown expiry → refresh to be safe
        return exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= RefreshSkewSeconds;
    }

    private SmvOAuthClient BuildClient(SmvServerSettings server)
    {
        var http = _httpClientFactory.CreateClient("smv-oauth");
        http.Timeout = OAuthTimeout;
        return new SmvOAuthClient(http, server.OAuthIssuerBase, server.HostedApiBase, server.SupabaseAnonKey);
    }
}
