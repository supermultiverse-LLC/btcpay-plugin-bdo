using System;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// PKCE (RFC 7636, method S256) + CSRF state generation for the OAuth Connect flow
/// (RFC-PLUGIN-007). Pure crypto with no host dependency so it is unit-testable in Core.
///
/// The plugin is a public client (no client secret, <c>token_endpoint_auth_method=none</c>),
/// so PKCE is what binds the authorization code to this exact <c>/connect</c> request:
/// the plugin holds the <c>code_verifier</c> server-side, sends only the
/// <c>code_challenge</c> to <c>/authorize</c>, and proves possession at <c>/token</c>.
/// </summary>
public static class PkceCodes
{
    /// <summary>A fresh high-entropy <c>code_verifier</c> — base64url(no-pad) of
    /// <paramref name="bytes"/> random bytes. 32 bytes → 43 chars, within the RFC's
    /// 43–128 range and the unreserved-character set.</summary>
    public static string NewCodeVerifier(int bytes = 32) => Base64Url(RandomBytes(bytes));

    /// <summary>The S256 <c>code_challenge</c> = base64url(no-pad) of SHA-256 over the
    /// ASCII <paramref name="codeVerifier"/> (RFC 7636 §4.2).</summary>
    public static string Challenge(string codeVerifier)
    {
        if (string.IsNullOrEmpty(codeVerifier))
            throw new ArgumentException("code_verifier is required", nameof(codeVerifier));
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>A random opaque token for the CSRF <c>state</c> / nonce. The controller
    /// binds it to the Store + <c>code_verifier</c> across <c>/connect</c> → <c>/callback</c>.</summary>
    public static string NewStateToken(int bytes = 32) => Base64Url(RandomBytes(bytes));

    private static byte[] RandomBytes(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        var buffer = new byte[n];
        RandomNumberGenerator.Fill(buffer);
        return buffer;
    }

    // base64url without padding (RFC 7636 §A: '+'→'-', '/'→'_', strip '=').
    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
