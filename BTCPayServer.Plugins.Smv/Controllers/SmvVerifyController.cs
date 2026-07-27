using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Smv.Models;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("plugins/smv/verify")]
public class SmvVerifyController : Controller
{
    private static readonly Regex UuidRx =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Hex64Rx =
        new(@"^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SmvPublicApiClient _api;

    public SmvVerifyController(SmvPublicApiClient api)
    {
        _api = api;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View("Verify", new VerifyVm());
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup(string id, CancellationToken ct)
    {
        var vm = new VerifyVm { Query = id ?? "" };
        var q = (id ?? "").Trim();

        if (q.Length == 0)
        {
            vm.Error = "Enter an asset_id (UUID) or tapd_asset_id (64-hex).";
            return View("Verify", vm);
        }

        if (!UuidRx.IsMatch(q) && !Hex64Rx.IsMatch(q))
        {
            vm.Error = "Invalid format. Expected UUID v4 or 64-char hex.";
            return View("Verify", vm);
        }

        try
        {
            vm.Collectible = await _api.GetCollectibleAsync(q, ct);
        }
        catch (SmvApiException ex)
        {
            vm.Error = MapError(ex);
            vm.RetryAfter = ex.RetryAfterSeconds;
            vm.IsBlockingProofWarning = ex.Kind == SmvApiErrorKind.ProofCorrupted;
        }
        catch
        {
            vm.Error = "Service unreachable, retry later.";
        }

        return View("Verify", vm);
    }

    [HttpPost("sha256")]
    [IgnoreAntiforgeryToken] // read-only verification (fetches proof, computes SHA-256); no state mutation
    public async Task<IActionResult> VerifySha256(string id, CancellationToken ct)
    {
        var q = (id ?? "").Trim();
        var vm = new VerifyVm { Query = q };

        if (!UuidRx.IsMatch(q) && !Hex64Rx.IsMatch(q))
        {
            vm.Error = "Invalid id format.";
            return View("Verify", vm);
        }

        try
        {
            vm.Collectible = await _api.GetCollectibleAsync(q, ct);

            var (bytes, declared) = await _api.GetProofRawAsync(q, ct);
            var computed = ProofIntegrityService.Sha256Hex(bytes);
            var match = declared is not null && ProofIntegrityService.Matches(declared, computed);

            vm.Sha256Result = new Sha256VerifyVm
            {
                Match = match,
                Computed = computed,
                Declared = declared,
                Size = bytes.Length
            };
        }
        catch (SmvApiException ex)
        {
            vm.Error = MapError(ex);
            vm.RetryAfter = ex.RetryAfterSeconds;
            vm.IsBlockingProofWarning = ex.Kind == SmvApiErrorKind.ProofCorrupted;
        }
        catch
        {
            vm.Error = "Service unreachable, retry later.";
        }

        return View("Verify", vm);
    }

    private static string MapError(SmvApiException ex)
    {
        return ex.Kind switch
        {
            SmvApiErrorKind.InvalidId => "This ID was not found in the Bitcoin Digital Objects verification API.",
            SmvApiErrorKind.NotVerifiable => "Not a verifiable Bitcoin Digital Object (BDO).",
            SmvApiErrorKind.RateLimited => $"Rate limited. Retry in {ex.RetryAfterSeconds ?? 60}s.",
            SmvApiErrorKind.ProofCorrupted => "Proof integrity check failed at source. Do not trust this asset.",
            SmvApiErrorKind.ProofUnavailable => "Proof temporarily unavailable, retry later.",
            SmvApiErrorKind.ProofTooLarge => "Proof exceeds local size limit.",
            SmvApiErrorKind.Timeout => "Upstream timeout, retry later.",
            _ => "Service unreachable, retry later."
        };
    }
}

public class VerifyVm
{
    public string Query { get; set; } = "";
    public CollectibleResponse? Collectible { get; set; }
    public string? Error { get; set; }
    public int? RetryAfter { get; set; }
    public bool IsBlockingProofWarning { get; set; }
    public Sha256VerifyVm? Sha256Result { get; set; }
}

public class Sha256VerifyVm
{
    public bool Match { get; set; }
    public string Computed { get; set; } = "";
    public string? Declared { get; set; }
    public int Size { get; set; }
}