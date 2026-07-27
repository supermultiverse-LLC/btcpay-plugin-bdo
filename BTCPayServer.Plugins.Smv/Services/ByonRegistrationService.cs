using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Core;
using BTCPayServer.Plugins.Smv.Services.OAuth;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// Orchestrates BYON registration (RFC-PLUGIN-006 P2-2c mitad B): pin the canonical
/// metadata → build the creator envelope → POST managed-wallet-register-external-asset,
/// authenticated with the Store's OAuth-obtained <c>mwv1_</c> (scope
/// <c>assets:register_external</c>, kept fresh by <see cref="SmvOAuthTokenService"/>).
///
/// The mint already happened on the merchant's node; this projects it into a full SMV BDO.
/// A failure here is non-fatal — the asset stays sovereign on the node and the caller shows
/// a note (RFC-006 D2). The register is idempotent by <c>external_asset_id_hex</c> and the
/// pin by content, so a retry is safe.
/// </summary>
public sealed class ByonRegistrationService
{
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly SmvOAuthTokenService _oauthTokens;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly ILogger<ByonRegistrationService> _log;

    public ByonRegistrationService(
        ISmvStoreSettingsProvider storeSettings,
        SmvOAuthTokenService oauthTokens,
        ISettingsRepositoryAccessor serverSettings,
        ILogger<ByonRegistrationService> log)
    {
        _storeSettings = storeSettings;
        _oauthTokens = oauthTokens;
        _serverSettings = serverSettings;
        _log = log;
    }

