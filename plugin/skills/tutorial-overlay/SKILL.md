---
name: tutorial-overlay
description: Teach the user a Windows app step by step with marks drawn on top of it (TutorialOverlay MCP tools). Use when the user asks how to do something in a desktop app, to be shown where a button or setting is, or to be walked through a task in it.
---

# Teaching with TutorialOverlay

The `tutorial-overlay` MCP tools draw on top of any Windows app: rings and frames on its buttons, a step card, a
mascot that points at the target. Steps complete by themselves when the user does them, so you react to events
instead of asking "done?".

## If the tools are missing or fail to start

The program is not installed. Tell the user to run `winget install Kizrum.TutorialOverlay` (Windows 10/11) and to
restart Claude Code. Do not try to install or download it yourself.

## How to build a tutorial

1. **Look first.** `inspect(app)` returns a picture with numbered frames and a ready anchor for every number —
   UIA elements, and for apps that draw their own interface (FL Studio, games, many audio and 3D tools) the text OCR
   reads there and icons without text (numbered by what you see — they have no name; their anchor is a template). Use the anchor of the number you want as is. Inspect a smaller element (or `within`) when the picture
   is too reduced to read. For what you see but has no number (a dim caption, a vertical name, a number in a box),
   call `anchor_at(app, at: [x, y])` with the spot on that picture — several spots in one call: it returns a checked
   anchor and says how sturdy it is (element / text / template, or a fragile offset). Do not measure pixels yourself.
   Without `anchor_at` (an older TutorialOverlay): `find_text` for a caption, or `pick(anchor)` to let the user click. If `inspect` is not among the tools
   (an older TutorialOverlay), start with `describe(app)` and `screenshot(anchor)` instead.
2. **Write the whole path ahead.** Targets in menus and dialogs that are not open yet get anchors from what you
   know (`find: {text: 'General settings'}` or an element name; where versions name it differently, list the full
   names: `text: ['General settings', 'General preferences']` — needs a TutorialOverlay with `inspect`, an older one
   takes only one text). When you know which window the text is in, say so with `find.in`: `"main"` for the app's
   own menu bar and panels, `{title: 'Settings'}` for a dialog (the start of its title) — the same words in another
   window ("Options" in a settings page, "Theme settings" in a menu) are then never taken for it. The overlay finds them when the window opens, so
   the lesson runs without waiting on you. If the target is not where it should be (a window opened without it, or
   an open panel or list does not show it after a few seconds), a `target_not_found` event
   brings that window's numbered contents with ready anchors: pick the right one and call `retarget` — or tell the
   user with `hint` if the path itself was wrong. Never guess the nearest item.
   **Start with the whole lesson, not two steps.** The user waits on every `append_steps`: adding one step at a
   time, after reading each menu, leaves them idle for most of the lesson. The app's own menus and commands you know
   (Blender's Add → Mesh → Monkey, Object → Shade Smooth; Steam's game menu → Properties…; an editor's File → Save
   As…) are written ahead like any target — `target_not_found` + `retarget` catches a wording that differs. Only what
   cannot be known ahead waits: the values of a drop-down, items that depend on the user's data (their files,
   games, presets), a dialog you have never seen. Write that step as "open the list", read it when it is open
   (`find_text`, `inspect`), and `append_steps` everything after it in one call. (Without `retarget`, an older
   TutorialOverlay: watch `anchor_found`/`no_effect` events, then `describe` the new window and `append_steps`.)
