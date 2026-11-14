# Counts a Batch Translate run of the consistency test document against its answer key
# (tools\fixtures\consistency-test\; docs\design\in-run-references.md and restated-passages.md).
#
# The document is small and invented, with planted pairs of known kinds: a passage repeated word
# for word inside longer sentences, near-identical twins, a pair that differs in punctuation only,
# long lists under and over the size limit, two sentences with the same defect, boilerplate. This
# script takes the run's backup TMX and says, pair by pair: did step 1's finder pair them, was the
# later one shown the earlier translation (step 2; needs the prompt log), and do the two
# translations agree. Then totals. No Studio, no AI, nothing written but the optional -Json file.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\consistency_test_count.ps1 -Tmx <backup.tmx>
#
#   -Tmx        the run's backup TMX, from D:\Supervertaler\trados\batch_backups
#   -Tmx2       the backup of a second run on the segments the first left empty, if there was one;
#               it adds to the first
#   -PromptLog  the plugin's prompt log of that day (D:\Supervertaler\trados\logs\prompts\<date>.jsonl),
#               to see which segments were shown an earlier translation and which got a note
#   -Since      only requests logged at or after this time, as the log writes it: 2026-11-15T10:30
#   -BatchSize  the run's batch size (default 10), used without a prompt log to tell which pairs
#               were in one request
#   -Check      no run: only check that the finder pairs the document as the key says
#   -Json       also write the result as JSON to this file
#   -Core       an assembly holding Supervertaler's core; by default the one this repository builds
#               (dotnet build core\build\Supervertaler.Core.Build.csproj)
param(
    [string]$Tmx,
    [string]$Tmx2,
    [string]$PromptLog,
    [string]$Since,
    [int]$BatchSize = 10,
    [switch]$Check,
    [string]$Json,
    [string]$Core,
    [string]$Key
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Key) { $Key = Join-Path $PSScriptRoot 'fixtures\consistency-test\consistency-test-key.json' }
if (-not $Core) {
    foreach ($c in @('core\build\bin\Debug\net48\Supervertaler.Core.Build.dll', 'core\build\bin\Release\net48\Supervertaler.Core.Build.dll',
                     'core\tests\bin\Debug\net48\Supervertaler.Core.Tests.exe')) {
        $p = Join-Path $root $c
        if (Test-Path -LiteralPath $p) { $Core = $p; break }
    }
}
if (-not $Core -or -not (Test-Path -LiteralPath $Core)) { throw "Build the core first: dotnet build core\build\Supervertaler.Core.Build.csproj (or pass -Core)" }
if (-not $Check -and -not $Tmx) { throw "Give the run's backup file with -Tmx, or use -Check" }
[void][Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Core).Path)
Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue

function Plain([string]$text) { [regex]::Replace($text, '<\s*/?\s*t\s*\d+\s*/?\s*>', '') }
function Norm([string]$text) { [regex]::Replace((Plain $text).ToLowerInvariant(), '[^\p{L}\p{N}]+', '') }
function WordsOf([string]$text) { ,@([regex]::Matches((Plain $text).ToLowerInvariant(), "[\p{L}\p{N}]+(?:['\u2019-][\p{L}\p{N}]+)*") | ForEach-Object { $_.Value }) }
function HasNote([string]$text) { $text -match '\[\[TC:' -or $text -match '\u27E6TC' }

$k = $ser.DeserializeObject([IO.File]::ReadAllText((Resolve-Path -LiteralPath $Key).Path, [Text.Encoding]::UTF8))
$segments = @($k['segments'])
$count = $segments.Count
$sources = New-Object 'System.Collections.Generic.List[string]'
# A segment is recognised by its exact text first: two segments of the document differ in
# punctuation only. Without punctuation and case only where that still names one segment.
$byExact = @{}; $byNorm = @{}; $normTwice = @{}
foreach ($s in $segments) {
    $text = [string]$s['text']
    $sources.Add($text)
    $byExact[$text.Trim()] = [int]$s['n']
    $norm = Norm $text
    if ($byNorm.ContainsKey($norm)) { $normTwice[$norm] = $true } else { $byNorm[$norm] = [int]$s['n'] }
}
foreach ($norm in $normTwice.Keys) { $byNorm.Remove($norm) }
function SegmentOf([string]$text) {
    $plain = (Plain $text).Trim()
    if ($byExact.ContainsKey($plain)) { return $byExact[$plain] }
    $norm = Norm $text
    if ($byNorm.ContainsKey($norm)) { return $byNorm[$norm] }
    # a list marker the plugin put in front of the text: match on the end of it
    foreach ($e in $byExact.GetEnumerator()) { if ($e.Key.Length -gt 20 -and $plain.EndsWith($e.Key)) { return $e.Value } }
    return 0
}

