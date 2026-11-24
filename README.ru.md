<img alt="TutorialOverlay — уроки поверх любой программы Windows" src="docs/media/logo.webp" width="100%">

<a href="README.md">English</a> · <b>Русский</b>

[![kizrum/tutorial_overlay MCP server](https://glama.ai/mcp/servers/kizrum/tutorial_overlay/badges/score.svg)](https://glama.ai/mcp/servers/kizrum/tutorial_overlay)

Пошаговые туториалы прямо поверх любой программы Windows — их ведёт ИИ-агент.

<img alt="Урок в FL Studio: маскот показывает на ячейки, карточка отмечает каждый шаг" src="docs/media/fl-lesson.webp" width="800" />

Урок выше прошёл в FL Studio тестовый робот проекта.

**Покажет, где. Объяснит, зачем.** Агенты, которые сами прощёлкивают программу за вас, делают дело, но вы так и не
узнаёте, как оно делается. TutorialOverlay оставляет клики вам: показывает каждый и объясняет, почему именно он, —
и в следующий раз помощь уже не нужна.

Всё работает на вашем компьютере. Сервер слушает только `127.0.0.1` и никуда ничего не отправляет; он не записывает
экран и делает снимок, только когда его попросит агент ([подробнее](docs/server.md#screenshots-and-privacy)).

## Как начать

[Установите TutorialOverlay](#установка) и спросите своего ИИ-агента, как что-то сделать в программе:

> [!TIP]
> *«Проведи меня по шагам: как в Блокноте включить перенос по словам»*

Агент через MCP читает интерфейс программы и пишет урок — шаги, цели и *почему* для каждого шага:

```json
{
  "title": "Перенос по словам в Блокноте",
  "app": "Notepad",
  "steps": [
    {
      "title": "Откройте меню «Вид»",
      "why": "Всё, что касается вида текста, Блокнот держит в меню «Вид».",
      "targets": [{ "name": "Вид", "anchor": { "element": { "name": "Вид", "role": "MenuItem" } } }]
    },
    {
      "title": "Закройте меню клавишей Esc",
      "until": [{ "type": "key", "keys": "Esc" }]
    }
  ]
}
```

Оверлей обводит каждую цель поверх программы, маскот показывает на неё, а карточка шага ведёт по уроку. Шаги
засчитываются сами — по клику, пикселю, окну, сочетанию клавиш, перетаскиванию, прокрутке. Застряли? Нажмите
**Help** — агент ответит прямо в карточке. В конце урок можно сохранить и запускать из трея, уже без агента.

<img width="420" alt="Карточка шага: «Help» показывает цель и объясняет, затем «Сделай всё» — в шапке синяя плашка AUTO, шаги отмечаются сами" src="docs/media/card-help.webp" />

Хотите сначала посмотреть, как это делается? Кнопка **«Сделай шаг»** (или **«Сделай всё»**) на карточке отдаёт
мышь оверлею: он сам ведёт настоящий курсор и нажимает то, что обведено, а вы смотрите. Пока он работает, в шапке
карточки горит синяя плашка **АВТО**. Стоит тронуть мышь — управление сразу снова у вас. То, что нельзя отменить
(оплата, заказ, удаление навсегда, отправка), он никогда не нажимает: такая кнопка красная, с замком, и нажимаете её
вы сами.

Полный формат урока — в [`docs/tutorials.md`](docs/tutorials.md).

## Установка

> [!NOTE]
> Windows 10 или 11, x64. Больше ничего не нужно: .NET уже внутри exe.

TutorialOverlay — локальный MCP-сервер, им может управлять любой агент с поддержкой MCP. Скачайте
`TutorialOverlay-<версия>-win-x64.zip` со страницы [Releases][releases], распакуйте (например, в
`C:\Tools\TutorialOverlay`) и добавьте в `PATH` под именем `tutorial-overlay`:

```powershell
Copy-Item C:\Tools\TutorialOverlay\TutorialOverlay.exe C:\Tools\TutorialOverlay\tutorial-overlay.exe
$p = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$p;C:\Tools\TutorialOverlay", 'User')
```

Затем в **Claude Code** установите плагин — сервер и скилл, который учит агента писать хорошие уроки:

```text
/plugin marketplace add kizrum/tutorial_overlay
/plugin install tutorial-overlay@tutorial-overlay
```

Это всё: ни ключа, ни порта. Агент сам запустит оверлей, когда он понадобится, и тот останется в трее, пока вы
не закроете его из меню значка.

> [!TIP]
> Лучше всего работает Claude Code с плагином. Другие агенты получают те же инструменты и читают тот же скилл через
> инструмент `guide` (с версии после 0.6.0), но без хука плагина, который не даёт агенту отвлечься, пока идёт урок.

<details>
<summary>Codex</summary>

Добавьте в `~/.codex/config.toml` (или в `.codex/config.toml` проекта):

```toml
[mcp_servers.tutorial-overlay]
command = "tutorial-overlay"
args = ["--stdio"]
```

</details>

<details>
<summary>Claude Desktop</summary>

В Claude Desktop откройте **Settings → Developer → Edit Config** и добавьте сервер в `claude_desktop_config.json`
(другие записи `mcpServers`, если они есть, оставьте):

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

Укажите полный путь к exe: Claude Desktop может не увидеть `PATH`, изменённый, пока он был запущен. Полностью
закройте Claude Desktop (после закрытия окна он может остаться в трее) и откройте снова — инструменты появятся в
новом чате. Дальше просто попросите: *«Покажи, как в Блокноте включить
перенос по словам»*.

</details>

<details>
<summary>Claude Code без плагина, другие MCP-клиенты</summary>

Добавьте локальный (stdio) MCP-сервер: команда `tutorial-overlay` (или полный путь к `TutorialOverlay.exe`) с
аргументом `--stdio`. В Claude Code:

```powershell
claude mcp add --scope user tutorial-overlay -- tutorial-overlay --stdio
```

Не подключайте так вместе с плагином: агент увидит каждый инструмент дважды. Клиенты со Streamable HTTP могут
подключиться и прямо к запущенному оверлею — см. [`docs/server.md`](docs/server.md).

</details>

<details>
<summary>Обновление</summary>

Закройте TutorialOverlay из меню значка в трее, распакуйте новую версию поверх старой папки и снова скопируйте
`TutorialOverlay.exe` в `tutorial-overlay.exe`. Настройки, ключ и сохранённые туториалы лежат в
`%LOCALAPPDATA%\TutorialOverlay` и не меняются; настройки агента тоже остаются верными.

</details>

## Сборка из исходников

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Из корня репозитория:

```powershell
# сборка: publish\TutorialOverlay.exe
dotnet publish -c Release -o publish

# тесты
dotnet run --project tests

# подключить свою сборку к Claude Code
claude mcp add --scope user tutorial-overlay -- "$PWD\publish\TutorialOverlay.exe" --stdio
```

`.\release.ps1` собирает zip для проверки; опубликованный собирает [GitHub Actions](docs/development.md#releases) по
тегу релиза. Устройство программы — в [`docs/development.md`](docs/development.md).

## Справочник (на английском)

- [Сервер](docs/server.md) — параметры запуска, значок в трее, настройки, `config.json`, безопасность, журнал
- [Инструменты MCP и события](docs/tools.md) — что может вызывать агент и что получает в ответ
- [Туториалы](docs/tutorials.md) — формат урока: фигуры, якоря, шаги и условия, маскот, помощь
- [Разработка](docs/development.md) — устройство программы, тесты, тестовый робот

## Лицензия

[MIT](LICENSE)

***

<img src="docs/media/mascot-outro.webp" width="140" alt="Маскот Шажок танцует" />

Шажок передаёт привет. Как и всё здесь, он нарисован кодом — без картинок, шрифтов и пакетов.

[releases]: https://github.com/kizrum/tutorial_overlay/releases/latest
