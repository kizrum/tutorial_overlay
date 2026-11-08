# Tutorials: the spec format

[← README](../README.md) · [Server](server.md) · [MCP tools](tools.md) · [Tutorials](tutorials.md) · [Development](development.md)

## Shapes

On the canvas (clicks go straight through to the app):

- `circle` — a ring; `rect` — a frame (without `w/h` it frames the anchor's element); `arrow` — an arrow (aims at
  the target by itself, the tail faces the centre of the screen);
- `badge` — a number; `label` — a caption; `info` — an info box with a title and an icon;
- `check` — a check mark; `checkbox` — a checkbox that ticks itself when the target is done (handy for FL's cells);
- `dim` — darkens the screen, with "windows" over the targets.

Clickable (separate windows that do not take the focus from the app):

- `button` — a button anywhere on screen: `{text, id, action: done|next|back|skip|stop|undo|dismiss, style}`;
  sends the `button` event;
- **the step card** — "Step n of N" with a short progress strip beside it, title, text, a folded "Why" (click to
  open; it stays open for the rest of the lesson), a checklist of targets, quiet "Back", "↶" (undo the last hit,
  shown only after one) and "Skip" icons, "Help", "Done", "✕" (stop) and the step's own buttons (`buttons`). A
  problem that blocks the step is shown at the top of the card. Next to "✕", the assistant's icon: green while the
  agent follows the lesson, grey and struck through while it is away (its turn is over); then what needs the agent
  is dimmed (see [Helping the user](#helping-the-user) and server.md, "Is the agent listening?").

The default shape (a target without `shapes`): an anchor's element gets a frame round it; a found text or
template (`find`) gets a frame the size of the found piece; a point inside an element (`offset`) gets a ring. A
frame of the right size round a point is given explicitly: `{"type": "rect", "x": -60, "y": -18, "w": 120, "h": 36}`
(`x, y` from the anchor point).

Layout without overlaps: rings of neighbouring targets shrink, labels, info boxes and buttons without explicit
`x,y` find free room around the target (a label goes to the arrow's tail), and the card keeps clear of everything
drawn. When only its height changes (the "Why" opens, a hint arrives) it keeps the edge nearest the screen edge: in a
bottom corner it grows upward, so its buttons stay under the cursor. The card can be dragged with the mouse; it then
stays where it was put.

## Anchors

```json
{ "process": "FL64", "window": "part of the title", "title": "a window OR a Pane/Window in UIA",
  "element": [{ "name": "Channel rack", "role": "Pane" }, { "name": "…" }],
  "origin": "topleft|center|…", "offset": [dx, dy], "relativeTo": "frame|client" }
```

- Names match as a case-insensitive substring. Of several elements with the same name the largest is taken
  (`pick: largest`). FL needs this: each of its sub-windows shows up in UIA as two Panes.
- The resolver looks through **all** the process's windows, menus and dialogs included. UIA is queried on a
  background thread, and every frame a mark takes the window's current position, so it follows the window without lag.
- A mark hides when its window is minimised, closed, covered by another window or when the app is not in front
  (`hideWhenInactive`, on by default). While the user is in another window the step's conditions are frozen. On the
  way back everything shows again on the same step; if the window was recreated, the anchor is found again.
- Clicks are matched by the **process** of the window under the cursor, not by its title.

**Text anchor** — for programs that draw their own interface and expose nothing to UIA (FL Studio):

```json
{ "process": "FL64", "element": { "name": "Channel rack" }, "find": { "text": "Kick", "match": "contains", "index": 0 }, "offset": [-40, 0] }
```

The caption is found by the text recognition built into Windows (`Windows.Media.Ocr`, called directly through
WinRT, no extra libraries) in the window's own pixels, so it works under other windows too. The anchor point is the
centre of the found text; `offset` counts from it. The mark follows the caption while it scrolls (a search every
0.7 s). `match`: `contains` (default), `exact` (the whole line), `regex`. Recognition slips of the "a letter for a
look-alike" kind (l/I/1, 0/O, 5/S, 8/B, 2/Z, a Latin letter for the same-looking Cyrillic one — up to one in 6
letters) are forgiven, but only within whole words: "Kicks" will not match "Kick". Other characters must match:
"Fill each 8 steps" does not pass for "Fill each 2 steps", nor "Pattern 12" for "Pattern 1". Letter case never
matters (in text, element names, window titles); `automationId` alone is compared exactly. In a menu, anchor an
item to the menu window (`windowClass` + `offset`) rather than to its text: the hovered item is repainted and may
not be read. `index` is the occurrence's number top to bottom and left to right; `within: [x,y,w,h]` is a part of
the element.

A text named only by its app (`{process, find: {text}}`) is looked for in the app's menus and dialogs too: first a
window opened during the step, then the main window, then windows that were already open. `in` says which window it
lives in: `"main"`, or `{title, class}` — the start of the window's title (the part before " - ": "Settings" of
"Settings - Look and feel", which stays when the tab changes) and/or its class (a menu without a title by its class
alone). Such a text is looked for there only; with the window closed it is not shown, and the card says to open that
window. When the user clicks a text that has no `in`, the overlay adds it from the window it was found in (event
`target_pinned`), so going back or replaying the saved lesson never takes the same words in another window: the
"Theme settings" item of a menu for the Theme tab of the settings, a dialog's "Options" heading for the menu bar's
OPTIONS.

A step whose targets live in a menu or popup the previous step opens can say so with `inMenu: true` (useful where
the menu is drawn inside the app's window, as in Blender, so no window comes and goes). When such a menu is closed —
a wrong item picked, Esc, a click beside it — the step cannot be done, and the lesson goes back to the step that
opens it, with "It closed — open it again". The same holds for a target in a window of its own (`find.in`) and one
learned to live in the popup the previous step's click opened. Going back on its own is kept narrow so it can never
trap the user: only such targets; only after the target was on screen during the step and is gone for good (its
window closed, or its words are not at its place any more — a word only repainted or under a tooltip is not gone);
at most two steps back; and once a minute per step — gone again soon after, the card says what to reopen instead.

Many menus open a submenu on hover: the user points at "Mesh", and nobody clicks. A step that waits only for its click,
followed by an `inMenu` step, is therefore also done when that step's items show up (`step_completed` with
`how: "opened"`) — provided they were all away for a moment during the step first, so words already on screen or one
lost read of OCR never complete it. Moving off closes such a submenu again, and the lesson comes back to "Mesh" as
above.
A plain target that OCR misses for a moment, and steps that are actions (keys, drags, scrolling), never go back.
Done and Skip always move on.

Several occurrences in one window, no `index`: a whole caption comes before the same words inside a longer one ("Dark"
before "Dark orange", the "Ports" tab before "Ports.cs"), then the one in the author's letter case ("BassDrum" before
"Bassdrum 22"), then reading order. `index` counts in reading order alone. A text only matches from the start of a
word: "Reset" is not in "Presets".

`text` may list several wordings the app may use — versions and translations name the same item differently:
`find: {text: ["General settings", "General preferences"]}`. The first one found wins, in the given order, so a path
written ahead does not wait on the agent when the app says it differently. Letter case never matters. A wording that
is a cut-down form of another one listed (`"Snare"` beside `"808 Snare"`) counts only where it is most of its line:
otherwise, with the item missing, it would catch "AP - Future Bass Snare Reverse". Wordings of their own
(`"Preferences"`, `"Options"`) match like any text, also inside a menu bar or a tab strip, which OCR reads as one
line. Write full names; leave out generic words shared by many items ("808" in a list of 808 samples).

The recogniser follows the query's alphabet: Cyrillic gets the Russian recogniser, Latin the English one (the
English one turns Russian text into garbage, the Russian one reads "You" as "Уои"). `find_text` without `text`
reads with both and keeps the fitting variant for each line. `lang` sets the recogniser explicitly. Recognitions run
one at a time: the Windows engine cannot take two at once. `find_text` without `text` shows how OCR read an area.

