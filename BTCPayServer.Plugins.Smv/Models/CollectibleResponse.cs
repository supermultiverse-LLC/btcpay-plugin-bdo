using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Models;

public class CollectibleResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }

    // Image permanence: the backend pins the image to IPFS (content-addressed) and
    // publishes the CID, a gateway URL, and the image's SHA-256. Surfaced on Verify so
    // a holder can confirm the image lives independently of any single server.
    [JsonPropertyName("image_ipfs_cid")] public string? ImageIpfsCid { get; set; }
    [JsonPropertyName("image_ipfs_url")] public string? ImageIpfsUrl { get; set; }
    [JsonPropertyName("image_sha256")] public string? ImageSha256 { get; set; }

    [JsonPropertyName("external_url")] public string? ExternalUrl { get; set; }
    [JsonPropertyName("attributes")] public List<AttributeKv>? Attributes { get; set; }
    [JsonPropertyName("collection")] public CollectionRef? Collection { get; set; }
    [JsonPropertyName("signature")] public SignatureBlock? Signature { get; set; }
    [JsonPropertyName("verification")] public VerificationBlock? Verification { get; set; }
}

public class AttributeKv
{
    [JsonPropertyName("trait_type")] public string? TraitType { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public class CollectionRef
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class SignatureBlock
{
    [JsonPropertyName("scheme")] public string? Scheme { get; set; }
    [JsonPropertyName("signer_pubkey")] public string? SignerPubkey { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
    [JsonPropertyName("metadata_hash")] public string? MetadataHash { get; set; }
}

public class VerificationBlock
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("tapd_asset_id")] public string? TapdAssetId { get; set; }
    [JsonPropertyName("anchor_outpoint")] public string? AnchorOutpoint { get; set; }
    [JsonPropertyName("proof_hash")] public string? ProofHash { get; set; }
    [JsonPropertyName("proof_size_bytes")] public long? ProofSizeBytes { get; set; }
    [JsonPropertyName("proof_format")] public string? ProofFormat { get; set; }
    [JsonPropertyName("proof_url")] public string? ProofUrl { get; set; }
}

public class CollectionResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("collection")]
    public CollectionInfo Collection { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("network")]
    public string? Network { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("count")]
    public int Count { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public List<CollectionItem> Items { get; set; } = new();
}

public class CollectionInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("issuer_name")]
    public string? IssuerName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("cover_image_url")]
    public string? CoverImageUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total_supply")]
    public int? TotalSupply { get; set; }
}

public class CollectionItem
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("rarity")]
    public string? Rarity { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("supply")]
    public int? Supply { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("minted_at")]
    public string? MintedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("verification")]
    public CollectionItemVerification? Verification { get; set; }
}

public class CollectionItemVerification
{
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string? Status { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("network")]
    public string? Network { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("proof_hash")]
    public string? ProofHash { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("anchor_outpoint")]
    public string? AnchorOutpoint { get; set; }
}