// background.js
"use strict";

const DEFAULTS = {
  base: "https://index-translate.bilibili.com/v1",
  proxy: "",
  model: "Index-Translate-9B",
  key: "bili",
  target: "中文"
};

// Prepended to the base URL when the direct call answers HTTP 412 and no relay prefix is
// configured. With the default base this is exactly
// https://seep.eu.org/https://index-translate.bilibili.com/v1
const DEFAULT_RELAY_HOST = "https://seep.eu.org/";

const MENU_ID = "bili-translate-selection";
const MAX_LEN = 3000;
const TIMEOUT_MS = 30000;
const MAX_ERROR_BODY = 4000;

function stripSlashes(url) {
  return String(url || "").trim().replace(/\/+$/, "");
}

function getSettings() {
  return chrome.storage.sync.get(DEFAULTS);
}

function systemPrompt(target) {
  return (
    "You are a translation engine. Translate the user text into " + target + ". " +
    "Output ONLY the translation \u2014 no explanation, no quotes, no original text."
  );
}

// ---- API ---------------------------------------------------------------------------------

// All HTTP lives here on purpose: a service worker fetch is governed by host_permissions, not by
// the web page's CSP or origin, so bilibili / the relay never see a page-origin CORS preflight.
async function postChat(prefix, key, body) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(stripSlashes(prefix) + "/chat/completions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": "Bearer " + key
      },
      body: JSON.stringify(body),
      signal: controller.signal
    });
    const raw = await res.text();
    return { ok: res.ok, status: res.status, raw: raw };
  } catch (err) {
    if (err && err.name === "AbortError") {
      throw new Error("Request timed out after " + TIMEOUT_MS / 1000 + " s");
    }
    throw err;
  } finally {
    clearTimeout(timer);
  }
}

async function translate(text) {
  text = String(text || "").trim();
  if (!text) {
    return { ok: false, status: 0, body: "Nothing to translate." };
  }
  if (text.length > MAX_LEN) {
    return { ok: false, status: 0, body: "Selection is longer than " + MAX_LEN + " characters." };
  }

  const s = await getSettings();
  const body = {
    model: s.model,
    stream: false,
    temperature: 0.2,
    messages: [
      { role: "system", content: systemPrompt(s.target) },
      { role: "user", content: text }
    ]
  };

  const base = stripSlashes(s.base);
  const relay = stripSlashes(s.proxy);
  let via;
  let r;

  if (relay) {
    // A configured prefix routes every request through that relay.
    via = "relay";
    r = await postChat(relay, s.key, body);
  } else {
    via = "direct";
    r = await postChat(base, s.key, body);
    if (r.status === 412) {
      // bilibili's gateway answers 412 to requests it does not like (extension origin, missing
      // browser headers). The relay re-issues the call from a normal server, so retry once.
      via = "relay";
      r = await postChat(DEFAULT_RELAY_HOST + base, s.key, body);
    }
  }

  if (!r.ok) {
    return { ok: false, status: r.status, body: r.raw.slice(0, MAX_ERROR_BODY), via: via };
  }

  let data;
  try {
    data = JSON.parse(r.raw);
  } catch (e) {
    return {
      ok: false,
      status: r.status,
      body: "Response is not JSON: " + r.raw.slice(0, MAX_ERROR_BODY),
      via: via
    };
  }

  const choice = data && Array.isArray(data.choices) ? data.choices[0] : null;
  const content = choice && choice.message ? choice.message.content : null;
  if (typeof content !== "string") {
    return {
      ok: false,
      status: r.status,
      body: "No choices[0].message.content in: " + r.raw.slice(0, MAX_ERROR_BODY),
      via: via
    };
  }

  return { ok: true, text: content.trim(), model: s.model, via: via };
}

// ---- Messaging ---------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || msg.type !== "BILI_TRANSLATE") {
    return false;
  }

  translate(msg.text)
    .then(sendResponse)
    .catch((err) => {
      sendResponse({ ok: false, status: 0, body: String((err && err.message) || err) });
    });

  // MV3 requirement: return true synchronously so Chrome keeps the message channel open until
  // sendResponse() is called from the async work above. Without it the port closes as soon as
  // this listener returns and the caller receives undefined.
  return true;
});

// ---- Context menu (second entry path) ----------------------------------------------------

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create(
      {
        id: MENU_ID,
        title: "Translate selection with Bili",
        contexts: ["selection"]
      },
      () => void chrome.runtime.lastError
    );
  });
  // Seed storage with the defaults so the options page shows real values on first open.
  getSettings().then((s) => chrome.storage.sync.set(s));
});

async function sendToFrame(tabId, frameId, msg) {
  const target = { tabId: tabId, frameIds: [frameId] };
  try {
    await chrome.tabs.sendMessage(tabId, msg, { frameId: frameId });
  } catch (e) {
    // No listener in that frame: the page was open before the extension was (re)loaded.
    // Inject on demand (content.js is guarded against running twice) and retry once.
    await chrome.scripting.insertCSS({ target: target, files: ["content.css"] });
    await chrome.scripting.executeScript({ target: target, files: ["content.js"] });
    await chrome.tabs.sendMessage(tabId, msg, { frameId: frameId });
  }
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID || !tab || typeof tab.id !== "number" || tab.id < 0) {
    return;
  }
  // Chrome collects info.selectionText itself, so this path works even where the content
  // script's window.getSelection() comes back empty (shadow DOM of a video player, etc.).
  const msg = { type: "BILI_CONTEXT_TRANSLATE", text: info.selectionText || "" };
  sendToFrame(tab.id, info.frameId || 0, msg).catch((e) => {
    console.warn("[Bili Index Translate] context menu: cannot reach page:", e && e.message);
  });
});

chrome.action.onClicked.addListener(() => {
  chrome.runtime.openOptionsPage();
});

console.log("[Bili Index Translate] service worker started");
