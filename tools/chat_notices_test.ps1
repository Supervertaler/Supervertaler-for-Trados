# Notices in the chat (208). Supervertaler puts text of its own in the chat: the
# memory bank report (the Report button on the SuperMemory bar), "Switched to
# memory bank ...", "Prompt saved as ...". Two faults:
#   A  the Report button seemed to do nothing unless the Chat tab was open,
#      because its answer went into the chat and the pane stayed on its tab
#   B  every such notice was sent to the AI as something the AI itself had
#      said, and took one of the 10 places for earlier questions and answers
# Checked against the built plugin DLL, outside Studio, with the real
# AiAssistantControl, the real ChatPrompt.MessageWindow and the serialiser the
# chat history file is written with.
#
#   1  a message the user asked for (AddRequestedMessage) shows the Chat tab,
#      from every other tab, and scrolls to it; the keyboard focus, which the
#      click put on the button, stays inside the pane. A message nobody asked
#      for (AddMessage) leaves the tab and the keyboard focus alone
#   2  a notice is shown like an answer; a long one is cut with its full text
#      kept
#   3  what is sent: no notice, and the same messages as when the notices had
#      never been there; the history itself is not changed; 10,000 messages
#   4  the chat history file: a notice is still a notice after save and load; a
#      file from before 208 loads, and what it holds is sent as before
#   5  the source of AiAssistantViewPart (read as text, it cannot be run here):
#      the Report and Harvest buttons use the path that shows the Chat tab, the
#      automatic bank switch does not
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\chat_notices_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\chat_notices_test.ps1
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set), deleted
# at the end, also when a check throws.
#
# What it cannot reach: AiAssistantViewPart, which needs Studio. So not run
# here: the click on Report itself, OnSendRequested (that the window built here
# is what goes to the provider), and the view part's own SaveChatHistory and
# LoadChatHistory (the same serialiser and the same file path are used here).
# Section 5 reads the view part's source instead of running it.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Debug', 'Studio19\Release') } else { @('Studio18\Debug', 'Studio18\Release', 'Studio17\Debug', 'Studio17\Release') }
    foreach ($c in $want) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
$Dll = (Resolve-Path -LiteralPath $Dll).Path
$studio = if ($Dll -match '\\Studio19\\') { "C:\Program Files\Trados\Trados Studio\Studio19" }
          elseif ($Dll -match '\\Studio17\\') { "C:\Program Files (x86)\Trados\Trados Studio\Studio17" }
          else { "C:\Program Files (x86)\Trados\Trados Studio\Studio18" }
