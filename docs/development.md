# Development

[← README](../README.md) · [Server](server.md) · [MCP tools](tools.md) · [Tutorials](tutorials.md) · [Development](development.md)

## How it is built

One process, several threads. All tutorial state lives in `Engine` (files `src/Engine*.cs`) behind one lock,
`Engine.Sync`: tool calls, input handling and the interface's frame all take it.

| Thread | What it does | Where |
|---|---|---|
| UI (30 ms timer) | `Engine.Tick` builds the frame: marks, card, mascot; the canvas, the card and the mascot are drawn from it | `Program.cs`, `OverlayWindow.cs`, `CardWindow.cs`, `Mascot.cs` |
| `anchor-resolver` | every ~140 ms goes through the windows and finds UI Automation elements; the frame takes the ready result plus the window's current position | `Resolver.cs` |
| `template-finder` | looks for templates and text (OCR) in the window's own pixels | `Finder.cs`, `Ocr.cs` |
| `text-tracker` | between OCR reads, follows a found text ~30 times a second as it scrolls | `Finder.cs` |
| input hooks | low-level mouse and keyboard hooks; events go to a queue that `input-consumer` works through; a guard reinstalls the hooks if Windows removed them | `InputHook.cs` |
| MCP server | Kestrel on `127.0.0.1`, one agent at a time | `McpServer.cs`, `Tools.cs`, `ClientLease.cs` |
| event output | stdout on its own thread and a buffer of the last 1000 events | `Events.cs` |

The rules that keep it fast:

- **Nothing under `Engine.Sync` waits for the system.** Reading screen pixels (`check` probes, `pixel`
  conditions), window pictures and UI Automation run on background threads; under the lock there is only
  arithmetic on ready data. Otherwise clicks and the agent's calls wait for the frame.
- **A window picture is shared.** `PrintWindow` always renders the whole window, so each window's picture lives for
  90 ms, and the template search, OCR, scroll watching and the mascot cut their pieces out of it
  (`ScreenCapture.cs`).
- **The canvas repaints only what changed.** The frame buffer is kept between frames; the glow breathes in 8 steps,
  and each frame repaints only the rings and the edges of frames, not the whole window (`Renderer.Dirty`).
- **Window order changes in one batch.** The canvas is raised together with the card, the buttons and the mascot
  through `DeferWindowPos`, so a highlight is never above the card (`Native.RaiseTogether`).

## Tests

Logic that needs no screen (spec and grid parsing, typo checks, scale conversion, pixel expressions, hotkeys, OCR
text matching, the event buffer, files, default shapes, repaint regions) is covered by tests in `tests/` — a plain
console program without test frameworks or NuGet packages. Run from the repository root:

```powershell
dotnet run --project tests            # all
dotnet run --project tests -- Lint    # only those with "Lint" in their name
```

The exit code is the number of failed tests.

## Example tutorials

Ready tutorials (specs in JSON) are not part of the repository: the `examples/` folder and the tutorial library
(`%LOCALAPPDATA%\TutorialOverlay\tutorials\`) stay on your own computer, and `examples/` is in `.gitignore`. If the
folder exists, the tests check that no tutorial in it has unknown fields and that each gives the scale (`"scale"`)
its offsets were measured at; without it these tests check nothing.