# ---- step 1's finder on the document, against the key ---------------------------------------
$found = [Supervertaler.Core.RestatedPassages]::Find($sources)
$pairOf = @{}
foreach ($p in $found.Pairs) { $pairOf["$($p.A + 1)-$($p.B + 1)"] = $p }
$problems = @()
$expected = @{}
foreach ($kp in $k['pairs']) {
    $id = "$($kp['a'])-$($kp['b'])"
    $expected[$id] = $true
    $sees = -not ([string]$kp['finder']).StartsWith('cannot see')
    if ($sees -and -not $pairOf.ContainsKey($id)) { $problems += "the key says the finder pairs segments $($kp['a']) and $($kp['b']) ($($kp['kind'])); it does not" }
    if (-not $sees -and $pairOf.ContainsKey($id)) { $problems += "the key says the finder cannot see segments $($kp['a']) and $($kp['b']) ($($kp['kind'])); it pairs them" }
}
$unexpected = @($pairOf.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object { [int]($_ -split '-')[0] }, { [int]($_ -split '-')[1] })
$boiler = @($k['boilerplate']['segments'])
$boilerPairs = @($pairOf.Keys | Where-Object { $ab = $_ -split '-'; ($boiler -contains [int]$ab[0]) -and ($boiler -contains [int]$ab[1]) })
if ($boilerPairs.Count -gt 0) { $problems += "boilerplate segments paired with each other: $($boilerPairs -join ', ')" }
foreach ($u in $unexpected) { $problems += "the finder pairs segments $($u -replace '-', ' and '), which the key does not list" }

Write-Host "Document: $count segments; the finder pairs $($found.Pairs.Count); the key lists $(@($k['pairs']).Count) planted pairs."
if ($problems.Count -eq 0) { Write-Host "Key check: the finder pairs the document exactly as the key says; boilerplate pairs nothing." }
else { Write-Host "Key check FAILED:"; $problems | ForEach-Object { Write-Host "  $_" } }
if ($Check) { if ($problems.Count -gt 0) { exit 2 } else { exit 0 } }

# ---- the run: translations by segment ---------------------------------------------------------
$target = @{}
$fromFile = @{}
$unknownSources = 0
$fileNo = 0
foreach ($path in @($Tmx, $Tmx2)) {
    if (-not $path) { continue }
    $fileNo++
    [xml]$doc = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    foreach ($tu in $doc.tmx.body.tu) {
        $tuv = @($tu.tuv)
        if ($tuv.Count -lt 2) { continue }
        $s = if ($tuv[0].seg -is [string]) { [string]$tuv[0].seg } else { $tuv[0].seg.InnerText }
        $t = if ($tuv[1].seg -is [string]) { [string]$tuv[1].seg } else { $tuv[1].seg.InnerText }
        $n = SegmentOf $s
        if (-not $n) { $unknownSources++; continue }
        $target[$n] = $t
        $fromFile[$n] = $fileNo
    }
}
$translated = $target.Count
$missing = @(1..$count | Where-Object { -not $target.ContainsKey($_) -or [string]::IsNullOrWhiteSpace((Plain $target[$_])) })

