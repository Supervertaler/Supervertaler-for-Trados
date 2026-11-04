# TermLens termbase loading: speed with a project open, and every load off the
# UI thread (18.20.205) - checked against the built plugin DLL, outside Studio.
#
#  Part 1  TermbaseReader.LoadAllTerms with and without a project source language.
#          It worked out each termbase's direction once per ROW, parsing Studio's
#          language name ("Dutch (Netherlands)") every time at ~4 ms a go: a real
#          project load took ~53 s where the same load without a project took <1 s.
#          Checks the time, and that every entry is turned (or left out) exactly
#          as LanguageUtils.CompareTermbaseDirection says for its termbase.
#  Part 2  TermLensEditorViewPart's reload: start-up, ↻ / F5, a document from another
#          project, a settings change and a Workbench edit all read on a pool thread
#          and only swap in on the UI thread; requests coalesce; failure modes
#          (Workbench holding the database, a missing or broken file, a document
#          switch or an edit landing mid-read) keep the terms shown and never leave
#          the reload stuck.
#  Part 3  (optional, -RealDbCopy) timings on a COPY of a real database. Prints
#          counts and milliseconds only, never a term.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\termbase_load_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
#   ... -Scale 400000                              (terms in the big test database)
#   ... -RealDbCopy <copy of supervertaler.db> [-RealSettings <settings.json>]
#
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\termbase_load_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Everything runs on databases in %TEMP%, deleted afterwards. Settings are pre-seeded
# in memory, so the real settings and key files are never loaded (the first real load
# writes the key files); they are checked unchanged at the end. Diagnostic logging
# stays off, so the real diagnostic.log is not written either.
#
# "Longest UI-thread stall" is the longest this script's message loop went without
# running, so it includes PowerShell's own pauses and garbage collection. Windows
# PowerShell collects garbage non-concurrently ("Batch": every collection stops
# every thread, idle gaps up to ~250 ms); Studio uses the .NET default, concurrent.
# To measure as Studio would, run with the environment variable COMPlus_gcConcurrent=1
# (idle gaps then ~20 ms). The plugin's own UI-thread work is the swap, timed apart.
param(
    [string]$Dll,
    [int]$Scale = 40000,
    [string]$RealDbCopy,
    [string]$RealSettings
)
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
Write-Host "Plugin: $Dll"
Write-Host "Studio: $studio ($([IntPtr]::Size * 8)-bit process)"

# Compiled, not a scriptblock: the reload loads assemblies on pool threads, where a
# PowerShell scriptblock has no runspace to run in (StackOverflowException).
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class TermbaseLoadTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[TermbaseLoadTestResolver]::Install([string[]]@((Split-Path $Dll), $studio))

# ─── Real files: must be unchanged ───────────────────────────────────
$realFiles = @(
    (Join-Path $env:APPDATA "Supervertaler\config.json"),
    (Join-Path $env:APPDATA "Supervertaler\api-keys.locked.json"),
    (Join-Path $env:LOCALAPPDATA "Supervertaler.Trados\multiterm_debug.log"))
$cfg = Join-Path $env:APPDATA "Supervertaler\config.json"
if (Test-Path -LiteralPath $cfg) {
    try {
        $dataDir = (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).user_data_path
        if ($dataDir) {
            $realFiles += (Join-Path $dataDir "settings\api-keys.json")
            $realFiles += (Join-Path $dataDir "trados\settings\settings.json")
        }
    } catch { }
}
function Stamp {
    return (($realFiles | ForEach-Object {
        if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$($i.Length) $($i.LastWriteTimeUtc.ToString('o')) $_" } else { "missing $_" }
    }) -join "`n")
}
$realBefore = Stamp

Add-Type -AssemblyName System.Windows.Forms
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$sqlAsm = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $Dll) "Microsoft.Data.Sqlite.dll"))
$batteries = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $Dll) "SQLitePCLRaw.batteries_v2.dll"))
[void]$batteries.GetType("SQLitePCL.Batteries_V2").GetMethod("Init").Invoke($null, @())
$NP = [Reflection.BindingFlags]"NonPublic,Public,Static,Instance"

