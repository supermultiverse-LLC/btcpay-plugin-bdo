using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Server-admin surface for the one-shot P2 migration and its structural rollback
/// (TD §6.4, F4). Server-level authorization (<see cref="Policies.CanModifyServerSettings"/>)
/// gates the whole controller; migration additionally requires validated authority
/// over the explicitly selected target Store (<see cref="Policies.CanModifyStoreSettings"/>).
/// Ordinary Store settings pages remain governed solely by the Store-scoped policies.
/// </summary>
[Route("plugins/smv/admin")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvMigrationController : Controller
{
    private readonly SmvMigration _migration;
    private readonly StoreRepository _storeRepository;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<SmvMigrationController> _log;

    public SmvMigrationController(
        SmvMigration migration,
        StoreRepository storeRepository,
        IAuthorizationService authorizationService,
        ILogger<SmvMigrationController> log)
    {
        _migration = migration;
        _storeRepository = storeRepository;
        _authorizationService = authorizationService;
        _log = log;
    }

    [HttpGet("migration")]
    public async Task<IActionResult> Index()
    {
        return View(await BuildViewModelAsync());
    }

    [HttpPost("migration/migrate")]
    public async Task<IActionResult> Migrate(string? targetStoreId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetStoreId))
        {
            TempData["MigrationOutcome"] = nameof(SmvMigrationOutcome.Refused);
            TempData["MigrationMessage"] = "Select a target Store.";
            return RedirectToAction(nameof(Index));
        }

        // D14/D10: the actor must have validated authority over the SELECTED Store,
        // in addition to the server-level authorization on the controller.
        var storeAuth = await _authorizationService.AuthorizeAsync(User, targetStoreId, Policies.CanModifyStoreSettings);
        if (!storeAuth.Succeeded)
        {
            _log.LogWarning("smv.migration.denied_target_authority store={StoreId}", targetStoreId);
            TempData["MigrationOutcome"] = nameof(SmvMigrationOutcome.Refused);
            TempData["MigrationMessage"] = "You do not have authority over the selected Store.";
            return RedirectToAction(nameof(Index));
        }

        var result = await _migration.MigrateAsync(targetStoreId, cancellationToken);
        TempData["MigrationOutcome"] = result.Outcome.ToString();
        TempData["MigrationMessage"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("migration/rollback")]
    public async Task<IActionResult> Rollback(CancellationToken cancellationToken)
    {
        var result = await _migration.RollbackAsync(cancellationToken);
        TempData["MigrationOutcome"] = result.Outcome.ToString();
        TempData["MigrationMessage"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    private async Task<SmvMigrationPageModel> BuildViewModelAsync()
    {
        var status = await _migration.GetStatusAsync();
        var stores = await _storeRepository.GetStores();

        return new SmvMigrationPageModel
        {
            Status = status,
            Stores = stores
                .OrderBy(s => s.StoreName)
                .Select(s => new SmvMigrationPageModel.StoreOption(s.Id, s.StoreName))
                .ToList()
        };
    }
}

public sealed class SmvMigrationPageModel
{
    public required SmvMigrationStatus Status { get; init; }
    public required IReadOnlyList<StoreOption> Stores { get; init; }

    public sealed record StoreOption(string Id, string Name);
}
