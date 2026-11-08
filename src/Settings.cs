using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TutorialOverlay;

/// <summary>A key chord such as Ctrl+Alt+Right.</summary>
internal readonly record struct Chord(uint Mods, uint Vk)
{
    public override string ToString() => Keys.Format(this);
}

/// <summary>Key names ⇄ virtual-key codes. Used for card hotkeys and for "key" step conditions.</summary>
internal static class Keys
{
    private static readonly Dictionary<string, uint> Named = BuildNames();

    private static Dictionary<string, uint> BuildNames()
    {
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["space"] = 0x20, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE, ["slash"] = 0xBF,
            ["numpad+"] = 0x6B, ["numpad-"] = 0x6D, ["numpad*"] = 0x6A, ["numpad/"] = 0x6F,
            ["capslock"] = 0x14, ["printscreen"] = 0x2C, ["pause"] = 0x13,
            ["→"] = 0x27, ["←"] = 0x25, ["↑"] = 0x26, ["↓"] = 0x28,
        };
        for (int i = 1; i <= 24; i++) d["f" + i] = (uint)(0x70 + i - 1);
        for (int i = 0; i <= 9; i++) d["numpad" + i] = (uint)(0x60 + i);
        return d;
    }

    private static readonly Dictionary<uint, string> Display = new()
    {
        [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓", [0x0D] = "Enter", [0x20] = "Space", [0x09] = "Tab",
        [0x1B] = "Esc", [0x08] = "Backspace", [0x2E] = "Del", [0x2D] = "Ins", [0x24] = "Home", [0x23] = "End",
        [0x21] = "PgUp", [0x22] = "PgDn", [0xBB] = "+", [0xBD] = "-",
    };

    public static Chord? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        uint mods = 0, vk = 0;
        // "Ctrl++" style: a trailing '+' is the key itself
        var parts = s.Trim().EndsWith("++") ? s.Trim()[..^2].Split('+').Append("plus").ToArray() : s.Split('+');
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win" or "meta" or "cmd": mods |= Native.MOD_WIN; break;
                default:
                    if (Named.TryGetValue(p, out var v)) vk = v;
                    else if (p.Length == 1 && char.IsLetterOrDigit(p[0])) vk = char.ToUpperInvariant(p[0]);
                    else return null;
                    break;
            }
        }
        return vk == 0 ? null : new Chord(mods, vk);
    }

    public static string Format(Chord c)
    {
        var mods = FormatMods(c.Mods);
        return mods.Length > 0 ? mods + "+" + KeyName(c.Vk) : KeyName(c.Vk);
    }

    public static string FormatMods(uint mods)
    {
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    public static string KeyName(uint vk)
    {
        if (Display.TryGetValue(vk, out var d)) return d;
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
        return $"0x{vk:X2}";
    }
}