$ReaderT   = $plugin.GetType("Supervertaler.Trados.Core.TermbaseReader")
$LangT     = $plugin.GetType("Supervertaler.Trados.Core.LanguageUtils")
$LensT     = $plugin.GetType("Supervertaler.Trados.Controls.TermLensControl")
$MatcherT  = $plugin.GetType("Supervertaler.Trados.Core.TermMatcher")
$ViewT     = $plugin.GetType("Supervertaler.Trados.TermLensEditorViewPart")
$SettingsT = $plugin.GetType("Supervertaler.Trados.Settings.TermLensSettings")
$SvcT      = $plugin.GetType("Supervertaler.Trados.Settings.SettingsService")
$UiT       = $plugin.GetType("Supervertaler.Trados.Core.UiThread")
$EntryT    = $plugin.GetType("Supervertaler.Core.Models.TermEntry")
$DirT      = $LangT.GetNestedType("TermbaseDirection")
$ConnT     = $sqlAsm.GetType("Microsoft.Data.Sqlite.SqliteConnection")

# Settings first, before anything can load the real ones.
$seeded = [Activator]::CreateInstance($SettingsT)
$SvcT.GetField("_current", $NP).SetValue($null, $seeded)
# This thread plays Studio's UI thread: the reload marshals onto it, and that work
# runs whenever the loops below pump messages.
[void]$UiT.GetMethod("Install", $NP).Invoke($null, @())

$script:fails = 0
function Check($label, $actual, $expected) {
    if ("$actual" -ceq "$expected") { Write-Host "  ok   $label" }
    else { Write-Host "  FAIL $label`n       got: [$actual]`n  expected: [$expected]"; $script:fails++ }
}
function NewConn([string]$path) { $cn = [Activator]::CreateInstance($ConnT, @("Data Source=$path;Mode=ReadWrite;Pooling=False")); $cn.Open(); return $cn }
function Exec($cn, [string]$q, [hashtable]$p) {
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $q
    if ($p) { foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue($k, $p[$k]) } }
    $n = $cmd.ExecuteNonQuery(); $cmd.Dispose(); return $n
}
function Scalar($cn, [string]$q) { $cmd = $cn.CreateCommand(); $cmd.CommandText = $q; $v = $cmd.ExecuteScalar(); $cmd.Dispose(); return $v }
function Pump([int]$ms) { $sw = [Diagnostics.Stopwatch]::StartNew(); while ($sw.ElapsedMilliseconds -lt $ms) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 } }
# Pumps like a UI thread until $cond holds. MaxGap = the longest the "UI thread"
# went without getting back to its message loop - what typing would feel.
# Timeouts are sized for 40,000 terms and grow with -Scale (a load is linear);
# -Exact for a wait whose length is the point.
$timeScale = [Math]::Max(1.0, $Scale / 40000.0)
function WaitFor([scriptblock]$cond, [int]$timeoutMs = 60000, [switch]$Exact) {
    if (-not $Exact) { $timeoutMs = [int]($timeoutMs * $timeScale) }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $maxGap = 0; $last = 0
    while ($true) {
        [System.Windows.Forms.Application]::DoEvents()
        $now = $sw.ElapsedMilliseconds; if (($now - $last) -gt $maxGap) { $maxGap = $now - $last }; $last = $now
        if (& $cond) { return [pscustomobject]@{ Ok = $true; Ms = $now; MaxGap = $maxGap } }
        if ($now -gt $timeoutMs) { return [pscustomobject]@{ Ok = $false; Ms = $now; MaxGap = $maxGap } }
        Start-Sleep -Milliseconds 5
    }
}

