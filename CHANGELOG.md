# Changelog

All notable changes are listed here. Versions follow [Semantic Versioning](https://semver.org/): while the version
is 0.x, a minor bump (0.2 → 0.3) may change MCP tools or the tutorial spec format; a patch bump (0.2.0 → 0.2.1)
only fixes things.

## [Unreleased]

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

[Unreleased]: https://github.com/kizrum/tutorial_overlay/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/kizrum/tutorial_overlay/releases/tag/v0.2.0
