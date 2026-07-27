using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Core;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Plugins.Smv.Services.Tapd;
using BTCPayServer.Plugins.Smv.Settings;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Store-scoped resolver (P2/C3, extended in P3-H1a): reads the given Store's
/// settings exclusively via <see cref="ISmvStoreSettingsProvider"/> and returns the
/// backend for that Store's <see cref="SmvBackendMode"/> — <c>TapdAssetBackend</c>
/// (BYON) or <c>SmvHostedAssetBackend</c> (Hosted) — or <c>null</c> when that Store
/// is not configured. It never reads another Store, never enumerates Stores, and
/// there is no global fallback and no cross-mode fallback (TD §3.1, E3–E6).
/// </summary>
public sealed class SmvSettingsAssetBackendResolver : IAssetBackendResolver
{
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly SmvPublicApiClient _publicApi;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly Services.OAuth.SmvOAuthTokenService _oauthTokens;
    private readonly ILogger<TapdAssetBackend> _tapdLog;

    public SmvSettingsAssetBackendResolver(
        ISmvStoreSettingsProvider storeSettings,
        SmvPublicApiClient publicApi,
        ISettingsRepositoryAccessor serverSettings,
        Services.OAuth.SmvOAuthTokenService oauthTokens,
        ILogger<TapdAssetBackend> tapdLog)
    {
        _storeSettings = storeSettings;
        _publicApi = publicApi;
        _serverSettings = serverSettings;
        _oauthTokens = oauthTokens;
        _tapdLog = tapdLog;
    }

    public async Task<IAssetBackend?> ResolveAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            throw new ArgumentException("A non-empty storeId is required to resolve the asset backend.", nameof(storeId));

        var settings = await _storeSettings.GetAsync(storeId, cancellationToken);
        if (settings is null)
            return null; // Not configured for this Store: no backend, no fallback.

        return settings.BackendMode == SmvBackendMode.Hosted
            ? await ResolveHostedAsync(storeId, settings, cancellationToken)
            : ResolveByon(settings);
    }

    // BYON: requires tapd base URL + macaroon. The optional Bitcoin RPC config
    // (P3-H2) travels with the backend so it can report send-status confirmations.
    private IAssetBackend? ResolveByon(SmvStoreSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TapdBaseUrl) ||
            string.IsNullOrWhiteSpace(settings.TapdMacaroonHex))
        {
            return null;
        }

        var httpClient = TapdClient.CreateHttpClient(
            settings.TapdBaseUrl,
            settings.TapdMacaroonHex,
            settings.TapdHttpTimeoutMs);

        var bitcoinRpc = settings.HasBitcoinRpc
            ? new BitcoinRpcConfig(settings.BitcoinRpcUrl!, settings.BitcoinRpcUser!, settings.BitcoinRpcPassword!)
            : null;

        return new TapdAssetBackend(new TapdClient(httpClient), httpClient, settings.TapdBaseUrl, bitcoinRpc, _tapdLog, _publicApi);
    }

    // Hosted (P3): requires the mwv1_ token. The Managed Wallet API base is a
    // server-global default, overridable for dev.
    private async Task<IAssetBackend?> ResolveHostedAsync(string storeId, SmvStoreSettings settings, CancellationToken ct)
    {
        // OAuth Connect (RFC-PLUGIN-007): return a FRESH mwv1_, refreshing it first when it
        // is near expiry. For a non-OAuth Store this hands back the manually pasted token
        // unchanged. Either way, no token → not configured (no fallback).
        var token = await _oauthTokens.EnsureFreshTokenAsync(storeId, settings, ct);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var server = await _serverSettings.GetAsync();
        var baseUrl = string.IsNullOrWhiteSpace(server.HostedApiBase)
            ? SmvServerSettings.DefaultHostedApiBase
            : server.HostedApiBase;

        // OAuth Stores get the reactive re-auth handler: a 401 mid-lifetime (server-side
        // rotation / sweeper revocation) forces one refresh + re-exchange and retries the
        // request once. Manual-token Stores have nothing to refresh — no handler.
        var httpClient = ManagedWalletClient.CreateHttpClient(
            baseUrl,
            token,
            server.SmvHttpTimeoutMs,
            settings.HasOAuthConnection
                ? inner => new Services.OAuth.SmvOAuthReauthHandler(storeId, _oauthTokens, inner)
                : null);

        return new SmvHostedAssetBackend(new ManagedWalletClient(httpClient), httpClient, _publicApi);
    }
}
