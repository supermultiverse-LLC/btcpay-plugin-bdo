namespace BTCPayServer.Plugins.Smv.Core.Models;

public sealed class DecodedProofDto
{
    public string? AssetName { get; set; }
    public string? AssetId { get; set; }
    public string? AssetType { get; set; }
    public ulong? Amount { get; set; }
    public string? GenesisPoint { get; set; }
    public string? AnchorOutpoint { get; set; }
    public uint? BlockHeight { get; set; }
    public string? MetaHash { get; set; }
    public int? ProofDepth { get; set; }
    public bool? Valid { get; set; }
}
