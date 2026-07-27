# Bitcoin Digital Objects for BTCPay Server

**Issue, verify, deliver and manage Bitcoin Digital Objects (BDOs) — unique,
Bitcoin-anchored digital objects — right from your BTCPay Server.**

Certificates of authenticity, tickets, warranties, limited editions,
memberships: mint them as verifiable objects anchored on Bitcoin mainnet, and
hand them to customers with a link on **your own domain** — no wallet needed
on their side.

📖 **[User Guide](./docs/USER-GUIDE.md)** — the complete merchant
documentation, from install to delivering your first BDO.

---

## What you get

| | |
|---|---|
| **Create** | Mint single BDOs or numbered series (N unique objects anchored in ONE Bitcoin transaction). Upload the image from your device; costs shown on the button before you pay. |
| **Pay with credits** | An internal credit balance pays mint fees instantly — no invoice round-trip. Lightning fallback when the balance is short. Plans (Silver/Gold/Diamond) and top-ups are purchased in-plugin with any Lightning wallet, activated instantly. |
| **Deliver** | One-click **claim links on your own BTCPay domain**: the customer opens the link, enters their email, types a one-time code — and the BDO is theirs. Accounts auto-provisioned; no password, no wallet, no third-party branding. |
| **Verify** | Public verification for every BDO: signed metadata, downloadable cryptographic proof, Bitcoin anchor (mempool link), IPFS-pinned image. Anyone can audit without trusting you or the platform. |
| **Custody your way** | Hosted mode (zero infrastructure, custodial with a guaranteed self-custody exit path) or **BYON** — bring your own `tapd` node and hold the keys, with platform registration signed by *your own* Nostr key (Gold+). |
| **Onboard in one field** | The Store's BDO account activates with an email + 6-digit code, without leaving BTCPay. |

Requirements: **BTCPay Server ≥ 2.2.0**. Hosted mode needs nothing else;
self-custody mode needs your own Taproot Assets (`tapd`) node.

---

## Install

1. Download the `.btcpay` package from [Releases](../../releases) (or install
   from the BTCPay plugin marketplace when listed).
2. **Server Settings → Plugins → Upload**, restart.
3. Open **Bitcoin Digital Objects** in your Store's sidebar and follow the
   [User Guide](./docs/USER-GUIDE.md).

---

## Trust model

- Every BDO's issuance is committed inside a **real Bitcoin mainnet
  transaction** (Taproot Assets protocol) — no sidechain, no bridge.
- Metadata is canonicalized, hashed and **signed** — by the platform, or by
  the merchant's **own key** (Gold+/BYON) for platform-independent authorship.
- Proofs are downloadable and independently verifiable; images are pinned to
  IPFS (content-addressed).
- Hosted custody is explicit about being custodial, and **Send is the exit
  path**: any BDO can be withdrawn to self-custody at any time.
- The STAS-01 trust model does not require trusting Supermultiverse or any
  issuer — verification runs against Bitcoin.

**Read the full vision:** [MANIFESTO.md](./MANIFESTO.md)

---

## For developers

- Plugin source: `BTCPayServer.Plugins.Smv/` (.NET, targets the BTCPay plugin
  system; pinned lib set under `lib/btcpay/`).
- Build from source:

```bash
git clone https://github.com/supermultiverse-LLC/btcpay-plugin-bdo.git
cd btcpay-plugin-bdo
dotnet build BTCPayServer.Plugins.Smv/BTCPayServer.Plugins.Smv.csproj -c Release
```

Marketplace builds are produced from this repository by the
[BTCPay Plugin Builder](https://plugin-builder.btcpayserver.org) —
`dotnet publish` of the plugin project plus the official PluginPacker,
so what merchants install is exactly what this source builds.

---

## Links

- [User Guide](./docs/USER-GUIDE.md)
- [STAS-01 standard](https://github.com/supermultiverse-LLC/stas-standard)
- [Bitcoin Digital Objects](https://github.com/supermultiverse-LLC/bitcoin-digital-objects)
- [Supermultiverse](https://supermultiverse.io) · [Terms](https://app.supermultiverse.io/legal)

Built and maintained by [Supermultiverse](https://supermultiverse.io).