# ─── Test databases ──────────────────────────────────────────────────
$tmp = Join-Path $env:TEMP ("termbase-load-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

# Five termbases in the shapes a real database has: both directions, an unrelated
# pair, one declared by language NAME rather than code, one with no languages.
# Every term's text says its language, so orientation can be checked by eye.
function NewDb([string]$path, [int]$n, [string]$prefix) {
    [void]$ReaderT.GetMethod("CreateDatabase").Invoke($null, @([string]$path))
    $mk = $ReaderT.GetMethod("CreateTermbase")
    $tb = [ordered]@{
        EnNl  = [long]$mk.Invoke($null, @([string]$path, "$prefix EN-NL", "en", "nl"))
        NlEn  = [long]$mk.Invoke($null, @([string]$path, "$prefix NL-EN", "nl-NL", "en-GB"))
        DeEn  = [long]$mk.Invoke($null, @([string]$path, "$prefix DE-EN", "de", "en"))
        Named = [long]$mk.Invoke($null, @([string]$path, "$prefix English-Dutch", "English", "Dutch"))
        None  = [long]$mk.Invoke($null, @([string]$path, "$prefix undeclared", "", ""))
    }
    $cn = NewConn $path
    try {
        # One statement, so 400,000 rows take seconds, not a PowerShell loop's minutes.
        [void](Exec $cn @"
WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < @n)
INSERT INTO termbase_terms (source_term, target_term, source_lang, target_lang, termbase_id)
SELECT
  CASE WHEN i % 20 < 11 OR i % 20 = 18 THEN '$prefix en ' || i WHEN i % 20 < 16 THEN '$prefix nl ' || i WHEN i % 20 < 18 THEN '$prefix de ' || i ELSE '$prefix xx ' || i END,
  CASE WHEN i % 20 < 11 OR i % 20 = 18 THEN '$prefix nl ' || i WHEN i % 20 < 18 THEN '$prefix en ' || i ELSE '$prefix yy ' || i END,
  CASE WHEN i % 20 < 11 OR i % 20 = 18 THEN 'en' WHEN i % 20 < 16 THEN 'nl' WHEN i % 20 < 18 THEN 'de' ELSE '' END,
  CASE WHEN i % 20 < 11 OR i % 20 = 18 THEN 'nl' WHEN i % 20 < 18 THEN 'en' ELSE '' END,
  CASE WHEN i % 20 < 11 THEN @a WHEN i % 20 < 16 THEN @b WHEN i % 20 < 18 THEN @c WHEN i % 20 = 18 THEN @d ELSE @e END
FROM n
"@ @{ "@n" = $n; "@a" = "$($tb.EnNl)"; "@b" = "$($tb.NlEn)"; "@c" = "$($tb.DeEn)"; "@d" = "$($tb.Named)"; "@e" = "$($tb.None)" })
        # Synonyms on every 25th term (both sides), so the swap of synonym roles is covered.
        [void](Exec $cn "INSERT INTO termbase_synonyms (term_id, synonym_text, language) SELECT id, source_term || ' syn', 'source' FROM termbase_terms WHERE id % 25 = 0" $null)
        [void](Exec $cn "INSERT INTO termbase_synonyms (term_id, synonym_text, language) SELECT id, target_term || ' syn', 'target' FROM termbase_terms WHERE id % 25 = 0" $null)
        $hasAbbr = [long](Scalar $cn "SELECT COUNT(*) FROM pragma_table_info('termbase_terms') WHERE name = 'source_abbreviation'")
        if ($hasAbbr -gt 0) {
            [void](Exec $cn "UPDATE termbase_terms SET source_abbreviation = 'SA' || id, target_abbreviation = 'TA' || id WHERE id % 50 = 0" $null)
        }
    } finally { $cn.Dispose() }
    return $tb
}

function NewReader([string]$path) { $r = [Activator]::CreateInstance($ReaderT, @([string]$path)); if (-not $r.Open()) { throw "open failed: $($r.LastError)" }; return $r }
function LoadAll([string]$path, $disabled, [string]$lang) {
    $r = NewReader $path
    try {
        $l = if ($lang) { $lang } else { $null }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $idx = $ReaderT.GetMethod("LoadAllTerms").Invoke($r, @($disabled, $false, $l, $null))
        return [pscustomobject]@{ Index = $idx; Ms = $sw.ElapsedMilliseconds }
    } finally { $r.Dispose() }
}

try {
# ═══ Part 1: LoadAllTerms with a project language ═══════════════════
Write-Host "`nPart 1 - LoadAllTerms, $('{0:N0}' -f $Scale) terms in 5 termbases"
$dbA = Join-Path $tmp "a.db"
$sw = [Diagnostics.Stopwatch]::StartNew()
$tbA = NewDb $dbA $Scale "acme"
Write-Host "       test database built in $($sw.ElapsedMilliseconds) ms"

# The stored rows, for the orientation oracle.
$rows = @{}
$cn = NewConn $dbA
try {
    $cmd = $cn.CreateCommand(); $cmd.CommandText = "SELECT id, source_term, target_term, CAST(termbase_id AS INTEGER) FROM termbase_terms"
    $rd = $cmd.ExecuteReader()
    while ($rd.Read()) { $rows[$rd.GetInt64(0)] = @($rd.GetString(1), $rd.GetString(2), $rd.GetInt64(3)) }
    $rd.Dispose(); $cmd.Dispose()
} finally { $cn.Dispose() }
$tbLangs = @{}
$rdr = NewReader $dbA
foreach ($kv in $rdr.GetTermbaseDirections().GetEnumerator()) { $tbLangs[[long]$kv.Key] = $kv.Value }
$rdr.Dispose()

[void](LoadAll $dbA $null $null)   # warm-up: JIT and file cache, so the timings compare loads
$memBefore = [GC]::GetTotalMemory($true)
$base = LoadAll $dbA $null $null
$memIndex = [GC]::GetTotalMemory($true) - $memBefore
Write-Host ("       no project:                  {0,6} ms, {1:N0} keys" -f $base.Ms, $base.Index.Count)
Write-Host ("       one index of this size holds ~{0:N0} MB; a reload has two for a moment" -f ($memIndex / 1MB))
$cmp = $LangT.GetMethod("CompareTermbaseDirection")
foreach ($lang in @("Dutch (Netherlands)", "English (United Kingdom)", "nl-NL", "Dutch", "German (Germany)", "French (France)")) {
    $res = LoadAll $dbA $null $lang
    Write-Host ("       project source '{0}': {1,6} ms, {2:N0} keys" -f $lang, $res.Ms, $res.Index.Count)
    Check "  '$lang': as quick as a load without a project (<= 2x + 1 s)" ($res.Ms -le (2 * $base.Ms + 1000)) "True"

    # Oracle: every row of a termbase that is not Unrelated is indexed once under
    # the side the project reads, turned when the termbase is Inverted; rows of
    # an Unrelated termbase are left out.
    $dirs = @{}
    foreach ($id in $tbLangs.Keys) { $dirs[$id] = [string]$cmp.Invoke($null, @($lang, [string]$tbLangs[$id].Item1, [string]$tbLangs[$id].Item2)) }
    $seen = @{}; $wrong = 0
    foreach ($kv in $res.Index.GetEnumerator()) {
        foreach ($e in $kv.Value) {
            if ($seen.ContainsKey($e.Id)) { continue }
            $seen[$e.Id] = $true
            $row = $rows[$e.Id]; $d = $dirs[[long]$row[2]]
            $wantSrc = if ($d -eq 'Inverted') { $row[1] } else { $row[0] }
            if ($d -eq 'Unrelated' -or $e.SourceTerm -cne $wantSrc) { $wrong++ }
        }
    }
    $expected = 0; foreach ($id in $rows.Keys) { if ($dirs[[long]$rows[$id][2]] -ne 'Unrelated') { $expected++ } }
    Check "  '$lang': every entry turned as its termbase says, none of an unrelated pair" $wrong "0"
    Check "  '$lang': every row of a related termbase is in" $seen.Count $expected
}
$enKey = "acme en 1"
$nlKey = "acme nl 1"
$dutch = (LoadAll $dbA $null "Dutch (Netherlands)").Index
Check "Dutch project: EN-NL row 1 found under its Dutch word"  ($dutch.ContainsKey($nlKey) -and $dutch[$nlKey][0].TargetTerm -ceq $enKey) "True"
Check "Dutch project: its English word is not a key"           $dutch.ContainsKey($enKey) "False"
Check "Dutch project: DE-EN terms left out"                    $dutch.ContainsKey("acme de 16") "False"
Check "No project: EN-NL row 1 under its stored English word"  $base.Index.ContainsKey($enKey) "True"
$t1 = $LangT.GetMethod("CompareTermbaseDirection")
$sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt 200; $i++) { [void]$t1.Invoke($null, @("Dutch (Netherlands)", "en", "nl")) }
Write-Host ("       one direction comparison for 'Dutch (Netherlands)': {0:N3} ms (was ~4.3 ms; the load made one per term)" -f ($sw.Elapsed.TotalMilliseconds / 200))
Check "a direction comparison no longer scans every culture (< 1 ms)" (($sw.Elapsed.TotalMilliseconds / 200) -lt 1) "True"
# Same answers as before: the plugin's own start-up self-test of the direction
# rules, and the names Studio and termbases use.
Check "LanguageUtils start-up self-test" ([string]$LangT.GetMethod("RunStartupSelfTest").Invoke($null, @())) ""
$shortMi = $LangT.GetMethod("ShortenLanguageName")
foreach ($p in @(@("Dutch (Netherlands)", "Dutch (NL)"), @("English (United Kingdom)", "English (GB)"), @("nl-BE", "Dutch (BE)"),
                 @("en", "English"), @("Dutch", "Dutch"), @("Chinese (Simplified)", "Chinese (Simplified)"))) {
    Check "  ShortenLanguageName('$($p[0])')" ([string]$shortMi.Invoke($null, @($p[0]))) $p[1]
}
$rows = $null; $dutch = $null

# ═══ Part 2: the panel's reloads, off the UI thread ══════════════════
Write-Host "`nPart 2 - TermLensEditorViewPart reloads (no document open)"
$view = [System.Runtime.Serialization.FormatterServices]::GetUninitializedObject($ViewT)
# Field initialisers do not run for an uninitialised object; the MultiTerm clean-up
# locks this one.
$ViewT.GetField("_prewarmedTextsLock", $NP).SetValue($view, (New-Object object))
$ViewT.GetField("_prewarmedTexts", $NP).SetValue($view, [System.Collections.Generic.HashSet[string]]::new())
$ViewT.GetField("_currentInstance", $NP).SetValue($null, $view)
$lens = $ViewT.GetField("_control", $NP).GetValue($null).Value
$seeded.TermbasePath = $dbA
$KindT = $ViewT.GetNestedType("ReloadKind", $NP)
$reqMi = $ViewT.GetMethod("RequestTermbaseReload", $NP)
function Request([int]$k) { [void]$reqMi.Invoke($view, @([Enum]::ToObject($KindT, $k))) }
$runningF = $ViewT.GetField("_termReloadRunning", $NP)
function Idle { -not [bool]$runningF.GetValue($null) }
function Matcher { $LensT.GetField("_matcher", $NP).GetValue($lens) }
function Version { [int]$MatcherT.GetProperty("Version").GetValue((Matcher)) }
function HasKey([string]$k) { $i = $MatcherT.GetField("_termIndex", $NP).GetValue((Matcher)); return ($null -ne $i -and $i.ContainsKey($k)) }
function Status { $LensT.GetField("_statusLabel", $NP).GetValue($lens).Text }
$watchedF = $ViewT.GetField("_watchedDbPath", $NP)
function WaitIdle([int]$timeoutMs = 120000) { return WaitFor { Idle } $timeoutMs }

# Start-up / ↻: a full reload.
$v0 = Version
$sw = [Diagnostics.Stopwatch]::StartNew(); Request 3; $callMs = $sw.ElapsedMilliseconds
$r = WaitIdle
Check "Refresh / start-up: the request returns at once ($callMs ms)" ($callMs -lt 200) "True"
Write-Host "       loaded $($r.Ms) ms later; longest UI-thread stall $($r.MaxGap) ms"
Check "the terms are in" "$($lens.CurrentDbPath -eq $dbA) $(HasKey $enKey)" "True True"
Check "one swap" ((Version) - $v0) "1"
Check "the file watcher is on the loaded database" ($watchedF.GetValue($view)) $dbA
# Not zero: garbage collection pauses every thread, and a read this size makes
# plenty (see the header). What must not happen is the read itself on this thread.
$stallLimit = [Math]::Max(400, [int]($base.Ms / 4))
Check "the UI thread is never held for the read (stall < $stallLimit ms, a quarter of a read)" ($r.MaxGap -lt $stallLimit) "True"
# For comparison: what the same read costs when it runs on the UI thread, as
# Refresh / F5 / a document switch did. Then the part that does run there: the swap.
$ra = New-Object 'object[]' 5; $ra[0] = [string]$dbA; $ra[1] = $null; $ra[2] = $false; $ra[3] = $null; $ra[4] = $null
$sw = [Diagnostics.Stopwatch]::StartNew(); $rd = $LensT.GetMethod("ReadTermbase").Invoke($null, $ra); $readMs = $sw.ElapsedMilliseconds
Write-Host "       the same read ON the UI thread (the old Refresh) would hold it $readMs ms - and ~4 ms per term more before the fix"
Check "the read carries the status line" $rd.StatusText "5 termbases, $Scale terms"
$sw = [Diagnostics.Stopwatch]::StartNew(); $swapped = $LensT.GetMethod("SwapInTermbase").Invoke($lens, @($rd, (Version))); $swapMs = $sw.ElapsedMilliseconds
$rd.Dispose()
Write-Host "       the swap, which is all that runs on the UI thread: $swapMs ms"
Check "the swap is quick (< 500 ms)" "$swapped $($swapMs -lt 500)" "True True"
$v1 = Version
$rd = $LensT.GetMethod("ReadTermbase").Invoke($null, $ra)
Check "a read overtaken by an index change is refused" ($LensT.GetMethod("SwapInTermbase").Invoke($lens, @($rd, ($v1 - 1)))) "False"
Check "... and its connection is closed on dispose" "$($null -ne $rd.GetType().GetField('Reader', $NP).GetValue($rd))" "True"
$rd.Dispose()
Check "    (closed)" "$($null -eq $rd.GetType().GetField('Reader', $NP).GetValue($rd))" "True"

# Coalescing: a storm of requests while one runs.
$v0 = Version
for ($i = 0; $i -lt 30; $i++) { Request (1 + ($i % 3)) }
$r = WaitIdle
Check "30 requests in a row: at most 2 loads (the running one + one owed)" (((Version) - $v0) -le 2) "True"
Write-Host "       loads: $((Version) - $v0); longest UI-thread stall $($r.MaxGap) ms"

# Workbench: a burst of edits from another connection, through the watcher.
$wb = NewConn $dbA
function WbInsert([string]$src) { [void](Exec $wb "INSERT INTO termbase_terms (source_term, target_term, source_lang, target_lang, termbase_id) VALUES (@s, 'x', 'en', 'nl', @tb)" @{ "@s" = $src; "@tb" = "$($tbA.EnNl)" }) }
$v0 = Version
for ($i = 1; $i -le 20; $i++) { WbInsert "workbench burst $i"; Pump 50 }
$r = WaitFor { (HasKey "workbench burst 20") -and (Idle) } 30000
Check "Workbench: 20 edits in a burst all show" "$($r.Ok) $(HasKey 'workbench burst 1')" "True True"
Check "Workbench: ... in ONE reload" ((Version) - $v0) "1"
Write-Host "       last edit shown $($r.Ms) ms after it; longest UI-thread stall $($r.MaxGap) ms"

# An edit that lands while a load is reading.
$v0 = Version
Request 2; Pump 150
WbInsert "workbench during read"
$r = WaitFor { (HasKey "workbench during read") -and (Idle) } 30000
Check "an edit made during a load still shows" $r.Ok "True"
Check "... with one load after the running one, not more" (((Version) - $v0) -le 2) "True"
Pump 1500
Check "... and nothing reloads after that by itself" (Idle) "True"

# Workbench mid-write (WAL): an open write transaction does not hold up the read.
$v0 = Version
[void](Exec $wb "BEGIN IMMEDIATE" $null); WbInsert "uncommitted"
$sw = [Diagnostics.Stopwatch]::StartNew(); Request 2
$r = WaitIdle 30000
Check "Workbench mid-write: the load does not wait for it" (($r.Ok) -and ($sw.ElapsedMilliseconds -lt [Math]::Max(5000, 3 * $readMs))) "True"
Check "... and shows the committed state" "$(HasKey 'uncommitted')" "False"
[void](Exec $wb "COMMIT" $null)
$r = WaitFor { (HasKey "uncommitted") -and (Idle) } 30000
Check "... the write shows once committed" $r.Ok "True"
$wb.Dispose()

# A writer holding the database so that readers must wait. In WAL mode - which
# supervertaler.db uses - a writer cannot do that while other connections are
# open: readers keep reading the last commit (above). A database in rollback-
# journal mode can be held, so that is the worst case: the load waits on its pool
# thread; the UI thread does not.
$dbL = Join-Path $tmp "locked.db"
$tbL = NewDb $dbL 2000 "lock"
# The plugin's create calls leave pooled connections open, and the journal mode
# changes only with none.
[void]$ConnT.GetMethod("ClearAllPools").Invoke($null, @())
$jm = NewConn $dbL; $mode = [string](Scalar $jm "PRAGMA journal_mode=DELETE"); $jm.Dispose()
Check "(rollback-journal test database: journal_mode $mode)" $mode "delete"
$seeded.TermbasePath = $dbL; Request 2; [void](WaitIdle 60000)
Check "(rollback-journal test database loaded)" (HasKey "lock en 1") "True"
$lock = NewConn $dbL
[void](Exec $lock "BEGIN EXCLUSIVE" $null)
[void](Exec $lock "INSERT INTO termbase_terms (source_term, target_term, source_lang, target_lang, termbase_id) VALUES ('held write', 'x', 'en', 'nl', '$($tbL.EnNl)')" $null)
Request 2
$held = WaitFor { $false } 3000 -Exact
$stillRunning = -not (Idle)
[void](Exec $lock "COMMIT" $null); $lock.Dispose()
$r = WaitFor { (HasKey "held write") -and (Idle) } 60000
Check "database held for 3 s: the load waited for it" $stillRunning "True"
Check "... while the UI thread kept running (stall < 400 ms)" ($held.MaxGap -lt 400) "True"
Check "... and finished once it was let go, with the held write" $r.Ok "True"
Write-Host "       shown $($r.Ms) ms after release; longest stall while held $($held.MaxGap) ms"
$seeded.TermbasePath = $dbA; Request 2; [void](WaitIdle 60000)

# A missing database (moved, drive gone): nothing to load, terms kept, not stuck.
$fallbacks = @((Join-Path $env:USERPROFILE "Supervertaler_Data\resources\supervertaler.db"),
               (Join-Path $env:LOCALAPPDATA "Supervertaler\resources\supervertaler.db")) | Where-Object { Test-Path -LiteralPath $_ }
if ($fallbacks) {
    Write-Host "  skip missing-file check: a default database exists here and the plugin would fall back to it"
} else {
    $v0 = Version
    $seeded.TermbasePath = (Join-Path $tmp "gone.db")
    Request 3
    $r = WaitIdle 30000
    Check "missing database: the reload ends" $r.Ok "True"
    Check "... the terms shown stay" "$((Version) - $v0) $(HasKey $enKey)" "0 True"
    $seeded.TermbasePath = $dbA
}

# A file that is not a database: the read fails, the terms stay, the next request works.
$bad = Join-Path $tmp "broken.db"
[IO.File]::WriteAllBytes($bad, [byte[]](1..4096 | ForEach-Object { 0x5A }))
$v0 = Version
$seeded.TermbasePath = $bad
Request 2
$r = WaitIdle 30000
Check "broken file: the reload ends, the terms shown stay" "$($r.Ok) $((Version) - $v0) $(HasKey $enKey)" "True 0 True"
Check "... the status line says so" (Status) "Failed to open termbase"
$seeded.TermbasePath = $dbA
Request 2
$r = WaitIdle 60000
Check "... and the next request loads again" "$((Version) - $v0) $(HasKey $enKey) $($lens.CurrentDbPath -eq $dbA)" "1 True True"
Check "... the problem no longer shows" ((Status) -eq "Failed to open termbase") "False"

# A document from another project opens while a load is reading: that project
# names a different database. The read in progress is for the old one and must
# not go in, even briefly; the new one is loaded.
$dbB = Join-Path $tmp "b.db"
$tbB = NewDb $dbB 200 "other"
$v0 = Version
Request 2
Pump 300          # the load has taken its inputs and is reading a.db
$seeded.TermbasePath = $dbB; Request 2    # what ApplyProjectSettingsFromDocument does
$r = WaitIdle 60000
Check "document switch mid-read: the new project's database is loaded" "$($lens.CurrentDbPath -eq $dbB) $(HasKey 'other en 1') $(HasKey $enKey)" "True True False"
Check "... and the old read was never swapped in (one swap)" ((Version) - $v0) "1"
Check "... the watcher moved with it" ($watchedF.GetValue($view)) $dbB
if ($readMs -lt 600) { Write-Host "       (note: a read of a.db takes $readMs ms, so the switch may have come before it began; use a larger -Scale to be sure)" }

# The same with a Read tick changed mid-read (the other half of a project switch).
$seeded.TermbasePath = $dbA; Request 2; [void](WaitIdle 60000)
$v0 = Version
Request 2
Pump 300
$seeded.DisabledTermbaseIds.Add($tbA.EnNl); Request 2
$r = WaitIdle 60000
Check "Read tick changed mid-read: the EN-NL termbase is out" "$(HasKey $enKey) $(HasKey 'acme nl 12')" "False True"
Check "... in one swap" ((Version) - $v0) "1"
$seeded.DisabledTermbaseIds.Clear()

# An edit in this panel lands mid-read (18.20.205's check, still there).
Request 2; [void](WaitIdle 60000)
$v0 = Version
Request 2
Pump 300
$own = NewConn $dbA
[void](Exec $own "INSERT INTO termbase_terms (source_term, target_term, source_lang, target_lang, termbase_id) VALUES ('own edit', 'x', 'en', 'nl', '$($tbA.EnNl)')" $null)
$ownId = [long](Scalar $own "SELECT last_insert_rowid()"); $own.Dispose()
$ids = [System.Collections.Generic.List[long]]::new(); $ids.Add($ownId)
[void]$ViewT.GetMethod("NotifyTermsEdited").Invoke($null, @(,$ids))
Check "own edit: shows at once" (HasKey "own edit") "True"
$r = WaitFor { (Idle) } 60000
Pump 2000; $r = WaitFor { (Idle) } 60000
Check "... and the read it overtook did not undo it" (HasKey "own edit") "True"

# MultiTerm entries survive a Supervertaler reload.
$mt = [Activator]::CreateInstance($EntryT)
$mt.Id = -77; $mt.SourceTerm = "multiterm only"; $mt.TargetTerm = "x"; $mt.IsMultiTerm = $true
$lst = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($EntryT)); $lst.Add($mt)
$dict = [Activator]::CreateInstance([System.Collections.Generic.Dictionary``2].MakeGenericType([string], $lst.GetType()), @([StringComparer]::OrdinalIgnoreCase))
$dict["multiterm only"] = $lst
[void]$LensT.GetMethod("MergeMultiTermEntries").Invoke($lens, @($dict, $null))
Request 2; [void](WaitIdle 60000)
Check "a MultiTerm entry is kept through a reload" (HasKey "multiterm only") "True"

