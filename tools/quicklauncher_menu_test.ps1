# QuickLauncher menu (Alt+Q): the fixed top section (18.20.209 / 19.20.209).
# Checked against the built plugin DLL, outside Studio, on a real
# ContextMenuStrip shown off-screen:
#
#   A  three entries, in order: Translate active segment, FuzzyFixer, Proofread
#      active segment, each with its shipped shortcut shown.
#   B  with the menu open, the keys T, F and P run them (and only them); a key
#      that belongs to none runs nothing; clicking runs the one clicked.
#   C  a prompt below the section whose name starts with one of the letters does
#      not take the key; one that claims a letter with '&' makes the key move
#      between the two instead of running either.
#   D  the action behind each entry is the one the Editor's own action starts
#      (read from the source).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\quicklauncher_menu_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Release build)
#
# Makes no AI request, reads no settings, writes nothing.
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

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System; using System.IO; using System.Reflection; using System.Collections.Generic; using System.Windows.Forms;
public static class QlMenuTest {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
    public static readonly List<string> Ran = new List<string>();
    public static Action<string> Recorder() { return delegate(string id) { Ran.Add(id); }; }
    // The key as the open menu gets it from the keyboard.
    public static bool Key(ToolStrip menu, char c) {
        var m = typeof(Control).GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic);
        return (bool)m.Invoke(menu, new object[] { c });
    }
}
"@
[QlMenuTest]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

$any = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$menuType = $asm.GetType('Supervertaler.Trados.Core.QuickLauncherMenu', $true)
$add = $menuType.GetMethod('AddBuiltIns', $any)

function NewMenu([string[]]$promptNames) {
    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripMenuItem 'Supervertaler QuickLauncher'))
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void]$add.Invoke($null, @($menu.Items, [QlMenuTest]::Recorder()))
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    foreach ($n in $promptNames) {
        $item = New-Object System.Windows.Forms.ToolStripMenuItem $n
        $item.Tag = 'prompt:' + $n
        $item.add_Click({ param($s, $e) [QlMenuTest]::Ran.Add([string]$s.Tag) })
        [void]$menu.Items.Add($item)
    }
    $menu.Show((New-Object System.Drawing.Point(-32000, -32000)))
    [System.Windows.Forms.Application]::DoEvents()
    return $menu
}
function Press($menu, [char]$c) { $handled = [QlMenuTest]::Key($menu, $c); [System.Windows.Forms.Application]::DoEvents(); return $handled }
function Ran { $r = [QlMenuTest]::Ran -join ','; [QlMenuTest]::Ran.Clear(); return $r }

Write-Host "A  the section"
$menu = NewMenu @('Translate selection in context of current project', 'Define', 'Make it sound better')
$own = @($menu.Items | Where-Object { $_ -is [System.Windows.Forms.ToolStripMenuItem] -and $_.Tag -and -not ([string]$_.Tag).StartsWith('prompt:') })
Check ($own.Count -eq 3) "A three entries ($($own.Count))"
Check ((($own | ForEach-Object { $_.Text }) -join ' | ') -eq '&Translate active segment | &FuzzyFixer | &Proofread active segment') "A in the order Translate, FuzzyFixer, Proofread: $(($own | ForEach-Object { $_.Text.Replace('&', '') }) -join ' | ')"
Check ((($own | ForEach-Object { $_.ShortcutKeyDisplayString }) -join ' | ') -eq 'Alt+T | Ctrl+Alt+F | Ctrl+Alt+P') "A each shows its shipped shortcut: $(($own | ForEach-Object { $_.ShortcutKeyDisplayString }) -join ' | ')"
Check ($menu.Items.IndexOf($own[0]) -eq 2 -and ($menu.Items[5] -is [System.Windows.Forms.ToolStripSeparator])) "A it sits under the header, with a line before the prompts"
Check (-not ($own | Where-Object { [string]::IsNullOrWhiteSpace($_.ToolTipText) })) "A each has a tooltip"
$menu.Close()

Write-Host "B  the keys"
foreach ($k in @(@('t', 'translate'), @('F', 'fuzzyfixer'), @('p', 'proofread'))) {
    $menu = NewMenu @('Translate selection in context of current project', 'Find a term', 'Proofread it again')
    [void](Ran)
    $handled = Press $menu ([char]$k[0])
    $ran = Ran
    Check ($handled -and ($ran -eq $k[1])) "B key $($k[0]): runs $($k[1]) and nothing else ($ran)"
    $menu.Close(); [System.Windows.Forms.Application]::DoEvents()
}
$menu = NewMenu @('Define')
[void](Ran)
$handled = Press $menu ([char]'x')
Check ((Ran) -eq '') "B a key that belongs to nothing: nothing runs"
$own = @($menu.Items | Where-Object { $_.Tag -eq 'fuzzyfixer' })
$own[0].PerformClick(); [System.Windows.Forms.Application]::DoEvents()
Check ((Ran) -eq 'fuzzyfixer') "B a click on FuzzyFixer runs FuzzyFixer"
$menu.Close()

Write-Host "C  prompts with the same letters"
$menu = NewMenu @('Translate selection in context of current project', 'Fix the tags', 'Polish this')
[void](Ran)
[void](Press $menu ([char]'t')); $r1 = Ran
Check ($r1 -eq 'translate') "C a prompt that merely starts with T does not take the key: $r1"
$menu.Close(); [System.Windows.Forms.Application]::DoEvents()
$menu = NewMenu @('&Fix the tags')
[void](Ran)
[void](Press $menu ([char]'f')); $r2 = Ran
Check ($r2 -eq '') "C a prompt that claims F with '&': the key only moves the highlight, nothing runs by surprise ($r2)"
$menu.Close(); [System.Windows.Forms.Application]::DoEvents()
$empty = New-Object System.Windows.Forms.ContextMenuStrip
[void]$add.Invoke($null, @($empty.Items, $null))
$empty.Items[0].PerformClick()
Check ($empty.Items.Count -eq 3) "C no callback given: the entries are there and a click does not throw"
[void]$add.Invoke($null, @($null, [QlMenuTest]::Recorder()))
Check ($true) "C no menu given: nothing happens"

Write-Host "D  what each entry starts (source)"
$src = Join-Path $root 'src\Supervertaler.Trados'
if (Test-Path $src) {
    $ql = [IO.File]::ReadAllText((Join-Path $src 'QuickLauncherAction.cs'))
    foreach ($pair in @(@('translate', 'HandleTranslateActiveSegment', 'TranslateActiveSegmentAction.cs'), @('fuzzyfixer', 'HandleFuzzyFixActiveSegment', 'FuzzyFixActiveSegmentAction.cs'), @('proofread', 'HandleProofreadActiveSegment', 'ProofreadActiveSegmentAction.cs'))) {
        $own = [IO.File]::ReadAllText((Join-Path $src $pair[2]))
        Check ($ql.Contains('id == "' + $pair[0] + '") AiAssistantViewPart.' + $pair[1] + '();') -and $own.Contains('AiAssistantViewPart.' + $pair[1] + '();')) "D $($pair[0]) starts $($pair[1]), as the Editor's own action does"
    }
    Check ($ql.IndexOf('HasAssistantAccess') -lt $ql.IndexOf('QuickLauncherMenu.AddBuiltIns')) "D the licence is checked before the menu is built"
    Check (-not $ql.Contains('No QuickLauncher prompts are configured')) "D with no prompts the menu still opens (the old message box is gone)"
} else { Write-Host "  (source not beside this script: section D skipped)" }

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
