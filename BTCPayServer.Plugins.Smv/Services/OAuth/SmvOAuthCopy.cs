using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Smv.Services.OAuth;

/// <summary>
/// Shared human copy for OAuth/account surfaces (RFC-007 §11.8 R3): capability names
/// and typed denial reasons render as merchant-readable text, never raw codes. Used by
/// both the SSO connect flow (SmvOAuthController) and the embedded account flow
/// (SmvAccountController).
/// </summary>
public static class SmvOAuthCopy
{
    public static readonly IReadOnlyDictionary<string, string> CapabilityLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["assets:read"] = "Read assets",
            ["assets:receive"] = "Receive assets",
            ["assets:send"] = "Send assets",
            ["assets:mint"] = "Mint BDOs",
            ["assets:register_external"] = "Register external BDOs",
        };

    public static readonly IReadOnlyDictionary<string, string> DenialReasonLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["requires_silver_or_higher"] = "requires Silver tier or higher",
            ["tier_below_silver"] = "requires Silver tier or higher",
            ["requires_gold_or_first_hosted_mint"] = "requires Gold tier, or your first hosted mint",
            ["tier_below_gold"] = "requires Gold tier",
            ["wallet_not_bound"] = "your account has no Supermultiverse wallet yet",
            ["no_wallet_bound"] = "your account has no Supermultiverse wallet yet",
            ["feature_disabled"] = "temporarily disabled by the platform",
            ["unknown_capability"] = "not recognized by the platform",
        };

    public static string DescribeDenied(IReadOnlyList<Mwv1Denied> denied)
        => string.Join(", ", denied.Select(d =>
        {
            var capability = CapabilityLabels.TryGetValue(d.Scope ?? "", out var c) ? c : d.Scope;
            if (string.IsNullOrWhiteSpace(d.Reason))
                return capability;
            var reason = DenialReasonLabels.TryGetValue(d.Reason, out var r) ? r : d.Reason;
            return $"{capability} ({reason})";
        }));

    /// <summary>The standard "connected" banner: account label + partial-grant detail.</summary>
    public static string ConnectedMessage(string? accountLabel, IReadOnlyList<Mwv1Denied> denied)
    {
        var msg = $"Connected to Supermultiverse{(string.IsNullOrWhiteSpace(accountLabel) ? "" : $" as {accountLabel}")}.";
        if (denied.Count > 0)
            msg += " Some capabilities weren't granted: " + DescribeDenied(denied) + ".";
        return msg;
    }
}
