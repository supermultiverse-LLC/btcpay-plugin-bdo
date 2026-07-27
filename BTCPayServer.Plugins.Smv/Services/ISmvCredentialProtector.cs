namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// Protects and unprotects individual credential strings at rest.
///
/// Contract (TD §3.3, RFC §9.2/§9.3, E17):
/// <list type="bullet">
///   <item><see cref="TryUnprotect"/> never throws on a failed unprotect.</item>
///   <item>Neither ciphertext nor plaintext is ever logged.</item>
///   <item>Each failed unprotect emits exactly one safe event (Store + category),
///   with no secrets.</item>
/// </list>
/// </summary>
public interface ISmvCredentialProtector
{
    /// <summary>Protects a plaintext secret for storage and returns the protected payload.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Attempts to reverse <see cref="Protect"/>. Never throws: on any failure it
    /// returns <c>false</c>, sets <paramref name="plaintext"/> to <see cref="string.Empty"/>,
    /// and emits one safe event describing the failure category (no ciphertext/plaintext).
    /// </summary>
    bool TryUnprotect(string protectedValue, out string plaintext);
}
