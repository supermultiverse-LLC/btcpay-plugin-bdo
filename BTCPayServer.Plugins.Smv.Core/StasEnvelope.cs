using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Smv.Core;

/// <summary>
/// STAS-01 register envelope (RFC-PLUGIN-006 P2-2c). Builds the <c>envelope_signers</c>
/// entries the SMV <c>managed-wallet-register-external-asset</c> endpoint — and its
/// <c>envelopeMultiSignerVerifier</c> — consume.
///
/// The <c>nostr_event</c> fields are passed through VERBATIM: the creator's Nostr client
/// computed the event id as sha256(JSON.stringify([0,pubkey,created_at,kind,tags,content]))
/// and signed it, so altering any of those bytes (even case) would make the verifier's
/// id recomputation — and the schnorr check — fail. The plugin never reconstructs them.
///
/// Shape confirmed against Lovable's deployed verifier (2026-07-22). Hard requirements:
/// kind==30078, the three tags (purpose/domain/metadata_hash) exactly as signed,
/// pubkey==signer_id, tags.metadata_hash == target_value == sha256(canonical_bytes).
/// The signer must be pre-registered via verify-nostr-signer (Gold+) or the endpoint
/// returns <c>signer_not_registered</c>.
/// </summary>
public static class StasEnvelope
{
    /// <summary>Nostr event kind for an SMV asset-authorship signature (NIP-78 app data).</summary>
    public const int NostrAssetSignatureKind = 30078;

    /// <summary>
    /// One <c>creator</c> signer with scheme <c>nostr_nip07</c>, in the exact shape the
    /// SMV verifier expects. Built as an ordered dictionary so serialization emits the
    /// snake_case keys verbatim (System.Text.Json Web defaults would otherwise camelCase).
    /// The event fields are pass-through; only the sibling <c>signer_id</c>/<c>target_value</c>
    /// matching fields are derived.
    /// </summary>
    public static Dictionary<string, object> CreatorSignerNostr(
        string pubkeyHex,
        string signatureHex,
        string eventId,
        long createdAt,
        int kind,
        IReadOnlyList<IReadOnlyList<string>> tags,
        string content,
        string metadataHash)
    {
        if (string.IsNullOrWhiteSpace(pubkeyHex)) throw new ArgumentException("pubkey is required", nameof(pubkeyHex));
        if (string.IsNullOrWhiteSpace(signatureHex)) throw new ArgumentException("signature is required", nameof(signatureHex));
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("event id is required", nameof(eventId));
        if (string.IsNullOrWhiteSpace(metadataHash)) throw new ArgumentException("metadata_hash is required", nameof(metadataHash));

        // Verbatim pass-through of the signed event tags (List of string lists).
        var eventTags = (tags ?? Array.Empty<IReadOnlyList<string>>())
            .Select(t => t.ToList())
            .ToList();

        // B4 as-built alignment (2026-07-25), certified against the deployed verifier
        // (envelopeMultiSignerVerifier.parseSigners/verifyOne):
        //   • kind   = "nostr_nip07"  (the signer KIND — where the sealed name lives)
        //   • scheme = "nostr-event"  (the VERIFICATION scheme; "nostr_nip07" as a
        //     scheme fails with unsupported_scheme)
        //   • event  = the signed Nostr event VERBATIM, under the field name `event`
        //   • target = the metadata hash VALUE in lowercase hex (a {"target":"metadata_hash",
        //     "target_value":…} pair verifies the literal label and always fails)
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["role"] = "creator",
            ["kind"] = "nostr_nip07",
            ["scheme"] = "nostr-event",
            ["pubkey"] = pubkeyHex,                  // == event.pubkey (verbatim)
            ["signer_id"] = pubkeyHex,
            ["target"] = metadataHash.ToLowerInvariant(),   // == sha256(canonical_bytes)
            ["signature"] = signatureHex,            // == event.sig
            ["event"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = eventId,
                ["pubkey"] = pubkeyHex,
                ["created_at"] = createdAt,
                ["kind"] = kind,
                ["tags"] = eventTags,
                ["content"] = content ?? "",
                ["sig"] = signatureHex,
            },
        };
    }
}
