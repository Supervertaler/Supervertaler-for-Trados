# "Open example project" (first-run plan, step 1): the parts that run without Studio.
#
# Runs the BUILT plugin's own code with Studio's own TM engine. No Studio is
# started, no project is made, no AI request is sent. Writes only under -Root
# (%TEMP%), and sets the plugin's data folder there first.
#
#   A  the shipped data: ids unique, every TM entry and term in place, which
#      languages are complete
#   B  languages: which data serves which culture (de-AT, pt-BR, zh-TW, en-GB),
#      directions refused (into English without data, English both sides),
#      an incomplete language counts as not prepared, the first guess
#   C  name and folder: free name, someone else's folder, an earlier example
#      (same languages / other languages), a registered name, a gap at "(2)"
#   D  the Word document: every text is in it, bold, italic, the hyperlink,
#      two tables, two numbered lists that each start at 1; into English
#   E  the TM, built with Studio's TM API: ten units, and every planted match
#      comes back from Studio's own fuzzy search in its intended band
#   F  the termbase: made in a new database, fifteen terms, this project only
#      (another termbase off for TermLens and AI), replaced on a second run,
#      removed again when filling it fails
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\example_project_test.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\example_project_test.ps1 -Dll <Supervertaler.Trados.dll>
# A Studio 2024 / 2022 build is 32-bit: the script starts the 32-bit PowerShell
# for it by itself.
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Debug\Supervertaler.Trados.dll"),
    [string]$Root = (Join-Path $env:TEMP "sv-example-project-test"),
    [switch]$Child
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

if (-not $Child) {
    $real = @('D:\Supervertaler\trados\logs\diagnostic.log', 'D:\Supervertaler\trados\settings\settings.json',
              'D:\Supervertaler\resources\supervertaler.db')
    function Stamp { ($real | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } else { "$_|absent" } }) -join "`n" }
    $before = Stamp
    $ps = if ($Dll -like '*Studio19*') { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
          else { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
    Write-Host "Plugin: $Dll"
    & $ps -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Child -Dll $Dll -Root $Root
    $code = $LASTEXITCODE
    if ((Stamp) -ne $before) { Write-Host "NOTE  a real settings, log or termbase file changed during the test (a running Studio writes to them too)" }
    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue }
    if ($code -ne 0) { Write-Host "RESULT: FAILED"; exit 1 }
    Write-Host "RESULT: all passed"
    exit 0
}

# -- Child ---------------------------------------------------------------------
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'data') | Out-Null
$studio = if ($Dll -like '*Studio19*') { 'C:\Program Files\Trados\Trados Studio\Studio19' }
          elseif ($Dll -like '*Studio17*') { 'C:\Program Files (x86)\Trados\Trados Studio\Studio17' }
          else { 'C:\Program Files (x86)\Trados\Trados Studio\Studio18' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class ExampleTestResolver
{
    // By name, whatever version is asked for: Studio itself redirects every
    // version to the one it ships (SDLTradosStudio.exe.config; Studio 2024's TM
    // engine asks for Microsoft.Extensions.Logging.Abstractions 8.0 and gets
    // 10.0). Remembered, so a second request for the same name gets the same one.
    private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    public static void Register(string[] dirs)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            var n = new AssemblyName(e.Name).Name;
            lock (Loaded)
            {
                Assembly a;
                if (Loaded.TryGetValue(n, out a)) return a;
                foreach (var d in dirs)
                {
                    var c = Path.Combine(d, n + ".dll");
                    if (File.Exists(c)) { try { a = Assembly.LoadFrom(c); } catch { a = null; } break; }
                }
                Loaded[n] = a;
                return a;
            }
        };
    }
}
'@
[ExampleTestResolver]::Register(@((Split-Path $Dll), $studio))
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$dataRoot = [string](Join-Path $Root 'data')
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @($dataRoot))
$logPath = [string]$plugin.GetType("Supervertaler.Trados.Core.DiagnosticLog", $true).GetProperty("LogFilePath").GetValue($null)
if (-not $logPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "The diagnostic log would be written to $logPath, outside the test root." }

