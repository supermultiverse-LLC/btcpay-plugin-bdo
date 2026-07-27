using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Smv.Settings;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// One-shot, assisted, fail-safe migration of the legacy server-global backend
/// configuration into a single admin-selected Store, plus structural rollback
/// (TD §6.4, F4). The runtime never consults the backup; it exists only for the
/// rollback window and is removed in a later release.
///
/// Secrets are never written in plaintext outside the protected Store record and
/// the protected backup, and are never logged.
/// </summary>
public sealed class SmvMigration
{
    // Legacy/global public record (unchanged key). Marker + backup are separate
    // server-global records under versioned keys.
    private const string LegacyKey = "Smv.Settings";
    private const string MarkerKey = "BTCPayServer.Plugins.Smv.Migration.v1";
    private const string BackupKey = "BTCPayServer.Plugins.Smv.MigrationBackup.v1";
    private const string MigrationVersion = "1";

    private readonly ISettingsRepository _global;
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly ISmvCredentialProtector _protector;
    private readonly ILogger<SmvMigration> _log;

    public SmvMigration(
        ISettingsRepository global,
        ISmvStoreSettingsProvider storeSettings,
        ISmvCredentialProtector protector,
        ILogger<SmvMigration> log)
    {
        _global = global;
        _storeSettings = storeSettings;
        _protector = protector;
        _log = log;
    }

    public async Task<SmvMigrationStatus> GetStatusAsync()
    {
        var marker = await _global.GetSettingAsync<SmvMigrationMarker>(MarkerKey);
        var legacy = await _global.GetSettingAsync<SmvSettings>(LegacyKey);
        var backup = await _global.GetSettingAsync<SmvMigrationBackup>(BackupKey);

        var legacyBackendPresent = legacy is not null &&
            (!string.IsNullOrWhiteSpace(legacy.TapdBaseUrl) ||
             !string.IsNullOrWhiteSpace(legacy.TapdMacaroonHex) ||
             legacy.HasBitcoinRpc);

        return new SmvMigrationStatus
        {
            Migrated = marker?.Migrated == true,
            TargetStoreId = marker?.TargetStoreId,
            MigratedAtUtc = marker?.MigratedAtUtc,
            LegacyBackendPresent = legacyBackendPresent,
            BackupPresent = backup is not null
        };
    }

