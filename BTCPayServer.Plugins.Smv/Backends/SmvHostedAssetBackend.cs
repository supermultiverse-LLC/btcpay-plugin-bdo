using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Core;
using BTCPayServer.Plugins.Smv.Services;

namespace BTCPayServer.Plugins.Smv.Backends;

/// <summary>
/// Hosted (custodial) implementation of <see cref="IAssetBackend"/> (Plugin-P3),
/// speaking the Managed Wallet API v1.1 instead of tapd. H1a implements the read
/// paths — wallet info and holdings list with best-effort enrichment. Receive is
/// deferred to API v1.2 (RFC-PLUGIN-003 §10) and Send arrives in H3; both throw a
/// typed <see cref="HostedFeatureNotAvailableException"/> for now.
///
/// Owns the per-request <see cref="HttpClient"/> and disposes it, exactly like
/// <c>TapdAssetBackend</c>. The shared <see cref="SmvPublicApiClient"/> (enrichment)
/// is owned by DI and must NOT be disposed here.
/// </summary>
public sealed class SmvHostedAssetBackend : IAssetBackend
{
    // Bound the enrichment N+1 fan-out so a large wallet never bursts the Public
    // Verification API rate limit (60 requests / 60s per IP; contract §5.2).
    private const int EnrichmentConcurrency = 4;

    private readonly ManagedWalletClient _client;
    private readonly HttpClient _httpClient;
    private readonly SmvPublicApiClient _publicApi;

    public SmvHostedAssetBackend(ManagedWalletClient client, HttpClient httpClient, SmvPublicApiClient publicApi)
    {
        _client = client;
        _httpClient = httpClient;
        _publicApi = publicApi;
    }

    public string? ConnectionLabel => "Supermultiverse (Hosted)";

    public bool IsCustodial => true; // Hosted: Supermultiverse custodies the keys.

