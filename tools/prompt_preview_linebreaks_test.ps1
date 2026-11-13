# Prompt preview shows every line break (Controls\PromptPreviewDialog.ForDisplay),
# against the built plugin DLL, without Studio. Pure text: writes nothing anywhere.
#
#   powershell -NoProfile -File tools\prompt_preview_linebreaks_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    foreach ($c in @('Studio19\Release', 'Studio18\Release', 'Studio19\Debug', 'Studio18\Debug', 'Studio17\Release')) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
Write-Host "DLL: $Dll"

$asm = [Reflection.Assembly]::LoadFrom($Dll)
$T = $asm.GetType('Supervertaler.Trados.Controls.PromptPreviewDialog')
if (-not $T) { throw 'PromptPreviewDialog not found in the DLL' }
$m = $T.GetMethod('ForDisplay', [Reflection.BindingFlags]'Public,NonPublic,Static')
if (-not $m) { throw 'ForDisplay not found' }

$cr = [string][char]13; $lf = [string][char]10; $crlf = $cr + $lf
$fails = 0; $checks = 0
function Show([string]$s) { if ($null -eq $s) { '<null>' } else { $s.Replace($cr, '<CR>').Replace($lf, '<LF>') } }
function Expect([string]$name, $in, [string]$want) {
    $script:checks++
    $args1 = New-Object 'object[]' 1; $args1[0] = $in
    $got = $m.Invoke($null, $args1)
    $good = ($got -ceq $want)
    if (-not $good) { $script:fails++ }
    Write-Host "$(if ($good) {'PASS'} else {'FAIL'}) $name -> $(Show $got)$(if (-not $good) { "  (want $(Show $want))" })"
}

# The 13 Nov 2026 case: a memory-bank file saved with LF only
Expect 'LF only'            ("# Style" + $lf + $lf + "Where each rule" + $lf)  ("# Style" + $crlf + $crlf + "Where each rule" + $crlf)
Expect 'CRLF stays'         ("a" + $crlf + "b")                               ("a" + $crlf + "b")
Expect 'mixed LF and CRLF'  ("a" + $lf + "b" + $crlf + "c")                    ("a" + $crlf + "b" + $crlf + "c")
Expect 'old Mac CR only'    ("a" + $cr + "b")                                 ("a" + $crlf + "b")
Expect 'CR CR LF'           ("a" + $cr + $crlf + "b")                         ("a" + $crlf + $crlf + "b")
Expect 'blank lines kept'   ("a" + $lf + $lf + $lf + "b")                      ("a" + $crlf + $crlf + $crlf + "b")
Expect 'no break'           'one line'                                        'one line'
Expect 'empty'              ''                                                ''
Expect 'null'               $null                                             ''
Expect 'ends with break'    ("a" + $lf)                                       ("a" + $crlf)

# Idempotent: showing already-shown text changes nothing
$checks++
$a1 = New-Object 'object[]' 1; $a1[0] = "x" + $lf + "y" + $crlf + "z" + $cr
$once = $m.Invoke($null, $a1); $a1[0] = $once; $twice = $m.Invoke($null, $a1)
if ($once -ceq $twice) { Write-Host 'PASS applying twice changes nothing' } else { $fails++; Write-Host 'FAIL applying twice changed the text' }

# Scale: a prompt of about 2 million characters (the largest seen is about 90,000)
$checks++
$big = (("A line of prompt text that is about sixty characters long here" + $lf) * 32000)
$a1[0] = $big
$sw = [Diagnostics.Stopwatch]::StartNew(); $out = $m.Invoke($null, $a1); $sw.Stop()
$okBig = ($out.Length -eq $big.Length + 32000) -and ($sw.ElapsedMilliseconds -lt 2000)
if ($okBig) { Write-Host "PASS $($big.Length) characters in $($sw.ElapsedMilliseconds) ms" } else { $fails++; Write-Host "FAIL large text: $($out.Length) chars, $($sw.ElapsedMilliseconds) ms" }

Write-Host ''
Write-Host "$($checks - $fails) passed, $fails failed"
if ($fails -gt 0) { exit 1 }
