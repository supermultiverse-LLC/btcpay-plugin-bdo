using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Backends;

// Wire DTOs for the v1.2.1 additive holdings-by-collection endpoints
// (RFC-PLUGIN-005 Phase 2). These back the collection-first My BDOs listing:
// Level 1 = holdings-collections, Level 2 = holdings-units (cursor-paginated).
// Hosted-only — BYON keeps the Phase 1 client-side grouping.

/// <summary>GET /managed-wallet-holdings-collections — the merchant's held collections.</summary>
public sealed class ManagedHoldingsCollectionsResponse
{
    [JsonPropertyName("items")] public List<ManagedHoldingCollection> Items { get; set; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

public sealed class ManagedHoldingCollection
{
    [JsonPropertyName("collection_id")] public string? CollectionId { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("cover_image_url")] public string? CoverImageUrl { get; set; }

    // owned_count = possession (live SUM of active holdings); collection_size = total_supply.
    // ALWAYS distinct — never derive one from the other (contract §7 R3).
    [JsonPropertyName("owned_count")] public long OwnedCount { get; set; }
    [JsonPropertyName("collection_size")] public long CollectionSize { get; set; }

    [JsonPropertyName("modality")] public string? Modality { get; set; }   // unique_bdo | fungible_edition | unique_series
    [JsonPropertyName("group_key")] public string? GroupKey { get; set; }  // 66-hex, null until Modality 3
    [JsonPropertyName("issuer_name")] public string? IssuerName { get; set; }
}

/// <summary>GET /managed-wallet-holdings-units — a collection's held units, cursor-paginated.</summary>
public sealed class ManagedHoldingsUnitsResponse
{
    [JsonPropertyName("items")] public List<ManagedHoldingUnit> Items { get; set; } = new();
    [JsonPropertyName("next_cursor")] public string? NextCursor { get; set; }
}

public sealed class ManagedHoldingUnit
{
    [JsonPropertyName("id")] public string? Id { get; set; }              // asset uuid (smv_id) → /v1/public/collectible/:id
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }   // tapd 64-hex → Send; null in the pre-anchor window
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("batch_index")] public int? BatchIndex { get; set; } // position within its series; null when minted alone
    // RFC-PLUGIN-013: the series this unit came from, so a drop can be scoped to
    // one of them. Both null for a one-off mint — a real category, not a gap.
    [JsonPropertyName("series_id")] public string? SeriesId { get; set; }
    [JsonPropertyName("series_name")] public string? SeriesName { get; set; }
    [JsonPropertyName("acquired_at")] public string? AcquiredAt { get; set; }
}

/// <summary>A collection seen as GROUPS: one row per series, plus each BDO
/// minted alone as a group of one. Counts cover every unit held, not a page.</summary>
public sealed class ManagedHeldGroup
{
    // A series uuid, or "asset:{uuid}" for a BDO minted on its own.
    [JsonPropertyName("group_id")] public string? GroupId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("held")] public long Held { get; set; }
    /// <summary>The series' minted size. Held below it means some have gone out.</summary>
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
}

public sealed class ManagedHeldGroupsResponse
{
    [JsonPropertyName("groups")] public List<ManagedHeldGroup> Groups { get; set; } = new();
}
