using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Smv.Settings;

namespace BTCPayServer.Plugins.Smv.Services;

public class SmvSettingsAccessor : ISettingsRepositoryAccessor
{
    private const string SettingsKey = "Smv.Settings";

    private readonly ISettingsRepository _repo;

    public SmvSettingsAccessor(ISettingsRepository repo)
    {
        _repo = repo;
    }

    // Reads the server-global public subset. The legacy full record deserializes
    // into SmvServerSettings unchanged (Newtonsoft ignores the backend fields), so
    // Verify works before and after the migration reduces the record (F1/F4).
    public async Task<SmvServerSettings> GetAsync()
        => (await _repo.GetSettingAsync<SmvServerSettings>(SettingsKey)) ?? new SmvServerSettings();
}