    public async Task<ByonRegistrationResult> RegisterAsync(ByonRegistrationInput input, CancellationToken ct = default)
    {
        var settings = await _storeSettings.GetAsync(input.StoreId, ct);
        if (settings is null)
            return ByonRegistrationResult.Fail("This Store isn't configured.");

        var token = await _oauthTokens.EnsureFreshTokenAsync(input.StoreId, settings, ct);
        if (string.IsNullOrWhiteSpace(token))
            return ByonRegistrationResult.Fail("Activate your BDO account in Settings to register this BDO.");

        var server = await _serverSettings.GetAsync();
        var http = ManagedWalletClient.CreateHttpClient(server.HostedApiBase, token, Math.Max(server.SmvHttpTimeoutMs, 20000));
        try
        {
            var client = new ExternalAssetRegistrationClient(http);

            // 1) Pin the EXACT canonical metadata bytes → metadata_uri (hash verified server-side).
            var pin = await client.PinMetadataAsync(new PinMetadataRequest
            {
                ContentBase64 = Convert.ToBase64String(input.CanonicalMetaBytes),
                MetadataHash = input.MetadataHash
            }, Guid.NewGuid().ToString(), ct);

            // 2) Creator envelope — the signed Nostr event, verbatim.
            var signer = StasEnvelope.CreatorSignerNostr(
                input.CreatorPubkeyHex, input.CreatorSig, input.CreatorEventId,
                input.CreatedAt, input.Kind, input.Tags, input.Content, input.MetadataHash);

            // 3) Register.
            var reg = new RegisterExternalAssetRequest
            {
                ExternalAssetIdHex = input.AssetId,
                Asset = new RegisterAssetInfo { Name = input.Name, Description = input.Description, ImageUrl = input.ImageUrl },
                Collection = new RegisterCollectionInfo { Name = input.CollectionName, Slug = input.CollectionSlug },
                MetadataHash = input.MetadataHash,
                MetadataUri = pin.MetadataUri ?? "",
                Envelope = new RegisterEnvelope { MetadataHash = input.MetadataHash, Signers = { signer } },
                ProofBlobBase64 = input.ProofBlobBase64,
                CanonicalMetadataBase64 = Convert.ToBase64String(input.CanonicalMetaBytes)
            };
            var resp = await client.RegisterAsync(reg, Guid.NewGuid().ToString(), ct);

            _log.LogInformation("byon_register.ok store={StoreId} asset_id={AssetId} smv_id={SmvId} already={Already}",
                input.StoreId, input.AssetId, resp.AssetId, resp.AlreadyRegistered);
            return ByonRegistrationResult.Ok(resp);
        }
        catch (RegisterExternalAssetException ex)
        {
            _log.LogWarning("byon_register.rejected store={StoreId} status={Status} code={Code} detail={Detail} message={Message}",
                input.StoreId, ex.StatusCode, ex.Code, ex.Detail, ex.Message);
            return ByonRegistrationResult.Fail(MapError(ex));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "byon_register.failed store={StoreId}", input.StoreId);
            return ByonRegistrationResult.Fail("Couldn't register the BDO with Supermultiverse. It's still on your node — retry later from My BDOs.");
        }
        finally
        {
            http.Dispose();
        }
    }

    // ── Platform media upload ───────────────────────────────────────────────────
    // Shared plumbing for the Create form's "upload from device": stream the bytes
    // to plugin-upload-image with the Store's mwv1_ (any connected mode — the
    // endpoint needs only assets:read) and get back the SMV-hosted https URL that
    // goes into the STAS-01 metadata. Content-addressed server-side, so re-uploads
    // of the same file are free and idempotent.
    public async Task<(bool Ok, string UrlOrMessage)> UploadImageAsync(
        string storeId, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        if (settings is null)
            return (false, "This Store isn't configured.");
        var token = await _oauthTokens.EnsureFreshTokenAsync(storeId, settings, ct);
        if (string.IsNullOrWhiteSpace(token))
            return (false, "Sign in to your account in Settings to upload images.");

        var server = await _serverSettings.GetAsync();
        var http = ManagedWalletClient.CreateHttpClient(server.HostedApiBase, token, Math.Max(server.SmvHttpTimeoutMs, 30000));
        try
        {
            using var resp = await http.PostAsync("plugin-upload-image",
                new StringContent(
                    JsonSerializer.Serialize(new { content_base64 = Convert.ToBase64String(bytes), content_type = contentType }),
                    System.Text.Encoding.UTF8, "application/json"),
                ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!resp.IsSuccessStatusCode)
            {
                var message = doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                              && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() : null;
                _log.LogWarning("image_upload.rejected store={StoreId} status={Status} message={Message}",
                    storeId, (int)resp.StatusCode, message);
                return (false, message ?? "The image was rejected by the platform.");
            }
            var url = doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                return (false, "The platform returned no image URL.");
            _log.LogInformation("image_upload.ok store={StoreId} bytes={Bytes}", storeId, bytes.Length);
            return (true, url!);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "image_upload.failed store={StoreId}", storeId);
            return (false, "Couldn't reach the platform to upload the image. Try again.");
        }
        finally
        {
            http.Dispose();
        }
    }

    // ── Pending-registration queue (B4) ─────────────────────────────────────────
    // A signed mint whose proof isn't exportable yet (anchor unconfirmed) is queued
    // in Store settings and completed opportunistically — from the create-page status
    // poll and from My BDOs loads. The merchant never has to act, and the queue
    // survives restarts and navigation (the earlier page-bound retry lost the
    // signature the moment the merchant navigated away — a certified dead end).

    private static readonly JsonSerializerOptions PendingJson = new(JsonSerializerDefaults.Web);
    private const int MaxPending = 20;

    public async Task QueuePendingAsync(string storeId, PendingByonRegistration entry, CancellationToken ct = default)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        if (settings is null) return;

        // Bind the entry to the account that minted it: the registration fee
        // and the platform ownership follow the completing token, so a store
        // that switches accounts mid-queue must NOT silently register one
        // creator's mint under another account (attribution hygiene).
        if (string.IsNullOrWhiteSpace(entry.QueuedByAccount))
            entry.QueuedByAccount = settings.OAuthConnectedAccount;

        var list = ParsePending(settings.PendingByonRegistrationsJson);
        if (list.Exists(p => string.Equals(p.MintRef, entry.MintRef, StringComparison.OrdinalIgnoreCase)))
            return;   // idempotent — a re-render never duplicates the entry
        list.Add(entry);
        if (list.Count > MaxPending) list.RemoveAt(0);
        settings.PendingByonRegistrationsJson = JsonSerializer.Serialize(list, PendingJson);
        await _storeSettings.SetAsync(storeId, settings, ct);
        _log.LogInformation("byon_register.queued store={StoreId} mint_ref={MintRef}", storeId, entry.MintRef);
    }

    /// <summary>Attempt every queued registration whose proof is now exportable.
    /// Fire-and-forget semantics for callers: never throws, returns how many
    /// completed. Entries stay queued on transient failures and are removed on
    /// success or when the platform says the asset is already registered.</summary>
    public async Task<int> TryCompletePendingAsync(string storeId, Backends.TapdAssetBackend tapd, CancellationToken ct = default)
    {
        try
        {
            var settings = await _storeSettings.GetAsync(storeId, ct);
            if (settings is null) return 0;
            var list = ParsePending(settings.PendingByonRegistrationsJson);
            if (list.Count == 0) return 0;

            var completed = 0;
            var remaining = new List<PendingByonRegistration>();
            foreach (var p in list)
            {
                // Account binding: only the account that minted may complete the
                // registration (it pays the fee and becomes the platform owner).
                // A mismatch keeps the entry queued — switching back completes it.
                // Legacy entries without the stamp stay completable by anyone.
                if (!string.IsNullOrWhiteSpace(p.QueuedByAccount) &&
                    !string.Equals(p.QueuedByAccount, settings.OAuthConnectedAccount, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("byon_register.pending_account_mismatch store={StoreId} mint_ref={MintRef} queued_by={QueuedBy}",
                        storeId, p.MintRef, p.QueuedByAccount);
                    remaining.Add(p);
                    continue;
                }

                string? proofB64 = null;
                try { proofB64 = await tapd.ExportProofBase64Async(p.MintRef, ct); }
                catch { /* anchor still unconfirmed — keep queued */ }
                if (string.IsNullOrWhiteSpace(proofB64)) { remaining.Add(p); continue; }

                ByonSignedEventData? sig = null;
                try { sig = JsonSerializer.Deserialize<ByonSignedEventData>(p.SignedEventJson, PendingJson); }
                catch { /* unreadable entry — drop below */ }
                if (sig is null) { _log.LogWarning("byon_register.pending_unreadable store={StoreId} mint_ref={MintRef}", storeId, p.MintRef); continue; }

                var reg = await RegisterAsync(new ByonRegistrationInput(
                    StoreId: storeId,
                    AssetId: p.MintRef,
                    CanonicalMetaBytes: Convert.FromBase64String(p.CanonicalMetaBase64),
                    MetadataHash: p.MetadataHash,
                    CreatorPubkeyHex: sig.Pubkey ?? "",
                    CreatorSig: sig.Sig ?? "",
                    CreatorEventId: sig.Id ?? "",
                    CreatedAt: sig.CreatedAt,
                    Kind: sig.Kind,
                    Tags: sig.Tags ?? new List<List<string>>(),
                    Content: sig.Content ?? "",
                    Name: p.Name,
                    Description: p.Description,
                    ImageUrl: p.ImageUrl,
                    CollectionName: p.CollectionName,
                    CollectionSlug: p.CollectionSlug,
                    ProofBlobBase64: proofB64!), ct);

                if (reg.Success) completed++;
                else remaining.Add(p);
            }

            if (remaining.Count != list.Count || completed > 0)
            {
                var fresh = await _storeSettings.GetAsync(storeId, ct);
                if (fresh is not null)
                {
                    fresh.PendingByonRegistrationsJson = remaining.Count == 0
                        ? null : JsonSerializer.Serialize(remaining, PendingJson);
                    await _storeSettings.SetAsync(storeId, fresh, ct);
                }
            }
            return completed;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "byon_register.pending_sweep_failed store={StoreId}", storeId);
            return 0;
        }
    }

    public async Task<bool> HasPendingAsync(string storeId, string mintRef, CancellationToken ct = default)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        return settings is not null &&
               ParsePending(settings.PendingByonRegistrationsJson)
                   .Exists(p => string.Equals(p.MintRef, mintRef, StringComparison.OrdinalIgnoreCase));
    }

    private static List<PendingByonRegistration> ParsePending(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<PendingByonRegistration>();
        try { return JsonSerializer.Deserialize<List<PendingByonRegistration>>(json, PendingJson) ?? new(); }
        catch { return new List<PendingByonRegistration>(); }
    }

    private static string MapError(RegisterExternalAssetException ex) => ex.Code switch
    {
        "insufficient_balance" => "Not enough mint credits to register this BDO. Top up your Supermultiverse credits and try again.",
        "insufficient_scope"   => "Your account isn't enabled for self-custody registration yet (BYON allowlist).",
        "invalid_request"      => string.IsNullOrWhiteSpace(ex.Message)
                                    ? "The registration was rejected as invalid."
                                    : $"The registration was rejected: {ex.Message}",
        _                      => ex.StatusCode == 401
                                    ? "Your Supermultiverse connection was rejected — reconnect in Settings."
                                    : "Couldn't register the BDO with Supermultiverse."
    };
}

