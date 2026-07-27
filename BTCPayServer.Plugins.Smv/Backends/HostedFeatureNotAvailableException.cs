using System;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Thrown when a Hosted Store invokes an <see cref="IAssetBackend"/> capability
/// not yet available in the current phase: Receive is deferred to Managed Wallet
/// API v1.2 (RFC-PLUGIN-003 §10); Send arrives in P3-H3. Callers surface a typed
/// "not available yet" message — never a raw 500.
/// </summary>
public sealed class HostedFeatureNotAvailableException : Exception
{
    public HostedFeatureNotAvailableException(string message) : base(message) { }
}
