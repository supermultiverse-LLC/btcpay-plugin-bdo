using BTCPayServer.Plugins.Smv.Services.Tapd;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// P1-only adapters: map backend-neutral DTOs back to the existing
/// Services.Tapd view models so the current Razor views bind unchanged
/// (guarantees zero visual change). When the views are later rebound to the
/// neutral DTOs directly, this class is deleted.
/// </summary>
public static class BackendViewAdapters
{
    public static TapdAsset ToTapdAsset(OwnedAsset a) => new()
    {
        AssetId = a.AssetId,
        Name = a.Name,
        Type = a.Type,
        Amount = a.Amount,
        GenesisPoint = null,
        ImageUrl = a.ImageUrl,
        ImageIpfsUrl = a.ImageIpfsUrl,
        ImageIpfsCid = a.ImageIpfsCid,
        Description = a.Description,
        ExternalUrl = a.ExternalUrl,
        Attributes = a.Attributes,
        IsConfirming = a.IsConfirming,
        AnchorBlockHeight = a.AnchorBlockHeight
    };

    public static IReadOnlyList<TapdAsset> ToTapdAssets(IReadOnlyList<OwnedAsset> assets)
    {
        var list = new List<TapdAsset>(assets.Count);
        foreach (var a in assets) list.Add(ToTapdAsset(a));
        return list;
    }

    // Level 2 (RFC-PLUGIN-005 Phase 2): a HeldUnit reuses the certified _AssetRow /
    // Send / Info / wallet.js machinery. A BDO unit is always amount 1. AssetId may be
    // null pre-anchor — the caller filters those out (shown as "confirming" instead).
    public static TapdAsset ToTapdAsset(HeldUnit u) => new()
    {
        AssetId = u.AssetId,
        Name = u.Name,
        Type = null,
        Amount = "1",
        GenesisPoint = null,
        ImageUrl = u.ImageUrl
    };

    public static IReadOnlyList<TapdAsset> ToTapdAssets(IEnumerable<HeldUnit> units)
    {
        var list = new List<TapdAsset>();
        foreach (var u in units) list.Add(ToTapdAsset(u));
        return list;
    }

    public static TapdReceiveEvent ToTapdReceiveEvent(PendingIncomingAsset e) => new()
    {
        Encoded = e.Encoded,
        AssetId = e.AssetId,
        AssetType = e.AssetType,
        Amount = e.Amount,
        Status = e.Status,
        Outpoint = e.Outpoint,
        ConfirmationHeight = e.ConfirmationHeight,
        HasProof = e.HasProof,
        CreatedAtUnix = e.CreatedAtUnix
    };

    public static IReadOnlyList<TapdReceiveEvent> ToTapdReceiveEvents(IReadOnlyList<PendingIncomingAsset> events)
    {
        var list = new List<TapdReceiveEvent>(events.Count);
        foreach (var e in events) list.Add(ToTapdReceiveEvent(e));
        return list;
    }

    public static TapdReceiveAddress ToTapdReceiveAddress(ReceiveAddress a) => new()
    {
        Encoded = a.Encoded,
        AssetId = a.AssetId,
        AssetType = a.AssetType,
        Amount = a.Amount,
        ProofCourierAddr = a.ProofCourierAddr,
        AssetVersion = a.AssetVersion
    };
}