    public async Task<BackendInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var wallet = await _client.GetWalletAsync(cancellationToken);
        // Version carries the custodian for the "custodied by" surface; Network is
        // the wallet's network. Reaching this point means the token authenticated,
        // so Connected = true. CreditBalanceSats (v1.2 §3) surfaces spendable mint
        // credit; 0 on a v1.1 backend that omits the field.
        return new BackendInfo(
            Network: wallet.Network,
            Version: wallet.Custodian,
            Connected: true,
            CreditBalanceSats: wallet.CreditBalanceSats);
    }

    public async Task<IReadOnlyList<OwnedAsset>> ListAssetsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _client.ListAssetsAsync(cancellationToken);

        // Defensive (belt-and-suspenders): drop holdings with no Taproot Assets id.
        // Contract §5.2 says these are excluded by the backend (a null asset_id is
        // inert for Verify/Send), but we filter client-side too so a future backend
        // regression can never surface an unusable, un-verifiable row in My BDOs.
        var usable = items.Where(i => !string.IsNullOrWhiteSpace(i.AssetId));

        // Enrich concurrently but bounded. Task ordering is preserved by Select +
        // WhenAll, so the returned list matches the API's holding order.
        using var gate = new SemaphoreSlim(EnrichmentConcurrency);

        var tasks = usable.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await EnrichAsync(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private async Task<OwnedAsset> EnrichAsync(ManagedAssetItem item, CancellationToken ct)
    {
        string? name = null, imageUrl = null, collection = null, collectionSlug = null, ipfsUrl = null, ipfsCid = null;

        // Enrichment is best-effort: a rate-limit, a missing collectible, or any
        // upstream hiccup must NOT drop the holding. Core fields (asset_id, smv_id,
        // amount) always come through from the wallet API itself.
        if (!string.IsNullOrWhiteSpace(item.SmvId))
        {
            try
            {
                var c = await _publicApi.GetCollectibleAsync(item.SmvId!, ct);
                name = c.Name;
                imageUrl = c.ImageUrl;
                collection = c.Collection?.Name;
                collectionSlug = c.Collection?.Slug;
                ipfsUrl = c.ImageIpfsUrl;
                ipfsCid = c.ImageIpfsCid;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine cancellation propagates; a transient enrich error does not
            }
            catch
            {
                // best-effort: leave display fields null, still list the holding
            }
        }

        return new OwnedAsset(
            AssetId: item.AssetId,
            SmvId: item.SmvId,
            Name: name,
            Type: null,
            Amount: item.Amount,
            AnchorOutpoint: item.AnchorOutpoint,
            ImageUrl: imageUrl,
            Collection: collection,
            CollectionSlug: collectionSlug,
            ImageIpfsUrl: ipfsUrl,
            ImageIpfsCid: ipfsCid);
    }

    // transferRef is the Managed Wallet transfer_ref (UUID). Maps the sealed status
    // domain (contract §5.5) to the neutral SendStatus. Not reached until Hosted
    // Send is wired in H3; implemented here so the interface is complete.
    public async Task<SendStatus> GetSendStatusAsync(string transferRef, CancellationToken cancellationToken = default)
    {
        var t = await _client.GetTransferStatusAsync(transferRef, cancellationToken);
        var status = t.Status ?? "pending_payment";

        var (broadcasted, message) = status switch
        {
            "pending_payment" => (false, "Waiting for the Lightning fee invoice to be paid."),
            "paid"            => (false, "Fee paid. Preparing to broadcast the transfer."),
            "broadcasting"    => (true,  "Broadcasting the transfer on-chain."),
            "fulfilled"       => (true,  "Transfer fulfilled."),
            "failed"          => (false, t.ErrorMessage ?? "Transfer failed."),
            "cancelled"       => (false, "Transfer cancelled (the fee invoice expired)."),
            _                 => (false, "Transfer status unknown.")
        };

        return new SendStatus(
            State: status,
            Ref: t.TransferRef ?? transferRef,
            Broadcasted: broadcasted,
            Message: message);
    }

    public Task<IReadOnlyList<PendingIncomingAsset>> ListPendingIncomingAsync(CancellationToken cancellationToken = default)
        // No pending-deposit list exists in v1.1 (pending-incoming is a Receive
        // concept, deferred with Receive — RFC §10).
        => Task.FromResult<IReadOnlyList<PendingIncomingAsset>>(Array.Empty<PendingIncomingAsset>());

    public async Task<ReceiveAddress> CreateReceiveAddressAsync(ReceiveRequest request, CancellationToken cancellationToken = default)
    {
        // v1.2.1: the SMV node mints a fresh single-use address for the asset; inbound
        // payment lands in this token's custodial wallet via the standard holdings
        // projection. tapd-only extras (asset type/version, proof courier) stay null.
        var amount = int.TryParse(request.Amount, out var a) && a >= 1 ? a : 1;
        var resp = await _client.CreateReceiveAddressAsync(
            new ManagedReceiveAddressRequest { AssetId = request.AssetId, Amount = amount },
            cancellationToken);
        // Echo the endpoint's asset_id: tapd emits the address against the exact requested
        // leaf (never substitutes, even within a group), and the backend normalizes the
        // field to clean 64-hex (falling back to the requested id if unparseable).
        return new ReceiveAddress(
            Encoded: resp.Address,
            AssetId: resp.AssetId,
            Amount: resp.Amount.ToString());
    }

    public async Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AssetId))
            throw new ManagedWalletApiException(ManagedWalletErrorCode.InvalidRequest, 400, "asset_id is required for a hosted send.");
        if (string.IsNullOrWhiteSpace(request.Amount))
            throw new ManagedWalletApiException(ManagedWalletErrorCode.InvalidRequest, 400, "amount is required for a hosted send.");

        var body = new ManagedSendRequest
        {
            AssetId = request.AssetId,
            Amount = request.Amount,
            DestinationAddress = request.DestinationAddress
        };

        // Fresh Idempotency-Key per send ATTEMPT (RFC-PLUGIN-003 §9). The client does
        // not auto-retry the POST, so any retry is user-initiated — a new attempt with
        // a new key. On payment_required (402) the caller likewise retries, which mints
        // a fresh key here rather than replaying the expired invoice.
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var r = await _client.SendAsync(body, idempotencyKey, cancellationToken);

        LnInvoice? payment = r.Payment is { Invoice: not null } p
            ? new LnInvoice(p.Invoice!, PaymentHash: string.Empty, p.AmountSats, p.ExpiresAt)
            : null;

        return new SendResult(
            TransferRef: r.TransferRef,
            State: MapSendState(r.Status),
            Txid: r.Txid,
            Payment: payment,
            ProviderState: r.Status,
            RawJson: null);
    }

    private static SendState MapSendState(string? status) => status switch
    {
        "pending_payment"        => SendState.PaymentRequired,
        "paid" or "broadcasting" => SendState.Pending,
        "fulfilled"              => SendState.Fulfilled,
        "failed" or "cancelled"  => SendState.Failed,
        _                        => SendState.Pending
    };

    // ── Issuance API v1.2 (contract §4, §5) ────────────────────────────────────

    public async Task<IReadOnlyList<MintCollection>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _client.ListCollectionsAsync(cancellationToken);
        return items
            .Select(c => new MintCollection(
                Id: c.Id,
                Name: c.Name,
                Slug: c.Slug,
                TotalSupply: c.TotalSupply,
                MintedCount: c.MintedCount,
                RemainingSupply: c.RemainingSupply,
                ImageUrl: c.ImageUrl))
            .ToList();
    }

    public async Task<MintQuote> MintQuoteAsync(MintQuoteRequest request, CancellationToken cancellationToken = default)
    {
        var body = new ManagedMintQuoteRequest
        {
            Asset = new ManagedMintQuoteAsset
            {
                Supply = request.Supply,
                Divisibility = request.Divisibility,
                AssetType = request.AssetType
            }
        };

        var r = await _client.MintQuoteAsync(body, cancellationToken);

        if (r.Estimate is not { } e)
        {
            throw new ManagedWalletApiException(
                ManagedWalletErrorCode.ServerError, 502,
                "The Managed Wallet API returned a mint quote with no estimate.");
        }

        return new MintQuote(
            OnchainFeeSats: e.OnchainFeeSats,
            PlatformMarginSats: e.PlatformMarginSats,
            TotalSats: e.TotalSats,
            FeeRateSatPerVb: e.FeeRateSatPerVb,
            Network: e.Network,
            Note: r.Note,
            BatchOnchainFeeSats: r.Batch?.OnchainFeeSats);
    }

    public async Task<MintResult> MintAsync(MintRequest request, CancellationToken cancellationToken = default)
    {
        var body = new ManagedMintRequest
        {
            Collection = new ManagedMintCollectionRequest
            {
                Mode = "create_or_reuse",
                Name = request.CollectionName,
                Slug = request.CollectionSlug,
                TotalSupply = request.CollectionTotalSupply,
                ImageUrl = request.CollectionImageUrl
            },
            Asset = new ManagedMintAssetRequest
            {
                Name = request.AssetName,
                Supply = request.Supply,
                Divisibility = request.Divisibility,
                AssetType = request.AssetType,
                ImageUrl = request.AssetImageUrl,
                Description = request.Description,
                Attributes = request.Attributes?
                    .Select(a => new ManagedMintAttribute { TraitType = a.TraitType, Value = a.Value })
                    .ToList(),
                ExternalReference = request.ExternalReference
            },
            Billing = new ManagedMintBilling
            {
                AcceptFeeQuoteUpToSats = request.AcceptFeeQuoteUpToSats
            }
        };

        // Fresh Idempotency-Key per mint ATTEMPT (contract §8): a transport retry of
        // the same attempt replays the cached 202, never a second invoice/asset. A
        // user-initiated re-quote after expiry is a NEW attempt with a new key.
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var r = await _client.MintAsync(body, idempotencyKey, cancellationToken);

        // The LN fee invoice is returned inline in the 202 (contract §5.2), unlike a
        // polled flow — the UI reuses the P3 invoice/QR render on it directly.
        LnInvoice? invoice = r.Invoice is { Bolt11: not null } inv
            ? new LnInvoice(inv.Bolt11!, inv.PaymentHash ?? string.Empty, inv.AmountSats, inv.ExpiresAt)
            : null;

        var paidWithCredits = string.Equals(r.Payment?.Method, "credits", StringComparison.OrdinalIgnoreCase);
        return new MintResult(
            MintRef: r.MintRef,
            State: MapMintState(r.Status),
            Invoice: invoice,
            CollectionId: r.Collection?.Id,
            CollectionCreated: r.Collection?.Created ?? false,
            PollUrl: r.PollUrl,
            ProviderState: r.Status,
            CreditsCharged: paidWithCredits ? r.Payment!.ChargedSats : null,
            CreditsBalanceAfter: paidWithCredits ? r.Payment!.BalanceAfterSats : null);
    }

    public async Task<MintStatus> GetMintStatusAsync(string mintRef, CancellationToken cancellationToken = default)
    {
        var s = await _client.GetMintStatusAsync(mintRef, cancellationToken);
        var state = MapMintState(s.Status);

        var message = state switch
        {
            MintState.AwaitingPayment => "Waiting for the Lightning fee to be paid.",
            MintState.Minting         => "Payment received — minting your BDO…",
            MintState.Minted          => "Your BDO has been minted.",
            MintState.RefundedCredit  => $"Minting failed — you were refunded {s.Refund?.CreditSats ?? 0} sats as credit.",
            MintState.Failed          => s.Error?.Message ?? "Minting failed.",
            _                         => "Minting in progress…"
        };

        return new MintStatus(
            State: state,
            MintRef: s.MintRef ?? mintRef,
            Message: message,
            InvoiceStatus: s.InvoiceStatus,
            BdoId: s.Asset?.BdoId,
            SmvId: s.Asset?.SmvId,
            AnchorOutpoint: s.Asset?.AnchorOutpoint,
            ProofUrl: s.Asset?.ProofUrl,
            CollectionName: s.Collection?.Name,
            ErrorCode: s.Error?.Code,
            ErrorMessage: s.Error?.Message,
            RefundCreditSats: s.Refund?.CreditSats ?? 0,
            ProviderState: s.Status);
    }

    // ── Batch mint (RFC_BATCH_MINTING_V1, Modality 3 — the moat) ────────────────

    public async Task<MintBatchResult> MintBatchAsync(MintBatchRequest request, CancellationToken cancellationToken = default)
    {
        var body = new ManagedMintBatchRequest
        {
            Collection = new ManagedMintCollectionRequest
            {
                Mode = "create_or_reuse",
                Name = request.CollectionName,
                Slug = request.CollectionSlug,
                TotalSupply = request.CollectionTotalSupply,
                ImageUrl = request.CollectionImageUrl
            },
            Template = new ManagedMintBatchTemplate
            {
                Name = request.TemplateName,
                ImageUrl = request.ImageUrl,
                Description = request.Description,
                Attributes = request.Attributes?
                    .Select(a => new ManagedMintAttribute { TraitType = a.TraitType, Value = a.Value })
                    .ToList()
            },
            UnitCount = request.UnitCount,
            Billing = new ManagedMintBilling { AcceptFeeQuoteUpToSats = request.AcceptFeeQuoteUpToSats }
        };

        // Fresh Idempotency-Key per submit ATTEMPT (contract §8) — a transport retry
        // replays the cached 202, never a second batch/invoice.
        var idempotencyKey = Guid.NewGuid().ToString("N");
        var r = await _client.MintBatchAsync(body, idempotencyKey, cancellationToken);

        LnInvoice? invoice = r.Invoice is { Bolt11: not null } inv
            ? new LnInvoice(inv.Bolt11!, inv.PaymentHash ?? string.Empty, inv.AmountSats, inv.ExpiresAt)
            : null;

        var batchPaidWithCredits = string.Equals(r.Payment?.Method, "credits", StringComparison.OrdinalIgnoreCase);
        return new MintBatchResult(
            BatchRef: r.BatchRef,
            State: MapBatchState(r.Status),
            Invoice: invoice,
            CollectionId: r.Collection?.Id,
            CollectionCreated: r.Collection?.Created ?? false,
            ProviderState: r.Status,
            CreditsCharged: batchPaidWithCredits ? r.Payment!.ChargedSats : null,
            CreditsBalanceAfter: batchPaidWithCredits ? r.Payment!.BalanceAfterSats : null);
    }

    public async Task<MintBatchStatus> GetMintBatchStatusAsync(string batchRef, CancellationToken cancellationToken = default)
    {
        var s = await _client.GetMintBatchStatusAsync(batchRef, cancellationToken);
        var state = MapBatchState(s.Status);
        var minted = s.Progress?.Minted ?? 0;
        var total = s.Progress?.Total ?? 0;

        var message = state switch
        {
            MintState.AwaitingPayment => "Waiting for the Lightning fee to be paid.",
            MintState.Minting         => "Minting your batch…",
            MintState.Minted          => $"Your batch of {total} BDOs has been minted.",
            MintState.RefundedCredit  => $"Batch mint failed — you were refunded {s.Refund?.CreditSats ?? 0} sats as credit.",
            MintState.Failed          => s.Error?.Message ?? "Batch mint failed.",
            _                         => "Batch mint in progress…"
        };

        return new MintBatchStatus(
            State: state,
            BatchRef: s.BatchRef ?? batchRef,
            Message: message,
            Minted: minted,
            Total: total,
            InvoiceStatus: s.InvoiceStatus,
            CollectionName: s.Collection?.Name,
            CollectionSlug: s.Collection?.Slug,
            CollectionId: s.Collection?.Id,
            ErrorCode: s.Error?.Code,
            ErrorMessage: s.Error?.Message,
            RefundCreditSats: s.Refund?.CreditSats ?? 0,
            ProviderState: s.Status);
    }

    // Map the batch state machine to the neutral MintState. Unknown → non-terminal
    // (Minting) so polling continues, never a false terminal.
    private static MintState MapBatchState(string? status) => status switch
    {
        "draft" or "quoted" or "invoiced" or "awaiting_payment"  => MintState.AwaitingPayment,
        "paid" or "minting" or "broadcasting"                    => MintState.Minting,
        "confirmed" or "minted"                                  => MintState.Minted,
        "failed"                                                 => MintState.Failed,
        "refunded" or "refunded_credit"                          => MintState.RefundedCredit,
        _                                                        => MintState.Minting
    };

    // ── My BDOs listing Phase 2 (RFC-PLUGIN-005) — holdings-by-collection ───────

    public async Task<IReadOnlyList<HeldCollection>> ListHeldCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _client.ListHoldingsCollectionsAsync(cancellationToken);
        return items.Select(c => new HeldCollection(
            CollectionId: c.CollectionId,
            Slug: c.Slug,
            Name: c.Name,
            CoverImageUrl: c.CoverImageUrl,
            OwnedCount: c.OwnedCount,
            CollectionSize: c.CollectionSize,
            Modality: c.Modality,
            GroupKey: c.GroupKey,
            IssuerName: c.IssuerName)).ToList();
    }

    public async Task<HeldUnitsPage> ListHeldUnitsAsync(
        string collectionId, int? limit, string? cursor, string? q, string? sort,
        CancellationToken cancellationToken = default)
    {
        var resp = await _client.ListHoldingsUnitsAsync(collectionId, limit, cursor, q, sort, cancellationToken);
        var units = resp.Items.Select(u => new HeldUnit(
            Id: u.Id,
            AssetId: u.AssetId,
            Name: u.Name,
            ImageUrl: u.ImageUrl,
            BatchIndex: u.BatchIndex,
            SeriesId: u.SeriesId,
            SeriesName: u.SeriesName,
            AcquiredAt: u.AcquiredAt)).ToList();
        return new HeldUnitsPage(units, resp.NextCursor);
    }

    public async Task<IReadOnlyList<HeldGroup>> ListHeldGroupsAsync(
        string collectionId, CancellationToken cancellationToken = default)
    {
        var resp = await _client.ListHeldGroupsAsync(collectionId, cancellationToken);
        return resp.Groups
            .Where(g => !string.IsNullOrWhiteSpace(g.GroupId))
            .Select(g => new HeldGroup(
                GroupId: g.GroupId!,
                Name: string.IsNullOrWhiteSpace(g.Name) ? "Untitled" : g.Name!,
                Held: g.Held,
                Total: g.Total,
                ImageUrl: g.ImageUrl))
            .ToList();
    }

    public async Task<HeldUnitsPage> ListHeldUnitsInGroupAsync(
        string collectionId, string groupId, int? limit, string? cursor, string? q, string? sort,
        CancellationToken cancellationToken = default)
    {
        var resp = await _client.ListHoldingsUnitsAsync(
            collectionId, limit, cursor, q, sort, groupId, cancellationToken);
        var units = resp.Items.Select(u => new HeldUnit(
            Id: u.Id,
            AssetId: u.AssetId,
            Name: u.Name,
            ImageUrl: u.ImageUrl,
            BatchIndex: u.BatchIndex,
            SeriesId: u.SeriesId,
            SeriesName: u.SeriesName,
            AcquiredAt: u.AcquiredAt)).ToList();
        return new HeldUnitsPage(units, resp.NextCursor);
    }

    // Collapse the sealed mint state machine (contract §7) to the neutral MintState.
    // An unknown/unmapped status stays non-terminal (Minting) so polling continues —
    // the plugin never treats an unrecognised status as minted or failed.
    private static MintState MapMintState(string? status) => status switch
    {
        "quote_pending" or "awaiting_payment"                                  => MintState.AwaitingPayment,
        "minting" or "paying" or "preparing" or "broadcasting" or "confirming" => MintState.Minting,
        "minted"                                                    => MintState.Minted,
        "failed"                                                    => MintState.Failed,
        "refunded_credit"                                           => MintState.RefundedCredit,
        _                                                           => MintState.Minting
    };

    public void Dispose() => _httpClient.Dispose();
}
