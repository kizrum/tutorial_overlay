# TutorialOverlay

**English** · [Русский](README.ru.md)

An on-screen overlay for step-by-step tutorials in any Windows app. An AI agent (Claude Code) drives it over MCP:
the agent reads the app's interface, highlights the right buttons right on top of it and walks the user through
the steps, while the overlay notices what the user did and moves on by itself.

A single exe in C# / .NET 8, written from scratch: no NuGet packages and no third-party assets. All graphics
(rings, arrows, icons, check marks) are drawn as vectors in code; text uses the Windows system font.

## What it does

- **Marks on top of any app** — rings, frames, arrows, captions. They are tied to UI elements (UI Automation), to
  text on screen (the OCR built into Windows) or to a small picture of the UI, and follow the window as it moves
  and scrolls. Clicks go straight through to the app.
- **A step card** — "Step 2 / 5", text, a checklist and Back, Skip, Done and Help buttons.
- **Steps complete by themselves**: a click on the target, a pixel colour (a cell switched on), a window that
  opened, a key combination, a drag, a mouse path, scrolling.
- **The mascot "Shazhok"** walks up to the target, points at it and says what to do.
- **Help**: the user presses "?" or circles a spot with a pencil — the agent learns where the user is stuck and
  answers right in the card.
- **A library**: a finished tutorial can be saved and run again from the tray icon's menu, without the agent.

The interface of the overlay itself is in Russian or English (`"lang"` in the settings).

## Requirements

- Windows 10 or 11, x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build. Running a built exe needs the .NET 8
  Desktop Runtime and the ASP.NET Core Runtime.
- [Claude Code](https://claude.com/claude-code) — the agent that writes and runs the tutorials.

## Build and run

```powershell
dotnet publish -c Release -o publish
publish\TutorialOverlay.exe
```

An icon appears in the tray: the server is listening on `http://127.0.0.1:7788/mcp`. The first run creates an
access key (`%LOCALAPPDATA%\TutorialOverlay\key.txt`). The server only listens on this computer.

## Connect Claude Code

Right-click the tray icon → **"Copy the command for the agent"** and run it in a terminal. It looks like this:

```
claude mcp add --transport http tutorial-overlay http://127.0.0.1:7788/mcp --header "Authorization: Bearer <key>"
```

This repository already has a `.mcp.json`; it takes the key from the `TUTORIAL_OVERLAY_KEY` environment variable.

## Try it

Open Claude Code and ask, for example:

- "Show me where word wrap is turned on in Notepad" — the agent finds the menu item and highlights it;
- "Walk me through changing the font in Notepad, step by step" — the step card, marks and the mascot appear; steps
  complete as you click;
- "Make an FL Studio tutorial: add a channel to the Channel rack and switch on some of its steps" — an example for
  an app that draws its own interface.

At the end the card offers to save the tutorial. Saved tutorials are in the tray icon's menu under **"Tutorials"**.

To stop the server: **"Quit TutorialOverlay"** in the tray icon's menu, or `TutorialOverlay.exe --stop`.

## Documentation

- [Server](docs/server.md) — command-line options, one agent at a time, the tray icon, the settings window,
  `config.json`, security, the log.
- [MCP tools and events](docs/tools.md) — what the agent can call and what it gets back.
- [Tutorials: the spec format](docs/tutorials.md) — shapes, anchors, steps and conditions, the mascot, the
  library, help.
- [Development](docs/development.md) — how the program is built inside, and the tests.
