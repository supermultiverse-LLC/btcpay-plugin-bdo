namespace BTCPayServer.Plugins.Smv.Services.Tapd;

public class TapdSendResult
{
    public string? TransferId { get; set; }

    public string? AnchorTxid { get; set; }

    public string? State { get; set; }

    public string? RawJson { get; set; }
}