/// <summary>User-visible strings of the card and helpers (ru / en).</summary>
internal sealed class Loc
{
    public string Step = "ШАГ", Waiting = "ОЖИДАНИЕ", Tutorial = "Туториал", Preparing = "Готовлю следующий шаг…";
    public string Back = "Назад", Skip = "Пропустить", Done = "Готово", Got = "Есть!", Undo = "Отменить", Help = "Помощь";
    public string Marked = "Отмечено {0} из {1}", Target = "Цель {0}";
    public string HintDone = "готово", HintBack = "назад", HintSkip = "пропустить", HintUndo = "отменить", HintStop = "завершить";
    public string PickTitle = "Укажите точку", PickText = "Кликните в нужное место. Esc — отмена.", PickCount = "Точка {0} из {1}";
    public string Review = "Повтор шага — нажмите «Готово», чтобы продолжить.";
    public string Why = "Почему так";
    public string HeadStep = "Шаг {0}", HeadOf = "из {0}", HeadWaiting = "Ожидание", HeadOutro = "Готово";
    public string ScrollUp = "Прокрутите вверх", ScrollDown = "Прокрутите вниз", ScrollLeft = "Прокрутите влево", ScrollRight = "Прокрутите вправо";
    public string ScrollIssue = "Нужное место за краем окна — {0}.", ScrollFind = "Прокрутите окно, чтобы показать нужное место.",
        ScrollHere = "Покрутите здесь — я найду «{0}»", ScrollHereAny = "Покрутите здесь — я найду нужное место",
        ScrollOver = "Крутите над «{0}» — там, где рамка", ScrollOverAny = "Крутите внутри рамки";
    public string IssueDialog = "Сначала закройте окно «{0}» — пока оно открыто, программа не принимает клики.";
    public string PrepareTitle = "Подготовимся к началу туториала", DialogTitle = "Сначала закроем окно",
        PrepareText = "В программе открыто окно «{0}». Ответьте на него или закройте — пока оно открыто, программа не принимает клики. Потом продолжим.";
    public string DialogMark = "Сначала ответьте на это окно или закройте его";
    public string CantFind = "Не вижу, куда жать", DontGet = "Не понимаю, что делать", Cancel = "Отмена", HintHelp = "помощь";
    public string Other = "Другое — опишу сам", OtherTitle = "Опишите, что не получается", Send = "Отправить",
        OtherHint = "Enter — отправить · Shift+Enter — новая строка · Esc — отмена";
    public string Sketch = "Обвести", Sketched = "Обведено: {0}", SketchTitle = "Обведите на экране, о чём речь",
        SketchFrozen = "Экран остановлен", SketchPen = "Карандаш", SketchCircle = "Круг", SketchRect = "Рамка",
        SketchUndo = "Отменить фигуру", SketchCancel = "Отмена", SketchCancelTip = "Не отправлять снимок",
        SketchDoneTip = "Готово: снимок уйдёт с вопросом", SketchMove = "Перетащите панель";
    public string ChatHello = "Что не получается на этом шаге? Напишите или обведите на экране.", ChatAgent = "Ассистент",
        ChatHint = "Enter — отправить · Esc — вернуться к шагу", ChatAsk = "Опишите, что не получается…", ChatReply = "Уточнить…",
        ChatRow = "Разговор по шагу", ChatLooking = "Ассистент смотрит…", ChatSketchOnly = "Обвёл на снимке",
        ChatShapes = "фигур на снимке: {0}", ChatBack = "Шаг {0}", ChatRemoveSketch = "Убрать снимок",
        ReplyElsewhere = "Ответ ассистента ждёт на шаге {0} — вернитесь туда кнопкой «Назад»";

    public string OutroHead = "ГОТОВО", OutroTitle = "Туториал пройден!", OutroText = "Вы прошли «{0}» — шагов: {1}. Что дальше?",
        OutroSave = "Сохранить туториал", OutroRepeat = "Пройти ещё раз", OutroContinue = "Продолжить обучение", OutroClose = "Закрыть", WaitFinish = "Завершить",
        OutroSavedTitle = "Сохранено", OutroSavedText = "Туториал в вашей библиотеке: его можно пройти снова из меню значка в трее (Туториалы).",
        TrayTutorials = "Туториалы", TrayTutorialsEmpty = "Пока нет сохранённых", TrayTutorialsFolder = "Открыть папку туториалов";
    public string HoldKeys = "Держите {0}";
    public string AskTitle = "Вопрос от ассистента", AskHint = "Enter — отправить · Shift+Enter — новая строка · Esc — закрыть";

    // tray icon
    public string TrayRunning = "Работает · 127.0.0.1:{0}", TrayAgentNever = "Агент ещё не обращался", TrayAgentAgo = "Агент обращался {0} назад",
        TrayTutorial = "Туториал «{0}» · шаг {1} из {2}", TrayNoTutorial = "Туториал не запущен",
        TraySettings = "Настройки…", TrayCopyKey = "Скопировать ключ", TrayCopyUrl = "Скопировать адрес MCP",
        TrayCopyCommand = "Скопировать команду для агента",
        AlreadyRunning = "TutorialOverlay уже запущен: 127.0.0.1:{0} (PID {1}).\n\nЕго значок — в области уведомлений, второй сервер не нужен.",
        TrayOpenFolder = "Открыть папку настроек", TrayStopTutorial = "Остановить туториал", TrayExit = "Закрыть TutorialOverlay",
        TrayExitConfirm = "Сейчас идёт туториал. Всё равно закрыть TutorialOverlay?", Sec = "с", Min = "мин", Hour = "ч";

