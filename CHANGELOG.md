# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/): while the version
is 0.x, a minor bump (0.2 → 0.3) may change MCP tools or the tutorial spec format; a patch bump (0.2.0 → 0.2.1)
only fixes things.

## [Unreleased]

- `validate` notes a shape used for what it is not for: a `marker` on a control (it is for a phrase in text,
  anchored with `find.text`) and an arrow as a target's only mark (an arrow shows a direction or a path; the target
  keeps its outline). The skill and the docs say which mark suits which thing.
- Fixed: a mark on a found text or icon jumped from under the user's hand and back. Pointing changes what the
  app shows there — Blender's hovered shading sphere lights up and no longer looks like its picture, a field's
  tooltip — and the search found a neighbour. While the pointer rests on a mark of the anchor, the mark stays where
  it was. A template's thing is no bigger than the template and centred on the match (a hovered sphere's measure
  took its neighbours in).
- A spot counted from a found caption or icon (a place in a 3D view) is ringed, not framed by a small square.
- Autopilot: a step that waits for Done is looked at — the robot points at each target and reports a mark that
  moves under the hand; an arrow at a target is followed along its curve before the click; a twitch names the boxes.
- Fixed: a target anchored on a found caption with an offset that leads off it (a spot in Blender's 3D view counted
  from "User Perspective") framed the caption and took clicks there; a click on the spot itself was a miss. The
  spot is now the target, as with an offset into an element.
- Fixed: a curved arrow's head pointed past a small target — its tip was placed for a straight arrow. The tip now
  sits where the bow's last stretch points at the target's middle, and with no outline round the target it stops
  2 px from the element, not 6.
- Arrows for dense grids: `route: "elbow"` goes only across and down with a rounded corner, into the middle of the
  target's side.
- `band` marks a whole row (or `dir: "column"`, a column) across the visible part of its pane; `forbid` marks a
  place not to click — warning-coloured stripes, a dashed outline and a "don't click" tag; it is never waited for.
- Targets of a step that share `"group": "name"` are outlined together, with the name on the outline, instead of a
  frame on each.
- A target off every monitor gets a tab on the nearest screen edge pointing toward it, saying "off screen".
- `dotnet run --project tests -- --shapes out.png [filter]` draws the overlay's shapes over plain stand-ins for an
  app, through the real layout and renderer — to look at a shape without a live lesson.
- New target shapes that take the target at its own size, like `auto`: `brackets` (four corners — a field or a
  panel shows its bounds without being covered), `pill` (a capsule with a dot under it, for a segment among alike
  ones) and `marker` (a highlighter band over a phrase). `validate` no longer calls `auto` an unknown shape.
- Arrows can bow round what is in their way (`curve: 0.2`): the overlay picks the side that stays on screen and
  crosses fewer of the other marks, else the one away from the screen's centre; a caption placed at the tail follows
  the curve. `end` sets what the tip ends in — `arrow` (default), `dot`, `chevron`, `bar`, `ring`, `none` — and
  `taper: tail | both` thins the shaft toward the tail or at both ends. Unknown values are named by `validate`.
- Fixed: a text on screen that OCR misread ("Settinqs") got the "scroll here" wheel, sending the user scrolling
  away from it. A text not found where words that nearly spell it are read (a third of the letters off at most,
  the same digits) gets no wheel; a list of like items ("Track 2" for "Track 12") still does.
