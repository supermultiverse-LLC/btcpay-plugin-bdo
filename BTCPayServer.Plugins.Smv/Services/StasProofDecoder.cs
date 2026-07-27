using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Smv.Models;

namespace BTCPayServer.Plugins.Smv.Services;

public enum DecodeErrorKind
{
    NotConfigured,
    ProofTooLarge,
    Network,
    Timeout,
    UpstreamHttp,
    UpstreamPayload
}

public sealed class DecodeResult
{
    public bool Ok { get; init; }
    public DecodeErrorKind? ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
    public int? UpstreamStatus { get; init; }
    public DecodedProofDto? Decoded { get; init; }
    public string? RawJson { get; init; }
    public string? Raw => RawJson;

    public static DecodeResult Success(DecodedProofDto? decoded, string rawJson) =>
        new() { Ok = true, Decoded = decoded, RawJson = rawJson };

    public static DecodeResult Failure(DecodeErrorKind kind, string message, int? upstreamStatus = null, string? rawJson = null) =>
        new() { Ok = false, ErrorKind = kind, ErrorMessage = message, UpstreamStatus = upstreamStatus, RawJson = rawJson };
}

public sealed class StasProofDecoder
{
    public const int MaxProofBytes = 2 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsRepositoryAccessor _settings;

    public StasProofDecoder(
        IHttpClientFactory httpClientFactory,
        ISettingsRepositoryAccessor settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public async Task<DecodeResult> DecodeAsync(byte[] proofBytes, bool withMetaReveal, CancellationToken ct)
    {
        if (proofBytes.Length > MaxProofBytes)
        {
            return DecodeResult.Failure(
                DecodeErrorKind.ProofTooLarge,
                $"Proof is too large. Max allowed size is {MaxProofBytes} bytes.");
        }

        var settings = await _settings.GetAsync();
        var endpoint = settings.StasProofDecodeEndpoint?.Trim();

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return DecodeResult.Failure(
                DecodeErrorKind.NotConfigured,
                "STAS Proof Decode Endpoint is not configured.");
        }

        var client = _httpClientFactory.CreateClient("smv-decode");
        client.Timeout = TimeSpan.FromSeconds(20);

        var payload = JsonSerializer.Serialize(new
        {
            raw_proof = Convert.ToBase64String(proofBytes),
            with_meta_reveal = withMetaReveal
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try
        {
            response = await client.PostAsync(endpoint, content, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return DecodeResult.Failure(DecodeErrorKind.Timeout, ex.Message);
        }
        catch (Exception ex)
        {
            return DecodeResult.Failure(DecodeErrorKind.Network, ex.Message);
        }

        var rawJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return DecodeResult.Failure(
                DecodeErrorKind.UpstreamHttp,
                $"Decode endpoint returned HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode,
                rawJson);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<DecodeProofEnvelope>(
                rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (envelope is null)
            {
                return DecodeResult.Failure(
                    DecodeErrorKind.UpstreamPayload,
                    "Decode endpoint returned an empty response.",
                    null,
                    rawJson);
            }

            if (!string.IsNullOrWhiteSpace(envelope.Error))
            {
                return DecodeResult.Failure(
                    DecodeErrorKind.UpstreamHttp,
                    envelope.Detail ?? envelope.Error,
                    envelope.UpstreamStatus,
                    rawJson);
            }

            return DecodeResult.Success(envelope.Decoded, rawJson);
        }
        catch (Exception ex)
        {
            return DecodeResult.Failure(
                DecodeErrorKind.UpstreamPayload,
                ex.Message,
                null,
                rawJson);
        }
    }
}