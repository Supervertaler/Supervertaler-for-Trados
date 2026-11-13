# FigureLens window: what it offers and says when the documents hold no images
# and the drawings are files in the chosen folder - checked against the built
# plugin DLL, outside Studio. The window is built from a state the test makes
# up; no project, no folder, no memory bank and no AI are involved.
#
# The reason for it (13 Nov 2026): a patent whose four drawings came as
# Figure 1.png ... Figure 4.png. The window showed "(4 image files)" for the
# folder and kept both step-2 buttons grey with "No images to describe."
#
# The folder scan, the file-name matching, the size limits and the run itself
# (stop, every request failing, 200 files) are tested in Plugin Core:
#   core\tests\FolderFiguresTests.cs
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\figurelens_folder_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\figurelens_folder_test.ps1
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
public static class FigureLensTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[FigureLensTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$stateType = $asm.GetType('Supervertaler.Trados.Controls.ImagesState', $true)
$actionsType = $asm.GetType('Supervertaler.Trados.Controls.ImagesActions', $true)
$dialogType = $asm.GetType('Supervertaler.Trados.Controls.ImagesDialog', $true)
$rowType = $asm.GetType('Supervertaler.Trados.Controls.DocumentRow', $true)
$private = [System.Reflection.BindingFlags]'Instance,NonPublic'

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# A project with one Word document that holds no images, a memory bank of its
# own, and whatever the case sets on top.
function New-State([hashtable]$set) {
    $st = [Activator]::CreateInstance($stateType)
    $base = @{
        ProjectOpen = $true; ProjectName = 'Acme PROJ-001'; DocumentCount = 1
        BankName = 'acme-proj-001'; SuggestedBankName = 'acme-proj-001'; ProviderName = 'Claude (Anthropic)'
    }
    foreach ($k in $set.Keys) { $base[$k] = $set[$k] }
    foreach ($k in $base.Keys) { $stateType.GetField($k).SetValue($st, $base[$k]) }
    $row = [Activator]::CreateInstance($rowType)
    $rowType.GetField('Name').SetValue($row, 'PROJ-001 description.docx')
    $rowType.GetField('Note').SetValue($row, 'no images')
    if ($base['TotalImages'] -gt 0) { $stateType.GetField('Documents').GetValue($st).Add($row) }
    else { $stateType.GetField('DocumentsWithoutImages').GetValue($st).Add($row) }
    return $st
}

# The window, shown off-screen so that Visible means something, and its parts.
function Open-Window($state, $actions) {
    $dlg = [Activator]::CreateInstance($dialogType, @($actions, $state))
    $dlg.StartPosition = 'Manual'
    $dlg.Location = New-Object System.Drawing.Point(-32000, -32000)
    $dlg.Show()
    $part = @{ Form = $dlg }
    foreach ($f in '_btnExtract', '_lblExtractNote', '_lblFolder', '_btnAnalyse', '_lblAnalyseNote', '_btnWrite', '_lblWriteNote', '_lblOutcome', '_lblDocs') {
        $part[$f] = $dialogType.GetField($f, $private).GetValue($dlg)
    }
    return $part
}
function Close-Window($w) { $w.Form.Close(); $w.Form.Dispose() }
function Click($button) {
    [System.Windows.Forms.Button].GetMethod('OnClick', $private).Invoke($button, @([EventArgs]::Empty))
}

$folder = 'D:\Jobs\Acme\PROJ-001\Drawings'

Write-Host "1  No images in the document, four drawing files in the folder, all named for their figure"
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 4; TextFigures = 4 }) $null
Check $w._btnAnalyse.Enabled "Describe images with AI is available"
Check ($w._btnAnalyse.Text -eq 'Describe images with AI') "and is called that"
Check ($w._lblAnalyseNote.Text -like '4 AI requests to Claude (Anthropic), one per image file in the folder.*') "the cost is stated before it is spent: $($w._lblAnalyseNote.Text)"
Check ($w._lblAnalyseNote.Text -like '*All are matched to a figure by file name.*') "and how the files were matched"
Check ($w._lblDocs.Text -like '*No images in the 1 document*The 4 image files in the folder below will be used.*') "the documents line says the folder's files will be used: $($w._lblDocs.Text)"
Check (-not $w._btnExtract.Enabled) "step 1 is off"
Check ($w._lblExtractNote.Text -like '*not needed*already in the folder*') "and says it is not needed"
Check $w._btnWrite.Enabled "Describe from the text only is available"
Check ($w._lblWriteNote.Text -like '*each of the 4 figures it describes*Needs no image files*') "and says what it writes: $($w._lblWriteNote.Text)"
Check (-not $w._lblOutcome.Visible) "no outcome line when there is nothing to report"
Close-Window $w

