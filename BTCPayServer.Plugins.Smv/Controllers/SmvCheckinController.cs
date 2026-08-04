using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Smv.Backends;
using BTCPayServer.Plugins.Smv.Services;
using BTCPayServer.Plugins.Smv.Services.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Smv.Controllers;

/// <summary>
/// Event check-in, merchant side (RFC-INTEGRATION-002 §5, RFC-PLUGIN-013 F4).
///
/// The plugin had no check-in surface at all while the API behind it was built
/// and certified. This closes that: create an event for a collection, declare
/// which SERIES are its ticket types, and watch admissions per type.
///
/// A ticket type is a series — N identical units minted together — so declaring
/// one is picking from this collection's series. An event with none declared
/// admits nobody, which is the point: the old rule admitted anything sharing a
/// collection, and that let a padel event count a certificate of authenticity
/// as an admission.
///
/// The door scan is deliberately NOT here. An integrator runs their own scanner
/// against the API (that is the whole integration model), and a merchant-facing
/// camera scanner needs a QR decoder the plugin has no build step to bundle —
/// its own slice, with its own problems.
/// </summary>
[Route("stores/{storeId}/plugins/smv/checkin")]
public class SmvCheckinController : Controller
{
    private readonly ISmvStoreSettingsProvider _storeSettings;
    private readonly SmvOAuthTokenService _oauthTokens;
    private readonly ISettingsRepositoryAccessor _serverSettings;
    private readonly ILogger<SmvCheckinController> _log;

    public SmvCheckinController(
        ISmvStoreSettingsProvider storeSettings,
        SmvOAuthTokenService oauthTokens,
        ISettingsRepositoryAccessor serverSettings,
        ILogger<SmvCheckinController> log)
    {
        _storeSettings = storeSettings;
        _oauthTokens = oauthTokens;
        _serverSettings = serverSettings;
        _log = log;
    }

    private async Task<(System.Net.Http.HttpClient? Http, string? Error)> BuildHttpAsync(string storeId, CancellationToken ct)
    {
        var settings = await _storeSettings.GetAsync(storeId, ct);
        if (settings is null)
            return (null, "This Store isn't configured.");
        var token = await _oauthTokens.EnsureFreshTokenAsync(storeId, settings, ct);
        if (string.IsNullOrWhiteSpace(token))
            return (null, "Activate your BDO account in Settings first.");
        var server = await _serverSettings.GetAsync();
        return (ManagedWalletClient.CreateHttpClient(server.HostedApiBase, token, Math.Max(server.SmvHttpTimeoutMs, 20000)), null);
    }