$script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what"; $script:fail++ }
}
$EP = $plugin.GetType('Supervertaler.Trados.Core.ExampleProject', $true)
$EPS = $plugin.GetType('Supervertaler.Trados.Core.ExampleProjectStudio', $true)
$NP = [Reflection.BindingFlags]'NonPublic,Public,Static'
function S([string]$name) { $EPS.GetMethod($name, $NP) }

$data = $EP::Load()

# == A  the shipped data ========================================================
Write-Host "-- A  the shipped data"
$docIds = @($EP::DocumentIds($data))
Check ($docIds.Count -ge 50) "the document has $($docIds.Count) texts (at least 50)"
Check (($docIds | Sort-Object -Unique).Count -eq $docIds.Count) 'document ids are unique'
$tmIds = @($data.Tm | ForEach-Object { $_.Id })
Check (($tmIds | Sort-Object -Unique).Count -eq $tmIds.Count -and $tmIds.Count -eq 10) 'ten TM entries, ids unique'
Check (@($data.Tm | Where-Object { $docIds -notcontains $_.For }).Count -eq 0) 'every TM entry points at a document text'
Check (@($data.Terms).Count -eq 15 -and (@($data.Terms | ForEach-Object { $_.Id }) | Sort-Object -Unique).Count -eq 15) 'fifteen terms, ids unique'
$allText = (@($data.Tm | % { $_.Source }) + @($data.Document | ForEach-Object { $_.Text; if ($_.Items) { $_.Items | % { $_.Text } }; if ($_.Rows) { $_.Rows | % { $_ | % { $_.Text } } } }) -join "`n").ToLowerInvariant()
$absent = @($data.Terms | Where-Object { -not $allText.Contains($_.Source.ToLowerInvariant().Substring(0, [Math]::Min(6, $_.Source.Length))) })
Check ($absent.Count -eq 0) ('every term occurs in the document or a TM entry' + $(if ($absent.Count) { ': not ' + (($absent | % { $_.Source }) -join ', ') }))
$complete = @($data.Languages.Keys | Where-Object { $EP::Missing($data, $_).Count -eq 0 })
Write-Host "INFO  prepared languages complete: $($complete.Count) of $($data.Languages.Count) ($($complete -join ', '))"
foreach ($k in $data.Languages.Keys) {
    $m = @($EP::Missing($data, $k))
    Check ($m.Count -eq 0) "language $k is complete$(if ($m.Count) { ': missing ' + ($m[0..([Math]::Min(4, $m.Count - 1))] -join ', ') })"
}

# A fake set of languages for B and D, built from the English: "[xx] text".
function FakeData([string[]]$keys, [string]$incomplete) {
    $json = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Supervertaler.Trados\Resources\example-project\example-project.json'))
    $d = $EP::Parse($json)
    $d.Languages.Clear()
    foreach ($k in $keys) {
        $l = New-Object Supervertaler.Trados.Core.ExampleLanguage
        foreach ($b in $d.Document) {
            if ($b.Id) { $l.Document[$b.Id] = "[$k] " + $b.Text }
            if ($b.Items) { foreach ($i in $b.Items) { $l.Document[$i.Id] = "[$k] " + $i.Text } }
            if ($b.Rows) { foreach ($r in $b.Rows) { foreach ($c in $r) { $l.Document[$c.Id] = "[$k] " + $c.Text } } }
        }
        foreach ($t in $d.Tm) { $l.Tm[$t.Id] = "[$k] " + $t.Source }
        foreach ($t in $d.Terms) { $l.Terms[$t.Id] = "[$k] " + $t.Source }
        if ($k -eq $incomplete) { [void]$l.Terms.Remove('w3') }
        $d.Languages[$k] = $l
    }
    return $d
}

