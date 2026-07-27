using System;

namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// Recognises Taproot Assets address prefixes (the bech32m human-readable part).
/// Used to reject obviously-wrong send destinations before hitting a backend.
/// Lives in Core so the accepted-network set is host-independent and unit-testable.
///
/// P3 note: BYON Send historically accepted only <c>taprt1</c> (regtest). Hosted
/// Send moves real mainnet assets, whose addresses are <c>tapbc1</c> — so the set
/// is widened to the standard networks. This only ever WIDENS acceptance; it never
/// rejects an address BYON accepted before.
/// </summary>
public static class TaprootAssetAddress
{
    // Taproot Assets bech32m HRPs by network:
    //   mainnet  -> tapbc1
    //   testnet  -> taptb1
    //   regtest  -> taprt1
    private static readonly string[] ValidPrefixes = { "tapbc1", "taptb1", "taprt1" };

    /// <summary>
    /// True when <paramref name="address"/> starts with a known Taproot Assets HRP
    /// (case-insensitive). This is a cheap shape check, not full bech32m validation —
    /// the backend is the authority on whether the address actually decodes.
    /// </summary>
    public static bool HasValidPrefix(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        foreach (var prefix in ValidPrefixes)
        {
            if (address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
