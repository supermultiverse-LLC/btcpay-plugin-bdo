/* SMV BTCPay Plugin — A5.0 Proof Inspector
 * Vanilla JS, no jQuery dependency. Calls the plugin's own controller
 * (`/plugins/stas/assets/{id}/inspect-proof`) which proxies the decode.
 */
(function () {
  "use strict";

  const FIELDS = [
    { key: "asset_name",      label: "Asset Name" },
    { key: "asset_id",        label: "Asset ID",        hex: true },
    { key: "asset_type",      label: "Asset Type" },
    { key: "amount",          label: "Amount" },
    { key: "genesis_point",   label: "Genesis Point",   hex: true },
    { key: "anchor_outpoint", label: "Anchor Outpoint", hex: true },
    { key: "block_height",    label: "Block Height",    number: true },
    { key: "meta_hash",       label: "Meta Hash",       hex: true },
  ];

  function truncHex(s) {
    if (typeof s !== "string" || s.length <= 20) return s;
    return s.slice(0, 8) + "…" + s.slice(-8);
  }

  function fmtNumber(n) {
    if (n === null || n === undefined) return null;
    try { return Number(n).toLocaleString(); } catch { return String(n); }
  }

  function copyButton(value) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "btn btn-link btn-sm p-0 ms-1";
    btn.title = "Copy";
    btn.textContent = "copy";
    btn.addEventListener("click", function () {
      navigator.clipboard.writeText(value).catch(function () {});
    });
    return btn;
  }

  function renderFields(panel, decoded) {
    const dl = panel.querySelector("[data-smv-fields]");
    dl.innerHTML = "";

    FIELDS.forEach(function (f) {
      let v = decoded[f.key];
      if (v === null || v === undefined || v === "") return;

      const dt = document.createElement("dt");
      dt.className = "col-sm-4 text-muted";
      dt.textContent = f.label;

      const dd = document.createElement("dd");
      dd.className = "col-sm-8 font-monospace text-break";

      if (f.number) {
        dd.textContent = fmtNumber(v);
      } else if (f.hex) {
        const span = document.createElement("span");
        span.textContent = truncHex(String(v));
        span.title = String(v);
        dd.appendChild(span);
        dd.appendChild(copyButton(String(v)));
      } else {
        dd.textContent = String(v);
      }

      dl.appendChild(dt);
      dl.appendChild(dd);
    });

    // Proof depth (X of Y)
    if (decoded.proof_at_depth !== null && decoded.proof_at_depth !== undefined) {
      const dt = document.createElement("dt");
      dt.className = "col-sm-4 text-muted";
      dt.textContent = "Proof Depth";
      const dd = document.createElement("dd");
      dd.className = "col-sm-8";
      const total = decoded.number_of_proofs;
      dd.textContent = total ? (decoded.proof_at_depth + " of " + total) : String(decoded.proof_at_depth);
      dl.appendChild(dt);
      dl.appendChild(dd);
    }

    dl.classList.remove("d-none");
  }

  function renderValidity(panel, raw) {
    if (!raw) return;
    const valid = (raw.valid !== undefined) ? raw.valid
                : (raw.verify_result && raw.verify_result.valid !== undefined) ? raw.verify_result.valid
                : null;
    if (valid === null) return;
    const dl = panel.querySelector("[data-smv-fields]");
    const dt = document.createElement("dt");
    dt.className = "col-sm-4 text-muted";
    dt.textContent = "Valid";
    const dd = document.createElement("dd");
    dd.className = "col-sm-8";
    dd.textContent = valid ? "✓ true" : "✗ false";
    dd.classList.add(valid ? "text-success" : "text-danger");
    dl.appendChild(dt);
    dl.appendChild(dd);
  }

  function setStatus(panel, label, cls) {
    const badge = panel.querySelector("[data-smv-status]");
    badge.textContent = label;
    badge.className = "badge " + (cls || "bg-secondary");
  }

  function reset(panel) {
    panel.querySelector(".smv-inspector-loading").classList.add("d-none");
    panel.querySelector(".smv-inspector-error").classList.add("d-none");
    panel.querySelector(".smv-inspector-not-configured").classList.add("d-none");
    panel.querySelector("[data-smv-fields]").classList.add("d-none");
    panel.querySelector(".smv-inspector-raw").classList.add("d-none");
  }

  async function decode(button, panel) {
    const endpoint = button.getAttribute("data-endpoint");
    if (!endpoint) return;

    reset(panel);
    panel.classList.remove("d-none");
    panel.querySelector(".smv-inspector-loading").classList.remove("d-none");
    setStatus(panel, "Decoding…", "bg-info");

    const labelEl = button.querySelector(".smv-inspect-label");
    const spinner = button.querySelector(".smv-inspect-spinner");
    button.disabled = true;
    spinner.classList.remove("d-none");

    let resp, body;
    try {
      resp = await fetch(endpoint, {
        method: "POST",
        headers: { "Accept": "application/json" },
      });
      body = await resp.json();
    } catch (e) {
      reset(panel);
      const err = panel.querySelector(".smv-inspector-error");
      panel.querySelector("[data-smv-error-message]").textContent =
        "Network error: " + (e && e.message ? e.message : "unknown");
      err.classList.remove("d-none");
      setStatus(panel, "Failed", "bg-danger");
      return;
    } finally {
      button.disabled = false;
      spinner.classList.add("d-none");
    }

    if (resp.status === 503 && body && body.error_kind === "NotConfigured") {
      reset(panel);
      panel.querySelector(".smv-inspector-not-configured").classList.remove("d-none");
      setStatus(panel, "Not configured", "bg-warning text-dark");
      return;
    }

    if (!body || !body.ok) {
      reset(panel);
      const err = panel.querySelector(".smv-inspector-error");
      const msg = (body && (body.error || body.detail)) || ("HTTP " + resp.status);
      const upstream = body && body.upstream_status ? " (relay " + body.upstream_status + ")" : "";
      panel.querySelector("[data-smv-error-message]").textContent = msg + upstream;
      err.classList.remove("d-none");
      setStatus(panel, "Failed", "bg-danger");
      return;
    }

    reset(panel);
    renderFields(panel, body.decoded || {});
    renderValidity(panel, body.raw || null);
    if (body.raw) {
      const rawPre = panel.querySelector("[data-smv-raw]");
      rawPre.textContent = JSON.stringify(body.raw, null, 2);
      panel.querySelector(".smv-inspector-raw").classList.remove("d-none");
    }
    setStatus(panel, "Decoded ✓", "bg-success");
    panel.querySelector("[data-smv-redecode]").disabled = false;
  }

  function init() {
    document.querySelectorAll("[data-smv-inspect-proof]").forEach(function (btn) {
      const assetId = btn.getAttribute("data-asset-id");
      const panel = document.querySelector(
        '[data-smv-inspector-panel][data-asset-id="' + assetId + '"]'
      );
      if (!panel) return;

      btn.addEventListener("click", function () { decode(btn, panel); });
      panel.querySelector("[data-smv-retry]").addEventListener("click", function () { decode(btn, panel); });
      panel.querySelector("[data-smv-redecode]").addEventListener("click", function () { decode(btn, panel); });
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
