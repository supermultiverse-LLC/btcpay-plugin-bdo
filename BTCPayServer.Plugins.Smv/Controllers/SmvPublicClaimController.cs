using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Plugins.Smv.Services.OAuth;
using BTCPayServer.Plugins.Smv.Settings;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// RFC-PLUGIN-009: the PUBLIC claim page — white-label BDO delivery on the
/// merchant's own domain. Anonymous by design (the recipient is the merchant's
/// END CUSTOMER, who has no BTCPay account): the page shows the BDO card for a
/// claim code, proves the recipient's email with the v0.14.0 one-time-code
/// machinery, and executes the claim with the recipient's own JWT. Stateless
/// full-page POSTs; the JWT never outlives the verify-and-claim request.
/// </summary>
[AllowAnonymous]
[Route("plugins/smv/claim")]
public class SmvPublicClaimController : Controller
{
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(15);
    // Local throttle so a merchant's public page can't be used as an OTP cannon.
    // The platform's own GoTrue/RPC rate limits remain the authoritative wall.
    private const int MaxPostsPerWindow = 10;
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(10);

    private readonly StoreRepository _stores;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SmvPublicClaimController> _log;

    public SmvPublicClaimController(
        StoreRepository stores,
        ISettingsRepositoryAccessor serverSettings,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<SmvPublicClaimController> log)
    {
        _stores = stores;
        _serverSettings = serverSettings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _log = log;
    }

