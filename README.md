# Bili Index Translate

Two implementations of the same idea — select text, read the Chinese translation right where you
are — backed by Bilibili's Index-Translate models through an OpenAI-compatible endpoint.

| Folder | What | Status |
|---|---|---|
| [`BiliIndexTranslate.Desktop/`](BiliIndexTranslate.Desktop/) | Windows desktop app (C# / .NET 8, WPF + UI Automation). A non-activating card appears above the selection in any app that exposes UIA text. | Compiles cleanly, 96/96 core unit tests pass, win-x64 build produced. **Not yet run on a Windows desktop** — see `docs/TESTING.md`. |
| [`bili-index-translate/`](bili-index-translate/) | Chrome extension (Manifest V3). Bubble above the selection on web pages. | Complete; load unpacked via `chrome://extensions`. |

Prebuilt desktop binaries are attached to the GitHub Releases page rather than committed here.

## Quick start

* **Desktop:** `BiliIndexTranslate.Desktop\tools\run.cmd` (needs the .NET 8 SDK) or download the
  release zip (needs the .NET 8 Desktop Runtime). See [`BiliIndexTranslate.Desktop/README.md`](BiliIndexTranslate.Desktop/README.md).
* **Extension:** open `chrome://extensions`, enable Developer mode, *Load unpacked*, pick `bili-index-translate/`.

Both default to `https://index-translate.bilibili.com/v1`, key `bili`, model `Index-Translate-9B`,
target Simplified Chinese, and send only the selected text plus request metadata. No telemetry,
no history.
