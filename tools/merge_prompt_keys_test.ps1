# "Similar Term Found" dialog: its buttons have access keys (S, E, K), the
# underlines are visible from the start, and each key does what its button
# does - checked against the built plugin DLL, outside Studio. The dialog is
# built from made-up terms; no termbase, no project and no file are involved.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\merge_prompt_keys_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\merge_prompt_keys_test.ps1
#
# Touches no files.
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
public static class MergePromptTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[MergePromptTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$dialogType = $asm.GetType('Supervertaler.Trados.Controls.MergePromptDialog', $true)
$matchType = $asm.GetType('Supervertaler.Trados.Core.MergeMatch', $true)
$private = [System.Reflection.BindingFlags]'Instance,NonPublic'
$tryKey = $dialogType.GetMethod('TryAccessKey', $private)
$cues = [System.Windows.Forms.Control].GetProperty('ShowKeyboardCues', $private)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# The dialog as the translator gets it: one existing entry with the same target.
function Open-Dialog {
    $m = [Activator]::CreateInstance($matchType)
    $m.TermId = 1; $m.TermbaseId = 1; $m.TermbaseName = 'ACME'
    $m.SourceTerm = 'voorkeursvorm'; $m.TargetTerm = 'preferred embodiment'; $m.MatchType = 'target'
    $list = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($matchType))
    $list.Add($m)
    $dlg = [Activator]::CreateInstance($dialogType, @($list, 'voorkeurdragende uitvoeringsvorm', 'preferred embodiment'))
    $dlg.StartPosition = 'Manual'
    $dlg.Location = New-Object System.Drawing.Point(-32000, -32000)
    $dlg.Show()
    $part = @{ Form = $dlg }
    foreach ($f in '_btnMerge', '_btnEditReview', '_btnKeepBoth') { $part[$f] = $dialogType.GetField($f, $private).GetValue($dlg) }
    return $part
}
function Close-Dialog($w) { if (-not $w.Form.IsDisposed) { $w.Form.Close(); $w.Form.Dispose() } }
# What pressing a key in the dialog does: (handled, the dialog's result).
function Press([System.Windows.Forms.Keys]$keys) {
    $w = Open-Dialog
    $handled = [bool]$tryKey.Invoke($w.Form, @($keys))
    $result = [string]$w.Form.DialogResult
    Close-Dialog $w
    return @($handled, $result)
}
$K = [System.Windows.Forms.Keys]

Write-Host "1 Each button names its key, and the underline shows without pressing Alt"
$w = Open-Dialog
Check ($w._btnMerge.Text -eq 'Add as &Synonym') "Add as Synonym: S ($($w._btnMerge.Text))"
Check ($w._btnEditReview.Text -eq ('Add && &Edit' + [char]0x2026)) "Add & Edit: E ($($w._btnEditReview.Text))"
Check ($w._btnKeepBoth.Text -eq '&Keep Both') "Keep Both: K ($($w._btnKeepBoth.Text))"
foreach ($f in '_btnMerge', '_btnEditReview', '_btnKeepBoth') {
    Check ([bool]$cues.GetValue($w[$f], $null)) "$f shows its underline from the start"
}
$letters = @('_btnMerge', '_btnEditReview', '_btnKeepBoth') | ForEach-Object { $t = $w[$_].Text.Replace('&&', ''); $t.Substring($t.IndexOf('&') + 1, 1).ToUpper() }
Check (($letters | Select-Object -Unique).Count -eq 3) "no two buttons share a letter: $($letters -join ', ')"
Check ($w.Form.AcceptButton -eq $w._btnMerge) "Enter is still Add as Synonym"
$fits = $true
foreach ($f in '_btnMerge', '_btnEditReview', '_btnKeepBoth') {
    $b = $w[$f]; $need = [System.Windows.Forms.TextRenderer]::MeasureText($b.Text.Replace('&&', '&').Replace('&', ''), $b.Font).Width
    if ($need -gt $b.Width - 8) { $fits = $false }
}
Check $fits "every label still fits its button"
Close-Dialog $w

Write-Host "2 A letter alone does what its button does"
$r = Press $K::S; Check ($r[0] -and $r[1] -eq 'Yes') "S: Add as Synonym ($($r[1]))"
$r = Press $K::E; Check ($r[0] -and $r[1] -eq 'Retry') "E: Add & Edit ($($r[1]))"
$r = Press $K::K; Check ($r[0] -and $r[1] -eq 'No') "K: Keep Both ($($r[1]))"

Write-Host "3 So does Alt with the letter"
$r = Press ($K::Alt -bor $K::S); Check ($r[0] -and $r[1] -eq 'Yes') "Alt+S: Add as Synonym ($($r[1]))"
$r = Press ($K::Alt -bor $K::E); Check ($r[0] -and $r[1] -eq 'Retry') "Alt+E: Add & Edit ($($r[1]))"
$r = Press ($K::Alt -bor $K::K); Check ($r[0] -and $r[1] -eq 'No') "Alt+K: Keep Both ($($r[1]))"

Write-Host "4 Other keys do nothing"
foreach ($keys in @($K::A, $K::C, $K::X, ($K::Control -bor $K::S), ($K::Shift -bor $K::S), ($K::Control -bor $K::K), $K::D1)) {
    $r = Press $keys
    Check ((-not $r[0]) -and $r[1] -eq 'None') "$keys is not handled and decides nothing ($($r[1]))"
}

Write-Host "5 A button that is switched off does not answer to its key"
$w = Open-Dialog
$w._btnKeepBoth.Enabled = $false
$handled = [bool]$tryKey.Invoke($w.Form, @($K::K))
Check ((-not $handled) -and [string]$w.Form.DialogResult -eq 'None') "K with Keep Both disabled: nothing happens"
Close-Dialog $w

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
