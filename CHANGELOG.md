# Changelog

All notable changes to the Bitcoin Digital Objects plugin.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · Versioning: [SemVer](https://semver.org/).

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
