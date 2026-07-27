using System;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Thrown when a BYON (self-custody) Store invokes an issuance capability. Minting
/// from self-custody is plugin <b>Track B</b>, deferred out of v1.2 (RFC-PLUGIN-004
/// §3, contract §9). The Create surface is hidden for BYON Stores, so this is a
/// defence-in-depth guard: callers surface a typed "not available yet" message
/// rather than a raw 500. Distinct from <see cref="HostedFeatureNotAvailableException"/>
/// (a Hosted capability not yet shipped) so the two paths never blur.
/// </summary>
public sealed class SelfCustodyMintNotAvailableException : Exception
{
    public SelfCustodyMintNotAvailableException()
        : base("Minting from a self-custody (BYON) wallet is not available yet. "
               + "Issuance in this version is Hosted-only; self-custody minting arrives in a later release.")
    {
    }
}
