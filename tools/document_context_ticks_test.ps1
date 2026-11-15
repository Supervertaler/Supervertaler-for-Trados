# "Include full document content in AI context": two tick boxes since 208, one
# on the Chat tab (Chat and QuickLauncher) and one on the Batch Operations tab
# (the batch features), where there was one in Settings > AI Settings for
# everything. Checked against the built plugin DLL, outside Studio, with the
# real controls and the real settings save/load code.
#
#   1  the box on the Batch Operations tab: there, labelled, under "Send TM
#      matches to the AI", in Translate and in Proofread mode; showing the
#      stored value is not a change, a change by the user is reported once;
#      locked during a run
#   2  the box on the Chat tab: there, labelled, above the input area, also in
#      a narrow pane; same rules for changes
#   3  the two settings: Chat follows the batch one until it has been set;
#      setting one never changes the other
#   4  the settings file: one written before 208 (only includeDocumentContext)
#      gives Chat the same value; after Chat is unticked the file holds both and
#      the batch one is as it was; the helper is not written to the file
#   5  Settings > AI Settings: the tick box is gone, Max segments stays and is
#      always enabled, and OK writes neither setting
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\document_context_ticks_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\document_context_ticks_test.ps1
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set), deleted
# at the end, also when a check throws. The settings file it saves and loads is
# the one in that folder.
#
# What it cannot reach: AiAssistantViewPart, which needs Studio - that a change
# of a box is saved, that the boxes are refreshed after the Settings window,
# and that Chat really sends the document or not (the view part reads
# AiSettings.ChatIncludesDocumentContext, which IS tested here).
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
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class DocumentTicksTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[DocumentTicksTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
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
$temp = Join-Path $env:TEMP ("sv-document-ticks-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$paths = $null
foreach ($a in [AppDomain]::CurrentDomain.GetAssemblies()) { $t = $a.GetType('Supervertaler.Core.SupervertalerPaths', $false); if ($t) { $paths = $t; break } }
if (-not $paths) { $paths = $asm.GetType('Supervertaler.Core.SupervertalerPaths', $false) }
if (-not $paths) {
    $coreDll = Join-Path (Split-Path -Parent $Dll) 'Supervertaler.Core.dll'
    if (Test-Path $coreDll) { $paths = [System.Reflection.Assembly]::LoadFrom($coreDll).GetType('Supervertaler.Core.SupervertalerPaths', $false) }
}
if (-not $paths) { throw "SupervertalerPaths not found: the test will not run against the real data folder" }
$paths.GetMethod('Set').Invoke($null, @([string]$temp)) | Out-Null
Check ([string]::Equals([string]$paths.GetProperty('Root').GetValue($null, $null), $temp, [StringComparison]::OrdinalIgnoreCase)) "data folder is the temp folder"

$private = [System.Reflection.BindingFlags]'Instance,NonPublic'
$label = 'Include full document content in AI context'
$hosts = @()
function AllControls($c) { foreach ($k in $c.Controls) { $k; AllControls $k } }

try {
    $settingsType = $asm.GetType('Supervertaler.Trados.Settings.TermLensSettings', $true)
    $aiType = $asm.GetType('Supervertaler.Trados.Settings.AiSettings', $true)
    $settingsFile = [string]$settingsType.GetProperty('SettingsFilePath').GetValue($null, $null)
    Check ($settingsFile.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) "the settings file is in the temp folder ($settingsFile)"
    if (-not $settingsFile.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to go on: the settings file is $settingsFile" }

    # ── 1 ────────────────────────────────────────────────────────────────
    Write-Host "1 The box on the Batch Operations tab"
    $batchType = $asm.GetType('Supervertaler.Trados.Controls.BatchTranslateControl', $true)
    $host1 = New-Object System.Windows.Forms.Form
    $hosts += $host1
    $host1.StartPosition = 'Manual'; $host1.Location = New-Object System.Drawing.Point(-32000, -32000)
    $host1.Size = New-Object System.Drawing.Size(1200, 900)
    $batch = [Activator]::CreateInstance($batchType)
    $batch.Dock = 'Fill'
    $host1.Controls.Add($batch)
    $host1.Show(); [System.Windows.Forms.Application]::DoEvents()

    $box = $batchType.GetField('_chkIncludeDocument', $private).GetValue($batch)
    $sendTm = $batchType.GetField('_chkSendTmMatches', $private).GetValue($batch)
    $targetOnly = $batchType.GetField('_chkTargetOnly', $private).GetValue($batch)
    $retry = $batchType.GetField('_chkRetry', $private).GetValue($batch)
    $rbProofread = $batchType.GetField('_rbProofread', $private).GetValue($batch)
    $rbTranslate = $batchType.GetField('_rbTranslate', $private).GetValue($batch)
    Check ($null -ne $box -and $box.Visible) "there is a tick box and it is visible"
    Check ($box.Text -eq $label) "it reads '$label' ($($box.Text))"
    Check ($box.Top -ge $sendTm.Bottom -and $box.Top - $sendTm.Bottom -le 12 -and $box.Left -eq $sendTm.Left) "directly under 'Send TM matches to the AI', aligned with it"
    Check ($box.Bottom -le $retry.Top) "and above 'Retry segments left empty', not on top of it"
    Check ($box.Right -le $batch.ClientSize.Width) "inside the tab ($($box.Right) of $($batch.ClientSize.Width))"
    $over = @($batch.Controls | Where-Object { $_ -ne $box -and $_.Visible -and $_.Bounds.IntersectsWith($box.Bounds) })
    Check ($over.Count -eq 0) "no other control of the tab overlaps it in Translate mode ($($over.Count))"
    $rbProofread.Checked = $true; [System.Windows.Forms.Application]::DoEvents()
    Check ($box.Visible -and -not $sendTm.Visible -and $targetOnly.Visible) "Proofread mode: still visible (Send TM matches is not)"
    $over = @($batch.Controls | Where-Object { $_ -ne $box -and $_.Visible -and $_.Bounds.IntersectsWith($box.Bounds) })
    Check ($over.Count -eq 0 -and $box.Top -ge $targetOnly.Bottom) "and under 'Translation only (no source)', overlapping nothing ($($over.Count))"
    $rbTranslate.Checked = $true; [System.Windows.Forms.Application]::DoEvents()

    $script:batchChanges = 0
    $batch.add_IncludeDocumentContextChanged({ $script:batchChanges++ })
    Check ($batch.IncludeDocumentContext) "before any setting is shown it is ticked, as the setting's default is"
    $batch.IncludeDocumentContext = $false
    Check (-not $batch.IncludeDocumentContext -and -not $box.Checked) "a stored 'off' is shown unticked"
    $batch.IncludeDocumentContext = $true
    $batch.IncludeDocumentContext = $true
    Check ($batch.IncludeDocumentContext -and $box.Checked) "a stored 'on' is shown ticked"
    Check ($script:batchChanges -eq 0) "showing the stored value is not reported as a change ($($script:batchChanges))"
    $box.Checked = $false
    Check ($script:batchChanges -eq 1 -and -not $batch.IncludeDocumentContext) "unticking it is reported once ($($script:batchChanges))"
    $box.Checked = $false
    Check ($script:batchChanges -eq 1) "setting the same value again reports nothing"
    $box.Checked = $true
    Check ($script:batchChanges -eq 2) "ticking it again is reported once more ($($script:batchChanges))"
    $batch.SetRunning($true)
    Check (-not $box.Enabled) "locked during a run"
    $batch.SetRunning($false)
    Check ($box.Enabled) "and free again afterwards"

    # ── 2 ────────────────────────────────────────────────────────────────
    Write-Host "2 The box on the Chat tab"
    $chatType = $asm.GetType('Supervertaler.Trados.Controls.AiAssistantControl', $true)
    $host2 = New-Object System.Windows.Forms.Form
    $hosts += $host2
    $host2.StartPosition = 'Manual'; $host2.Location = New-Object System.Drawing.Point(-32000, -32000)
    $host2.Size = New-Object System.Drawing.Size(700, 800)
    $chat = [Activator]::CreateInstance($chatType)
    $chat.Dock = 'Fill'
    $host2.Controls.Add($chat)
    $host2.Show(); [System.Windows.Forms.Application]::DoEvents()

    $chatBox = $chatType.GetField('_chkIncludeDocument', $private).GetValue($chat)
    $inputPanel = $chatType.GetField('_inputPanel', $private).GetValue($chat)
    $txtInput = $chatType.GetField('_txtInput', $private).GetValue($chat)
    $tabs = $chatType.GetField('_tabControl', $private).GetValue($chat)
    Check ($tabs.SelectedTab.Text -eq 'Chat') "the Chat tab is the one showing"
    Check ($null -ne $chatBox -and $chatBox.Visible) "there is a tick box and it is visible"
    Check ($chatBox.Text -eq $label) "with the same label ($($chatBox.Text))"
    $page = $tabs.SelectedTab
    function InPage($c) { $page.RectangleToClient($c.RectangleToScreen($c.ClientRectangle)) }
    $rBox = InPage $chatBox; $rInput = InPage $inputPanel; $rText = InPage $txtInput
    Check ($rBox.Bottom -le $rInput.Top -and $rInput.Top - $rBox.Bottom -le 4) "in a row of its own directly above the input area (box ends at $($rBox.Bottom), input starts at $($rInput.Top))"
    Check (-not $rBox.IntersectsWith($rText)) "not on top of the input box"
    Check ($rBox.Left -ge 0 -and $rBox.Right -le $page.ClientSize.Width -and $rBox.Height -ge 14) "inside the tab, tall enough to read ($($rBox.Height) px)"
    $inputHeight = $rText.Height
    $host2.Width = 260; [System.Windows.Forms.Application]::DoEvents()
    $rBox = InPage $chatBox; $rInput = InPage $inputPanel
    Check ($chatBox.Visible -and $rBox.Width -ge 150 -and $rBox.Right -le $page.ClientSize.Width -and $rBox.Bottom -le $rInput.Top) "a narrow pane (260 px): still there, inside the tab, above the input ($($rBox.Width) px wide)"
    Check ((InPage $txtInput).Height -eq $inputHeight) "and the input box is as tall as it was ($inputHeight px)"
    $host2.Width = 700; [System.Windows.Forms.Application]::DoEvents()

    $script:chatChanges = 0
    $chat.add_IncludeDocumentContextChanged({ $script:chatChanges++ })
    $chat.IncludeDocumentContext = $false
    $chat.IncludeDocumentContext = $true
    $chat.IncludeDocumentContext = $false
    Check (-not $chat.IncludeDocumentContext -and -not $chatBox.Checked -and $script:chatChanges -eq 0) "showing the stored value (off, on, off) is not reported as a change ($($script:chatChanges))"
    $chatBox.Checked = $true
    Check ($script:chatChanges -eq 1 -and $chat.IncludeDocumentContext) "ticking it is reported once ($($script:chatChanges))"
    $chatBox.Checked = $true
    Check ($script:chatChanges -eq 1) "setting the same value again reports nothing"
    Check ($script:batchChanges -eq 2 -and $batch.IncludeDocumentContext) "the Batch tab's box heard nothing of it"
    $inner = $chat.BatchTranslateControl
    $innerBox = $batchType.GetField('_chkIncludeDocument', $private).GetValue($inner)
    $chat.IncludeDocumentContext = $false; $inner.IncludeDocumentContext = $true
    Check (-not $chatBox.Checked -and $innerBox.Checked) "in one pane the two boxes hold different values"

    # ── 3 ────────────────────────────────────────────────────────────────
    Write-Host "3 The two settings"
    function Ai { [Activator]::CreateInstance($aiType) }
    $ai = Ai
    Check ($ai.IncludeDocumentContext -and $null -eq $ai.IncludeDocumentContextInChat -and $ai.ChatIncludesDocumentContext) "new settings: batch on, Chat never set and so on"
    $ai.IncludeDocumentContext = $false
    Check ($null -eq $ai.IncludeDocumentContextInChat -and -not $ai.ChatIncludesDocumentContext) "the one setting from before 208 off: Chat is off too"
    $ai.IncludeDocumentContext = $true
    Check ($ai.ChatIncludesDocumentContext) "and on: Chat is on too"

    $ai = Ai
    $ai.SetChatDocumentContext($false)
    Check ($ai.IncludeDocumentContext -and $ai.IncludeDocumentContextInChat -eq $false -and -not $ai.ChatIncludesDocumentContext) "Chat unticked: Chat off, batch still on"
    $ai.SetBatchDocumentContext($false)
    $ai.SetBatchDocumentContext($true)
    Check ($ai.IncludeDocumentContext -and -not $ai.ChatIncludesDocumentContext) "batch unticked and ticked again: Chat stays off"
    $ai.SetChatDocumentContext($true)
    Check ($ai.IncludeDocumentContext -and $ai.ChatIncludesDocumentContext) "Chat ticked again: on, batch untouched"

    $ai = Ai
    $ai.SetBatchDocumentContext($false)
    Check (-not $ai.IncludeDocumentContext -and $ai.ChatIncludesDocumentContext) "batch unticked while Chat was never set: batch off, Chat keeps the 'on' it had"
    $ai = Ai; $ai.IncludeDocumentContext = $false
    $ai.SetBatchDocumentContext($true)
    Check ($ai.IncludeDocumentContext -and -not $ai.ChatIncludesDocumentContext) "batch ticked while Chat was never set: batch on, Chat keeps the 'off' it had"
    $ai = Ai
    $ai.SetBatchDocumentContext($true)
    Check ($null -eq $ai.IncludeDocumentContextInChat) "a batch 'change' to the value it already has leaves Chat unset"

    # ── 4 ────────────────────────────────────────────────────────────────
    Write-Host "4 The settings file"
    $tryLoad = $settingsType.GetMethod('TryLoad', [System.Reflection.BindingFlags]'Static,NonPublic')
    function Load { $a = New-Object 'object[]' 1; $s = $tryLoad.Invoke($null, $a); if ($null -eq $s) { throw "settings could not be loaded: $($a[0])" }; $s }
    function FileText { [IO.File]::ReadAllText($settingsFile) }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsFile) | Out-Null

    foreach ($stored in $true, $false) {
        $json = '{"aiSettings":{"includeDocumentContext":' + $stored.ToString().ToLowerInvariant() + ',"documentContextMaxSegments":300,"batchSize":50}}'
        [IO.File]::WriteAllText($settingsFile, $json, (New-Object System.Text.UTF8Encoding($true)))
        $s = Load
        Check ($s.AiSettings.IncludeDocumentContext -eq $stored -and $null -eq $s.AiSettings.IncludeDocumentContextInChat -and $s.AiSettings.ChatIncludesDocumentContext -eq $stored) "a file from before 208 with the setting $(if ($stored) { 'on' } else { 'off' }): batch and Chat both $(if ($stored) { 'on' } else { 'off' })"
        Check ($s.AiSettings.DocumentContextMaxSegments -eq 300 -and $s.AiSettings.BatchSize -eq 50) "and its other settings are read as before"
        Check ([bool]$s.Save()) "saved again without a change"
        $text = FileText
        Check ($text -match '"includeDocumentContext":' + $stored.ToString().ToLowerInvariant() -and $text -notmatch 'includeDocumentContextInChat' -and $text -notmatch '(?i)chatIncludesDocumentContext') "the file still holds the one setting: nothing is written for Chat while it is unset, and the helper never"
    }

    $json = '{"aiSettings":{"includeDocumentContext":true,"documentContextMaxSegments":300}}'
    [IO.File]::WriteAllText($settingsFile, $json, (New-Object System.Text.UTF8Encoding($true)))
    $s = Load
    $s.AiSettings.SetChatDocumentContext($false)
    Check ([bool]$s.Save()) "Chat unticked and saved"
    $text = FileText
    Check ($text -match '"includeDocumentContext":true' -and $text -match '"includeDocumentContextInChat":false') "the file now holds both: batch on as it was, Chat off"
    $s = Load
    Check ($s.AiSettings.IncludeDocumentContext -and $s.AiSettings.IncludeDocumentContextInChat -eq $false -and -not $s.AiSettings.ChatIncludesDocumentContext) "read back: batch on, Chat off"
    $s.AiSettings.SetBatchDocumentContext($false); [void]$s.Save()
    $s = Load
    Check (-not $s.AiSettings.IncludeDocumentContext -and -not $s.AiSettings.ChatIncludesDocumentContext) "batch unticked too, saved, read back: both off"
    $s.AiSettings.SetChatDocumentContext($true); [void]$s.Save()
    $s = Load
    Check (-not $s.AiSettings.IncludeDocumentContext -and $s.AiSettings.ChatIncludesDocumentContext -and (FileText) -match '"includeDocumentContextInChat":true') "Chat ticked, saved, read back: Chat on, batch still off"

    $json = '{"aiSettings":{"includeDocumentContext":false,"includeDocumentContextInChat":null}}'
    [IO.File]::WriteAllText($settingsFile, $json, (New-Object System.Text.UTF8Encoding($true)))
    $s = Load
    Check ($null -eq $s.AiSettings.IncludeDocumentContextInChat -and -not $s.AiSettings.ChatIncludesDocumentContext) "a file that says null for Chat: read as never set, Chat follows batch"
    [IO.File]::WriteAllText($settingsFile, '{"aiSettings":{"includeDocumentContext":true,"includeDocumentContextInChat":"perhaps"}}', (New-Object System.Text.UTF8Encoding($true)))
    $a = New-Object 'object[]' 1; $bad = $null; $threw = $false
    try { $bad = $tryLoad.Invoke($null, $a) } catch { $threw = $true }
    Check (-not $threw -and $null -eq $bad -and -not [string]::IsNullOrEmpty([string]$a[0])) "a file with nonsense for Chat's setting: refused as unreadable with a reason, like any broken settings file, no exception"

    # ── 5 ────────────────────────────────────────────────────────────────
    Write-Host "5 Settings > AI Settings"
    $panelType = $asm.GetType('Supervertaler.Trados.Controls.AiSettingsPanel', $true)
    $host3 = New-Object System.Windows.Forms.Form
    $hosts += $host3
    $host3.StartPosition = 'Manual'; $host3.Location = New-Object System.Drawing.Point(-32000, -32000)
    $host3.Size = New-Object System.Drawing.Size(900, 900)
    $panel = [Activator]::CreateInstance($panelType)
    $panel.Dock = 'Fill'
    $host3.Controls.Add($panel)
    $host3.Show(); [System.Windows.Forms.Application]::DoEvents()
    $all = @(AllControls $panel)
    Check (@($all | Where-Object { $_ -is [System.Windows.Forms.CheckBox] -and $_.Text -eq $label }).Count -eq 0) "the tick box is no longer in the panel"
    Check ($null -eq $panelType.GetField('_chkIncludeDocumentContext', $private)) "nor is its field in the code"
    $note = @($all | Where-Object { $_ -is [System.Windows.Forms.Label] -and $_.Text -like '*Chat tab*' -and $_.Text -like '*Batch Operations tab*' })
    Check ($note.Count -eq 1 -and $note[0].Text -like "*$label*") "a note says where the tick box is now: $($note[0].Text)"
    $nud = $panelType.GetField('_nudMaxSegments', $private).GetValue($panel)
    $lbl = $panelType.GetField('_lblMaxSegments', $private).GetValue($panel)
    Check ($lbl.Text -eq 'Max segments:' -and $nud.Enabled -and $lbl.Enabled) "Max segments is still there, with its label, enabled"

    foreach ($case in @(@($false, $true), @($true, $false), @($false, $null), @($true, $null))) {
        $ai = Ai
        $ai.IncludeDocumentContext = $case[0]
        $ai.IncludeDocumentContextInChat = $case[1]
        $ai.DocumentContextMaxSegments = 300
        $panel.PopulateFromSettings($ai)
        Check ([int]$nud.Value -eq 300 -and $nud.Enabled) "batch $($case[0]), Chat $(if ($null -eq $case[1]) { 'never set' } else { $case[1] }): Max segments shows 300 and is enabled"
        $nud.Value = 700
        $panel.ApplyToSettings($ai)
        Check ($ai.IncludeDocumentContext -eq $case[0] -and $ai.IncludeDocumentContextInChat -eq $case[1] -and $ai.DocumentContextMaxSegments -eq 700) "OK saves Max segments (700) and leaves both tick-box settings as they were"
    }
}
finally {
    foreach ($h in $hosts) { try { $h.Close(); $h.Dispose() } catch { } }
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Check (((Snap) -join "`n") -eq ($before -join "`n")) "the real settings and API-key files are as they were"

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
