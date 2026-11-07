# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/): while the version
is 0.x, a minor bump (0.2 → 0.3) may change MCP tools or the tutorial spec format; a patch bump (0.2.0 → 0.2.1)
only fixes things.

## [Unreleased]

- `validate` warns (`repeated`) when a text target without `index` is on screen more than once, and lists where:
  the mark goes to the first in reading order, which may not be the one the step means.
- `find.in` names the window a text lives in (`"main"`, or `{title, class}`): it is looked for there only, and with
  that window closed the card says to open it. A text clicked where it was found is pinned to that window
  (`target_pinned`, kept by `save_tutorial`): going back no longer marks "Theme settings" in a menu for the Theme tab
  of the settings, or a dialog's "Options" heading for the menu bar's OPTIONS.
- On a step gone back to, a window opened just before it is no longer taken for the step's own.
- A text seen in a menu that has closed no longer asks to scroll the main window toward where the menu was.
- Several occurrences in one window, no `index`: a whole caption before the same words in a longer one, then the
  author's letter case, then reading order.
- A text found in the window the previous step's click opened, or in the previous step's window, is pinned there
  before it is clicked, and that window is read right after the step's new ones: the step after the Theme tab finds
  the settings' "Options", not the menu bar's OPTIONS.
- A window the lesson led to (a target lives in it, the step's condition names it, or the previous step's click opened
  it) is never "a dialog to close first".
- Not found, but a line holds part of it: that line is read again, enlarged ("Save preset…" on FL's dark-orange
  button, where the recognizer dropped "Save").
- Looked for in a whole window, a still text that turns unreadable (repainted in a new theme, hovered) keeps its mark
  for up to 20 s and never asks to scroll: a window's button bar does not scroll. One jump of the follower before it
  is lost no longer counts as scrolling.
- A hovered menu item no longer shakes the mark: a sideways step of up to half a line, or a pixel or two, is not
  followed.
- A caption framed as its control only when it sits in the middle of it: the caption under a picture tile no longer
  reaches into the next tile, and two buttons on one strip ("Reset", "Save preset…") are framed apart.
- `--record` (development): a screenshot with the overlay for every lesson event and mark change, with the event and
  the marks' states beside it, in `%LOCALAPPDATA%\TutorialOverlay\records\`.