# == B  languages ===============================================================
Write-Host "-- B  languages"
$fake = FakeData @('de', 'pt', 'pt-BR', 'zh-Hans', 'nl') 'nl'
$cases = @(@('de-DE', 'de'), @('de-AT', 'de'), @('pt-PT', 'pt'), @('pt-BR', 'pt-BR'), @('zh-CN', 'zh-Hans'),
           @('zh-TW', $null), @('en-GB', $null), @('fr-FR', $null), @('', $null))
foreach ($c in $cases) {
    $got = $EP::DataKeyFor($fake, $c[0])
    Check ($got -eq $c[1]) "data for '$($c[0])' is '$($c[1])' (got '$got')"
}
$onlyPt = FakeData @('pt') $null
Check ($null -eq $EP::DataKeyFor($onlyPt, 'pt-BR')) 'Brazil does not get Portugal''s data'
$err = $null
$d1 = $EP::Direction($fake, 'de-AT', $false, 'en-GB', [ref]$err)
Check ($d1 -and $d1.Source -eq 'en-GB' -and $d1.Target -eq 'de-AT' -and $d1.DataKey -eq 'de' -and -not $d1.IntoEnglish) 'English (UK) into German (Austria), German data'
$d2 = $EP::Direction($fake, 'de-DE', $true, 'en-US', [ref]$err)
Check ($d2 -and $d2.Source -eq 'de-DE' -and $d2.Target -eq 'en-US' -and $d2.IntoEnglish) 'German into English (US)'
$d3 = $EP::Direction($fake, 'fr-FR', $true, 'en-US', [ref]$err)
Check ($null -eq $d3 -and $err -like '*not yet available in French*') 'into English refused for a language that is not prepared'
$d4 = $EP::Direction($fake, 'fr-FR', $false, 'en-US', [ref]$err)
Check ($d4 -and -not $d4.Prepared) 'English into French: document only'
$d5 = $EP::Direction($fake, 'nl-NL', $false, 'en-US', [ref]$err)
Check ($d5 -and -not $d5.Prepared) 'a language with a missing term counts as not prepared'
$d6 = $EP::Direction($fake, 'en-GB', $false, 'en-US', [ref]$err)
Check ($null -eq $d6 -and $err -like '*other language*') 'English on both sides refused'
$d7 = $EP::Direction($fake, 'xx-YY', $false, 'en-US', [ref]$err)
Check ($null -eq $d7) 'an unknown language refused'
$g = $EP::Guess('en-US', 'de-DE', 'en-GB'); Check ($g.Item1 -eq 'de-DE' -and -not $g.Item2) 'guess: the open project''s target'
$g = $EP::Guess('nl-NL', 'en-GB', 'en-GB'); Check ($g.Item1 -eq 'nl-NL' -and $g.Item2) 'guess: the open project''s source, into English'
$g = $EP::Guess($null, $null, 'pl-PL'); Check ($g.Item1 -eq 'pl-PL' -and -not $g.Item2) 'guess: Windows'' language'
$g = $EP::Guess($null, $null, 'en-US'); Check ($null -eq $g.Item1) 'guess: nothing on an English Windows with no project'
Check ($EP::ProjectName($d1) -eq 'Supervertaler example (English to German)') "name: $($EP::ProjectName($d1))"
Check ($EP::ProjectName($d2) -eq 'Supervertaler example (German to English)') "name: $($EP::ProjectName($d2))"
$dBr = $EP::Direction($fake, 'pt-BR', $false, 'en-US', [ref]$err)
Check ($EP::ProjectName($dBr) -eq 'Supervertaler example (English to Portuguese (Brazil))') "name: $($EP::ProjectName($dBr))"

