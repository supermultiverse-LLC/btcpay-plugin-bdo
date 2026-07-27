using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Core;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Create BDO (issuance) surface — Plugin-I2/I3 (RFC-PLUGIN-004). Hosted-only,
/// collectibles-only in v1.2: a BYON Store sees a disabled explanatory panel
/// (symmetric with Receive being BYON-only). The GET renders the form + the
/// merchant's collections (reuse-or-create picker) + a server-rendered cost
/// preview; the POST commits the mint and renders the inline LN fee invoice
/// (contract §5.2); the <see cref="Status"/> endpoint feeds create.js, which
/// polls the mint to <c>minted</c> / <c>refunded_credit</c> / <c>failed</c>.
/// </summary>
[Route("stores/{storeId}/plugins/smv/create")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvCreateController : Controller
{
    // Fee-drift buffer on the pre-invoice cap (RFC-PLUGIN-004 §7): the quote is an
    // estimate, the real fee is computed at mint time, so authorise the quoted total
    // plus 25% (min 250 sats) — a larger drift returns fee_too_high before any charge.
    private const int FeeBufferPercent = 25;
    private const long FeeBufferFloorSats = 250;

    private readonly IAssetBackendResolver _backends;
    private readonly Services.ByonRegistrationService _byonRegistration;
    private readonly Services.ISmvStoreSettingsProvider _storeSettings;
    private readonly Services.OAuth.SmvOAuthTokenService _oauthTokens;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly ILogger<SmvCreateController> _log;

    public SmvCreateController(
        IAssetBackendResolver backends,
        Services.ByonRegistrationService byonRegistration,
        Services.ISmvStoreSettingsProvider storeSettings,
        Services.OAuth.SmvOAuthTokenService oauthTokens,
        ISettingsRepositoryAccessor serverSettings,
        ILogger<SmvCreateController> log)
    {
        _backends = backends;
        _byonRegistration = byonRegistration;
        _storeSettings = storeSettings;
        _oauthTokens = oauthTokens;
        _serverSettings = serverSettings;
        _log = log;
    }

    // At-a-glance plan + credits for the Create page (both modes). Best-effort:
    // any failure leaves the fields null and the page renders as before.
    private async Task PopulatePlanGlanceAsync(string storeId, Settings.SmvStoreSettings? settings, SmvCreateVm vm, CancellationToken ct)
    {
        if (settings?.HasOAuthConnection != true) return;
        try
        {
            var token = await _oauthTokens.EnsureFreshTokenAsync(storeId, settings, ct);
            if (string.IsNullOrWhiteSpace(token)) return;
            var server = await _serverSettings.GetAsync();
            using var http = ManagedWalletClient.CreateHttpClient(server.HostedApiBase, token, Math.Max(server.SmvHttpTimeoutMs, 15000));
            var client = new ManagedWalletClient(http);
            var sub = await client.GetSubscriptionInfoAsync(ct);
            vm.PlanTier = sub.Current?.Tier;
            if (vm.CreditBalanceSats is null)
            {
                var topup = await client.GetTopupInfoAsync(ct);
                vm.CreditBalanceSats = topup.BalanceSats;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "create.plan_glance_failed store={StoreId}", storeId);
        }
    }

    // Gating matrix: resolve the Create surface's gate BEFORE any form renders or
    // submits. Hosted without an account → sign-in door; Hosted connected with
    // assets:mint KNOWN-denied → plan gate (the grant is stored locally, no API call).
    // BYON and unknown grants (manual tokens) pass through.
    private async Task<bool> ApplyCreateGateAsync(string storeId, SmvCreateVm vm, CancellationToken ct)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        // Device upload is a platform service: usable whenever the Store holds ANY
        // token (OAuth or manual), in either mode. The picker gates at the door.
        vm.CanUploadImage = settings is not null &&
            (!string.IsNullOrWhiteSpace(settings.HostedApiToken) || settings.HasOAuthConnection);
        if (settings?.IsHostedNotConnected == true)
        {
            vm.AccountGate = true;
            return true;
        }
        if (settings?.IsHosted == true && settings.HasGrantedScope("assets:mint") == false)
        {
            vm.MintNotGranted = true;
            return true;
        }
        return false;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        var vm = new SmvCreateVm();

        if (await ApplyCreateGateAsync(store.Id, vm, cancellationToken))
            return View(vm);

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null)
        {
            vm.Error = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            vm.NotConfigured = true;
            return View(vm);
        }

        vm.ConnectionLabel = backend.ConnectionLabel;
        vm.IsSelfCustody = !backend.IsCustodial;

        // BYON (RFC-PLUGIN-006 P2-1): minted on the merchant's own node — no credit
        // balance and no upfront LN fee (the SMV service fee is charged at
        // registration, P2-2). Render the form directly, telling the merchant
        // UPFRONT what will happen at registration time (the wall must never
        // appear by surprise after the mint).
        if (!backend.IsCustodial)
        {
            var settings = await _storeSettings.GetAsync(store.Id, cancellationToken);
            vm.ByonAccountConnected = settings?.HasOAuthConnection == true;
            vm.ByonCanRegister = settings?.HasGrantedScope("assets:register_external");
            await PopulatePlanGlanceAsync(store.Id, settings, vm, cancellationToken);
            return View(vm);
        }

        try
        {
            // GetInfo authenticates the token and carries the credit balance; a
            // failure here is a genuine "cannot reach / not authorised" hard error.
            var info = await backend.GetInfoAsync(cancellationToken);
            vm.CreditBalanceSats = info.CreditBalanceSats;
        }
        catch (Exception ex)
        {
            vm.Error = MapError(ex, "reach the hosted wallet");
            return View(vm);
        }

        // Cost preview is best-effort: if the issuance endpoints are not deployed yet
        // (or hiccup), still render the form so it stays usable.
        await PopulateQuoteAsync(backend, vm, cancellationToken);

        var hostedSettings = await _storeSettings.GetAsync(store.Id, cancellationToken);
        await PopulatePlanGlanceAsync(store.Id, hostedSettings, vm, cancellationToken);

        return View(vm);
    }

    [HttpPost("")]
    public async Task<IActionResult> Create(SmvCreateVm form, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // Preserve the merchant's input for a re-render on validation/backend error.
        var vm = new SmvCreateVm
        {
            AssetName = form.AssetName?.Trim() ?? "",
            ImageUrl = form.ImageUrl?.Trim() ?? "",
            Description = form.Description?.Trim() ?? "",
            AttributesText = form.AttributesText ?? "",
            ExternalReference = form.ExternalReference?.Trim() ?? "",
            SignedEventJson = form.SignedEventJson  // BYON: the optional creator signature
        };

        // Same gate the GET applies — defends against a stale form posting after the
        // connection was dropped or its capabilities changed.
        if (await ApplyCreateGateAsync(store.Id, vm, cancellationToken))
            return View("Index", vm);

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null)
        {
            vm.Error = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            vm.NotConfigured = true;
            return View("Index", vm);
        }

        vm.ConnectionLabel = backend.ConnectionLabel;
        vm.IsSelfCustody = !backend.IsCustodial;

        try
        {
            var info = await backend.GetInfoAsync(cancellationToken);
            vm.CreditBalanceSats = info.CreditBalanceSats;
        }
        catch { /* non-fatal for the POST; the mint call below is the authority */ }

        // Best-effort quote so the cost preview survives a re-render on a validation
        // error, and to feed the pre-invoice fee cap below (reused, not re-fetched).
        try { vm.Quote = await backend.MintQuoteAsync(new MintQuoteRequest(), cancellationToken); }
        catch { vm.QuoteUnavailable = true; }

        if (string.IsNullOrWhiteSpace(vm.AssetName))
        {
            vm.Error = "Enter a name for the Bitcoin Digital Object.";
            return View("Index", vm);
        }

        // The backend requires a valid https image URL for a collectible (v1.2). Validate
        // client-side-friendly here so the merchant gets a clear message, not a raw 400.
        if (string.IsNullOrWhiteSpace(vm.ImageUrl) || !vm.ImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            vm.Error = "An image is required — paste a public https link to a PNG, JPEG, or WebP.";
            return View("Index", vm);
        }

        // BYON: the creator signature is OPTIONAL — an authorship add-on. If the merchant
        // signed, it's carried into registration; if not, the BDO is created unsigned.
        var creatorSig = vm.IsSelfCustody ? TryParseSignedEvent(vm.SignedEventJson) : null;

        // BYON: compute the STAS-01 canonical metadata ONCE. Its exact bytes are minted
        // into asset_meta.data, so sha256(asset_meta) == the metadata_hash. When the
        // creator signed, that signature MUST cover these very bytes — otherwise the
        // details changed after signing and we refuse to mint a mismatched asset.
        byte[]? canonicalMeta = null;
        string? byonMetaHash = null;
        if (vm.IsSelfCustody)
        {
            // Attributes must parse COMPLETELY or the mint stops — a silently dropped
            // line would mint metadata that differs from what the merchant intended
            // (and from what they signed, had prepare seen different bytes).
            if (AttributesLineMismatch(vm.AttributesText, out var badLine))
            {
                vm.Error = $"Attribute line \"{badLine}\" isn't valid — use one attribute per line as \"trait: value\" (e.g. \"tier: gold\").";
                return View("Index", vm);
            }

            var metadata = BuildBdoMetadata(store, vm.AssetName, vm.ImageUrl, vm.Description, vm.AttributesText, vm.ExternalReference);
            byonMetaHash = StasMetadata.MetadataHash(metadata);
            canonicalMeta = Encoding.UTF8.GetBytes(StasMetadata.Canonicalize(metadata));

            if (creatorSig is not null
                && !string.Equals(SignedMetadataHash(creatorSig), byonMetaHash, StringComparison.OrdinalIgnoreCase))
            {
                vm.Error = "The signature doesn't match this BDO's details — if you changed a field after signing, sign again.";
                return View("Index", vm);
            }
        }

        // "Mint one BDO" hides the collection concept: every single mint gets its OWN
        // size-1 collection, derived from the BDO name with a unique slug so mints never
        // collide (a whole collection at once is "Mint a series" — RFC-PLUGIN-005).
        var collectionName = vm.AssetName;
        var collectionSlug = Slugify(vm.AssetName) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        const long collectionTotalSupply = 1;
        var collectionImageUrl = string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl;

        try
        {
            // Reuse the estimate fetched above (a few seconds old is fine — the backend
            // re-computes and enforces the real fee against the cap at reservation, §7).
            var quote = vm.Quote ?? await backend.MintQuoteAsync(new MintQuoteRequest(), cancellationToken);
            vm.Quote = quote;

            var request = new MintRequest(
                CollectionName: collectionName,
                CollectionSlug: collectionSlug,
                CollectionTotalSupply: collectionTotalSupply,
                AssetName: vm.AssetName,
                AcceptFeeQuoteUpToSats: FeeCapFor(quote.TotalSats),
                CollectionImageUrl: collectionImageUrl,
                AssetImageUrl: string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl,
                Description: string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description,
                Attributes: ParseAttributes(vm.AttributesText),
                ExternalReference: string.IsNullOrWhiteSpace(vm.ExternalReference) ? null : vm.ExternalReference,
                CanonicalMetaBytes: canonicalMeta);   // BYON: mint the exact signed STAS-01 bytes

            vm.Result = await backend.MintAsync(request, cancellationToken);

            // BYON: the asset is minted on the node with its canonical STAS-01 meta. When the
            // creator signed, register it as a full SMV BDO (pin → envelope → register). A
            // register failure is non-fatal: the asset stays sovereign on the node (RFC-006 D2)
            // and we surface a note. Unsigned BYON is not registered (no platform fallback).
            if (vm.IsSelfCustody && vm.Result is not null && creatorSig is not null
                && canonicalMeta is not null && byonMetaHash is not null)
            {
                var assetId = vm.Result.MintRef;

                // B4 finding: the proof exists only once the anchor tx CONFIRMS — right
                // after the mint broadcast this export fails as the NORMAL case, so it
                // must degrade to the retry offer, never to a raw 500 banner.
                string? proofB64 = null;
                if (backend is TapdAssetBackend tapd && !string.IsNullOrWhiteSpace(assetId))
                {
                    try { proofB64 = await tapd.ExportProofBase64Async(assetId!, cancellationToken); }
                    catch (Exception proofEx)
                    {
                        _log.LogInformation(proofEx, "byon_register.proof_not_ready mint_ref={MintRef}", assetId);
                    }
                }

                if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(proofB64))
                {
                    // Queue it — registration completes AUTOMATICALLY (status poll +
                    // My BDOs loads) once the anchor confirms. No merchant action.
                    if (!string.IsNullOrWhiteSpace(assetId) && !string.IsNullOrWhiteSpace(vm.SignedEventJson))
                    {
                        await _byonRegistration.QueuePendingAsync(store.Id, new PendingByonRegistration
                        {
                            MintRef = assetId!,
                            CanonicalMetaBase64 = Convert.ToBase64String(canonicalMeta),
                            MetadataHash = byonMetaHash,
                            SignedEventJson = vm.SignedEventJson!,
                            Name = vm.AssetName,
                            Description = string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description,
                            ImageUrl = string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl,
                            CollectionName = collectionName,
                            CollectionSlug = collectionSlug,
                            QueuedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                        }, cancellationToken);
                    }
                    vm.RegistrationNote = "Minted on your node. It will be registered with Supermultiverse automatically " +
                        "once the anchor confirms on-chain (typically a few minutes) — nothing else to do.";
                }
                else
                {
                    var reg = await _byonRegistration.RegisterAsync(new ByonRegistrationInput(
                        StoreId: store.Id,
                        AssetId: assetId!,
                        CanonicalMetaBytes: canonicalMeta,
                        MetadataHash: byonMetaHash,
                        CreatorPubkeyHex: creatorSig.Pubkey ?? "",
                        CreatorSig: creatorSig.Sig ?? "",
                        CreatorEventId: creatorSig.Id ?? "",
                        CreatedAt: creatorSig.CreatedAt,
                        Kind: creatorSig.Kind,
                        Tags: creatorSig.Tags ?? new List<List<string>>(),
                        Content: creatorSig.Content ?? "",
                        Name: vm.AssetName,
                        Description: string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description,
                        ImageUrl: string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl,
                        CollectionName: collectionName,
                        CollectionSlug: collectionSlug,
                        ProofBlobBase64: proofB64!), cancellationToken);

                    vm.RegistrationNote = reg.Success
                        ? (reg.Response?.AlreadyRegistered == true
                            ? "Already registered with Supermultiverse."
                            : "Registered with Supermultiverse — verifying the on-chain anchor.")
                        : reg.Message;
                }
            }
        }
        catch (Exception ex)
        {
            vm.Error = MapError(ex, "create the Bitcoin Digital Object");
        }

        return View("Index", vm);
    }

    // Live mint-status for create.js polling (I3). Returns the neutral status as a
    // small stable JSON shape. Fails SAFE to a non-terminal "minting" so a transient
    // read hiccup keeps the poller going rather than declaring a false failure.
    [HttpGet("status/{mintRef}")]
    public async Task<IActionResult> Status(string mintRef, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null)
            return Json(new { state = "failed", message = "Minting is not available for this Store." });

        // B4: the poll doubles as the pending-registration driver — once the anchor
        // confirms, the queued signed mint registers automatically, no merchant action.
        if (backend is TapdAssetBackend pollTapd)
        {
            try { await _byonRegistration.TryCompletePendingAsync(store.Id, pollTapd, cancellationToken); }
            catch { /* best-effort; the My BDOs load retries too */ }
        }

        try
        {
            var s = await backend.GetMintStatusAsync(mintRef, cancellationToken);
            return Json(new
            {
                state = WireState(s.State),
                message = s.Message,
                invoice_status = s.InvoiceStatus,
                bdo_id = s.BdoId,
                smv_id = s.SmvId,
                proof_url = s.ProofUrl,
                collection_name = s.CollectionName,
                refund_credit_sats = s.RefundCreditSats,
                provider_state = s.ProviderState
            });
        }
        catch (Exception ex)
        {
            // A transient read failure must NOT hide the invoice: reporting "minting"
            // makes create.js treat the fee as paid. Report "awaiting_payment" (a no-op
            // in create.js) so the invoice stays visible and polling continues. Log the
            // real cause so a persistent status-read failure is diagnosable.
            _log.LogWarning(ex, "ui_create.mint_status_read_failed mint_ref={MintRef}", mintRef);
            return Json(new { state = "awaiting_payment", message = "Checking status…" });
        }
    }

    // Create form "upload from device" (both modes): proxy the file to the platform's
    // plugin-upload-image with the Store's token; upload-image.js fills #ImageUrl with
    // the returned SMV-hosted https URL. The platform re-validates type/size and
    // stores content-addressed, so re-uploads are idempotent.
    [HttpPost("upload-image")]
    public async Task<IActionResult> UploadImage(Microsoft.AspNetCore.Http.IFormFile? image, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        if (image is null || image.Length == 0)
            return Json(new { ok = false, message = "Choose an image file." });
        if (image.Length > 10 * 1024 * 1024)
            return Json(new { ok = false, message = "The image exceeds 10 MB." });
        var contentType = (image.ContentType ?? "").ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            return Json(new { ok = false, message = "Use a PNG, JPEG or WebP image." });

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await image.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }

        var (okUp, urlOrMessage) = await _byonRegistration.UploadImageAsync(store.Id, bytes, contentType, cancellationToken);
        return okUp
            ? Json(new { ok = true, url = urlOrMessage })
            : Json(new { ok = false, message = urlOrMessage });
    }

    // BYON: compute the STAS-01 metadata_hash the creator signs BEFORE minting
    // (Studio "Prepare" step — the signature attests authorship of the content, so it
    // precedes the mint). byon-create.js calls this, then signs, then submits the form.
    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare(CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        string raw;
        using (var reader = new StreamReader(Request.Body))
            raw = await reader.ReadToEndAsync(cancellationToken);

        ByonPrepareRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<ByonPrepareRequest>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return Json(new { ok = false, message = "Malformed request." });
        }

        if (req is null || string.IsNullOrWhiteSpace(req.AssetName))
            return Json(new { ok = false, message = "Enter a name for the BDO." });
        if (string.IsNullOrWhiteSpace(req.ImageUrl)
            || !req.ImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Json(new { ok = false, message = "An image is required — a public https link." });

        if (AttributesLineMismatch(req.AttributesText, out var badLine))
            return Json(new { ok = false, message = $"Attribute line \"{badLine}\" isn't valid — use one attribute per line as \"trait: value\"." });

        var metadata = BuildBdoMetadata(store, req.AssetName, req.ImageUrl, req.Description, req.AttributesText, req.ExternalReference);
        var hash = StasMetadata.MetadataHash(metadata);

        return Json(new { ok = true, metadata_hash = hash, issuer = IssuerFor(store) });
    }

    // Issuer = the creator/store display name (Lovable's rule); never an id/npub/url.
    private static string IssuerFor(BTCPayServer.Data.StoreData store)
        => string.IsNullOrWhiteSpace(store.StoreName) ? "Creator" : store.StoreName;

    // Single source of truth for a BYON BDO's STAS-01 metadata (RFC-PLUGIN-006 P2-2c).
    // Prepare hashes it for the creator to sign; Create mints its canonical bytes. Same
    // construction + same inputs → sha256(minted asset_meta) == the signed metadata_hash,
    // so there is no drift between what was signed and what is on-chain.
    private static Dictionary<string, object> BuildBdoMetadata(
        BTCPayServer.Data.StoreData store, string? name, string? imageUrl, string? description, string? attributesText,
        string? externalReference = null)
    {
        var attrs = (ParseAttributes(attributesText) ?? new List<MintAttribute>())
            .Select(a => (a.TraitType, a.Value)).ToList();
        return StasMetadata.Build(
            name: (name ?? "").Trim(),
            issuer: IssuerFor(store),
            description: string.IsNullOrWhiteSpace(description) ? null : description,
            image: string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim(),
            attributes: attrs,
            // BYON enrichment fix: the form's external reference was silently dropped
            // from the minted metadata — Build validates it to http(s) and emits
            // external_url per STAS-01.
            externalUrl: string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim());
    }

    // The metadata_hash the creator committed to, read from the signed event's tags
    // (["metadata_hash", "<hex>"]). Null when the tag is absent/malformed.
    private static string? SignedMetadataHash(ByonSignedEvent ev)
        => ev.Tags?.FirstOrDefault(t => t is { Count: >= 2 } && t[0] == "metadata_hash")?[1];

    // Stable wire tokens for create.js (decoupled from the enum's C# names).
    private static string WireState(MintState state) => state switch
    {
        MintState.AwaitingPayment => "awaiting_payment",
        MintState.Minting         => "minting",
        MintState.Minted          => "minted",
        MintState.Failed          => "failed",
        MintState.RefundedCredit  => "refunded_credit",
        _                         => "minting"
    };

    // Parse + shape-check the creator's Nostr-signed event from the form field.
    private static ByonSignedEvent? TryParseSignedEvent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ev = JsonSerializer.Deserialize<ByonSignedEvent>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (ev is null || string.IsNullOrEmpty(ev.Id) || string.IsNullOrEmpty(ev.Pubkey) || string.IsNullOrEmpty(ev.Sig))
                return null;
            return ev;
        }
        catch { return null; }
    }

    private static async Task PopulateQuoteAsync(IAssetBackend backend, SmvCreateVm vm, CancellationToken ct)
    {
        try { vm.Quote = await backend.MintQuoteAsync(new MintQuoteRequest(), ct); }
        catch { vm.QuoteUnavailable = true; }
    }

    private static long FeeCapFor(long totalSats)
        => totalSats + Math.Max(totalSats * FeeBufferPercent / 100, FeeBufferFloorSats);

    // "trait: value" (or "trait = value") per line → attributes. Blank lines and
    // lines without a separator are ignored. No JS needed for dynamic rows.
    // True when the attributes textarea contains a non-empty line that ParseAttributes
    // would silently drop — surfaced as a validation error instead (BYON enrichment fix:
    // silent drops made merchants believe their attributes were minted when they weren't).
    private static bool AttributesLineMismatch(string? text, out string badLine)
    {
        badLine = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var sep = line.IndexOfAny(new[] { ':', '=' });
            if (sep <= 0 || sep >= line.Length - 1
                || line[..sep].Trim().Length == 0 || line[(sep + 1)..].Trim().Length == 0)
            {
                badLine = line.Length > 60 ? line[..60] + "…" : line;
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<MintAttribute>? ParseAttributes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var list = new List<MintAttribute>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var sep = line.IndexOfAny(new[] { ':', '=' });
            if (sep <= 0 || sep >= line.Length - 1) continue;

            var trait = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (trait.Length == 0 || value.Length == 0) continue;

            list.Add(new MintAttribute(trait, value));
        }

        return list.Count > 0 ? list : null;
    }

    // Lowercase, alphanumerics kept, everything else collapsed to single hyphens.
    private static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var sb = new StringBuilder(input.Length);
        var lastHyphen = false;
        foreach (var ch in input.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastHyphen = false;
            }
            else if (!lastHyphen)
            {
                sb.Append('-');
                lastHyphen = true;
            }
        }

        return sb.ToString().Trim('-');
    }

    // Maps a backend failure to a user message (RFC-PLUGIN-004 §14, closed set §6).
    // Unknown/unmapped codes fall through to a generic message (fail closed).
    private static string MapError(Exception ex, string action) => ex switch
    {
        SelfCustodyMintNotAvailableException => ex.Message,
        HostedFeatureNotAvailableException => ex.Message,
        ManagedWalletApiException api => api.Code switch
        {
            ManagedWalletErrorCode.QuoteExpired       => "The price quote expired — please try again.",
            ManagedWalletErrorCode.QuoteNotFound      => "That quote is no longer valid — please start again.",
            ManagedWalletErrorCode.FeeTooHigh         => "The on-chain fee moved above the allowed cap. Please try again in a moment.",
            ManagedWalletErrorCode.ImageFetchFailed   => "We couldn't fetch that image URL — check it's public and reachable.",
            ManagedWalletErrorCode.ImageTooLarge      => "That image is too large (maximum 10 MB).",
            ManagedWalletErrorCode.CollectionFull     => "This collection is full. Pick another collection or create a new one.",
            ManagedWalletErrorCode.SupplyExceeded     => "That exceeds the collection's remaining supply.",
            ManagedWalletErrorCode.MintFailed         => "Minting failed after payment — you were refunded as credit. Please try again.",
            ManagedWalletErrorCode.TapdUnavailable    => "The minting service is busy right now. Please try again shortly.",
            ManagedWalletErrorCode.IdempotencyConflict => "A conflicting mint is already in progress. Please retry.",
            ManagedWalletErrorCode.IdempotencyInFlight => "This mint is already being processed. Please wait a moment and check its status.",
            ManagedWalletErrorCode.InsufficientScope  => "This connection can't mint — it doesn't have the mint permission. In Settings, reconnect your BDO account to grant it (or, for a manual token, re-issue it with the assets:mint scope).",
            ManagedWalletErrorCode.InvalidRequest     => string.IsNullOrWhiteSpace(api.Message)
                                                            ? "The mint request was rejected as invalid — check the fields and try again."
                                                            : $"The mint request was rejected: {api.Message}",
            ManagedWalletErrorCode.Unauthorized       => "The hosted connection was rejected. Check the token in Settings.",
            ManagedWalletErrorCode.RateLimited        => "Too many requests right now. Please wait a moment and try again.",
            _                                         => $"Could not {action}: {api.Message}"
        },
        _ => $"Could not {action}: {ex.Message}"
    };
}

