# Tests tools\consistency_test_count.ps1, the counter for the consistency test document, with
# made-up runs: so that it is known to count right before anyone pays for a real one.
#
#   A  the key and the document agree with the real finder (-Check)
#   B  a run in which every repeated passage is translated the same way: all 17 agree
#   C  a run in which four known pairs are worded differently, one segment is missing and only
#      one of the two defect sentences got a note: exactly those are reported
#   D  the prompt log: which pairs were shown an earlier translation, which sat in one request,
#      a note that is only in the AI's reply (written as a Trados comment), and TM matches sent
#   E  a second backup file (a run on the empty segments) adds to the first
#   F  failure: a backup of another document counts nothing and says so
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\consistency_test_count_test.ps1
#
# Needs the core built (dotnet build core\build\Supervertaler.Core.Build.csproj). No Studio, no AI.
# Everything is written to a folder in %TEMP%, deleted afterwards.
$ErrorActionPreference = 'Stop'
$counter = Join-Path $PSScriptRoot 'consistency_test_count.ps1'
$keyPath = Join-Path $PSScriptRoot 'fixtures\consistency-test\consistency-test-key.json'
$temp = Join-Path $env:TEMP ("sv-consistency-count-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" } else { $script:fail++; Write-Host "  FAIL $what" }
}

Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$key = $ser.DeserializeObject([IO.File]::ReadAllText($keyPath, [Text.Encoding]::UTF8))
$src = @{}
foreach ($s in $key['segments']) { $src[[int]$s['n']] = [string]$s['text'] }
$count = $src.Count

# A made-up "translation": every word with an x in front. The same source words give the same
# target words, so every repeated passage agrees unless the test changes it.
function Fake([string]$text) {
    (@([regex]::Matches($text, "[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*") | ForEach-Object { 'x' + $_.Value.ToLowerInvariant() }) -join ' ') + '.'
}
# The same with four words in the middle turned round: a different wording of the passage.
function Reworded([string]$text) {
    $w = @([regex]::Matches($text, "[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*") | ForEach-Object { 'x' + $_.Value.ToLowerInvariant() })
    $m = [int]($w.Count / 2)
    $swap = $w[($m + 3)], $w[($m + 2)], $w[($m + 1)], $w[$m]
    (@($w[0..($m - 1)]) + $swap + @($w[($m + 4)..($w.Count - 1)]) -join ' ') + '.'
}
function Esc([string]$t) { [Security.SecurityElement]::Escape($t) }
function WriteTmx([string]$path, $targets) {
    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine('<tmx version="1.4"><header srclang="nl-NL" datatype="plaintext" segtype="sentence" adminlang="en" o-tmf="x" creationtool="test" creationtoolversion="1"/><body>')
    foreach ($n in ($targets.Keys | Sort-Object)) {
        [void]$sb.AppendLine("<tu><tuv xml:lang=`"nl-NL`"><seg>$(Esc $src[$n])</seg></tuv><tuv xml:lang=`"en-GB`"><seg>$(Esc $targets[$n])</seg></tuv></tu>")
    }
    [void]$sb.AppendLine('</body></tmx>')
    [IO.File]::WriteAllText($path, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
}
function Run([string[]]$extra) {
    $json = Join-Path $temp ("out-" + [guid]::NewGuid().ToString("N").Substring(0, 6) + ".json")
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $counter, '-Json', $json) + $extra
    $text = & powershell @argsList 2>&1 | Out-String
    $result = $null
    if (Test-Path -LiteralPath $json) { $result = $ser.DeserializeObject([IO.File]::ReadAllText($json, [Text.Encoding]::UTF8)) }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; R = $result }
}
function Pair($r, [int]$a, [int]$b) { $r.R['pairs'] | Where-Object { $_['a'] -eq $a -and $_['b'] -eq $b } }
function N([string]$id) { [int](($key['segments'] | Where-Object { $_['id'] -eq $id })['n']) }