# == C  name and folder =========================================================
Write-Host "-- C  name and folder"
$proj = Join-Path $Root 'Projects'
New-Item -ItemType Directory -Force -Path $proj | Out-Null
$none = New-Object 'System.Collections.Generic.List[string]'
$p = $EP::PlanFolder($proj, $d1, $none)
Check ($p.Name -eq 'Supervertaler example (English to German)' -and -not $p.ExistingProjectFile) 'nothing there: the plain name'
New-Item -ItemType Directory -Force -Path (Join-Path $proj $p.Name) | Out-Null
Set-Content -LiteralPath (Join-Path $proj "$($p.Name)\mine.txt") -Value 'user file'
$p = $EP::PlanFolder($proj, $d1, $none)
Check ($p.Name -eq 'Supervertaler example (English to German) (2)' -and -not $p.ExistingProjectFile) 'someone else''s folder: (2), and it is left alone'
Check (Test-Path -LiteralPath (Join-Path $proj 'Supervertaler example (English to German)\mine.txt')) 'the user''s file is still there'
# An earlier example at (3), (2) free.
$ex3 = Join-Path $proj 'Supervertaler example (English to German) (3)'
New-Item -ItemType Directory -Force -Path $ex3 | Out-Null
$m = New-Object Supervertaler.Trados.Core.ExampleMarker
$m.Source = 'en-GB'; $m.Target = 'de-AT'; $m.Termbase = 'Supervertaler example (English to German) (3)'
$EP::WriteMarker($ex3, $m)
Set-Content -LiteralPath (Join-Path $ex3 'x.sdlproj') -Value '<Project/>'
$p = $EP::PlanFolder($proj, $d1, $none)
Check ($p.ExistingProjectFile -like '*(3)\x.sdlproj' -and $p.Name -like '*German) (2)') 'an earlier example at (3) is found past a free (2)'
$dOther = $EP::Direction($fake, 'de-DE', $false, 'en-GB', [ref]$err)
$p = $EP::PlanFolder($proj, $dOther, $none)
Check (-not $p.ExistingProjectFile -and $p.Name -like '*German) (2)') 'an example for other languages (de-AT, not de-DE) is not this one'
$reg = New-Object 'System.Collections.Generic.List[string]'; $reg.Add('Supervertaler example (English to German) (2)')
$p = $EP::PlanFolder($proj, $d1, $reg)
Check ($p.Name -like '*German) (4)') 'a name in Studio''s Projects list is not used, even without a folder'
Remove-Item -LiteralPath (Join-Path $ex3 'x.sdlproj')
$p = $EP::PlanFolder($proj, $d1, $none)
Check (-not $p.ExistingProjectFile) 'a marker without a project file is not an example project'
$p = $EP::PlanFolder('', $d1, $none)
Check ($p.Error -like '*Projects folder*') 'no Projects folder: said so'

# == D  the Word document =======================================================
Write-Host "-- D  the Word document"
$docx = Join-Path $Root 'en.docx'
$EP::WriteDocx($docx, $data, $d1)
$wd = [DocumentFormat.OpenXml.Packaging.WordprocessingDocument]::Open($docx, $false)
try {
    $body = $wd.MainDocumentPart.Document.Body
    $paras = @($body.Descendants() | Where-Object { $_ -is [DocumentFormat.OpenXml.Wordprocessing.Paragraph] } | ForEach-Object { $_.InnerText })
    $missingText = @($docIds | Where-Object {
        $id = $_
        $b = @($data.Document | % { $_; if ($_.Items) { $_.Items }; if ($_.Rows) { $_.Rows | % { $_ } } } | Where-Object { $_.Id -eq $id })[0]
        $paras -notcontains $EP::PlainText($b.Text) })
    Check ($missingText.Count -eq 0) "every text is a paragraph of its own$(if ($missingText.Count) { ': not ' + ($missingText -join ', ') })"
    $bold = @($body.Descendants() | Where-Object { $_ -is [DocumentFormat.OpenXml.Wordprocessing.Bold] }).Count
    $ital = @($body.Descendants() | Where-Object { $_ -is [DocumentFormat.OpenXml.Wordprocessing.Italic] }).Count
    $links = @($body.Descendants() | Where-Object { $_ -is [DocumentFormat.OpenXml.Wordprocessing.Hyperlink] }).Count
    $tables = @($body.Elements() | Where-Object { $_ -is [DocumentFormat.OpenXml.Wordprocessing.Table] }).Count
    Check ($bold -ge 4 -and $ital -ge 2 -and $links -eq 1) "bold $bold, italic $ital, hyperlinks $links"
    Check ($tables -eq 2) "$tables tables"
    $inst = @($wd.MainDocumentPart.NumberingDefinitionsPart.Numbering.Elements() | Where-Object { $_ -is [DocumentFormat.OpenXml.Wordprocessing.NumberingInstance] }).Count
    Check ($inst -eq 3) "three list instances (bullets, two numbered lists that each restart): $inst"
    $errors = @((New-Object DocumentFormat.OpenXml.Validation.OpenXmlValidator).Validate($wd))
    Check ($errors.Count -eq 0) "valid Office Open XML$(if ($errors.Count) { ': ' + $errors[0].Description })"
} finally { $wd.Dispose() }
$docx2 = Join-Path $Root 'de.docx'
$EP::WriteDocx($docx2, $fake, $d2)
$wd = [DocumentFormat.OpenXml.Packaging.WordprocessingDocument]::Open($docx2, $false)
try {
    $t = $wd.MainDocumentPart.Document.Body.InnerText
    Check ($t.Contains('[de] Disconnect the appliance') -and -not $t.Contains('Disconnect the appliance from the mains before opening the cover.Read')) 'into English: the German document is the source'
} finally { $wd.Dispose() }