    // settings window
    public string SetTitle = "Настройки TutorialOverlay", SetTabServer = "Сервер", SetTabLook = "Вид", SetTabKeys = "Горячие клавиши",
        SetPort = "Порт", SetPortArg = "сейчас задан параметром --port {0}",
        SetPortNote = "Новый порт начнёт работать после сохранения и перезапуска. Потом нажмите «Для агента» и отдайте команду агенту — он переподключится сам.",
        SetPortFree = "свободен ✓", SetPortBusy = "занят: {0}", SetPortBusyUnknown = "занят другой программой", SetPortHere = "на нём работает этот сервер",
        SetPortBad = "порт — число от 1024 до 65535", SetPortPick = "Подобрать", SetPortNone = "нет свободных портов {0}–{1}",
        SetForAgent = "Для агента", SetForAgentBtn = "Скопировать команду подключения", Copied = "Скопировано ✓",
        SetForAgentNote = "Команда claude mcp add для всех проектов. Порт и ключ агент найдёт сам, так что после смены порта команда остаётся верной. Выполните её в терминале (или вставьте в чат агенту).",
        RestartBusy = "Порт {0} уже занят ({1}). Сервер не перезапущен и продолжает работать на {2}.",
        SetUrl = "Адрес MCP", SetKey = "Ключ", SetKeySource = "Откуда взят: {0}", Show = "Показать", Hide = "Скрыть", Copy = "Копировать",
        SetLang = "Язык", SetAccent = "Цвет отметок", SetPickColor = "Выбрать…", SetReset = "Сбросить",
        SetAutoColor = "Подбирать цвет отметок под приложение", SetAnimate = "Анимация", SetMascot = "Маскот «Шажок» показывает, куда нажать",
        SetHideInactive = "Прятать отметки, когда приложение не на переднем плане",
        SetHideCapture = "Скрывать оверлей от скриншотов и записи экрана",
        SetKeysNote = "Щёлкните в поле и нажмите сочетание (с Ctrl, Alt или Win). Backspace или Delete — отключить.",
        KeyStop = "Завершить туториал", KeyNone = "нет", SetSave = "Сохранить", SetDefaults = "По умолчанию",
        SetRestartAsk = "Порт изменён на {0}. Перезапустить сервер сейчас?\n\nАгенту придётся подключиться заново по новому адресу.",
        SetAfterTutorial = "Сохранено. Цвет и язык применятся после текущего туториала.", SetSaveFailed = "Не удалось сохранить настройки: {0}";
    public string HelpTitle = "Подсказка", HelpVisible = "Нажмите «{0}» — он обведён рамкой, сейчас подсвечиваю.",
        HelpHidden = "Нужно нажать «{0}». {1}", HelpAgentToo = "Ассистент тоже посмотрит и подскажет подробнее.",
        HelpAgentIdle = "Ассистент сейчас не следит за уроком: чтобы он ответил, напишите ему в чат «помоги» — шаг и ваш вопрос он увидит.";
    public string ContinueUnheard = "Ассистент сейчас не следит за уроком. Напишите ему в чат «продолжай» — он увидит, что вы нажали эту кнопку, и подготовит следующий урок.";
    public string AgentOn = "Ассистент на связи: «Помощь» дойдёт до него сразу",
        AgentOff = "Ассистент не на связи — напишите ему в чат",
        AgentOffFor = "Ассистент не на связи {0} мин — напишите ему в чат",
        AgentOffContinue = "Чтобы продолжить, напишите ассистенту в чат «продолжай»";
    public string HelpSent = "Передал ассистенту — сейчас подскажу.", Highlighting = "Подсвечиваю, куда нажать…", Checking = "Уточняю, где этот пункт…";
    public string IssueMinimized = "Окно свёрнуто — разверните его.", IssueOccluded = "Нужное место закрыто другим окном.",
        IssueInactive = "Переключитесь в нужное приложение.", IssueMissing = "Не нахожу нужный элемент на экране.",
        IssueClosed = "Это было в меню или окне, которое сейчас закрыто, — откройте его снова.",
        IssueClosedIn = "Это в окне «{0}» — сначала откройте его.",
        IssueOpenPrevious = "Не вижу «{0}». Если это пункт меню или окна — сначала снова откройте «{1}».",
        ReopenTitle = "Меню закрылось", ReopenText = "«{0}» было в меню или окне, которое закрылось. Откройте его снова: нажмите «{1}».",
        IssueOffscreen = "Нужное место за пределами экрана.", IssueNotRunning = "Приложение не запущено.",
        IssueNotAnswering = "Приложение сейчас не отвечает — закройте открытое меню (Esc), я найду нужное место.",
        IssueElevated = "Программа запущена от имени администратора — запустите TutorialOverlay так же, иначе я не вижу ваших кликов в ней.";

