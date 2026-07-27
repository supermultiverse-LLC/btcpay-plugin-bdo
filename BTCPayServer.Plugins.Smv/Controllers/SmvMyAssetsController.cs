using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Services.Tapd;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("stores/{storeId}/plugins/smv/my-assets")]
[Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvMyAssetsController : Controller
{
    private readonly IAssetBackendResolver _backends;
    private readonly Services.ByonRegistrationService _byonRegistration;
    private readonly Services.ISmvStoreSettingsProvider _storeSettings;
    private readonly Services.SmvPublicApiClient _publicApi;

    public SmvMyAssetsController(
        IAssetBackendResolver backends,
        Services.ByonRegistrationService byonRegistration,
        Services.ISmvStoreSettingsProvider storeSettings,
        Services.SmvPublicApiClient publicApi)
    {
        _backends = backends;
        _byonRegistration = byonRegistration;
        _storeSettings = storeSettings;
        _publicApi = publicApi;
    }

    // Level 2 (Collection detail) enrichment: the holdings-units endpoint is
    // deliberately minimal (anti fan-out at scale), so the Info panel fields
    // (description, attributes, IPFS, external link) come from the Public API
    // here — bounded to the page (≤48) with small concurrency, served from
    // SmvPublicApiClient's cache on repeat loads. Best-effort per row.
    private async Task<IReadOnlyList<Services.Tapd.TapdAsset>> EnrichUnitsAsync(
        IReadOnlyList<HeldUnit> units, CancellationToken ct)
    {
        var ready = units.Where(u => !string.IsNullOrWhiteSpace(u.AssetId)).ToList();
        var rows = new Services.Tapd.TapdAsset[ready.Count];
        using var gate = new System.Threading.SemaphoreSlim(4);
        var tasks = ready.Select(async (u, i) =>
        {
            var row = BackendViewAdapters.ToTapdAsset(u);
            await gate.WaitAsync(ct);
            try
            {
                var c = await _publicApi.GetCollectibleAsync(u.AssetId!, ct);
                row.ImageUrl = c.ImageUrl ?? row.ImageUrl;
                row.Description = c.Description;
                row.ExternalUrl = c.ExternalUrl;
                row.ImageIpfsCid = c.ImageIpfsCid;
                row.ImageIpfsUrl = c.ImageIpfsUrl;
                if (c.Attributes is { Count: > 0 })
                {
                    row.Attributes = c.Attributes
                        .Where(a => !string.IsNullOrWhiteSpace(a.TraitType))
                        .Select(a => new AssetAttribute(a.TraitType!, a.Value ?? ""))
                        .ToList();
                }
            }
            catch { /* best-effort — the bare row still renders */ }
            finally { gate.Release(); }
            rows[i] = row;
        }).ToList();
        await Task.WhenAll(tasks);
        return rows;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Bind the authorized Store from the framework (the policy already
        // validated it). storeId is never read from body/query.
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // Gate at the door (gating matrix): in Hosted mode the account IS the wallet —
        // without a sign-in there is nothing to list, so offer the door, not an error.
        var settings = await _storeSettings.GetAsync(store.Id, cancellationToken);
        if (settings?.IsHostedNotConnected == true)
        {
            ViewData["AccountGate"] = true;
            return View(new SmvMyAssetsViewModel());
        }

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);

        if (backend is null)
        {
            ViewData["Error"] = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            return View(new SmvMyAssetsViewModel());
        }

        ViewData["IsCustodial"] = backend.IsCustodial;
        // Send capability for the row buttons: known-denied → disabled with a tooltip;
        // unknown (manual token) or BYON → enabled as today.
        ViewData["CanSend"] = !backend.IsCustodial || settings?.HasGrantedScope("assets:send") != false;

        // B4: any My BDOs load drives queued BYON registrations to completion once
        // their anchor confirmed — heals mints whose create page was navigated away.
        if (backend is TapdAssetBackend sweepTapd)
        {
            try { await _byonRegistration.TryCompletePendingAsync(store.Id, sweepTapd, cancellationToken); }
            catch { /* best-effort */ }
        }

        try
        {
            var pendingIncoming = await backend.ListPendingIncomingAsync(cancellationToken);
            ViewData["TapdBaseUrl"] = backend.ConnectionLabel;

            // Phase 2 (Hosted): Level 1 from the scalable holdings-collections endpoint —
            // no per-asset enrichment fan-out. Falls back to the Phase 1 client-side
            // grouping if the endpoint isn't reachable, so the listing never hard-fails.
            if (backend.IsCustodial)
            {
                try
                {
                    var held = await backend.ListHeldCollectionsAsync(cancellationToken);
                    return View(new SmvMyAssetsViewModel
                    {
                        UseHostedCollections = true,
                        HostedCollections = held,
                        PendingIncoming = BackendViewAdapters.ToTapdReceiveEvents(pendingIncoming)
                    });
                }
                catch { /* fall through to the Phase 1 grouping below */ }
            }

            // BYON, or the Hosted fallback: Phase 1 client-side grouping.
            var assets = await backend.ListAssetsAsync(cancellationToken);
            IReadOnlyList<MintCollection> collections = Array.Empty<MintCollection>();
            if (backend.IsCustodial)
            {
                try { collections = await backend.ListCollectionsAsync(cancellationToken); }
                catch { /* best-effort — groups degrade to held-count only */ }
            }

            return View(BuildViewModel(assets, collections, pendingIncoming));
        }
        catch (ManagedWalletApiException ex) when (ex.HttpStatus == 401)
        {
            // The reactive re-auth handler already tried a refresh + retry (RFC-007
            // §11.3); a surviving 401 means the grant itself is gone — reconnect.
            ViewData["Error"] = "Your Supermultiverse connection is no longer valid.";
            ViewData["ReconnectNeeded"] = true;
            return View(new SmvMyAssetsViewModel());
        }
        catch (Exception ex)
        {
            ViewData["Error"] = $"Cannot reach the wallet backend: {ex.Message}";
            return View(new SmvMyAssetsViewModel());
        }
    }

    // My BDOs Level 2 (RFC-PLUGIN-005 Phase 2): a single collection's held units,
    // cursor-paginated + searchable. Hosted-only (BYON groups client-side on Index).
    [HttpGet("collection/{collectionId}")]
    public async Task<IActionResult> Collection(string collectionId, string? cursor, string? q, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null || !backend.IsCustodial)
            return NotFound();

        // The Level 2 rows reuse _SendPanel/_AssetRow, which read these from
        // ViewData — Index sets them but this action didn't, so the
        // claim-link section (IsCustodial-gated) never rendered here.
        var settings = await _storeSettings.GetAsync(store.Id, cancellationToken);
        ViewData["IsCustodial"] = true;
        ViewData["CanSend"] = settings?.HasGrantedScope("assets:send") != false;

        var vm = new SmvCollectionDetailViewModel { CollectionId = collectionId, Query = q };

        try
        {
            // Collection header (name / owned vs size / cover) from the Level 1 read.
            var cols = await backend.ListHeldCollectionsAsync(cancellationToken);
            var meta = cols.FirstOrDefault(c => string.Equals(c.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
            if (meta is null)
                return NotFound();
            vm.Name = meta.Name;
            vm.CoverImageUrl = meta.CoverImageUrl;
            vm.OwnedCount = meta.OwnedCount;
            vm.CollectionSize = meta.CollectionSize;

            var page = await backend.ListHeldUnitsAsync(
                collectionId, limit: 48, cursor: cursor,
                q: string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
                sort: "acquired_at_desc", cancellationToken);
            vm.Units = page.Items;
            vm.NextCursor = page.NextCursor;
            vm.EnrichedRows = await EnrichUnitsAsync(page.Items, cancellationToken);
        }
        catch (Exception ex)
        {
            vm.Error = $"Couldn't load this collection: {ex.Message}";
        }

        return View(vm);
    }

    // A fungible Taproot Asset (tapd NORMAL) is one shared identity split into N
    // interchangeable units — it is NOT a BDO (BDO-01 §3/§4). Segregate it from the
    // BDO listing. Hosted mints are always collectibles (Type null), so this only
    // ever matches BYON-held fungibles.
    private static bool IsFungible(string? type) =>
        string.Equals(type, "NORMAL", StringComparison.OrdinalIgnoreCase);

    // Collection-first grouping (RFC-PLUGIN-005 Phase 1). Groups BDOs by collection over
    // the existing holdings + collections endpoints — no backend change. A large batch
    // (Modality 3) needs Phase 2's collection-level fetch; here the flat holdings are
    // grouped client-side.
    private static SmvMyAssetsViewModel BuildViewModel(
        IReadOnlyList<OwnedAsset> assets,
        IReadOnlyList<MintCollection> collections,
        IReadOnlyList<PendingIncomingAsset> pendingIncoming)
    {
        // Recency order: confirming mints first (the merchant just made them),
        // then newest anchor first. Final tie-breaker is the REVERSED native
        // position: tapd lists in insertion (≈ chronological) order, so this
        // keeps newest-first even on nodes that never populate block_height
        // (tapd 0.3.x reports 0 for everything).
        assets = assets
            .Select((a, i) => (Asset: a, Index: i))
            .OrderByDescending(x => x.Asset.IsConfirming)
            .ThenByDescending(x => x.Asset.AnchorBlockHeight)
            .ThenByDescending(x => x.Index)
            .Select(x => x.Asset)
            .ToList();

        var bdos = assets.Where(a => !IsFungible(a.Type)).ToList();
        var editions = assets.Where(a => IsFungible(a.Type)).ToList();

        // Match a holding's collection to its metadata by slug first, then name.
        var bySlug = collections
            .Where(c => !string.IsNullOrWhiteSpace(c.Slug))
            .GroupBy(c => c.Slug!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = collections
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var groups = new List<BdoCollectionGroup>();
        var unsorted = new List<OwnedAsset>();

        foreach (var grp in bdos.GroupBy(a => a.CollectionSlug ?? a.Collection, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(grp.Key))
            {
                unsorted.AddRange(grp);
                continue;
            }

            var first = grp.First();
            MintCollection? meta = null;
            if (!string.IsNullOrWhiteSpace(first.CollectionSlug) && bySlug.TryGetValue(first.CollectionSlug!, out var m1))
                meta = m1;
            else if (!string.IsNullOrWhiteSpace(first.Collection) && byName.TryGetValue(first.Collection!, out var m2))
                meta = m2;

            groups.Add(new BdoCollectionGroup
            {
                Name = meta?.Name ?? first.Collection ?? first.CollectionSlug,
                Slug = meta?.Slug ?? first.CollectionSlug,
                CoverImageUrl = meta?.ImageUrl,
                TotalSupply = meta?.TotalSupply,
                Items = BackendViewAdapters.ToTapdAssets(grp.ToList())
            });
        }

        return new SmvMyAssetsViewModel
        {
            Assets = BackendViewAdapters.ToTapdAssets(assets),
            PendingIncoming = BackendViewAdapters.ToTapdReceiveEvents(pendingIncoming),
            Collections = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Unsorted = BackendViewAdapters.ToTapdAssets(unsorted),
            Editions = BackendViewAdapters.ToTapdAssets(editions)
        };
    }
}
