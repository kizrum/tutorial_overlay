<img alt="TutorialOverlay — Lektionen direkt über jeder Windows-App" src="docs/media/logo.webp" width="100%">

<a href="README.md">English</a> · <a href="README.ru.md">Русский</a> · <a href="README.es.md">Español</a> · <b>Deutsch</b> · <a href="README.zh-CN.md">简体中文</a>

[![kizrum/tutorial_overlay MCP server](https://glama.ai/mcp/servers/kizrum/tutorial_overlay/badges/score.svg)](https://glama.ai/mcp/servers/kizrum/tutorial_overlay)

Schritt-für-Schritt-Anleitungen direkt über jeder Windows-App – geführt von einem KI-Agenten.

<img alt="Eine Lektion in FL Studio: Das Maskottchen zeigt auf die Zellen des Schritts, die Karte hakt jeden Schritt ab" src="docs/media/fl-lesson.webp" width="800" />

Die Lektion oben hat der Testroboter des Projekts in FL Studio durchgespielt.

**Zeigt, wo. Erklärt, warum.** Agenten, die sich für Sie durch eine App klicken, erledigen die Aufgabe – und Sie
wissen danach nicht mehr als vorher. TutorialOverlay überlässt die Klicks Ihnen: Es zeigt auf jeden und erklärt,
warum er der richtige ist, damit Sie beim nächsten Mal keine Hilfe brauchen.

Alles läuft auf Ihrem Computer. Der Server hört nur auf `127.0.0.1` und sendet nichts irgendwohin; er nimmt den
Bildschirm nicht auf und macht nur dann einen Screenshot, wenn der Agent darum bittet
([mehr](docs/server.md#screenshots-and-privacy)).

## So geht es los

[Installieren Sie TutorialOverlay](#installation) und fragen Sie Ihren KI-Agenten, wie etwas in einer App geht:

> [!TIP]
> *„Zeig mir Schritt für Schritt, wie ich im Editor den Zeilenumbruch einschalte“*

Der Agent liest die Oberfläche der App über MCP und schreibt die Lektion – Schritte, Ziele und für jeden ein
*Warum*:

```json
{
  "title": "Zeilenumbruch im Editor",
  "app": "Notepad",
  "lang": "de",
  "steps": [
    {
      "title": "Öffnen Sie das Menü Ansicht",
      "why": "Alles, was betrifft, wie der Text aussieht, steht im Menü Ansicht.",
      "targets": [{ "name": "Ansicht", "anchor": { "element": { "name": "Ansicht", "role": "MenuItem" } } }]
    },
    {
      "title": "Schließen Sie das Menü mit Esc",
      "until": [{ "type": "key", "keys": "Esc" }]
    }
  ]
}
```

Das Overlay umrahmt jedes Ziel direkt über der App, das Maskottchen zeigt darauf, und eine Schrittkarte führt durch
die Lektion. Schritte erledigen sich von selbst – durch einen Klick, ein Pixel, ein Fenster, ein Tastenkürzel, ein
Ziehen, ein Scrollen. Kommen Sie nicht weiter? Klicken Sie auf **Hilfe**, und der Agent antwortet direkt in der
Karte. Am Ende lässt sich die Lektion speichern und über den Infobereich wiederholen, ganz ohne Agent.

<img width="420" alt="Die Schrittkarte: Hilfe markiert das Ziel und erklärt es, dann Alles ausführen – ein blaues AUTO-Schild führt die Kopfzeile an, und die Schritte haken sich selbst ab" src="docs/media/card-help.webp" />

Möchten Sie es erst einmal sehen? **Schritt ausführen** (oder **Alles ausführen**) auf der Karte gibt dem Overlay die
Maus: Es bewegt den echten Mauszeiger und klickt, was eingerahmt ist, während Sie zuschauen, und ein blaues
**AUTO** im Kopf der Karte zeigt, dass es arbeitet. Berühren Sie die Maus, und die Steuerung liegt sofort wieder bei
Ihnen. Was sich nicht rückgängig machen lässt – bezahlen, bestellen, endgültig löschen, senden –, drückt es nie:
Diese Schaltfläche ist rot mit einem Schloss, und drücken müssen Sie sie selbst.

Karte, Infobereich und Einstellungen sprechen Deutsch, Englisch, Russisch, Spanisch und Chinesisch; eine eigene
Sprache lässt sich hinzufügen ([wie](docs/server.md)).

Das vollständige Format einer Lektion steht in [`docs/tutorials.md`](docs/tutorials.md) (auf Englisch).

## Installation

> [!NOTE]
> Windows 10 oder 11, x64. Sonst nichts: .NET steckt in der exe.

TutorialOverlay ist ein lokaler MCP-Server, jeder Agent, der MCP spricht, kann ihn also steuern. Laden Sie
`TutorialOverlay-<Version>-win-x64.zip` unter [Releases][releases] herunter, entpacken Sie es (etwa nach
`C:\Tools\TutorialOverlay`) und legen Sie es als `tutorial-overlay` in den `PATH`:

```powershell
Copy-Item C:\Tools\TutorialOverlay\TutorialOverlay.exe C:\Tools\TutorialOverlay\tutorial-overlay.exe
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$p;C:\Tools\TutorialOverlay", 'User')
```

Installieren Sie dann in **Claude Code** das Plugin – den Server und den Skill, der dem Agenten beibringt, gute
Lektionen zu schreiben:

```text
/plugin marketplace add kizrum/tutorial_overlay
/plugin install tutorial-overlay@tutorial-overlay
```

Das ist alles: kein Schlüssel, kein Port. Der Agent startet das Overlay, wenn er es braucht, und es bleibt im
Infobereich, bis Sie es über dessen Menü beenden.

> [!TIP]
> Am besten funktioniert Claude Code mit dem Plugin. Andere Agenten bekommen dieselben Werkzeuge und lesen denselben
> Skill über das Werkzeug `guide` (ab 0.7.0), aber ohne den Hook des Plugins, der den Agenten während einer Lektion
> aufmerksam hält.

<details>
<summary>Codex</summary>

In `~/.codex/config.toml` (oder `.codex/config.toml` in einem Projekt) eintragen:

```toml
[mcp_servers.tutorial-overlay]
command = "tutorial-overlay"
args = ["--stdio"]
```

</details>

<details>
<summary>Claude Desktop</summary>

Öffnen Sie in Claude Desktop **Settings → Developer → Edit Config** und fügen Sie den Server in
`claude_desktop_config.json` hinzu (vorhandene Einträge in `mcpServers` bleiben):

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

Verwenden Sie den vollständigen Pfad zur exe: Claude Desktop sieht einen `PATH`, den Sie während der Laufzeit
geändert haben, unter Umständen nicht. Beenden Sie Claude Desktop vollständig (das Fenster zu schließen kann es im
Infobereich weiterlaufen lassen) und öffnen Sie es neu: Die Werkzeuge erscheinen in einem neuen Chat. Dann einfach
fragen: *„Zeig mir, wie ich im Editor den Zeilenumbruch einschalte.“*

</details>

<details>
<summary>Claude Code ohne Plugin, andere MCP-Clients</summary>

Fügen Sie einen lokalen MCP-Server (stdio) hinzu: den Befehl `tutorial-overlay` (oder den vollständigen Pfad zu
`TutorialOverlay.exe`) mit dem Argument `--stdio`. In Claude Code:

```powershell
claude mcp add --scope user tutorial-overlay -- tutorial-overlay --stdio
```

Nicht zusätzlich zum Plugin so hinzufügen: Der Agent sähe jedes Werkzeug doppelt. Clients, die Streamable HTTP
sprechen, können sich auch direkt mit dem laufenden Overlay verbinden – siehe [`docs/server.md`](docs/server.md).

</details>

<details>
<summary>Aktualisieren</summary>

Beenden Sie TutorialOverlay über das Menü im Infobereich, entpacken Sie die neue Version über den alten Ordner und
kopieren Sie `TutorialOverlay.exe` erneut als `tutorial-overlay.exe`. Einstellungen, Schlüssel und gespeicherte
Tutorials liegen in `%LOCALAPPDATA%\TutorialOverlay` und bleiben erhalten; auch die Einstellungen des Agenten
bleiben richtig.

</details>

## Aus dem Quellcode bauen

Benötigt wird das [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Im Stammordner des Repositorys:

```powershell
# bauen: publish\TutorialOverlay.exe
dotnet publish -c Release -o publish

# die Tests ausführen
dotnet run --project tests

# den eigenen Build in Claude Code verwenden
claude mcp add --scope user tutorial-overlay -- "$PWD\publish\TutorialOverlay.exe" --stdio
```

`.\release.ps1` baut das Release-Zip zur Kontrolle; das veröffentlichte baut
[GitHub Actions](docs/development.md#releases) beim Release-Tag. Wie das Programm innen aufgebaut ist, steht in
[`docs/development.md`](docs/development.md).

## Referenz (auf Englisch)

- [Server](docs/server.md) – Befehlszeilenoptionen, das Symbol im Infobereich, Einstellungen, `config.json`, Sicherheit, das Protokoll
- [MCP-Werkzeuge und -Ereignisse](docs/tools.md) – was der Agent aufrufen kann und was er zurückbekommt
- [Tutorials](docs/tutorials.md) – das Format der Lektion: Formen, Anker, Schritte und Bedingungen, das Maskottchen, die Hilfe
- [Entwicklung](docs/development.md) – das Innere des Programms, die Tests, der Testroboter

## Lizenz

[MIT](LICENSE)

***

<img src="docs/media/mascot-outro.webp" width="140" alt="Shazhok, das Maskottchen, tanzt" />

Shazhok sagt Hallo. Wie alles hier ist es in Code gezeichnet – keine Bilder, keine Schriften, keine Pakete.

[releases]: https://github.com/kizrum/tutorial_overlay/releases/latest
