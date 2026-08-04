using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Smv.Backends;

// Event check-in (RFC-INTEGRATION-002 §5, RFC-PLUGIN-013 F4). The plugin manages
// events and their ticket types; the door scan itself is deliberately not here —
// an integrator's own scanner calls the API directly, and a merchant scanner is
// its own slice.

public sealed class ManagedCheckinEvent
{
    [JsonPropertyName("event_id")] public string? EventId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("collection_id")] public string? CollectionId { get; set; }
    // Units of the DECLARED ticket types — not every asset in the collection.
    [JsonPropertyName("total_tickets")] public long TotalTickets { get; set; }
    [JsonPropertyName("ticket_type_count")] public int TicketTypeCount { get; set; }
    [JsonPropertyName("checked_in")] public long CheckedIn { get; set; }
}

public sealed class ManagedCheckinEventsResponse
{
    [JsonPropertyName("events")] public List<ManagedCheckinEvent> Events { get; set; } = new();
}

public sealed class ManagedCheckinEventResponse
{
    [JsonPropertyName("event")] public ManagedCheckinEvent? Event { get; set; }
}

public sealed class ManagedEventCreated
{
    [JsonPropertyName("event_id")] public string? EventId { get; set; }
}

public sealed class ManagedTicketType
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    // A series uuid, or "asset:<uuid>" for a BDO minted on its own.
    [JsonPropertyName("group_id")] public string? GroupId { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("unit_count")] public int UnitCount { get; set; }
    [JsonPropertyName("checked_in")] public long CheckedIn { get; set; }
}

/// <summary>A series that could still become a ticket type of this event —
/// already filtered server-side against series spoken for by a live event.</summary>
public sealed class ManagedSelectableSeries
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("unit_count")] public int UnitCount { get; set; }
}

public sealed class ManagedTicketTypesResponse
{
    [JsonPropertyName("ticket_types")] public List<ManagedTicketType> TicketTypes { get; set; } = new();
    [JsonPropertyName("selectable_series")] public List<ManagedSelectableSeries> SelectableSeries { get; set; } = new();
}

public sealed class ManagedTicketTypeCreated
{
    [JsonPropertyName("ticket_type_id")] public string? TicketTypeId { get; set; }
}

/// <summary>Someone the issuer has allowed to run this collection's doors
/// (RFC-PLUGIN-012 P3). The email is the address they sign in with — how the
/// grant was made and the only way the issuer can tell one from another.</summary>
public sealed class ManagedEventOrganizer
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("granted_at")] public string? GrantedAt { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
}

public sealed class ManagedOrganizersResponse
{
    [JsonPropertyName("organizers")] public List<ManagedEventOrganizer> Organizers { get; set; } = new();
}

public sealed class ManagedOrganizerGranted
{
    [JsonPropertyName("grant_id")] public string? GrantId { get; set; }
}