    /// <summary>Events of this account, filtered to one collection by the caller.
    /// The API list is account-wide; an event shown inside a foreign collection
    /// would read as if it gated those units.</summary>
    [HttpGet("events")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Events(string storeId, string collectionId, CancellationToken ct)
    {
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            var list = await new ManagedWalletClient(http).ListCheckinEventsAsync(ct);
            var events = new System.Collections.Generic.List<object>();
            foreach (var e in list.Events)
            {
                if (!string.IsNullOrWhiteSpace(collectionId) && e.CollectionId != collectionId) continue;
                events.Add(new
                {
                    eventId = e.EventId,
                    name = e.Name,
                    status = e.Status,
                    totalTickets = e.TotalTickets,
                    ticketTypeCount = e.TicketTypeCount,
                    checkedIn = e.CheckedIn
                });
            }
            return Ok(new { events });
        }
        catch (ManagedWalletApiException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.events.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't load the events." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("event-create")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EventCreate(string storeId, string name, string collectionId, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 120 || string.IsNullOrWhiteSpace(collectionId))
            return BadRequest(new { message = "Give the event a name." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            var created = await new ManagedWalletClient(http).CreateCheckinEventAsync(name, collectionId, ct);
            _log.LogInformation("checkin.event_created store={StoreId} event={EventId}", storeId, created.EventId);
            return Ok(new { eventId = created.EventId });
        }
        catch (ManagedWalletApiException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.event_create.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't create the event." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("event-close")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> EventClose(string storeId, string eventId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return BadRequest(new { message = "Invalid request." });
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            await new ManagedWalletClient(http).CloseCheckinEventAsync(eventId.Trim(), ct);
            _log.LogInformation("checkin.event_closed store={StoreId} event={EventId}", storeId, eventId);
            return Ok(new { closed = true });
        }
        catch (ManagedWalletApiException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.event_close.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't close the event." });
        }
        finally { http.Dispose(); }
    }

    /// <summary>Ticket types of an event plus the series still free to become one.
    /// One call: what is selectable is decided server-side, so the list offered can
    /// never include a series another live event already claimed.</summary>
    [HttpGet("ticket-types")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> TicketTypes(string storeId, string eventId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return BadRequest(new { message = "Invalid request." });
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            var resp = await new ManagedWalletClient(http).ListTicketTypesAsync(eventId.Trim(), ct);
            return Ok(new
            {
                ticketTypes = resp.TicketTypes.ConvertAll(t => new
                {
                    id = t.Id, label = t.Label, unitCount = t.UnitCount, checkedIn = t.CheckedIn
                }),
                selectableSeries = resp.SelectableSeries.ConvertAll(s => new
                {
                    id = s.Id, name = s.Name, unitCount = s.UnitCount
                })
            });
        }
        catch (ManagedWalletApiException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.ticket_types.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't load the ticket types." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("ticket-type-add")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> TicketTypeAdd(string storeId, string eventId, string groupId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(groupId))
            return BadRequest(new { message = "Invalid request." });
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            var created = await new ManagedWalletClient(http)
                .AddTicketTypeAsync(eventId.Trim(), groupId.Trim(), null, ct);
            _log.LogInformation("checkin.ticket_type_added store={StoreId} event={EventId}", storeId, eventId);
            return Ok(new { ticketTypeId = created.TicketTypeId });
        }
        catch (ManagedWalletApiException ex)
        {
            // The platform answers with machine codes; turn the two a merchant can
            // actually act on into plain English and pass anything else through.
            var msg = ex.Message switch
            {
                "series_already_in_a_live_event" => "That series is already the ticket of another open event. Close it first.",
                "already_admitted_on_this_type" => "Somebody has already been admitted on this type.",
                _ => ex.Message
            };
            return BadRequest(new { message = msg });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.ticket_type_add.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't add the ticket type." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("ticket-type-remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> TicketTypeRemove(string storeId, string ticketTypeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ticketTypeId)) return BadRequest(new { message = "Invalid request." });
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            await new ManagedWalletClient(http).RemoveTicketTypeAsync(ticketTypeId.Trim(), ct);
            return Ok(new { removed = true });
        }
        catch (ManagedWalletApiException ex)
        {
            var msg = ex.Message == "already_admitted_on_this_type"
                ? "Somebody has already been admitted on this type, so it can't be removed."
                : ex.Message;
            return BadRequest(new { message = msg });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.ticket_type_remove.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't remove the ticket type." });
        }
        finally { http.Dispose(); }
    }

    // ── Organisers ────────────────────────────────────────────────────────
    // The grant means: this person can run the doors of this collection —
    // create events on it, declare ticket types, scan, close. It does NOT hand
    // over the holders' email addresses; the platform withholds those from
    // anyone who is not the collection's issuer, grant or no grant.

    [HttpGet("organizers")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Organizers(string storeId, string collectionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(collectionId)) return BadRequest(new { message = "Invalid request." });
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            var resp = await new ManagedWalletClient(http).ListOrganizersAsync(collectionId.Trim(), ct);
            // isIssuer, separately from the list: an empty list means "nobody
            // added yet" for an issuer and "not your collection" for anyone
            // else, and the card has to tell those two apart to know whether to
            // show itself at all.
            return Ok(new
            {
                isIssuer = true,
                organizers = resp.Organizers.ConvertAll(o => new { id = o.Id, email = o.Email })
            });
        }
        catch (ManagedWalletApiException)
        {
            // Includes "you are not the issuer of this collection", which the
            // platform answers as not_found on purpose — it will not confirm to
            // a stranger that the collection exists. No alarm here either: the
            // card simply stays hidden.
            return Ok(new { isIssuer = false, organizers = new object[0] });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.organizers.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't load the organisers." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("organizer-grant")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> OrganizerGrant(string storeId, string collectionId, string email, CancellationToken ct)
    {
        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(collectionId) || email.Length is 0 or > 254 || !email.Contains('@'))
            return BadRequest(new { message = "Enter the email address they sign in with." });

        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            var granted = await new ManagedWalletClient(http)
                .GrantOrganizerAsync(collectionId.Trim(), email, ct);
            _log.LogInformation("checkin.organizer_granted store={StoreId}", storeId);
            return Ok(new { grantId = granted.GrantId });
        }
        catch (ManagedWalletApiException ex)
        {
            var msg = ex.Message switch
            {
                // Not an error the merchant caused: the person has to exist as an
                // account before a door can be handed to them.
                "user_not_found" => "No account uses that email yet. Ask them to sign up first, then add them.",
                "already_the_issuer" => "That's you — the issuer can always run their own doors.",
                "not_authorized" => "Only the issuer of this collection can add organisers.",
                _ => ex.Message
            };
            return BadRequest(new { message = msg });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.organizer_grant.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't add the organiser." });
        }
        finally { http.Dispose(); }
    }

    [HttpPost("organizer-revoke")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> OrganizerRevoke(string storeId, string grantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(grantId)) return BadRequest(new { message = "Invalid request." });
        var (http, error) = await BuildHttpAsync(storeId, ct);
        if (http is null) return BadRequest(new { message = error });
        try
        {
            await new ManagedWalletClient(http).RevokeOrganizerAsync(grantId.Trim(), ct);
            _log.LogInformation("checkin.organizer_revoked store={StoreId}", storeId);
            return Ok(new { revoked = true });
        }
        catch (ManagedWalletApiException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "checkin.organizer_revoke.failed store={StoreId}", storeId);
            return BadRequest(new { message = "Couldn't remove the organiser." });
        }
        finally { http.Dispose(); }
    }
}