public class SmvCreateVm
{
    public string? Error { get; set; }

    /// <summary>True for a BYON (self-custody) Store: minted on the merchant's own node,
    /// so the view shows the self-custody flow — no credit/quote, and no LN fee invoice
    /// (the SMV service fee is charged at registration, RFC-PLUGIN-006 P2-2).</summary>
    public bool IsSelfCustody { get; set; }

    /// <summary>BYON: the creator's Nostr-signed event (JSON), filled by byon-create.js
    /// from window.nostr before the form submits — the authorship signature.</summary>
    public string? SignedEventJson { get; set; }

    /// <summary>True when the Store has no backend configured: the view shows only the
    /// "not configured" notice, no form. A validation/mint error is NOT this — the form
    /// must re-render with the merchant's input on those.</summary>
    public bool NotConfigured { get; set; }

    /// <summary>Gating matrix: Hosted without an account — the view renders the sign-in
    /// door instead of the form (the account IS the wallet).</summary>
    public bool AccountGate { get; set; }

    /// <summary>Gating matrix: Hosted connected but assets:mint is KNOWN-denied — the
    /// view renders the plan gate (requires Silver+) instead of a form that would fail
    /// at submit. Unknown grants (manual tokens) never set this.</summary>
    public bool MintNotGranted { get; set; }