Write-Host "2  One of the four files has no figure number in its name; a figure in the text has no file; a PDF lies beside them"
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 3; TextFigures = 4; FiguresWithoutFile = 1; FolderOtherFiles = 1 }) $null
Check $w._btnAnalyse.Enabled "still available: a file that matches nothing is described all the same"
Check ($w._lblAnalyseNote.Text -like '*3 matched to a figure by file name; 1 with no figure number in the name will be described without one.*') "the unmatched file is announced: $($w._lblAnalyseNote.Text)"
Check ($w._lblAnalyseNote.Text -like '*1 figure in the text has no file.*') "and so is the figure without a file"
Check ($w._lblFolder.Text -like '*(4 image files; 1 other file not used: PDF and other formats are not read)*') "the folder line says the PDF is not used: $($w._lblFolder.Text)"
Close-Window $w

Write-Host "3  The folder is empty"
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 0; TextFigures = 4 }) $null
Check (-not $w._btnAnalyse.Enabled) "nothing to describe"
Check ($w._lblAnalyseNote.Text -like '*none in the documents, and no image files in the folder*') "and it says where it looked: $($w._lblAnalyseNote.Text)"
Check $w._btnWrite.Enabled "the text-only button needs no images, so it stays available"
Close-Window $w

Write-Host "4  The folder is gone"
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = -1 }) $null
Check (-not $w._btnAnalyse.Enabled) "nothing to describe"
Check ($w._lblAnalyseNote.Text -like '*the folder no longer exists*') "and it says so: $($w._lblAnalyseNote.Text)"
Check ($w._lblFolder.Text -like '*(no longer exists)*') "on the folder line too"
Check (-not $w._btnWrite.Enabled) "the text describes no figures here, so the text-only button is off"
Check ($w._lblWriteNote.Text -like 'Nothing to write without the AI*') "and says why: $($w._lblWriteNote.Text)"
Close-Window $w

Write-Host "5  No folder chosen yet"
$w = Open-Window (New-State @{ }) $null
Check (-not $w._btnAnalyse.Enabled) "nothing to describe"
Check ($w._lblAnalyseNote.Text -like '*If the images came as separate files, choose their folder under step 1.*') "and it says what to do: $($w._lblAnalyseNote.Text)"
Close-Window $w

Write-Host "6  A folder with more image files than one run takes"
$w = Open-Window (New-State @{ Folder = 'D:\Downloads'; FolderImages = 201; FolderTooMany = $true }) $null
Check (-not $w._btnAnalyse.Enabled) "refused"
Check ($w._lblAnalyseNote.Text -like '*more than 200 image files*Choose a folder that holds only this project*') "with the reason and the way out: $($w._lblAnalyseNote.Text)"
Check ($w._lblFolder.Text -like '*(more than 200 image files)*') "the folder line does not pretend to an exact count"
Close-Window $w

Write-Host "7  Files in the folder, but the active memory bank is the shared one"
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 4; TextFigures = 4; BankIsShared = $true }) $null
Check (-not $w._btnAnalyse.Enabled) "not available without a bank of the project's own"
Check ($w._lblAnalyseNote.Text -like 'Needs a memory bank*') "and says so"
Check (-not $w._btnWrite.Enabled) "nor is the text-only button"
Close-Window $w

