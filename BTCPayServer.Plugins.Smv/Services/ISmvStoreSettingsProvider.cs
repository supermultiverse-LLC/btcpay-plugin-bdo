using BTCPayServer.Plugins.Smv.Settings;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// Reads and writes the Store-scoped <see cref="SmvStoreSettings"/> record,
/// backed by <c>IStoreRepository</c>. Credential fields are protected on write
/// and unprotected on read INSIDE this component; callers only ever see
/// plaintext in memory and never touch the protector directly (TD §3.2–3.3).
/// </summary>
public interface ISmvStoreSettingsProvider
{
    /// <summary>
    /// Returns the Store's settings with credential fields unprotected, or
    /// <c>null</c> when no record exists or a protected field fails to unprotect
    /// (E4/E17) — a Store is never served partially-decrypted configuration.
    /// </summary>
    Task<SmvStoreSettings?> GetAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the Store's settings, protecting credential fields at rest.
    /// Plaintext credentials are never written to storage.
    /// </summary>
    Task SetAsync(string storeId, SmvStoreSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Removes the Store's settings record entirely.</summary>
    Task DeleteAsync(string storeId, CancellationToken cancellationToken = default);
}
