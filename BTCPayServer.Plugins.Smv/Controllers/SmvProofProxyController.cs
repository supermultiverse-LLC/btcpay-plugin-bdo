using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("plugins/smv/proof")]
public class SmvProofProxyController : Controller
{
    private static readonly Regex UuidRx =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Hex64Rx =
        new(@"^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SmvPublicApiClient _api;
    public SmvProofProxyController(SmvPublicApiClient api) { _api = api; }

    [HttpGet("{id}")]
    public async Task<IActionResult> Download(string id, CancellationToken ct)
    {
        if (!UuidRx.IsMatch(id) && !Hex64Rx.IsMatch(id))
            return BadRequest("Invalid id format.");

        try
        {
            var (bytes, hash) = await _api.GetProofRawAsync(id, ct);
            if (!string.IsNullOrEmpty(hash))
                Response.Headers["X-Proof-Hash"] = hash;
            var filename = $"smv-{id}.proof";
            return File(bytes, "application/octet-stream", filename);
        }
        catch (SmvApiException ex)
        {
            return StatusCode(ex.HttpStatus ?? 502, new { error = ex.Kind.ToString(), message = ex.Message });
        }
    }
}