    public static readonly Loc Ru = new();

    public static readonly Loc En = new()
    {
        Step = "STEP", Waiting = "WAITING", Tutorial = "Tutorial", Preparing = "Preparing the next step…",
        Back = "Back", Skip = "Skip", Done = "Done", Got = "Got it!", Undo = "Undo", Help = "Help",
        Marked = "{0} of {1} done", Target = "Target {0}",
        HintDone = "done", HintBack = "back", HintSkip = "skip", HintUndo = "undo", HintStop = "finish",
        PickTitle = "Pick a point", PickText = "Click the spot. Esc cancels.", PickCount = "Point {0} of {1}",
        Review = "Repeating this step — press Done to continue.",
        Why = "Why",
        HeadStep = "Step {0}", HeadOf = "of {0}", HeadWaiting = "Waiting", HeadOutro = "Done",
        ScrollUp = "Scroll up", ScrollDown = "Scroll down", ScrollLeft = "Scroll left", ScrollRight = "Scroll right",
        ScrollIssue = "The spot is outside the window — {0}.", ScrollFind = "Scroll the window to bring the spot into view.",
        ScrollHere = "Scroll here — I'll spot “{0}”", ScrollHereAny = "Scroll here — I'll spot the place",
        ScrollOver = "Scroll over “{0}” — inside the frame", ScrollOverAny = "Scroll inside the frame",

        IssueDialog = "Close the \"{0}\" window first — the app ignores clicks while it is open.",
        PrepareTitle = "Let's get ready to start", DialogTitle = "First, close this window",
        PrepareText = "The app has the \"{0}\" window open. Answer or close it — the app ignores clicks while it is open. Then we carry on.",
        DialogMark = "Answer or close this window first",
        CantFind = "I can't find where to click", DontGet = "I don't get what to do", Cancel = "Cancel", HintHelp = "help",
        Other = "Something else — I'll describe it", OtherTitle = "Describe what isn't working", Send = "Send",
        OtherHint = "Enter — send · Shift+Enter — new line · Esc — cancel",
        Sketch = "Circle it", Sketched = "Circled: {0}", SketchTitle = "Circle the part of the screen you mean",
        SketchFrozen = "Screen paused", SketchPen = "Pencil", SketchCircle = "Circle", SketchRect = "Frame",
        SketchUndo = "Undo shape", SketchCancel = "Cancel", SketchCancelTip = "Don't send a picture",
        SketchDoneTip = "Done: the picture goes with the question", SketchMove = "Drag the toolbar",
        ChatHello = "What isn't working on this step? Write, or mark it on the screen.", ChatAgent = "Assistant",
        ChatHint = "Enter — send · Esc — back to the step", ChatAsk = "Describe what isn't working…", ChatReply = "Ask more…",
        ChatRow = "About this step", ChatLooking = "The assistant is looking…", ChatSketchOnly = "Marked on the picture",
        ChatShapes = "shapes on the picture: {0}", ChatBack = "Step {0}", ChatRemoveSketch = "Remove the picture",
        ReplyElsewhere = "The assistant's answer waits on step {0} — go back there with Back",
        OutroHead = "DONE", OutroTitle = "Tutorial complete!", OutroText = "You finished \"{0}\" — {1} steps. What next?",
        OutroSave = "Save tutorial", OutroRepeat = "Go through again", OutroContinue = "Keep learning", OutroClose = "Close", WaitFinish = "Finish",
        OutroSavedTitle = "Saved", OutroSavedText = "It is in your library: replay it any time from the tray icon menu (Tutorials).",
        TrayTutorials = "Tutorials", TrayTutorialsEmpty = "Nothing saved yet", TrayTutorialsFolder = "Open tutorials folder",
        HoldKeys = "Hold {0}",
        AskTitle = "The assistant asks", AskHint = "Enter — send · Shift+Enter — new line · Esc — close",
        TrayRunning = "Running · 127.0.0.1:{0}", TrayAgentNever = "No requests from the agent yet", TrayAgentAgo = "Agent's last request {0} ago",
        TrayTutorial = "Tutorial \"{0}\" · step {1} of {2}", TrayNoTutorial = "No tutorial running",
        TraySettings = "Settings…", TrayCopyKey = "Copy key", TrayCopyUrl = "Copy MCP address",
        TrayCopyCommand = "Copy the command for the agent",
        AlreadyRunning = "TutorialOverlay is already running: 127.0.0.1:{0} (PID {1}).\n\nIts icon is in the notification area; a second server is not needed.",
        TrayOpenFolder = "Open settings folder", TrayStopTutorial = "Stop tutorial", TrayExit = "Quit TutorialOverlay",
        TrayExitConfirm = "A tutorial is running. Quit TutorialOverlay anyway?", Sec = "s", Min = "min", Hour = "h",
        SetTitle = "TutorialOverlay settings", SetTabServer = "Server", SetTabLook = "Look", SetTabKeys = "Hotkeys",
        SetPort = "Port", SetPortArg = "currently set by --port {0}",
        SetPortNote = "A new port takes effect after Save and a restart. Then press \"For the agent\" and give the command to the agent — it reconnects by itself.",
        SetPortFree = "free ✓", SetPortBusy = "in use: {0}", SetPortBusyUnknown = "in use by another program", SetPortHere = "this server runs on it",
        SetPortBad = "a port is a number 1024–65535", SetPortPick = "Pick a free one", SetPortNone = "no free ports {0}–{1}",
        SetForAgent = "For the agent", SetForAgentBtn = "Copy the connect command", Copied = "Copied ✓",
        SetForAgentNote = "A claude mcp add command for all projects. The agent finds the port and the key itself, so the command stays right after a port change. Run it in a terminal (or paste it to the agent in chat).",
        RestartBusy = "Port {0} is already in use ({1}). The server was not restarted and keeps running on {2}.",
        SetUrl = "MCP address", SetKey = "Key", SetKeySource = "Taken from: {0}", Show = "Show", Hide = "Hide", Copy = "Copy",
        SetLang = "Language", SetAccent = "Mark colour", SetPickColor = "Choose…", SetReset = "Reset",
        SetAutoColor = "Match mark colour to the app", SetAnimate = "Animation", SetMascot = "Mascot \"Shazhok\" shows where to click",
        SetHideInactive = "Hide marks while the app is not in front",
        SetHideCapture = "Hide the overlay from screenshots and screen recording",
        SetKeysNote = "Click a field and press a chord (with Ctrl, Alt or Win). Backspace or Delete turns it off.",
        KeyStop = "Stop tutorial", KeyNone = "none", SetSave = "Save", SetDefaults = "Defaults",
        SetRestartAsk = "The port is now {0}. Restart the server now?\n\nThe agent will have to reconnect to the new address.",
        SetAfterTutorial = "Saved. Colour and language apply after the current tutorial.", SetSaveFailed = "Could not save settings: {0}",
        HelpTitle = "Hint", HelpVisible = "Click «{0}» — it is framed, highlighting it now.",
        HelpHidden = "Click «{0}». {1}", HelpAgentToo = "The assistant will look too and say more.",
        HelpAgentIdle = "The assistant isn't following the lesson right now: to get its answer, write \"help\" to it in the chat — it will see the step and your question.",
        ContinueUnheard = "The assistant isn't following the lesson right now. Write \"continue\" to it in the chat: it will see you pressed this button and prepare the next lesson.",
        AgentOn = "The assistant is here: Help reaches it at once",
        AgentOff = "The assistant is away — write to it in the chat",
        AgentOffFor = "The assistant has been away {0} min — write to it in the chat",
        AgentOffContinue = "To keep learning, write \"continue\" to the assistant in the chat",
        HelpSent = "Told the assistant — a hint is coming.", Highlighting = "Highlighting where to click…", Checking = "Checking where this item is…",
        IssueMinimized = "The window is minimized — restore it.", IssueOccluded = "Another window covers the spot.",
        IssueInactive = "Switch to the app.", IssueMissing = "Can't find the element on screen.",
        IssueClosed = "It was in a menu or window that is closed now — open it again.",
        IssueClosedIn = "It is in the \"{0}\" window — open it first.",
        IssueOpenPrevious = "I can't see \"{0}\". If it is in a menu or window, open \"{1}\" again first.",
        ReopenTitle = "It closed", ReopenText = "\"{0}\" was in a menu or window that has closed. Open it again: click \"{1}\".",
        IssueOffscreen = "The spot is off screen.", IssueNotRunning = "The app is not running.",
        IssueNotAnswering = "The app is not answering right now — close the open menu (Esc) and I'll find the spot.",
        IssueElevated = "The app runs as administrator — start TutorialOverlay as administrator too, or I can't see your clicks in it.",
    };

