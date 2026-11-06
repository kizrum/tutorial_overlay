# Server

[← README](../README.md) · [Server](server.md) · [MCP tools](tools.md) · [Tutorials](tutorials.md) · [Development](development.md)

## Running and options

Options: `--port 7788` (1–65535; an invalid value exits with code 1), `--key KEY`, `--no-anim`, `--show-inactive`
(do not hide the marks while the app is not in front), `--verbose`. The key comes from `--key`, from the
`TUTORIAL_OVERLAY_KEY` variable or from `%LOCALAPPDATA%\TutorialOverlay\key.txt` (created on the first run).

The default port is 7788; the `port` field in `config.json` changes it (`--port` wins).

**One server and one agent.** Each user runs exactly one server, with one tray icon. A second launch (on any port,
even at the same moment as the first) does not start: if the exe was started by hand it shows a window
"TutorialOverlay is already running: 127.0.0.1:7789 (PID …)"; if an agent reads its output it prints the event
`{"event":"already_running","port":7789,"pid":…,"url":…,"advice":…}` (after waiting for the first one to write its
address) and exits with code 2. Where the server runs is always in `%LOCALAPPDATA%\TutorialOverlay\server.json`
(`port`, `pid`, `url`). There is no `--instance` option any more.

The server serves **one agent at a time**. The first MCP session (`Mcp-Session-Id`) that calls a tool holds it; any
call from another session is refused with `busy: … held by another agent` and does nothing (only `initialize`,
`tools/list` and `ping` are open; the notification stream belongs to the holder too). A second agent waits for its
turn with `wait_for_turn(timeoutMs)`, which returns `{"granted": true}` as soon as the server is free. The server
becomes free:

- when the holder's client closes. An MCP client keeps the notification stream (`GET /mcp`) open while it runs; on
  exit the connection drops (Claude Code sends no `DELETE` on `/exit`, but the connection always closes). If the
  stream is not reopened within 5 seconds, the server is free (`client disconnected`). While the stream is open the
  server stays with the holder, however long the agent is silent;
- when the holder calls `release` or closes its session (HTTP `DELETE`);
- for a client without a stream, after 3 minutes of silence (30 while its tutorial runs).