# == E  the TM, with Studio's TM engine ========================================
Write-Host "-- E  the TM and the planted matches"
$units = $EP::TmUnits($fake, $d1)
Check ($units.Count -eq 10 -and $units[0].Value.StartsWith('[de] ')) 'ten units, the target from the language data'
Check ($EP::TmUnits($fake, $d4).Count -eq 0) 'no units for a language that is not prepared'
$unitsRev = $EP::TmUnits($fake, $d2)
Check ($unitsRev[0].Key.StartsWith('[de] ') -and -not $unitsRev[0].Value.StartsWith('[')) 'into English: German source, English target'
$tmPath = Join-Path $Root 'tm\example.sdltm'
New-Item -ItemType Directory -Force -Path (Split-Path $tmPath) | Out-Null
(S 'CreateTmFile').Invoke($null, @([string]$tmPath, 'en-US', 'de-AT', $units))
$tm = New-Object Sdl.LanguagePlatform.TranslationMemoryApi.FileBasedTranslationMemory($tmPath)
Check ($tm.LanguageDirection.GetTranslationUnitCount() -eq 10) "the TM holds $($tm.LanguageDirection.GetTranslationUnitCount()) units"
Check ($tm.LanguageDirection.TargetLanguage.Name -eq 'de-AT') "the TM is en-US to de-AT (exactly the project's languages)"
$settings = New-Object Sdl.LanguagePlatform.TranslationMemory.SearchSettings
$settings.Mode = [Sdl.LanguagePlatform.TranslationMemory.SearchMode]::NormalSearch
$settings.MinScore = 30; $settings.MaxResults = 5
$src = [Globalization.CultureInfo]::GetCultureInfo('en-US')
function FirstSentence([string]$t) { $t = $EP::PlainText($t); $i = $t.IndexOf('. '); if ($i -gt 0) { $t.Substring(0, $i + 1) } else { $t } }
$allBlocks = @($data.Document | % { $_; if ($_.Items) { $_.Items }; if ($_.Rows) { $_.Rows | % { $_ } } })
$TFL = $plugin.GetType('Supervertaler.Trados.Core.TmFuzzyLookup', $true)
foreach ($e in $data.Tm) {
    $text = FirstSentence (@($allBlocks | Where-Object { $_.Id -eq $e.For })[0].Text)
    $seg = New-Object Sdl.LanguagePlatform.Core.Segment($src)
    $seg.Add($text)
    $res = $tm.LanguageDirection.SearchSegment($settings, $seg)
    $best = 0
    foreach ($r in $res.Results) { if ($r.MemoryTranslationUnit.SourceSegment.ToPlain() -eq $e.Source -and $r.ScoringResult.Match -gt $best) { $best = $r.ScoringResult.Match } }
    # What FuzzyFixer makes of it, with its default lowest score of 70.
    $ff = $TFL::Describe($text, $e.Source, 'target', $best, $false, 70, 'example')
    $ok = switch ($e.Band) {
        '100'     { $best -eq 100 -and $ff -and $ff.Score -eq 100 }
        'numbers' { $ff -and $ff.Score -eq 99 -and $ff.Numbers.ToString() -ne 'None' }
        'fuzzy'   { $best -ge 70 -and $best -le 99 -and $ff -and $ff.Score -eq $best }
        'low'     { $best -lt 70 -and $null -eq $ff }
        default   { $false }
    }
    $shown = if ($ff) { "FuzzyFixer sees $($ff.Score)%" } else { 'FuzzyFixer leaves it' }
    Check $ok ("{0} Studio {1,3}%, {2} (band {3}) for '{4}'" -f $e.Id, $best, $shown, $e.Band, $text)
}
$tm = $null

