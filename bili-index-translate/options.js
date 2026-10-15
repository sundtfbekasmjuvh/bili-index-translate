// options.js
"use strict";

const DEFAULTS = {
  base: "https://index-translate.bilibili.com/v1",
  proxy: "",
  model: "Index-Translate-9B",
  key: "bili",
  target: "中文"
};

const TEST_TEXT = "The quick brown fox jumps over the lazy dog.";

function $(id) {
  return document.getElementById(id);
}

function setStatus(text, isError) {
  const el = $("status");
  el.textContent = text;
  el.className = isError ? "err" : "ok";
}

function stripSlashes(url) {
  return String(url || "").trim().replace(/\/+$/, "");
}

function isHttpUrl(url) {
  try {
    const u = new URL(url);
    return u.protocol === "http:" || u.protocol === "https:";
  } catch (e) {
    return false;
  }
}

function fillForm(s) {
  $("base").value = s.base;
  $("proxy").value = s.proxy;
  $("key").value = s.key;
  $("target").value = s.target;

  const select = $("model");
  if (!Array.from(select.options).some((o) => o.value === s.model)) {
    select.add(new Option(s.model, s.model)); // keep an unknown stored model visible
  }
  select.value = s.model;
}

function readForm() {
  return {
    base: stripSlashes($("base").value),
    proxy: stripSlashes($("proxy").value),
    model: $("model").value,
    key: $("key").value.trim(),
    target: $("target").value.trim() || DEFAULTS.target
  };
}

// Hosts outside the manifest's host_permissions need a runtime grant; otherwise the service
// worker's fetch is an ordinary cross-origin request that only works if the server sends CORS
// headers for chrome-extension:// origins. Must run inside a user gesture (the button click).
function requestHostAccess(urls) {
  const origins = urls.filter(Boolean).map((u) => new URL(u).origin + "/*");
  return chrome.permissions.request({ origins: origins }).catch(() => false);
}

async function save() {
  const s = readForm();
  if (!isHttpUrl(s.base)) {
    setStatus("Base URL must be an http(s) URL.", true);
    return null;
  }
  if (s.proxy && !isHttpUrl(s.proxy)) {
    setStatus("Relay prefix must be empty or an http(s) URL.", true);
    return null;
  }

  const granted = await requestHostAccess([s.base, s.proxy]);
  await chrome.storage.sync.set(s);

  if (granted) {
    setStatus("Saved.");
  } else {
    setStatus("Saved, but host access was declined - requests to that host may fail (CORS).", true);
  }
  return s;
}

async function test() {
  const s = await save();
  if (!s) return;

  setStatus("Testing " + s.model + " ...");
  let res;
  try {
    res = await chrome.runtime.sendMessage({ type: "BILI_TRANSLATE", text: TEST_TEXT });
  } catch (e) {
    res = { ok: false, status: 0, body: String((e && e.message) || e) };
  }

  if (res && res.ok) {
    setStatus("OK via " + res.via + ":\n" + res.text);
  } else {
    const status = res && res.status ? "HTTP " + res.status + " " : "";
    setStatus("Failed: " + status + String((res && res.body) || "no response").slice(0, 300), true);
  }
}

document.addEventListener("DOMContentLoaded", async () => {
  fillForm(await chrome.storage.sync.get(DEFAULTS));

  $("form").addEventListener("submit", (e) => {
    e.preventDefault();
    save();
  });
  $("test").addEventListener("click", test);
  $("reset").addEventListener("click", async () => {
    fillForm(DEFAULTS);
    await chrome.storage.sync.set(DEFAULTS);
    setStatus("Defaults restored.");
  });
});