- Scrolling seen in the pixels is judged in strips: a page that scrolls beside a list that stays (Steam's settings)
  counts — touchpad scrolling in a browser-based app sends no wheel messages, so the pixels are all there is.
- A text that stood still and turns unreadable (a selected list row, light on blue) while the same words show
  elsewhere does not jump there: its place is read again, enlarged and inverted, and while words are still there the
  mark stays — "Counter-Strike 2" in Steam's library list, not the "Counter-Strike 2 Update" news beside it.
- A step whose target lives in a menu (`inMenu: true`), a window of its own (`find.in`) or the popup the previous
  click opened, and was seen and then gone for good, goes back to the step that opens it, with a card saying so —
  at most two steps, once a minute per step; plain targets and action steps never do.
- Blender's underlined shortcut letters: a word's first letter read as another ("Bender Image") is one slip in a
  longer text. The help circling fades after 6 s. A mark is held at its place only while the same word (by its
  letters' shape) is there; no scroll hint without evidence of scrolling, nor over an area another window covers.
  A dialog in the way stays in the way while open, wherever the pointer (Blender's focus) goes.
- A text is found from the start of a word on: "Reset" no longer lands on the "Presets" heading above the button
  ("Kick" still counts in "Kicks").
- `match: "exact"` no longer takes one side of a dashed title for a menu-bar item: "Master" is not found in
  "Mixer - Master".
- A found text is framed and clicked as the control it sits on: a drop-down with its arrow, a tab, a menu-bar item
  (up to halfway to its neighbour). A click on the arrow or beside the caption is a hit, not a miss. Text in a page,
  with nothing around it, is still framed by its words.
- `match: "exact"` also matches one item of a menu bar or toolbar that OCR reads as one line ("Steam View Friends").
- A found text no longer jitters: it stays put through a read that misses it or places it a few pixels off, keeps
  to the occurrence it was on (not the page heading with the same words), and keeps the size of its control while
  the pointer hovers it or its neighbours. Between reads it is followed in the app's own pixels, not the screen,
  so the overlay's frame and mascot over it no longer throw the follower off; a highlighted neighbour no longer
  counts as scrolling.
- "Help" on the card is answered at once by the overlay itself: what to click, and where it is or why it is not
  marked. A hint from the agent replaces it; the card never waits on an agent that is busy or gone.
- "Scroll" is suggested only where there is something to scroll (a pane, or an area as big as a list), not for a
  text missing from a menu bar.
- Text looked for in the whole app searches a menu or dialog opened during the step first, then the main window,
  then dialogs that were already open (the menu's OPTIONS, not the "Options" heading of an open settings window).
  `validate` searches an app's menus and dialogs too.
- An app that stops answering UI Automation (Steam while a menu is open) no longer stalls the others: windows are
  tracked on their own thread, each anchor waits for its app on its own, `validate` gives up after 3 s with a
  reason, and the card says the app is not answering instead of waiting silently.

- `inspect(anchor | app)`: one look instead of `describe` + `screenshot` + `find_text`. The picture comes with
  numbered frames on the UI Automation elements (and, when UIA names next to nothing, on the text OCR reads), and
  each number carries an anchor that finds exactly that element. Look-alikes get `match: exact` and `index` counted
  the way the resolver counts them; window-handle automation ids, which change on every run, are never used.
  Everything that passes the visibility checks gets a number; not numbered, and counted instead: elements that draw
  nothing right now (buttons that appear on hover), wrappers around other controls, rows covered by a sticky header.
  Text gets a number only after the same read the anchor's text search will make (that panel's pixels, language
  and size) finds it there, once: inside the smallest named panel around it, so "808 Kick" in the browser and in the
  channel rack both get one. The overview reads the window at full size, in tiles (small UI text was lost when a
  big window was shrunk to fit one recognizer run).
- OCR: a line the Cyrillic recognizer reads with look-alikes only ("ТгасК" for "Track") goes to the script the
  unambiguous lines are in, instead of always to Cyrillic. Affects `find_text` without `text` and `inspect`.
- Paths through menus and dialogs: when a window of the app opens and the current step's target is still not
  found 1.5 s later, a `target_not_found` event brings that window's numbered contents (the `inspect` list with
  ready anchors); `retarget(target, anchor)` swaps the target's anchor while the tutorial runs. So a whole path can
  be written ahead from what the agent knows, and the agent only steps in when the app differs. Also when no window
  opens but the target is missing for 4 s where it is looked for (a tab of an open window, a list): the candidates
  then cover just that panel, with a note when it is a list the target may only be scrolled out of. Meanwhile the card
  says it is checking where the item is; `retarget` also takes the item's real name (`label`, `title`, `text`), so
  the card never keeps asking for an item that does not exist.
- A text anchor that names only the app (`{process, find: {text}}`) is also looked for in the app's open menus and
  dialogs, topmost first, and its mark belongs to the window it was found in. Before, such anchors searched only
  the main window, so a menu item written ahead was never found.
- No scroll hint ("scroll down") for a text looked for in a whole app: not seen there means a menu or dialog that is
  not open yet, not a list to scroll. Text inside an element or `within` keeps the hint.
- Window captures of apps that are not DPI-aware (old WinForms, some plugins) on a scaled monitor: the print is
  stretched like Windows stretches the window, so it lines up with UI Automation's rectangles. Before, such
  windows came out at 2/3 size (at 150 %), and text and templates were looked for in the wrong place.
- `find: {text: [...]}`: several wordings for one target (versions and translations name items differently); the
  first one found wins. A cut-down wording ("Snare" beside "808 Snare") counts only where it is most of its line, so
  with the item missing it does not catch "AP - Future Bass Snare Reverse"; wordings of their own still find their
  word in a menu bar or tab strip. Letter case never matters.
- `stop_tutorial` after the last step of a `keepOpen` tutorial finishes it with the end card (done, save, repeat)
  instead of hiding the card without a word; midway it still stops and hides (`outro` overrides either way).
- The mascot at the end card (and while the card waits for more steps) walks over to the card and points at it,
  instead of staying where the last target was, over the app's toolbar.
- Targets are framed at their own size by default (`shapes` left out, or `{type: "auto"}`): the element, the found
  text, or the visible thing itself — a template's icon or the caption at a spot, measured from the window's
  pixels (and re-measured while the lesson runs). Always a rectangle: no ring around a tall mixer name, no frame
  the size of a template's margin. `validate` warns when a hand-made ring or frame does not fit its target.
- A template that fits two places almost equally (a strip captured while selected, another strip selected now)
  counts as not found instead of marking the wrong one.
- A panel named "Title - detail" ("Mixer - Master", which becomes "Mixer - Insert 5" with the selection) is
  anchored by its stable part ("Mixer -").
- `anchor_at(app, at)`: the agent points at a spot on the inspect picture — anything without a number — and gets a
  checked anchor: the UIA element under it, its text, a template grown until it occurs once (or the n-th of
  identical look-alikes), else an offset marked fragile. No pixel measuring by the agent. On the local accuracy set
  it closes every remaining miss: a dim "Slot 1", vertical mixer names, the tempo number.
- `find: {template, index}`: the n-th good match of a template in reading order, as for text.
- `inspect` numbers icons that have no text and no UIA name (FL's Play / Stop / Record), each with a template anchor
  cut from the picture. Found from edges alone (own code, about 50 ms for a whole FL window); a mark that occurs
  more than once (step buttons, knobs) gets no number — told apart by shape, ink colour and size.
- Template anchors are looked for around where they were found a moment ago before the whole area: one search of
  a whole FL window takes ~150 ms, and a template target was searched like that on every pass.
- Text anchors looked for in a whole app window read it at full size in tiles, as `inspect` does: shrunk to fit one
  recognizer run, small UI text was lost (FL's "TOOLS" menu was never found).
- `inspect` numbers a caption even when a UIA panel of the same name lies around it ("Channel rack"): only an
  element about the caption's own size makes the caption redundant.
- `validate` reads each window and caption once per check: a grid of 68 cells on one caption took 15 s, now 0.35 s.
- The log records where a frame's time goes (engine, canvas, card, mascot) every 5 s while marks are shown.
- `describe` and other UIA walks fetch each node's children in one call: 2–2.5× faster on deep trees (VS Code,
  Chrome).
- The log file records how long each tool call took.

## [0.3.0] - 2026-11-03

Connect like any other MCP server: one command, no key, no port.

- `TutorialOverlay.exe --stdio`: MCP over stdin/stdout. It starts the overlay if it is not running (in the tray, on
  its own, so it outlives the agent), finds the port and the key itself and passes the messages through.
- "Copy the connect command" (tray menu, settings) now gives
  `claude mcp add --scope user tutorial-overlay -- "<path to exe>" --stdio`: for all projects, and it stays right
  after a port change.
- Every MCP tool has a title and annotations (read-only, destructive, local only), so clients know which calls
  change something.
- `--version`; the version is stamped on the exe.
- The exe is no longer compressed inside: about half the memory (the overlay 83 MB, each `--stdio` 44 MB).
- The HTTP endpoint with a key works as before.

## [0.2.0] - 2026-11-03

The first public release.

- Marks on top of any Windows app: rings, frames, arrows and captions, anchored to UI Automation elements, on-screen
  text (Windows OCR) or a small picture of the UI; they follow the window as it moves and scrolls.
- The step card with a checklist and Back, Skip, Done and Help buttons.
- Steps that complete by themselves: a click on the target, a pixel colour, a window that opened, a key combination,
  a drag, a mouse path, scrolling.
- The mascot "Shazhok", which walks up to the target and points at it.
- Help: "?" or circling a spot with the pencil sends the agent where the user is stuck.
- The tutorial library: saved tutorials run again from the tray menu, without the agent.
- A self-contained single-file exe: no .NET install needed to run it.

[Unreleased]: https://github.com/kizrum/tutorial_overlay/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.3.0
[0.2.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.2.0