# ---- the prompt log: what each request showed ---------------------------------------------------
$shown = @{}; $request = @{}; $replyNote = @{}; $tmSent = 0; $requests = 0; $blockRequests = 0; $earlierShown = 0; $blockChars = 0
$haveLog = $false
if ($PromptLog) {
    $haveLog = $true
    foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $PromptLog).Path, [Text.Encoding]::UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $o = $ser.DeserializeObject($line) } catch { continue }
        if (-not $o -or ([string]$o['feature']) -ne 'BatchTranslate.batch') { continue }
        if ($Since -and ([string]::CompareOrdinal([string]$o['ts'], $Since) -lt 0)) { continue }
        $user = [string]$o['userPrompt']
        $part = $user
        $at = $part.IndexOf('ONLY the numbered translations.'); if ($at -ge 0) { $part = $part.Substring($at) }
        $at = $part.IndexOf('**YOUR TRANSLATIONS'); if ($at -ge 0) { $part = $part.Substring(0, $at) }
        $inRequest = @{}
        foreach ($m in [regex]::Matches($part, '(?m)^(\d+)\. (.*?)\r?$')) {
            $n = SegmentOf $m.Groups[2].Value
            if ($n) { $inRequest[[int]$m.Groups[1].Value] = $n }
        }
        if ($inRequest.Count -eq 0) { continue }   # a request for another document
        $requests++
        if ($user.Contains('**TRANSLATION MEMORY MATCHES')) { $tmSent++ }
        if ($user.Contains('**EARLIER TRANSLATIONS FROM THIS DOCUMENT')) {
            $blockRequests++
            $earlierShown += ([regex]::Matches($user, '(?m)^  earlier translation: ')).Count
            $blockChars += $user.IndexOf('**SEGMENTS TO TRANSLATE') - $user.IndexOf('**EARLIER TRANSLATIONS FROM THIS DOCUMENT')
        }
        $reply = [string]$o['response']
        foreach ($e in $inRequest.GetEnumerator()) {
            $n = $e.Value
            $request[$n] = $requests
            $shown[$n] = [regex]::IsMatch($user, "(?m)^Segment $($e.Key) (\u2013|-) repeats")
            $rm = [regex]::Match($reply, "(?ms)^$($e.Key)\. (.*?)(?=^\d+\. |\z)")
            if ($rm.Success) { $replyNote[$n] = (HasNote $rm.Groups[1].Value) }
        }
    }
}

function NoteOn([int]$n) {
    if ($target.ContainsKey($n) -and (HasNote $target[$n])) { return 'yes' }
    if ($replyNote.ContainsKey($n)) { if ($replyNote[$n]) { return 'yes' } else { return 'no' } }
    if (-not $target.ContainsKey($n)) { return '-' }
    if ($haveLog) { return 'no' } else { return 'no (in the target; a note written as a Trados comment needs -PromptLog)' }
}
function LongestRun([string]$x, [string]$y) {
    $a = WordsOf $x; $b = WordsOf $y; $best = 0
    $prev = New-Object 'int[]' ($b.Count + 1)
    for ($i = 1; $i -le $a.Count; $i++) {
        $cur = New-Object 'int[]' ($b.Count + 1)
        for ($j = 1; $j -le $b.Count; $j++) { if ($a[$i - 1] -eq $b[$j - 1]) { $cur[$j] = $prev[$j - 1] + 1; if ($cur[$j] -gt $best) { $best = $cur[$j] } } }
        $prev = $cur
    }
    $best
}

# ---- pair by pair -------------------------------------------------------------------------------
$rows = @()
foreach ($kp in $k['pairs']) {
    $a = [int]$kp['a']; $b = [int]$kp['b']; $id = "$a-$b"
    $sees = $pairOf.ContainsKey($id)
    $have = $target.ContainsKey($a) -and $target.ContainsKey($b)
    $result = 'no translation'
    $detail = ''
    if ($have) {
        if ($sees) {
            $v = "" + [Supervertaler.Core.RestatedPassages]::Compare($pairOf[$id], (Plain $target[$a]), (Plain $target[$b]))
            $result = if ($v -eq 'Same') { 'agree' } elseif ($v -eq 'Different') { 'DIFFER' } else { 'not compared' }
        } else {
            $result = 'not judged'
            $detail = "longest wording the two translations share: $(LongestRun $target[$a] $target[$b]) words"
        }
    }
    $how = 'no prompt log'
    if ($haveLog) {
        if (-not $request.ContainsKey($a) -or -not $request.ContainsKey($b)) { $how = 'not in the log' }
        elseif ($request[$a] -eq $request[$b]) { $how = 'same request' }
        elseif ($shown[$b] -or $shown[$a]) { $how = 'shown' }
        else { $how = 'NOT shown' }
    } elseif ([Math]::Floor(($a - 1) / $BatchSize) -eq [Math]::Floor(($b - 1) / $BatchSize)) { $how = 'same request (by batch size)' }
    $row = [ordered]@{ kind = [string]$kp['kind']; a = $a; b = $b; finder = $(if ($sees) { 'pairs them' } else { 'cannot see' }); shown = $how; result = $result; detail = $detail }
    if ($kp['kind'] -eq 'defect-twin') { $row['noteA'] = (NoteOn $a); $row['noteB'] = (NoteOn $b) }
    $rows += $row
}

