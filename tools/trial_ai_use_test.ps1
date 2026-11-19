# Trial check-in: has this installation used the AI? (18.20.210 / 19.20.210)
# Checked against the built plugin DLL, outside Studio:
#
#   A  TrialAiUse.Count on a usage folder of its own: no folder, an empty one,
#      failed and successful lines, the first day from the oldest month, the cap
#      of 100, the line limit, a garbled line, a file held open for writing,
#      the ledger switched off (unknown only when nothing was found).
#   B  what the check-in sends: ai_requests always, first_ai_day and event only
#      when set (the payload as the plugin serialises it).
#      With the anonymous usage statistics off, nothing about the AI is sent.
#   C  the extra check-in after a session's first AI request (read from the
#      source, not run: running it would call the live dashboard): once per
#      session, never for a failed request or a connection test, never on a
#      licensed computer, subscribed after the usage ledger.
#   D  scale: a ledger of 20 months x 5,000 lines is answered within the limits.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\trial_ai_use_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Release build)
#
# Makes no network call. Uses a folder of its own under %TEMP%, deleted at the end.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Release') } else { @('Studio18\Release', 'Studio17\Release') }
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

Add-Type -AssemblyName System.Runtime.Serialization
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class TrialAiUseResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[TrialAiUseResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

$any = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$use = $asm.GetType('Supervertaler.Trados.Licensing.TrialAiUse', $true)
$count = $use.GetMethod('Count', $any)
function CountIn([string]$dir, [bool]$on) {
    $args2 = [object[]]@($dir, $on, $null)
    $n = $count.Invoke($null, $args2)
    return @([int]$n, $args2[2])
}
function Line([string]$day, [bool]$ok) {
    $okText = if ($ok) { 'true' } else { 'false' }
    return '{"id":"x","ts":"' + $day + 'T10:00:00Z","product":"trados","task":"Translate","provider":"claude","model":"m","ok":' + $okText + ',"error":null}'
}
$temp = Join-Path $env:TEMP ("sv-trial-ai-use-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

try {
    Write-Host "A  counting"
    $r = CountIn (Join-Path $temp 'missing') $true
    Check (($r[0] -eq 0) -and ($r[1] -eq $null)) "A no usage folder: 0, no day"
    $r = CountIn (Join-Path $temp 'missing') $false
    Check ($r[0] -eq -1) "A no usage folder and the ledger switched off: unknown (-1)"
    $d = Join-Path $temp 'a'; New-Item -ItemType Directory -Path $d | Out-Null
    $r = CountIn $d $true
    Check ($r[0] -eq 0) "A an empty folder: 0"
    [IO.File]::WriteAllText((Join-Path $d 'usage-2026-11.jsonl'), ((Line '2026-11-03' $false), (Line '2026-11-04' $true), (Line '2026-11-05' $true), 'not json at all {', '') -join "`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $d 'usage-2026-10.jsonl'), ((Line '2026-10-30' $false), (Line '2026-10-31' $true)) -join "`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $d 'supervertaler-usage-2026-10-01.csv'), 'ok,true', $utf8)
    $r = CountIn $d $true
    Check ($r[0] -eq 3) "A successful lines only, across months, a garbled line and other files ignored: $($r[0])"
    Check ($r[1] -eq '2026-10-31') "A the first day comes from the oldest month's first successful line: $($r[1])"
    $r = CountIn $d $false
    Check (($r[0] -eq 3) -and ($r[1] -eq '2026-10-31')) "A the ledger switched off later: what it holds still counts"
    $f = Join-Path $temp 'f'; New-Item -ItemType Directory -Path $f | Out-Null
    [IO.File]::WriteAllText((Join-Path $f 'usage-2026-11.jsonl'), ((Line '2026-11-01' $false), (Line '2026-11-02' $false)) -join "`n", $utf8)
    $r = CountIn $f $true
    Check (($r[0] -eq 0) -and ($r[1] -eq $null)) "A only failed requests: 0, no day"
    $r = CountIn $f $false
    Check ($r[0] -eq -1) "A only failed requests and the ledger off: unknown"

    $c = Join-Path $temp 'cap'; New-Item -ItemType Directory -Path $c | Out-Null
    $lines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt 250; $i++) { $lines.Add((Line '2026-09-15' $true)) }
    [IO.File]::WriteAllText((Join-Path $c 'usage-2026-09.jsonl'), ($lines -join "`n"), $utf8)
    $r = CountIn $c $true
    Check ($r[0] -eq 100) "A more than 100: reported as 100 ($($r[0]))"

    $l = Join-Path $temp 'limit'; New-Item -ItemType Directory -Path $l | Out-Null
    $lines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt 6000; $i++) { $lines.Add((Line '2026-08-01' $false)) }
    $lines.Add((Line '2026-08-31' $true))
    [IO.File]::WriteAllText((Join-Path $l 'usage-2026-08.jsonl'), ($lines -join "`n"), $utf8)
    $r = CountIn $l $true
    Check ($r[0] -eq 0) "A a success after 5,000 lines is not read: the line limit holds ($($r[0]))"

    $w = Join-Path $temp 'open'; New-Item -ItemType Directory -Path $w | Out-Null
    $path = Join-Path $w 'usage-2026-11.jsonl'
    [IO.File]::WriteAllText($path, (Line '2026-11-10' $true) + "`n", $utf8)
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    try {
        $r = CountIn $w $true
        Check ($r[0] -eq 1) "A a ledger file held open for writing is still read"
    } finally { $fs.Dispose() }
    $x = Join-Path $temp 'locked'; New-Item -ItemType Directory -Path $x | Out-Null
    $path = Join-Path $x 'usage-2026-11.jsonl'
    [IO.File]::WriteAllText($path, (Line '2026-11-10' $true) + "`n", $utf8)
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $r = CountIn $x $true
        Check ($r[0] -eq 0) "A a file that cannot be opened at all: nothing found, no exception"
    } finally { $fs.Dispose() }

    Write-Host "B  what is sent"
    $reg = $asm.GetType('Supervertaler.Trados.Licensing.TrialRegistration', $true)
    $payloadType = $reg.GetNestedType('TrialRegisterPayload', $any)
    function Ser($p) {
        $ser = New-Object System.Runtime.Serialization.Json.DataContractJsonSerializer($payloadType)
        $ms = New-Object IO.MemoryStream; $ser.WriteObject($ms, $p); [Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    function SetP($o, $n, $v) { $payloadType.GetProperty($n, $any).SetValue($o, $v, $null) }
    $p = [Activator]::CreateInstance($payloadType, $true)
    SetP $p 'Fingerprint' 'abc'; SetP $p 'Status' 'trial'
    $j = Ser $p
    Check (($j -notmatch 'ai_requests') -and ($j -notmatch 'first_ai_day') -and ($j -notmatch '"event"')) "B usage statistics off: nothing about the AI is sent ($j)"
    SetP $p 'AiRequests' ([Nullable[int]]0)
    $j = Ser $p
    Check (($j -match '"ai_requests":0') -and ($j -notmatch 'first_ai_day') -and ($j -notmatch '"event"')) "B a startup check-in with no AI use: ai_requests 0, no day, no event ($j)"
    SetP $p 'AiRequests' ([Nullable[int]]4); SetP $p 'FirstAiDay' '2026-11-19'; SetP $p 'Event' 'ai'
    $j = Ser $p
    Check (($j -match '"ai_requests":4') -and ($j -match '"first_ai_day":"2026-11-19"') -and ($j -match '"event":"ai"')) "B after the first AI request: count, day and event"
    Check (($j -notmatch 'project|model|file|client|prompt')) "B nothing about projects, models, files or text"

    Write-Host "C  the extra check-in (source)"
    $src = Join-Path $root 'src\Supervertaler.Trados'
    if (Test-Path $src) {
        $lm = [IO.File]::ReadAllText((Join-Path $src 'Licensing\LicenseManager.cs'))
        $init = [IO.File]::ReadAllText((Join-Path $src 'AppInitializer.cs'))
        $tr = [IO.File]::ReadAllText((Join-Path $src 'Licensing\TrialRegistration.cs'))
        Check ($lm.Contains('entry.IsError || entry.Feature == PromptLogFeature.ConnectionTest) return;')) "C a failed request or a connection test sends nothing"
        Check ($lm.Contains('if (_licence.IsActivated) return;')) "C a licensed computer sends nothing"
        Check ($lm.Contains('if (Settings.SettingsService.Current?.UsageStatisticsEnabled != true) return;')) "C usage statistics off: no extra check-in"
        Check ($tr.Contains('if (settings?.UsageStatisticsEnabled == true)')) "C usage statistics off: the startup check-in carries nothing about the AI"
        Check ($lm.Contains('Interlocked.Exchange(ref _aiReported, 1) == 1) return;')) "C once per Studio session, also when two requests finish at once"
        Check ($lm.Contains('afterFirstAiRequest: true')) "C it is marked as the check-in after an AI request"
        $iLedger = $init.IndexOf('UsageLogger.EnsureSubscribed()'); $iNote = $init.IndexOf('NoteAiRequest(e)')
        Check (($iLedger -ge 0) -and ($iNote -gt $iLedger)) "C subscribed after the usage ledger, so the request is in the ledger first"
        Check ($tr.Contains('if (afterFirstAiRequest && aiRequests < 1)')) "C after a request the count is at least 1, even if the ledger is off or not yet written"
    } else { Write-Host "  (source not beside this script: section C skipped)" }

    Write-Host "D  scale"
    $big = Join-Path $temp 'big'; New-Item -ItemType Directory -Path $big | Out-Null
    $month = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt 5000; $i++) { $month.Add((Line '2025-01-01' $false)) }
    $text = $month -join "`n"
    for ($m = 1; $m -le 20; $m++) { [IO.File]::WriteAllText((Join-Path $big ('usage-2025-{0:D2}.jsonl' -f $m)), $text, $utf8) }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = CountIn $big $true
    $sw.Stop()
    Check (($r[0] -eq 0) -and ($sw.ElapsedMilliseconds -lt 2000)) "D 20 months x 5,000 lines: stops at the line limit, in $($sw.ElapsedMilliseconds) ms"
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
