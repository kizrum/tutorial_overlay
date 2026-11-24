# Development

[← README](../README.md) · [Server](server.md) · [MCP tools](tools.md) · [Tutorials](tutorials.md) · [Development](development.md)

Building from source and installing are in the [README](../README.md#building-from-source).

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

## Shape gallery

```powershell
dotnet run --project tests -- --shapes out.png            # every shape
dotnet run --project tests -- --shapes out.png elbow      # only cells whose caption has "elbow"
```

Draws each shape the way a tutorial writes it, through the real `Layout` and `Renderer`, over plain stand-ins for
an app (a button, a field, a segmented control, a line of text, a table, a dark grid). For looking at a shape's
design without starting a lesson; add a cell in `tests/ShapeGallery.cs` when adding a shape.

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

Start the server with `--record` for such a run: it saves a picture of the screen (the overlay included) for every
lesson event and every change of a mark's state, with what happened beside it, to
`%LOCALAPPDATA%\TutorialOverlay\records\<start time>\` (`NNNN-reason.jpg` and `index.jsonl`). The run can then be
looked at exactly as the user saw it.

For every problem note: the app, the step, what happened, how much it got in the way (blocks the lesson / costs
time / cosmetic), whether it repeats, and whether it is general or this app only. Fix in that order: what breaks the
lesson, marks on the wrong place or lost, steps that complete wrongly or never, Help, then navigation.

## Autopilot: lessons played by a robot

`tests/Autopilot.cs` plays tutorials in real apps the way a person would, through the running server: it reads where
the current step's target is (`status`), clicks it, presses the step's keys or turns the wheel, and waits for the step
to complete. It moves the real mouse, so nobody may use the computer while it runs.

```powershell
dotnet run --project tests -- --autopilot examples/autopilot             # every case in the folder
dotnet run --project tests -- --autopilot examples/autopilot --case calc # only cases whose file name has "calc"
dotnet run --project tests -- --autopilot examples/autopilot/fl-phonk-minute.json --keep-open   # leave the app open with what it made
dotnet run --project tests -- --autopilot examples/autopilot --autoplay             # the overlay's autoplay does the steps
dotnet run --project tests -- --autopilot examples/autopilot --autoplay --takeover  # ... and the robot takes the mouse midway
```

With `--autoplay` the robot presses "Do them all" and keeps its hands off: every step must complete with
`by: autoplay`. Where the autoplay hands the controls back on purpose (a target that cannot be undone, a choice, a step
with `"autoplay": false`, a password) the robot does that step itself, as the user would, and presses "Continue
automatically"; any other pause fails the case. A `help` from the autoplay (a target it cannot find) goes into the
report as a warning: the robot is no agent and does not answer it. `--takeover` moves the mouse while the autoplay is moving it: the controls must come back
within 1.5 s (`user_took_over` or `user_active`), then the robot resumes it.

A case file is `{name, launch: {exe, args, process, window, readyTimeoutMs, ready, dismiss}, spec}`. An app whose first
screen varies from start to start (FL Studio: a cloud-folder warning, a welcome window, the panels left open last time)
gets `ready`, an anchor that marks it as up, and `dismiss`, a list of `{anchor, waitMs}` the robot clicks in turn if
they show within `waitMs` — before the lesson, which then starts from the same screen every time. `process` is what the robot checks
before it starts the app and what it closes afterwards: a case whose app is already open is skipped (the robot never
touches a window it did not open). An app that keeps running in the tray (Steam) gets `attach: true`: the robot uses it as it runs, `exe` and `args`
only bring its window up (`steam.exe steam://open/main`), and afterwards it closes the windows that were not on screen
before (as their cross does) and leaves the app running. A lesson that changes files works in a `sandbox`:
`{dir, files: {name: text}}`, a folder inside `%TEMP%` that the robot makes anew with those files before the lesson
and deletes afterwards (Explorer: `exe` `explorer.exe`, `args` the folder). Environment variables in `exe`, `args`
and `dir` are expanded. The app counts as ready when `validate` resolves the first step's target; when the app opened behind another window, the robot raises it (by `window`, its title, for apps another process hosts, such as Calculator in ApplicationFrameHost; else by `process`).

What it does follows the step: a click with the target's `button` (left, right, middle); the step's keys, with the
pointer on the target first (Blender's keys act on the area under the pointer); the wheel over the scroll's area; a
`drag` from `from` to `to`, or a `trace` along its path, with the condition's button pressed and its `hold` keys down.
Where a region or path lies is asked from the server (a one-step `validate`), so it is resolved as the lesson resolves
it. Input goes through `SendInput`, moves included, so the overlay's hooks see it as a person's; Delete, Home, the
arrows and other keys the number pad shares are sent as extended keys. A step that waits for Done is looked at: the robot
points at each of its targets in turn and reports a mark that moves under the hand, then completes it on its own
behalf; a step that waits for a count is completed on its behalf and marked as skipped. An arrow at a target is
the way there: the robot moves along its curve, tail to tip (the server gives the line as the target's `path` in
`status`), then clicks.

It moves like a person: before acting it watches the step for 1.5 s, holds the pointer on the target for 1 s, looks
again and aims where the mark is now. In menus the way matters, since many open a submenu on hover: from the menu bar
into a menu (an `inMenu` step after one that is not) it goes down first, into a submenu (an `inMenu` step after
another) along the row first, then down. A step already done while the pointer rests on its target (a submenu that
opened on hover) gets no click, as a person would not click there either.

A step fails on `target_not_found`, on a target that stays not visible (the server's reason is in the report), or when
it does not complete within 20 s (3 s more for every target past the third: a checklist gets a click per target). Warnings: twitching (a mark or the card that moved more than 2 px while it watched,
or a mark that moved while the pointer was on it — unless its frame only grew to the row the hover lit up — a target
that blinked between visible and hidden), the card
covering the target, a target partly off screen, misses and `no_effect`. The report (`results/<time>/report.html` next to the cases) has every step with a screenshot of the
screen as the user saw it; failed cases are open. The exit code is the number of failed cases.

Cases are tutorial specs, so they stay local like `examples/`.

## The README's pictures

`docs/media/` holds the README's pictures: the logo, the lesson clip, the step card and the mascot. The logo
(`logo.webp`, one transparent picture for both GitHub themes) is an HTML page in `tests/logo.py` rendered by headless
Edge: `python tests/logo.py`. The animations are made locally with `tests/make_media.py` (Python with Pillow:
`pip install pillow`):

```powershell
# the mascot: the act drawn by the real figure code (MascotWindow.DrawStill) at 2x, lossless, empty margin trimmed
dotnet run --project tests -- --mascot $env:TEMP\mascot 25 2
python tests/make_media.py mascot $env:TEMP\mascot docs/media --act outro

# a lesson: record the screen (pointer drawn in, a ring on each click) while the autopilot plays a case
python tests/make_media.py record $env:TEMP\rec            # in a second terminal; stops when $env:TEMP\rec\stop exists
dotnet run --project tests -- --autopilot <case.json>
New-Item $env:TEMP\rec\stop
python tests/make_media.py clip $env:TEMP\rec docs/media/fl-lesson.webp --from 180 --to 730 --crop 420,0,2140,1500 --width 1600 --quality 90

# the step card alone, cut out of a recording (found wherever it stands): one run asks for help on step 1
# ("askHelp": [0] in the case), a second is the autoplay's; the README's card is step 1 of the first, then the second
python tests/make_media.py record $env:TEMP\rec            # in a second terminal, as above
dotnet run --project tests -- --autopilot <word-wrap case.json>
dotnet run --project tests -- --autopilot <word-wrap case.json> --autoplay
New-Item $env:TEMP\rec\stop
python tests/make_media.py card $env:TEMP\rec $env:TEMP\help.webp --to <last frame of the first run>
python tests/make_media.py card $env:TEMP\rec $env:TEMP\auto.webp --from <first frame of the second run>
python tests/make_media.py join docs/media/card-help.webp $env:TEMP\help.webp:0:5 $env:TEMP\auto.webp
```

Keep them sharp: frames are recorded nearly lossless, the card and the mascot are encoded lossless, and pictures
are about twice the width the page shows them at (high-DPI screens). The recording is the whole screen: take only
the lesson's frames and crop away the taskbar and anything else that is yours. Mascot acts are listed in
`tests/MascotFilm.cs`.

## Example tutorials

Ready tutorials (specs in JSON) are not part of the repository: the `examples/` folder and the tutorial library
(`%LOCALAPPDATA%\TutorialOverlay\tutorials\`) stay on your own computer, and `examples/` is in `.gitignore`. If the
folder exists, the tests check that no tutorial in it has unknown fields and that each gives the scale (`"scale"`)
its offsets were measured at; without it these tests check nothing.

## Releases

Tests and checks run on this computer; the published zip is built by GitHub Actions, and only on a release tag (so
that the exe can be signed: SignPath Foundation signs only builds it sees made from the source). Bump `<Version>` in
`TutorialOverlay.csproj`, add its section to `CHANGELOG.md`, then:

```powershell
.\release.ps1 -Tag
git push origin main v<version>
# when the workflow is done:
.\release.ps1 -Winget
```

`-Tag` runs the tests, publishes a self-contained single-file exe, checks that `--version` reports the new version,
builds a local zip as a check and tags `v<version>`. The pushed tag starts
[`.github/workflows/release.yml`](../.github/workflows/release.yml): it builds the exe again (`release.ps1 -Ci`),
signs it once the repository has the variable `SIGNPATH_ORGANIZATION_ID` and the secret `SIGNPATH_API_TOKEN`
(unsigned until then), zips it and creates the GitHub release `v<version>` with the zip and the version's
`CHANGELOG.md` section. It never replaces an existing release. `-Winget` downloads that zip and writes the winget
manifest with its hash to `dist\winget\<version>`. The workflow runs on a standard runner, free for a public
repository; nothing else runs on GitHub Actions.
