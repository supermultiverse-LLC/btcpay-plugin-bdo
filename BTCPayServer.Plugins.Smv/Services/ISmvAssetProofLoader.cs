using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// Loads the raw .proof bytes for a known SMV asset id. In v0.1 this is
/// implemented by `SmvPublicApiProofLoader` which calls the Public
/// Verification API `/proof.raw` endpoint already used by Download .proof.
/// </summary>
public interface ISmvAssetProofLoader
{
    Task<byte[]?> LoadProofAsync(string assetId, CancellationToken ct);
}