The server tells agents apart by `Mcp-Session-Id`, so every request except `initialize` must carry it: without the
header `POST`, `GET` and `DELETE` get `400` with the hint "call initialize first" (such clients used to share one
session and could get in each other's way).

Any session may open a notification stream, but only the holder gets events. Streams opening and closing, `DELETE`
and requests refused for a missing key are logged. Events: `client_connected` / `client_released`.

**Stopping.** `--stop` asks the running server to quit the same way as "Quit" in its menu: the icon leaves the tray
and `server.json` is deleted. On a restart from the settings window the old process's icon is removed before the
new one starts.

**If the process is killed** (`taskkill /F`, Task Manager, stopping a background task), it cannot remove its icon,
and Windows keeps such an icon until the mouse passes over it. So the icon is registered with a fixed GUID (it
depends on the exe's path), and any process of the same exe can remove it:

- the server starts a **guard** (`--tray-guard`, no windows, no icon; started in two hops so that it is outside the
  server's process tree and survives `taskkill /T`). The guard waits for the server to end and removes its icon and
  `server.json` at once;
- the next server start also removes an icon left from before.

If Explorer restarts, the icon comes back.

**The tray icon** shows that the server is running: a small step card. During a tutorial the bar at the top shows
its progress; the dot in the corner is green if the agent made a request in the last 5 minutes, grey if not. The
tooltip shows the port, the agent's last request, the current tutorial and step. A left click opens the settings, a
right click a dark menu: "Tutorials ▸" (saved tutorials, a click runs one), "Settings…", "Copy the key", "Copy the
MCP address", "Copy the command for the agent", "Open the settings folder", "Stop tutorial", "Quit TutorialOverlay".

**The settings window** (tabs "Server", "Look", "Hotkeys") is drawn in the card's dark style: toggles, rounded
fields, colour swatches for the marks, a dark title bar. It writes `config.json` and applies changes at once.
`--settings [server|look|keys]` opens it right at startup.

**Port.** Next to the port field is a live check: "free ✓", "in use: program (PID)" or "this server runs on it".
"Pick a free one" keeps the entered port if it will do, otherwise puts in the first free one in 7788–7899. A port in
use cannot be saved, and a restart does not begin if the new port was taken meanwhile, so a working server is never
closed for nothing. The new port works after a restart; the window offers to restart the server. A client connected
over stdio (`--stdio`) finds the new port by itself; one connected over HTTP has its address changed by hand.

"For the agent → Copy the connect command" (and the same item in the tray menu) puts a ready line on the clipboard:
`claude mcp add --scope user tutorial-overlay -- "<this exe>" --stdio`. It holds neither the port nor the key
(`--stdio` finds both), so it stays right after a port change. Run it in a terminal or give it to the agent in chat.
While a tutorial runs, the language, colour and hotkeys from its spec apply until it ends.

The server listens on `127.0.0.1` only. The key is taken from a header only: `Authorization: Bearer` or
`X-Overlay-Key` (a key in the URL, `?key=`, is no longer accepted: it ends up in logs and shell history). Foreign
`Origin`s are refused.

**stdout** carries one JSON event per line, **stderr** diagnostics. Claude Code can start the exe through Monitor
and react to events without asking "done?". stdout is written by its own thread: if nobody reads it, the overlay
does not freeze and old lines are dropped from the queue (the `events` tool still returns them).

**The log** is `%LOCALAPPDATA%\TutorialOverlay\logs\overlay.log` (diagnostics and events; at 2 MB the file rolls
over, 3 previous files are kept). It is useful when the exe runs without a console and nobody sees stderr.
`--verbose` adds debug lines, for example why scrolling was not counted.

## Connecting an MCP client

**stdio (the usual way).** `TutorialOverlay.exe --stdio` is what MCP clients start:

```
claude mcp add --scope user tutorial-overlay -- "C:\path\to\TutorialOverlay.exe" --stdio
```

It is a thin bridge to the one server of this user, not a second overlay:

- the server is not running → it starts it on its own (two hops, as for the tray guard), so the overlay stays in the
  tray after the agent exits and a client that kills its server's process tree does not take it along;
- the port comes from `%LOCALAPPDATA%\TutorialOverlay\server.json`, the key from `TUTORIAL_OVERLAY_KEY` or `key.txt`;
- every stdin line goes to the server as a POST, answers and server-sent events come back as stdout lines, and the
  bridge keeps the event stream (GET) open, which is how the server knows the agent is still there;
- stdin closed (the client exits) → DELETE: the server is free for the next agent at once;
- the server went away (quit, restarted on another port) → the bridge finds or starts it again and keeps the
  session id, so the agent keeps its turn;
- a server started with another key (`--key`) answers 401: the bridge says so in the error the agent gets.

stdout carries JSON-RPC only; the bridge's diagnostics go to stderr.

**Streamable HTTP.** Clients that speak it may connect to the running overlay directly:

```
claude mcp add --transport http tutorial-overlay http://127.0.0.1:7788/mcp --header "Authorization: Bearer <key>"
```

with the port and the key of this computer.

The repository root's `.mcp.json` uses stdio through the installed `tutorial-overlay` command (no port or key to keep
in sync). That bridge talks to whichever server is running — a development build started by hand included — and
does not hold the development exe open, so rebuilding is not blocked while Claude Code is connected.

## Settings

`%LOCALAPPDATA%\TutorialOverlay\config.json` (all fields optional). The same fields can be given in a tutorial's
spec; they then apply until it ends:

```json
{ "port": 7788, "lang": "ru", "accent": "#FF4D6D", "animate": true, "hideWhenInactive": true, "autoColor": true, "hideFromCapture": false,
  "hotkeys": { "done": "Ctrl+Alt+Right", "back": "Ctrl+Alt+Left", "skip": "Ctrl+Alt+Down", "undo": "Ctrl+Alt+Z", "stop": "none" } }
```

The settings window writes the file atomically. If `config.json` cannot be read (a typo made by hand), it is kept
next to it as `config.json.broken-<date>` rather than lost.

`autoColor` (default `true`): the marks' colour is matched to the app. The overlay samples the pixels around the
step's first target and picks, out of bright colours (magenta, orange, yellow, cyan, red, violet), the one least like
the interface. An explicit `color` on a shape wins. `lang`: `ru` or `en` (the card's and hints' text). `"none"`
turns a hotkey off. The hint line at the bottom of the card is built from the hotkeys in effect.

Default hotkeys (active while the card is shown): **Ctrl+Alt+→** done, **←** back, **↓** skip, **Z** undo a hit,
**X** stop.

A guard watches the mouse and keyboard hooks. If the system sees input while the hooks have been silent for more
than 1.5 s, it is not necessarily broken: some input never reaches the hooks — touchpad scrolling and gestures in
programs that take them directly (browsers, VS Code), input to windows run as administrator, the sign-in screen and
UAC. So the guard first probes the hooks: it sends a 1 px mouse move and back (the cursor stays put). If it arrives,
the hooks are alive, and once a minute the log says where the unseen input went. If it does not, Windows has removed
the hooks (it does so without warning to a hook that once answered too slowly): they are installed again, the
`hook_reinstalled` event is sent, and the log says whether the hook thread responds, the handler's longest answer and
the garbage collector's pauses. The hook thread does not let Windows slow it down to save power.

## Apps run as administrator

Windows does not let a normal process reach the input and the interface of a process with administrator rights.
Targets in such an app get the visibility `elevated`, the card explains that TutorialOverlay has to be run as
administrator too, and `validate` counts it as an error. The hook guard does not reinstall the hooks in vain meanwhile.

## Screenshots and privacy

The overlay does not record the screen on its own. A full screenshot is taken only on a `screenshot` call. A pixel
check reads a 3×3 square at the target's point every 150 ms, and only for targets with a `check`.

Your own screenshots and screen recordings see the overlay. A pixel check looks through a tiny transparent "hole"
the canvas leaves at the probe point. The agent's `screenshot()` excludes the overlay's windows from capture for a
split second (`includeOverlay: true` takes them in). The windows of Snipping Tool, ShareX, Greenshot, OBS and the like
do not count as leaving the app and do not hide the marks. To keep the overlay out of every capture, set
`"hideFromCapture": true` in `config.json` or start it with `--hide-from-capture`.
