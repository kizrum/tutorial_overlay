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

`PluginContract` tests keep the plugin and the program in step. Every `tool(args)` call in the skill
(`plugin/skills/tutorial-overlay/SKILL.md`) must be a tool the server lists, with arguments it takes. Every name in a
code span of the skill (spec fields, events, answer fields) must be a word the program uses; `find.X` and
`type: 'x'` are checked against the spec parser's own field lists. The Stop hook (`plugin/hooks/stop-guard.ps1`)
must read only the `lesson.json` fields and states the program writes. When the program renames something, these
tests name the line of the plugin to fix. A fallback the skill keeps for an older TutorialOverlay goes into
`PluginContractTests.Legacy` with its reason.

## OCR bench

Saved screenshots of real apps with the text to look for and where it truly is, read through the same path as a
live text anchor (`Finder.FindIn`). It measures what matters for a mark: does it land on the target (recall), on
something else (false match), how far off the centre is, how often the text is on screen more than once, and how
long a read takes. Each run is saved and compared with the previous one, so a change to the OCR, its
preprocessing or the matching shows up as cases that changed, not as a feeling.

```powershell
dotnet run --project tests -- --ocr-capture FL64 fl_main            # the app's window as a live search reads it
dotnet run --project tests -- --ocr-bench                           # all cases in examples/ocr/cases.json
```

The capture writes `name.png` and, to help with labelling, `name.uia.txt` (UI Automation elements: `rel` is the
position in the picture, a truth that does not come from OCR) and `name.ocr.txt` (what OCR reads now — check it on
the picture before taking it as a truth). A case is an anchor's `find` as written in a tutorial and a `truth`
rectangle in the picture's pixels, or `null` for a text that must not be found:

```json
{ "cases": [ { "id": "fl-options", "image": "fl_main.png", "find": { "text": "OPTIONS" }, "truth": [264, 15, 74, 30] } ] }
```

Like the examples, the set lives in `examples/ocr/` on your own computer only. Worth covering: each app you teach,
dark and light themes, 100 % and 150 % scale, small and dim captions, Cyrillic and mixed lines, words that occur
more than once, and texts that are absent but close to something present.

## Live UX run

Tests cover the logic, the bench covers OCR; only a live lesson shows whether the loop works for a person. One
5–15 minute lesson per app (FL Studio first; then one with a real UIA tree, such as Excel or OBS), driven by the agent
through the plugin as a user would, each time through all of these:

1. ask the agent for the lesson; do a few steps as the card says;
2. pretend not to understand a step and press Help — does the answer get you unstuck?
3. make a mistake (click beside the target, open the wrong menu), circle the area and ask the agent to sort it out;
4. move and resize the window, scroll, switch a tab, open a dialog — do the marks follow, and come back?
5. go back a step and do it again;
6. finish, and ask to go on with a new topic.

For every problem note: the app, the step, what happened, how much it got in the way (blocks the lesson / costs
time / cosmetic), whether it repeats, and whether it is general or this app only. Fix in that order: what breaks the
lesson, marks on the wrong place or lost, steps that complete wrongly or never, Help, then navigation.

## Example tutorials

Ready tutorials (specs in JSON) are not part of the repository: the `examples/` folder and the tutorial library
(`%LOCALAPPDATA%\TutorialOverlay\tutorials\`) stay on your own computer, and `examples/` is in `.gitignore`. If the
folder exists, the tests check that no tutorial in it has unknown fields and that each gives the scale (`"scale"`)
its offsets were measured at; without it these tests check nothing.

## Releases

Built on this computer, no hosted CI. Bump `<Version>` in `TutorialOverlay.csproj`, add its section to
`CHANGELOG.md`, then:

```powershell
.\release.ps1 -Tag
```

It runs the tests, publishes a self-contained single-file exe, checks that `--version` reports the new version, and
writes `dist\TutorialOverlay-<version>-win-x64.zip` and tags `v<version>`. Then: push `main` and the tag, and
create the GitHub release `v<version>` with the zip.
