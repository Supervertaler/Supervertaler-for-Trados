# Voice "select by number" parsing (VoiceControl\NumberWords.TryParse), against the
# built plugin DLL, without Studio. Pure parsing: writes nothing anywhere.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\voice_numbers_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    foreach ($c in @('Studio18\Debug', 'Studio18\Release', 'Studio19\Debug', 'Studio19\Release', 'Studio17\Debug')) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
Write-Host "DLL: $Dll"

$asm = [Reflection.Assembly]::LoadFrom($Dll)
$NW = $asm.GetType('Supervertaler.Trados.VoiceControl.NumberWords')
if (-not $NW) { throw 'NumberWords not found in the DLL' }
$m = $NW.GetMethod('TryParse', [Reflection.BindingFlags]'Public,NonPublic,Static')

$fails = 0; $checks = 0
function Expect([string]$said, $ok, [int]$from, [int]$to) {
    $script:checks++
    $a = New-Object 'object[]' 3
    $a[0] = $said
    $r = $m.Invoke($null, $a)
    $good = ($r -eq $ok) -and (-not $ok -or ($a[1] -eq $from -and $a[2] -eq $to))
    if (-not $good) { $script:fails++ }
    $got = if ($r) { "$($a[1])-$($a[2])" } else { 'no number' }
    $want = if ($ok) { "$from-$to" } else { 'no number' }
    Write-Host "$(if ($good) {'PASS'} else {'FAIL'}) '$said' -> $got (want $want)"
}

# The 10 Nov 2026 cases: "two" heard as "to" right after "select"
Expect 'select to to three'   $true 2 3
Expect 'select to thru three' $true 2 3
Expect 'select too to three'  $true 2 3
Expect 'select to through five' $true 2 5
Expect 'source select to to three' $true 2 3
Expect 'select to'            $true 2 2
# Left alone on purpose: cannot be told apart
Expect 'select to three'      $true 3 3
# A stray "to" is never word 2
Expect 'to select'            $false 0 0
Expect 'go to'                $false 0 0
Expect 'select'               $false 0 0
# Everything that worked before still does
Expect 'select two to three'  $true 2 3
Expect 'select seven'         $true 7 7
Expect 'seven two nine'       $true 7 9
Expect 'four two'             $true 42 42
Expect 'select twenty two to twenty three' $true 22 23
Expect 'select one thru six'  $true 1 6
Expect 'select three to two'  $true 2 3
Expect 'select 12 to 14'      $true 12 14
Expect 'select seven to to nine' $true 7 9

Write-Host ""
Write-Host "$($checks - $fails)/$checks passed"
if ($fails -gt 0) { exit 1 }