Write-Host "8  The documents DO hold images: nothing changes, and the folder's own files are not counted"
$w = Open-Window (New-State @{ TotalImages = 3; Labelled = 3; Folder = $folder; FolderImages = 99; FolderMatched = 0; FolderOtherFiles = 5 }) $null
Check $w._btnAnalyse.Enabled "available"
Check ($w._lblAnalyseNote.Text -eq '3 AI requests to Claude (Anthropic), one per image.') "three requests, for the three images in the documents: $($w._lblAnalyseNote.Text)"
Check ($w._lblDocs.Text -notlike '*will be used*') "the documents line says nothing about the folder's files"
Check $w._btnExtract.Enabled "step 1 is available"
Check ($w._lblFolder.Text -like '*(99 image files)') "the folder line is as it was"
Close-Window $w

Write-Host "9  While a run is on, the button stops it"
$script:calls = @()
$actions = [Activator]::CreateInstance($actionsType)
$actionsType.GetField('Analyse').SetValue($actions, [Action] { $script:calls += 'analyse' })
$actionsType.GetField('Stop').SetValue($actions, [Action] { $script:calls += 'stop' })
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 4; TextFigures = 4; AnalysisRunning = $true; LastOutcome = 'left over from an earlier run' }) $actions
Check ($w._btnAnalyse.Text -eq 'Stop') "it is called Stop"
Check $w._btnAnalyse.Enabled "and can be pressed"
Check ($w._lblAnalyseNote.Text -like 'Running*saves nothing*') "the note says a stopped run saves nothing: $($w._lblAnalyseNote.Text)"
Check (-not $w._btnWrite.Enabled) "the text-only button is off meanwhile"
Check (-not $w._lblOutcome.Visible) "an earlier run's outcome is not shown during the next"
Click $w._btnAnalyse
Check (($script:calls -join ',') -eq 'stop') "pressing it stops the run and starts no second one: $($script:calls -join ',')"
Close-Window $w

Write-Host "10 When no run is on, the same button starts one"
$script:calls = @()
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 4; TextFigures = 4 }) $actions
Click $w._btnAnalyse
Check (($script:calls -join ',') -eq 'analyse') "pressing it starts the run: $($script:calls -join ',')"
Close-Window $w

Write-Host "11 A run that was stopped, or in which nothing could be described, says so in the window"
$outcome = 'None of the 4 image(s) could be described. figures.md was not changed. Reason given for the first: 401: invalid API key'
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 4; TextFigures = 4; LastOutcome = $outcome; LastOutcomeIsError = $true }) $null
Check $w._lblOutcome.Visible "the outcome line is shown"
Check ($w._lblOutcome.ForeColor.ToArgb() -eq [System.Drawing.Color]::Firebrick.ToArgb()) "in red, because it is a failure"
Check ($w._lblOutcome.Text -eq $outcome) "with the whole message, including that figures.md was not changed"
Check $w._btnAnalyse.Enabled "and the run can be tried again"
Close-Window $w
$done = 'Described 4 image(s). Written to D:ankscme-proj-001igures.md'
$w = Open-Window (New-State @{ Folder = $folder; FolderImages = 4; FolderMatched = 4; TextFigures = 4; LastOutcome = $done }) $null
Check ($w._lblOutcome.Visible -and $w._lblOutcome.Text -eq $done) "a run that succeeded says so too"
Check ($w._lblOutcome.ForeColor.ToArgb() -ne [System.Drawing.Color]::Firebrick.ToArgb()) "and not in red: it is not an error"
Close-Window $w

Write-Host "12 The text describes figures but no folder is chosen: the text-only button alone is available"
$w = Open-Window (New-State @{ TextFigures = 3 }) $null
Check $w._btnWrite.Enabled "Describe from the text only is available"
Check (-not $w._btnAnalyse.Enabled) "Describe images with AI is not"
Close-Window $w

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
