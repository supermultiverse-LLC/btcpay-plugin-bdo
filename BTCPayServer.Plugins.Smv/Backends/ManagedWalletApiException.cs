using System;
using BTCPayServer.Plugins.Smv.Core;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// A non-2xx response from the Managed Wallet API v1.1, carrying the closed-set
/// error <see cref="Code"/> (contract §3.3) and, for <c>rate_limited</c>, the
/// <see cref="RetryAfterSeconds"/> hint. Full mapping of these to plugin outcomes
/// is RFC-PLUGIN-003 §12; H1a only needs the read-path failures to surface typed.
/// </summary>
public sealed class ManagedWalletApiException : Exception
{
    public ManagedWalletErrorCode Code { get; }
    public int HttpStatus { get; }
    public int? RetryAfterSeconds { get; }

    public ManagedWalletApiException(
        ManagedWalletErrorCode code,
        int httpStatus,
        string message,
        int? retryAfterSeconds = null)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
