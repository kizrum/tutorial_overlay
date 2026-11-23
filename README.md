<img alt="TutorialOverlay — lessons drawn on top of any Windows app" src="docs/media/logo.webp" width="100%">

<b>English</b> · <a href="README.ru.md">Русский</a>

[![kizrum/tutorial_overlay MCP server](https://glama.ai/mcp/servers/kizrum/tutorial_overlay/badges/score.svg)](https://glama.ai/mcp/servers/kizrum/tutorial_overlay)

Step-by-step tutorials drawn right on top of any Windows app — led by an AI agent.

<img alt="A lesson in FL Studio: the mascot points at the step cells, the card ticks off each step" src="docs/media/fl-lesson.webp" width="800" />

The lesson above was played through by the project's test robot in FL Studio.

**Shows where. Explains why.** Agents that click through an app for you get the job done and leave you none the
wiser. TutorialOverlay leaves the clicks to you: it points at each one and says why it is the right one, so next
time you need no help.

Everything runs on your computer. The server listens on `127.0.0.1` only and sends nothing anywhere; it does not
record the screen and takes a screenshot only when the agent asks for one ([more](docs/server.md#screenshots-and-privacy)).

## Tutorial

To get started, [install TutorialOverlay](#installation) and ask your AI agent how to do something in an app:

> [!TIP]
> *“Walk me through turning on word wrap in Notepad, step by step”*

The agent reads the app's interface over MCP and writes the lesson — steps, targets and a *why* for each one:

```json
{
  "title": "Word wrap in Notepad",
  "app": "Notepad",
  "steps": [
    {
      "title": "Open the View menu",
      "why": "Notepad keeps everything about how text looks in the View menu.",
      "targets": [{ "name": "View", "anchor": { "element": { "name": "View", "role": "MenuItem" } } }]
    },
    {
      "title": "Close the menu with Esc",
      "until": [{ "type": "key", "keys": "Esc" }]
    }
  ]
}
```

The overlay rings each target on top of the app, the mascot points at it, and a step card leads the way. Steps
complete by themselves — a click, a pixel, a window, a shortcut, a drag, a scroll. Stuck? Press **Help** and the
agent answers right in the card. At the end the lesson can be saved and replayed from the tray, no agent needed.

<img width="420" alt="The step card: Why opens, Help offers choices, the agent's answer appears, the end card offers Save tutorial" src="docs/media/card-help.webp" />

For the full spec format see [`docs/tutorials.md`](docs/tutorials.md).

## Installation

> [!NOTE]
> Windows 10 or 11, x64. Nothing else: .NET is inside the exe.

TutorialOverlay is a local MCP server, so any agent that speaks MCP can drive it. Download
`TutorialOverlay-<version>-win-x64.zip` from [Releases][releases], unzip it (say to `C:\Tools\TutorialOverlay`) and
put it on `PATH` as `tutorial-overlay`:

```powershell
Copy-Item C:\Tools\TutorialOverlay\TutorialOverlay.exe C:\Tools\TutorialOverlay\tutorial-overlay.exe
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$p;C:\Tools\TutorialOverlay", 'User')
```

Then, in **Claude Code**, install the plugin — the server plus the skill that teaches the agent to write good
lessons:

```text
/plugin marketplace add kizrum/tutorial_overlay
/plugin install tutorial-overlay@tutorial-overlay
```

That's all: no key, no port. The agent starts the overlay when it needs it, and it stays in the tray until you
quit it from the tray menu.

> [!TIP]
> Claude Code with the plugin works best. Other agents get the same tools and read the same skill through the
> `guide` tool (from the version after 0.6.0), but without the plugin's hook that keeps the agent listening while a
> lesson runs.

<details>
<summary>Codex</summary>

Add to `~/.codex/config.toml` (or `.codex/config.toml` in a project):

```toml
[mcp_servers.tutorial-overlay]
command = "tutorial-overlay"
args = ["--stdio"]
```

</details>

<details>
<summary>Claude Desktop</summary>

In Claude Desktop open **Settings → Developer → Edit Config** and add the server to `claude_desktop_config.json`
(keep the other entries of `mcpServers` if you have some):

```json
{
  "mcpServers": {
    "tutorial-overlay": {
      "command": "C:\\Tools\\TutorialOverlay\\TutorialOverlay.exe",
      "args": ["--stdio"]
    }
  }
}
```

Use the full path to the exe: Claude Desktop may not see a `PATH` you changed while it was running. Quit Claude
Desktop completely (closing the window may leave it running in the tray) and open it again: the tools appear in a
new chat. Then just ask: *“Show me how to turn on word wrap in Notepad.”*

</details>

<details>
<summary>Claude Code without the plugin, other MCP clients</summary>

Add a local (stdio) MCP server: the command `tutorial-overlay` (or the full path to `TutorialOverlay.exe`) with the
argument `--stdio`. In Claude Code:

```powershell
claude mcp add --scope user tutorial-overlay -- tutorial-overlay --stdio
```

Do not add it this way next to the plugin: the agent would see every tool twice. Clients that speak Streamable HTTP
can also connect to the running overlay directly — see [`docs/server.md`](docs/server.md).

</details>

<details>
<summary>Updating</summary>

Quit TutorialOverlay from the tray menu, unzip the new version over the old folder and copy `TutorialOverlay.exe` to
`tutorial-overlay.exe` again. Settings, the key and saved tutorials live in `%LOCALAPPDATA%\TutorialOverlay` and
stay as they are; the agent's settings stay right too.

</details>

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). From the repository root:

```powershell
# build: publish\TutorialOverlay.exe
dotnet publish -c Release -o publish

# run the tests
dotnet run --project tests

# use your build in Claude Code
claude mcp add --scope user tutorial-overlay -- "$PWD\publish\TutorialOverlay.exe" --stdio
```

`.\release.ps1` builds the release zip as a check; the published one is built on a release tag by
[GitHub Actions](docs/development.md#releases). How the program is built inside is in
[`docs/development.md`](docs/development.md).

## Reference

- [Server](docs/server.md) — command-line options, the tray icon, settings, `config.json`, security, the log
- [MCP tools and events](docs/tools.md) — what the agent can call and what it gets back
- [Tutorials](docs/tutorials.md) — the spec format: shapes, anchors, steps and conditions, the mascot, help
- [Development](docs/development.md) — the inside of the program, the tests, the test robot

## License

[MIT](LICENSE)

***

<img src="docs/media/mascot-outro.webp" width="140" alt="Shazhok, the mascot, dancing" />

Shazhok says hi. Like everything else here, it is drawn in code — no images, no fonts, no packages.

[releases]: https://github.com/kizrum/tutorial_overlay/releases/latest
