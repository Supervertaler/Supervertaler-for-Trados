# The startup warning about a Trados Studio still running in the background
# without a window (HiddenStudioCheck, 25 Nov 2026) - checked against the built
# plugin DLL, outside Studio.
#
# What it must do: report another SDLTradosStudio process started from the SAME
# exe as ours that shows no window; never report our own process, a Studio with
# a window, another Studio version (2024 beside 2026, the beta beside the
# release), or a process whose path can't be read; cope with nothing at all to
# read; and say clearly what to do. It also reads the real process list once,
# read-only, to show that never throws.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\hidden_studio_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Release build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\hidden_studio_test.ps1
#
# Writes nothing: no data folder is touched and no process is started or ended.
# What it cannot reach: the 30-second timer and the message box inside Studio.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Release', 'Studio19\Debug') } else { @('Studio18\Release', 'Studio18\Debug', 'Studio17\Release', 'Studio17\Debug') }
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

Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class HiddenStudioTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[HiddenStudioTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

$check = $asm.GetType('Supervertaler.Trados.Core.HiddenStudioCheck', $true)
$procType = $asm.GetType('Supervertaler.Trados.Core.HiddenStudioCheck+StudioProcess', $true)
$static = [System.Reflection.BindingFlags]'Static,NonPublic,Public'
$find = $check.GetMethod('FindHidden', $static)
$message = $check.GetMethod('Message', $static)
$listType = [System.Collections.Generic.List``1].MakeGenericType($procType)

$own = 'C:\Program Files\Trados\Trados Studio\Studio19\SDLTradosStudio.exe'
$beta = 'C:\Program Files\Trados\Trados Studio\Studio19Beta\SDLTradosStudio.exe'
$s2024 = 'C:\Program Files (x86)\Trados\Trados Studio\Studio18\SDLTradosStudio.exe'

function P([int]$id, [string]$path, [bool]$window, $started = $null) {
    $p = [Activator]::CreateInstance($procType, $true)
    $procType.GetField('Id').SetValue($p, $id)
    $procType.GetField('Path').SetValue($p, $path)
    $procType.GetField('HasWindow').SetValue($p, $window)
    if ($started) { $procType.GetField('Started').SetValue($p, [Nullable[DateTime]]$started) }
    return $p
}
function L([object[]]$items) {
    $l = [Activator]::CreateInstance($listType)
    foreach ($i in $items) { $l.Add($i) }
    return ,$l
}
function Ids($result) { return (@($result | ForEach-Object { $procType.GetField('Id').GetValue($_) }) -join ',') }
function Find([int]$ownId, [string]$ownPath, $list) { return ,$find.Invoke($null, [object[]]@($ownId, $ownPath, $list)) }
function Msg($list) { $a = New-Object 'object[]' 1; $a[0] = $list; return $message.Invoke($null, $a) }

Write-Host "Who is reported"
$r = Find 100 $own (L @((P 100 $own $true)))
Check ((Ids $r) -eq '') "only our own Studio: nothing"
$r = Find 100 $own (L @((P 100 $own $true), (P 200 $own $false)))
Check ((Ids $r) -eq '200') "a windowless copy of our Studio: reported"
$r = Find 100 $own (L @((P 100 $own $false)))
Check ((Ids $r) -eq '') "our own process, even before its window exists: never"
$r = Find 100 $own (L @((P 100 $own $true), (P 200 $own $true)))
Check ((Ids $r) -eq '') "another copy WITH a window: not reported"
$r = Find 100 $own (L @((P 100 $own $true), (P 300 $beta $false), (P 400 $s2024 $false)))
Check ((Ids $r) -eq '') "the beta or Studio 2024 without a window: not ours, not reported"
$r = Find 100 $own (L @((P 100 $own $true), (P 500 $null $false), (P 501 '' $false), (P 502 '   ' $false)))
Check ((Ids $r) -eq '') "a process whose path can't be read: left out"
$r = Find 100 $own (L @((P 100 $own $true), (P 600 $own.ToUpperInvariant() $false), (P 601 'C:\Program Files\Trados\Trados Studio\Studio19\..\Studio19\SDLTradosStudio.exe' $false)))
Check ((Ids $r) -eq '600,601') "the same exe written differently (case, ..\): still ours"
$r = Find 100 $own (L @((P 100 $own $true), (P 900 $own $false), (P 700 $own $false)))
Check ((Ids $r) -eq '700,900') "several: all reported, in process-ID order"

Write-Host "Nothing to go on"
$r = Find 100 $null (L @((P 200 $own $false)))
Check ((Ids $r) -eq '') "our own path unknown: nothing (when in doubt, say nothing)"
$r = Find 100 $own $null
Check ((Ids $r) -eq '' -and $null -ne $r) "no process list: an empty answer, not an error"
$r = Find 100 $own (L @($null, (P 200 $own $false)))
Check ((Ids $r) -eq '200') "a null entry in the list is skipped"
$r = Find 100 'C:\bad|path<>\x.exe' (L @((P 200 'C:\bad|path<>\x.exe' $false)))
Check ((Ids $r) -eq '') "a path Windows refuses: nothing, no exception"

Write-Host "Scale"
$many = @((P 1 $own $true)); for ($i = 2; $i -le 5000; $i++) { $many += (P $i $s2024 $false) }
$many += (P 99999 $own $false)
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Find 1 $own (L $many)
$sw.Stop()
Check ((Ids $r) -eq '99999') "5,000 other Studio processes: the one hidden copy found"
Check ($sw.ElapsedMilliseconds -lt 2000) "and quickly ($($sw.ElapsedMilliseconds) ms)"

Write-Host "The message"
Check ($null -eq (Msg $null)) "nothing hidden: no message"
Check ($null -eq (Msg (L @()))) "an empty list: no message"
$m1 = [string](Msg (L @((P 4242 $own $false ([DateTime]'2026-11-25 09:14')))))
Check ($m1 -match 'Another copy of Trados Studio is still running in the background') "one: says a copy is running in the background"
Check ($m1 -match '4242 \(started 09:14\)') "one: names the process ID and when it started"
Check ($m1 -match 'Ctrl\+Shift\+Esc' -and $m1 -match 'Details' -and $m1 -match 'End task') "says how to close it"
Check ($m1 -match 'not affected') "says the Studio being used is not affected"
$m2 = [string](Msg (L @((P 1 $own $false), (P 2 $own $false))))
Check ($m2 -match '^Other copies' -and $m2 -match 'process ID 1, 2' -and $m2 -match 'close them') "two: plural wording, both IDs"

Write-Host "The real process list (read-only)"
$ok = $true
try {
    $snap = $check.GetMethod('Snapshot', $static).Invoke($null, @())
    $ownPath = $check.GetMethod('OwnPath', $static).Invoke($null, @())
} catch { $ok = $false }
Check $ok "reading the processes on this computer does not throw"
Check (-not [string]::IsNullOrEmpty($ownPath)) "our own exe path is found ($ownPath)"
Write-Host "  (Studio processes running now: $(@($snap).Count))"

Write-Host ""
Write-Host "Passed: $script:pass, failed: $script:fail"
if ($script:fail -gt 0) { Write-Host "RESULT: FAILED"; exit 1 }
Write-Host "RESULT: all passed"
exit 0
