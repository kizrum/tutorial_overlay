# TutorialOverlay Stop hook: keeps the agent that runs a lesson from ending its turn while the user is in it.
#
# The card's buttons (Help, Keep learning) reach the agent only through the `events` tool, and an agent whose turn is
# over polls nothing: the user would press a button and nobody would answer. So while a lesson this session started
# is running and the user is active in it, ending the turn is refused with a reminder to keep polling.
#
# It never blocks when in doubt: no lesson.json (an older TutorialOverlay), a server that is gone, a lesson that is
# not running, a lesson this session did not start (its transcript decides), or a user idle for 15 minutes.
# Windows PowerShell 5.1, no modules. Reads the hook input (JSON) on stdin.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)   # the lesson title may be Cyrillic
$IdleMinutes = 15

function Allow { exit 0 }

try {
    $raw = [Console]::In.ReadToEnd()
    $hookInput = if ($raw) { $raw | ConvertFrom-Json } else { $null }

    $lessonPath = Join-Path $env:LOCALAPPDATA 'TutorialOverlay\lesson.json'
    if (-not (Test-Path -LiteralPath $lessonPath)) { Allow }
    $lesson = Get-Content -LiteralPath $lessonPath -Raw -Encoding UTF8 | ConvertFrom-Json

    # a lesson.json left by a server that is gone says nothing
    if (-not $lesson.pid -or -not (Get-Process -Id ([int]$lesson.pid) -ErrorAction SilentlyContinue)) { Allow }
    if ($lesson.state -ne 'running' -and $lesson.state -ne 'waiting') { Allow }
    if ($lesson.agent -eq 'gone') { Allow }

    # the user walked away: polling on would only burn the agent's turns
    if ($lesson.lastUserAction) {
        $last = [DateTime]::Parse($lesson.lastUserAction, $null, [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
        if (([DateTime]::UtcNow - $last).TotalMinutes -gt $IdleMinutes) { Allow }
    }

    # only the session that runs the lesson: its own last TutorialOverlay call started or extended one
    $transcript = if ($hookInput) { $hookInput.transcript_path } else { $null }
    if (-not $transcript -or -not (Test-Path -LiteralPath $transcript)) { Allow }
    $pattern = '"name":"mcp__[^"]*tutorial-overlay__(start_tutorial|append_steps|stop_tutorial|release)"'
    $hits = Select-String -LiteralPath $transcript -Pattern $pattern -AllMatches
    if (-not $hits) { Allow }
    $lastCall = ($hits | Select-Object -Last 1).Matches | Select-Object -Last 1
    $tool = $lastCall.Groups[1].Value
    if ($tool -ne 'start_tutorial' -and $tool -ne 'append_steps') { Allow }

    $what = if ($lesson.state -eq 'waiting') { 'is waiting for your next steps' } else { "is on step $($lesson.step) of $($lesson.total)" }
    $reason = "TutorialOverlay: the lesson `"$($lesson.title)`" $what, and the user is in it. " +
        "The card's buttons (Help, Keep learning) reach you only through events: keep calling events(since, timeoutMs: 100000) " +
        "and react to hit / miss / help / continue_requested. The user's chat messages still reach you between calls. " +
        "End the turn after stop_tutorial (then release), or when the user asks you to stop."
    @{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress
    exit 0
}
catch {
    # a hook that fails must never trap the agent
    exit 0
}
