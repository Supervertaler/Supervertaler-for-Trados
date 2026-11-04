# Source text outside any segment: the detector, the monitor's reuse of results,
# the get_active_project warnings and the check_unsegmented_text response - checked
# against the built plugin DLL.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\unsegmented_text_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>     (default: this repo's build)
#   ... -RealDir <folder with .sdlxliff files>       (optional acceptance run; also
#                                                     SV_UNSEGMENTED_REAL)
#
# The checked-in fixtures (tools\fixtures\unsegmented-text) are synthetic Acme text, so
# the repo carries no client file. -RealDir runs the detector on real files too and
# prints only file names, counts and locations. The scale check writes a ~50 MB file
# into %TEMP% and deletes it afterwards. Nothing else is written anywhere.
param(
    [string]$Dll,
    [string]$RealDir = $env:SV_UNSEGMENTED_REAL,
    [int]$ScaleUnits = 20000
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    foreach ($c in @('Studio18\Release', 'Studio18\Debug', 'Studio19\Release', 'Studio19\Debug')) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
Write-Host "DLL: $Dll"

$studioDirs = @()
foreach ($pf in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
    foreach ($v in @('Studio18', 'Studio19')) {
        $d = Join-Path $pf "Trados\Trados Studio\$v"
        if (Test-Path $d) { $studioDirs += $d }
    }
}
$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @((Split-Path $Dll)) + $studioDirs) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Det    = $plugin.GetType('Supervertaler.Trados.Core.UnsegmentedTextDetector')
$Mon    = $plugin.GetType('Supervertaler.Trados.Core.UnsegmentedTextMonitor')
$Bridge = $plugin.GetType('Supervertaler.Trados.Core.SupervertalerBridge')
if (-not $Det -or -not $Mon -or -not $Bridge) { throw 'types not found in the DLL' }

