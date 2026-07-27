using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Server-side proxy for the Proof Inspector. Browser never talks
/// to the decode endpoint directly — avoids CORS and lets the plugin
/// fetch the .proof bytes from the already-known asset record.
/// </summary>
[Route("plugins/stas/assets")]
public sealed class SmvProofInspectorController : Controller
{
    private readonly ISmvAssetProofLoader _proofs;
    private readonly StasProofDecoder _decoder;
    private readonly ILogger<SmvProofInspectorController> _log;

    public SmvProofInspectorController(
        ISmvAssetProofLoader proofs,
        StasProofDecoder decoder,
        ILogger<SmvProofInspectorController> log)
    {
        _proofs = proofs;
        _decoder = decoder;
        _log = log;
    }

    [HttpPost("{id}/inspect-proof")]
    [IgnoreAntiforgeryToken] // read-only (loads proof, decodes it); no state mutation
    public async Task<IActionResult> InspectProof(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { ok = false, error = "missing asset id" });

        var proof = await _proofs.LoadProofAsync(id, ct);
        if (proof is null)
            return NotFound(new { ok = false, error = "asset has no proof on file" });

        if (proof.Length > StasProofDecoder.MaxProofBytes)
            return StatusCode((int)HttpStatusCode.RequestEntityTooLarge,
                new { ok = false, error = "proof too large" });

        var result = await _decoder.DecodeAsync(proof, withMetaReveal: true, ct);
        if (!result.Ok)
        {
            // Surface the upstream decode body (relay stderr detail) to operator logs
            // only — NOT to the public response (this endpoint is anonymous).
            var upstreamBody = result.RawJson is { Length: > 0 } rj
                ? (rj.Length > 1000 ? rj.Substring(0, 1000) : rj)
                : "(none)";

            _log.LogWarning(
                "proof_inspect.error asset={Asset} kind={Kind} status={Status} upstream_body={UpstreamBody}",
                id, result.ErrorKind, result.UpstreamStatus, upstreamBody);

            var http = result.ErrorKind switch
            {
                DecodeErrorKind.NotConfigured  => (int)HttpStatusCode.ServiceUnavailable,
                DecodeErrorKind.ProofTooLarge  => (int)HttpStatusCode.RequestEntityTooLarge,
                DecodeErrorKind.Timeout        => (int)HttpStatusCode.GatewayTimeout,
                DecodeErrorKind.Network        => (int)HttpStatusCode.BadGateway,
                DecodeErrorKind.UpstreamHttp   => (int)HttpStatusCode.BadGateway,
                DecodeErrorKind.UpstreamPayload => (int)HttpStatusCode.BadGateway,
                _ => (int)HttpStatusCode.InternalServerError,
            };
            return StatusCode(http, new
            {
                ok = false,
                error_kind = result.ErrorKind.ToString(),
                error = result.ErrorMessage,
                upstream_status = result.UpstreamStatus,
            });
        }

        _log.LogInformation("proof_inspect.ok asset={Asset}", id);
        return Json(new
        {
            ok = true,
            decoded = result.Decoded,
            raw = result.Raw,
        });
    }
}
