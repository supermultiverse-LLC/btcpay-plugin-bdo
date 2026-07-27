using System;
using System.Security.Cryptography;

namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// Pure SHA-256 + size computation over a raw .proof blob.
/// Host-independent: safe to unit test without BTCPayServer.
/// </summary>
public sealed class ProofIntegrityService : IProofIntegrityService
{
    public ProofIntegrityResult Compute(byte[] proofBytes)
    {
        if (proofBytes is null) throw new ArgumentNullException(nameof(proofBytes));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(proofBytes, hash);
        return new ProofIntegrityResult(Convert.ToHexString(hash).ToLowerInvariant(), proofBytes.Length);
    }
}