# Into English, every prepared language: the TM's source is that language now,
# and the scores follow that language's wording, so entries designed to sit
# near 70% land either side of it. What the demo needs, and what is checked:
# the exact matches at 100%, and at least four matches FuzzyFixer repairs.
# Entries outside their English band are listed, not failed.
$checkedRev = 0
foreach ($k in @($data.Languages.Keys | Sort-Object)) {
    if ($EP::Missing($data, $k).Count -gt 0) { continue }
    $cult = switch ($k) { 'pt' { 'pt-PT' } 'zh-Hans' { 'zh-CN' } default { [Globalization.CultureInfo]::CreateSpecificCulture($k).Name } }
    $dir = $EP::Direction($data, $cult, $true, 'en-US', [ref]$err)
    if (-not $dir) { Check $false "into English from $k ($cult): $err"; continue }
    $u = $EP::TmUnits($data, $dir)
    $p2 = [string](Join-Path $Root "tm\rev-$k.sdltm")
    (S 'CreateTmFile').Invoke($null, [object[]]@($p2, $cult, 'en-US', $u.PSObject.BaseObject))
    $tmr = New-Object Sdl.LanguagePlatform.TranslationMemoryApi.FileBasedTranslationMemory($p2)
    $c = [Globalization.CultureInfo]::GetCultureInfo($cult)
    $bad = @(); $shown = @(); $off = @(); $repairable = 0
    foreach ($e in $data.Tm) {
        $text = FirstSentence ($data.Languages[$k].Document[$e.For])
        $tsrc = $EP::PlainText($data.Languages[$k].Tm[$e.Id])
        $seg = New-Object Sdl.LanguagePlatform.Core.Segment($c); $seg.Add($text)
        $best = 0
        foreach ($r in $tmr.LanguageDirection.SearchSegment($settings, $seg).Results) {
            if ($r.MemoryTranslationUnit.SourceSegment.ToPlain() -eq $tsrc -and $r.ScoringResult.Match -gt $best) { $best = $r.ScoringResult.Match } }
        $ff = $TFL::Describe($text, $tsrc, 'target', $best, $false, 70, 'example')
        if ($e.Band -eq '100' -and $best -ne 100) { $bad += "$($e.Id) $best% (exact match expected)" }
        if ($ff -and $ff.Score -ge 70 -and $ff.Score -le 99) { $repairable++ }
        $inBand = switch ($e.Band) {
            '100'     { $best -eq 100 }
            'numbers' { $ff -and $ff.Score -ge 99 }
            'fuzzy'   { $ff -and $ff.Score -ge 70 -and $ff.Score -le 99 }
            'low'     { $null -eq $ff }
        }
        $shown += "$($e.Id) $best"
        if (-not $inBand -and $e.Band -ne '100') { $off += "$($e.Id) $best% (band $($e.Band))" }
    }
    if ($repairable -lt 4) { $bad += "only $repairable matches FuzzyFixer repairs" }
    $tmr = $null
    Check ($bad.Count -eq 0) ("into English from {0}: {1} repairable; {2}{3}{4}" -f $cult, $repairable, ($shown -join ', '),
        $(if ($off.Count) { '  (outside the English band: ' + ($off -join '; ') + ')' }), $(if ($bad.Count) { '  PROBLEM: ' + ($bad -join '; ') }))
    $checkedRev++
}
Write-Host "INFO  into-English directions checked: $checkedRev"

