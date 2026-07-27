using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Batch mint (Modality 3 — the moat): N unique BDOs anchored in ~1 on-chain tx via a
/// Taproot Assets group key. Hosted-only (a BYON Store sees a disabled panel, symmetric
/// with single Create). The GET renders the form; the POST submits the batch and renders
/// the inline LN fee invoice (aggregate quote); <see cref="Status"/> feeds batch.js, which
/// polls by phase to <c>confirmed</c> / <c>failed</c> / <c>refunded_credit</c>. Async by
/// design (a 2000-unit batch is a few minutes): submit → 202 → poll, never a sync wait.
/// </summary>
[Route("stores/{storeId}/plugins/smv/batch")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class SmvBatchController : Controller
{
    // Cap 2000 is enforced by the backend RPC; mirror it here for a clear message.
    private const long MaxUnitCount = 2000;

    // Fee-drift buffer on the pre-invoice cap (mirrors single Create): the aggregate
    // quote is an estimate, so authorise +25% (min 250 sats) — a larger drift returns
    // fee_too_high before any charge.
    private const int FeeBufferPercent = 25;
    private const long FeeBufferFloorSats = 250;

    private readonly IAssetBackendResolver _backends;
    private readonly ILogger<SmvBatchController> _log;

    public SmvBatchController(IAssetBackendResolver backends, ILogger<SmvBatchController> log)
    {
        _backends = backends;
        _log = log;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        var vm = new SmvBatchVm();

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null)
        {
            vm.Error = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            vm.NotConfigured = true;
            return View(vm);
        }

        if (!backend.IsCustodial)
        {
            vm.Disabled = true;
            vm.ConnectionLabel = backend.ConnectionLabel;
            return View(vm);
        }

        vm.ConnectionLabel = backend.ConnectionLabel;

        // A per-unit quote to show the cost shape ("on-chain is ~one-off; platform is
        // per-unit × quantity"); the exact aggregate is on the invoice at submit.
        try { vm.UnitQuote = await backend.MintQuoteAsync(new MintQuoteRequest(), cancellationToken); }
        catch { vm.QuoteUnavailable = true; }

        return View(vm);
    }

    [HttpPost("")]
    public async Task<IActionResult> Create(SmvBatchVm form, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        // Preserve input for a re-render on validation/backend error.
        var vm = new SmvBatchVm
        {
            SeriesName = form.SeriesName?.Trim() ?? "",
            CollectionName = form.CollectionName?.Trim() ?? "",
            UnitCount = form.UnitCount,
            CollectionTotalSupply = form.CollectionTotalSupply,
            ImageUrl = form.ImageUrl?.Trim() ?? "",
            Description = form.Description?.Trim() ?? "",
            AttributesText = form.AttributesText ?? ""
        };

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null)
        {
            vm.Error = "Bitcoin Digital Objects infrastructure is not configured. Add your connection in Settings.";
            vm.NotConfigured = true;
            return View("Index", vm);
        }

        if (!backend.IsCustodial)
        {
            vm.Disabled = true;
            vm.ConnectionLabel = backend.ConnectionLabel;
            return View("Index", vm);
        }

        vm.ConnectionLabel = backend.ConnectionLabel;
        try { vm.UnitQuote = await backend.MintQuoteAsync(new MintQuoteRequest(), cancellationToken); }
        catch { vm.QuoteUnavailable = true; }

        // ── Validation ─────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(vm.SeriesName))
        {
            vm.Error = "Enter a series name — each BDO is named “<series> #0001”, “#0002”, …";
            return View("Index", vm);
        }
        if (vm.UnitCount < 1 || vm.UnitCount > MaxUnitCount)
        {
            vm.Error = $"Quantity must be between 1 and {MaxUnitCount}.";
            return View("Index", vm);
        }
        if (string.IsNullOrWhiteSpace(vm.ImageUrl) || !vm.ImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            vm.Error = "An image is required — paste a public https link to a PNG, JPEG, or WebP.";
            return View("Index", vm);
        }

        // Collection: reuse the series name if a distinct collection name isn't given.
        // total_supply defaults to the batch size unless the merchant set a larger one.
        var collectionName = string.IsNullOrWhiteSpace(vm.CollectionName) ? vm.SeriesName : vm.CollectionName;
        var collectionTotalSupply = Math.Max(vm.CollectionTotalSupply, vm.UnitCount);

        try
        {
            var unitQuote = vm.UnitQuote ?? await backend.MintQuoteAsync(new MintQuoteRequest(), cancellationToken);
            // Aggregate estimate for the pre-invoice cap: Layer A on-chain ~constant +
            // Layer B platform × N (RFC §3.3). Uses the BATCH anchor estimate (154 vB,
            // same math as the commit) — the single-mint on-chain fee is a different,
            // larger transaction and overquoted the series (2026-07-26 find).
            var batchOnchain = unitQuote.BatchOnchainFeeSats ?? unitQuote.OnchainFeeSats;
            var aggregateEstimate = batchOnchain + unitQuote.PlatformMarginSats * vm.UnitCount;

            var request = new MintBatchRequest(
                CollectionName: collectionName,
                // Unique slug so every series creates its OWN new, complete collection —
                // two series with the same name never merge (no reuse in the plugin).
                CollectionSlug: Slugify(collectionName) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                CollectionTotalSupply: collectionTotalSupply,
                UnitCount: vm.UnitCount,
                TemplateName: vm.SeriesName,
                AcceptFeeQuoteUpToSats: FeeCapFor(aggregateEstimate),
                CollectionImageUrl: string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl,
                ImageUrl: string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl,
                Description: string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description,
                Attributes: ParseAttributes(vm.AttributesText));

            vm.Result = await backend.MintBatchAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            vm.Error = MapError(ex, "create the batch");
        }

        return View("Index", vm);
    }

    // Live batch-status for batch.js. Progress runs through the phase (state); minted
    // jumps 0 → total atomically on completion, so the UI shows the phase, not a bar.
    [HttpGet("status/{batchRef}")]
    public async Task<IActionResult> Status(string batchRef, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null)
            return NotFound();

        using var backend = await _backends.ResolveAsync(store.Id, cancellationToken);
        if (backend is null || !backend.IsCustodial)
            return Json(new { state = "failed", message = "Batch minting is not available for this Store." });

        try
        {
            var s = await backend.GetMintBatchStatusAsync(batchRef, cancellationToken);
            return Json(new
            {
                state = WireState(s.State),
                message = s.Message,
                invoice_status = s.InvoiceStatus,
                minted = s.Minted,
                total = s.Total,
                collection_id = s.CollectionId,
                collection_name = s.CollectionName,
                refund_credit_sats = s.RefundCreditSats,
                provider_state = s.ProviderState
            });
        }
        catch (Exception ex)
        {
            // A transient read failure must NOT hide the invoice: report awaiting_payment
            // (a no-op for batch.js) so the invoice stays visible and polling continues.
            _log.LogWarning(ex, "ui_batch.status_read_failed batch_ref={BatchRef}", batchRef);
            return Json(new { state = "awaiting_payment", message = "Checking status…" });
        }
    }

    private static string WireState(MintState state) => state switch
    {
        MintState.AwaitingPayment => "awaiting_payment",
        MintState.Minting         => "minting",
        MintState.Minted          => "minted",
        MintState.Failed          => "failed",
        MintState.RefundedCredit  => "refunded_credit",
        _                         => "minting"
    };

    private static long FeeCapFor(long totalSats)
        => totalSats + Math.Max(totalSats * FeeBufferPercent / 100, FeeBufferFloorSats);

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

    private static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";

        var sb = new StringBuilder(input.Length);
        var lastHyphen = false;
        foreach (var ch in input.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastHyphen = false; }
            else if (!lastHyphen) { sb.Append('-'); lastHyphen = true; }
        }

        return sb.ToString().Trim('-');
    }

    private static string MapError(Exception ex, string action) => ex switch
    {
        SelfCustodyMintNotAvailableException => ex.Message,
        HostedFeatureNotAvailableException => ex.Message,
        ManagedWalletApiException api => api.Code switch
        {
            ManagedWalletErrorCode.QuoteExpired        => "The price quote expired — please try again.",
            ManagedWalletErrorCode.QuoteNotFound       => "That quote is no longer valid — please start again.",
            ManagedWalletErrorCode.FeeTooHigh          => "The on-chain fee moved above the allowed cap. Please try again in a moment.",
            ManagedWalletErrorCode.ImageFetchFailed    => "We couldn't fetch that image URL — check it's public and reachable.",
            ManagedWalletErrorCode.ImageTooLarge       => "That image is too large (maximum 10 MB).",
            ManagedWalletErrorCode.CollectionFull      => "This collection is full. Pick another name or a larger collection.",
            ManagedWalletErrorCode.SupplyExceeded      => "That exceeds the collection's remaining supply.",
            ManagedWalletErrorCode.MintFailed          => "The batch mint failed after payment — you were refunded as credit. Please try again.",
            ManagedWalletErrorCode.TapdUnavailable     => "The minting service is busy right now. Please try again shortly.",
            ManagedWalletErrorCode.IdempotencyConflict => "A conflicting batch is already in progress. Please retry.",
            ManagedWalletErrorCode.IdempotencyInFlight => "This batch is already being processed. Please wait a moment and check its status.",
            ManagedWalletErrorCode.InsufficientScope   => "This connection can't mint. Re-issue its token with the assets:mint scope.",
            ManagedWalletErrorCode.InvalidRequest      => string.IsNullOrWhiteSpace(api.Message)
                                                             ? "The batch request was rejected as invalid — check the fields and try again."
                                                             : $"The batch request was rejected: {api.Message}",
            ManagedWalletErrorCode.Unauthorized        => "The hosted connection was rejected. Check the token in Settings.",
            ManagedWalletErrorCode.RateLimited         => "Too many requests right now. Please wait a moment and try again.",
            _                                          => $"Could not {action}: {api.Message}"
        },
        _ => $"Could not {action}: {ex.Message}"
    };
}

public class SmvBatchVm
{
    public string? Error { get; set; }
    public bool Disabled { get; set; }
    public bool NotConfigured { get; set; }
    public string? ConnectionLabel { get; set; }

    // Form fields.
    public string SeriesName { get; set; } = "";           // template.name — units are "<series> #0001…"
    public string CollectionName { get; set; } = "";       // optional; defaults to SeriesName
    public long UnitCount { get; set; } = 100;
    public long CollectionTotalSupply { get; set; }        // optional; defaults to max(UnitCount)
    public string ImageUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public string AttributesText { get; set; } = "";

    // Per-unit cost shape (the exact aggregate is on the invoice).
    public MintQuote? UnitQuote { get; set; }
    public bool QuoteUnavailable { get; set; }

    // The submitted batch: pay the inline LN invoice; batch.js polls to completion.
    public MintBatchResult? Result { get; set; }
}
