namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// Which backend a Store's SMV configuration targets (Plugin-P3).
///
/// <see cref="Byon"/> is <c>0</c> — the enum default — ON PURPOSE: a settings
/// record persisted before P3 (which has no backend-mode field) deserializes to
/// <see cref="Byon"/>, so every existing Store keeps self-custody behaviour with
/// no migration and never silently switches to the custodial Hosted backend.
/// Never renumber these values; <c>Byon</c> MUST stay 0.
///
/// This enum lives in Core (host-independent) so the zero-default invariant is
/// unit-testable without compiling the BTCPay host.
/// </summary>
public enum SmvBackendMode
{
    /// <summary>Bring Your Own Node: the merchant runs their own tapd (P1/P2). Default.</summary>
    Byon = 0,

    /// <summary>Supermultiverse-managed custodial wallet via the Managed Wallet API v1.1 (P3).</summary>
    Hosted = 1
}