- Fixed: in a checklist step the mascot jerked from the target just done to the next one without walking (FL
  Studio's step grid). The next target of the same step was taken for the old one moved with its window; it is now
  a new spot to walk to, or just to turn the arm toward. The same for a target that is found a moment after its step
  begins.
- Tests check that the plugin and the program speak the same language: the skill calls only tools and arguments the
  server has and names only fields, events and answers the program uses, and the Stop hook reads only what
  lesson.json holds. A rename in the program that the plugin missed now fails `dotnet run --project tests`.
- A robot user for testing (`dotnet run --project tests -- --autopilot <cases>`): it plays lessons in real apps,
  clicking, pressing keys and scrolling like a person, and writes an HTML report with a screenshot of every step.
- Fixed: a target in a menu of a modern Windows app (Notepad, Paint, Terminal and other WinUI 3 apps) was reported
  as "covered by another window" and not marked. Such apps draw their menus in a popup window of their own, and that
  popup counted as something over the target; the app's own captionless popup that holds the target now does not.
- A submenu that opens on hover no longer waits for a click: the step that opens it (Blender's Add › Mesh) is done
  when the next `inMenu` step's items show up (`step_completed` with `how: "opened"`). They must have been away for
  a moment during the step first, so words already on screen never complete it.
- Fixed: the mark on a menu item found by its text jumped sideways now and then (Blender's "Mesh ▸", "Torus"), and
  a click on it could count as a miss. OCR reads the submenu arrow or the item's icon sometimes as a word of its own,
  sometimes glued to the caption; the mark now frames the caption's letters either way.
- Fixed: a menu item was reported as `target_not_found` in apps with a big, slow UI Automation tree (Steam): the
  "not found" came from a search of the main window that began before the menu opened. Such a verdict no longer
  counts against a window opened after it, and an element is now looked for in the newest window first.
- Fixed: in Explorer, the folder window was taken for a dialog over the desktop (the biggest window of the explorer
  process) on a step whose target was not shown yet: the card asked to close it, and the click on a menu item did
  not count. A window a target of the lesson has been shown in is never such a dialog.
- Marks no longer wait for slow UI Automation (Steam, apps drawn by Chromium, where one search takes seconds): after a
  second, an element named exactly by its caption is framed by that caption, found by OCR, until UIA answers
  (`status`: `via: "caption"`). `validate` marks such targets `slow` and suggests the caption anchor.
- A tutorial without `lang` gets its card (buttons, "Step 3 of 12", the hint line) in the language its steps are
  written in, not in the overlay's setting: English steps no longer sit between Russian buttons.

## [0.5.0] - 2026-11-08

- An answer is not lost when the user has moved on: `hint` with `expectIndex` set to the help event's `step` puts
  it into that step's conversation (a "can't find" / "don't get it" question becomes its first message), and the
  card says in its bottom line that the answer waits there — "‹ Back" brings the user to it. A hint nobody asked
  for, or for a question older than 15 minutes, is still refused.
- `validate` marks names that carry a number which changes as the user works (`Ln 36, Col 1`, `3 problems`,
  `12:04`) as `volatile`, with the steady part to anchor by instead; shortcut digits (`Ctrl+1`) do not count.
- Only one `--stdio` bridge starts the server: when several sessions find none at once, the others wait for it
  (a system-wide lock) instead of each launching its own.
- The mascot moves the way the design sheet shows:
  - appearing, it drops in stretched and lands with a squash and a puff of dust (it used to pop up with a spin);
  - walking, it leans in, steps with a push and a squash, and stops with a sway;
  - on a hit it takes turns between a jump with the arms up, a thumbs-up with a wink and a nod, and a glint runs
    along its progress bar;
  - on a miss its face shakes "no" while the body stays, then it points again, harder, and the target flashes;
  - while the agent prepares a hint it looks at the target through a loupe;
  - while the agent is away it dozes (heavy lids, slow breaths, floating "z"s);
  - on a step that waits for a shortcut it holds up a plate with one key cap per key (Ctrl, Shift, P) and presses
    them one after another; a key the user holds down lights up, and a line that only repeats the keys gets no
    bubble (the plate says it); on a drag
    or stroke it carries a glowing dot along a dashed path; on a scroll it turns a wheel with arrows running the
    way to scroll;
  - after 20 s with nothing happening it looks round and stretches, then points again;
  - at the end it dances under confetti;
  - pointing, it follows the cursor with its eyes when the cursor comes close.

  Nothing it draws leaves the box that is kept off the card.

- Fixed: typing a question into the card ("Help" → "Something else") was impossible — clicking the line hid the
  card for a moment (the overlay's own question line taking the keyboard counted as leaving the app), and the line
  closed with it. The card now stays while its line or the sketch toolbar has the keyboard.
- Fixed: the loupe never showed while the agent prepared an answer to "can't find it" (the card's own quick tip
  counted as the answer).
- Fixed: the card shook while "Why this way" grew upward — Windows showed each new size a frame before the new
  picture, so the buttons jumped up and back. The card is now a layered window: its place, size and picture change
  in one step, with corners rounded by the card itself.

- Fixed: the card shook when dragged against the screen's edge or the taskbar (the placement pulled it back while
  the hand pushed it out). While held, only the hand moves it, and the edge stops the drag itself; dropped, it
  keeps to the monitor it was dropped on instead of the app's.

- The mascot has a new look: a light rim and a soft top light so it stands out on dark apps, lids when it
  blinks, mitten hands and capsule feet, and a thin dark outline on its limbs so they read on light apps too.
- The mascot never puts a hand, foot or sparkle on the card: spots are checked against everything it draws, the
  pointing arm to the fingertip included. When the card is dragged or grows onto it, it fades at once and comes
  back where it is clear once the card rests; with no such spot it stays away.

- No console window: the exe is a window app now. Started from a shortcut or by the plugin it shows only the
  tray icon; started from a terminal it still writes into that terminal (`--version`, `--help`, the log), and the
  event lines still go to stdout when it is piped.

- Fixed: with no server running, the plugin's `--stdio` bridge started the winget-installed exe through the shell,
  and Windows asked "The publisher could not be verified" every time (the zip's internet mark), once per waiting
  session and again on every request. The launcher drops that mark from its own exe before starting the server.

- "Something else" is a conversation inside the step card instead of a separate window: the user's questions and the
  agent's `hint` answers as messages, three dots while the agent is looking, ask again in the same line. "‹ Step N"
  in the header (or Esc) goes back to the step, and the conversation folds into a row under its text with a dot for
  an unseen answer. A draft survives clicking away; the conversation belongs to its step.
- Marking the screen has a toolbar by the card: pencil, circle and frame (1/2/3, Shift for round/square), undo
  shape, Cancel (no picture) and Done with the shape count. Several shapes per picture, each numbered on screen and
  in the picture sent to the agent.

- The step card shows whether the assistant is there: its icon next to the close cross is green while the agent
  follows the lesson and grey, struck through, once its turn is over. What only the agent can do is dimmed then:
  "Something else" in Help and "Keep learning" on the end card; pressing them says, in the bottom line, to write to
  the assistant in the chat. Hovering the icon names the state. Known only from the agent's own calls (an `events`
  poll, or any tool call in the last 45 s): nothing is sent to the agent to find out.
- `events` long-polls up to 5 minutes (`timeoutMs` up to 300000, was 60000): it returns as soon as something happens.
  The skill and the plugin's Stop hook ask for 100 s (Claude Code moves a tool call longer than 120 s to the
  background): an agent following a quiet lesson wakes less often than with 60 s.

## [0.4.0] - 2026-11-07

- The plugin has a `Stop` hook: while a lesson its session started is running and the user acted in it in the last
  15 minutes, the agent is asked to keep polling `events` instead of ending its turn, so Help and Keep learning are
  answered. It reads the new `%LOCALAPPDATA%\TutorialOverlay\lesson.json` (state, title, step, the user's last
  action, whether an agent holds the lesson) and does nothing with a TutorialOverlay that does not write it.
- Help tells the truth about the assistant: when no agent follows the events, the card gives its own hint and says to
  write to the assistant in the chat instead of "the assistant will look too"; the `help` event carries `heard`.
- "Keep learning" pressed while no agent follows the events (its turn is over) no longer gives up after 30 s: the
  card says to write "continue" to the assistant in the chat and waits up to 10 minutes. `continue_requested` carries
  `heard: false` then, and the skill tells the agent to keep its events long-poll going for the whole lesson and to
  read `events` first when the user writes again.
- The step card has a new, quieter look: outlines instead of filled tiles; "Step 3 of 8" with a short progress strip
  next to the close cross instead of a row of its own; the step's text in full brightness; one accent. "Why" is folded
  to one line and unrolls on a click, staying open for the rest of the lesson. Back and Skip are small icons (their
  name and hotkey show in the bottom line on hover), Undo appears only when there is something to undo, Help and Done
  sit at the right. A problem that blocks the step is shown at the top.
  The assistant's question, "Something else" and the sketch bar use the same outline buttons.
- A card whose height changes keeps the edge nearest the screen edge: in a bottom corner it grows upward instead of
  running off the screen and jumping to a new place, and it no longer stays raised after shrinking back.
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

[Unreleased]: https://github.com/kizrum/tutorial_overlay/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.5.0
[0.4.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.4.0
[0.3.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.3.0
[0.2.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.2.0