    public static Loc For(string? lang) => (lang ?? "ru").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? En : Ru;

    /// <summary>
    /// The language a lesson is written in, from the words of its title and steps: "ru" when a good share of the
    /// letters is Cyrillic, "en" when they are Latin, null when there are too few to tell. A spec without "lang"
    /// gets its card in that language, so English steps do not sit between Russian buttons. A Russian lesson names
    /// Latin items ("Mesh", "File") and an English one may quote a Russian button, hence a share, not any letter.
    /// </summary>
    public static string? LangOf(JsonNode? spec)
    {
        int cyr = 0, lat = 0;
        void Count(string? s)
        {
            if (s == null) return;
            foreach (var ch in s)
                if (ch is >= 'А' and <= 'я' or 'Ё' or 'ё') cyr++;
                else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') lat++;
        }
        Count(J.Str(spec, "title"));
        foreach (var s in J.Items(J.Get(spec, "steps")))
            foreach (var k in new[] { "title", "say", "why", "text" }) Count(J.Str(s, k));
        if (cyr + lat < 12) return null;
        return cyr >= (cyr + lat) * 0.3 ? "ru" : "en";
    }
}

/// <summary>
/// Global defaults from %LOCALAPPDATA%\TutorialOverlay\config.json; a tutorial spec can override
/// lang / accent / hotkeys for its own run.
/// </summary>
internal sealed class Settings
{
    public string Lang = "ru";
    public Color Accent = Palette.DefaultAccent;
    public Dictionary<string, Chord> Hotkeys = DefaultHotkeys();
    public bool? Animate, HideWhenInactive;
    /// <summary>
    /// true: our windows are excluded from every capture (also the user's screenshots and recordings).
    /// false (default): visible to the user's tools; our own probes look through holes, screenshot() hides briefly.
    /// </summary>
    public bool HideFromCapture;
    /// <summary>Pick mark colours that contrast with the app around each target (explicit "color" still wins).</summary>
    public bool AutoColor = true;
    /// <summary>"Шажок", the mascot that points at targets during a tutorial.</summary>
    public bool Mascot = true;
    /// <summary>Server port when --port is not given (read at start-up).</summary>
    public int? Port;

