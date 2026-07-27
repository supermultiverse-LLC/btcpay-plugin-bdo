namespace BTCPayServer.Plugins.Smv.Services;

public enum SmvApiErrorKind
{
    InvalidId,
    NotVerifiable,
    RateLimited,
    ProofCorrupted,
    ProofUnavailable,
    Timeout,
    Upstream,
    ProofTooLarge
}

public class SmvApiException : Exception
{
    public SmvApiErrorKind Kind { get; }
    public int? RetryAfterSeconds { get; }
    public int? HttpStatus { get; }

    public SmvApiException(SmvApiErrorKind kind, string message, int? httpStatus = null, int? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        HttpStatus = httpStatus;
        RetryAfterSeconds = retryAfter;
    }
}
