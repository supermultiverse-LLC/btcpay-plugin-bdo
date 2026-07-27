using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace BTCPayServer.Plugins.Smv.Controllers;

[Route("plugins/smv")]
public class SmvStaticController : Controller
{
    // Renders a QR (PNG) for arbitrary text — used for the Hosted LN fee invoice so
    // the merchant can scan it with a phone wallet. PngByteQRCode is pure (no
    // System.Drawing), so this is safe on any host. Not Store-scoped or sensitive.
    [HttpGet("qr")]
    public IActionResult Qr(string? data)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Length > 4096)
            return BadRequest();

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(6);

        Response.Headers["Cache-Control"] = "no-store";
        return File(png, "image/png");
    }

    // Mint-credits top-up card (Settings). Flow: info → pick package → LN
    // invoice + QR → poll until settled. The balance is only ever what the
    // backend reports — the page never computes credits.
    [HttpGet("topup.js")]
    public IActionResult TopupJs()
    {
        const string js = """
(function () {
  "use strict";

  var card = document.querySelector("[data-smv-topup]");
  if (!card) return;

  var urls = {
    info: card.getAttribute("data-url-info"),
    create: card.getAttribute("data-url-create"),
    status: card.getAttribute("data-url-status")
  };
  var csrf = card.querySelector("input[name='__RequestVerificationToken']");
  var pollTimer = null;

  // One client_request_id per PURCHASE, not per page: it must be reused while
  // an invoice is pending (idempotent replay) and regenerated once that
  // purchase settles or the merchant switches package — otherwise the second
  // buy replays a terminal intent and no new invoice ever appears.
  function newRequestId() {
    return (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now()) + Math.random();
  }
  var requestId = newRequestId();
  var currentPackageId = null;
  var currentSettled = false;

  function el(sel) { return card.querySelector(sel); }
  function show(sel) { var n = el(sel); if (n) n.classList.remove("d-none"); }
  function hide(sel) { var n = el(sel); if (n) n.classList.add("d-none"); }
  function setText(sel, text) { var n = el(sel); if (n) n.textContent = text; }

  // Credits are prepaid service units (1:1 with sats today). The BALANCE and
  // the PACKAGES read as credits; only the Lightning invoice itself talks
  // sats, because that is what the wallet actually pays.
  function fmtCredits(n) { return Number(n).toLocaleString() + " credits"; }
  function fmtSats(n) { return Number(n).toLocaleString() + " sats"; }

  function setError(message) {
    setText("[data-topup-error]", message || "Something went wrong.");
    show("[data-topup-error]");
  }

  function renderInfo(info) {
    hide("[data-topup-loading]");
    if (!info.connected) {
      setText("[data-topup-unavailable]", info.message || "Sign in first.");
      show("[data-topup-unavailable]");
      return;
    }
    setText("[data-topup-balance]", fmtCredits(info.balanceSats));
    show("[data-topup-body]");

    // Account-card at-a-glance slot (outside this section, hence document).
    var acctCredits = document.querySelector("[data-account-credits]");
    if (acctCredits) acctCredits.textContent = Number(info.balanceSats).toLocaleString();

    // No active plan → nothing credits could pay for (mint and registration
    // are both plan-gated). Keep the buttons visible but disabled, and say
    // why — the plan section above is the answer.
    var gated = info.hasPlan === false;
    if (gated) show("[data-topup-needs-plan]"); else hide("[data-topup-needs-plan]");

    var list = el("[data-topup-packages]");
    if (!list) return;
    list.innerHTML = "";
    (info.packages || []).forEach(function (p) {
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "btn btn-outline-primary btn-sm me-2 mb-2";
      btn.textContent = p.label + " — " + fmtCredits(p.amountSats);
      if (gated) {
        btn.disabled = true;
        btn.title = "Choose a plan first — credits pay per-mint fees once a plan is active.";
      } else {
        btn.addEventListener("click", function () { createInvoice(p.id, btn); });
      }
      list.appendChild(btn);
    });
  }

  function createInvoice(packageId, btn) {
    hide("[data-topup-error]");

    // Fresh purchase (previous one settled, or a different package) → fresh
    // idempotency id. Same still-pending package → same id (safe replay).
    if (currentSettled || (currentPackageId && currentPackageId !== packageId)) {
      requestId = newRequestId();
    }
    currentPackageId = packageId;
    currentSettled = false;

    btn.disabled = true;
    var form = new FormData();
    form.append("packageId", packageId);
    form.append("clientRequestId", requestId);
    if (csrf) form.append("__RequestVerificationToken", csrf.value);

    fetch(urls.create, { method: "POST", body: form, credentials: "same-origin" })
      .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
      .then(function (res) {
        btn.disabled = false;
        if (!res.ok) {
          // Terminal replay (e.g. page reloaded mid-flow): retry once fresh.
          requestId = newRequestId();
          setError(res.j && res.j.message);
          return;
        }
        showInvoice(res.j);
      })
      .catch(function () { btn.disabled = false; setError("Network error."); });
  }

  function showInvoice(inv) {
    // Full reset: this panel is reused across purchases.
    setText("[data-topup-amount]", fmtSats(inv.amountSats));
    var qr = el("[data-topup-qr]");
    if (qr) qr.src = "/plugins/smv/qr?data=" + encodeURIComponent(inv.bolt11);
    var bolt = el("[data-topup-bolt11]");
    if (bolt) bolt.value = inv.bolt11;
    var state = el("[data-topup-state]");
    if (state) state.className = "small text-muted mb-0";
    setText("[data-topup-state]", "Waiting for payment…");
    show("[data-topup-pay-line]");
    show("[data-topup-qr-wrap]");
    show("[data-topup-bolt11-row]");
    show("[data-topup-invoice]");

    if (pollTimer) clearInterval(pollTimer);
    var elapsed = 0;
    pollTimer = setInterval(function () {
      elapsed += 3;
      if (elapsed > 3600) { clearInterval(pollTimer); return; }
      fetch(urls.status + "&intentId=" + encodeURIComponent(inv.intentId), { credentials: "same-origin" })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (s) {
          if (!s) return;
          if (s.paid) {
            clearInterval(pollTimer);
            currentSettled = true;
            requestId = newRequestId();   // next purchase = new intent
            // The invoice is spent — showing it again only invites confusion.
            hide("[data-topup-pay-line]");
            hide("[data-topup-qr-wrap]");
            hide("[data-topup-bolt11-row]");
            var st = el("[data-topup-state]");
            if (st) st.className = "text-success fw-semibold mb-0";
            setText("[data-topup-state]", "Paid — credits added to your balance.");
            setText("[data-topup-balance]", fmtCredits(s.balanceSats));
          }
        })
        .catch(function () { /* transient — keep polling */ });
    }, 3000);
  }

  var copyBtn = el("[data-topup-copy]");
  if (copyBtn) {
    copyBtn.addEventListener("click", function () {
      var bolt = el("[data-topup-bolt11]");
      if (bolt && navigator.clipboard) navigator.clipboard.writeText(bolt.value);
      copyBtn.textContent = "Copied";
      setTimeout(function () { copyBtn.textContent = "Copy invoice"; }, 1500);
    });
  }

  fetch(urls.info, { credentials: "same-origin" })
    .then(function (r) { return r.json(); })
    .then(renderInfo)
    .catch(function () {
      hide("[data-topup-loading]");
      setText("[data-topup-unavailable]", "Couldn't reach the platform.");
      show("[data-topup-unavailable]");
    });
})();
""";
        Response.Headers["Cache-Control"] = "no-store";
        return Content(js, "application/javascript");
    }

    // Send-to-customer claim links (GAP G2). Per-asset section inside the
    // Send panel: create → link + QR + copy + cancel. The BDO only leaves
    // the merchant's wallet when the customer redeems (atomic, server-side).
    [HttpGet("claim-link.js")]
    public IActionResult ClaimLinkJs()
    {
        const string js = """
(function () {
  "use strict";

  var csrf = document.querySelector("input[name='__RequestVerificationToken']");

  // One list fetch per page: which BDOs already have an open link. Each send
  // panel then shows THAT link (URL + QR + cancel) instead of a Create button
  // that would only bounce off the one-live-link-per-holding rule.
  var pendingLinksPromise = null;
  function fetchPendingLinks(listUrl) {
    if (!pendingLinksPromise) {
      pendingLinksPromise = fetch(listUrl, { credentials: "same-origin" })
        .then(function (r) { return r.ok ? r.json() : { pending: [] }; })
        .then(function (j) { return (j && j.pending) || []; })
        .catch(function () { return []; });
    }
    return pendingLinksPromise;
  }

  document.querySelectorAll("[data-claim-section]").forEach(function (section) {
    var urls = {
      create: section.getAttribute("data-url-create"),
      cancel: section.getAttribute("data-url-cancel"),
      list: section.getAttribute("data-url-list")
    };
    var form = section.closest("[data-send-form]");
    var assetId = form ? form.getAttribute("data-send-asset-id") : null;
    var currentCode = null;

    function el(sel) { return section.querySelector(sel); }
    function show(sel) { var n = el(sel); if (n) n.classList.remove("d-none"); }
    function hide(sel) { var n = el(sel); if (n) n.classList.add("d-none"); }
    function setError(msg) {
      var n = el("[data-claim-error]");
      if (n) { n.textContent = msg || "Something went wrong."; n.classList.remove("d-none"); }
    }

    function showLink(code, url) {
      currentCode = code;
      var input = el("[data-claim-url]");
      if (input) input.value = url;
      var codeInput = el("[data-claim-code-display]");
      if (codeInput) codeInput.value = code;
      var qr = el("[data-claim-qr]");
      if (qr) qr.src = "/plugins/smv/qr?data=" + encodeURIComponent(url);
      var btn = el("[data-claim-create]");
      if (btn) btn.classList.add("d-none");
      show("[data-claim-result]");
    }

    // Pre-existing open link for this BDO → surface it right away.
    if (urls.list && assetId) {
      fetchPendingLinks(urls.list).then(function (pending) {
        var match = pending.find(function (l) {
          return (l.tapdAssetId && l.tapdAssetId.toLowerCase() === assetId.toLowerCase()) ||
                 (l.assetId && l.assetId.toLowerCase() === assetId.toLowerCase());
        });
        if (match && match.code && match.claimUrl && !currentCode) {
          showLink(match.code, match.claimUrl);
        }
      });
    }

    var createBtn = el("[data-claim-create]");
    if (createBtn) createBtn.addEventListener("click", function () {
      if (!assetId) { setError("Missing asset reference."); return; }
      hide("[data-claim-error]");
      createBtn.disabled = true;
      var fd = new FormData();
      fd.append("assetId", assetId);
      if (csrf) fd.append("__RequestVerificationToken", csrf.value);
      fetch(urls.create, { method: "POST", body: fd, credentials: "same-origin" })
        .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
        .then(function (res) {
          createBtn.disabled = false;
          if (!res.ok) { setError(res.j && res.j.message); return; }
          showLink(res.j.code, res.j.claimUrl);
        })
        .catch(function () { createBtn.disabled = false; setError("Network error."); });
    });

    var copyBtn = el("[data-claim-copy]");
    if (copyBtn) copyBtn.addEventListener("click", function () {
      var input = el("[data-claim-url]");
      if (input && navigator.clipboard) navigator.clipboard.writeText(input.value);
      copyBtn.textContent = "Copied";
      setTimeout(function () { copyBtn.textContent = "Copy link"; }, 1500);
    });

    var copyCodeBtn = el("[data-claim-code-copy]");
    if (copyCodeBtn) copyCodeBtn.addEventListener("click", function () {
      var input = el("[data-claim-code-display]");
      if (input && navigator.clipboard) navigator.clipboard.writeText(input.value);
      copyCodeBtn.textContent = "Copied";
      setTimeout(function () { copyCodeBtn.textContent = "Copy code"; }, 1500);
    });

    var cancelBtn = el("[data-claim-cancel]");
    if (cancelBtn) cancelBtn.addEventListener("click", function () {
      if (!currentCode) return;
      hide("[data-claim-error]");
      cancelBtn.disabled = true;
      var fd = new FormData();
      fd.append("code", currentCode);
      if (csrf) fd.append("__RequestVerificationToken", csrf.value);
      fetch(urls.cancel, { method: "POST", body: fd, credentials: "same-origin" })
        .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
        .then(function (res) {
          cancelBtn.disabled = false;
          if (!res.ok) { setError(res.j && res.j.message); return; }
          currentCode = null;
          hide("[data-claim-result]");
          if (createBtn) createBtn.classList.remove("d-none");
        })
        .catch(function () { cancelBtn.disabled = false; setError("Network error."); });
    });
  });
})();
""";
        Response.Headers["Cache-Control"] = "no-store";
        return Content(js, "application/javascript");
    }

    // Premium subscription card (Settings, GAP G1). Flow: info → pick tier →
    // LN invoice + QR → poll; on activation the server force-refreshes the
    // token (assets:mint lands) and the page reloads so every gate updates.
    [HttpGet("subscription.js")]
    public IActionResult SubscriptionJs()
    {
        const string js = """
(function () {
  "use strict";

  var card = document.querySelector("[data-smv-sub]");
  if (!card) return;

  var urls = {
    info: card.getAttribute("data-url-info"),
    create: card.getAttribute("data-url-create"),
    status: card.getAttribute("data-url-status")
  };
  var csrf = card.querySelector("input[name='__RequestVerificationToken']");
  var pollTimer = null;

  function newRequestId() {
    return (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Date.now()) + Math.random();
  }
  var requestId = newRequestId();
  var currentTier = null;

  // Labels match Studio's pricing page; benefits are the REAL entitlements
  // (verified against the exchange gates and the settle RPC). The credit
  // amounts arrive from the backend (creditGrantSats) — never hardcoded here.
  var TIERS = {
    silver_v2: { label: "Silver",  benefit: "Unlimited mints, signed by Supermultiverse" },
    tier_1:    { label: "Gold",    benefit: "Everything in Silver + sign with your OWN key (Nostr) — also required to register BDOs minted on your own node" },
    tier_2:    { label: "Diamond", benefit: "Everything in Gold + 1 managed drop per year + featured placement" }
  };

  var MODE_INTRO = {
    hosted: "A plan unlocks creating BDOs from this Store — and includes credits every year.",
    byon: "You mint on your own node freely. A plan unlocks registering those BDOs on the platform (Gold+ signs with your own key) — and includes credits every year."
  };

  function el(sel) { return card.querySelector(sel); }
  function show(sel) { var n = el(sel); if (n) n.classList.remove("d-none"); }
  function hide(sel) { var n = el(sel); if (n) n.classList.add("d-none"); }
  function setText(sel, text) { var n = el(sel); if (n) n.textContent = text; }
  function fmtSats(n) { return Number(n).toLocaleString() + " sats"; }

  function setError(message) {
    setText("[data-sub-error]", message || "Something went wrong.");
    show("[data-sub-error]");
  }

  function renderInfo(info) {
    hide("[data-sub-loading]");
    if (!info.connected) {
      setText("[data-sub-unavailable]", info.message || "Sign in first.");
      show("[data-sub-unavailable]");
      // Dead connection: the account card must not keep claiming "Connected"
      // while every data call fails (2026-07-27 contradiction find).
      if (info.authExpired) {
        var badge = document.querySelector("[data-account-badge]");
        if (badge) {
          badge.textContent = "Reconnect needed";
          badge.classList.remove("bg-success");
          badge.classList.add("bg-warning", "text-dark");
        }
      }
      return;
    }
    show("[data-sub-body]");

    var planCard = card.closest("[data-smv-plan-mode]");
    var mode = planCard ? planCard.getAttribute("data-smv-plan-mode") : "hosted";

    var TIER_RANK = { silver_v2: 1, tier_1: 2, tier_2: 3 };
    var currentRank = TIER_RANK[info.currentTier] || 0;

    if (info.currentTier) {
      var t = TIERS[info.currentTier] || { label: info.currentTier, benefit: "" };
      var until = info.currentExpiresAt ? new Date(info.currentExpiresAt).toLocaleDateString() : "";
      setText("[data-sub-current]", "Active plan: " + t.label + (until ? " (until " + until + ")" : ""));
      show("[data-sub-current-wrap]");

      // The user must always see what THEIR plan includes — the generic intro
      // gives way to the contracted plan's benefit + credits + per-BDO fee
      // (numbers from the backend, same source the charges come from).
      var cur = (info.tiers || []).filter(function (x) { return x.name === info.currentTier; })[0];
      var desc = t.benefit || "";
      if (cur) {
        var curCredits = Number(cur.creditGrantSats || 0);
        var curFee = Number(cur.mintFeeSats || 0);
        if (curCredits > 0) desc += " · includes " + curCredits.toLocaleString() + " credits/year";
        if (curFee > 0) desc += " · " + curFee.toLocaleString() + " credits per minted BDO";
      }
      setText("[data-sub-current-desc]", desc);
      show("[data-sub-current-desc]");
      hide("[data-sub-intro]");
    } else {
      setText("[data-sub-intro]", MODE_INTRO[mode] || MODE_INTRO.hosted);
      show("[data-sub-intro]");
    }

    // Account-card at-a-glance slot (outside this section, hence document).
    var acctPlan = document.querySelector("[data-account-plan]");
    if (acctPlan) {
      acctPlan.textContent = info.currentTier
        ? (TIERS[info.currentTier] || { label: info.currentTier }).label
        : "None";
    }

    var list = el("[data-sub-tiers]");
    if (!list) return;
    list.innerHTML = "";

    // Lifetime (Founders) Pass — FIRST and highlighted (purple): one payment,
    // Diamond forever, 250 units globally with live scarcity. Hidden when the
    // user already IS Diamond-forever (no expiry) — nothing left to sell them.
    var isDiamondForever = info.currentTier === "tier_2" && !info.currentExpiresAt;
    var lt = info.lifetime;
    if (lt && !lt.alreadyOwned && !isDiamondForever) {
      var ltWrap = document.createElement("div");
      ltWrap.className = "border rounded p-2 mb-2";
      ltWrap.style.borderColor = "#8e44ec";
      ltWrap.style.borderWidth = "2px";
      var ltBtn = document.createElement("button");
      ltBtn.type = "button";
      ltBtn.className = "btn btn-sm";
      ltBtn.style.backgroundColor = "#8e44ec";
      ltBtn.style.color = "#fff";
      if (lt.soldOut) {
        ltBtn.disabled = true;
        ltBtn.textContent = "Lifetime Pass — SOLD OUT";
      } else {
        ltBtn.textContent = "Lifetime Pass — " + fmtSats(lt.priceSats) + " · one-time";
        ltBtn.addEventListener("click", function () { createInvoice("lifetime", ltBtn); });
      }
      var ltLine = document.createElement("div");
      ltLine.className = "small text-muted mt-1";
      ltLine.textContent = "Diamond forever — pay once, never renew · includes " +
        Number(lt.creditGrantSats).toLocaleString() + " credits (one-time, not yearly)" +
        (Number(lt.mintFeeSats || 0) > 0 ? " · " + Number(lt.mintFeeSats).toLocaleString() + " credits per minted BDO" : "");
      ltWrap.appendChild(ltBtn);
      ltWrap.appendChild(ltLine);
      if (!lt.soldOut) {
        // Full scarcity story: global claimed + escalator pressure.
        var ltScarcity = document.createElement("div");
        ltScarcity.className = "small mt-1";
        ltScarcity.style.color = "#8e44ec";
        var txt = Number(lt.totalSold).toLocaleString() + "/" + Number(lt.totalCap).toLocaleString() +
          " claimed · " + Number(lt.unitsRemainingTier).toLocaleString() + " left at this price";
        if (lt.nextPriceSats) txt += " — then " + fmtSats(lt.nextPriceSats);
        ltScarcity.textContent = txt;
        ltWrap.appendChild(ltScarcity);
      }
      list.appendChild(ltWrap);
    }

    (info.tiers || []).forEach(function (tier) {
      var meta = TIERS[tier.name] || { label: tier.name, benefit: "" };
      // With an active plan only UPGRADES are offered — the current tier is
      // described above, and downgrades would be refused by the backend anyway.
      if ((TIER_RANK[tier.name] || 0) <= currentRank) return;
      var wrap = document.createElement("div");
      wrap.className = "border rounded p-2 mb-2";
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "btn btn-outline-primary btn-sm";
      btn.textContent = meta.label + " — " + fmtSats(tier.priceSats) + " / year";
      btn.addEventListener("click", function () { createInvoice(tier.name, btn); });
      var line = document.createElement("div");
      line.className = "small text-muted mt-1";
      var credits = Number(tier.creditGrantSats || 0);
      // Tier-differentiated per-BDO fee — served by the backend from the same
      // config the mint paths charge from; never hardcoded here.
      var mintFee = Number(tier.mintFeeSats || 0);
      line.textContent = meta.benefit +
        (credits > 0 ? " · includes " + credits.toLocaleString() + " credits/year" : "") +
        (mintFee > 0 ? " · " + mintFee.toLocaleString() + " credits per minted BDO" : "");
      wrap.appendChild(btn);
      wrap.appendChild(line);
      list.appendChild(wrap);
    });
  }

  function createInvoice(tierName, btn) {
    hide("[data-sub-error]");
    if (currentTier && currentTier !== tierName) requestId = newRequestId();
    currentTier = tierName;

    btn.disabled = true;
    var form = new FormData();
    form.append("tierName", tierName);
    form.append("clientRequestId", requestId);
    if (csrf) form.append("__RequestVerificationToken", csrf.value);

    fetch(urls.create, { method: "POST", body: form, credentials: "same-origin" })
      .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
      .then(function (res) {
        btn.disabled = false;
        if (!res.ok) {
          requestId = newRequestId();
          setError(res.j && res.j.message);
          return;
        }
        showInvoice(res.j);
      })
      .catch(function () { btn.disabled = false; setError("Network error."); });
  }

  function showInvoice(inv) {
    setText("[data-sub-amount]", fmtSats(inv.amountSats));
    var qr = el("[data-sub-qr]");
    if (qr) qr.src = "/plugins/smv/qr?data=" + encodeURIComponent(inv.bolt11);
    var bolt = el("[data-sub-bolt11]");
    if (bolt) bolt.value = inv.bolt11;
    var state = el("[data-sub-state]");
    if (state) state.className = "small text-muted mb-0";
    setText("[data-sub-state]", "Waiting for payment…");
    show("[data-sub-pay-line]");
    show("[data-sub-qr-wrap]");
    show("[data-sub-bolt11-row]");
    show("[data-sub-invoice]");

    if (pollTimer) clearInterval(pollTimer);
    var elapsed = 0;
    pollTimer = setInterval(function () {
      elapsed += 3;
      if (elapsed > 3600) { clearInterval(pollTimer); return; }
      fetch(urls.status + "&intentId=" + encodeURIComponent(inv.intentId), { credentials: "same-origin" })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (s) {
          if (!s) return;
          if (s.paid) {
            clearInterval(pollTimer);
            hide("[data-sub-pay-line]");
            hide("[data-sub-qr-wrap]");
            hide("[data-sub-bolt11-row]");
            var st = el("[data-sub-state]");
            if (st) st.className = "text-success fw-semibold mb-0";
            setText("[data-sub-state]", "Subscribed! Unlocking your new capabilities…");
            setTimeout(function () { window.location.reload(); }, 1800);
          }
        })
        .catch(function () { /* transient — keep polling */ });
    }, 3000);
  }

  var copyBtn = el("[data-sub-copy]");
  if (copyBtn) {
    copyBtn.addEventListener("click", function () {
      var bolt = el("[data-sub-bolt11]");
      if (bolt && navigator.clipboard) navigator.clipboard.writeText(bolt.value);
      copyBtn.textContent = "Copied";
      setTimeout(function () { copyBtn.textContent = "Copy invoice"; }, 1500);
    });
  }

  fetch(urls.info, { credentials: "same-origin" })
    .then(function (r) { return r.json(); })
    .then(renderInfo)
    .catch(function () {
      hide("[data-sub-loading]");
      setText("[data-sub-unavailable]", "Couldn't reach the platform.");
      show("[data-sub-unavailable]");
    });
})();
""";
        Response.Headers["Cache-Control"] = "no-store";
        return Content(js, "application/javascript");
    }

    [HttpGet("proof-inspector.js")]
    public IActionResult ProofInspectorJs()
    {
        const string js = """
(function () {
  "use strict";

  function setStatus(panel, text, cls) {
    var badge = panel.querySelector("[data-smv-status]");
    if (!badge) return;
    badge.textContent = text;
    badge.className = "badge " + cls;
  }

  function setError(panel, message) {
    var error = panel.querySelector(".smv-inspector-error");
    var errorMsg = panel.querySelector("[data-smv-error-message]");
    if (errorMsg) errorMsg.textContent = message || "Unknown error";
    if (error) error.classList.remove("d-none");
    setStatus(panel, "Failed", "bg-danger");
  }

  function reset(panel) {
    var loading = panel.querySelector(".smv-inspector-loading");
    var error = panel.querySelector(".smv-inspector-error");
    var notConfigured = panel.querySelector(".smv-inspector-not-configured");
    var fields = panel.querySelector("[data-smv-fields]");
    var rawBox = panel.querySelector(".smv-inspector-raw");

    if (loading) loading.classList.add("d-none");
    if (error) error.classList.add("d-none");
    if (notConfigured) notConfigured.classList.add("d-none");
    if (fields) {
      fields.innerHTML = "";
      fields.classList.add("d-none");
    }
    if (rawBox) rawBox.classList.add("d-none");
  }

            function addRow(fields, label, value, mono)
            {
                if (value === null || value === undefined || value === "") return;

                var dt = document.createElement("dt");
            dt.className = "col-sm-3";
            dt.textContent = label;

            var dd = document.createElement("dd");
            dd.className = "col-sm-9" + (mono ? " font-monospace text-break" : "");

            var text = String(value);

            if (mono && text.length > 24)
            {
                var shortText = text.substring(0, 8) + "..." + text.substring(text.length - 8);

                var span = document.createElement("span");
                span.textContent = shortText;
                span.title = text;

                var copyBtn = document.createElement("button");
                copyBtn.type = "button";
                copyBtn.className = "btn btn-link btn-sm ms-2 p-0";
                copyBtn.textContent = "copy";

                copyBtn.addEventListener("click", function() {
                    navigator.clipboard.writeText(text);
                });

                dd.appendChild(span);
                dd.appendChild(copyBtn);
            }
            else
            {
                dd.textContent = text;
            }

            fields.appendChild(dt);
            fields.appendChild(dd);
        }

        function render(panel, body) {
    var decoded = body.decoded || {};
    var fields = panel.querySelector("[data-smv-fields]");
    var raw = panel.querySelector("[data-smv-raw]");
    var rawBox = panel.querySelector(".smv-inspector-raw");

    if (!fields) return;

    function pick(obj, snake, camel) {
  if (!obj) return null;
  if (obj[snake] !== null && obj[snake] !== undefined) return obj[snake];
  if (obj[camel] !== null && obj[camel] !== undefined) return obj[camel];
  return null;
}

        var rawObj = body.raw;

        if (typeof rawObj === "string")
        {
            try
            {
                rawObj = JSON.parse(rawObj);
            }
            catch (e) { }
        }

        if (!decoded.assetType &&
            rawObj &&
            rawObj.decoded_proof &&
            rawObj.decoded_proof.asset &&
            rawObj.decoded_proof.asset.asset_genesis &&
            rawObj.decoded_proof.asset.asset_genesis.asset_type)
        {
            decoded.assetType = rawObj.decoded_proof.asset.asset_genesis.asset_type;
        }

        addRow(fields, "Asset Name", decoded.assetName, false);
addRow(fields, "Asset ID", decoded.assetId, true);
addRow(fields, "Asset Type", decoded.assetType, false);
addRow(fields, "Amount", decoded.amount, false);
addRow(fields, "Genesis Point", decoded.genesisPoint, true);
addRow(fields, "Anchor Outpoint", decoded.anchorOutpoint, true);
addRow(fields, "Block Height", decoded.blockHeight, false);
addRow(fields, "Meta Hash", decoded.metaHash, true);

var depth = pick(decoded, "proof_at_depth", "proofAtDepth");
var count = pick(decoded, "number_of_proofs", "numberOfProofs");

if (depth !== null && depth !== undefined) {
  addRow(fields, "Proof Depth", depth + " of " + (count || "?"), false);
}
    fields.classList.remove("d-none");

    if (raw && body.raw) {
      raw.textContent = JSON.stringify(body.raw, null, 2);
      if (rawBox) rawBox.classList.remove("d-none");
    }

    setStatus(panel, "Decoded", "bg-success");

    var redecode = panel.querySelector("[data-smv-redecode]");
    if (redecode) redecode.disabled = false;
  }

  async function decode(button, panel) {
    var endpoint = button.getAttribute("data-endpoint");
    if (!endpoint) {
      panel.classList.remove("d-none");
      setError(panel, "Missing inspect endpoint.");
      return;
    }

    panel.classList.remove("d-none");
    reset(panel);

    var loading = panel.querySelector(".smv-inspector-loading");
    var spinner = button.querySelector(".smv-inspect-spinner");

    if (loading) loading.classList.remove("d-none");
    if (spinner) spinner.classList.remove("d-none");

    setStatus(panel, "Decoding...", "bg-info");

    button.disabled = true;

    try {
      var response = await fetch(endpoint, {
        method: "POST",
        headers: {
          "Accept": "application/json"
        }
      });

      var body = await response.json();

      if (loading) loading.classList.add("d-none");

      if (response.status === 503 && body && body.error_kind === "NotConfigured") {
        var notConfigured = panel.querySelector(".smv-inspector-not-configured");
        if (notConfigured) notConfigured.classList.remove("d-none");
        setStatus(panel, "Not configured", "bg-warning text-dark");
        return;
      }

      if (!body || !body.ok) {
        var msg = "HTTP " + response.status;
        if (body) {
          msg = body.error || body.detail || body.error_message || msg;
          if (body.upstream_status) msg += " (upstream " + body.upstream_status + ")";
        }
        setError(panel, msg);
        return;
      }

      render(panel, body);
    } catch (e) {
      if (loading) loading.classList.add("d-none");
      setError(panel, "Network error: " + (e && e.message ? e.message : e));
    } finally {
      button.disabled = false;
      if (spinner) spinner.classList.add("d-none");
    }
  }

  function init() {
    document.querySelectorAll("[data-smv-inspect-proof]").forEach(function (button) {
      var assetId = button.getAttribute("data-asset-id");
      var panel = document.querySelector('[data-smv-inspector-panel][data-asset-id="' + assetId + '"]');

      if (!panel) return;

      button.addEventListener("click", function () {
        decode(button, panel);
      });

      var retry = panel.querySelector("[data-smv-retry]");
      if (retry) {
        retry.addEventListener("click", function () {
          decode(button, panel);
        });
      }

      var redecode = panel.querySelector("[data-smv-redecode]");
      if (redecode) {
        redecode.addEventListener("click", function () {
          decode(button, panel);
        });
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }
  [HttpGet("wallet.js")]
    public IActionResult WalletJs()
    {
        const string js = """
(function () {
  "use strict";

  function isValidTaprootAssetAddress(value) {
    if (!value) return false;
    // Mirror the server-side TaprootAssetAddress.HasValidPrefix: mainnet (tapbc1),
    // testnet (taptb1), regtest (taprt1). A Hosted wallet is mainnet -> tapbc1.
    var v = value.trim().toLowerCase();
    return v.startsWith("tapbc1") || v.startsWith("taptb1") || v.startsWith("taprt1");
  }

  async function copyText(text) {
    if (!text) return false;

    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch (e) {
      // Fall back below.
    }

    try {
      var textarea = document.createElement("textarea");
      textarea.value = text;
      textarea.setAttribute("readonly", "");
      textarea.style.position = "fixed";
      textarea.style.left = "-9999px";
      textarea.style.top = "-9999px";
      document.body.appendChild(textarea);
      textarea.focus();
      textarea.select();
      var ok = document.execCommand("copy");
      document.body.removeChild(textarea);
      return ok;
    } catch (e) {
      return false;
    }
  }

  function setText(element, text) {
    if (element) element.textContent = text;
  }

  function updateSendForm(form) {
    var addressInput = form.querySelector("[data-send-address]");
    var reviewButton = form.querySelector("[data-send-review]");
    var errorText = form.querySelector("[data-send-address-error]");

    if (!addressInput || !reviewButton || !errorText) return;

    var value = addressInput.value.trim();
    var isValid = isValidTaprootAssetAddress(value);

    reviewButton.disabled = !isValid;

    if (value.length === 0) {
      addressInput.classList.remove("is-valid");
      addressInput.classList.remove("is-invalid");
      errorText.classList.add("d-none");
      return;
    }

    if (isValid) {
      addressInput.classList.add("is-valid");
      addressInput.classList.remove("is-invalid");
      errorText.classList.add("d-none");
    } else {
      addressInput.classList.add("is-invalid");
      addressInput.classList.remove("is-valid");
      errorText.classList.remove("d-none");
    }
  }

  function resetReviewStatus(panel) {
    var status = panel.querySelector("[data-send-status]");
    var success = panel.querySelector("[data-send-success]");
    var error = panel.querySelector("[data-send-error]");
    var invoice = panel.querySelector("[data-send-invoice]");
    var progress = panel.querySelector("[data-send-confirmation-progress]");

    if (status) status.classList.add("d-none");
    if (success) success.classList.add("d-none");
    if (error) error.classList.add("d-none");
    if (invoice) invoice.classList.add("d-none");
    if (progress) {
      progress.style.width = "0%";
      progress.setAttribute("aria-valuenow", "0");
      progress.textContent = "";
    }
  }

  function showStatus(panel, title, message) {
    var status = panel.querySelector("[data-send-status]");
    var statusTitle = panel.querySelector("[data-send-status-title]");
    var statusMessage = panel.querySelector("[data-send-status-message]");

    if (statusTitle) statusTitle.textContent = title || "Working...";
    if (statusMessage) statusMessage.textContent = message || "";
    if (status) status.classList.remove("d-none");
  }

  function showSuccess(panel, title, message, txid, confirmations, required) {
    var status = panel.querySelector("[data-send-status]");
    var success = panel.querySelector("[data-send-success]");
    var successTitle = panel.querySelector("[data-send-success-title]");
    var successMessage = panel.querySelector("[data-send-success-message]");
    var successTxid = panel.querySelector("[data-send-success-txid]");
    var progress = panel.querySelector("[data-send-confirmation-progress]");

    if (status) status.classList.add("d-none");
    if (successTitle) successTitle.textContent = title || "Transfer submitted.";
    if (successMessage) successMessage.textContent = message || "";
    if (successTxid) successTxid.textContent = txid ? "TXID: " + txid : "";

    if (progress) {
      var safeRequired = required || 1;
      var safeConfirmations = confirmations || 0;
      var pct = Math.min(100, Math.round((safeConfirmations / safeRequired) * 100));
      progress.style.width = pct + "%";
      progress.setAttribute("aria-valuenow", String(pct));
      progress.textContent = safeConfirmations + "/" + safeRequired;
    }

    if (success) success.classList.remove("d-none");
  }

  function showError(panel, message) {
    var status = panel.querySelector("[data-send-status]");
    var error = panel.querySelector("[data-send-error]");
    var errorMessage = panel.querySelector("[data-send-error-message]");

    if (status) status.classList.add("d-none");
    if (errorMessage) errorMessage.textContent = message || "Unexpected error.";
    if (error) error.classList.remove("d-none");
  }

  // Hosted only: render the LN fee invoice and wire its copy button.
  function showInvoice(panel, payment) {
    if (!payment) return;
    var box = panel.querySelector("[data-send-invoice]");
    var value = panel.querySelector("[data-send-invoice-value]");
    var amount = panel.querySelector("[data-send-invoice-amount]");
    var expiry = panel.querySelector("[data-send-invoice-expiry]");
    var copyBtn = panel.querySelector("[data-send-invoice-copy]");
    var qr = panel.querySelector("[data-send-invoice-qr]");
    var invoice = payment.invoice || "";

    if (value) value.textContent = invoice;
    if (qr && invoice) {
      // Server-rendered QR (plugin endpoint); scannable with a phone LN wallet.
      qr.src = "/plugins/smv/qr?data=" + encodeURIComponent(invoice);
      qr.classList.remove("d-none");
    }
    if (amount) amount.textContent = (payment.amount_sats || 0) + " sats fee";
    if (expiry && payment.expires_at) {
      expiry.textContent = "expires " + payment.expires_at;
      expiry.classList.remove("d-none");
    }
    if (copyBtn) {
      copyBtn.onclick = async function () {
        var original = copyBtn.textContent;
        var ok = await copyText(invoice);
        copyBtn.textContent = ok ? "Copied" : "Copy failed";
        setTimeout(function () { copyBtn.textContent = original; }, 1200);
      };
    }
    if (box) box.classList.remove("d-none");
  }

  function hideInvoice(panel) {
    var box = panel.querySelector("[data-send-invoice]");
    if (box) box.classList.add("d-none");
  }

  function openReview(form) {
    var addressInput = form.querySelector("[data-send-address]");
    var amountInput = form.querySelector("[data-send-amount]");
    var reviewTargetInput = form.querySelector("[data-send-review-target]");
    var reviewTarget = reviewTargetInput ? reviewTargetInput.value : "";
    var reviewPanel = reviewTarget ? document.querySelector(reviewTarget) : null;

    if (!addressInput || !amountInput || !reviewPanel) return;

    document.querySelectorAll("[data-review-panel]").forEach(function (panel) {
      if (panel === reviewPanel) return;

      if (window.bootstrap && window.bootstrap.Collapse) {
        var otherCollapse = window.bootstrap.Collapse.getOrCreateInstance(panel, { toggle: false });
        otherCollapse.hide();
      } else {
        panel.classList.remove("show");
      }
    });

    var assetName = form.getAttribute("data-send-asset-name") || "-";
    var assetType = form.getAttribute("data-send-asset-type") || "-";
    var assetId = form.getAttribute("data-send-asset-id") || "";
    var recipientAddress = addressInput.value.trim();
    var amount = amountInput.value || amountInput.getAttribute("value") || "1";

    var reviewAssetName = reviewPanel.querySelector("[data-review-asset-name]");
    var reviewRecipient = reviewPanel.querySelector("[data-review-recipient]");
    var reviewAmount = reviewPanel.querySelector("dd[data-review-amount]");
    var reviewType = reviewPanel.querySelector("[data-review-type]");
    var reviewAssetId = reviewPanel.querySelector("input[data-review-asset-id]");
    var reviewAmountHidden = reviewPanel.querySelector("input[data-review-amount]");

    if (reviewAssetName) reviewAssetName.textContent = assetName;
    if (reviewRecipient) reviewRecipient.textContent = recipientAddress;
    if (reviewAmount) reviewAmount.textContent = amount;
    if (reviewType) reviewType.textContent = assetType;
    if (reviewAssetId) reviewAssetId.value = assetId;
    if (reviewAmountHidden) reviewAmountHidden.value = amount;

    resetReviewStatus(reviewPanel);

    var confirmButton = reviewPanel.querySelector("[data-confirm-send]");
    if (confirmButton) {
      confirmButton.disabled = false;
      confirmButton.textContent = "Confirm Send";
    }

    if (window.bootstrap && window.bootstrap.Collapse) {
      var collapse = window.bootstrap.Collapse.getOrCreateInstance(reviewPanel, { toggle: false });
      collapse.show();
      reviewPanel.scrollIntoView({ behavior: "smooth", block: "nearest" });
    } else {
      reviewPanel.classList.add("show");
    }
  }

  async function pollSendStatus(ref, panel, isHosted) {
    if (!ref || !panel) return;

    // Endpoint is supplied by the view (Store-scoped); the script never builds it.
    var walletCfg = document.querySelector("[data-smv-wallet]");
    var statusTemplate = walletCfg ? walletCfg.getAttribute("data-send-status-endpoint") : null;
    if (!statusTemplate) return;
    var statusUrl = statusTemplate.replace("__TXID__", encodeURIComponent(ref));

    var attempts = 0;
    // Hosted polls fast (1s) so a paid invoice / broadcast reflects near-instantly
    // once the backend reports it; BYON tracks on-chain confirmations (2s is plenty).
    var pollMs = isHosted ? 1000 : 2000;
    // Keep the overall budget: ~5 min for Hosted (a human pays), ~2 min for BYON.
    var maxAttempts = isHosted ? 300 : 60;

    if (!isHosted) {
      showSuccess(
        panel,
        "Transfer submitted.",
        "Waiting for blockchain confirmation...",
        ref,
        0,
        1);
    }

    var timer = setInterval(async function () {
      attempts++;

      try {
        var response = await fetch(statusUrl, {
          headers: {
            "Accept": "application/json"
          }
        });

        var body = await response.json();
        var confirmations = body.confirmations || 0;
        var required = body.required || 1;
        var state = body.state || "";
        var message = body.message || "";

        if (isHosted) {
          // Hosted status domain: pending_payment -> paid -> broadcasting -> fulfilled.
          if (state === "fulfilled") {
            clearInterval(timer);
            hideInvoice(panel);
            showSuccess(panel, "Transfer complete ✓", message || "Transfer fulfilled. Refreshing wallet...", ref, 1, 1);
            setTimeout(function () { window.location.reload(); }, 1500);
            return;
          }
          if (state === "failed" || state === "cancelled") {
            clearInterval(timer);
            hideInvoice(panel);
            showError(panel, message || "Transfer failed.");
            return;
          }
          // Once the fee is paid the invoice is no longer actionable.
          if (state === "paid" || state === "broadcasting") {
            hideInvoice(panel);
          }
          showStatus(panel, "Transfer in progress", message || "Working...");
        } else {
          var byonMessage = message || "Waiting for blockchain confirmation...";

          if (state === "confirmed" || confirmations >= required) {
            clearInterval(timer);

            showSuccess(
              panel,
              "Transfer confirmed ✓",
              "Transaction confirmed. Refreshing wallet...",
              ref,
              confirmations,
              required);

            setTimeout(function () {
              window.location.reload();
            }, 1500);

            return;
          }

          showSuccess(
            panel,
            "Transfer submitted.",
            byonMessage,
            ref,
            confirmations,
            required);
        }
      } catch (e) {
        if (!isHosted) {
          showSuccess(
            panel,
            "Transfer submitted.",
            "Waiting for confirmation...",
            ref,
            0,
            1);
        }
      }

      if (attempts >= maxAttempts) {
        clearInterval(timer);
        if (isHosted) {
          showStatus(panel, "Still waiting", "The transfer is taking longer than expected. You can refresh the wallet later to check its status.");
        } else {
          showSuccess(
            panel,
            "Transfer submitted.",
            "Still waiting for confirmation. You can refresh the wallet later.",
            ref,
            0,
            1);
        }
      }
    }, pollMs);
  }

  function init() {
    // Hide any asset thumbnail whose image fails to load (CSP-safe; no inline
    // onerror, which the nonce-based CSP would block).
    document.querySelectorAll("img[data-asset-thumb]").forEach(function (img) {
      img.addEventListener("error", function () { img.style.display = "none"; });
    });

    document.querySelectorAll("[data-send-form]").forEach(function (form) {
      var addressInput = form.querySelector("[data-send-address]");
      var reviewButton = form.querySelector("[data-send-review]");

      if (!addressInput) return;

      updateSendForm(form);

      addressInput.addEventListener("input", function () {
        updateSendForm(form);
      });

      if (reviewButton) {
        reviewButton.addEventListener("click", function () {
          openReview(form);
        });
      }
    });

    document.querySelectorAll("[data-confirm-send]").forEach(function (button) {
      button.addEventListener("click", async function () {
        var panel = button.closest("[data-review-panel]");
        if (!panel) return;

        var recipient = panel.querySelector("[data-review-recipient]");
        var assetIdInput = panel.querySelector("input[data-review-asset-id]");
        var amountInput = panel.querySelector("input[data-review-amount]");

        var address = recipient ? recipient.textContent.trim() : "";
        var assetId = assetIdInput ? assetIdInput.value : "";
        var amount = amountInput ? parseInt(amountInput.value || "1", 10) : 1;

        resetReviewStatus(panel);
        showStatus(panel, "Sending...", "Submitting transfer...");

        button.disabled = true;
        button.textContent = "Sending...";

        try {
          // Endpoint is supplied by the view (Store-scoped); the script never builds it.
          var walletCfg = document.querySelector("[data-smv-wallet]");
          var sendEndpoint = walletCfg ? walletCfg.getAttribute("data-send-endpoint") : null;
          if (!sendEndpoint) throw new Error("Send endpoint is unavailable.");

          var sendHeaders = {
            "Content-Type": "application/json",
            "Accept": "application/json"
          };
          // Antiforgery (CSRF) token for the cookie-authenticated POST. Without it,
          // BTCPay rejects the request before the controller with an empty body.
          var tokenInput = document.querySelector("input[name='__RequestVerificationToken']");
          if (tokenInput && tokenInput.value) {
            sendHeaders["RequestVerificationToken"] = tokenInput.value;
          }

          var response = await fetch(sendEndpoint, {
            method: "POST",
            headers: sendHeaders,
            body: JSON.stringify({
              asset_id: assetId,
              address: address,
              amount: amount
            })
          });

          // Parse defensively: an auth/antiforgery rejection can return an empty or
          // non-JSON body. Blind JSON.parse there yields the opaque
          // "Unexpected end of JSON input" instead of a useful message.
          var rawBody = await response.text();
          var body = {};
          if (rawBody) {
            try { body = JSON.parse(rawBody); } catch (parseErr) { body = {}; }
          }

          if (!response.ok) {
            throw new Error(body.error || ("Send failed (HTTP " + response.status + ")."));
          }

          if (body.payment && body.payment.invoice) {
            // Hosted: the merchant must pay the LN fee invoice; then we poll the
            // transfer_ref through the hosted status domain until fulfilled.
            showInvoice(panel, body.payment);
            showStatus(panel, "Awaiting Lightning payment", "Pay the invoice above to start the transfer.");

            button.disabled = true;
            button.textContent = "Awaiting payment";

            var ref = body.transfer_id || "";
            if (ref) {
              pollSendStatus(ref, panel, true);
            }
          } else {
            // BYON: broadcast immediately, poll on-chain confirmations.
            var txid = body.anchor_txid || body.transfer_id || "";

            showSuccess(
              panel,
              "Transfer submitted.",
              txid ? "Transaction broadcast. Waiting for confirmation..." : "Transfer submitted.",
              txid,
              0,
              1);

            button.disabled = true;
            button.textContent = "Sent";

            if (txid) {
              pollSendStatus(txid, panel, false);
            }
          }
        } catch (e) {
          showError(panel, e && e.message ? e.message : "Unexpected error.");
          button.disabled = false;
          button.textContent = "Confirm Send";
        }
      });
    });

    document.querySelectorAll("[data-review-cancel]").forEach(function (button) {
      button.addEventListener("click", function () {
        var panel = button.closest("[data-review-panel]");
        if (!panel) return;

        if (window.bootstrap && window.bootstrap.Collapse) {
          var collapse = window.bootstrap.Collapse.getOrCreateInstance(panel, { toggle: false });
          collapse.hide();
        } else {
          panel.classList.remove("show");
        }
      });
    });

    document.querySelectorAll("[data-copy-text]").forEach(function (button) {
      button.addEventListener("click", async function () {
        var text = button.getAttribute("data-copy-text");
        if (!text) return;

        var original = button.textContent;
        var ok = await copyText(text);

        button.textContent = ok ? "Copied" : "Copy failed";
        setTimeout(function () {
          button.textContent = original;
        }, 1200);
      });
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // Live create/mint flow (Plugin-I3). Polls the Store-scoped mint-status endpoint
    // (supplied by the view, never built here) and drives the invoice → minting →
    // minted / refunded_credit / failed transitions. CSP-safe: external script, no
    // inline handlers. Mirrors wallet.js's structure and its 1s Hosted poll cadence.
    [HttpGet("create.js")]
    public IActionResult CreateJs()
    {
        const string js = """
(function () {
  "use strict";

  async function copyText(text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch (e) {}
    try {
      var ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand("copy");
      document.body.removeChild(ta);
      return ok;
    } catch (e) { return false; }
  }

  function hide(el) { if (el) el.classList.add("d-none"); }
  function show(el) { if (el) el.classList.remove("d-none"); }

  function showStatus(panel, title, message) {
    var box = panel.querySelector("[data-mint-status]");
    var t = panel.querySelector("[data-mint-status-title]");
    var m = panel.querySelector("[data-mint-status-message]");
    if (t && title) t.textContent = title;
    if (m) m.textContent = message || "";
    show(box);
  }

  function terminal(panel) {
    hide(panel.querySelector("[data-mint-invoice]"));
    hide(panel.querySelector("[data-mint-status]"));
  }

  function pollMintStatus(ref, panel) {
    var cfg = document.querySelector("[data-smv-mint]");
    if (!cfg) return;
    var statusTpl = cfg.getAttribute("data-mint-status-endpoint");
    var verifyTpl = cfg.getAttribute("data-verify-url");
    if (!statusTpl) return;
    var statusUrl = statusTpl.replace("__REF__", encodeURIComponent(ref));

    var attempts = 0;
    // ~25 min budget: a mint reaches `minted` only after >=1 on-chain confirmation,
    // which on mainnet is ~10 min. Poll fast at first to catch the payment, then
    // ease off for the confirmation wait (60x1s + 340x5s).
    var maxAttempts = 400;
    var done = false;

    function schedule() {
      if (done) return;
      if (attempts >= maxAttempts) {
        showStatus(panel, "Still working",
          "This is taking longer than expected — your BDO will appear in My BDOs once it confirms on-chain.");
        return;
      }
      setTimeout(tick, attempts < 60 ? 1000 : 5000);
    }

    async function tick() {
      attempts++;
      try {
        var res = await fetch(statusUrl, { headers: { "Accept": "application/json" } });
        var body = await res.json();
        var state = body.state || "";
        var message = body.message || "";

        if (state === "minted") {
          done = true;
          terminal(panel);
          var msg = panel.querySelector("[data-mint-success-message]");
          // The BDO is minted, but its public proof/holding register a moment later, so
          // set an honest expectation rather than pushing straight to Verify (which would
          // race and fail for a few seconds).
          if (msg) msg.textContent = "It's confirming on Bitcoin — it will appear in My BDOs, and become verifiable, within a few minutes.";
          var bdo = panel.querySelector("[data-mint-success-bdoid]");
          if (bdo && body.bdo_id) bdo.textContent = body.bdo_id;
          show(panel.querySelector("[data-mint-success]"));
          return;
        }

        if (state === "refunded_credit") {
          done = true;
          terminal(panel);
          var rm = panel.querySelector("[data-mint-refund-message]");
          if (rm) rm.textContent = message || ("You were refunded " + (body.refund_credit_sats || 0) + " sats as credit.");
          show(panel.querySelector("[data-mint-refund]"));
          return;
        }

        if (state === "failed") {
          done = true;
          terminal(panel);
          var em = panel.querySelector("[data-mint-error-message]");
          if (em) em.textContent = message || "Minting failed.";
          show(panel.querySelector("[data-mint-error]"));
          return;
        }

        if (state === "minting") {
          // Fee is paid; the invoice is no longer actionable.
          hide(panel.querySelector("[data-mint-invoice]"));
          showStatus(panel, "Minting your BDO…",
            message || "Payment received — minting your BDO… (confirming on-chain, this can take a few minutes)");
        }
        // awaiting_payment: leave the invoice visible, no extra chrome.
      } catch (e) {
        // transient read error: keep polling
      }
      schedule();
    }

    tick();
  }

  function init() {
    // Collection picker: show the "new collection" fields only when "create new" is
    // selected; when reusing an existing collection, hide them and show the reuse note.
    var collSelect = document.querySelector("#CollectionChoice");
    if (collSelect) {
      var newFields = document.querySelector("[data-new-collection-fields]");
      var reuseNote = document.querySelector("[data-reuse-note]");
      var syncColl = function () {
        var creatingNew = collSelect.value === "__new__";
        if (newFields) newFields.classList.toggle("d-none", !creatingNew);
        if (reuseNote) reuseNote.classList.toggle("d-none", creatingNew);
      };
      collSelect.addEventListener("change", syncColl);
      syncColl();
    }

    document.querySelectorAll("[data-copy-invoice]").forEach(function (button) {
      button.addEventListener("click", async function () {
        var text = button.getAttribute("data-copy-invoice");
        if (!text) return;
        var original = button.textContent;
        var ok = await copyText(text);
        button.textContent = ok ? "Copied" : "Copy failed";
        setTimeout(function () { button.textContent = original; }, 1200);
      });
    });

    var panel = document.querySelector("[data-smv-mint-panel]");
    if (panel) {
      var ref = panel.getAttribute("data-mint-ref");
      if (ref) pollMintStatus(ref, panel);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // BYON create flow (RFC-PLUGIN-006 P2-2). Intercepts the create form: Prepare
    // (server computes the metadata_hash) → the creator signs it with their NIP-07
    // Nostr extension (authorship, BEFORE minting) → the signed event is written to a
    // hidden field and the form submits (server mints + registers). CSP-safe.
    [HttpGet("byon-create.js")]
    public IActionResult ByonCreateJs()
    {
        const string js = """
(function () {
  "use strict";

  function tokenHeaders(base) {
    var t = document.querySelector("input[name='__RequestVerificationToken']");
    if (t) base["RequestVerificationToken"] = t.value;
    return base;
  }

  async function run(form) {
    var statusEl = form.querySelector("[data-byon-status]");
    var submitBtn = form.querySelector("[data-byon-create-btn]");
    var hidden = form.querySelector("[data-byon-signed-event]");
    var prepareUrl = form.getAttribute("data-prepare-endpoint");

    function status(msg, cls) {
      if (!statusEl) return;
      statusEl.textContent = msg;
      statusEl.className = "small mt-2 " + (cls || "text-muted");
    }
    function reset() { if (submitBtn) submitBtn.disabled = false; }

    if (submitBtn) submitBtn.disabled = true;

    var data = {
      AssetName: (form.querySelector("#AssetName") || {}).value || "",
      ImageUrl: (form.querySelector("#ImageUrl") || {}).value || "",
      Description: (form.querySelector("#Description") || {}).value || "",
      AttributesText: (form.querySelector("#AttributesText") || {}).value || "",
      ExternalReference: (form.querySelector("#ExternalReference") || {}).value || ""
    };

    // 1. Prepare — the server computes the canonical STAS-01 metadata_hash.
    status("Preparing your BDO…");
    var prep;
    try {
      var r = await fetch(prepareUrl, { method: "POST", headers: tokenHeaders({ "Content-Type": "application/json" }), body: JSON.stringify(data) });
      prep = await r.json();
      if (!r.ok || !prep || !prep.ok || !prep.metadata_hash) { status(prep && prep.message ? prep.message : "Could not prepare the BDO.", "text-danger"); reset(); return; }
    } catch (e) { status("Could not reach the server. Try again.", "text-danger"); reset(); return; }

    // 2. Creator Signature — sign the metadata_hash (authorship) BEFORE minting.
    if (!window.nostr || typeof window.nostr.signEvent !== "function") {
      status("No Nostr signer found. Install a NIP-07 extension (Alby or nos2x), or untick “Sign as the creator” above to create without a signature.", "text-danger");
      reset(); return;
    }
    status("Waiting for your Creator Signature…");
    var event = {
      kind: 30078,
      created_at: Math.floor(Date.now() / 1000),
      content: "",
      // Tag order and literals are contract-fixed — the verifier recomputes the event id
      // over exactly this structure (RFC-PLUGIN-006 §3).
      tags: [
        ["purpose", "supermultiverse_asset_signature"],
        ["domain", "supermultiverse"],
        ["schema_version", "1"],
        ["metadata_hash", prep.metadata_hash]
      ]
    };
    var signed;
    try { signed = await window.nostr.signEvent(event); }
    catch (e) { status("Signing was cancelled.", "text-warning"); reset(); return; }
    if (!signed || !signed.id || !signed.pubkey || !signed.sig) { status("The signer returned an incomplete signature.", "text-danger"); reset(); return; }

    // 3. Submit — the form now carries the signature; the server mints + registers.
    status("Signed ✓ — minting on your node…");
    if (hidden) hidden.value = JSON.stringify(signed);
    form.submit(); // programmatic submit skips this handler → no re-intercept
  }

  function init() {
    var form = document.querySelector("[data-byon-create-form]");
    if (!form) return;
    form.addEventListener("submit", function (e) {
      var hidden = form.querySelector("[data-byon-signed-event]");
      if (hidden && hidden.value) return; // already signed → let it submit
      var toggle = form.querySelector("[data-byon-sign-toggle]");
      if (toggle && !toggle.checked) return; // signing opt-out → submit unsigned
      e.preventDefault();
      run(form);
    });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // A lightweight navigation loader: shows a spinner overlay when the user follows a
    // link or submits a form on any plugin page, so slow tab/collection loads give
    // feedback instead of looking unresponsive. CSP-safe (served, no inline script).
    // Included on every plugin page via _SmvPluginNav; skips new-tab, anchor, download,
    // copy and Bootstrap-toggle (Send/Info) controls, and self-clears after 12 s.
    [HttpGet("loader.js")]
    public IActionResult LoaderJs()
    {
        const string js = """
(function () {
  // CSP-safe: hide any asset thumbnail whose image 404s so the "BDO" placeholder
  // underneath shows through. Capture phase — the error event does not bubble.
  document.addEventListener('error', function (e) {
    var t = e.target;
    if (t && t.tagName === 'IMG' && t.hasAttribute('data-asset-thumb')) {
      t.style.display = 'none';
    }
  }, true);

  var overlay = document.getElementById('smv-loading-overlay');
  if (!overlay) return;
  var t;
  function show() { overlay.style.display = 'flex'; clearTimeout(t); t = setTimeout(hide, 12000); }
  function hide() { overlay.style.display = 'none'; clearTimeout(t); }

  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a') : null;
    if (!a) return;
    var href = a.getAttribute('href');
    if (!href || href.charAt(0) === '#') return;
    if (a.getAttribute('target') === '_blank') return;
    if (a.getAttribute('data-bs-toggle')) return;
    if (a.hasAttribute('download')) return;
    if (a.hasAttribute('data-copy-text') || a.hasAttribute('data-copy-invoice')) return;
    if (a.hasAttribute('data-smv-oauth-popup')) return; // opens a popup, not a navigation
    if (e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
    show();
  }, true);

  document.addEventListener('submit', function () { show(); }, true);

  window.addEventListener('pageshow', hide);
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // OAuth Connect popup (RFC-PLUGIN-007). Two behaviours in one file:
    //  • On the callback close-page (has [data-smv-oauth-close]): reload the opener and
    //    close this window — so the merchant lands back on Settings, already updated.
    //  • On Settings (links with [data-smv-oauth-popup]): open Connect/Reconnect in a
    //    popup so the plugin stays anchored; when the popup closes, reload Settings.
    // The login itself still happens on supermultiverse.io (the plugin never sees the
    // password — that is the whole point of OAuth); the popup only keeps you anchored.
    [HttpGet("oauth-popup.js")]
    public IActionResult OAuthPopupJs()
    {
        const string js = """
(function () {
  if (document.querySelector('[data-smv-oauth-close]')) {
    try { if (window.opener && !window.opener.closed) window.opener.location.reload(); } catch (e) {}
    window.close();
    setTimeout(function () {
      document.body.innerHTML = '<p style="padding:3rem;font-family:system-ui,sans-serif">Connected. You can close this window and return to BTCPay.</p>';
    }, 500);
    return;
  }
  var links = document.querySelectorAll('[data-smv-oauth-popup]');
  for (var i = 0; i < links.length; i++) {
    links[i].addEventListener('click', function (e) {
      e.preventDefault();
      var href = this.getAttribute('href');
      var url = href + (href.indexOf('?') >= 0 ? '&' : '?') + 'popup=true';
      var w = 520, h = 700;
      var left = Math.max(0, (window.screen.width - w) / 2);
      var top = Math.max(0, (window.screen.height - h) / 2);
      var win = window.open(url, 'smv-oauth', 'width=' + w + ',height=' + h + ',left=' + left + ',top=' + top);
      if (!win) { window.location.href = href; return; } // popup blocked -> full-page fallback
      var timer = setInterval(function () {
        if (win.closed) { clearInterval(timer); window.location.reload(); }
      }, 700);
    });
  }
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // Settings backend toggle: show the Hosted or BYON section matching the dropdown
    // instantly (no save needed for the form to switch). Both sections are in the DOM;
    // the POST only consumes the active mode's fields, so this is purely presentational.
    // Drops (RFC-PLUGIN-010): create/list/cancel drop campaigns from the
    // collection page. One URL/QR dispenses the series first come, first
    // served — the live-event feature. External file (CSP).
    [HttpGet("drops.js")]
    public IActionResult DropsJs()
    {
        const string js = """
(function () {
  "use strict";

  var section = document.querySelector("[data-drop-section]");
  if (!section) return;

  var csrf = document.querySelector("input[name='__RequestVerificationToken']");
  var urls = {
    create: section.getAttribute("data-url-create"),
    list: section.getAttribute("data-url-list"),
    cancel: section.getAttribute("data-url-cancel")
  };
  var unitIds = (section.getAttribute("data-unit-ids") || "").split(",").filter(Boolean);
  var listBox = section.querySelector("[data-drop-list]");

  function setError(msg) {
    var n = section.querySelector("[data-drop-error]");
    if (n) { n.textContent = msg || "Something went wrong."; n.classList.remove("d-none"); }
  }
  function clearError() {
    var n = section.querySelector("[data-drop-error]");
    if (n) n.classList.add("d-none");
  }

  function renderDrop(d) {
    var wrap = document.createElement("div");
    wrap.className = "border rounded p-2 mb-2";
    var head = document.createElement("div");
    head.className = "d-flex justify-content-between align-items-center flex-wrap gap-2";
    var title = document.createElement("div");
    title.innerHTML = "<strong></strong> <span class='text-muted small'></span>";
    title.querySelector("strong").textContent = d.name || "Drop";
    title.querySelector("span").textContent = d.claimed + " of " + d.total + " claimed";
    var cancelBtn = document.createElement("button");
    cancelBtn.type = "button";
    cancelBtn.className = "btn btn-link btn-sm text-danger p-0";
    cancelBtn.textContent = "Close drop";
    cancelBtn.addEventListener("click", function () {
      clearError();
      cancelBtn.disabled = true;
      var fd = new FormData();
      fd.append("campaignId", d.campaignId);
      if (csrf) fd.append("__RequestVerificationToken", csrf.value);
      fetch(urls.cancel, { method: "POST", body: fd, credentials: "same-origin" })
        .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
        .then(function (res) {
          if (!res.ok) { cancelBtn.disabled = false; setError(res.j && res.j.message); return; }
          wrap.remove();
        })
        .catch(function () { cancelBtn.disabled = false; setError("Network error."); });
    });
    head.appendChild(title);
    head.appendChild(cancelBtn);

    var urlGroup = document.createElement("div");
    urlGroup.className = "input-group input-group-sm mt-2";
    var input = document.createElement("input");
    input.type = "text"; input.readOnly = true;
    input.className = "form-control font-monospace";
    input.value = d.dropUrl;
    var copyBtn = document.createElement("button");
    copyBtn.type = "button";
    copyBtn.className = "btn btn-outline-secondary";
    copyBtn.textContent = "Copy link";
    copyBtn.addEventListener("click", function () {
      if (navigator.clipboard) navigator.clipboard.writeText(input.value);
      copyBtn.textContent = "Copied";
      setTimeout(function () { copyBtn.textContent = "Copy link"; }, 1500);
    });
    urlGroup.appendChild(input);
    urlGroup.appendChild(copyBtn);

    var qrWrap = document.createElement("div");
    qrWrap.className = "text-center mt-2";
    var qr = document.createElement("img");
    qr.alt = "Drop QR";
    qr.className = "img-fluid rounded bg-white p-2";
    qr.style.maxWidth = "200px";
    qr.src = "/plugins/smv/qr?data=" + encodeURIComponent(d.dropUrl);
    qrWrap.appendChild(qr);

    wrap.appendChild(head);
    wrap.appendChild(urlGroup);
    wrap.appendChild(qrWrap);
    listBox.appendChild(wrap);
  }

  function loadDrops() {
    fetch(urls.list, { credentials: "same-origin" })
      .then(function (r) { return r.ok ? r.json() : { drops: [] }; })
      .then(function (j) {
        if (!listBox) return;
        listBox.innerHTML = "";
        (j.drops || []).forEach(renderDrop);
      })
      .catch(function () {});
  }

  var createBtn = section.querySelector("[data-drop-create]");
  if (createBtn) createBtn.addEventListener("click", function () {
    clearError();
    var count = parseInt((section.querySelector("[data-drop-count]") || {}).value, 10) || unitIds.length;
    var name = ((section.querySelector("[data-drop-name]") || {}).value || "").trim() ||
      (section.getAttribute("data-collection-name") || "Drop");
    createBtn.disabled = true;
    var reward = parseInt((section.querySelector("[data-drop-reward]") || {}).value, 10) || 0;
    var fd = new FormData();
    fd.append("name", name);
    fd.append("unitIds", unitIds.join(","));
    fd.append("count", String(count));
    fd.append("rewardCredits", String(reward));
    if (csrf) fd.append("__RequestVerificationToken", csrf.value);
    fetch(urls.create, { method: "POST", body: fd, credentials: "same-origin" })
      .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
      .then(function (res) {
        createBtn.disabled = false;
        if (!res.ok) { setError(res.j && res.j.message); return; }
        loadDrops();
      })
      .catch(function () { createBtn.disabled = false; setError("Network error."); });
  });

  loadDrops();
})();
""";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // Custody note collapse (My BDOs): important but intrusive — the merchant can
    // fold the explanation and the choice persists per browser (localStorage), so
    // it isn't re-hidden every session. The one-line custody FACT stays visible.
    [HttpGet("custody-note.js")]
    public IActionResult CustodyNoteJs()
    {
        const string js = """
(function () {
  "use strict";
  var note = document.querySelector("[data-custody-note]");
  if (!note) return;
  var body = note.querySelector("[data-custody-body]");
  var toggle = note.querySelector("[data-custody-toggle]");
  if (!body || !toggle) return;
  var KEY = "smv-custody-note-collapsed";

  function setCollapsed(collapsed, persist) {
    body.style.display = collapsed ? "none" : "";
    toggle.textContent = collapsed ? "Details" : "Hide";
    if (persist) {
      try { localStorage.setItem(KEY, collapsed ? "1" : "0"); } catch (e) { /* private mode */ }
    }
  }

  var stored = null;
  try { stored = localStorage.getItem(KEY); } catch (e) { /* private mode */ }
  setCollapsed(stored === "1", false);

  toggle.addEventListener("click", function () {
    setCollapsed(body.style.display !== "none", true);
  });
})();
""";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    [HttpGet("settings-backend.js")]
    public IActionResult SettingsBackendJs()
    {
        const string js = """
(function () {
  var sel = document.querySelector('[data-smv-backend-select]');
  if (!sel) return;
  var sections = document.querySelectorAll('[data-smv-backend-section]');
  function apply() {
    for (var i = 0; i < sections.length; i++) {
      sections[i].style.display =
        (sections[i].getAttribute('data-smv-backend-section') === sel.value) ? '' : 'none';
    }
  }
  // The selector IS the action: changing the backend auto-submits the form so
  // the mode is persisted immediately — no Save to remember (the POST handler
  // only consumes the active mode's fields and blank credentials mean "keep",
  // so submitting right away can't lose anything). Credential EDITS in BYON
  // still use the explicit Save button inside that section.
  sel.addEventListener('change', function () {
    apply();
    var form = sel.closest('form');
    if (form) {
      // NEVER disable the select before submitting — disabled controls are
      // excluded from the POST, so BackendMode arrived empty and the binder
      // fell back to the default (v0.16.2 bug: impossible to leave BYON).
      // pointer-events gives the same "locked while saving" feel and still posts.
      sel.style.pointerEvents = 'none';
      sel.setAttribute('aria-busy', 'true');
      if (form.requestSubmit) form.requestSubmit(); else form.submit();
    }
  });
  apply();
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // Batch submit + phase-based poll (mirrors create.js). A batch reaches `minted` only
    // after the aggregate mint + on-chain confirm (a few minutes); `minted` in the batch
    // status jumps 0 → N atomically, so the UI shows the PHASE, not a per-unit bar.
    [HttpGet("batch.js")]
    public IActionResult BatchJs()
    {
        const string js = """
(function () {
  "use strict";

  async function copyText(text) {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch (e) {}
    try {
      var ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand("copy");
      document.body.removeChild(ta);
      return ok;
    } catch (e) { return false; }
  }

  function hide(el) { if (el) el.classList.add("d-none"); }
  function show(el) { if (el) el.classList.remove("d-none"); }
  function setCardTitle(panel, text) { var el = panel.querySelector("[data-batch-card-title]"); if (el) el.textContent = text; }

  function showStatus(panel, title, message) {
    var box = panel.querySelector("[data-batch-status]");
    var t = panel.querySelector("[data-batch-status-title]");
    var m = panel.querySelector("[data-batch-status-message]");
    if (t && title) t.textContent = title;
    if (m) m.textContent = message || "";
    show(box);
  }

  function terminal(panel) {
    hide(panel.querySelector("[data-batch-invoice]"));
    hide(panel.querySelector("[data-batch-status]"));
  }

  function pollBatchStatus(ref, panel) {
    var cfg = document.querySelector("[data-smv-batch]");
    if (!cfg) return;
    var statusTpl = cfg.getAttribute("data-batch-status-endpoint");
    var collBase = cfg.getAttribute("data-collection-base");
    if (!statusTpl) return;
    var statusUrl = statusTpl.replace("__REF__", encodeURIComponent(ref));

    var attempts = 0;
    var maxAttempts = 400;
    var done = false;

    function schedule() {
      if (done) return;
      if (attempts >= maxAttempts) {
        showStatus(panel, "Still working",
          "This is taking longer than expected — your series will appear in My BDOs once it confirms on-chain.");
        return;
      }
      setTimeout(tick, attempts < 60 ? 1000 : 5000);
    }

    async function tick() {
      attempts++;
      try {
        var res = await fetch(statusUrl, { headers: { "Accept": "application/json" } });
        var body = await res.json();
        var state = body.state || "";
        var message = body.message || "";
        var total = body.total || 0;

        if (state === "minted") {
          done = true;
          terminal(panel);
          setCardTitle(panel, "Your batch is minted");
          var msg = panel.querySelector("[data-batch-success-message]");
          if (msg) msg.textContent = "Your series of " + total + " BDOs is confirming on Bitcoin — it will appear in My BDOs within a few minutes.";
          var link = panel.querySelector("[data-batch-collection-link]");
          if (link && collBase && body.collection_id) {
            link.setAttribute("href", collBase.replace("__ID__", encodeURIComponent(body.collection_id)));
            link.textContent = "View the series →";
          }
          if (link) show(link);
          show(panel.querySelector("[data-batch-success]"));
          return;
        }

        if (state === "refunded_credit") {
          done = true;
          terminal(panel);
          setCardTitle(panel, "Batch refunded as credit");
          var rm = panel.querySelector("[data-batch-refund-message]");
          if (rm) rm.textContent = message || ("You were refunded " + (body.refund_credit_sats || 0) + " sats as credit.");
          show(panel.querySelector("[data-batch-refund]"));
          return;
        }

        if (state === "failed") {
          done = true;
          terminal(panel);
          setCardTitle(panel, "Batch mint failed");
          var em = panel.querySelector("[data-batch-error-message]");
          if (em) em.textContent = message || "Batch mint failed.";
          show(panel.querySelector("[data-batch-error]"));
          return;
        }

        if (state === "minting") {
          hide(panel.querySelector("[data-batch-invoice]"));
          setCardTitle(panel, "Minting your batch — this can take a few minutes");
          showStatus(panel, "Anchoring your series on Bitcoin…",
            "Your BDOs are being minted and anchored in one transaction. You can leave this page open — it updates on its own, and confirms once Bitcoin includes it in a block (typically a few minutes).");
        }
        // awaiting_payment: leave the invoice visible.
      } catch (e) {
        // transient read error: keep polling
      }
      schedule();
    }

    tick();
  }

  // Live cost estimate: onchain (one-off) + margin × quantity, re-computed as the
  // merchant edits Quantity. Fills the estimate card AND the pay button/breakdown —
  // with credits-first the charge fires on click, so the price must be on the
  // button. Lives here (not inline in the view) because BTCPay's CSP blocks
  // inline scripts on backoffice pages.
  function initEstimate() {
    var box = document.querySelector("[data-batch-estimate]");
    var qty = document.getElementById("UnitCount");
    if (!box || !qty) return;
    var onchain = Number(box.getAttribute("data-batch-onchain")) || 0;
    var margin = Number(box.getAttribute("data-batch-margin")) || 0;
    var total = box.querySelector("[data-batch-estimate-total]");
    var payTotal = document.querySelector("[data-batch-pay-total]");
    var breakdown = document.querySelector("[data-batch-pay-breakdown]");
    function update() {
      var n = Math.max(1, parseInt(qty.value, 10) || 1);
      var sum = (onchain + margin * n).toLocaleString();
      if (total) total.textContent = sum;
      if (payTotal) payTotal.textContent = sum;
      if (breakdown) breakdown.textContent =
        "~" + sum + " credits = " + onchain.toLocaleString() +
        " on-chain (one-off, paid to Bitcoin miners) + " +
        margin.toLocaleString() + " platform × " + n + " BDOs.";
    }
    qty.addEventListener("input", update);
    update();
  }

  function init() {
    initEstimate();

    document.querySelectorAll("[data-copy-invoice]").forEach(function (button) {
      button.addEventListener("click", async function () {
        var text = button.getAttribute("data-copy-invoice");
        if (!text) return;
        var original = button.textContent;
        var ok = await copyText(text);
        button.textContent = ok ? "Copied" : "Copy failed";
        setTimeout(function () { button.textContent = original; }, 1200);
      });
    });

    var panel = document.querySelector("[data-smv-batch-panel]");
    if (panel) {
      var ref = panel.getAttribute("data-batch-ref");
      if (ref) pollBatchStatus(ref, panel);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }

    // Create form: upload an image from the device instead of pasting a URL. The file
    // goes to the plugin's upload proxy (which forwards to the platform with the
    // Store's token) and the returned SMV-hosted https URL fills #ImageUrl. CSP-safe.
    [HttpGet("upload-image.js")]
    public IActionResult UploadImageJs()
    {
        const string js = """
(function () {
  "use strict";

  var MAX_BYTES = 10 * 1024 * 1024;
  var TYPES = { "image/png": 1, "image/jpeg": 1, "image/webp": 1 };

  function init() {
    var file = document.getElementById("ImageUploadFile");
    var url = document.getElementById("ImageUrl");
    var status = document.getElementById("ImageUploadStatus");
    if (!file || !url) return;
    var endpoint = file.getAttribute("data-upload-endpoint");
    if (!endpoint) return;

    function note(text, cls) {
      if (!status) return;
      status.textContent = text;
      status.className = "form-text " + (cls || "text-muted");
    }

    file.addEventListener("change", async function () {
      var f = file.files && file.files[0];
      if (!f) return;
      if (!TYPES[f.type]) { note("Use a PNG, JPEG or WebP image.", "text-danger"); file.value = ""; return; }
      if (f.size > MAX_BYTES) { note("The image exceeds 10 MB.", "text-danger"); file.value = ""; return; }

      note("Uploading …");
      file.disabled = true;
      try {
        var fd = new FormData();
        fd.append("image", f, f.name);
        var headers = {};
        var t = document.querySelector("input[name='__RequestVerificationToken']");
        if (t) headers["RequestVerificationToken"] = t.value;
        var r = await fetch(endpoint, { method: "POST", headers: headers, body: fd });
        var data = await r.json();
        if (!r.ok || !data || !data.ok || !data.url) {
          note((data && data.message) || "Upload failed. Try again.", "text-danger");
          return;
        }
        url.value = data.url;
        url.dispatchEvent(new Event("input", { bubbles: true }));
        note("Uploaded ✓ — hosted for you at a permanent URL.", "text-success");
      } catch (e) {
        note("Upload failed. Check your connection and try again.", "text-danger");
      } finally {
        file.disabled = false;
      }
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
""";

        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Content(js, "application/javascript");
    }
}
