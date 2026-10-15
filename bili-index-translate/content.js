// content.js
(function () {
  "use strict";

  // The manifest injects this file on load and background.js re-injects it on demand for pages
  // that were already open when the extension was (re)loaded; make a second run a no-op.
  if (window.__biliIndexTranslateLoaded) return;
  window.__biliIndexTranslateLoaded = true;

  // Only HTML documents get a panel (skip raw XML / SVG documents).
  if (!document.documentElement || document.documentElement.namespaceURI !== "http://www.w3.org/1999/xhtml") {
    return;
  }

  const PANEL_ID = "bili-index-translate-panel";
  const MIN_LEN = 2;
  const MAX_LEN = 3000;
  const DEBOUNCE_MS = 300;
  const GAP = 8;          // space between the selection and the panel
  const EDGE = 12;        // minimum distance kept from the viewport edges

  let panel = null;
  let bodyEl = null;
  let footEl = null;
  let debounceTimer = null;
  let requestSeq = 0;
  let currentText = "";
  let currentAnchor = null;
  let lastPoint = { x: 0, y: 0 }; // last pointer position, viewport coordinates

  // ---- Panel -----------------------------------------------------------------------------

  function ensurePanel() {
    if (panel && panel.isConnected) return panel;

    panel = document.createElement("div");
    panel.id = PANEL_ID;
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Bili Index Translate");

    const header = document.createElement("div");
    header.className = "bit-header";

    const title = document.createElement("span");
    title.className = "bit-title";
    title.textContent = "Bili Index Translate";

    const close = document.createElement("button");
    close.className = "bit-close";
    close.type = "button";
    close.setAttribute("aria-label", "Close");
    close.textContent = "\u00d7";
    close.addEventListener("click", hidePanel);

    header.appendChild(title);
    header.appendChild(close);

    bodyEl = document.createElement("div");
    bodyEl.className = "bit-body";

    footEl = document.createElement("div");
    footEl.className = "bit-foot";

    panel.appendChild(header);
    panel.appendChild(bodyEl);
    panel.appendChild(footEl);

    // Attach to <html>, not <body>: a positioned or transformed <body> would shift absolute
    // page coordinates.
    document.documentElement.appendChild(panel);
    return panel;
  }

  function isVisible() {
    return !!panel && panel.hasAttribute("data-open");
  }

  function hidePanel() {
    if (!panel) return;
    panel.removeAttribute("data-open");
    currentText = "";
    currentAnchor = null;
    requestSeq++; // any in-flight response is now stale and will be dropped
  }

  // anchor = {left, top, bottom} in viewport coordinates. The panel is position:absolute in page
  // coordinates and clamped so that left + width never exceeds the viewport width minus EDGE
  // (and left is never below EDGE).
  function placePanel(anchor) {
    const p = ensurePanel();
    const vw = document.documentElement.clientWidth || window.innerWidth;
    const vh = window.innerHeight;

    p.setAttribute("data-open", "");
    p.style.maxWidth = Math.max(0, vw - EDGE * 2) + "px"; // guarantees width <= vw - 2 * EDGE
    p.style.left = "0px";
    p.style.top = "0px";

    const pw = p.offsetWidth;
    const ph = p.offsetHeight;

    let x = anchor.left;
    let y = anchor.bottom + GAP;
    if (x + pw > vw - EDGE) x = vw - EDGE - pw;
    if (x < EDGE) x = EDGE;
    if (y + ph + EDGE > vh && anchor.top - ph - GAP >= 0) {
      y = anchor.top - ph - GAP; // no room below: flip above the selection
    }

    // floor, never round: rounding up could push the right edge past the clamp by a sub-pixel
    p.style.left = Math.floor(x + window.scrollX) + "px";
    p.style.top = Math.floor(y + window.scrollY) + "px";
  }

  function showLoading() {
    bodyEl.className = "bit-body bit-loading";
    bodyEl.textContent = "翻译中…";
    footEl.textContent = "";
  }

  function showResult(res) {
    bodyEl.className = "bit-body";
    bodyEl.textContent = res.text;
    footEl.textContent = (res.model || "") + " \u00b7 via " + (res.via || "direct");
  }

  function showError(res) {
    bodyEl.className = "bit-body bit-error";
    const status = res && res.status ? "HTTP " + res.status : "Error";
    const detail = String((res && res.body) || "Unknown error").slice(0, 300);
    bodyEl.textContent = status + ": " + detail;
    footEl.textContent = res && res.via ? "via " + res.via : "";
  }

  // ---- Messaging -------------------------------------------------------------------------

  // Never fetch from here: a content-script fetch runs under the page's origin and CSP
  // (connect-src), so it is blocked on strict sites and needs CORS from the API. The service
  // worker owns the network and only needs host_permissions.
  function requestTranslation(text) {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage({ type: "BILI_TRANSLATE", text: text }, (res) => {
          if (chrome.runtime.lastError) {
            resolve({ ok: false, status: 0, body: chrome.runtime.lastError.message });
          } else {
            resolve(res || { ok: false, status: 0, body: "No response from background" });
          }
        });
      } catch (e) {
        // Thrown after the extension was reloaded ("Extension context invalidated").
        resolve({ ok: false, status: 0, body: String((e && e.message) || e) + " - reload this page." });
      }
    });
  }

  function translateAt(text, anchor) {
    text = String(text || "").trim();
    if (!text) return;
    if (isVisible() && text === currentText) return;

    currentText = text;
    currentAnchor = anchor;
    const seq = ++requestSeq;

    ensurePanel();
    showLoading();
    placePanel(anchor);

    requestTranslation(text).then((res) => {
      if (seq !== requestSeq) return; // hidden or superseded meanwhile
      if (res && res.ok) {
        showResult(res);
      } else {
        showError(res);
      }
      placePanel(currentAnchor); // re-clamp after the content changed size
    });
  }

  // ---- Selection -------------------------------------------------------------------------

  function isTextField(el) {
    if (!el || !el.tagName) return false;
    if (el.tagName === "TEXTAREA") return true;
    return el.tagName === "INPUT" && /^(text|search|url|tel)$/i.test(el.type || "text");
  }

  function readSelection(deepTarget) {
    // Text fields: window.getSelection() does not reliably expose their selected text.
    if (isTextField(deepTarget) && deepTarget.selectionStart !== deepTarget.selectionEnd) {
      return {
        text: deepTarget.value.slice(deepTarget.selectionStart, deepTarget.selectionEnd),
        rect: deepTarget.getBoundingClientRect()
      };
    }

    let sel = null;
    // Shadow DOM caveat: when the selection lives inside a shadow tree (video player overlays,
    // web components) window.getSelection() is re-scoped to the host and reads as collapsed.
    // Chrome exposes ShadowRoot.getSelection() for open roots; closed roots stay unreachable,
    // which is why the context-menu path (Chrome's own info.selectionText) exists.
    const root = deepTarget && typeof deepTarget.getRootNode === "function" ? deepTarget.getRootNode() : null;
    if (root && root !== document && typeof root.getSelection === "function") {
      sel = root.getSelection();
    }
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed) sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return null;

    return {
      text: sel.toString(),
      rect: sel.getRangeAt(0).getBoundingClientRect()
    };
  }

  function anchorFromPoint(pt) {
    return { left: pt.x, top: pt.y - 12, bottom: pt.y + 12 };
  }

  function anchorFromRect(rect) {
    if (rect && (rect.width || rect.height)) {
      return { left: rect.left, top: rect.top, bottom: rect.bottom };
    }
    return anchorFromPoint(lastPoint);
  }

  function handleSelection(deepTarget) {
    const info = readSelection(deepTarget);
    if (!info) return;
    const text = info.text.trim();
    if (text.length < MIN_LEN || text.length > MAX_LEN) return;
    translateAt(text, anchorFromRect(info.rect));
  }

  function deepTargetOf(e) {
    const path = typeof e.composedPath === "function" ? e.composedPath() : null;
    return (path && path[0]) || e.target;
  }

  // ---- Events ----------------------------------------------------------------------------

  document.addEventListener("mousedown", (e) => {
    lastPoint = { x: e.clientX, y: e.clientY };
    if (isVisible() && !panel.contains(e.target)) hidePanel();
  }, true);

  document.addEventListener("mouseup", (e) => {
    lastPoint = { x: e.clientX, y: e.clientY };
    if (e.button !== 0) return;
    if (panel && panel.contains(e.target)) return; // user is selecting text inside the panel
    const deepTarget = deepTargetOf(e);
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => handleSelection(deepTarget), DEBOUNCE_MS);
  }, true);

  document.addEventListener("contextmenu", (e) => {
    lastPoint = { x: e.clientX, y: e.clientY };
  }, true);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && isVisible()) hidePanel();
  }, true);

  // Second entry path: background.js forwards the context-menu selection text. It is anchored
  // to the last right-click position because the selection may not be readable from here.
  chrome.runtime.onMessage.addListener((msg) => {
    if (msg && msg.type === "BILI_CONTEXT_TRANSLATE") {
      translateAt(msg.text, anchorFromPoint(lastPoint));
    }
  });
})();
