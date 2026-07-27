using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// <see cref="ISmvCredentialProtector"/> backed by ASP.NET Core Data Protection.
///
/// The key ring lives under the host's <c>DataDir</c> (part of the backup
/// contract, RFC §14.1.3). The purpose string is stable: changing it would make
/// every previously protected payload undecryptable.
///
/// The safe event carries the failure <see cref="UnprotectFailureCategory"/> and
/// no secrets. The Store dimension is supplied by the caller via a logging scope
/// (see <c>SmvStoreSettingsProvider</c>), keeping this component storeId-agnostic.
/// </summary>
public sealed class SmvCredentialProtector : ISmvCredentialProtector
{
    // Stable protector purpose. NEVER change without a migration of protected data.
    private const string Purpose = "BTCPayServer.Plugins.Smv.Credentials.v1";

    private readonly IDataProtector _protector;
    private readonly ILogger<SmvCredentialProtector> _logger;

    public SmvCredentialProtector(IDataProtectionProvider dataProtectionProvider, ILogger<SmvCredentialProtector> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Protect(plaintext);
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = string.Empty;

        if (string.IsNullOrEmpty(protectedValue))
        {
            EmitFailure(UnprotectFailureCategory.PayloadMalformed);
            return false;
        }

        try
        {
            plaintext = _protector.Unprotect(protectedValue);
            return true;
        }
        catch (Exception ex)
        {
            plaintext = string.Empty;
            EmitFailure(Classify(ex));
            return false;
        }
    }

    // One safe event per failed attempt. Category only; no ciphertext, no plaintext.
    // The Store id (when known) is attached by the caller's logging scope.
    private void EmitFailure(UnprotectFailureCategory category)
        => _logger.LogWarning("SMV credential unprotect failed (category: {Category}).", category);

    private static UnprotectFailureCategory Classify(Exception ex) => ex switch
    {
        CryptographicException ce when
            ce.Message.Contains("key ring", StringComparison.OrdinalIgnoreCase) ||
            ce.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase)
            => UnprotectFailureCategory.KeyRingUnavailable,
        CryptographicException => UnprotectFailureCategory.PayloadMalformed,
        FormatException => UnprotectFailureCategory.PayloadMalformed,
        _ => UnprotectFailureCategory.Unexpected
    };
}

/// <summary>Non-sensitive classification of an unprotect failure (E17 / test 19).</summary>
public enum UnprotectFailureCategory
{
    KeyRingUnavailable,
    PayloadMalformed,
    Unexpected
}