try {
    Write-Host "A  the key against the real finder"
    $check = & powershell -NoProfile -ExecutionPolicy Bypass -File $counter -Check 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0 -and $check -match 'exactly as the key says') "the finder pairs the document as the key says ($count segments)"
    Check ($count -eq 100) "the document has 100 segments"

    $defectA = N 'm-defect-a'; $defectB = N 'm-defect-b'

    Write-Host "B  every repeated passage translated the same way"
    $same = @{}
    foreach ($n in 1..$count) { $same[$n] = Fake $src[$n] }
    $same[$defectA] += ' [[TC: minimum above maximum]]'
    $same[$defectB] += ' [[TC: minimum above maximum]]'
    $tmxSame = Join-Path $temp 'same.tmx'
    WriteTmx $tmxSame $same
    $b = Run @('-Tmx', $tmxSame)
    Check ($b.Exit -eq 0 -and $b.R['translated'] -eq 100 -and @($b.R['missing']).Count -eq 0) "100 of 100 translated"
    Check ($b.R['agree'] -eq 17 -and $b.R['differ'] -eq 0 -and $b.R['unmeasured'] -eq 0) "17 agree, none differ ($($b.R['agree']) / $($b.R['differ']) / $($b.R['unmeasured']))"
    Check (@($b.R['pairs'] | Where-Object { $_['result'] -eq 'not judged' -and $_['kind'] -eq 'reworded' }).Count -eq 2) "the two reworded pairs are listed as not judged"
    $twin = Pair $b $defectA $defectB
    Check ($twin['noteA'] -eq 'yes' -and $twin['noteB'] -eq 'yes') "a note on both defect sentences"
    Check ((Pair $b (N 'c-def-date') (N 'c-late'))['shown'] -eq 'same request (by batch size)') "without a prompt log, a pair inside one batch of 10 is named as such"

    Write-Host "C  four pairs worded differently, one segment missing, one note"
    $diff = @{}
    foreach ($n in 1..$count) { $diff[$n] = Fake $src[$n] }
    $changed = @((N 'c-repair'), (N 'c-penalty-b'), (N 'c-punct-b'), (N 'p-list-over-b'))
    foreach ($n in $changed) { $diff[$n] = Reworded $src[$n] }
    $diff.Remove((N 'p-claim-3'))
    $diff[$defectA] += ' [[TC: minimum above maximum]]'
    $tmxDiff = Join-Path $temp 'diff.tmx'
    WriteTmx $tmxDiff $diff
    $c = Run @('-Tmx', $tmxDiff)
    Check ($c.R['differ'] -eq 4 -and $c.R['agree'] -eq 12 -and $c.R['unmeasured'] -eq 1) "4 differ, 12 agree, 1 not measured ($($c.R['differ']) / $($c.R['agree']) / $($c.R['unmeasured']))"
    $expectDiffer = @(@((N 'c-def-defect'), (N 'c-repair')), @((N 'c-penalty-a'), (N 'c-penalty-b')), @((N 'c-punct-a'), (N 'c-punct-b')), @((N 'p-list-over-a'), (N 'p-list-over-b')))
    $all = $true
    foreach ($e in $expectDiffer) { if ((Pair $c $e[0] $e[1])['result'] -ne 'DIFFER') { $all = $false } }
    Check $all "exactly the four changed pairs: verbatim, twin, punctuation only, long list"
    Check ((Pair $c (N 'p-desc-3') (N 'p-claim-3'))['result'] -eq 'no translation') "the pair with the missing segment is 'no translation', not a difference"
    Check (@($c.R['missing']).Count -eq 1 -and $c.R['missing'][0] -eq (N 'p-claim-3')) "and the missing segment is named"
    $twin = Pair $c $defectA $defectB
    Check ($twin['noteA'] -eq 'yes' -and $twin['noteB'] -like 'no*') "a note on only one of the defect sentences is seen"

    Write-Host "D  the prompt log"
    # Requests of 10 segments, as the plugin writes them. The request with c-repair shows it an
    # earlier translation; the notes are only in the replies (as when they become Trados comments).
    $noNotes = @{}
    foreach ($n in $same.Keys) { $noNotes[$n] = Fake $src[$n] }
    $tmxPlain = Join-Path $temp 'plain.tmx'
    WriteTmx $tmxPlain $noNotes
    $shownSeg = N 'c-repair'
    $log = Join-Path $temp 'prompts.jsonl'
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add($ser.Serialize(@{ feature = 'BatchTranslate.batch'; ts = '2026-01-01T09:00:00Z'; promptName = 'batch 1 of 1'; userPrompt = "ONLY the numbered translations.`r`n`r`n1. Een zin uit een ander document.`r`n`r`n**YOUR TRANSLATIONS"; response = '1. x' }))
    for ($start = 1; $start -le $count; $start += 10) {
        $end = [Math]::Min($start + 9, $count)
        $user = ''
        if ($shownSeg -ge $start -and $shownSeg -le $end) {
            $user += "**EARLIER TRANSLATIONS FROM THIS DOCUMENT " + [char]0x2013 + " KEEP THE SAME WORDING**`r`n`r`nSome of the segments below repeat a passage.`r`n`r`nSegment $shownSeg " + [char]0x2013 + " repeats " + [char]0x201C + "elke afwijking" + [char]0x201D + " (18 words)`r`n  earlier source:      x`r`n  earlier translation: y`r`n`r`n"
        }
        $user += "**SEGMENTS TO TRANSLATE (" + ($end - $start + 1) + " segments):**`r`n`r`n5. NO explanations, NO commentary, ONLY the numbered translations.`r`n`r`n"
        $reply = ''
        foreach ($n in $start..$end) {
            $user += "$n. $($src[$n])`r`n"
            $reply += "$n. $(Fake $src[$n])$(if ($n -eq $defectA) { ' [[TC: minimum above maximum]]' })`n"
        }
        $user += "`r`n**YOUR TRANSLATIONS (numbered list):**`r`nBegin your translations now:"
        $lines.Add($ser.Serialize(@{ feature = 'BatchTranslate.batch'; ts = '2026-01-02T10:00:00Z'; promptName = "batch $([int](($start + 9) / 10)) of 10"; userPrompt = $user; response = $reply }))
    }
    [IO.File]::WriteAllLines($log, $lines, (New-Object Text.UTF8Encoding($false)))
    $d = Run @('-Tmx', $tmxPlain, '-PromptLog', $log, '-Since', '2026-01-02')
    Check ($d.R['requests'] -eq 10 -and $d.R['tmRequests'] -eq 0) "10 requests for this document, none with TM matches; the other document's request is left out ($($d.R['requests']))"
    Check ($d.R['blockRequests'] -eq 1 -and $d.R['earlierShown'] -eq 1) "one request carried an earlier translation"
    Check ((Pair $d (N 'c-def-defect') $shownSeg)['shown'] -eq 'shown') "the pair whose later segment was shown one is 'shown'"
    Check ((Pair $d (N 'c-def-date') (N 'c-late'))['shown'] -eq 'same request') "a pair inside one request is 'same request'"
    Check ((Pair $d (N 'p-desc-1') (N 'p-claim-1'))['shown'] -eq 'NOT shown') "a pair across requests with nothing shown is 'NOT shown'"
    Check ($d.R['shown'] -eq 1 -and $d.R['shownAgree'] -eq 1) "totals: 1 shown, and it agrees"
    $twin = Pair $d $defectA $defectB
    Check ($twin['noteA'] -eq 'yes' -and $twin['noteB'] -eq 'no') "a note that is only in the AI's reply is found through the log"

    $withTm = Join-Path $temp 'prompts-tm.jsonl'
    $tmLine = $ser.Serialize(@{ feature = 'BatchTranslate.batch'; ts = '2026-01-03T10:00:00Z'; promptName = 'batch 1 of 10'; userPrompt = "**TRANSLATION MEMORY MATCHES " + [char]0x2013 + " REFERENCE ONLY**`r`n`r`nSegment 1 " + [char]0x2013 + " 100% match`r`n`r`nONLY the numbered translations.`r`n`r`n1. $($src[1])`r`n`r`n**YOUR TRANSLATIONS"; response = '1. x' })
    [IO.File]::WriteAllLines($withTm, @($tmLine), (New-Object Text.UTF8Encoding($false)))
    $t = Run @('-Tmx', $tmxPlain, '-PromptLog', $withTm)
    Check ($t.R['tmRequests'] -eq 1 -and $t.Text -match 'NOT comparable') "a run that sent TM matches is counted and called not comparable"

    Write-Host "E  a second backup adds to the first"
    $first = @{}; $second = @{}
    foreach ($n in 1..$count) { if ($n -le 90) { $first[$n] = Fake $src[$n] } else { $second[$n] = Fake $src[$n] } }
    $tmx1 = Join-Path $temp 'first.tmx'; $tmx2 = Join-Path $temp 'second.tmx'
    WriteTmx $tmx1 $first; WriteTmx $tmx2 $second
    $one = Run @('-Tmx', $tmx1)
    $two = Run @('-Tmx', $tmx1, '-Tmx2', $tmx2)
    Check ($one.R['translated'] -eq 90 -and $one.R['unmeasured'] -gt 0) "the first alone: 90 translated, some pairs not measured ($($one.R['unmeasured']))"
    Check ($two.R['translated'] -eq 100 -and $two.R['agree'] -eq 17) "both together: 100 translated, all 17 agree"

    Write-Host "F  failure: a backup of another document"
    $other = Join-Path $temp 'other.tmx'
    [IO.File]::WriteAllText($other, '<?xml version="1.0" encoding="utf-8"?><tmx version="1.4"><header srclang="nl-NL" datatype="plaintext" segtype="sentence" adminlang="en" o-tmf="x" creationtool="t" creationtoolversion="1"/><body><tu><tuv xml:lang="nl-NL"><seg>Iets heel anders.</seg></tuv><tuv xml:lang="en-GB"><seg>Something else.</seg></tuv></tu></body></tmx>', (New-Object Text.UTF8Encoding($false)))
    $f = Run @('-Tmx', $other)
    Check ($f.R['translated'] -eq 0 -and $f.R['agree'] -eq 0 -and $f.R['differ'] -eq 0 -and $f.R['unmeasured'] -eq 17) "nothing translated, nothing called a difference, 17 not measured"
    Check ($f.Text -match 'not from this document') "and it says the backup is not from this document"
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail)
if ($script:fail -gt 0) { exit 1 }
