# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/): while the version
is 0.x, a minor bump (0.2 → 0.3) may change MCP tools or the tutorial spec format; a patch bump (0.2.0 → 0.2.1)
only fixes things.

## [Unreleased]

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
