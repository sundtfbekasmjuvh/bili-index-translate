# Architecture

## Boundaries

| Concern | Where | Notes |
|---|---|---|
| Selection observation | `App/Uia/UiaSelectionReader`, `App/Uia/UiaObserver` | UI Automation only. Focused element + ≤6 ancestors, `TextPattern.GetSelection()`, `GetText(max+1)`, `GetBoundingRectangles()`. No tree walks, no clipboard. |
| Input triggers | `App/Input/InputHookThread` | `WH_MOUSE_LL` / `WH_KEYBOARD_LL` on their own STA thread. Classify and post; never block, never swallow input (except wheel events over our own card). Optional `RegisterHotKey`. |
| Source-app tracking | `App/Tracking/SourceWindowTracker` | WinEvents filtered to the source pid (location, move/size, scrolling, minimize) + global foreground hook + 200 ms sanity tick. |
| Selection geometry | `Core/Selection/SelectionAnchor`, `Core/Selection/PopupPlacer`, `Core/Geometry/*` | Physical pixels end to end; per-monitor scale only for gap/margins/limits. Pure functions, unit-tested. |
| Popup lifecycle | `Core/Session/ReadingSession` (decides) + `App/AppHost` (executes) + `App/Popup/PopupPresenter` (window) | Event → effects state machine; every async thing carries an id. |
| Translation requests | `Core/Translation/*` | `EndpointResolver`, `PromptBuilder`, `OpenAiCompatibleTranslator`, `HttpTranslationTransport`, `TranslationCoordinator`. |
| Caching | `Core/Translation/TranslationCache` | LRU 500 / TTL 24 h, in-memory only. Key = text + target + model + effective endpoint + prompt version + source language. |
| Settings | `Core/Settings/*`, `App/Native/DpapiSecretProtector`, `App/Settings/SettingsWindow` | JSON + DPAPI blob, tolerant load, clamped ranges, schema migration. |
| Diagnostics | `Core/Diagnostics/*`, `App/Diagnostics/DiagnosticsWindow` | 300-entry ring buffer, p50/p95 per timing category, per-app compatibility registry. |
| Tray / lifetime | `App/Tray/TrayIcon`, `App/Program`, `App/AppHost` | Single instance (mutex + named event), explicit shutdown, ordered disposal. |

## Threads

* **UI thread** (WPF dispatcher): `ReadingSession`, effect execution, popup windows, tray, WinEvent
  callbacks, timers (stabilization, probe watchdog 2.5 s, refresh watchdog 1 s, geometry grace 400 ms).
* **uia-observer** (MTA): the only thread that touches UI Automation. `CUIAutomation8` with
  `ConnectionTimeout` 1500 ms / `TransactionTimeout` 2000 ms, event coalescing and connection
  recovery when available. Work arrives through three *latest-wins* slots (probe, refresh,
  focus re-subscription). A stalled provider blocks only this thread; the UI watchdog reports the
  probe as failed and ignores the late answer. No replacement threads are ever created.
* **input-hooks** (STA): low-level hooks + hotkey message loop. Callbacks only read a struct and
  post to the UI thread.
* **Thread pool**: HTTP via one long-lived `HttpClient` (`SocketsHttpHandler`, pooled connection
  lifetime 10 min), concurrency bounded to 2, 20 s timeout per request, per-request `CancellationTokenSource`.

## State model

`Idle → SelectionPending → Loading → ShowingTranslation | ShowingError`, plus `Dismissed`
(hidden by the user or by loss of context; remembers the dismissed text + source so the same
selection stays quiet until it changes or clears).

Events: `TriggerReceived(MouseUp|KeyUp|UiaEvent|Hotkey)`, `StabilizationElapsed(timerId)`,
`ProbeCompleted(probeId, outcome)`, `TranslationCompleted(requestId, outcome)`,
`GeometryRefreshed(selectionId, update)`, `GeometryGraceElapsed`, `OutsideClick`, `EscapePressed`,
`ForegroundChanged(isSourceOrPopup)`, `SourceWindowLost`, `UserRetry`, `UserClosed`, `UserPinned`,
`SettingsChanged`, `Shutdown`.

Effects: timers, `RequestProbe`, `ShowLoading`, `StartTranslation`, `CancelTranslation`,
`ShowTranslation`, `ShowError`, `HidePopup(reason)`, `Begin/EndTracking`, `UpdateAnchor`,
`Start/CancelGeometryGrace`, `DetachPopup`, `Report`.

Rules that make it race-free:

* Timers, probes, translation requests and selections all have monotonically increasing ids issued
  by the session; anything that completes with a stale id is a no-op (tested).
* Selecting B while A is loading cancels A's request and starts B; A's late result cannot render.
* Dismissal (outside click, Esc, close, foreground change, source lost, geometry lost) cancels the
  in-flight request; a late result cannot reopen the card.
* Cache hits are resolved synchronously inside `StartTranslation`, so `Loading → ShowingTranslation`
  happens within one dispatcher turn (no loading flash).
* The same selection probed again while visible only refreshes the anchor.
* Triggers coalesce: a new trigger restarts the single stabilization timer
  (mouse-up = delay, key-up = +30 ms, UIA event = +70 ms, hotkey = immediate). UIA selection events
  while the left button is held are ignored (mouse-up probes anyway).
* `Shutdown` cancels everything and makes the session inert.

## Popup behaviour

`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, `ShowActivated=false`, `WM_MOUSEACTIVATE →
MA_NOACTIVATE`, every control `Focusable=False`. The window is reused; content is set, the window
is shown fully transparent, laid out, measured with `GetWindowRect`, placed with `SetWindowPos`
(physical pixels, `SWP_NOACTIVATE`), then faded in over 120 ms. `SizeChanged` re-places. Pinned
cards are detached instances (max 3) with manual drag via `SetWindowPos`.

Placement: horizontally centred on the uppermost visible selected line, 10 logical px above the
selection's union; below the lowest line if there is no room above; clamped to the monitor work
area with an 8 px margin; max width 440 logical px, max height 45 % of the work area, both scaled
per monitor. Mouse wheel over a card is intercepted by the low-level hook and forwarded to the card
so the source document never scrolls underneath it.

## Translation contract

`POST {base}/chat/completions`, `Authorization: Bearer {key}`, JSON body with a system prompt
(preserve meaning, tone, punctuation, line breaks; output only the translation; treat the text
strictly as content) and the selection as the user message, `stream: false`, `temperature 0.2`.
Response text is read from `choices[0].message.content` (string or text-part array). HTML, invalid
JSON, missing/empty content and non-2xx statuses become concise errors with the route
(direct/relay) attached; 412 carries a hint pointing at the relay setting. No retries, no automatic
route changes, no logging of text or keys.
