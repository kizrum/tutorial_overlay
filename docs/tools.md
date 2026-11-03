# MCP tools and events

[← README](../README.md) · [Server](server.md) · [MCP tools](tools.md) · [Tutorials](tutorials.md) · [Development](development.md)

## Tools

| Tool | What it does |
|---|---|
| `draw(shapes, anchor, dismissOnClick, …)` | marks outside a tutorial; a click dismisses the mark and still reaches the app |
| `clear(id?)` | remove marks |
| `start_tutorial(spec)` | start a tutorial (only the current step is shown) |
| `append_steps(steps)` | add steps (with `keepOpen` the tutorial waits for them after the last one) |
| `goto_step(index\|id\|relative)` | go to a step; its targets are shown again |
| `complete_step(expectIndex?)` | complete the step on the agent's behalf |
| `stop_tutorial()` | stop |
| `status()` | the state, the targets' rectangles and why each is or is not visible (`visible/occluded/minimized/inactive/…`) |
| `describe(app)` | the process's windows and its UI Automation tree with physical rectangles and `rel` offsets |
| `screenshot(anchor)` | a picture of a window or element **without** the overlay's drawings; `includeOverlay: true` takes them in |
| `events(since, timeoutMs)` | events with long-polling (the same as stdout and MCP notifications); `gap` means some events after `since` have already left the buffer (the last 1000) or did not fit in the answer (200) |
| `hint(text, title?, highlight?)` | the agent's answer in the step card (for example after a `help` event) |
| `pick(anchor, count, prompt)` | calibration: the user clicks a point, the agent gets its offset from the anchor, the colour, the UIA element and a ready `suggestedAnchor`; by default the click does not reach the app, Esc cancels |
| `find_text(text?, anchor, within, match, lang)` | find a caption on screen with the OCR built into Windows: rectangle, centre, `rel` and a `suggestedAnchor` with `find: {text}`; without `text`, all the text recognised in the area |
| `template(id, anchor, rect)` | remember a small piece of UI as a picture for `find: {template}` anchors |
| `validate(spec?)` | check a tutorial before it starts (or the current one): for each step, whether its target is visible now, and if not, why |
| `wait_for_turn(timeoutMs)` | wait until another agent frees the server (after a `busy` answer) and take it |
| `release()` | finish: the server is free for another agent, a running tutorial carries on |
| `ask(question, options, input)` | ask the user in a panel above the card and wait for the answer: a button (`{choice, label}`) or text (`{text}`) |
| `ask_area(prompt)` | ask the user to circle a spot on screen with the pencil and wait: bounds, strokes, the UIA element at the centre and a picture of the area |
| `list_tutorials`, `save_tutorial(name?, spec?)`, `delete_tutorial(name)` | the tutorial library (see [Tutorials](tutorials.md#end-of-a-tutorial-and-the-library)) |

## Events

`ready`, `tutorial_started/finished/stopped/waiting`, `steps_appended`, `step_started/completed/back/skipped`,
`hit`, `unhit`, `miss`, `undo`, `check_changed`, `uia_changed`, `key_matched`, `drag`, `opened`, `no_effect`,
`button`, `help`, `sequence_reset`, `pick_started`, `pick`, `pick_finished`, `ask`, `answer`, `ask_area`, `area`,
`window_opened/closed`, `anchor_found/lost`, `hook_reinstalled`, `exit`. `miss` carries `wrongButton` /
`expectedButton` when the click hit the target with the wrong mouse button.

`hit` and `miss` carry `rel`, the click's offset from the anchor point. From `miss` the agent can correct a
calibrated offset.

```json
{"event":"hit","seq":10,"ts":"19:31:46.689","mark":"s1t0","name":"Bold","x":1023,"y":246,"button":"left","rel":[26,30],"step":1,"done":1,"required":3}
```

## Long calls

`ask`, `ask_area`, `pick` and `events` with a wait can wait for the user for minutes. If the client accepts
`text/event-stream`, the answer to such a call is streamed: every 10 s comes `notifications/progress` (if the client
sent `_meta.progressToken`) or a ping comment, and at the end the result itself. So neither a client timeout nor an
idle connection cuts the wait short. The MCP protocol version is negotiated: the server supports 2025-06-18,
2025-03-26 and 2024-11-05.

## The agent's token use

Everything the server returns lands in the agent's context, so answers are lean:

- the tool descriptions are short (~3k tokens): only `draw` carries the full anchor and shape format, the others
  refer to it;
- results are compact JSON without empty fields (40–50% shorter than indented JSON);
- `screenshot` scales the long side down to 1024 px by default (a window shot ≈ 0.8k tokens instead of ≈ 2k). This
  does not shift positions: `find_text`, anchors, `pick`, `status` and `validate` work in full-size pixels. If the
  picture is scaled below 0.6, the answer warns about it (`precision`): do not measure by eye or read small text from
  it — use `find_text`/`pick`, a shot of one element through `anchor`, or `maxEdge: 0`;
- `find_text` returns at most 5 matches (`max`) and one ready anchor.
