using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Smv;

public class SmvPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.2.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        // P2 (C2): single entry inside the Store's Integrations menu (no server
        // header entry). Sub-navigation lives in _SmvPluginNav.cshtml.
        services.AddUIExtension("store-integrations-nav", "SmvNavExtension");

        // P2 (C4): server-admin entry for the one-shot migration surface.
        services.AddUIExtension("server-nav", "SmvServerNavExtension");

        services.AddMemoryCache();

        services.AddSingleton<ISettingsRepositoryAccessor, SmvSettingsAccessor>();

        // P2 (C1, inert): Store-scoped settings plumbing. New services only — no
        // consumer wiring yet, so plugin behaviour is unchanged.
        // Protector is stateless and holds a single DataProtection key ring: Singleton.
        // Provider depends on the request-scoped IStoreRepository: Scoped.
        services.AddSingleton<ISmvCredentialProtector, SmvCredentialProtector>();
        services.AddScoped<ISmvStoreSettingsProvider, SmvStoreSettingsProvider>();

        // P2 (C4): one-shot migration component. Scoped: depends on the Scoped
        // Store settings provider (IStoreRepository).
        services.AddScoped<SmvMigration>();

        // OAuth Connect (RFC-PLUGIN-007): keeps the per-Store mwv1_ fresh; consumed by
        // the resolver before it hands a token to a backend.
        services.AddScoped<Services.OAuth.SmvOAuthTokenService>();
        services.AddHttpClient("smv-oauth");

        // BYON registration (RFC-PLUGIN-006 P2-2c): pin + envelope + register-external-asset.
        services.AddScoped<Services.ByonRegistrationService>();

        // Asset ownership backend seam (P1): controllers depend on IAssetBackend
        // via the resolver instead of instantiating TapdClient directly.
        services.AddScoped<IAssetBackendResolver, SmvSettingsAssetBackendResolver>();

        services.AddHttpClient<SmvPublicApiClient>(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BTCPayServer.Plugins.Smv/0.1");
        });

        services.AddHttpClient("smv-decode");
        services.AddHttpClient("smv-public");

        services.AddSingleton<SmvCache>();
        services.AddSingleton<ISmvAssetProofLoader, SmvPublicApiProofLoader>();
        services.AddSingleton<StasProofDecoder>();
    }
}