3. **Leave the outline to the overlay; pick a mark by what it must say.** Give a target no `shapes` (or
   `{type: 'auto'}`): it is framed at its own size — the element, the found text, the icon, the caption at a spot,
   measured live. Your own shapes add to the outline; `validate` warns when a hand-made ring or frame does not fit
   the target, a marker sits on a control, or an arrow is a target's only mark. Choose by the thing, not by looks:
   - a button, icon, menu item, tab, checkbox — `auto` (the default);
   - a field, a panel, a big area whose inside must stay readable — `{type: 'brackets'}` (corners only);
   - one segment or button in a row of alike ones (Day | Week | Month, a toolbar) — `{type: 'pill'}`;
   - a phrase *in text* (a line in a document, a help page, a dialog's message), anchored with `find.text` —
     `{type: 'marker'}`. Never on a control: a menu item or button under a see-through band reads as a smudge;
   - a whole row or column of a list or table — `{type: 'band'}`;
   - several targets of one step ticked as one (three checkboxes) — the same `group: 'name'` on each;
   - a dangerous control right next to the target — a mark of its own `{type: 'forbid'}` (never waited for).
   **Arrows are not for pointing at a control** — on their own they leave the user guessing which thing at the tip
   is meant. Use an arrow for a *direction or a path*: a drag (the `drag` condition draws source → arrow → drop
   zone), a move in a viewport (the `path` shape), which way to scroll or pan, or from the card toward a far area
   that is framed too. A drawn movement the user must follow is a `path` + `trace`. An arrow that would cut across
   other content can bow round it (`curve: 0.2`; the side is picked for you) and end in what the tip means:
   `end: 'dot'` an element, `'bar'` an edge, `'ring'` a spot; in a dense grid it goes only across and down:
   `route: 'elbow'`. (An older TutorialOverlay draws nothing for `brackets`, `pill`, `marker`, `forbid`, `band` —
   `validate` says "unknown shape": keep `auto` there — ignores `group`, draws every arrow straight with an
   arrowhead, and has no `auto`: frame elements and texts with `rect` and no size.) A caption on
   a bigger control (a tab, a drop-down with its arrow, a menu-bar item) is framed and clicked as that control
   (`validate` shows it as `visible`). (An older TutorialOverlay frames only the words: there, give a `rect` the
   size of the control, or a click on the arrow beside the text is a miss.)
4. **One small action per step**, in the user's words: what to click and where it is. Know each step's role as you
   write it: it opens a menu or window, it acts inside one, it is an action (keys, drag, scroll), or a setting in a
   window that stays. A step whose target is in a menu the previous step opens gets `inMenu: true`; a target in a
   window gets `find.in`, and the click that must open a window gets `opens`. Then a menu or window closed by a
   wrong click sends the lesson back to the step that opens it, by itself, with a card saying so — the user is never
   left on "click Render Image" with the menu gone. A submenu that opens on hover (Blender, Windows menus) needs no
   click either: the step that opens it is done once the next `inMenu` step's item shows up, so say "point at Mesh".
   (An older TutorialOverlay waits for the click there: say "click Mesh", which opens it too.)
5. **Teach, not just click.** Put the reason in each step's `why`: what the tool does, why this way, the usual
   mistake. Keep `say` (the mascot's line) under 60 characters. Write the steps in the user's language, and set the
   spec's `lang` ('ru' | 'en') to it, so the card's own buttons match the steps.
6. **Say when a step is done.** No `until`: done when all targets are clicked. Otherwise pick the condition that
   matches the result: `appear` for a window that opens, `uia` for a checkbox or toggle state, `pixel` for a cell
   that lights up, `key` for a shortcut, `drag`, `scroll`, `trace`. Use `opens` on a target that must open a window.
   To get the user to something below the fold, give the step that thing as its target and
   `{type: 'scroll', direction: 'down', until: 'visible'}`: it completes when the target shows up, however far the
   page needed — a fixed number of `notches` is a guess, and touchpads scroll in no notches at all.
7. **Check before starting:** `validate(spec)` resolves every anchor. Fix errors; warnings on later steps often
   mean a window an earlier step opens. Never start a step you have not validated, even a quick test one. A text
   target with `repeated` is on screen several times and the mark goes to the first: check which one the step means
   and pin it with `find.within` or `find.index` (or use the element / numbered anchor) — "Save" in the menu bar is
   not the "Save" button of a dialog.
   A target with `volatile` has numbers in its name that change ("Ln 36, Col 1", "3 unread", a time): anchor on the
   part that stays, or the mark is lost as soon as the number changes.
   A target with `slow` sits in a window whose UI Automation takes seconds (Steam, pages drawn by Chromium): anchor
   it by its caption as the note says (`find.text`, `match: 'exact'`), or its mark comes late on every visit. An
   older TutorialOverlay does not say `slow`: when `inspect` takes many seconds on a window, do the same there.
8. **Start and follow:** `start_tutorial(spec)`, then `events(since, timeoutMs)` to see hits, misses, `no_effect`
   and `help`. For a long lesson use `keepOpen: true` and `append_steps`. While the lesson runs, keep an `events`
   long-poll going all the time (`timeoutMs: 100000`: it returns at once when something happens, so a long one only
   saves you turns; an older server cuts it to 60 s, and Claude Code moves a call longer than 120 s to the background) and do nothing slow in between (no builds, no restarts, no long reading): the user
   is waiting on you. **Do not end your turn while the lesson runs** — the card's buttons ("Help", "Keep learning")
   reach you only through `events`, and the user's chat messages still arrive between your calls. If your turn did
   end, on the user's next message call `events` first: a `help` or `continue_requested` may be waiting
   (`heard: false`: the card showed you as away — its assistant icon grey, "Something else" and "Keep learning"
   dimmed — and told the user to write to you). A miss right beside the target that did what the step wanted (the list opened, the page
   changed): check it and `complete_step` instead of leaving the user stuck.
9. **Help:** on a `help` event, answer with `hint(text)` right in the card; explain the cause, not only the click.
   "Something else" (`reason: other`) is a conversation about the step: the user may ask again in it, and each
   `hint` becomes your next message there. If it has `sketch.image`, Read it: the shapes on it are numbered, refer
   to them by number ("№2 is the tab you need"). Pass the help event's `step` as `expectIndex`: if the user has moved
   on meanwhile, the answer waits in that step's conversation and the card tells them where (an older
   TutorialOverlay refuses it instead).
10. **Finish:** with `keepOpen`, when no more steps will come, call `stop_tutorial` after the last step: the user
   gets the end card (done, save, repeat) instead of a card that waits. Never stop a lesson without telling the user
   why. (An older TutorialOverlay only hides the card on `stop_tutorial`: say "that's all" with `hint` first.) Offer
   to save the tutorial (the end card does too), then call `release`.

11. **A lesson broke for the user: repair it, then save the repaired lesson.** Any lesson the user is going through —
   the one you wrote in this conversation or one from their library (replayed from the tray or with
   `start_tutorial({name})`) — can fail them: `target_not_found`, `no_effect`, misses on the same step, a `help`
   saying the step does not match the screen. Fix the lesson itself, not only this run, by how much of it is broken:
   - **one target moved or was renamed:** `retarget` it (the lesson itself changes);
   - **a step is wrong or missing** (the app asks for one more click, opens a dialog, wants another order): take the
     lesson's spec — your own, or the saved file (`path` in `list_tutorials`) — fix or add those steps, `validate`,
     and go on from the broken step with `start_tutorial({spec, startAt, replace: true})`, `startAt` being that step's
     index as events give it: what the user already did stays done. For a lesson from the library keep its library
     name in the spec (`name`) next to the fixed `steps`: it stays that lesson, now repaired. A step only missing at
     the end is `append_steps`;
   - **every step fails** (a redesigned app, another version or language, a wrong path from the start): `inspect` the
     app and write the lesson anew, as in steps 1–7, and start it in place of the old one.
   When the user has gone through the repaired steps, the repaired lesson is saved:
   - **written in this conversation:** `save_tutorial()` it yourself and tell the user in one line what changed and
     that it is saved;
   - **from the library** (`status` gives its `savedAs`; an older TutorialOverlay does not, then find its title in
     `list_tutorials`): saving replaces the old version, so the user decides. Finish with `stop_tutorial`: the end card
     says the lesson was repaired and offers Save, and Save first warns that the old lesson will be overwritten. An
     older TutorialOverlay's end card does not warn: there `ask` the user ("the old «…» will be overwritten — save the
     repaired one?") and `save_tutorial({name})` with its library name only on yes.
   Save only steps the user has gone through or `validate` resolved, never a guess. This is for lessons real users
   take; not for test runs (an autopilot, a developer checking the overlay), whose lessons stay as they were.

For a single "where is X" question, `draw` one mark instead of a whole tutorial.

## Rules

- Coordinates are physical pixels. Prefer element anchors over screen points: marks then follow the window.
- One agent at a time: if a tool answers `busy`, call `wait_for_turn` — never start a second TutorialOverlay.
- Screenshots cost tokens: capture an element, not the whole screen.
- The full spec format (shapes, anchors, conditions, grids, the mascot) is in
  https://github.com/kizrum/tutorial_overlay/blob/main/docs/tutorials.md
