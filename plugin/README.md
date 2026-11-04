# TutorialOverlay

Step-by-step tutorials drawn right on top of any Windows app. Ask Claude "walk me through changing the font in
Notepad" or "show me how to add a channel in FL Studio": it reads the app's interface, highlights the right buttons
on screen, and a step card guides you. The overlay notices what you clicked and moves to the next step by itself;
press "?" on the card when you are stuck and Claude answers right there.

## Requirements

- Windows 10 or 11, x64.
- The TutorialOverlay program. Install it once:

  ```
  winget install Kizrum.TutorialOverlay
  ```

  Or download the zip from [Releases](https://github.com/kizrum/tutorial_overlay/releases/latest), unzip it and add
  the folder to `PATH` with the exe renamed or linked as `tutorial-overlay.exe`.

Then install the plugin in Claude Code:

```
/plugin install tutorial-overlay --marketplace kizrum/tutorial_overlay
```

The plugin starts the program for you when Claude needs it; it then stays in the tray until you quit it from the tray menu.
Update with `winget upgrade Kizrum.TutorialOverlay`.

## What the plugin contains

- An MCP server entry that runs `tutorial-overlay --stdio` (the program above). Its tools draw marks, run
  tutorials, read the interface of the app being taught and ask you questions; each tool is annotated as read-only
  or not.
- A skill, `tutorial-overlay`, that tells Claude how to build good tutorials with these tools.

Source code of the program: [github.com/kizrum/tutorial_overlay](https://github.com/kizrum/tutorial_overlay) (C#,
MIT license, no third-party packages).

## What it runs, reads and sends

- **Runs:** `tutorial-overlay.exe`, installed by you. If it is not running, `--stdio` starts it in the tray; it
  listens on `127.0.0.1` only, with a random key kept in `%LOCALAPPDATA%\TutorialOverlay\key.txt`.
- **Reads, when Claude calls a tool:** window titles and the UI Automation tree of the app being taught, text on
  screen (the OCR built into Windows), pixels of screen areas, your clicks on the targets of the current step, and
  — only while a step asks for them — key combinations, the mouse wheel and drags. Only the result goes out (for
  example "the target was clicked"), never a record of your input.
- **Sends:** nothing to the internet. Tool results — UI trees, recognised text, screenshots of the areas Claude
  asks for — go to Claude as part of your conversation, like any other tool result.
- **Stores on your computer:** settings, the key, saved tutorials, UI pictures used as anchors and help sketches in
  `%LOCALAPPDATA%\TutorialOverlay`, and a log there (`logs\overlay.log`, at most 4 files of 2 MB). Help sketches
  are kept for 14 days at most. Delete the folder to remove everything.

## Privacy Policy

TutorialOverlay collects no data about you and has no server of its own: nothing is sent to the author or to any
third party. What it reads from your screen is used only to answer the tool calls Claude makes in your
conversation, and those answers are handled by Claude under
[Anthropic's privacy policy](https://www.anthropic.com/legal/privacy). Files it keeps stay on your computer until
you delete them (see above). Questions: [open an issue](https://github.com/kizrum/tutorial_overlay/issues).

## License

[MIT](LICENSE)
