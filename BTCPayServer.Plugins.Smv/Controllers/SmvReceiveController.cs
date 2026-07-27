using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Services.Tapd;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("stores/{storeId}/plugins/smv/receive")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvReceiveController : Controller
{
    private readonly IAssetBackendResolver _backends;
    private readonly Services.ISmvStoreSettingsProvider _storeSettings;

    public SmvReceiveController(IAssetBackendResolver backends, Services.ISmvStoreSettingsProvider storeSettings)
    {
        _backends = backends;
        _storeSettings = storeSettings;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        var vm = new SmvReceiveVm();

        // Gate at the door (gating matrix): Hosted without an account has no wallet to
        // receive into; Hosted connected but with assets:receive known-denied gets the
        // capability explanation instead of a form that would fail at the end.
        var settings = await _storeSettings.GetAsync(store.Id, cancellationToken);
        vm.IsHosted = settings?.IsHosted == true;
        if (settings?.IsHostedNotConnected == true)
        {
            vm.AccountGate = true;
            return View(vm);
        }
        if (settings?.IsHosted == true && settings.HasGrantedScope("assets:receive") == false)
        {
            vm.Error = "Your connection doesn't include receiving — reconnect your account in Settings to update its permissions.";
            return View(vm);
        }

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        if (backend is null)
        {
            vm.Error = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            return View(vm);
        }

        try
        {
            var assets = await backend.ListAssetsAsync(cancellationToken);

            vm.Assets = BackendViewAdapters.ToTapdAssets(assets);
            vm.TapdBaseUrl = backend.ConnectionLabel;
        }
        catch (ManagedWalletApiException ex) when (ex.HttpStatus == 401)
        {
            vm.Error = "Your Supermultiverse connection is no longer valid. Reconnect your account in Settings.";
        }
        catch (Exception ex)
        {
            vm.Error = $"Cannot reach the wallet backend: {ex.Message}";
        }

        return View(vm);
    }

    [HttpPost("")]
    public async Task<IActionResult> Generate(
        string assetId,
        string amount,
        CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        var vm = new SmvReceiveVm
        {
            AssetId = assetId?.Trim() ?? "",
            Amount = string.IsNullOrWhiteSpace(amount) ? "1" : amount.Trim()
        };
        vm.IsHosted = (await _storeSettings.GetAsync(store.Id, cancellationToken))?.IsHosted == true;

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        if (backend is null)
        {
            vm.Error = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            return View("Index", vm);
        }

        try
        {
            vm.Assets = BackendViewAdapters.ToTapdAssets(await backend.ListAssetsAsync(cancellationToken));
            vm.TapdBaseUrl = backend.ConnectionLabel;
            vm.ReceiveAddress = BackendViewAdapters.ToTapdReceiveAddress(
                await backend.CreateReceiveAddressAsync(
                    new ReceiveRequest(vm.AssetId, null, vm.Amount),
                    cancellationToken));
        }
        catch (Exception ex)
        {
            vm.Error = $"Cannot generate receive address: {ex.Message}";
        }

        return View("Index", vm);
    }
}

public class SmvReceiveVm
{
    public IReadOnlyList<TapdAsset> Assets { get; set; } = Array.Empty<TapdAsset>();

    public string? TapdBaseUrl { get; set; }

    public string AssetId { get; set; } = "";

    public string Amount { get; set; } = "1";

    public TapdReceiveAddress? ReceiveAddress { get; set; }

    public string? Error { get; set; }

    /// <summary>Gating matrix: Hosted without an account — render the sign-in door
    /// instead of the receive form (the account IS the wallet).</summary>
    public bool AccountGate { get; set; }

    /// <summary>Hosted mode — enables the claim-code redemption card (platform
    /// accounting receive; meaningless in BYON).</summary>
    public bool IsHosted { get; set; }
}