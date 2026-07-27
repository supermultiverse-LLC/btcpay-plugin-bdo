using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Models;

/// <summary>
/// Mirrors the `decoded` payload returned by the Lovable Cloud
/// edge function `tapd-decode-proof`. Fields are nullable because
/// tapd schemas vary slightly across versions.
/// </summary>
public sealed class DecodedProofDto
{
    [JsonPropertyName("asset_type")]      public string? AssetType { get; set; } = "NORMAL";
    [JsonPropertyName("asset_name")]      public string? AssetName { get; set; }
    [JsonPropertyName("amount")]          public string? Amount { get; set; }
    [JsonPropertyName("genesis_point")]   public string? GenesisPoint { get; set; }
    [JsonPropertyName("anchor_tx")]       public string? AnchorTx { get; set; }
    [JsonPropertyName("anchor_outpoint")] public string? AnchorOutpoint { get; set; }
    [JsonPropertyName("block_height")]    public long? BlockHeight { get; set; }
    [JsonPropertyName("block_hash")]      public string? BlockHash { get; set; }
    [JsonPropertyName("script_key")]      public string? ScriptKey { get; set; }
    [JsonPropertyName("internal_key")]    public string? InternalKey { get; set; }
    [JsonPropertyName("meta_hash")]       public string? MetaHash { get; set; }
    [JsonPropertyName("meta_reveal")]     public object? MetaReveal { get; set; }
    [JsonPropertyName("proof_at_depth")]  public int? ProofAtDepth { get; set; }
    [JsonPropertyName("number_of_proofs")] public int? NumberOfProofs { get; set; }
}

public sealed class DecodeProofEnvelope
{
    [JsonPropertyName("ok")]      public bool Ok { get; set; }
    [JsonPropertyName("decoded")] public DecodedProofDto? Decoded { get; set; }
    [JsonPropertyName("raw")]     public System.Text.Json.JsonElement? Raw { get; set; }
    [JsonPropertyName("error")]   public string? Error { get; set; }
    [JsonPropertyName("upstream_status")] public int? UpstreamStatus { get; set; }
    [JsonPropertyName("detail")]  public string? Detail { get; set; }
}
