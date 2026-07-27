using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// STAS-01 v0.2 metadata: build the canonical BDO metadata object and its
/// <c>metadata_hash</c> for BYON registration (RFC-PLUGIN-006 P2-2).
///
/// The hash MUST be byte-identical to the SMV backend's
/// <c>stasStandardAdapter.canonicalize()</c> (prepareAsset.generateMetadata), or the
/// creator's Nostr signature over it won't verify. Rules (per Lovable's live contract):
///   • field set below, empties omitted (not null / not "")
///   • all string VALUES NFC-normalized
///   • object keys sorted lexicographically (ordinal), recursively
///   • compact JSON (no insignificant whitespace), JS <c>JSON.stringify</c> escaping
///   • integers only (no floats); payload ≤ 65_536 bytes
///   • metadata_hash = sha256(utf8(canonical_json)) as lowercase 64-hex
///
/// VERIFY: confirm against a backend test vector before relying on it in production.
/// </summary>
public static class StasMetadata
{
    public const int MaxCanonicalBytes = 65_536;

    /// <summary>Build the STAS-01 metadata field set. Insertion order is irrelevant —
    /// <see cref="Canonicalize"/> sorts keys. Omits optional fields per the rules
    /// (description/collection blank → omitted; supply==1 and divisibility==0 omitted).</summary>
    public static Dictionary<string, object> Build(
        string name,
        string issuer,
        string? description = null,
        string? image = null,
        string? collection = null,
        IReadOnlyList<(string Trait, string Value)>? attributes = null,
        string? externalUrl = null)
    {
        var m = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = "stas-01",
            ["version"] = "1.0",
            ["name"] = name.Trim(),
            ["issuer"] = issuer.Trim(),
            ["asset_type"] = "collectible",
        };

        if (!string.IsNullOrWhiteSpace(description)) m["description"] = description.Trim();
        if (!string.IsNullOrWhiteSpace(image)) m["image"] = image.Trim();
        if (!string.IsNullOrWhiteSpace(collection)) m["collection"] = collection.Trim();

        if (!string.IsNullOrWhiteSpace(externalUrl)
            && Uri.TryCreate(externalUrl.Trim(), UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            m["external_url"] = u.AbsoluteUri;
        }

        if (attributes is { Count: > 0 })
        {
            m["attributes"] = attributes
                .Select(a => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["trait_type"] = a.Trait.Trim(),
                    ["value"] = a.Value.Trim(),
                })
                .ToList();
        }

        return m;
    }

    /// <summary>Canonical JSON string (sorted keys, NFC values, compact, JS escaping).</summary>
    public static string Canonicalize(object? value)
    {
        var sb = new StringBuilder(256);
        Write(sb, value);
        return sb.ToString();
    }

    /// <summary>metadata_hash = sha256(utf8(canonical_json)), lowercase 64-hex.</summary>
    public static string MetadataHash(object metadata)
    {
        var canonical = Canonicalize(metadata);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        if (bytes.Length > MaxCanonicalBytes)
            throw new InvalidOperationException($"STAS-01 metadata exceeds {MaxCanonicalBytes} bytes ({bytes.Length}).");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                WriteString(sb, s.Normalize(NormalizationForm.FormC)); // NFC all string values
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int or long or short or byte:
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case IDictionary<string, object> dict:
                sb.Append('{');
                var keys = dict.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(sb, keys[i]);           // field-name keys are ASCII literals
                    sb.Append(':');
                    Write(sb, dict[keys[i]]);
                }
                sb.Append('}');
                break;
            case System.Collections.IEnumerable list: // arrays keep their order (e.g. attributes by sort_order)
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                break;
            default:
                throw new InvalidOperationException($"Unsupported canonical type: {value.GetType()}");
        }
    }

    // JSON string escaping identical to JS JSON.stringify: escape " \ and control chars
    // (short forms for \b\t\n\f\r, else \uXXXX); everything else (incl. unicode) emitted raw.
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
