using System;
using System.Collections.Generic;
using System.Text.Json;
using BTCPayServer.Plugins.Smv.Backends;

namespace BTCPayServer.Plugins.Smv.Services.Tapd;

/// <summary>
/// The display fields of a STAS-01 canonical metadata blob, decoded from the asset's
/// own <c>asset_meta.data</c> (BYON local enrichment, RFC-PLUGIN-006). This is the
/// sovereign source of truth: it works for every asset on the merchant's node,
/// registered with the platform or not. Parsing is tolerant — anything malformed
/// simply yields nulls and the listing degrades to name/id as before.
/// </summary>
public sealed record TapdMetaInfo(
    string? ImageUrl,
    string? Description,
    string? ExternalUrl,
    IReadOnlyList<AssetAttribute>? Attributes)
{
    public static TapdMetaInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            List<AssetAttribute>? attrs = null;
            if (r.TryGetProperty("attributes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                attrs = new List<AssetAttribute>();
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var trait = Str(a, "trait_type");
                    var value = Str(a, "value");
                    if (!string.IsNullOrWhiteSpace(trait) && value is not null)
                        attrs.Add(new AssetAttribute(trait!, value));
                }
                if (attrs.Count == 0) attrs = null;
            }

            var image = HttpsOnly(Str(r, "image"));
            var external = HttpsOnly(Str(r, "external_url"));
            var description = Str(r, "description");

            if (image is null && external is null && description is null && attrs is null)
                return null;

            return new TapdMetaInfo(image, description, external, attrs);
        }
        catch
        {
            return null;
        }
    }

    // Only http(s) URLs are ever emitted to the views (the metadata is
    // merchant-authored but rendered in the admin UI — no javascript:/data: URIs).
    private static string? HttpsOnly(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.AbsoluteUri : null;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
