# Reports tab: AI requests in time order, oldest at the top and newest at the
# bottom, like the chat - checked against the built plugin DLL, outside Studio,
# with the real ReportsControl.
#
# What it must do: put each new request card at the bottom; scroll to it if the
# view was at the bottom (or nothing scrolled yet), and leave the view alone if
# the translator has scrolled up; keep 500 cards, dropping the oldest without
# moving the view; with a proofreading report, list its findings first and the
# request cards after them, and show a report from its top; Clear empties it.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\reports_order_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Release build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\reports_order_test.ps1
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set), deleted
# at the end. Makes no AI request; every card's content is invented.
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

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class ReportsOrderTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[ReportsOrderTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# Its own data folder, before anything of the plugin runs.
$temp = Join-Path $env:TEMP ("sv-reports-order-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
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

$entryType = $asm.GetType('Supervertaler.Core.Models.PromptLogEntry', $true)
$featureType = $asm.GetType('Supervertaler.Core.Models.PromptLogFeature', $true)
$issueType = $asm.GetType('Supervertaler.Trados.Models.ProofreadingIssue', $true)
$reportType = $asm.GetType('Supervertaler.Trados.Models.ProofreadingReport', $true)
$controlType = $asm.GetType('Supervertaler.Trados.Controls.ReportsControl', $true)
$private = [System.Reflection.BindingFlags]'Instance,NonPublic'

$script:clock = [DateTime]::Today.AddHours(9)
$script:n = 0
function New-Entry {
    $script:n++
    $script:clock = $script:clock.AddSeconds(7)
    $e = [Activator]::CreateInstance($entryType)
    $e.Timestamp = $script:clock
    $e.Feature = [Enum]::Parse($featureType, 'BatchTranslate')
    $e.PromptName = 'PROJ-001 house style'
    $e.Provider = 'Acme AI'
    $e.Model = 'acme-model-1'
    $e.DisplayModel = 'Acme Model 1'
    # Eight lines, so the opened box is taller than the rest of the card.
    $e.SystemPrompt = "You translate for Acme (PROJ-001).`nRequest $($script:n).`nKeep tags.`nUse the termbase.`nDutch (NL).`nNo comments.`nKeep numbers.`nEnd."
    $e.UserPrompt = "Translate segment $($script:n) of PROJ-001: The valve is closed."
    $e.Response = "Het ventiel is gesloten. ($($script:n))"
    $e.EstimatedInputTokens = 120
    $e.EstimatedOutputTokens = 30
    $e.Duration = [TimeSpan]::FromSeconds(1.5)
    return $e
}

function New-Report([int]$count) {
    $r = [Activator]::CreateInstance($reportType)
    $r.TotalSegmentsChecked = $count * 2
    for ($i = 1; $i -le $count; $i++) {
        $x = [Activator]::CreateInstance($issueType)
        $x.SegmentIndex = $i - 1
        $x.SegmentNumber = $i
        $x.FileName = 'Acme - PROJ-001.docx'
        $x.SourceText = "The valve $i is closed when the pressure exceeds 17,1 bar."
        $x.TargetText = "Het ventiel $i is gesloten."
        $x.IsOk = $false
        $x.IssueDescription = "Segment $i leaves out the pressure condition of the source."
        $x.Suggestion = "Add 'wanneer de druk 17,1 bar overschrijdt'."
        $x.ParagraphUnitId = "pu$i"
        $x.SegmentId = "$i"
        $r.Issues.Add($x)
    }
    return $r
}

try {
    $form = New-Object System.Windows.Forms.Form
    $form.StartPosition = 'Manual'
    # On screen but fully transparent: in a 32-bit process, cards in a form
    # parked at -32000 sometimes came back from Windows at a wrong position
    # (X 7224, though the code sets 4) - a coordinate wrap of the parking
    # place, not of the code, which a form on screen does not have.
    $form.Location = New-Object System.Drawing.Point(0, 0)
    $form.Opacity = 0
    $form.Size = New-Object System.Drawing.Size(520, 640)
    $form.ShowInTaskbar = $false
    $reports = [Activator]::CreateInstance($controlType)
    $reports.Dock = 'Fill'
    $form.Controls.Add($reports)
    $form.Show(); [System.Windows.Forms.Application]::DoEvents()

    $cards = $controlType.GetField('_promptCards', $private).GetValue($reports)
    $panel = $controlType.GetField('_resultsPanel', $private).GetValue($reports)
    $fScroll = $controlType.GetField('_scrollY', $private)
    $boundCards = $controlType.GetField('_boundCards', $private).GetValue($reports)
    $empty = $controlType.GetField('_lblEmpty', $private).GetValue($reports)
    $mScrollTo = $controlType.GetMethod('ScrollFindingsTo', $private)
    $mArrange = $controlType.GetMethod('Arrange', $private)
    $mListHeight = $controlType.GetMethod('ListHeight', $private)
    function ScrollY { [int]$fScroll.GetValue($reports) }
    function ScrollTo([int]$y) { $mScrollTo.Invoke($reports, @([int]$y)) | Out-Null }
    function MaxScroll { [Math]::Max(0, [int]$mListHeight.Invoke($reports, $null) - $panel.ClientSize.Height) }
    function Add { $reports.AddPromptLog((New-Entry)) }
    function Newest { $cards[$cards.Count - 1] }
    function InView($c) { $c.Visible -and $c.Top -ge 0 -and $c.Bottom -le $panel.ClientSize.Height }
    $viewH = $panel.ClientSize.Height

    Write-Host "1 A few requests: oldest at the top, newest at the bottom"
    Add; Add; Add
    Check ($cards.Count -eq 3) "three cards ($($cards.Count))"
    $inOrder = $true
    for ($i = 1; $i -lt $cards.Count; $i++) {
        if (-not ($cards[$i].Tag.Timestamp -gt $cards[$i - 1].Tag.Timestamp)) { $inOrder = $false }
        if (-not ($cards[$i].Top -gt $cards[$i - 1].Top)) { $inOrder = $false }
    }
    Check $inOrder "each card is later in time and lower on screen than the one before"
    Check ((ScrollY) -eq 0) "nothing to scroll yet: the view is at the top ($(ScrollY))"
    Check (-not $empty.Visible) "the 'No reports yet' text is gone"

    Write-Host "2 More than fit: the view follows the newest"
    $followOk = $true; $needed = 0
    for ($i = 0; $i -lt 25; $i++) {
        Add
        if ((MaxScroll) -gt 0) {
            $needed++
            if (-not (InView (Newest))) { $followOk = $false }
            if ((ScrollY) -ne (MaxScroll)) { $followOk = $false }
        }
    }
    Check ($needed -gt 5) "the list needed a scroll bar for $needed of the added cards"
    Check $followOk "after each add the view was at the very bottom and the newest card wholly in view"
    $nw = Newest
    Check ($nw.Bottom -le $viewH -and $nw.Bottom -ge $viewH - 12) "the newest card's bottom is at the view's bottom ($($nw.Bottom) of $viewH)"
    $order = $true
    for ($i = 1; $i -lt $cards.Count; $i++) { if (-not ($cards[$i].Tag.Timestamp -gt $cards[$i - 1].Tag.Timestamp)) { $order = $false } }
    Check $order "all $($cards.Count) cards in time order"

    Write-Host "3 Scrolled up to read: a new card does not move the view"
    ScrollTo ((ScrollY) - 400)
    $at = ScrollY
    $reading = $null
    foreach ($c in $cards) { if (InView $c) { $reading = $c; break } }
    $readTop = $reading.Top
    Add; Add
    Check ((ScrollY) -eq $at) "the scroll position stayed at $at ($(ScrollY))"
    Check ($reading.Visible -and $reading.Top -eq $readTop) "the card being read did not move ($readTop -> $($reading.Top))"
    Check (-not (InView (Newest))) "the new card is below the view, not in it"

    Write-Host "4 Within a few pixels of the bottom counts as the bottom"
    ScrollTo ((MaxScroll) - 5)
    Add
    Check ((ScrollY) -eq (MaxScroll) -and (InView (Newest))) "followed to the new card ($(ScrollY) of $(MaxScroll))"

    Write-Host "5 Opening a card's section does not jump the view"
    ScrollTo 1000000
    $nw = Newest
    $toggle = $null
    foreach ($c in $nw.Controls) { if ([string]$c.Tag -eq 'toggle') { $toggle = $c; break } }
    $before = ScrollY; $h0 = $nw.Height
    $toggle.GetType().GetMethod('OnClick', $private).Invoke($toggle, @([EventArgs]::Empty)) | Out-Null
    Check ($nw.Height -gt $h0) "the card grew ($h0 -> $($nw.Height))"
    Check ((ScrollY) -eq $before) "the view stayed where it was ($before -> $(ScrollY))"
    $toggle.GetType().GetMethod('OnClick', $private).Invoke($toggle, @([EventArgs]::Empty)) | Out-Null
    ScrollTo 1000000

    Write-Host "6 Five hundred cards, then the oldest goes"
    $t500 = $null
    while ($cards.Count -lt 499) { Add }
    $sw = [Diagnostics.Stopwatch]::StartNew(); Add; $sw.Stop(); $t500 = $sw.Elapsed.TotalMilliseconds
    Check ($cards.Count -eq 500) "500 cards ($($cards.Count))"
    Check (InView (Newest)) "the 500th is in view at the bottom"
    $sw = [Diagnostics.Stopwatch]::StartNew(); $mArrange.Invoke($reports, $null) | Out-Null; $sw.Stop(); $tArr = $sw.Elapsed.TotalMilliseconds
    Write-Host ("  time: adding the 500th card {0:N1} ms, Arrange with 500 cards {1:N1} ms" -f $t500, $tArr)
    $oldest = $cards[0].Tag.Timestamp; $second = $cards[1].Tag.Timestamp
    $sw = [Diagnostics.Stopwatch]::StartNew(); Add; $sw.Stop(); $t501 = $sw.Elapsed.TotalMilliseconds
    Write-Host ("  time: adding the 501st card (one dropped) {0:N1} ms" -f $t501)
    Check ($cards.Count -eq 500) "still 500 after the 501st ($($cards.Count))"
    Check ($cards[0].Tag.Timestamp -eq $second) "the oldest went; the first is now the old second"
    Check ((Newest).Tag.Timestamp -gt $cards[498].Tag.Timestamp -and (InView (Newest))) "the newest is last, and in view"
    Check ($t500 -lt 500 -and $tArr -lt 500) "adding and arranging at 500 cards take under half a second each"

    Write-Host "7 At the cap, scrolled up: dropping the oldest does not move what is shown"
    ScrollTo ([int]((MaxScroll) / 2))
    $reading = $null
    foreach ($c in $cards) { if (InView $c) { $reading = $c; break } }
    $readTop = $reading.Top
    Add
    Check ($cards.Count -eq 500) "still 500"
    Check ($reading.Visible -and $reading.Top -eq $readTop) "the card being read is where it was ($readTop -> $($reading.Top))"
    ScrollTo 0
    $topCard = $cards[0]; $topAt = $topCard.Top
    Add
    Check ($cards[0].Visible -and $cards[0].Top -eq $topAt) "scrolled to the very top: the (new) oldest card is at the top"

    Write-Host "8 Clear empties the request log"
    $reports.ClearResults()
    Check ($cards.Count -eq 0 -and (ScrollY) -eq 0) "Clear emptied the list"
    Check ($empty.Visible -and $empty.Text -eq 'No reports yet') "and shows 'No reports yet'"

    Write-Host "9 A proofread finishes: its report goes on top, the request log stays below it"
    Add; Add; Add; Add   # the run's own requests
    $before = @($cards | ForEach-Object { $_.Tag })
    $reports.SetResults((New-Report 3))
    [System.Windows.Forms.Application]::DoEvents()
    Check ($cards.Count -eq 4) "the four request cards are still there ($($cards.Count))"
    $same = $true
    for ($i = 0; $i -lt 4; $i++) { if (-not [object]::ReferenceEquals($cards[$i].Tag, $before[$i])) { $same = $false } }
    Check $same "the same requests, in the same time order"
    $findTops = @($boundCards.Values | ForEach-Object { $_.Top })
    Check ($findTops.Count -eq 3) "three finding cards are shown ($($findTops.Count))"
    $lowestFinding = ($boundCards.Values | Measure-Object -Property Bottom -Maximum).Maximum
    Check ($lowestFinding -le $cards[0].Top) "every finding is above the first request card ($lowestFinding <= $($cards[0].Top))"
    Check (($findTops | Measure-Object -Minimum).Minimum -le 8) "the first finding is at the top of the list"
    $tops = $true
    $shown = @($cards | Where-Object { $_.Visible })   # placed only while in view
    for ($i = 1; $i -lt $shown.Count; $i++) { if (-not ($shown[$i].Top -gt $shown[$i - 1].Top)) { $tops = $false } }
    Check ($shown.Count -ge 2 -and $tops) "the request cards in view below the findings are in time order ($($shown.Count) in view)"
    Check ((ScrollY) -eq 0) "the view is at the top of the findings"
    Add; Add
    Check ($cards.Count -eq 6 -and $cards[5].Tag.Timestamp -gt $cards[3].Tag.Timestamp) "later requests are added after them"

    Write-Host "10 A report with nothing to show does not hide the request log"
    $reports.SetResults((New-Report 0))
    Check ($cards.Count -eq 6) "the request cards are kept ($($cards.Count))"
    Check (-not $empty.Visible) "the 'No issues found' text does not cover them"
    Check ($cards[0].Visible -and $cards[0].Top -le 8) "the oldest request card is at the top"

    Write-Host "11 A long report shows from its top, and request cards wait below it"
    ScrollTo 1000000
    $reports.SetResults((New-Report 200))
    Check ((ScrollY) -eq 0) "showing the report puts the view at its top ($(ScrollY))"
    Check ($cards.Count -eq 6) "the request cards are kept ($($cards.Count))"
    Add
    Check ((ScrollY) -eq 0) "a request card added after it does not move the view off the findings ($(ScrollY))"
    $firstShown = $null
    foreach ($kv in $boundCards.GetEnumerator()) { if ($kv.Value.Top -le 8) { $firstShown = $kv.Key } }
    Check ($null -ne $firstShown -and $firstShown.SegmentNumber -eq 1) "segment 1's finding is at the top"
    # Restored (as on a document change) after scrolling down: back to the top, log kept.
    ScrollTo 3000
    $n0 = $cards.Count
    $reports.SetResults((New-Report 200))
    Check ((ScrollY) -eq 0) "re-showing a report after scrolling down puts the view at its top again ($(ScrollY))"
    Check ($cards.Count -eq $n0) "and keeps the request cards ($($cards.Count))"
    Add; Add; Add

    Write-Host "12 At the bottom, under a long report: the newest card stays put while findings are measured"
    ScrollTo 1000000
    $nw = Newest
    Check (InView $nw) "the newest request card is in view at the bottom"
    $nwTop = $nw.Top
    $deadline = [DateTime]::Now.AddSeconds(5)
    while ([DateTime]::Now -lt $deadline) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 20 }
    Check ($nw.Visible -and $nw.Top -eq $nwTop) "after the findings' heights were refined the card is where it was ($nwTop -> $($nw.Top))"
    $visFindings = @($boundCards.Values | Where-Object { $_.Visible })
    $visRequests = @($cards | Where-Object { $_.Visible })
    if ($visFindings.Count -gt 0) {
        $lowestFinding = ($visFindings | Measure-Object -Property Bottom -Maximum).Maximum
        $firstRequest = ($visRequests | Measure-Object -Property Top -Minimum).Minimum
        Check ($lowestFinding -le $firstRequest) "the last findings in view sit above the request cards ($lowestFinding <= $firstRequest)"
    }
    Add
    Check ((ScrollY) -eq (MaxScroll) -and (InView (Newest))) "a new card under the report is followed"

    Write-Host "13 Clear empties both"
    $reports.ClearResults()
    Check ($cards.Count -eq 0 -and $boundCards.Count -eq 0 -and (ScrollY) -eq 0) "no request cards, no findings, at the top"
    Check ($empty.Visible -and $empty.Text -eq 'No reports yet') "the empty text is back"

    $form.Close(); $form.Dispose()
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