$fails = 0; $checks = 0
function Check($ok, $label) {
    $script:checks++
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$None = [Threading.CancellationToken]::None
function CheckFile([string]$path, $token = $None) {
    return $Det.GetMethod('CheckFile').Invoke($null, @($path, $token))
}

$fixtures = Join-Path $PSScriptRoot 'fixtures\unsegmented-text'
$stray = Join-Path $fixtures 'acme-stray-text.sdlxliff'
$clean = Join-Path $fixtures 'acme-clean.sdlxliff'
$work = Join-Path $env:TEMP ("sv-unsegmented-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    # ---- letter runs ---------------------------------------------------------------
    $hasRun = $Det.GetMethod('HasLetterRun')
    foreach ($case in @(
            @('Acme', $true), @('Hee', $true), @('Ωμέγα', $true), @('日本語', $true),
            @('नमस्ते', $true), @('Ära', $true),
            @('km', $false), @('B4 / C7 (km)', $false), @('a1b2c3', $false), @(' – … • ', $false),
            @('12,5 mm', $false), @('', $false), @('日本', $false))) {
        $got = $hasRun.Invoke($null, @([string]$case[0]))
        Check ($got -eq $case[1]) ("letter run in '{0}' = {1}" -f $case[0], $case[1])
    }

    # ---- the stray-text fixture: five stretches and two unsegmented paragraphs, in file order
    $r = CheckFile $stray
    Check $r.Ok 'stray fixture: read to the end'
    Check ($r.Segments -eq 12) "stray fixture: 12 segments counted (got $($r.Segments))"
    Check ($r.HitCount -eq 5) "stray fixture: 5 stretches between segments (got $($r.HitCount))"
    Check ($r.ParagraphCount -eq 2) "stray fixture: 2 paragraphs with no segment, kept apart (got $($r.ParagraphCount))"
    $expect = @(
        @{ t = 'Welcome to Acme.';                                              b = $null; a = '1';  n = $false; pu = '1' },
        @{ t = 'Does your garden need a special soil, such as type B4 or C7?';  b = '3';   a = '4';  n = $false; pu = '2' },
        @{ t = 'Acme ships worldwide.';                                         b = '5';   a = '6';  n = $false; pu = '3' },
        @{ t = 'Acme.';                                                         b = '9';   a = '10'; n = $false; pu = '8' },
        @{ t = 'Ωμέγα.';                                                        b = '11';  a = '12'; n = $false; pu = '9' },
        @{ t = 'Contact Acme for a quote.';                                     b = '6';   a = '7';  n = $true;  pu = '4' },
        @{ t = 'Acme Corporation';                                              b = '8';   a = '9';  n = $true;  pu = '7' })
    $found = @($r.Hits) + @($r.Paragraphs)
    for ($i = 0; $i -lt $expect.Count; $i++) {
        $h = $found[$i]; $e = $expect[$i]
        if (-not $h) { Check $false "finding $($i + 1) missing"; continue }
        Check ($h.Text -ceq $e.t) ("finding {0} text '{1}' (got '{2}')" -f ($i + 1), $e.t, $h.Text)
        Check (($h.SegmentBefore -eq $e.b) -and ($h.SegmentAfter -eq $e.a)) `
            ("finding {0} between {1} and {2} (got {3} / {4}: {5})" -f ($i + 1), $e.b, $e.a, $h.SegmentBefore, $h.SegmentAfter, $h.Location)
        Check ($h.ParagraphNotSegmented -eq $e.n) ("finding {0} paragraphNotSegmented = {1}" -f ($i + 1), $e.n)
        Check ($h.ParagraphUnitId -eq ('00000000-0000-4000-8000-00000000000' + $e.pu)) ("finding {0} in paragraph {1}" -f ($i + 1), $e.pu)
    }
    Check ($r.Hits[0].Location -eq 'before segment 1') "first stretch reads 'before segment 1' (got '$($r.Hits[0].Location)')"
    Check ($r.Hits[0].JumpSegmentId -eq '1' -and $r.Hits[0].JumpParagraphUnitId -eq '00000000-0000-4000-8000-000000000001') 'no segment before: jump goes to the one after'
    Check ($r.Hits[1].Location -eq 'between segments 3 and 4') "the real-job shape reads 'between segments 3 and 4'"
    Check ($r.Hits[1].JumpSegmentId -eq '3' -and $r.Hits[1].JumpParagraphUnitId -eq '00000000-0000-4000-8000-000000000002') 'jump goes to the segment before'

    # ---- the clean fixture: nothing ------------------------------------------------------
    $r = CheckFile $clean
    Check ($r.Ok -and $r.HitCount -eq 0 -and $r.ParagraphCount -eq 0) "clean fixture: nothing found (got $($r.HitCount) / $($r.ParagraphCount))"

    # ---- a file with no segments at all (never prepared): nothing reported ---------------------
    $raw = Join-Path $work 'not-prepared.sdlxliff'
    [IO.File]::WriteAllText($raw, '<?xml version="1.0" encoding="utf-8"?><xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2"><file><body><trans-unit id="a"><source>Acme sells rakes.</source></trans-unit><trans-unit id="b"><source>Acme sells seeds.</source><seg-source>Acme sells seeds.</seg-source></trans-unit></body></file></xliff>')
    $r = CheckFile $raw
    Check ($r.Ok -and $r.Segments -eq 0 -and $r.HitCount -eq 0 -and $r.ParagraphCount -eq 0 -and $r.Paragraphs.Count -eq 0) 'a file with no segments reports nothing'

    # ---- the check changes nothing on disk ------------------------------------------------
    $before = (Get-FileHash $stray).Hash
    $null = CheckFile $stray
    Check ((Get-FileHash $stray).Hash -eq $before) 'the file is unchanged after a check'

    # ---- failure modes: no throw, no hits, a reason ----------------------------------------
    $bytes = [IO.File]::ReadAllBytes($stray)
    $half = Join-Path $work 'half-written.sdlxliff'
    [IO.File]::WriteAllBytes($half, $bytes[0..([int]($bytes.Length * 0.6))])
    $r = CheckFile $half
    Check ((-not $r.Ok) -and $r.HitCount -eq 0 -and $r.Hits.Count -eq 0 -and $r.Error) "half-written file: not ok, no hits ($($r.Error))"

    $junk = Join-Path $work 'not-xml.sdlxliff'
    [IO.File]::WriteAllText($junk, 'This is not XML at all.')
    $r = CheckFile $junk
    Check ((-not $r.Ok) -and $r.HitCount -eq 0) 'not XML: not ok, no hits'

    $empty = Join-Path $work 'empty.sdlxliff'
    [IO.File]::WriteAllBytes($empty, [byte[]]@())
    $r = CheckFile $empty
    Check ((-not $r.Ok) -and $r.HitCount -eq 0) 'empty file: not ok, no hits'

    $r = CheckFile (Join-Path $work 'missing.sdlxliff')
    Check ((-not $r.Ok) -and $r.HitCount -eq 0) 'missing file: not ok, no hits'

    $locked = Join-Path $work 'locked.sdlxliff'
    Copy-Item $stray $locked
    $lock = [IO.File]::Open($locked, 'Open', 'ReadWrite', 'None')
    try {
        $r = CheckFile $locked
        Check ((-not $r.Ok) -and $r.HitCount -eq 0 -and $r.Error -match 'IOException') "locked file: not ok, no hits ($($r.Error))"
    } finally { $lock.Dispose() }

    # Studio holding the file open for writing (sharing it) must not stop a check.
    $shared = [IO.File]::Open($locked, 'Open', 'ReadWrite', 'ReadWrite')
    try {
        $r = CheckFile $locked
        Check ($r.Ok -and $r.HitCount -eq 5) 'file open for writing elsewhere (shared): checked normally'
    } finally { $shared.Dispose() }

    $cts = New-Object Threading.CancellationTokenSource
    $cts.Cancel()
    $threw = $false
    try { $null = CheckFile $stray $cts.Token }
    catch { $threw = ($_.Exception.InnerException -is [OperationCanceledException]) }
    Check $threw 'a cancelled check stops with OperationCanceledException'
    $cts.Dispose()

    # ---- caps: findings per file, of each kind, and text per finding -------------------------------
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append('<?xml version="1.0" encoding="utf-8"?><xliff xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2"><file><body>')
    for ($i = 0; $i -lt 300; $i++) { [void]$sb.Append("<trans-unit id=`"p$i`"><source>Acme paragraph $i</source></trans-unit>") }
    $long = ('Acme ' * 300).Trim()
    [void]$sb.Append("<trans-unit id=`"long`"><seg-source><mrk mtype=`"seg`" mid=`"1`">Start.</mrk> $long <mrk mtype=`"seg`" mid=`"2`">End.</mrk></seg-source></trans-unit>")
    for ($i = 0; $i -lt 300; $i++) { [void]$sb.Append("<trans-unit id=`"s$i`"><seg-source><mrk mtype=`"seg`" mid=`"x$i`">One.</mrk> Acme stray $i <mrk mtype=`"seg`" mid=`"y$i`">Two.</mrk></seg-source></trans-unit>") }
    [void]$sb.Append('</body></file></xliff>')
    $many = Join-Path $work 'many.sdlxliff'
    [IO.File]::WriteAllText($many, $sb.ToString())
    $r = CheckFile $many
    $max = $Det.GetField('MaxHitsPerFile').GetValue($null)
    $maxText = $Det.GetField('MaxTextChars').GetValue($null)
    Check ($r.HitCount -eq 301 -and $r.Hits.Count -eq $max) "301 stretches counted, $max kept (got $($r.HitCount) / $($r.Hits.Count))"
    Check ($r.ParagraphCount -eq 300 -and $r.Paragraphs.Count -eq $max) "300 unsegmented paragraphs counted, $max kept (got $($r.ParagraphCount) / $($r.Paragraphs.Count))"
    Check ($r.Hits[0].Text.StartsWith('Acme Acme') -and $r.Hits[$max - 1].Text -eq "Acme stray $($max - 2)") 'many unsegmented paragraphs never crowd out a stretch between segments'
    Check ($r.Hits[0].Text.Length -le $maxText + 1 -and $r.Hits[0].Text.EndsWith('…')) "a long stretch is cut to $maxText characters (got $($r.Hits[0].Text.Length))"

    # ---- the monitor: results kept per file, re-checked only after a change ------------------
    $a = Join-Path $work 'doc-a.sdlxliff'; Copy-Item $stray $a
    $b = Join-Path $work 'doc-b.sdlxliff'; Copy-Item $clean $b
    $gone = Join-Path $work 'doc-gone.sdlxliff'
    $Mon.GetField('_activeFiles', $Static).SetValue($null, [string[]]@($a, $b, $gone))
    $now = $Mon.GetMethod('CheckActiveDocumentNow', $Static)
    $argv = [object[]]@($null)
    $first = $now.Invoke($null, $argv)
    Check (-not $argv[0]) 'monitor: reports that it is not attached to a Studio editor'
    Check ($first.Count -eq 3 -and $first[0].HitCount -eq 5 -and $first[1].HitCount -eq 0) 'monitor: one result per file of the document'
    Check ((-not $first[2].Ok) -and $first[2].Error) "monitor: a missing file comes back as not checked ($($first[2].Error))"
    $second = $now.Invoke($null, [object[]]@($null))
    Check ([object]::ReferenceEquals($first[0], $second[0]) -and [object]::ReferenceEquals($first[1], $second[1])) 'monitor: an unchanged file is not read again'
    (Get-Item $a).LastWriteTimeUtc = (Get-Item $a).LastWriteTimeUtc.AddSeconds(5)
    $third = $now.Invoke($null, [object[]]@($null))
    Check ((-not [object]::ReferenceEquals($second[0], $third[0])) -and [object]::ReferenceEquals($second[1], $third[1])) 'monitor: a saved file is checked again, the other is not'

    $curArgs = [object[]]@($null)
    $current = $Mon.GetMethod('Current', $Static).Invoke($null, $curArgs)
    Check ($current.Count -eq 2 -and $curArgs[0] -eq 1) "monitor: Current() has the two readable files, 1 pending (got $($current.Count), $($curArgs[0]))"

    $warn = $Mon.GetMethod('BridgeWarnings', $Static).Invoke($null, @(3))
    Check ($warn.Count -eq 4) "get_active_project: 3 stretches named, then one line for the rest (got $($warn.Count))"
    Check ($warn[1] -match 'between segments 3 and 4' -and $warn[1] -match 'doc-a' -and $warn[1] -match 'untranslated') 'get_active_project: a warning names file, place and consequence'
    Check ($warn[3] -match '2 more' -and $warn[3] -match 'check_unsegmented_text') 'get_active_project: the last line points to check_unsegmented_text'
    Check ($warn[1] -notmatch '\.sdlxliff') 'get_active_project: file shown without .sdlxliff'
    Check (-not ($warn -match 'Contact Acme|Acme Corporation')) 'get_active_project: unsegmented paragraphs are not among the warnings'

    # ---- the check_unsegmented_text response --------------------------------------------------
    $build = $Bridge.GetMethod('BuildUnsegmentedResponse', $Static)
    $resp = $build.Invoke($null, @($third, $true, 3))
    Check ($resp.Available -and $resp.Total -eq 5 -and $resp.Stretches.Count -eq 3 -and $resp.Truncated) 'check_unsegmented_text: total 5, 3 listed, truncated'
    Check ($resp.ParagraphsNotSegmentedTotal -eq 2 -and $resp.ParagraphsNotSegmented.Count -eq 2 -and $resp.ParagraphsNotSegmented[0].Text -eq 'Contact Acme for a quote.') 'check_unsegmented_text: unsegmented paragraphs listed apart'
    Check ($resp.Files[0].Segments -eq 12 -and $resp.Files[0].Stretches -eq 5 -and $resp.Files[0].ParagraphsNotSegmented -eq 2) 'check_unsegmented_text: per-file segments, stretches and paragraphs'
    Check ($resp.Files.Count -eq 3 -and -not $resp.Files[2].Checked -and $resp.Files[2].Error) 'check_unsegmented_text: an unreadable file is named with its error'
    Check ($resp.Stretches[0].NearestSegmentId -eq '00000000-0000-4000-8000-000000000001:1') "check_unsegmented_text: nearestSegmentId is go_to_segment's id ($($resp.Stretches[0].NearestSegmentId))"
    Check ($resp.Stretches[1].SegmentBefore -eq '3' -and $resp.Stretches[1].SegmentAfter -eq '4') 'check_unsegmented_text: before and after segments'
    Check ($resp.Note -match 'untranslated' -and $resp.Note -match '3 of the 5' -and $resp.Note -match 'usually deliberate') 'check_unsegmented_text: the note explains both kinds and says the list is cut'
    $resp = $build.Invoke($null, @([Collections.Generic.List[Supervertaler.Trados.Core.UnsegmentedFileResult]]::new(), $true, 100))
    Check (-not $resp.Available -and $resp.Note -match 'No document') 'check_unsegmented_text: no document open'
    $resp = $build.Invoke($null, @($third, $false, 100))
    Check (-not $resp.Available) 'check_unsegmented_text: not attached to Studio'

    $Mon.GetField('_activeFiles', $Static).SetValue($null, [string[]]@())

    # ---- scale: a 50 MB file with a big embedded original --------------------------------------
    $big = Join-Path $work 'scale.sdlxliff'
    $w = New-Object IO.StreamWriter($big, $false, (New-Object Text.UTF8Encoding($false)))
    try {
        $w.Write('<?xml version="1.0" encoding="utf-8"?><xliff xmlns:sdl="http://sdl.com/FileTypes/SdlXliff/1.0" xmlns="urn:oasis:names:tc:xliff:document:1.2" version="1.2"><file><header><reference><internal-file form="base64">')
        $chunk = 'QUFB' * 16384
        for ($i = 0; $i -lt 470; $i++) { $w.Write($chunk) }   # ~30 MB embedded original
        $w.Write('</internal-file></reference></header><body>')
        $sentence = 'Acme delivers garden tools, seeds and hoses to shops in nine countries, and ships orders within 48 hours.'
        $plant = @{ 5000 = $true; 12000 = $true; ($ScaleUnits - 1) = $true }
        for ($i = 0; $i -lt $ScaleUnits; $i++) {
            $m = $i + 1
            $mid = if ($plant.ContainsKey($i)) { ' Acme stray sentence. ' } else { ' <mrk mtype="x-sdl-comment" sdl:cid="c1"/> ' }
            $w.Write("<group><trans-unit id=`"u$i`"><source>$sentence $sentence</source><seg-source><mrk mtype=`"seg`" mid=`"$m`">$sentence</mrk>$mid<mrk mtype=`"seg`" mid=`"$m b`">$sentence</mrk></seg-source><target><mrk mtype=`"seg`" mid=`"$m`">$sentence</mrk> <mrk mtype=`"seg`" mid=`"$m b`">$sentence</mrk></target><sdl:seg-defs><sdl:seg id=`"$m`" conf=`"Translated`"/><sdl:seg id=`"$m b`" conf=`"Translated`"/></sdl:seg-defs></trans-unit></group>")
        }
        $w.Write('</body></file></xliff>')
    } finally { $w.Dispose() }
    $mb = [math]::Round((Get-Item $big).Length / 1MB, 1)
    [GC]::Collect()
    $proc = [Diagnostics.Process]::GetCurrentProcess(); $proc.Refresh()
    $peakBefore = $proc.PeakWorkingSet64
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = CheckFile $big
    $sw.Stop()
    $proc.Refresh()
    $peakGrowth = [math]::Round(($proc.PeakWorkingSet64 - $peakBefore) / 1MB, 1)
    Check ($r.Ok -and $r.HitCount -eq 3) ("scale: {0} MB, {1} paragraphs / {2} segments: 3 stretches (got {3})" -f $mb, $ScaleUnits, (2 * $ScaleUnits), $r.HitCount)
    Check ($r.Hits[2].SegmentBefore -eq "$ScaleUnits" -and $r.Hits[2].SegmentAfter -eq "$ScaleUnits b") 'scale: segment ids with a space (split segments) are kept as they are'
    Write-Host ("      scale: checked in {0} ms; peak working set grew {1} MB" -f $sw.ElapsedMilliseconds, $peakGrowth)
    Check ($peakGrowth -lt 40) "scale: memory does not grow with the file (peak +$peakGrowth MB for $mb MB)"

    # ---- optional: the real acceptance files ---------------------------------------------------
    if ($RealDir) {
        Write-Host ""
        Write-Host "Real files in the given folder (read only):"
        foreach ($f in Get-ChildItem -LiteralPath $RealDir -Filter '*.sdlxliff') {
            $hashBefore = (Get-FileHash -LiteralPath $f.FullName).Hash
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $r = CheckFile $f.FullName
            $sw.Stop()
            $unchanged = (Get-FileHash -LiteralPath $f.FullName).Hash -eq $hashBefore
            $where = ($r.Hits | ForEach-Object { $_.Location }) -join '; '
            Write-Host ("  {0}: ok={1} segments={2} stretches={3} [{4}] paragraphs without segments={5}, {6} ms, file unchanged={7}" -f `
                $f.Name, $r.Ok, $r.Segments, $r.HitCount, $where, $r.ParagraphCount, $sw.ElapsedMilliseconds, $unchanged)
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "$checks checks, $fails failed"
if ($fails -gt 0) { exit 1 }
