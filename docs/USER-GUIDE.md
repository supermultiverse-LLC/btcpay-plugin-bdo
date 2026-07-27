# Bitcoin Digital Objects — User Guide

*Issue, verify, deliver and manage Bitcoin Digital Objects (BDOs) from your BTCPay Server.*

This guide covers everything a merchant needs: from installing the plugin to
handing a verifiable, Bitcoin-anchored digital object to a customer who has
never touched a wallet.

---

## 1. What is a Bitcoin Digital Object?

A **BDO** is a unique digital object — a certificate, a ticket, a warranty, a
collectible, a membership — whose existence and authenticity are **anchored on
the Bitcoin blockchain** (via the Taproot Assets protocol) and whose metadata
is cryptographically signed and independently verifiable:

- **Anchored on Bitcoin mainnet** — every BDO's issuance is committed inside a
  real Bitcoin transaction. No sidechain, no token, no separate network.
- **Verifiable by anyone** — a public verification page and a downloadable
  cryptographic proof let any third party audit a BDO against Bitcoin without
  trusting you or the platform.
- **Permanent metadata** — images are pinned to IPFS; the signed metadata
  (name, description, attributes) travels with the object.
- **Transferable** — send a BDO to a customer with a simple claim link (no
  wallet required on their side), or to any Taproot Assets address for full
  self-custody.

Typical uses: authenticity certificates for physical products, event tickets,
warranties, limited editions and collectibles, memberships and loyalty passes.

---

## 2. Installation

1. In BTCPay Server go to **Server Settings → Plugins**.
2. Upload the `BTCPayServer.Plugins.Smv.btcpay` file (or install *Bitcoin
   Digital Objects* from the plugin marketplace when listed) and restart.
3. A **Bitcoin Digital Objects** entry appears in your Store's sidebar.

Requirements: BTCPay Server **2.2.0 or newer**. No extra infrastructure is
needed in Hosted mode; self-custody mode needs your own `tapd` node (see §8).

---

## 3. Getting started — your BDO account

Everything the plugin does for a Store runs through a **BDO account**. You
create it without leaving BTCPay:

1. Open **Bitcoin Digital Objects → Settings**.
2. In the **BDO account** card, enter your **email**, accept the Terms of
   Service, and press **Send code**.
3. Type the **6-digit code** from your inbox and press **Activate**.

That's it — no password needed (you can also sign in with an existing
account/password from the secondary tabs). Your account **is** your Store's
wallet in Hosted mode: it mints, holds and sends your BDOs.

> The account is operated by Supermultiverse, the infrastructure provider
> behind this plugin. In Hosted mode it is **custodial**: the platform holds
> the keys, you keep ownership and can move any BDO out to self-custody at any
> time with Send — that is your exit path.

---

## 4. Plans & credits

Creating BDOs requires an active **plan** (from Silver up). Plans and credits
are bought right in **Settings → Plan & credits**, paid with any Lightning
wallet:

| Plan | What it unlocks | Yearly credits included | Fee per minted BDO |
|---|---|---|---|
| **Silver** | Unlimited mints, signed by the platform | 2,000 | 400 credits |
| **Gold** | Everything in Silver + sign with your **own key** (Nostr) — required to register BDOs minted on your own node | 5,000 | 250 credits |
| **Diamond** | Everything in Gold + 1 managed drop per year + featured placement | 30,000 | 100 credits |

The **fee per minted BDO** is the platform's part of each mint (the other
part is the on-chain Bitcoin fee) — higher tiers mint cheaper. It applies per
single mint, per unit in a series, and per BYON registration, and your quotes
and receipts always reflect your current tier automatically.

- A **Lifetime (Founders) Pass** may be offered while supply lasts: Diamond
  for life plus a one-time credit grant, at a price that steps up as units
  sell.
- **Mint credits** pay per-mint costs (see §5). Buy top-up packages any time;
  plan purchases include a yearly grant. Credits become spendable once a plan
  is active.
- Activation is instant: pay the Lightning invoice and the plugin unlocks
  minting on the spot — no reconnect needed.

---

## 5. Creating BDOs

### Single BDO — *Create → Mint one BDO*

Fill in name, collection, image (upload from your device or paste a URL),
description and attributes (`trait: value` pairs). The **estimated cost is
shown on the button itself** before you commit.

### A series — *Create → Mint a series*

Mint N unique, numbered BDOs (`Name #001 … #N`) **anchored in a single
Bitcoin transaction**. You pay the on-chain fee once for the whole series plus
a per-unit platform fee. The live total on the button updates as you change
the quantity.

### How you pay

- **Credits first**: if your credit balance covers the total, it is **charged
  immediately when you click** — no invoice step. The receipt shows the exact
  charge and your new balance.
- **Lightning fallback**: if your balance doesn't cover it, a Lightning
  invoice for the exact amount appears first. Nothing is ever charged twice.

The cost has two parts: the **on-chain fee** (paid to Bitcoin miners at the
current network fee rate — one-off per mint or per series) and the **platform
fee** per BDO.

