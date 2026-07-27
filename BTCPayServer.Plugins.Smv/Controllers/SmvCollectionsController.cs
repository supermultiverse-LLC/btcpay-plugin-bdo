using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Smv.Models;
using BTCPayServer.Plugins.Smv.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("plugins/smv/collections")]
public class SmvCollectionsController : Controller
{
    private static readonly Regex SlugRe = new(@"^[a-z0-9][a-z0-9-]{0,80}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SmvPublicApiClient _api;
    private readonly ILogger<SmvCollectionsController> _logger;

    public SmvCollectionsController(SmvPublicApiClient api, ILogger<SmvCollectionsController> logger)
    {
        _api = api;
        _logger = logger;
    }

    [HttpGet("")]
    public IActionResult Browse()
    {
        return View();
    }

    [HttpPost("go")]
    [ValidateAntiForgeryToken]
    public IActionResult Go(string slug)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();

        if (!SlugRe.IsMatch(normalized))
        {
            TempData["SmvCollectionsError"] = "Invalid collection slug.";
            return RedirectToAction(nameof(Browse));
        }

        return RedirectToAction(nameof(Collection), new { slug = normalized });
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Collection(string slug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug) || !SlugRe.IsMatch(slug))
        {
            TempData["SmvCollectionsError"] = "Invalid collection slug.";
            return RedirectToAction(nameof(Browse));
        }

        try
        {
            var response = await _api.GetCollectionAsync(slug, ct);
            return View("View", response);
        }
        catch (SmvApiException ex) when (ex.Kind == SmvApiErrorKind.NotVerifiable)
        {
            TempData["SmvCollectionsError"] = $"Collection '{slug}' not found.";
            return RedirectToAction(nameof(Browse));
        }
        catch (SmvApiException ex)
        {
            _logger.LogWarning(ex, "SMV collection lookup failed for slug {Slug}", slug);
            TempData["SmvCollectionsError"] = $"Lookup failed: {ex.Message}";
            return RedirectToAction(nameof(Browse));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading SMV collection {Slug}", slug);
            TempData["SmvCollectionsError"] = "Unexpected error. Please try again.";
            return RedirectToAction(nameof(Browse));
        }
    }
}