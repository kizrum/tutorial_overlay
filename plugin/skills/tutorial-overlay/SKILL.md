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
   reads there. Use the anchor of the number you want as is. Inspect a smaller element (or `within`) when the picture
   is too reduced to read. For what has no number, `describe(app)` gives the UIA tree, `find_text` searches a caption,
   and `pick(anchor)` lets the user click the spot — you get the exact offset.
2. **Write the whole path ahead.** Targets in menus and dialogs that are not open yet get anchors from what you
   know (`find: {text: 'General settings'}` or an element name); the overlay finds them when the window opens, so
   the lesson runs without waiting on you. If a window opens and the target is not in it, a `target_not_found` event
   brings that window's numbered contents with ready anchors: pick the right one and call `retarget` — or tell the
   user with `hint` if the path itself was wrong. Never guess the nearest item.
3. **One small action per step**, in the user's words: what to click and where it is.
4. **Teach, not just click.** Put the reason in each step's `why`: what the tool does, why this way, the usual
   mistake. Keep `say` (the mascot's line) under 60 characters.
5. **Say when a step is done.** No `until`: done when all targets are clicked. Otherwise pick the condition that
   matches the result: `appear` for a window that opens, `uia` for a checkbox or toggle state, `pixel` for a cell
   that lights up, `key` for a shortcut, `drag`, `scroll`, `trace`. Use `opens` on a target that must open a window.
6. **Check before starting:** `validate(spec)` resolves every anchor. Fix errors; warnings on later steps often
   mean a window an earlier step opens.
7. **Start and follow:** `start_tutorial(spec)`, then `events(since, timeoutMs)` to see hits, misses, `no_effect`
   and `help`. For a long lesson use `keepOpen: true` and `append_steps`.
8. **Help:** on a `help` event, answer with `hint(text)` right in the card; explain the cause, not only the click.
9. **Finish:** offer to save the tutorial (the end card does too), then call `release`.

For a single "where is X" question, `draw` one mark instead of a whole tutorial.

## Rules

- Coordinates are physical pixels. Prefer element anchors over screen points: marks then follow the window.
- One agent at a time: if a tool answers `busy`, call `wait_for_turn` — never start a second TutorialOverlay.
- Screenshots cost tokens: capture an element, not the whole screen.
- The full spec format (shapes, anchors, conditions, grids, the mascot) is in
  https://github.com/kizrum/tutorial_overlay/blob/main/docs/tutorials.md
