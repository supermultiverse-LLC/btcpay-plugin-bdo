using System.Text.Json;
using BTCPayServer.Plugins.Smv.Models;
using BTCPayServer.Plugins.Smv.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Services;

public class SmvPublicApiClient
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ISettingsRepositoryAccessor _settings;
    private readonly ILogger<SmvPublicApiClient> _log;

    public SmvPublicApiClient(
        HttpClient http,
        IMemoryCache cache,
        ISettingsRepositoryAccessor settings,
        ILogger<SmvPublicApiClient> log)
    {
        _http = http;
        _cache = cache;
        _settings = settings;
        _log = log;
    }

    public async Task<CollectibleResponse> GetCollectibleAsync(string id, CancellationToken ct)
    {
        var s = await _settings.GetAsync();
        var cacheKey = $"smv:collectible:{id}";

        if (_cache.TryGetValue<CollectibleResponse>(cacheKey, out var cached) && cached is not null)
            return cached;

        var url = $"{s.SmvPublicApiBase.TrimEnd('/')}/collectible/{Uri.EscapeDataString(id)}";
        var json = await GetJsonWithRetryAsync(url, s, ct);

        var dto = JsonSerializer.Deserialize<CollectibleResponse>(json)
                  ?? throw new SmvApiException(SmvApiErrorKind.Upstream, "Empty response from API");

        // IPFS pinning converges minutes after a mint/registration. A response
        // without the CID is still converging — caching it for the full TTL
        // (default 24h) would hide the pin until tomorrow. Once pinned the
        // payload is immutable and safe to cache for the full TTL.
        var ttl = string.IsNullOrEmpty(dto.ImageIpfsCid)
            ? TimeSpan.FromSeconds(Math.Min(s.SmvCacheTtlSeconds, 300))
            : TimeSpan.FromSeconds(s.SmvCacheTtlSeconds);

        _cache.Set(cacheKey, dto, ttl);
        return dto;
    }

    public async Task<CollectionResponse> GetCollectionAsync(string slug, CancellationToken ct)
    {
        var s = await _settings.GetAsync();
        var cacheKey = $"smv:collection:{slug}";

        if (_cache.TryGetValue<CollectionResponse>(cacheKey, out var cached) && cached is not null)
            return cached;

        var url = $"{s.SmvPublicApiBase.TrimEnd('/')}/collection/{Uri.EscapeDataString(slug)}";
        var json = await GetJsonWithRetryAsync(url, s, ct);

        var dto = JsonSerializer.Deserialize<CollectionResponse>(json)
                  ?? throw new SmvApiException(SmvApiErrorKind.Upstream, "Empty response from API");

        _cache.Set(cacheKey, dto, TimeSpan.FromSeconds(s.SmvCacheTtlSeconds));
        return dto;
    }

    public async Task<(byte[] Bytes, string? ProofHashHeader)> GetProofRawAsync(string id, CancellationToken ct)
    {
        var s = await _settings.GetAsync();
        var url = $"{s.SmvPublicApiBase.TrimEnd('/')}/collectible/{Uri.EscapeDataString(id)}/proof.raw";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(s.SmvHttpTimeoutMs);

        HttpResponseMessage resp;

        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SmvApiException(SmvApiErrorKind.Timeout, "Proof download timed out");
        }

        using (resp)
        {
            await ThrowForKnownErrorsAsync(resp, ct);

            var max = s.SmvProofMaxBytes;

            if (resp.Content.Headers.ContentLength is long cl && cl > max)
            {
                throw new SmvApiException(
                    SmvApiErrorKind.ProofTooLarge,
                    $"Proof exceeds max allowed bytes ({cl} > {max})");
            }

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();

            var buf = new byte[16 * 1024];
            long total = 0;
            int read;

            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                total += read;

                if (total > max)
                {
                    throw new SmvApiException(
                        SmvApiErrorKind.ProofTooLarge,
                        $"Proof exceeds max allowed bytes (> {max})");
                }

                ms.Write(buf, 0, read);
            }

            string? proofHash = null;

            if (resp.Headers.TryGetValues("X-Proof-Hash", out var vals))
                proofHash = vals.FirstOrDefault();

            return (ms.ToArray(), proofHash);
        }
    }

    private async Task<string> GetJsonWithRetryAsync(string url, SmvServerSettings s, CancellationToken ct)
    {
        try
        {
            return await GetJsonOnceAsync(url, s, ct);
        }
        catch (SmvApiException ex) when (
            ex.Kind == SmvApiErrorKind.Timeout ||
            ex.Kind == SmvApiErrorKind.Upstream)
        {
            _log.LogWarning("SMV upstream transient failure, retry x1: {Kind}", ex.Kind);
            return await GetJsonOnceAsync(url, s, ct);
        }
    }

    private async Task<string> GetJsonOnceAsync(string url, SmvServerSettings s, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(s.SmvHttpTimeoutMs);

        HttpResponseMessage resp;

        try
        {
            resp = await _http.SendAsync(req, cts.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SmvApiException(SmvApiErrorKind.Timeout, "Upstream request timed out");
        }

        using (resp)
        {
            await ThrowForKnownErrorsAsync(resp, ct);
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    private static async Task ThrowForKnownErrorsAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        string body = "";

        try
        {
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // ignore body read errors
        }

        string? code = null;

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("error", out var e))
                code = e.GetString();
        }
        catch
        {
            // response body is not JSON
        }

        switch ((int)resp.StatusCode)
        {
            case 400:
                throw new SmvApiException(SmvApiErrorKind.InvalidId, code ?? "invalid_id", 400);

            case 404:
                throw new SmvApiException(SmvApiErrorKind.NotVerifiable, code ?? "asset_not_verifiable", 404);

            case 429:
                int? retry = null;

                if (resp.Headers.RetryAfter?.Delta is TimeSpan d)
                    retry = (int)d.TotalSeconds;

                throw new SmvApiException(SmvApiErrorKind.RateLimited, code ?? "rate_limited", 429, retry);

            case 502:
                if (code == "proof_corrupted")
                    throw new SmvApiException(SmvApiErrorKind.ProofCorrupted, "proof_corrupted", 502);

                throw new SmvApiException(SmvApiErrorKind.ProofUnavailable, code ?? "proof_unavailable", 502);

            default:
                throw new SmvApiException(
                    SmvApiErrorKind.Upstream,
                    $"Upstream {(int)resp.StatusCode}",
                    (int)resp.StatusCode);
        }
    }
}

public interface ISettingsRepositoryAccessor
{
    Task<SmvServerSettings> GetAsync();
}