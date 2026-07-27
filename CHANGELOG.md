# Changelog

All notable changes to the Bitcoin Digital Objects plugin.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · Versioning: [SemVer](https://semver.org/).

## [0.18.3] - 2026-07-27

### Added
- **Drops — one QR for a whole audience**: turn N held units of a collection
  into ONE public URL/QR on your own domain. Each person claims their own
  unit with just an email — first come, first served, one per person, units
  dispensed in order with a live "X of N claimed" counter. Optionally gift
  credits to each claimer, funded from your own balance at claim time.
  Close a drop any time; claimed units are untouched. Built for live events.
- **Redeem claim codes from the Receive tab**: paste a `BDO-XXXX` code
  someone sent your Store and the BDO lands directly in the Store's wallet,
  confirmed on My BDOs.
- The Send panel's claim section shows the bare code with its own Copy
  button, and surfaces an already-open link in place (URL + QR + cancel).

### Changed
- Concurrency and live-event hardening: atomic unit assignment under
  concurrent claims, same-account double-claim protection, and a
  venue-grade rate limit on drop pages (audiences share one venue IP).
- Settings shows the contracted plan's full benefits; backend selector
  auto-saves; account state never reads "Connected" while a reconnect is
  needed; redeem success lands on My BDOs with a clear confirmation.
- Custody note is collapsible with per-browser memory.

## [0.17.3] - 2026-07-27

First publicly listed release. Highlights of the 0.13 → 0.17 series:

### Added
- **Email-first activation**: create the Store's BDO account with an email +
  6-digit one-time code, without leaving BTCPay. No password required.
- **In-plugin plans & credits**: purchase Silver/Gold/Diamond (and the
  Lifetime Pass while available) with any Lightning wallet; instant
  activation; credit top-up packages.
- **Tier-differentiated mint pricing**: per-BDO platform fee by plan
  (Silver 400 / Gold 250 / Diamond 100 credits), always shown before paying.
- **Credits-first minting**: single mints and series charge the credit
  balance when it covers the total — no invoice round-trip; Lightning
  fallback otherwise. Cost breakdown lives on the pay button.
- **Series minting**: N unique numbered BDOs anchored in one Bitcoin
  transaction, with a live cost estimate; image upload from device.
- **Claim links (white-label delivery)**: hand a BDO to any customer with a
  link on the merchant's own BTCPay domain — email + one-time code, account
  auto-provisioned; QR and bare-code (copyable) variants; one live link per
  BDO, shown in place with cancel.
- **Claim-code redemption in Receive**: a Store can redeem codes it receives
  directly into its wallet, landing on My BDOs with confirmation.
- **Public verification**: every BDO (single or series unit) serves signed
  metadata, a downloadable proof, its Bitcoin anchor and an IPFS-pinned
  image — verifiability survives sends and claims.
- **Self-custody (BYON)**: mint on your own tapd node; platform registration
  signed with your own Nostr key (Gold+); IPFS pinning and image mirroring.

### Changed
- The account is presented as the Store's **BDO account** everywhere; the
  operator (Supermultiverse) stays named in ToS, custody statements and About.
- Backend selector auto-saves; Save only where credentials need it.
- Collapsible custody note with per-browser memory.

## Earlier (0.1 – 0.12)

Foundations: store-scoped settings with encrypted credentials, Hosted and
BYON backends, OAuth/embedded account connection, My BDOs collection-first
listing, Verify with proof download, Receive addresses, single-mint issuance
and batch minting groundwork. See the release notes of each tag.
