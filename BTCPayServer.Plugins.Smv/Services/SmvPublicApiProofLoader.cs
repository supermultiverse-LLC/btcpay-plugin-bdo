namespace BTCPayServer.Plugins.Smv.Services;

public sealed class SmvPublicApiProofLoader : ISmvAssetProofLoader
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SmvPublicApiProofLoader(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<byte[]?> LoadProofAsync(string assetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return null;

        var client = _httpClientFactory.CreateClient("smv-public");

        var url = $"http://localhost:49392/plugins/smv/proof/{Uri.EscapeDataString(assetId)}";

        try
        {
            return await client.GetByteArrayAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }
}