# == F  the termbase ============================================================
Write-Host "-- F  the termbase"
$TR = $plugin.GetType('Supervertaler.Trados.Core.TermbaseReader', $true)
$PS = $plugin.GetType('Supervertaler.Trados.Settings.ProjectSettings', $true)
$db = (S 'DatabasePath').Invoke($null, @($true))
Check ($db.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $db)) "a first run makes a database in the data folder: $db"
$otherId = $TR::CreateTermbase($db, 'Acme client terms', 'en-US', 'de-DE')
$fakeProj = Join-Path $proj 'fake\fake.sdlproj'
New-Item -ItemType Directory -Force -Path (Split-Path $fakeProj) | Out-Null
Set-Content -LiteralPath $fakeProj -Value '<Project/>'
$terms = $EP::TermPairs($fake, $d1)
Check ($terms.Count -eq 15) 'fifteen term pairs'
$tbName = 'Supervertaler example (English to German)'
function TermbasesNamed([string]$n) {
    $r = [Activator]::CreateInstance($TR, @($db)); try { [void]$r.Open(); @($r.GetTermbases() | Where-Object { $_.Name -eq $n }) } finally { $r.Dispose() }
}
$why = (S 'AddTermbase').Invoke($null, [object[]]@([string]$fakeProj, $tbName, $d1, $terms.PSObject.BaseObject))
Check ($null -eq $why) "termbase made$(if ($why) { ': ' + $why })"
$tb = @(TermbasesNamed $tbName)
Check ($tb.Count -eq 1) 'one example termbase'
$n = $TR::GetAllTermsByTermbaseId($db, $tb[0].Id).Count
Check ($n -eq 15) "it holds $n terms"
$ps1 = $PS::Load($fakeProj)
Check ($ps1.ProjectTermbaseId -eq $tb[0].Id -and $ps1.TermbasePath -eq $db) 'the project uses it, from this database'
Check ($ps1.DisabledTermbaseIds -contains $otherId -and $ps1.DisabledAiTermbaseIds -contains $otherId -and -not ($ps1.DisabledTermbaseIds -contains $tb[0].Id)) 'in the example project: the example on, the user''s other termbase off (TermLens and AI)'
$glob = $plugin.GetType('Supervertaler.Trados.Settings.SettingsService', $true)::Current
Check ($glob.DisabledTermbaseIds -contains $tb[0].Id -and $glob.AiSettings.DisabledAiTermbaseIds -contains $tb[0].Id) 'everywhere else: the example termbase off (TermLens and AI)'
$why = (S 'AddTermbase').Invoke($null, [object[]]@([string]$fakeProj, $tbName, $d1, $terms.PSObject.BaseObject))
$tb2 = @(TermbasesNamed $tbName)
Check ($null -eq $why -and $tb2.Count -eq 1 -and $tb2[0].Id -ne $tb[0].Id) 'a second run replaces it (still one, new id)'
# A list with an empty term: refused before anything is made. (A failure
# half-way through filling deletes the termbase again; that path is read, not
# provoked: the database takes any term once the list is checked.)
$bad = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
$bad.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' @('water heater', 'Warmwasserbereiter')))
$bad.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' @($null, $null)))
$why = (S 'AddTermbase').Invoke($null, [object[]]@([string]$fakeProj, 'Supervertaler example (broken)', $d1, $bad.PSObject.BaseObject))
Check ($null -ne $why -and @(TermbasesNamed 'Supervertaler example (broken)').Count -eq 0) "an empty term: refused, and no termbase made ($why)"
Check (@(TermbasesNamed 'Acme client terms').Count -eq 1) 'the user''s own termbase is untouched'

if ($script:fail -gt 0) { Write-Host "$($script:fail) check(s) failed"; exit 1 }
exit 0
