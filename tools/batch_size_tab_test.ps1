# Batch Operations tab: the Batch size box beside Limit - checked against the
# built plugin DLL, outside Studio, with the real BatchTranslateControl.
#
# What it must do: show the stored batch size; hold it to what this Studio
# takes (5 to 100; a 32-bit Studio 10) WITHOUT reporting that as a change the
# translator made, so a 32-bit Studio showing 10 never overwrites the 50 the
# 64-bit one uses; report a change the translator does make, once; and be
# locked while a run is going.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\batch_size_tab_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\batch_size_tab_test.ps1
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set), deleted
# at the end. Reads and writes no settings file.
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
$is32 = [IntPtr]::Size -eq 4
Write-Host "Plugin: $Dll ($([IntPtr]::Size * 8)-bit process)"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class BatchSizeTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[BatchSizeTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# Its own data folder, before anything of the plugin runs.
$temp = Join-Path $env:TEMP ("sv-batch-size-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
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

try {
    $controlType = $asm.GetType('Supervertaler.Trados.Controls.BatchTranslateControl', $true)
    $private = [System.Reflection.BindingFlags]'Instance,NonPublic'
    $host2 = New-Object System.Windows.Forms.Form
    $host2.StartPosition = 'Manual'
    $host2.Location = New-Object System.Drawing.Point(-32000, -32000)
    $host2.Size = New-Object System.Drawing.Size(1200, 900)
    $batch = [Activator]::CreateInstance($controlType)
    $batch.Dock = 'Fill'
    $host2.Controls.Add($batch)
    $host2.Show(); [System.Windows.Forms.Application]::DoEvents()

    $nud = $controlType.GetField('_nudBatchSize', $private).GetValue($batch)
    $lbl = $controlType.GetField('_lblBatchSizeLabel', $private).GetValue($batch)
    $limit = $controlType.GetField('_nudMaxSegments', $private).GetValue($batch)
    $max = if ($is32) { 10 } else { 100 }
    $script:changes = 0
    $batch.add_BatchSizeChanged({ $script:changes++ })

    Write-Host "1 The box is on the tab, beside Limit"
    Check ($null -ne $nud -and $nud.Visible) "there is a Batch size box and it is visible"
    Check ($lbl.Text -eq 'Batch size:') "its label reads 'Batch size:' ($($lbl.Text))"
    Check ($lbl.Left -gt $limit.Right -and $nud.Left -ge $lbl.Right) "label and box sit to the right of Limit, not on top of it or of each other"
    Check ([Math]::Abs($nud.Top - $limit.Top) -le 1) "on the same row as Limit"
    Check ($nud.Right -le $batch.ClientSize.Width) "inside the tab at this width ($($nud.Right) of $($batch.ClientSize.Width))"

    Write-Host "2 Its range is what this Studio takes"
    Check ([int]$nud.Minimum -eq 5) "smallest is 5"
    Check ([int]$nud.Maximum -eq $max) "largest is $max in this $([IntPtr]::Size * 8)-bit process ($([int]$nud.Maximum))"

    Write-Host "3 Showing the stored setting is not a change the translator made"
    $batch.BatchSize = 8
    Check ($batch.BatchSize -eq 8 -and [int]$nud.Value -eq 8) "a stored 8 is shown as 8"
    $batch.BatchSize = 50
    $want = [Math]::Min(50, $max)
    Check ($batch.BatchSize -eq $want) "a stored 50 is shown as $want"
    $batch.BatchSize = 500
    Check ($batch.BatchSize -eq $max) "a stored 500 is held to $max"
    $batch.BatchSize = 2
    Check ($batch.BatchSize -eq 5) "a stored 2 is raised to 5"
    $batch.BatchSize = 0
    Check ($batch.BatchSize -eq [Math]::Min(20, $max)) "a stored 0 (never set) shows the default, $([Math]::Min(20, $max))"
    Check ($script:changes -eq 0) "none of that was reported as a change ($($script:changes))"

    Write-Host "4 A change made in the box is reported, once"
    $batch.BatchSize = 5
    $nud.Value = 7
    Check ($script:changes -eq 1) "typing 7 is reported once ($($script:changes))"
    Check ($batch.BatchSize -eq 7) "and the tab now says 7"
    $nud.Value = 7
    Check ($script:changes -eq 1) "setting the same value again reports nothing ($($script:changes))"
    $batch.BatchSize = 9
    Check ($script:changes -eq 1) "a later refresh from the setting is still not a change ($($script:changes))"

    Write-Host "5 Locked while a run is going"
    $batch.SetRunning($true)
    Check (-not $nud.Enabled) "disabled during a run"
    $batch.SetRunning($false)
    Check ($nud.Enabled) "enabled again afterwards"

    $host2.Close(); $host2.Dispose()
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