    [HttpGet("{storeId}")]
    public async Task<IActionResult> Index(string storeId, string? code, CancellationToken ct)
    {
        var vm = await BaseVmAsync(storeId, ct);
        if (vm is null) return NotFound();

        code = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(code))
        {
            vm.Stage = PublicClaimStage.Enter;
            return View("Index", vm);
        }
        await FillCardAsync(vm, code, ct);
        return View("Index", vm);
    }

    [HttpPost("{storeId}/send-code")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendCode(string storeId, string code, string email, bool tosAccepted, CancellationToken ct)
    {
        var vm = await BaseVmAsync(storeId, ct);
        if (vm is null) return NotFound();
        if (Throttled()) return TooMany(vm, code);

        code = NormalizeCode(code) ?? "";
        email = (email ?? "").Trim();
        await FillCardAsync(vm, code, ct);
        if (vm.Stage != PublicClaimStage.Card) return View("Index", vm);

        if (string.IsNullOrWhiteSpace(email))
        {
            vm.Error = "Enter your email.";
            return View("Index", vm);
        }
        if (!tosAccepted)
        {
            vm.Error = "Please accept the Terms of Service to continue.";
            return View("Index", vm);
        }

        try
        {
            await (await BuildOAuthClientAsync()).RequestEmailCodeAsync(email, tosAccepted: true, ct);
            vm.Stage = PublicClaimStage.Otp;
            vm.Email = email;
            vm.Notice = $"We emailed a 6-digit code to {email}. Enter it below to claim.";
        }
        catch (SmvOAuthException ex)
        {
            _log.LogInformation("public_claim.otp_request_failed store={StoreId} status={Status}", storeId, ex.StatusCode);
            vm.Error = ex.StatusCode == 429
                ? "Too many codes requested. Please wait a minute and try again."
                : "Could not send the code. Please try again.";
        }
        return View("Index", vm);
    }

    [HttpPost("{storeId}/claim")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(string storeId, string code, string email, string otp, CancellationToken ct)
    {
        var vm = await BaseVmAsync(storeId, ct);
        if (vm is null) return NotFound();
        if (Throttled()) return TooMany(vm, code);

        code = NormalizeCode(code) ?? "";
        email = (email ?? "").Trim();
        otp = (otp ?? "").Trim();
        await FillCardAsync(vm, code, ct);
        if (vm.Stage != PublicClaimStage.Card) return View("Index", vm);

        vm.Stage = PublicClaimStage.Otp;
        vm.Email = email;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp))
        {
            vm.Error = "Enter the 6-digit code from the email.";
            return View("Index", vm);
        }

        try
        {
            var tokens = await (await BuildOAuthClientAsync()).VerifyEmailCodeAsync(email, otp, ct);
            var outcome = await (await BuildClaimClientAsync()).ExecuteAsync(code, tokens.AccessToken, ct);
            if (!outcome.Success)
            {
                _log.LogInformation("public_claim.execute_failed store={StoreId} code={Code}", storeId, outcome.ErrorCode);
                vm.Error = outcome.ErrorMessage ?? "The claim could not be completed. Please try again.";
                return View("Index", vm);
            }
            vm.Stage = PublicClaimStage.Done;
            _log.LogInformation("public_claim.claimed store={StoreId}", storeId);
        }
        catch (SmvOAuthException ex) when (ex.StatusCode is 400 or 401 or 403)
        {
            vm.Error = "That code is wrong or has expired — enter it again, or request a new one.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public_claim.unexpected store={StoreId}", storeId);
            vm.Error = "Something went wrong. Please try again.";
        }
        return View("Index", vm);
    }

    // ── Drops (RFC-PLUGIN-010): one URL/QR dispenses a series ───────────────
    // Same anonymous page and email→code flow as a single claim; the claim
    // step atomically receives the NEXT available unit instead of a fixed one.

    [HttpGet("/plugins/smv/drop/{storeId}/{campaignId}")]
    public async Task<IActionResult> Drop(string storeId, string campaignId, CancellationToken ct)
    {
        var vm = await BaseVmAsync(storeId, ct);
        if (vm is null) return NotFound();
        await FillDropAsync(vm, campaignId, ct);
        return View("Index", vm);
    }

    [HttpPost("/plugins/smv/drop/{storeId}/{campaignId}/send-code")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DropSendCode(string storeId, string campaignId, string email, bool tosAccepted, CancellationToken ct)
    {
        var vm = await BaseVmAsync(storeId, ct);
        if (vm is null) return NotFound();
        if (Throttled(DropMaxPostsPerWindow)) { await FillDropAsync(vm, campaignId, ct); vm.Error = "Too many attempts from this connection. Please wait a few minutes and try again."; return View("Index", vm); }

        email = (email ?? "").Trim();
        await FillDropAsync(vm, campaignId, ct);
        if (vm.Stage != PublicClaimStage.Card) return View("Index", vm);
        if (string.IsNullOrWhiteSpace(email)) { vm.Error = "Enter your email."; return View("Index", vm); }
        if (!tosAccepted) { vm.Error = "Please accept the Terms of Service to continue."; return View("Index", vm); }

        try
        {
            await (await BuildOAuthClientAsync()).RequestEmailCodeAsync(email, tosAccepted: true, ct);
            vm.Stage = PublicClaimStage.Otp;
            vm.Email = email;
            vm.Notice = $"We emailed a 6-digit code to {email}. Enter it below to claim yours.";
        }
        catch (SmvOAuthException ex)
        {
            _log.LogInformation("public_drop.otp_request_failed store={StoreId} status={Status}", storeId, ex.StatusCode);
            vm.Error = ex.StatusCode == 429
                ? "Too many codes requested. Please wait a minute and try again."
                : "Could not send the code. Please try again.";
        }
        return View("Index", vm);
    }

    [HttpPost("/plugins/smv/drop/{storeId}/{campaignId}/claim")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DropClaim(string storeId, string campaignId, string email, string otp, CancellationToken ct)
    {
        var vm = await BaseVmAsync(storeId, ct);
        if (vm is null) return NotFound();
        if (Throttled(DropMaxPostsPerWindow)) { await FillDropAsync(vm, campaignId, ct); vm.Error = "Too many attempts from this connection. Please wait a few minutes and try again."; return View("Index", vm); }

        email = (email ?? "").Trim();
        otp = (otp ?? "").Trim();
        await FillDropAsync(vm, campaignId, ct);
        if (vm.Stage != PublicClaimStage.Card) return View("Index", vm);

        vm.Stage = PublicClaimStage.Otp;
        vm.Email = email;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otp))
        {
            vm.Error = "Enter the 6-digit code from the email.";
            return View("Index", vm);
        }

        try
        {
            var tokens = await (await BuildOAuthClientAsync()).VerifyEmailCodeAsync(email, otp, ct);
            var outcome = await (await BuildClaimClientAsync()).ClaimNextAsync(campaignId, tokens.AccessToken, ct);
            if (!outcome.Success)
            {
                _log.LogInformation("public_drop.claim_failed store={StoreId} code={Code}", storeId, outcome.ErrorCode);
                vm.Error = outcome.ErrorCode switch
                {
                    "ALREADY_CLAIMED_YOURS" => "This account already claimed its unit from this drop.",
                    "DROP_EXHAUSTED" => "All units have been claimed — this drop is complete.",
                    "DROP_CLOSED" => "This drop has been closed by the issuer.",
                    _ => outcome.ErrorMessage ?? "The claim could not be completed. Please try again.",
                };
                return View("Index", vm);
            }
            vm.Stage = PublicClaimStage.Done;
            vm.AssetName = outcome.AssetName ?? vm.AssetName;
            _log.LogInformation("public_drop.claimed store={StoreId} campaign={CampaignId}", storeId, campaignId);
        }
        catch (SmvOAuthException ex) when (ex.StatusCode is 400 or 401 or 403)
        {
            vm.Error = "That code is wrong or has expired — enter it again, or request a new one.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public_drop.unexpected store={StoreId}", storeId);
            vm.Error = "Something went wrong. Please try again.";
        }
        return View("Index", vm);
    }

    private async Task FillDropAsync(PublicClaimViewModel vm, string campaignId, CancellationToken ct)
    {
        vm.CampaignId = campaignId;
        try
        {
            var c = await (await BuildClaimClientAsync()).LookupCampaignAsync(campaignId, ct);
            if (c is null)
            {
                vm.Stage = PublicClaimStage.Enter;
                vm.Error = "This drop does not exist.";
                return;
            }
            vm.DropName = c.Name;
            vm.DropTotal = c.Total;
            vm.DropClaimed = c.Claimed;
            vm.AssetName = c.AssetName;
            vm.AssetImageUrl = c.AssetImageUrl;
            vm.CollectionName = c.CollectionName;
            vm.IssuerName = c.IssuerName;
            if (!string.Equals(c.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                vm.Stage = PublicClaimStage.Enter;
                vm.Error = "This drop has been closed by the issuer.";
                return;
            }
            if (c.Claimed >= c.Total && c.Total > 0)
            {
                vm.Stage = PublicClaimStage.Enter;
                vm.Error = "All units have been claimed — this drop is complete.";
                return;
            }
            vm.Stage = PublicClaimStage.Card;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public_drop.lookup_failed");
            vm.Stage = PublicClaimStage.Enter;
            vm.Error = "Something went wrong. Please try again.";
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<PublicClaimViewModel?> BaseVmAsync(string storeId, CancellationToken ct)
    {
        var store = await _stores.FindStore(storeId);
        if (store is null) return null;
        return new PublicClaimViewModel
        {
            StoreId = storeId,
            StoreName = string.IsNullOrWhiteSpace(store.StoreName) ? "This store" : store.StoreName,
        };
    }

    private async Task FillCardAsync(PublicClaimViewModel vm, string code, CancellationToken ct)
    {
        vm.Code = code;
        if (string.IsNullOrWhiteSpace(code))
        {
            vm.Stage = PublicClaimStage.Enter;
            return;
        }
        try
        {
            var entry = await (await BuildClaimClientAsync()).LookupAsync(code, ct);
            if (entry is null)
            {
                vm.Stage = PublicClaimStage.Enter;
                vm.Error = "Claim code not found. Please check and try again.";
                return;
            }
            if (string.Equals(entry.Status, "claimed", StringComparison.OrdinalIgnoreCase))
            {
                vm.Stage = PublicClaimStage.Enter;
                vm.Error = "This code has already been claimed.";
                return;
            }
            if (string.Equals(entry.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                vm.Stage = PublicClaimStage.Enter;
                vm.Error = "This claim code has been cancelled by the issuer.";
                return;
            }
            vm.Stage = PublicClaimStage.Card;
            vm.AssetName = entry.AssetName;
            vm.AssetDescription = entry.AssetDescription;
            vm.AssetImageUrl = entry.AssetImageUrl;
            vm.CollectionName = entry.CollectionName;
            vm.IssuerName = entry.IssuerName;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "public_claim.lookup_failed");
            vm.Stage = PublicClaimStage.Enter;
            vm.Error = "Something went wrong. Please try again.";
        }
    }

    private async Task<SmvOAuthClient> BuildOAuthClientAsync()
    {
        var server = await _serverSettings.GetAsync();
        var http = _httpClientFactory.CreateClient("smv-oauth");
        http.Timeout = FlowTimeout;
        return new SmvOAuthClient(http, server.OAuthIssuerBase, server.HostedApiBase, server.SupabaseAnonKey);
    }

    private async Task<SmvClaimPublicClient> BuildClaimClientAsync()
    {
        var server = await _serverSettings.GetAsync();
        var http = _httpClientFactory.CreateClient("smv-public");
        http.Timeout = FlowTimeout;
        return new SmvClaimPublicClient(http, server);
    }

    private static string? NormalizeCode(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    private bool Throttled(int maxPerWindow = MaxPostsPerWindow)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var key = $"smv-public-claim:{ip}";
        var count = _cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = ThrottleWindow;
            return 0;
        });
        if (count >= maxPerWindow) return true;
        _cache.Set(key, count + 1, ThrottleWindow);
        return false;
    }

    // Live events share ONE venue IP (audit 2026-07-27): the single-claim
    // budget of 10/10min would wall off an audience after ~5 people. Drops
    // get room for ~150 people per IP per window — the real anti-abuse is
    // one-per-account + single-use OTP + the platform's own rate limits.
    private const int DropMaxPostsPerWindow = 300;

    private IActionResult TooMany(PublicClaimViewModel vm, string? code)
    {
        vm.Code = NormalizeCode(code);
        vm.Stage = string.IsNullOrWhiteSpace(vm.Code) ? PublicClaimStage.Enter : PublicClaimStage.Card;
        vm.Error = "Too many attempts from this connection. Please wait a few minutes and try again.";
        return View("Index", vm);
    }
}

public enum PublicClaimStage { Enter, Card, Otp, Done }

public class PublicClaimViewModel
{
    public string StoreId { get; set; } = "";
    public string StoreName { get; set; } = "";
    public string? Code { get; set; }
    // Drop mode (RFC-PLUGIN-010): set when the page serves a campaign URL.
    public string? CampaignId { get; set; }
    public string? DropName { get; set; }
    public long DropTotal { get; set; }
    public long DropClaimed { get; set; }
    public string? Email { get; set; }
    public PublicClaimStage Stage { get; set; } = PublicClaimStage.Enter;
    public string? Error { get; set; }
    public string? Notice { get; set; }
    public string? AssetName { get; set; }
    public string? AssetDescription { get; set; }
    public string? AssetImageUrl { get; set; }
    public string? CollectionName { get; set; }
    public string? IssuerName { get; set; }
}
