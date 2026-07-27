using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("plugins/smv/about")]
public class SmvAboutController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }
}