[void]$ViewT.GetMethod("TeardownTermbaseDbWatcher", $NP).Invoke($view, @())
Check "the reload is not left 'running'" (Idle) "True"

# ═══ Part 3: a copy of a real database (optional) ════════════════════
if ($RealDbCopy) {
    Write-Host "`nPart 3 - a copy of a real database (counts and ms only)"
    $disabled = $null
    if ($RealSettings) {
        $j = Get-Content -LiteralPath $RealSettings -Raw -Encoding UTF8 | ConvertFrom-Json
        $disabled = [System.Collections.Generic.HashSet[long]]::new()
        foreach ($id in $j.disabledTermbaseIds) { [void]$disabled.Add([long]$id) }
        Write-Host "       Read tick off for $($disabled.Count) termbase(s), from $RealSettings"
    }
    [void](LoadAll $RealDbCopy $disabled $null)
    foreach ($lang in @("", "Dutch (Netherlands)", "English (United Kingdom)")) {
        $res = LoadAll $RealDbCopy $disabled $lang
        Write-Host ("       {0,-26} {1,7:N0} keys {2,6} ms" -f ("'" + $lang + "'"), $res.Index.Count, $res.Ms)
    }
}
} finally {
    try { [void]$ViewT.GetMethod("TeardownTermbaseDbWatcher", $NP).Invoke($view, @()) } catch { }
    try { [void](WaitFor { Idle } 60000) } catch { }
    try { $lens.Dispose() } catch { }
    try { [void]$ConnT.GetMethod("ClearAllPools").Invoke($null, @()) } catch { }
    Start-Sleep -Milliseconds 300
    try { Remove-Item -LiteralPath $tmp -Recurse -Force } catch { Write-Host "  (could not remove $tmp)" }
}

Write-Host ""
Check "settings in memory still the seeded ones" ([object]::ReferenceEquals($SvcT.GetField("_current", $NP).GetValue($null), $seeded)) "True"
Check "real settings and key files unchanged" (Stamp) $realBefore
if ($script:fails -eq 0) { Write-Host "`nall passed" } else { Write-Host "`n$($script:fails) FAILED"; exit 1 }