    /// <summary>
    /// Migrates the legacy backend subset into <paramref name="targetStoreId"/>.
    /// Idempotent (marker), never overwrites a configured Store, and leaves no
    /// window in which Verify lacks its public config (the reduce step is a single
    /// write that still carries the public subset).
    /// </summary>
    public async Task<SmvMigrationResult> MigrateAsync(string targetStoreId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetStoreId))
            return SmvMigrationResult.Refused("A target Store must be selected.");

        // 1. Marker present → no-op (idempotent).
        var marker = await _global.GetSettingAsync<SmvMigrationMarker>(MarkerKey);
        if (marker?.Migrated == true)
            return SmvMigrationResult.NoOp($"Migration already completed (Store {marker.TargetStoreId}).");

        // 2. Global record absent → nothing to migrate.
        var legacy = await _global.GetSettingAsync<SmvSettings>(LegacyKey);
        if (legacy is null)
            return SmvMigrationResult.NoOp("No global settings record exists; nothing to migrate.");

        // 3. Target Store already configured → refuse, never overwrite.
        var existing = await _storeSettings.GetAsync(targetStoreId, cancellationToken);
        if (existing is not null)
            return SmvMigrationResult.Refused("The selected Store already has SMV settings. Refusing to overwrite.");

        // 4. Map backend subset → Store record. The provider protects sensitive fields on write.
        var storeRecord = new SmvStoreSettings
        {
            TapdBaseUrl = legacy.TapdBaseUrl,
            TapdMacaroonHex = legacy.TapdMacaroonHex,
            TapdTlsCert = legacy.TapdTlsCert,
            TapdHttpTimeoutMs = legacy.TapdHttpTimeoutMs,
            BitcoinRpcUrl = legacy.BitcoinRpcUrl,
            BitcoinRpcUser = legacy.BitcoinRpcUser,
            BitcoinRpcPassword = legacy.BitcoinRpcPassword
        };
        await _storeSettings.SetAsync(targetStoreId, storeRecord, cancellationToken);

        // 5. Write the temporary protected backup (sole rollback source, F4).
        var backup = new SmvMigrationBackup
        {
            TargetStoreId = targetStoreId,
            Version = MigrationVersion,
            CreatedAtUtc = DateTime.UtcNow.ToString("o"),
            TapdBaseUrl = legacy.TapdBaseUrl,
            TapdMacaroonHexProtected = Protect(legacy.TapdMacaroonHex),
            TapdTlsCert = legacy.TapdTlsCert,
            TapdHttpTimeoutMs = legacy.TapdHttpTimeoutMs,
            BitcoinRpcUrl = legacy.BitcoinRpcUrl,
            BitcoinRpcUser = legacy.BitcoinRpcUser,
            BitcoinRpcPasswordProtected = Protect(legacy.BitcoinRpcPassword)
        };
        await _global.UpdateSetting(backup, BackupKey);

        // 6. Reduce Smv.Settings to the public subset. Single write; the record
        //    always carries the public fields, so Verify is never left without config.
        var reduced = new SmvServerSettings
        {
            SmvPublicApiBase = string.IsNullOrWhiteSpace(legacy.SmvPublicApiBase)
                ? SmvServerSettings.DefaultApiBase
                : legacy.SmvPublicApiBase,
            StasProofDecodeEndpoint = legacy.StasProofDecodeEndpoint,
            SmvHttpTimeoutMs = legacy.SmvHttpTimeoutMs,
            SmvCacheTtlSeconds = legacy.SmvCacheTtlSeconds,
            SmvProofMaxBytes = legacy.SmvProofMaxBytes
        };
        await _global.UpdateSetting(reduced, LegacyKey);

        // 7. Write the migration marker (idempotency).
        await _global.UpdateSetting(new SmvMigrationMarker
        {
            Migrated = true,
            TargetStoreId = targetStoreId,
            Version = MigrationVersion,
            MigratedAtUtc = DateTime.UtcNow.ToString("o")
        }, MarkerKey);

        // 8. Audit — what, which Store, when. Never any credential content.
        _log.LogInformation("smv.migration.completed store={StoreId} version={Version}", targetStoreId, MigrationVersion);

        return SmvMigrationResult.Ok($"Migration completed. Backend moved to Store {targetStoreId}; the global record is now the public subset.");
    }

    /// <summary>
    /// Structural rollback: removes the migration marker and the Store record the
    /// migration created, keeps <c>Smv.Settings</c> as the public subset, and
    /// retains the protected backup. Never restores a secret to plaintext.
    /// </summary>
    public async Task<SmvMigrationResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        var marker = await _global.GetSettingAsync<SmvMigrationMarker>(MarkerKey);
        if (marker?.Migrated != true)
            return SmvMigrationResult.NoOp("No migration to roll back.");

        // Remove the Store record the migration created (physically removed).
        if (!string.IsNullOrWhiteSpace(marker.TargetStoreId))
            await _storeSettings.DeleteAsync(marker.TargetStoreId, cancellationToken);

        // Reset the marker to "not migrated". ISettingsRepository has no delete;
        // Migrated=false is behaviourally equivalent — the deployment reads as
        // pending reconfiguration and the migration may be re-run.
        await _global.UpdateSetting(new SmvMigrationMarker
        {
            Migrated = false,
            TargetStoreId = null,
            Version = MigrationVersion,
            MigratedAtUtc = null,
            RolledBackAtUtc = DateTime.UtcNow.ToString("o")
        }, MarkerKey);

        // Smv.Settings stays the public subset; the protected backup is retained.
        _log.LogWarning("smv.migration.rolledback store={StoreId}", marker.TargetStoreId);

        return SmvMigrationResult.Ok("Rollback complete. Deployment is pending reconfiguration; after downgrade, re-enter credentials in the legacy plugin.");
    }

    private string? Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? plaintext : _protector.Protect(plaintext);
}

/// <summary>Server-global marker that makes migration idempotent.</summary>
public class SmvMigrationMarker
{
    public bool Migrated { get; set; }
    public string? TargetStoreId { get; set; }
    public string? Version { get; set; }
    public string? MigratedAtUtc { get; set; }
    public string? RolledBackAtUtc { get; set; }
}

/// <summary>
/// Temporary migration backup: the backend subset with sensitive fields protected
/// at rest, plus metadata. Sole rollback source; removed in a later release (F4).
/// </summary>
public class SmvMigrationBackup
{
    public string? TargetStoreId { get; set; }
    public string? Version { get; set; }
    public string? CreatedAtUtc { get; set; }

    public string? TapdBaseUrl { get; set; }
    public string? TapdMacaroonHexProtected { get; set; }
    public string? TapdTlsCert { get; set; }
    public int TapdHttpTimeoutMs { get; set; }

    public string? BitcoinRpcUrl { get; set; }
    public string? BitcoinRpcUser { get; set; }
    public string? BitcoinRpcPasswordProtected { get; set; }
}

public enum SmvMigrationOutcome { Ok, NoOp, Refused }

public sealed class SmvMigrationStatus
{
    public bool Migrated { get; init; }
    public string? TargetStoreId { get; init; }
    public string? MigratedAtUtc { get; init; }
    public bool LegacyBackendPresent { get; init; }
    public bool BackupPresent { get; init; }
}

public sealed class SmvMigrationResult
{
    public SmvMigrationOutcome Outcome { get; }
    public string Message { get; }

    private SmvMigrationResult(SmvMigrationOutcome outcome, string message)
    {
        Outcome = outcome;
        Message = message;
    }

    public static SmvMigrationResult Ok(string message) => new(SmvMigrationOutcome.Ok, message);
    public static SmvMigrationResult NoOp(string message) => new(SmvMigrationOutcome.NoOp, message);
    public static SmvMigrationResult Refused(string message) => new(SmvMigrationOutcome.Refused, message);
}