Write-Host ""
Write-Host "Run: $translated of $count segments have a translation$(if ($missing.Count) { '; none for ' + ($missing -join ', ') } else { '' })."
if ($unknownSources) { Write-Host "  $unknownSources unit(s) in the backup are not from this document and were ignored." }
if ($haveLog) {
    Write-Host "Prompt log: $requests request(s) for this document; $tmSent with TM matches$(if ($tmSent) { ' (the run is NOT comparable: untick Send TM matches to the AI)' } else { '' }); $blockRequests with earlier translations ($earlierShown shown, $blockChars characters, about $([int]($blockChars / 4)) tokens)."
}
Write-Host ""
Write-Host ("{0,-18} {1,-9} {2,-11} {3,-28} {4}" -f 'Kind', 'Segments', 'Finder', 'Earlier translation', 'Result')
foreach ($r in $rows) {
    $extra = $r['detail']
    if ($r.Contains('noteA')) { $extra = "note on $($r['a']): $($r['noteA']); on $($r['b']): $($r['noteB'])" }
    Write-Host ("{0,-18} {1,-9} {2,-11} {3,-28} {4}{5}" -f $r['kind'], "$($r['a'])+$($r['b'])", $r['finder'], $r['shown'], $r['result'], $(if ($extra) { "  ($extra)" } else { '' }))
}

$judged = @($rows | Where-Object { $_['finder'] -eq 'pairs them' })
$agree = @($judged | Where-Object { $_['result'] -eq 'agree' }).Count
$differ = @($judged | Where-Object { $_['result'] -eq 'DIFFER' }).Count
$unmeasured = $judged.Count - $agree - $differ
$shownCount = @($rows | Where-Object { $_['shown'] -eq 'shown' }).Count
$shownAgree = @($rows | Where-Object { $_['shown'] -eq 'shown' -and $_['result'] -eq 'agree' }).Count
$shownDiffer = @($rows | Where-Object { $_['shown'] -eq 'shown' -and $_['result'] -eq 'DIFFER' }).Count
Write-Host ""
Write-Host "Totals: of $($judged.Count) pairs that must agree, $agree agree, $differ differ, $unmeasured not measured."
if ($haveLog) { Write-Host "        $shownCount pair(s) where one was shown the other's translation: $shownAgree agree, $shownDiffer differ." }

# ---- words that invite another English word each time -----------------------------------------
Write-Host ""
Write-Host "Ordinary words, and the English each occurrence got (not checked by steps 1 or 2):"
$syn = @()
foreach ($w in $k['synonyms']) {
    $seen = @{}
    $per = @()
    foreach ($n in $w['segments']) {
        if (-not $target.ContainsKey([int]$n)) { continue }
        $t = (Plain $target[[int]$n]).ToLowerInvariant()
        $hit = @($w['english'] | Where-Object { $t -match ('\b' + [regex]::Escape([string]$_)) })
        $name = if ($hit.Count) { ($hit | Select-Object -First 1) + '-' } else { '?' }
        $seen[$name] = 1 + [int]$seen[$name]
        $per += "$n`:$name"
    }
    $variants = @($seen.Keys | Where-Object { $_ -ne '?' })
    Write-Host ("  {0,-12} {1} variant(s): {2}" -f $w['word'], $variants.Count, (($seen.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Value)" }) -join ', '))
    $syn += [ordered]@{ word = [string]$w['word']; variants = $variants.Count; occurrences = $per }
}

if ($Json) {
    $out = [ordered]@{ segments = $count; translated = $translated; missing = @($missing); keyProblems = @($problems)
        requests = $requests; tmRequests = $tmSent; blockRequests = $blockRequests; earlierShown = $earlierShown; blockChars = $blockChars
        pairs = $rows; agree = $agree; differ = $differ; unmeasured = $unmeasured; shown = $shownCount; shownAgree = $shownAgree; shownDiffer = $shownDiffer
        synonyms = $syn }
    [IO.File]::WriteAllText($Json, ($out | ConvertTo-Json -Depth 8 -Compress), (New-Object Text.UTF8Encoding($false)))
}
if ($problems.Count -gt 0) { exit 2 }
