using System;
using System.Linq;
using BTCPayServer.Plugins.Smv.Backends;

namespace BTCPayServer.Plugins.Smv.Services.Tapd;

public sealed class TapdAsset
{
    public string? AssetId { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Amount { get; set; }
    public string? GenesisPoint { get; set; }
    // Lowercase hex. Needed with the asset_id to export the asset's proof
    // (BYON register, RFC-PLUGIN-006 P2-2c). Null when tapd omits it.
    public string? ScriptKey { get; set; }
    // Populated for Hosted assets via Public API enrichment; null for BYON.
    public string? ImageUrl { get; set; }

    // Image permanence (IPFS), enriched from the Public API; null when the asset
    // has no pinned image or the API omits it.
    public string? ImageIpfsUrl { get; set; }
    public string? ImageIpfsCid { get; set; }

    // STAS-01 metadata decoded from the asset itself (BYON local enrichment,
    // RFC-PLUGIN-006): description, external link and trait/value attributes.
    public string? Description { get; set; }
    public string? ExternalUrl { get; set; }
    public IReadOnlyList<AssetAttribute>? Attributes { get; set; }

    // True while the mint's anchor tx sits in the mempool (all-zeros anchor block
    // hash). The row shows a "Confirming on Bitcoin…" badge and Send stays
    // disabled — the proof doesn't exist until the block lands.
    public bool IsConfirming { get; set; }

    // Anchor confirmation height — the listing's recency key (newest first).
    // 0 while confirming, and always 0 on tapd 0.3.x (field never populated).
    public long AnchorBlockHeight { get; set; }
}

public sealed class TapdReceiveEvent
{
    public string? Encoded { get; set; }
    public string? AssetId { get; set; }
    public string? AssetType { get; set; }
    public string? Amount { get; set; }
    public string? Status { get; set; }
    public string? Outpoint { get; set; }
    public string? ConfirmationHeight { get; set; }
    public bool HasProof { get; set; }
    public string? CreatedAtUnix { get; set; }

    public bool IsPendingIncoming
    {
        get
        {
            var status = Status ?? string.Empty;

            return status.Contains("TRANSACTION_DETECTED", StringComparison.OrdinalIgnoreCase)
                   || !HasProof
                   || string.Equals(ConfirmationHeight, "0", StringComparison.OrdinalIgnoreCase);
        }
    }
}

// One collection the merchant holds BDOs in (My BDOs Level 1, RFC-PLUGIN-005).
// HeldCount is possession (what the merchant holds now — reflects sends); TotalSupply
// is the collection's size (null when the backend can't resolve it, e.g. BYON).
public sealed class BdoCollectionGroup
{
    public string? Name { get; init; }
    public string? Slug { get; init; }
    public string? CoverImageUrl { get; init; }
    public long? TotalSupply { get; init; }
    public IReadOnlyList<TapdAsset> Items { get; init; } = Array.Empty<TapdAsset>();
    public int HeldCount => Items.Count;
}

public sealed class SmvMyAssetsViewModel
{
    // All holdings (used for the empty-state check and totals).
    public IReadOnlyList<TapdAsset> Assets { get; init; } = Array.Empty<TapdAsset>();
    public IReadOnlyList<TapdReceiveEvent> PendingIncoming { get; init; } = Array.Empty<TapdReceiveEvent>();

    // Collection-first grouping (RFC-PLUGIN-005 Phase 1). Collections holds the BDOs
    // grouped by collection; Unsorted holds BDOs with no resolvable collection;
    // Editions holds fungible NORMAL holdings, which are NOT BDOs and are shown
    // segregated (never mixed in as Bitcoin Digital Objects).
    public IReadOnlyList<BdoCollectionGroup> Collections { get; init; } = Array.Empty<BdoCollectionGroup>();
    public IReadOnlyList<TapdAsset> Unsorted { get; init; } = Array.Empty<TapdAsset>();
    public IReadOnlyList<TapdAsset> Editions { get; init; } = Array.Empty<TapdAsset>();

    // Phase 2 (Hosted): Level 1 from the holdings-collections endpoint. When true the
    // view renders collection cards that link to the Level 2 detail; when false it
    // renders the Phase 1 client-side groups above (BYON, or a Hosted fallback).
    public bool UseHostedCollections { get; init; }
    public IReadOnlyList<HeldCollection> HostedCollections { get; init; } = Array.Empty<HeldCollection>();

    // Split for the view (LINQ lives here in C#, not in Razor — the host's EF Core
    // view imports make `.Where` on the view ambiguous). BDO collections drill into
    // Level 2; fungible editions are segregated (a balance, not a collection).
    private static bool IsEdition(HeldCollection c) =>
        string.Equals(c.Modality, "fungible_edition", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<HeldCollection> BdoCollections => HostedCollections.Where(c => !IsEdition(c)).ToList();
    public IReadOnlyList<HeldCollection> EditionCollections => HostedCollections.Where(IsEdition).ToList();

    // True when there is nothing to show, across either rendering mode.
    public bool IsEmpty => UseHostedCollections
        ? HostedCollections.Count == 0
        : Assets.Count == 0;
}

// My BDOs Level 2 (RFC-PLUGIN-005 Phase 2): a single collection's held units,
// cursor-paginated + searchable. Hosted-only.
public sealed class SmvCollectionDetailViewModel
{
    public string CollectionId { get; set; } = "";
    public string? Name { get; set; }
    public string? CoverImageUrl { get; set; }
    public long OwnedCount { get; set; }
    public long CollectionSize { get; set; }

    public IReadOnlyList<HeldUnit> Units { get; set; } = Array.Empty<HeldUnit>();

    // Level 2 rows enriched via the Public API (description, attributes, IPFS,
    // external link) — the holdings-units endpoint is deliberately minimal, so
    // without this pass the Info panel renders empty (Toni's 2026-07-26 find).
    // Null → the view falls back to the bare adapter rows.
    public IReadOnlyList<TapdAsset>? EnrichedRows { get; set; }
    public string? NextCursor { get; set; }   // opaque; drives the "Next" page link
    public string? Query { get; set; }        // current search term (q)
    public string? Error { get; set; }

    // Units with a tapd asset_id are sendable/renderable now; those without are in the
    // pre-anchor window (batch M3) → shown as a "confirming" note. LINQ in C#, not Razor.
    public IReadOnlyList<HeldUnit> ReadyUnits =>
        Units.Where(u => !string.IsNullOrWhiteSpace(u.AssetId)).ToList();
    public int PendingCount =>
        Units.Count(u => string.IsNullOrWhiteSpace(u.AssetId));
}
