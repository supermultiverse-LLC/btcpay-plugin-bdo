namespace BTCPayServer.Plugins.Smv.Core;

public interface IProofIntegrityService
{
    ProofIntegrityResult Compute(byte[] proofBytes);
}

public readonly record struct ProofIntegrityResult(string Sha256Hex, int SizeBytes);
