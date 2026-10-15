# Testing

## What was measured in this environment (Linux build sandbox, no Windows desktop)

| Check | Result |
|---|---|
| `dotnet build BiliIndexTranslate.sln -c Release` | 0 errors, 0 warnings |
| `dotnet test tests/BiliIndexTranslate.Core.Tests -c Release` | **96 passed, 0 failed** (session state machine, placement/anchoring, endpoint resolution, response parsing, cache, coordinator races, settings load/migrate) |
| `dotnet publish … -r win-x64 --self-contained false` | `publish/BiliIndexTranslate.exe` — PE32+ GUI, icon, PerMonitorV2 manifest, version 2.0.0 embedded (656 KB folder) |
| `dotnet publish … --self-contained true -p:PublishSingleFile=true` | single 145 MB exe produced |
| Icon | `tools/make-icon.py` output inspected at 256 px |

Everything below this line is **not measured**: the app was not executed. Timing targets
(200–350 ms settle, prompt loading, cache hits without network) are design parameters, not results.
The Diagnostics window reports p50/p95 for `detect`, `render`, `cache`, `network` and `inference`
once the app runs; fill in the table from there.

## Scenarios

Status key: ✅ verified on Windows · ◐ partial · ❌ failed · ⬜ not tested in this sandbox.
Core-only logic that is covered by unit tests is marked (unit).

| # | Scenario | Expected | Status |
|---|---|---|---|
| 1 | Select a sentence in a browser | Card appears above the selection within settle delay + request time; no focus change in the browser | ⬜ (placement math: unit) |
| 2 | Click to clear the selection | Card hides immediately on mouse-down; no reopen on mouse-up (probe → no selection) | ⬜ (session: unit) |
| 3 | Select A, then B while A is loading | A's request cancelled, card shows loading for B, A's late result never rendered | ⬜ (session + coordinator: unit) |
| 4 | Dismiss while a request is pending | Card stays hidden when the response arrives | ⬜ (unit) |
| 5 | Re-select the same text after dismissing | Stays quiet until a different selection or a clear | ⬜ (unit) |
| 6 | Move the pointer into the card | Card remains; source keeps focus and selection | ⬜ |
| 7 | Copy from the card | Clipboard updated; source window not activated | ⬜ |
| 8 | Long translation | Card capped at 45 % of work area height, wheel scrolls the card (hook forwards wheel), source does not scroll | ⬜ |
| 9 | Selection at screen edges | Flips below when no room above; clamped to the work area with 8 px margin | ⬜ (unit) |
| 10 | Second monitor with different DPI | Card appears on the selection's monitor, sized with that monitor's scale; negative coordinates handled | ⬜ (unit for math) |
| 11 | Move / minimize the source window | Card follows on move (WinEvent → refresh); minimize hides it | ⬜ |
| 12 | Switch to another app (Alt+Tab or click) | Card hides; returning does not reopen it | ⬜ |
| 13 | Unsupported control (no TextPattern) | Nothing appears; Diagnostics → Applications shows "unsupported", log line without text | ⬜ |
| 14 | Stalled UIA provider | Probe watchdog (2.5 s) reports failure, observer thread recovers on next request, no replacement threads | ⬜ |
| 15 | HTTP 412 / timeout / invalid JSON / empty output | Error card with concise message + route (direct/relay); no HTML rendered; no retry loop | ⬜ (parser + messages: unit) |
| 16 | Start a second instance | Second process exits; running instance opens Settings | ⬜ |
| 17 | Exit from tray | Hooks, WinEvent hooks, UIA handlers, HttpClient, windows and tray icon released; process ends | ⬜ |
| 18 | 200 repeated selections | Stable handle count and memory (single reused popup window, bounded caches/logs) | ⬜ |

## Applications

| App | Mechanism expected | Status |
|---|---|---|
| Chrome / Edge (web page) | UIA TextPattern on the document (Chromium exposes it when a screen reader / UIA client is active; first probe may be slow) | ⬜ not tested |
| Firefox | UIA TextPattern (Firefox's UIA support improved in 2024–2025 builds; older builds may be unsupported) | ⬜ not tested |
| Notepad (Windows 11) | TextPattern on the editor control | ⬜ not tested |
| Word | TextPattern on the document body | ⬜ not tested |
| VS Code / Electron editors | Monaco exposes a limited UIA text surface; may read as unsupported → use Alt+Q fallback | ⬜ not tested |
| Adobe Acrobat / PDF viewers | Highly variable; Edge PDF viewer behaves like a web page | ⬜ not tested |
| Single-line edit fields | Skipped by default (policy), unless enabled in Settings | ⬜ (policy: unit) |
| Password fields | Always skipped | ⬜ (policy: unit) |
| Console windows, games, custom-drawn controls | Unsupported; quiet failure | ⬜ |

## How to run the checks on Windows

1. `tools\run.cmd` — complete the welcome window (it has a demo paragraph for the first selection).
2. Open **Diagnostics…** from the tray and keep it on a second monitor if possible; it updates live.
3. Walk through the scenarios above; note p50/p95 from the Timings section after ~20 selections.
4. For scenario 14 use a UIA-heavy app under load, or temporarily set `ConnectionTimeout` lower in
   `UiaObserver` and probe a frozen window (`Suspend` it with Process Explorer).
5. For scenario 18 watch *Handles*, *USER Objects*, *GDI Objects* and *Private Bytes* in Task Manager
   / Process Explorer.
