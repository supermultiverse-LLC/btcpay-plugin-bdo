using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Smv.Settings;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// <see cref="ISmvStoreSettingsProvider"/> backed by <see cref="IStoreRepository"/>.
/// Protect/unprotect happens entirely inside this component; the protected values are
/// the credential fields: tapd macaroon, Bitcoin RPC password, the mwv1_ bearer
/// (HostedApiToken), and the OAuth refresh token.
/// </summary>
public sealed class SmvStoreSettingsProvider : ISmvStoreSettingsProvider
{
    private readonly IStoreRepository _stores;
    private readonly ISmvCredentialProtector _protector;
    private readonly ILogger<SmvStoreSettingsProvider> _logger;

    public SmvStoreSettingsProvider(
        IStoreRepository stores,
        ISmvCredentialProtector protector,
        ILogger<SmvStoreSettingsProvider> logger)
    {
        _stores = stores;
        _protector = protector;
        _logger = logger;
    }

    public async Task<SmvStoreSettings?> GetAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        cancellationToken.ThrowIfCancellationRequested();

        var stored = await _stores.GetSettingAsync<SmvStoreSettings>(storeId, SmvStoreSettings.SettingName);
        if (stored is null)
            return null;

        // If any protected field fails to unprotect, treat the whole record as
        // unusable (return null) rather than serve partially-decrypted config.
        if (!TryUnprotectField(storeId, nameof(SmvStoreSettings.TapdMacaroonHex), stored.TapdMacaroonHex, out var macaroon))
            return null;
        if (!TryUnprotectField(storeId, nameof(SmvStoreSettings.BitcoinRpcPassword), stored.BitcoinRpcPassword, out var rpcPassword))
            return null;
        if (!TryUnprotectField(storeId, nameof(SmvStoreSettings.HostedApiToken), stored.HostedApiToken, out var hostedToken))
            return null;
        if (!TryUnprotectField(storeId, nameof(SmvStoreSettings.OAuthRefreshToken), stored.OAuthRefreshToken, out var oauthRefresh))
            return null;

        stored.TapdMacaroonHex = macaroon;
        stored.BitcoinRpcPassword = rpcPassword;
        stored.HostedApiToken = hostedToken;
        stored.OAuthRefreshToken = oauthRefresh;
        // BackendMode is a non-secret value type: it flows through on `stored`
        // as-is (missing in a pre-P3 record => default SmvBackendMode.Byon).
        return stored;
    }

    public async Task SetAsync(string storeId, SmvStoreSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        // Persist a copy with credential fields protected; never write plaintext.
        // NOTE: this is an explicit field-by-field copy — any new SmvStoreSettings
        // field MUST be added here or it is silently dropped on save.
        var toStore = new SmvStoreSettings
        {
            BackendMode = settings.BackendMode,
            TapdBaseUrl = settings.TapdBaseUrl,
            TapdMacaroonHex = ProtectField(settings.TapdMacaroonHex),
            TapdTlsCert = settings.TapdTlsCert,
            TapdHttpTimeoutMs = settings.TapdHttpTimeoutMs,
            BitcoinRpcUrl = settings.BitcoinRpcUrl,
            BitcoinRpcUser = settings.BitcoinRpcUser,
            BitcoinRpcPassword = ProtectField(settings.BitcoinRpcPassword),
            HostedApiToken = ProtectField(settings.HostedApiToken),
            // OAuth Connect (RFC-PLUGIN-007): OAuthRefreshToken is the only credential here.
            OAuthClientId = settings.OAuthClientId,
            OAuthRefreshToken = ProtectField(settings.OAuthRefreshToken),
            OAuthScopes = settings.OAuthScopes,
            OAuthTokenExpiresAtUnix = settings.OAuthTokenExpiresAtUnix,
            OAuthConnectedAccount = settings.OAuthConnectedAccount,
            OAuthTokenId = settings.OAuthTokenId,
            OAuthWalletId = settings.OAuthWalletId,
            OAuthConnectedMode = settings.OAuthConnectedMode,
            OAuthSessionKind = settings.OAuthSessionKind,
            PendingByonRegistrationsJson = settings.PendingByonRegistrationsJson
        };

        await _stores.UpdateSetting(storeId, SmvStoreSettings.SettingName, toStore);
    }

    public async Task DeleteAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeId);
        cancellationToken.ThrowIfCancellationRequested();

        // Passing null removes the Store record (StoreRepository.UpdateSetting).
        await _stores.UpdateSetting<SmvStoreSettings>(storeId, SmvStoreSettings.SettingName, null!);
    }

    private string? ProtectField(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? plaintext : _protector.Protect(plaintext);

    // Unprotects a single field within a logging scope carrying the Store id and
    // field name, so any safe failure event the protector emits is attributable
    // (Store + category) without this layer or the protector ever seeing a secret in a log.
    private bool TryUnprotectField(string storeId, string field, string? protectedValue, out string? plaintext)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            plaintext = protectedValue;
            return true;
        }

        using (_logger.BeginScope(new Dictionary<string, object?>
        {
            ["StoreId"] = storeId,
            ["Field"] = field
        }))
        {
            if (_protector.TryUnprotect(protectedValue, out var value))
            {
                plaintext = value;
                return true;
            }
        }

        plaintext = null;
        return false;
    }
}
