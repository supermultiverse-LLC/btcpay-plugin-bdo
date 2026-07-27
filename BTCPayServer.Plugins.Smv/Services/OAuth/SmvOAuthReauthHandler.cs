using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Smv.Services.OAuth;

/// <summary>
/// Reactive OAuth re-auth for the Managed Wallet API (RFC-PLUGIN-007 §11.3: refresh
/// near expiry "or a call returns 401"). The proactive path in
/// <see cref="SmvOAuthTokenService.EnsureFreshTokenAsync"/> cannot see a mid-lifetime
/// server-side revocation (exchange rotation, sweeper), so this handler catches the
/// resulting 401, forces one refresh + re-exchange, and retries the request ONCE with
/// the fresh bearer. If the refresh fails (grant revoked / disconnected) the original
/// 401 is surfaced and the UI offers Reconnect.
///
/// Request bodies are buffered before the first send so the single retry can resend
/// them byte-for-byte — safe for the API's state-changing endpoints because they all
/// require an Idempotency-Key (same key + body replays, never duplicates).
/// Attached only for OAuth-connected Stores; manual-token Stores have nothing to refresh.
/// </summary>
internal sealed class SmvOAuthReauthHandler : DelegatingHandler
{
    private readonly string _storeId;
    private readonly SmvOAuthTokenService _tokens;

    public SmvOAuthReauthHandler(string storeId, SmvOAuthTokenService tokens, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _storeId = storeId;
        _tokens = tokens;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer the body up front — after a send the content stream is not rewindable.
        byte[]? bufferedBody = null;
        if (request.Content is not null)
            bufferedBody = await request.Content.ReadAsByteArrayAsync(ct);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        var rejected = request.Headers.Authorization?.Parameter;
        var fresh = await _tokens.ForceRefreshAsync(_storeId, rejected, ct);
        if (string.IsNullOrWhiteSpace(fresh) || string.Equals(fresh, rejected, StringComparison.Ordinal))
            return response;   // nothing better to offer — surface the 401 (→ Reconnect UX)

        response.Dispose();

        var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        if (bufferedBody is not null)
        {
            var content = new ByteArrayContent(bufferedBody);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            retry.Content = content;
        }

        return await base.SendAsync(retry, ct);
    }
}