**Shapes default to `auto`.** A target with no `shapes` is framed at its own size: an element by its rectangle, a
found text by its words, a template or a spot by the visible thing there (measured from the window's pixels, and
re-measured while the lesson runs, so the frame follows a caption that grows). Hand-made shapes are for arrows,
labels and notes; `validate` adds a `shapeNote` when a ring or frame given by hand does not fit the target.

Templates (`find: {template}`) work the same way, `index` included (the n-th of identical look-alikes in reading order,
such as one of FL's mixer strips): the anchor point is the centre of the found piece, and `offset`
counts from it. They are searched in two passes: a quick one on a half-size picture picks 8 candidates, an exact one
at full resolution chooses among them, so similar buttons on neighbouring rows ("808 Kick" and "808 Snare") are not
confused.

Captured templates are stored on disk (`%LOCALAPPDATA%\TutorialOverlay\templates\`) and survive a restart, and a
saved tutorial carries the templates it uses inside its file (`"templates": {id: PNG in base64}`), so it runs from
the tray menu and on another computer too.

**Checking before the start** — `validate(spec)` resolves all the tutorial's anchors right now. Errors (the process
is not running, a template was never captured, a step could not be parsed) must be fixed. Warnings on later steps
often mean something an earlier step will open; `occluded` and `inactive` only mean another window is in front now.
A target named with a number that changes as the user works (`Ln 36, Col 1`, `3 problems`) gets `volatile` with the
steady part of the name: anchor by that part (`find.text`) or by the element's position, or the mark is lost the
moment the number changes.

## Steps and conditions

```json
{ "id": "kick", "title": "…", "text": "…", "anchor": { … }, "toggle": true, "dim": 0.4, "numbered": true,
  "targets": [{ "name": "Step 1", "shapes": [ … ], "check": { "at": [335, 93], "expr": "lum>120" },
                "button": "left|right|any", "optional": false, "doneStyle": "hide|check" }],
  "until": [ "all_clicked", { "type": "appear", "title": "Piano roll" }, { "type": "disappear", "title": "Piano roll" },
             { "type": "pixel", "anchor": { … }, "at": [x, y], "expr": "r>140 && g<100" }, { "type": "count", "count": 3 } ],
  "untilMode": "any|all", "buttons": [{ "id": "yes", "text": "Yes" }], "controls": ["back", "undo", "skip", "done", "stop"] }
```

- Without `until`: a step with targets completes when all of them are clicked. A step without targets waits for
  "Done", a hotkey or `complete_step`.
- `disappear` fires only if the target was seen on screen first.
- `toggle`: a second click on a dimmed target lights it again. If the target has a `check`, its state comes from
  the pixel, and toggling happens by itself.
- Pixel check expressions: channels `r g b lum max min sat`, differences like `r-g`, operators `> >= < <= == !=`,
  joined with `&&` and `||`, closeness to a colour `~#1AD28F:40`. Check the real colours first: in FL, steps 1–4 of
  a bar light up grey, not green, so a brightness test (`lum>120`) is safer than a colour one.

More conditions:

- `{ "type": "key", "keys": ["F6", "Ctrl+S"] }` — a key combination pressed while the app is in front. Keys are read
  only while such a condition is active, and only a match is reported (`key_matched`): other presses are recorded
  nowhere.
- `{ "type": "drag", "from": { "element": { "name": "Browser" } }, "to": { "element": { "name": "Channel rack" } } }` —
  a drag from one area to another. Areas: the anchor's bounds, `rect:[x,y,w,h]` from the anchor point, or `radius`.
  Drawn as source → arrow → drop zone.
- `{ "type": "uia", "element": { "name": "Bold" }, "property": "toggle", "equals": "on" }` — an element's state:
  `exists`, `name`, `value`, `toggle`, `selected`, `expanded`, `enabled`, `focused`, `text`. Compared with `equals`,
  `contains` or `matches` (regex), plus `not`. When the value changes, the `uia_changed` event is sent.

**Follow a path** — the `path` shape and the `trace` condition:

```json
{ "type": "trace", "anchor": { "element": { "name": "Playlist" } }, "path": [[40, 60], [180, 20], [320, 80]],
  "hold": ["Shift"], "tolerance": 24, "coverage": 0.85 }
```

The points are offsets from the anchor point; a smooth curve runs through them (`smooth: false` — straight
segments). It is drawn dashed, with direction arrows, a start dot, an arrowhead at the end and a light running
along it; the part done so far fills in. It counts if the mouse, with a button held, covered at least `coverage` of
the path from its start (`ordered: false` — in either direction) without ever going further than `tolerance` px
from it. `hold` lists keys to keep down the whole time (`drag` has the same field); next to the start a "Hold Shift"
chip shows, green while the keys are down. Events: `trace_done`, and `trace_off` with the reason `off_path`,
`too_short`, `released_key`, `key_not_held` or `start_at_the_beginning` and the share of the path covered. The
mascot points at the start of the path.

**Scroll** — the `scroll` condition:

```json
{ "type": "scroll", "in": { "element": { "name": "Browser" } }, "direction": "down", "notches": 5 }
{ "type": "scroll", "in": { "element": { "name": "Browser" } }, "direction": "down", "until": "visible" }
```

Counts wheel notches over the area in the given direction (`up/down/left/right`); `until: "visible"` — until the
step's targets come into view. The area gets a thin frame, a mouse icon with running arrows and a counter sits at
its edge, and the mascot stands next to it. If the user scrolls the app but over another pane, the mascot says where
to scroll ("Scroll over “Browser” — inside the frame"). Event: `scroll_done`.
The wheel, the touchpad and the scroll bar all count. Wheel messages add up (a notch is 120 units; a touchpad sends
many small portions). But browsers and many programs take touchpad scrolling directly, without wheel messages, so
the overlay also watches the area itself: ~8 times a second it compares the area's own pixels (without our
drawings) and sees which way the content moved — moved up means scrolled down (~60 px a notch at 100%). The larger
of the two counts. A shift counts only with the cursor over the area and input from the user just now, so a chat, a
log or a video that scrolls by itself does not complete the step. `until: "visible"` also works without wheel
messages: the step completes when the hidden targets come into view.

Mouse movement (only with a button held) and the wheel are read only while the current step needs them; only the
result goes out, never the mouse's path.

**A target past the edge of its pane** — no separate scroll step is needed. If a step's target has gone past the
edge of its pane (a UIA element's rectangle outside the visible part; a text or template no longer found), a wheel
icon with running arrows appears at that edge, and the mascot stands next to it and says "Scroll down". The
direction: for UIA elements, from their rectangle, and then the distance shows too ("≈ 6" notches); for texts and
templates, from the way they were moving before they vanished, or the edge where they were last seen (no distance
here). Scrolled past it, the hint turns round. Once the target shows, the mascot goes to it and the mark is back on
it. While a text is out of view it is read twice as often. All this stays inside the overlay: the agent gets no
pictures and no events; `anchor_found/lost` come at most once in 3 s per anchor, so scrolling a long list back and
forth does not flood the agent with events.

**A click that opens a window** — the target's `opens` field:

```json
{ "anchor": { … }, "opens": { "title": "Piano roll" } }                         // a sub-window inside the program
{ "anchor": { … }, "opens": { "process": "chrome", "timeoutMs": 15000 } }        // a window of another program
```

The target counts only when a **new** window or pane appeared after the click: a window that was already open does
not count. If nothing appears within `timeoutMs` (6 s by default), the target stays lit and the agent gets
`no_effect`; success is the `opened` event. While the user works in the opened window of another program, the step
is not frozen, and its card stays visible.

**A chain within a step** — `"sequence": true`: the targets show one at a time, in order. The typical case is a
right click on a channel, then an item in the menu that opens:

```json
{ "sequence": true, "targets": [
  { "name": "Right-click FLEX Bass", "button": "right", "anchor": { "element": { "name": "Channel rack" }, "offset": [224, 272] },
    "shapes": [{ "type": "rect", "x": -62, "y": -18, "w": 124, "h": 36 }] },
  { "name": "The “Piano roll” item", "anchor": { "windowClass": "TQuickPopupMenuWindow", "offset": [40, 120] },
    "opens": { "title": "Piano roll" } } ] }
```

The menu is a window of its own, so the menu window itself is the anchor. Without `offset` the whole menu is framed;
the exact spot of an item is taken with `pick(anchor: { windowClass: … }, passThrough: true)`. If the menu closed
without the right item chosen, the previous target lights up again and the `sequence_reset` event is sent.

**A grid** — one entry instead of many targets:

```json
{ "name": "Step {col}", "grid": { "from": [335, 93], "step": [24, 45], "cells": [1, 5, 9, 13] }, "check": { "expr": "lum>120" } }
```

`from` is the centre of the first cell relative to the anchor point, `step` the distance between centres. Give
`rows` and `cols` or a list of `cells` (`[[row, column], …]`, or just column numbers of the first row; all counted
from one). The default shape is a checkbox of `w × h`. `from` and `step` are easiest to get with
`pick(anchor, count: 2)` on two neighbouring cells: the answer has a `gridHint`.

### Step 0: getting ready

If the app waits for an answer in a dialog the step knows nothing about (a window with a title of the same program
on top of its main window, for example "Cloud path warning"), the step's text is of no use: the right place cannot be
reached anyway. The card then shows getting ready instead of the step: before the tutorial starts it is
**"STEP 0 / N" — "Let's get ready to start"**, later "First, close this window". The dialog is framed, the mascot
stands next to it (not on it) and asks to answer or close it; scroll hints are not shown, and there are no "Done" and
"Skip" buttons. As soon as the dialog is closed the card moves on to the step by itself. The agent gets the
`prepare_started` and `prepare_done` events. While a screenshot tool (Snipping Tool, ShareX and the like) is open,
the tutorial still treats the program's window that was in front before it as active.

## The mascot "Shazhok"

The step card come alive: it flies up to the target, points at it and says the step's line in a speech bubble. On
its "forehead" is the tutorial's progress bar.

- It picks a spot out of 16 around the target: on screen; not on the target, the marks, captions or the card; not on
  the line from the cursor to the target (the user's hand travels there); over plain background rather than dense
  interface (judged from the app window's own pixels); other things equal, close to where it stands. The bubble also
  picks a free side. When the app's window is dragged, it rides along with the target.
- To the next target: if its hand reaches from where it stands, it just moves the hand; if the new spot is a couple
  of steps away, it walks; further than that, it vanishes and appears at the new spot instead of a long trip across
  the screen. Between steps and without a target it stays put.
- The target's highlight appears together with it: when it has finished appearing or stopped (but no later than
  1.5 s).
- It drops in from above and lands with a squash, and leans into a walk. A hit — a jump, a thumbs-up or a nod, in
  turn; a miss — its face shakes "no", then it points again, harder, and the target flashes; "?" — it looks at the
  target through a loupe while the agent prepares a hint; the agent away — it dozes; a step that waits for a shortcut
  (`until: key`) — it holds up a plate with the keys and presses it; a drag or stroke — it carries a dot along a
  dashed path; a scroll — it turns a wheel; 20 s with nothing happening — it looks round and stretches; the end of
  the tutorial — it dances under confetti. It hides together with the marks when the app
  is not in front.
- Its line is the step's `say` field (short, up to ~60 characters), otherwise the step's title.
- The overlay works out all the motion itself from the target's coordinates and events: the agent does not drive the
  mascot and spends no tokens on it. A separate small window, ~2% of one core.
- It is on by default. It is switched off in the settings ("Look" tab, the "Mascot" toggle; this takes effect at
  once, even in the middle of a tutorial) or for one tutorial in its spec: `"mascot": false`.

## End of a tutorial and the library

After the last step the card does not disappear; it shows "Tutorial complete!" and offers:

- **Save tutorial** — to the library `%LOCALAPPDATA%\TutorialOverlay\tutorials\` (the `tutorial_saved` event);
- **Keep learning** — the card starts waiting, the agent gets `continue_requested` and writes the next lesson with
  `append_steps`, and the user carries on in the same card. The button shows only if the agent made a request in the
  last 30 minutes;
- **Go through again** — the same tutorial from the first step;
- **Close**.

The choice arrives as the `outro` event. `"outro": false` in the spec turns it off.

A saved tutorial is plain JSON with the spec (including steps added with `append_steps`). What to do with it:

- go through it again without the agent — the tray icon's menu → "Tutorials";
- run it from the agent — `start_tutorial({ "name": "fl-instrument-setup" })`, optionally with `startAt`;
- `list_tutorials`, `save_tutorial(name?, spec?)`, `delete_tutorial(name)`;
- share it — the file from the library folder moves to another computer as it is, templates included.

Saving under an existing name overwrites the file; `save_tutorial` then answers `replaced`. Windows device names
(`con`, `nul`, `com1`…) get a `-1` suffix.

**Display scale.** Offsets in a spec are physical pixels, right only for the scale they were measured at. A saved
tutorial remembers it (`"scale": 1.65`; it can also be given by hand). If the tutorial runs at another scale, the
pixel fields (`offset`, `x`, `y`, `w`, `h`, `r`, numeric `from`/`to`/`step`, `at`, `path`, `points`, `within`,
`rect`) are multiplied by the ratio of the scales, and the `start_tutorial` answer says so in its `scale` field. Line
widths, `pad` and `tolerance` already follow the scale when drawn and are left alone.

## Checking a spec for typos

Spec parsing is lenient: an unknown field is simply ignored, so a typo (`"untill"`, `"tagrets"`) silently changes
how a step behaves. `start_tutorial`, `append_steps` and `draw` return `warnings`, and `validate` returns
`unknownFields`: every field the parser does not read, with a "did you mean" hint, plus unknown condition, shape and
card button types:

```
steps[2].untill: unknown field (did you mean 'until'?) — ignored
steps[0].targets[1].shapes[0].type: unknown shape 'circel' (did you mean 'circle'?) — nothing is drawn for it
```

## Helping the user

The card has a **"Help"** button (hotkey **Ctrl+Alt+H**). It opens the choices **"I can't find where to click"**,
**"I don't get what to do"** and **"Something else"** (the user describes it in their own words). While the agent
is away, "Something else" is dimmed: a press says to write to the assistant in the chat.

"Something else" turns the card into **a conversation about the step**: the user's messages on the right, the
assistant's answers on the left, three dots while it is looking. The input line is at the bottom (Enter — send,
Esc — back to the step). "‹ Step 3 · title" in the header goes back to the step; the conversation folds into an
"About this step" row under the step's text, with the newest message and a dot for an answer not seen yet. A click
on the row opens it again. What was typed and not sent stays when the user clicks elsewhere. The conversation
belongs to its step: on the next step the card starts without one.

- The agent gets the `help` event: the reason; for each target whether it is visible and why not (`visible`,
  `occluded`, `minimized`, `inactive`, `element not found`…) and where it is on screen; the step's latest misses; the
  active window and the cursor.
- On "I can't find…" the overlay itself darkens the screen around the targets for 6 seconds and sends ripples from
  them.
- Sketch pictures are kept in `%LOCALAPPDATA%\TutorialOverlay\help\`: on start the last 50 stay, none older than
  14 days.
- The input line has a **pencil**. It freezes the picture of the screen and puts a toolbar above the card:
  **pencil**, **circle** and **frame** (keys 1, 2, 3; Shift makes a round circle or a square frame), **undo shape**
  (Ctrl+Z or Backspace), **Cancel** (Esc — no picture) and **Done** with the number of shapes (Enter or a right
  click). Delete erases all; the toolbar can be dragged by its grip. Every shape gets a number, on the screen and in
  the picture the agent gets, so both can say "№2". The line then shows a small picture with a cross to drop it;
  pressing the pencil again adds to what was drawn. The circling
  stays on screen until the agent answers. The `help` event then has a `sketch` field: `bounds` and `center` in
  screen pixels, `strokes` (each stroke has a `shape`: `loop` | `line` | `dot`, `from`, `to` and up to 48 points),
  `atCenter` (the window, colour and UIA element at the centre of the circling, `rel` from the anchor of the step's
  first target, `suggestedAnchor`) and `image`, the path to a PNG of the place with the circling in
  `%LOCALAPPDATA%\TutorialOverlay\help\`.
- The agent answers with the `hint(text, title?, highlight?)` tool: the text appears right in the current step's
  card (in a conversation, as the assistant's next message). Until the answer, the card says "Told the assistant — a hint is coming."
- Pass the help event's `step` as `expectIndex`. If the user has moved on meanwhile, the answer is not dropped: it
  waits in that step's conversation and the card's bottom line says so ("‹ Back" leads there). A hint nobody asked
  for on that step, or for a question older than 15 minutes, is refused.
- If a target is not visible, the card explains why by itself: the window is minimised, the spot is covered by
  another window, the element was not found.
