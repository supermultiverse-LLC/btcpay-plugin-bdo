namespace BTCPayServer.Plugins.Smv.Services.Tapd;

public class TapdReceiveAddress
{
    public string? Encoded { get; set; }

    public string? AssetId { get; set; }

    public string? AssetType { get; set; }

    public string? Amount { get; set; }

    public string? ProofCourierAddr { get; set; }

    public string? AssetVersion { get; set; }
}