    /// <summary>Whether the device-upload picker is usable: uploading is a PLATFORM
    /// service (SMV hosts the bytes), so it needs a connected account even in BYON —
    /// the one Create feature that does. When false the view renders the picker
    /// disabled with a sign-in hint UP FRONT (gate at the door), and URL paste
    /// remains fully functional.</summary>
    public bool CanUploadImage { get; set; }

    public string? ConnectionLabel { get; set; }

    /// <summary>Spendable mint credit (Hosted, contract §3); null when unknown.</summary>
    public long? CreditBalanceSats { get; set; }

    // BYON pre-mint registration notice (approved journey fix "a"): tell the
    // merchant BEFORE minting whether the platform registration will follow.
    // Tri-state: true = will register; false = plan wall (Gold); null =
    // unknown grant. ByonAccountConnected=false = no account at all.
    public bool ByonAccountConnected { get; set; }
    public bool? ByonCanRegister { get; set; }

    // At-a-glance plan tier slug (silver_v2 | tier_1 | tier_2 | null).
    public string? PlanTier { get; set; }

    /// <summary>Server-rendered cost preview; null when unavailable.</summary>
    public MintQuote? Quote { get; set; }
    public bool QuoteUnavailable { get; set; }

    // Form fields. "Mint one BDO" has no collection concept — the controller derives a
    // size-1 collection from the name (a whole collection at once is "Mint a series").
    public string AssetName { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public string AttributesText { get; set; } = "";
    public string ExternalReference { get; set; } = "";

    /// <summary>The inline LN fee invoice returned by the mint (contract §5.2); null until minted.</summary>
    public MintResult? Result { get; set; }

    /// <summary>BYON only: the outcome of SMV registration after a signed self-custody mint
    /// (RFC-PLUGIN-006 P2-2c) — success/pending/error copy. Null when not applicable.</summary>
    public string? RegistrationNote { get; set; }

}

/// <summary>Body of POST create/prepare — the form fields, to compute the
/// metadata_hash the creator signs (RFC-PLUGIN-006 P2-2). Parsed with System.Text.Json.</summary>
public sealed class ByonPrepareRequest
{
    public string? AssetName { get; set; }
    public string? ImageUrl { get; set; }
    public string? Description { get; set; }
    public string? AttributesText { get; set; }
    public string? ExternalReference { get; set; }
}

/// <summary>A NIP-07-signed Nostr event (window.nostr.signEvent output).</summary>
public sealed class ByonSignedEvent
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("pubkey")] public string? Pubkey { get; set; }
    [JsonPropertyName("sig")] public string? Sig { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tags")] public List<List<string>>? Tags { get; set; }
}
