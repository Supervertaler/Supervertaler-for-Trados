# A Batch Translate run's progress window: its own small window with a Stop
# button, because during a run Studio keeps Translation Results in front of the
# Supervertaler Assistant pane and the tab's Stop button cannot be reached
# (Michael, 16 Nov 2026).
#
# Two parts. The window itself, run from the built plugin DLL outside Studio:
# Stop caption, progress against a changing total, the Stop click, closing.
# And the view part's source (read, not run - it needs Studio): the window is
# opened once, where a run starts, and closed on every way out of a run.
#
#   powershell -NoProfile -File tools\run_progress_window_test.ps1 [-Dll <Supervertaler.Trados.dll>]
#
# Makes no AI request and touches no data folder.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    # Release builds only: a Debug build left behind is usually stale.
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Release') } else { @('Studio18\Release', 'Studio17\Release') }
    foreach ($c in $want) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Release Supervertaler.Trados.dll found)" }
$Dll = (Resolve-Path -LiteralPath $Dll).Path
$studio = if ($Dll -match '\\Studio19\\') { "C:\Program Files\Trados\Trados Studio\Studio19" }
          elseif ($Dll -match '\\Studio17\\') { "C:\Program Files (x86)\Trados\Trados Studio\Studio17" }
          else { "C:\Program Files (x86)\Trados\Trados Studio\Studio18" }
Write-Host "Plugin: $Dll ($([IntPtr]::Size * 8)-bit process)"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class RunProgressTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[RunProgressTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}
$private = [System.Reflection.BindingFlags]'Instance,NonPublic,Public'
function Field($o, [string]$n) { $o.GetType().GetField($n, $private).GetValue($o) }

Write-Host "1 The window"
$type = $asm.GetType('Supervertaler.Trados.Controls.ReimportProgressForm', $true)
$ctor = $type.GetConstructor($private, $null, [Type[]]@([string], [string], [int]), $null)
$form = $ctor.Invoke(@('Supervertaler - Batch Translate', 'Translating 105 segment(s)...', 105))
try {
    $form.CancelText = 'Stop'
    $form.CancellingStatus = 'Stopping - finishing the current batch...'
    $form.TopMost = $false
    $form.StartPosition = 'Manual'
    $form.Location = New-Object System.Drawing.Point(-32000, -32000)
    $form.Show()
    $bar = Field $form '_bar'; $status = Field $form '_status'; $cancel = Field $form '_cancel'
    Check ($cancel.Text -eq 'Stop') "the button says Stop"
    Check ($bar.Maximum -eq 105 -and $bar.Value -eq 0) "the bar starts at 0 of 105"
    Check (-not $form.TopMost) "it does not stay above other programs"

    $form.SetProgress(10, 105, 'Translating batch 2/11 (segments 11-20)...')
    Check ($bar.Value -eq 10 -and $status.Text -like 'Translating batch 2/11*') "a batch line moves the bar and shows ($($bar.Value))"
    $form.SetProgress(20, 105, $null)
    Check ($bar.Value -eq 20 -and $status.Text -like 'Translating batch 2/11*') "a step without a batch line moves the bar and keeps the line"
    $form.SetProgress(30, 60, $null)
    Check ($bar.Maximum -eq 60 -and $bar.Value -eq 30) "a changed total is taken over ($($bar.Value) of $($bar.Maximum))"
    $form.SetProgress(999, 60, $null)
    Check ($bar.Value -eq 60) "a value past the total stops at the total, no exception"
    $form.SetProgress(5, 0, $null)
    Check ($bar.Maximum -eq 60 -and $bar.Value -eq 5) "a total of 0 leaves the total alone"

    $script:stops = 0
    $handler = [EventHandler]{ $script:stops++ }
    $form.add_CancelRequested($handler)
    $cancel.PerformClick()
    Check ($script:stops -eq 1) "clicking Stop asks for the stop, once"
    Check (-not $cancel.Enabled -and $status.Text -like 'Stopping*') "the button greys out and the line says it is stopping"
    $cancel.PerformClick()
    Check ($script:stops -eq 1) "a second click asks nothing more"

    $form.Finish()
    Check ($form.IsDisposed) "Finish closes and disposes the window"
    $threw = $false; try { $form.Finish(); $form.SetProgress(1, 2, 'x') } catch { $threw = $true }
    Check (-not $threw) "Finish and SetProgress on a closed window do nothing, no exception"
}
finally { if (-not $form.IsDisposed) { $form.Dispose() } }

Write-Host "2 The view part's source (read, not run)"
$src = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\AiAssistantViewPart.cs'))
function Body([string]$signature) {
    $s = $src.IndexOf($signature)
    if ($s -lt 0) { return $null }
    $n = [regex]::Match($src.Substring($s + 10), '\r?\n        (private|public|internal|protected) ')
    return $src.Substring($s, 10 + $n.Index)
}
Check ([regex]::Matches($src, '(?<!void )ShowRunProgress\(').Count -eq 1) "the window is opened in one place"
Check ($src -match 'ShowRunProgress\(segments\.Count\);\s*Task\.Run\(async \(\) =>') "that place is the last step before a Batch Translate run starts"

$completed = Body 'private void OnBatchCompleted(object sender, BatchCompletedEventArgs e)'
Check ($completed -match '^private void OnBatchCompleted\([^)]*\)\s*\{\s*(//[^\n]*\s*)*CloseRunProgress\(\);') "a run that ends (finished, stopped, failed) closes it first, before anything can throw"
$release = Body 'private void ReleaseBatchTranslator(BatchTranslator translator)'
Check ($release -match 'CloseRunProgress\(\);') "a run that ends while still preparing closes it"
$dispose = Body 'public override void Dispose()'
Check ($dispose -match 'CloseRunProgress\(\);') "closing the pane closes it"
$close = Body 'private void CloseRunProgress()'
Check ($close -match 'Interlocked\.Exchange\(ref _runProgress, null\)' -and $close -match 'form\.Finish\(\);') "closing takes the window out of the field first, so a second call finds nothing"
$show = Body 'private void ShowRunProgress(int segmentCount)'
Check ($show -match 'form\.CancelRequested \+= OnBatchStopRequested;') "its Stop button asks for the same stop as the tab's"
Check ($show -match 'catch \(Exception ex\)\s*\{[^}]*CloseRunProgress\(\);') "a window that cannot be shown is closed again and the run goes on"
$altT = Body 'public static void HandleTranslateActiveSegment()'
Check (-not ($altT -match 'ShowRunProgress')) "Alt+T opens no window"

Write-Host ""
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