After payment the plugin shows live progress: *preparing → anchoring on
Bitcoin → minted*. A new BDO shows **“Confirming on Bitcoin…”** until its
anchor transaction is included in a block (typically minutes); it becomes
sendable once confirmed.

---

## 6. My BDOs — your inventory

**My BDOs** lists what your Store holds, grouped by collection: *you hold X ·
collection of Y*. Open a collection to see each unit with its image, BDO ID
(the 64-hex Taproot Asset id), metadata (description, attributes), IPFS link
and actions:

- **Send** — deliver it to a customer (claim link) or to a Taproot address.
- **Info** — full detail, including the on-chain identifiers.

Fungible assets received from elsewhere appear separately as balances — they
are not BDOs.

---

## 7. Delivering a BDO to a customer

### Claim links — no wallet needed (recommended)

From **Send → Send to a customer**, press **Create claim link**. You get a
URL **on your own BTCPay domain** plus a QR code:

```
https://your-btcpay-domain/plugins/smv/claim/{store}?code=BDO-XXXX-XXXX-XXXX
```

Send it to your customer through any channel. When they open it they see the
BDO and your store's name, enter **their email**, receive a 6-digit code, type
it — and the BDO is theirs. An account is created for them automatically
(their email is their key; no password). They can later open the full
Supermultiverse app with the same email ("Email me a one-time code" on the
login page) or withdraw the BDO to their own node.

- One live link per BDO. If a link is already open, the Send panel shows it
  (with its QR and a **Cancel link** button) so you can re-share or revoke it.
- The BDO only leaves your wallet at the moment your customer redeems it.

### Taproot Assets address — full self-custody

If your customer runs their own Taproot Assets node, paste their `tapbc1…`
address in the Send form. The transfer is broadcast on-chain.

---

## 8. Hosted vs. self-custody (BYON)

The **Backend** selector in Settings chooses how your Store holds BDOs:

| | **Hosted** (default) | **Self-custody / BYON** |
|---|---|---|
| Keys | Held by the platform (custodial) | Your own `tapd` node — you hold the keys |
| Create BDOs | ✅ singles + series | Mint on your node; platform **registration** makes them publicly verifiable (Gold+ — signed with your own Nostr key) |
| Claim links | ✅ | — (send on-chain instead) |
| Exit path | Send any BDO out to a Taproot address any time | Already sovereign |

BYON (Bring Your Own Node) needs a reachable `tapd` instance; configure its
endpoint and macaroon in Settings. Both backends' settings are preserved when
you switch, so you can flip back without re-entering anything.

---

## 9. Verification — the trust story

Every BDO is independently auditable:

- **Verify tab**: paste a BDO ID (UUID or 64-hex) to see its metadata, the
  issuer signature (scheme, public key, signature, metadata hash) and the
  **Bitcoin anchor** (network, anchor outpoint with a mempool.space link,
  proof hash/size). You can **download the raw proof** and verify its SHA-256
  yourself.
- **Public API**: the same data is served by a public, unauthenticated API —
  your customers and third parties can verify without any account.
- **What the signature means**: the metadata hash is signed either by the
  platform (Hosted / Silver) or by **your own key** (Gold+ / BYON) — provable
  authorship that doesn't require trusting the platform.
- Images are **pinned to IPFS** (content-addressed) so the artwork's integrity
  outlives any single server.

Verifiability is permanent: it survives the BDO being sent, claimed or
withdrawn to self-custody.

---

## 10. Receiving BDOs

**Receive** generates a Taproot Assets address for your Store (Hosted) or your
node (BYON) so third parties can send assets to you on-chain.

---

## 11. Troubleshooting & FAQ

**“Confirming on Bitcoin…” for a long time** — the anchor transaction is
waiting for a block; at quiet fee rates this is normally minutes, occasionally
longer. Send unlocks automatically on confirmation.

**My customer's email shows a code AND a button — which one?** Type the
**code** on the page that asked for it. The button is an alternative that logs
into the app directly; they are the same single-use key — use one or the
other.

**The claim page says the code was already claimed / cancelled** — each claim
link is single-use; create a new link from the Send panel if needed.

**I mint on my own node but my BDOs don't show as registered** — platform
registration requires a connected BDO account with the Gold plan (it signs
with your own key). The Create page states the registration outcome before
you mint — no surprises.

**Credits show but I can't mint** — creating BDOs needs an active plan
(Silver+). Pick one in Settings → Plan & credits; activation is instant.

**Where is my money going when I mint?** — the receipt (and the button before
it) breaks it down: on-chain fee → Bitcoin miners; platform fee per BDO →
the service. Credits are the internal unit that pays both.

---

## 12. Links

- Plugin repository: <https://github.com/supermultiverse-LLC/btcpayserver-plugin-taproot-assets>
- STAS-01 standard: <https://github.com/supermultiverse-LLC/stas-standard>
- Bitcoin Digital Objects: <https://github.com/supermultiverse-LLC/bitcoin-digital-objects>
- Platform: <https://supermultiverse.io> · Terms: <https://app.supermultiverse.io/legal>