    public static readonly string[] Actions = { "done", "back", "skip", "undo", "stop", "help" };

    public static Dictionary<string, Chord> DefaultHotkeys() => new()
    {
        ["done"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 0x27),
        ["back"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 0x25),
        ["skip"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 0x28),
        ["undo"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'Z'),
        ["stop"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'X'),
        ["help"] = new Chord(Native.MOD_CONTROL | Native.MOD_ALT, 'H'),
    };

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorialOverlay", "config.json");

    public static Settings Global { get; private set; } = new();

    /// <summary>What is in effect right now (global, or the running tutorial's overrides).</summary>
    public static volatile Settings Current = Global;

    public static void LoadGlobal()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var n = JsonNode.Parse(File.ReadAllText(FilePath));
            Global = new Settings().Apply(n);
            Current = Global;
            Log.Info($"settings loaded from {FilePath}");
        }
        catch (Exception e) { Log.Warn($"config.json ignored: {e.Message}"); }
    }

    public Settings Clone() => new()
    {
        Lang = Lang, Accent = Accent, Hotkeys = new Dictionary<string, Chord>(Hotkeys), Animate = Animate, HideWhenInactive = HideWhenInactive,
        HideFromCapture = HideFromCapture, AutoColor = AutoColor, Port = Port, Mascot = Mascot,
    };

    /// <summary>Overlay the fields present in <paramref name="n"/>.</summary>
    public Settings Apply(JsonNode? n)
    {
        if (n == null) return this;
        if ((J.Str(n, "lang") ?? J.Str(n, "language")) is string lang) Lang = lang;
        if (J.Str(n, "accent") is string acc) Accent = J.ParseColor(acc, Accent);
        if (J.Bool(n, "animate") is bool an) Animate = an;
        if (J.Bool(n, "hideWhenInactive") is bool hw) HideWhenInactive = hw;
        if (J.Bool(n, "hideFromCapture") is bool hc) HideFromCapture = hc;
        if (J.Bool(n, "autoColor") is bool ac) AutoColor = ac;
        if (J.Bool(n, "mascot") is bool ms) Mascot = ms;
        if (J.Int(n, "port") is int port && port is > 0 and < 65536) Port = port;
        if (J.Get(n, "hotkeys") is JsonObject hk)
            foreach (var a in Actions)
            {
                var v = J.Get(hk, a);
                if (v == null) continue;
                var s = J.AsStr(v);
                if (s == null || s.Length == 0 || s == "none" || s == "off") { Hotkeys.Remove(a); continue; }
                if (Keys.Parse(s) is Chord c) Hotkeys[a] = c;
                else Log.Warn($"hotkey for {a} not understood: \"{s}\"");
            }
        return this;
    }

    public Loc Text => Loc.For(Lang);

    /// <summary>Footer line of the card: "Ctrl+Alt+→ done · …".</summary>
    public string HintLine(bool waiting)
    {
        var t = Text;
        if (waiting) return Hotkeys.TryGetValue("stop", out var st) ? $"{st} — {t.HintStop}" : "";
        var order = new[] { ("done", t.HintDone), ("back", t.HintBack), ("skip", t.HintSkip), ("undo", t.HintUndo), ("help", t.HintHelp) };
        var used = order.Where(o => Hotkeys.ContainsKey(o.Item1)).ToList();
        if (used.Count == 0) return "";
        // shared modifiers are written once: "Ctrl+Alt: → done · ← back · …"
        uint mods = Hotkeys[used[0].Item1].Mods;
        if (used.All(o => Hotkeys[o.Item1].Mods == mods) && mods != 0)
        {
            return Keys.FormatMods(mods) + ":  " + string.Join(" · ", used.Select(o => $"{Keys.KeyName(Hotkeys[o.Item1].Vk)} {o.Item2}"));
        }
        return string.Join(" · ", used.Select(o => $"{Hotkeys[o.Item1]} {o.Item2}"));
    }

    public string HotkeySignature => string.Join(",", Hotkeys.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.Mods}:{k.Value.Vk}"));
}
