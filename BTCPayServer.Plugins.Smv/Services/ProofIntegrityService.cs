using System.Security.Cryptography;

namespace BTCPayServer.Plugins.Smv.Services;

public static class ProofIntegrityService
{
    public static string Sha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Matches(string declared, string computed)
        => !string.IsNullOrEmpty(declared) &&
           string.Equals(declared.Trim().ToLowerInvariant(), computed, StringComparison.Ordinal);
}