Write-Host "Plugin: $Dll ($([IntPtr]::Size * 8)-bit process)"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.Serialization
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class ChatNoticesTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[ChatNoticesTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# The real settings and key files: looked at before and after, never opened.
$real = @('D:\Supervertaler\trados\settings\settings.json', 'D:\Supervertaler\settings\api-keys.json',
          (Join-Path $env:APPDATA 'Supervertaler\api-keys.locked.json'), (Join-Path $env:APPDATA 'Supervertaler\config.json'))
function Snap { $real | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.LastWriteTimeUtc.Ticks)|$($i.Length)" } else { "$_|absent" } } }
$before = Snap

# Its own data folder, before anything of the plugin runs.
$temp = Join-Path $env:TEMP ("sv-chat-notices-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
function FindType([string]$name) {
    $t = $asm.GetType($name, $false)
    if ($t) { return $t }
    foreach ($a in [AppDomain]::CurrentDomain.GetAssemblies()) { $t = $a.GetType($name, $false); if ($t) { return $t } }
    $coreDll = Join-Path (Split-Path -Parent $Dll) 'Supervertaler.Core.dll'
    if (Test-Path $coreDll) { return [System.Reflection.Assembly]::LoadFrom($coreDll).GetType($name, $false) }
    return $null
}
$paths = FindType 'Supervertaler.Core.SupervertalerPaths'
if (-not $paths) { throw "SupervertalerPaths not found: the test will not run against the real data folder" }
$paths.GetMethod('Set').Invoke($null, @([string]$temp)) | Out-Null
Check ([string]::Equals([string]$paths.GetProperty('Root').GetValue($null, $null), $temp, [StringComparison]::OrdinalIgnoreCase)) "data folder is the temp folder"

$private = [System.Reflection.BindingFlags]'Instance,NonPublic'
$hosts = @()

try {
    $msgType = FindType 'Supervertaler.Core.Models.ChatMessage'
    $roleType = FindType 'Supervertaler.Core.Models.ChatRole'
    $promptType = $asm.GetType('Supervertaler.Trados.Core.ChatPrompt', $true)
    $chatType = $asm.GetType('Supervertaler.Trados.Controls.AiAssistantControl', $true)
    $bubbleType = $asm.GetType('Supervertaler.Trados.Controls.ChatBubble', $true)
    $listType = ([System.Collections.Generic.List`1]).MakeGenericType($msgType)
    $roleUser = [Enum]::Parse($roleType, 'User'); $roleAssistant = [Enum]::Parse($roleType, 'Assistant'); $roleSystem = [Enum]::Parse($roleType, 'System')
    $mNotice = $promptType.GetMethod('Notice'); $mIsNotice = $promptType.GetMethod('IsNotice')
    $mWindow = $promptType.GetMethod('MessageWindow'); $mTags = $promptType.GetMethod('WithSegmentTags')
    function Msg($role, [string]$text) { $m = [Activator]::CreateInstance($msgType); $m.Role = $role; $m.Content = $text; $m }
    function Notice([string]$text) { $mNotice.Invoke($null, @([string]$text)) }
    function IsNotice($m) { if ($null -ne $m) { $m = $m.PSObject.BaseObject }; [bool]$mIsNotice.Invoke($null, [object[]]@(, $m)) }
    function NewList { , [Activator]::CreateInstance($listType) }
    function Window($list, [int]$max) { , $mWindow.Invoke($null, @($list, $max)) }
    function Texts($list) { (@($list | ForEach-Object { $_.Content }) -join '|') }

    # ── 1 ────────────────────────────────────────────────────────────────
    Write-Host "1 A message the user asked for shows the Chat tab; one nobody asked for does not"
    $host1 = New-Object System.Windows.Forms.Form
    $hosts += $host1
    $host1.StartPosition = 'Manual'; $host1.Location = New-Object System.Drawing.Point(-32000, -32000)
    $host1.Size = New-Object System.Drawing.Size(700, 800)
    $elsewhere = New-Object System.Windows.Forms.TextBox      # stands for the Trados editor
    $elsewhere.Dock = 'Top'
    $chat = [Activator]::CreateInstance($chatType)
    $chat.Dock = 'Fill'
    $host1.Controls.Add($chat); $host1.Controls.Add($elsewhere)
    $host1.Show(); [System.Windows.Forms.Application]::DoEvents()
    $tabs = $chatType.GetField('_tabControl', $private).GetValue($chat)
    $flow = $chatType.GetField('_messageFlow', $private).GetValue($chat)
    $scrolledUp = $chatType.GetField('_userScrolledUp', $private)
    $others = @($tabs.TabPages | Where-Object { $_.Text -ne 'Chat' })
    Check ($tabs.SelectedTab.Text -eq 'Chat' -and $others.Count -ge 3) "the pane opens on Chat and has $($others.Count) other tabs ($(($others | ForEach-Object { $_.Text }) -join ', '))"

    $reportButton = $chat.SuperMemoryToolbar.GetType().GetField('_btnOverview', $private).GetValue($chat.SuperMemoryToolbar)
    $tabs.SelectedTab = $others[0]; [System.Windows.Forms.Application]::DoEvents()
    $host1.ActiveControl = $elsewhere
    $chat.AddMessage((Notice 'Switched to memory bank **example-bank** for **Example project**.'))
    [System.Windows.Forms.Application]::DoEvents()
    Check ($tabs.SelectedTab -eq $others[0]) "on '$($others[0].Text)', a notice nobody asked for: the pane stays on '$($tabs.SelectedTab.Text)'"
    Check ($flow.Controls.Count -eq 1) "and the notice is in the chat all the same ($($flow.Controls.Count) bubble)"
    Check ($host1.ActiveControl -eq $elsewhere) "and the keyboard focus is where it was, outside the pane"
    $chat.AddMessage((Msg $roleAssistant 'Error: example'))
    Check ($tabs.SelectedTab -eq $others[0] -and $flow.Controls.Count -eq 2) "an error nobody asked for: stays as well"

    $n = 2
    foreach ($page in $others) {
        $tabs.SelectedTab = $page; [System.Windows.Forms.Application]::DoEvents()
        $reportButton.Select()
        $scrolledUp.SetValue($chat, $true)
        $chat.AddRequestedMessage((Notice "**example-bank** - 3 files, about 1,200 tokens (asked for on $($page.Text))"))
        [System.Windows.Forms.Application]::DoEvents(); $n++
        Check ($tabs.SelectedTab.Text -eq 'Chat') "on '$($page.Text)', a message the user asked for: the pane shows '$($tabs.SelectedTab.Text)'"
        Check ($flow.Controls.Count -eq $n -and $flow.Controls[$n - 1].Visible) "the message is the last bubble and is visible ($($flow.Controls.Count) bubbles)"
        Check (-not [bool]$scrolledUp.GetValue($chat)) "the chat scrolls to it also when the user had scrolled up"
        Check ($host1.ActiveControl -eq $chat -and $host1.ActiveControl -ne $elsewhere) "the keyboard focus, on the Report button after the click, is still inside the pane"
    }
    $chat.AddRequestedMessage((Notice 'asked for while already on Chat')); $n++
    Check ($tabs.SelectedTab.Text -eq 'Chat' -and $flow.Controls.Count -eq $n) "asked for while Chat is showing: still Chat, message added"
    $chat.AddMessage((Notice 'automatic, while on Chat')); $n++
    Check ($tabs.SelectedTab.Text -eq 'Chat' -and $flow.Controls.Count -eq $n) "nobody asked, while Chat is showing: still Chat, message added"

    # ── 2 ────────────────────────────────────────────────────────────────
    Write-Host "2 How a notice looks"
    $isUser = $bubbleType.GetField('_isUser', $private)
    $noticeBubble = $flow.Controls[$n - 1]
    Check (-not [bool]$isUser.GetValue($noticeBubble)) "a notice is drawn as an answer, not as something the user typed"
    $long = (1..400 | ForEach-Object { "- line $_ of a long report" }) -join "`n"
    $tabs.SelectedTab = $others[0]; [System.Windows.Forms.Application]::DoEvents()
    $chat.AddRequestedMessage((Notice $long)); [System.Windows.Forms.Application]::DoEvents(); $n++
    $longBubble = $flow.Controls[$n - 1]
    Check ($long.Length -gt 3000 -and [string]$bubbleType.GetProperty('FullMarkdownContent', $private).GetValue($longBubble, $null) -eq $long) "a long report ($($long.Length) characters) asked for on another tab: cut for display, the full text kept for Copy"
    Check ($tabs.SelectedTab.Text -eq 'Chat' -and $longBubble.Visible -and $longBubble.Height -gt 0) "and showing, on the Chat tab ($($longBubble.Height) px high)"

    # ── 3 ────────────────────────────────────────────────────────────────
    Write-Host "3 What is sent to the AI"
    $n1 = Notice 'x'
    Check ((IsNotice $n1) -and $n1.Role -eq $roleSystem -and $n1.Content -eq 'x') "a notice has the role System and its text"
    Check (-not (IsNotice (Msg $roleUser 'q')) -and -not (IsNotice (Msg $roleAssistant 'a')) -and -not (IsNotice $null)) "a question, an answer and nothing are not notices"

    # 12 exchanges, then the question being sent; notices in between, also first and in runs.
    $plain = NewList; $mixed = NewList
    $mixed.Add((Notice 'notice at the very start'))
    for ($i = 1; $i -le 12; $i++) {
        $q = Msg $roleUser "q$i"; $a = Msg $roleAssistant "a$i"
        $plain.Add($q); $plain.Add($a)
        $mixed.Add($q)
        if ($i % 3 -eq 0) { $mixed.Add((Notice "notice between q$i and a$i")) }
        $mixed.Add($a)
        if ($i % 2 -eq 0) { $mixed.Add((Notice "notice after a$i")); $mixed.Add((Notice "second notice after a$i")) }
    }
    $last = Msg $roleUser 'q13'
    $plain.Add($last); $mixed.Add($last)
    $notices = @($mixed | Where-Object { IsNotice $_ }).Count
    $mixedCount = $mixed.Count
    $wPlain = Window $plain 10; $wMixed = Window $mixed 10
    Check ($notices -eq 17 -and $mixed.Count -eq 42 -and $plain.Count -eq 25) "the history: 25 questions and answers with $notices notices among them ($($mixed.Count) entries)"
    Check (@($wMixed | Where-Object { IsNotice $_ }).Count -eq 0) "what is sent holds no notice"
    Check ($wMixed.Count -ge 10 -and $wMixed.Count -le 13) "it holds between 10 and 13 messages ($($wMixed.Count))"
    Check ((Texts $wMixed) -eq (Texts $wPlain)) "and exactly those sent when the notices had never been there ($(Texts $wMixed))"
    Check ($wMixed[$wMixed.Count - 1] -eq $last) "ending with the question being asked"
    Check ($wMixed[0].Role -eq $roleUser) "starting with a question, as before"
    Check ($mixed.Count -eq $mixedCount -and @($mixed | Where-Object { IsNotice $_ }).Count -eq $notices) "the history itself still holds its notices ($($mixed.Count) entries)"
    $tagged = $mTags.Invoke($null, [object[]]@(, $wMixed))
    Check ($tagged.Count -eq $wMixed.Count -and @($tagged | Where-Object { IsNotice $_ }).Count -eq 0) "the same after the segment tags are added, the last step before sending"

    # Before 208 the same history sent notices as answers: that is what an old
    # window of these 42 entries would have held. Shown by counting, not by running old code.
    $oldStart = [Math]::Floor(($mixed.Count - 10) / 4) * 4
    $oldNotices = 0; for ($i = $oldStart; $i -lt $mixed.Count; $i++) { if (IsNotice $mixed[$i]) { $oldNotices++ } }
    Check ($oldNotices -gt 0) "for comparison: the last $($mixed.Count - $oldStart) entries, which is what was sent before, hold $oldNotices notices"

    $short = NewList
    $short.Add((Notice 'n')); $short.Add((Msg $roleUser 'only question'))
    $w = Window $short 10
    Check ($w.Count -eq 1 -and $w[0].Content -eq 'only question') "one notice and one question: the question alone is sent"
    $only = NewList; $only.Add((Notice 'n1')); $only.Add((Notice 'n2'))
    Check ((Window $only 10).Count -eq 0) "a history of notices only: nothing to send, no exception"
    Check ((Window (NewList) 10).Count -eq 0) "an empty history: nothing to send"
    $withNull = NewList; $withNull.Add($null); $withNull.Add((Msg $roleUser 'q'))
    Check ((Window $withNull 10).Count -eq 1) "an empty entry in the history is passed over, no exception"

    # The window keeps its start for two exchanges at a time (prompt caching);
    # a notice arriving in between must not move it.
    $grow = NewList
    for ($i = 1; $i -le 8; $i++) { $grow.Add((Msg $roleUser "q$i")); $grow.Add((Msg $roleAssistant "a$i")) }
    $grow.Add((Msg $roleUser 'q9'))
    $first1 = (Window $grow 10)[0].Content
    $grow.Add((Notice 'bank switched')); $grow.Add((Notice 'report'))
    $first2 = (Window $grow 10)[0].Content
    Check ($first1 -eq $first2) "two notices arriving do not move the start of what is sent ($first1)"

    # Scale: tested at 42, designed for a chat that is never cleared.
    $big = NewList
    for ($i = 1; $i -le 5000; $i++) { $big.Add((Msg $roleUser "q$i")); $big.Add((Notice "n$i")); $big.Add((Msg $roleAssistant "a$i")) }
    $big.Add((Msg $roleUser 'last'))
    $sw = [Diagnostics.Stopwatch]::StartNew(); $w = Window $big 10; $sw.Stop()
    Check ($w.Count -ge 10 -and $w.Count -le 13 -and @($w | Where-Object { IsNotice $_ }).Count -eq 0 -and $sw.ElapsedMilliseconds -lt 1000) "15,001 entries, 5,000 of them notices: $($w.Count) sent, none a notice, in $($sw.ElapsedMilliseconds) ms"

    # ── 4 ────────────────────────────────────────────────────────────────
    Write-Host "4 The chat history file"
    $userData = $asm.GetType('Supervertaler.Trados.Settings.UserDataPath', $true)
    $file = [string]$userData.GetProperty('ChatHistoryFilePath').GetValue($null, $null)
    Check ($file.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) "the chat history file is in the temp folder ($file)"
    if (-not $file.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to go on: the chat history file is $file" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
    $ser = New-Object System.Runtime.Serialization.Json.DataContractJsonSerializer($listType)
    function SaveList($list) { $fs = [IO.File]::Open($file, 'Create', 'Write'); try { $ser.WriteObject($fs, $list) } finally { $fs.Dispose() } }
    function LoadList { $fs = [IO.File]::Open($file, 'Open', 'Read'); try { , $ser.ReadObject($fs) } finally { $fs.Dispose() } }

    $h = NewList
    $h.Add((Msg $roleUser 'What does klep mean here?')); $h.Add((Msg $roleAssistant 'Valve.'))
    $h.Add((Notice 'Switched to memory bank **example-bank** for **Example project**.'))
    $h.Add((Msg $roleUser 'And in segment 2?'))
    SaveList $h
    $text = [IO.File]::ReadAllText($file)
    Check ($text -match '"role":2' -and $text -notmatch '(?i)notice"') "saved: the notice is in the file with role 2 (System), and no new field"
    $back = LoadList
    Check ($back.Count -eq 4 -and (IsNotice $back[2]) -and $back[2].Content -eq $h[2].Content) "loaded again: 4 entries, the notice is still a notice, with its text"
    Check (-not (IsNotice $back[0]) -and -not (IsNotice $back[1]) -and -not (IsNotice $back[3])) "and the questions and the answer are not"
    Check ((Texts (Window $back 10)) -eq 'What does klep mean here?|Valve.|And in segment 2?') "what is sent after a restart: the two questions and the answer"

    # A file as 207 wrote it: notices saved as answers (role 1), no askedWhile on old questions.
    $old = '[{"content":"What does klep mean here?","role":0,"timestamp":"\/Date(1762257600000+0000)\/"},' +
           '{"content":"Valve.","role":1,"timestamp":"\/Date(1762257601000+0000)\/"},' +
           '{"content":"Switched to memory bank **example-bank**.","role":1,"timestamp":"\/Date(1762257602000+0000)\/"},' +
           '{"askedWhile":"segment 2 of 9","content":"And in segment 2?","role":0,"timestamp":"\/Date(1762257603000+0000)\/"}]'
    [IO.File]::WriteAllText($file, $old, (New-Object System.Text.UTF8Encoding($false)))
    $threw = $false; $back = $null
    try { $back = LoadList } catch { $threw = $true }
    Check (-not $threw -and $back.Count -eq 4) "a file from before 208 loads ($($back.Count) entries)"
    Check (@($back | Where-Object { IsNotice $_ }).Count -eq 0 -and $back[2].Role -eq $roleAssistant) "its old notice is an answer, as it was saved: nothing in it is taken for a notice"
    Check ((Window $back 10).Count -eq 4 -and $back[3].AskedWhile -eq 'segment 2 of 9') "and all 4 are sent as before, the question with its segment"
    $back.Add((Notice 'a new notice')); SaveList $back; $again = LoadList
    Check ($again.Count -eq 5 -and (IsNotice $again[4]) -and @($again | Where-Object { IsNotice $_ }).Count -eq 1) "a notice added to the old file and saved: 5 entries, one notice"

    # ── 5 ────────────────────────────────────────────────────────────────
    Write-Host "5 The view part's source (read, not run)"
    $src = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\AiAssistantViewPart.cs'))
    function Body([string]$signature) {
        $i = $src.IndexOf($signature); if ($i -lt 0) { return $null }
        $j = $src.IndexOf("`r`n        }`r`n", $i); if ($j -lt 0) { return $null }
        $src.Substring($i, $j - $i)
    }
    foreach ($case in @(@('private void OnOverview(object sender, EventArgs e)', 'Report'),
                        @('private void OnHarvestTrackedChanges(object sender, EventArgs e)', 'Harvest changes'))) {
        $b = Body $case[0]
        Check ($null -ne $b -and $b -match 'ShowSuperMemoryAnswer\(' -and $b -notmatch 'ShowSuperMemoryMessage\(') "$($case[1]): every result goes through the path that shows the Chat tab"
        Check ($null -ne $b -and $b -match 'AddErrorMessage\([^;]*userAsked: true\);' ) "$($case[1]): its error does too"
    }
    $b = Body 'private void ApplyProjectMemoryBank()'
    Check ($null -ne $b -and $b -match 'ShowSuperMemoryMessage\(' -and $b -notmatch 'ShowSuperMemoryAnswer\(' -and $b -notmatch 'SwitchToChatTab' -and $b -notmatch 'AddRequestedMessage') "the bank switch on a change of project: a notice, and nothing in it changes the tab"
    $b = Body 'private void ShowSuperMemoryMessage(string text)'
    Check ($null -ne $b -and $b -match 'ChatPrompt\.Notice\(' -and $b -match '\.AddMessage\(' -and $b -notmatch 'AddRequestedMessage|SwitchToChatTab') "ShowSuperMemoryMessage: a notice, added without changing the tab"
    $b = Body 'private void ShowSuperMemoryAnswer(string text)'
    Check ($null -ne $b -and $b -match 'ChatPrompt\.Notice\(' -and $b -match 'AddRequestedMessage\(') "ShowSuperMemoryAnswer: a notice, added with the Chat tab shown"
    Check ($src -match 'messagesToSend = ChatPrompt\.MessageWindow\(_chatHistory, 10\);') "a chat turn still takes what it sends from ChatPrompt.MessageWindow"

    # Start-up order: a notice saves the chat, so the saved chat must be read
    # before anything can post one, or a start-up notice overwrites the file.
    $b = Body 'private void InitializeFullIfNeeded()'
    $load = if ($b) { $b.IndexOf('LoadChatHistory();') } else { -1 }
    # Calls only (a comment may name it): "ApplyProjectMemoryBank();" or "(ApplyProjectMemoryBank)".
    $m = if ($b) { [regex]::Match($b, 'ApplyProjectMemoryBank(\(\);|\))') } else { $null }
    $firstApply = if ($m -and $m.Success) { $m.Index } else { -1 }
    Check ($null -ne $b -and $load -ge 0 -and $firstApply -gt $load) "start-up reads the saved chat before anything can post a notice (load at $load, first bank apply at $firstApply)"
    Check (([regex]::Matches($src, 'LoadChatHistory\(\);')).Count -eq 1) "the saved chat is read in one place only"
}
finally {
    foreach ($h1 in $hosts) { try { $h1.Close(); $h1.Dispose() } catch { } }
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Check (((Snap) -join "`n") -eq ($before -join "`n")) "the real settings and API-key files are as they were"

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
