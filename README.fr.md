<img alt="TutorialOverlay — des leçons dessinées par-dessus n'importe quelle application Windows" src="docs/media/logo.webp" width="100%">

<a href="README.md">English</a> · <a href="README.ru.md">Русский</a> · <a href="README.es.md">Español</a> · <a href="README.de.md">Deutsch</a> · <b>Français</b> · <a href="README.pt-BR.md">Português</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a>

[![kizrum/tutorial_overlay MCP server](https://glama.ai/mcp/servers/kizrum/tutorial_overlay/badges/score.svg)](https://glama.ai/mcp/servers/kizrum/tutorial_overlay)

Des tutoriels pas à pas dessinés directement par-dessus n'importe quelle application Windows, guidés par un agent IA.

<img alt="Une leçon dans FL Studio : la mascotte montre les cases de l'étape, la carte coche chaque étape" src="docs/media/fl-lesson.webp" width="800" />

La leçon ci-dessus a été parcourue par le robot de test du projet dans FL Studio.

**Montre où. Explique pourquoi.** Les agents qui cliquent à votre place dans une application finissent le travail,
mais vous n'avez rien appris. TutorialOverlay vous laisse les clics : il montre chacun d'eux et explique pourquoi
c'est le bon, pour que la prochaine fois vous n'ayez plus besoin d'aide.

Tout fonctionne sur votre ordinateur. Le serveur n'écoute que sur `127.0.0.1` et n'envoie rien nulle part ; il
n'enregistre pas l'écran et ne fait une capture que lorsque l'agent la demande
([plus](docs/server.md#screenshots-and-privacy)).

## Pour commencer

[Installez TutorialOverlay](#installation), puis demandez à votre agent IA comment faire quelque chose dans une
application :

> [!TIP]
> *« Montre-moi pas à pas comment activer le retour automatique à la ligne dans le Bloc-notes »*

L'agent lit l'interface de l'application par MCP et écrit la leçon : les étapes, les cibles et, pour chacune, un
*pourquoi* :

```json
{
  "title": "Retour à la ligne dans le Bloc-notes",
  "app": "Notepad",
  "lang": "fr",
  "steps": [
    {
      "title": "Ouvrez le menu Affichage",
      "why": "Tout ce qui concerne l'affichage du texte se trouve dans le menu Affichage.",
      "targets": [{ "name": "Affichage", "anchor": { "element": { "name": "Affichage", "role": "MenuItem" } } }]
    },
    {
      "title": "Fermez le menu avec Échap",
      "until": [{ "type": "key", "keys": "Esc" }]
    }
  ]
}
```

L'overlay encadre chaque cible par-dessus l'application, la mascotte la montre du doigt et une carte d'étapes vous
guide. Les étapes se valident d'elles-mêmes : un clic, un pixel, une fenêtre, un raccourci, un glisser, un
défilement. Bloqué ? Cliquez sur **Aide** et l'agent répond directement dans la carte. À la fin, la leçon peut être
enregistrée et refaite depuis la zone de notification, sans l'agent.

<img width="420" alt="La carte d'étapes : Aide marque la cible et l'explique, puis Fais tout — une étiquette bleue AUTO en tête de carte, et les étapes se cochent toutes seules" src="docs/media/card-help.webp" />

Vous voulez d'abord le voir faire ? **Fais cette étape** (ou **Fais tout**) sur la carte confie la souris à
l'overlay : il déplace le vrai pointeur et clique sur ce qui est encadré pendant que vous regardez, et une étiquette
bleue **AUTO** en tête de carte indique qu'il travaille. Touchez la souris et vous reprenez la main aussitôt. Ce qui
ne peut pas être annulé (payer, commander, supprimer définitivement, envoyer), il ne le presse jamais : ce bouton
est rouge avec un cadenas, et c'est à vous de le presser.

La carte, la zone de notification et les paramètres parlent français, anglais, russe, espagnol, allemand, portugais,
chinois et japonais ; vous pouvez ajouter votre propre langue ([comment](docs/server.md)).

Le format complet d'une leçon est décrit dans [`docs/tutorials.md`](docs/tutorials.md) (en anglais).

## Installation

> [!NOTE]
> Windows 10 ou 11, x64. Rien d'autre : .NET est inclus dans l'exe.

TutorialOverlay est un serveur MCP local : tout agent qui parle MCP peut le piloter. Téléchargez
`TutorialOverlay-<version>-win-x64.zip` depuis les [Releases][releases], décompressez-le (par exemple dans
`C:\Tools\TutorialOverlay`) et ajoutez-le au `PATH` sous le nom `tutorial-overlay` :

```powershell
Copy-Item C:\Tools\TutorialOverlay\TutorialOverlay.exe C:\Tools\TutorialOverlay\tutorial-overlay.exe
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$p;C:\Tools\TutorialOverlay", 'User')
```

Puis, dans **Claude Code**, installez le plugin : le serveur et la skill qui apprend à l'agent à écrire de bonnes
leçons :

```text
/plugin marketplace add kizrum/tutorial_overlay
/plugin install tutorial-overlay@tutorial-overlay
```

C'est tout : ni clé ni port. L'agent lance l'overlay quand il en a besoin, et celui-ci reste dans la zone de
notification jusqu'à ce que vous le quittiez depuis son menu.

> [!TIP]
> Claude Code avec le plugin fonctionne le mieux. Les autres agents reçoivent les mêmes outils et lisent la même
> skill via l'outil `guide` (depuis la 0.7.0), mais sans le hook du plugin qui garde l'agent attentif pendant la
> leçon.

<details>
<summary>Codex</summary>

Ajoutez à `~/.codex/config.toml` (ou à `.codex/config.toml` dans un projet) :

```toml
[mcp_servers.tutorial-overlay]
command = "tutorial-overlay"
args = ["--stdio"]
```

</details>

<details>
<summary>Claude Desktop</summary>

Dans Claude Desktop, ouvrez **Settings → Developer → Edit Config** et ajoutez le serveur à
`claude_desktop_config.json` (gardez les autres entrées de `mcpServers`, s'il y en a) :

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

Utilisez le chemin complet de l'exe : Claude Desktop peut ne pas voir un `PATH` modifié pendant qu'il tournait.
Quittez complètement Claude Desktop (fermer la fenêtre peut le laisser dans la zone de notification) et rouvrez-le :
les outils apparaissent dans une nouvelle conversation. Ensuite, demandez simplement : *« Montre-moi comment activer
le retour automatique à la ligne dans le Bloc-notes. »*

</details>

<details>
<summary>Claude Code sans le plugin, autres clients MCP</summary>

Ajoutez un serveur MCP local (stdio) : la commande `tutorial-overlay` (ou le chemin complet de `TutorialOverlay.exe`)
avec l'argument `--stdio`. Dans Claude Code :

```powershell
claude mcp add --scope user tutorial-overlay -- tutorial-overlay --stdio
```

Ne l'ajoutez pas ainsi en plus du plugin : l'agent verrait chaque outil deux fois. Les clients qui parlent Streamable
HTTP peuvent aussi se connecter directement à l'overlay en marche : voir [`docs/server.md`](docs/server.md).

</details>

<details>
<summary>Mettre à jour</summary>

Quittez TutorialOverlay depuis le menu de la zone de notification, décompressez la nouvelle version par-dessus
l'ancien dossier et copiez de nouveau `TutorialOverlay.exe` en `tutorial-overlay.exe`. Les paramètres, la clé et les
tutoriels enregistrés sont dans `%LOCALAPPDATA%\TutorialOverlay` et restent en place ; les réglages de l'agent restent
valables eux aussi.

</details>

## Compiler depuis les sources

Il faut le [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0). À la racine du dépôt :

```powershell
# compiler : publish\TutorialOverlay.exe
dotnet publish -c Release -o publish

# lancer les tests
dotnet run --project tests

# utiliser votre build dans Claude Code
claude mcp add --scope user tutorial-overlay -- "$PWD\publish\TutorialOverlay.exe" --stdio
```

`.\release.ps1` construit le zip de la version pour vérification ; celui qui est publié est construit par
[GitHub Actions](docs/development.md#releases) sur l'étiquette de version. Le fonctionnement interne du programme est
décrit dans [`docs/development.md`](docs/development.md).

## Référence (en anglais)

- [Serveur](docs/server.md) : options de ligne de commande, l'icône de notification, les paramètres, `config.json`, la sécurité, le journal
- [Outils et événements MCP](docs/tools.md) : ce que l'agent peut appeler et ce qu'il reçoit
- [Tutoriels](docs/tutorials.md) : le format de la leçon : formes, ancres, étapes et conditions, la mascotte, l'aide
- [Développement](docs/development.md) : l'intérieur du programme, les tests, le robot de test

## Licence

[MIT](LICENSE)

***

<img src="docs/media/mascot-outro.webp" width="140" alt="Shazhok, la mascotte, qui danse" />

Shazhok vous salue. Comme tout le reste ici, il est dessiné en code : pas d'images, pas de polices, pas de paquets.

[releases]: https://github.com/kizrum/tutorial_overlay/releases/latest