/// <summary>Everything the register orchestration needs. The signed-event fields are passed
/// verbatim (the signature covers those exact bytes).</summary>
public sealed record ByonRegistrationInput(
    string StoreId,
    string AssetId,
    byte[] CanonicalMetaBytes,
    string MetadataHash,
    string CreatorPubkeyHex,
    string CreatorSig,
    string CreatorEventId,
    long CreatedAt,
    int Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content,
    string Name,
    string? Description,
    string? ImageUrl,
    string CollectionName,
    string? CollectionSlug,
    string ProofBlobBase64);

/// <summary>A signed BYON mint queued for automatic registration. Carries the EXACT
/// canonical metadata bytes and the signed event verbatim — completion never
/// recomputes, so the signature can never drift from the registered content.</summary>
public sealed class PendingByonRegistration
{
    public string MintRef { get; set; } = "";
    public string CanonicalMetaBase64 { get; set; } = "";
    public string MetadataHash { get; set; } = "";
    public string SignedEventJson { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string CollectionName { get; set; } = "";
    public string? CollectionSlug { get; set; }
    public string QueuedAtUtc { get; set; } = "";
    // Account (email) connected when the mint was queued. Completion is
    // restricted to this account; null (legacy entries) = unrestricted.
    public string? QueuedByAccount { get; set; }
}

/// <summary>Minimal NIP-07 signed-event shape for pending-queue deserialization
/// (mirrors the create form's signed event).</summary>
public sealed class ByonSignedEventData
{
    [System.Text.Json.Serialization.JsonPropertyName("id")] public string? Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pubkey")] public string? Pubkey { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("sig")] public string? Sig { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("kind")] public int Kind { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("content")] public string? Content { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tags")] public List<List<string>>? Tags { get; set; }
}

public sealed class ByonRegistrationResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public RegisterExternalAssetResponse? Response { get; init; }

    public static ByonRegistrationResult Ok(RegisterExternalAssetResponse r) => new() { Success = true, Response = r };
    public static ByonRegistrationResult Fail(string message) => new() { Success = false, Message = message };
}
