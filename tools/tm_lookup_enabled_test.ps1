# Batch Translate's TM matches ("Send TM matches to the AI"): which of the
# project's translation memories are searched. Seen 14 Nov 2026: memories whose
# Enabled and/or Lookup box was unticked in Project Settings > Language Pairs >
# Translation Memory and Automated Translation were searched all the same.
#
# Runs the BUILT plugin's TmSearcher.FindLookupTms on project files written
# here (invented names, empty .sdltm files - the function only asks whether a
# memory's file exists, it does not open it) and TmSearcher.SkippedTmsSentence,
# the line the Batch Translate log shows. No Studio is started.
#
#   1  on, Enabled unticked, Lookup unticked: only the first is searched, and
#      the log sentence counts them
#   2  all on: all searched, no sentence; all off: none, and the sentence
#   3  a .sdltm lying in the project's Tm folder that is not in the list, or is
#      in it but switched off: not searched
#   4  a language pair with its own list, and one without (whose stale copy in
#      the project file must not be read)
#   5  EverTM in the list: on, Enabled unticked, Lookup unticked, old name
#   6  what is not a TM (machine translation) and a memory whose file is gone
#   7  a GroupShare memory; a project TM beside its main TM; one memory twice
#   8  paths: relative, spaces and parentheses, percent-escaped
#   9  failure modes: no project, an unreadable project file, no list at all
#  10  the log sentence's wording
#  11  scale: 2,000 memories in the list, every other one switched off
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\tm_lookup_enabled_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# The code is the same in a 32-bit process; to run it against a Studio 2024 /
# 2022 build use C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe.
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set), deleted
# at the end, also when a check throws.
#
# What it cannot reach: Studio itself. That the project file says
# Enabled="false" / PerformNormalSearch="false" when the boxes are unticked,
# and OverrideParent="true" when a language pair has its own list, is taken
# from project files Studio wrote, not from a running Studio; and the run's log
# line is shown by AiAssistantViewPart, which needs an open document.
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

Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class TmLookupTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[TmLookupTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# Its own data folder, before anything of the plugin runs.
$temp = Join-Path $env:TEMP ("sv-tm-lookup-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
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

$searcher = $asm.GetType('Supervertaler.Trados.Core.TmSearcher', $true)
$find = $searcher.GetMethod('FindLookupTms', [Type[]]@([string], [string], [string]))
$sentence = $searcher.GetMethod('SkippedTmsSentence')
$findAll = $searcher.GetMethod('FindProjectTms')

# One line of the TM list, as Studio writes it.
function Entry([string]$uri, [string]$enabled = 'true', [string]$lookup = 'true', [string]$projectTmUri = $null) {
    $u = [System.Security.SecurityElement]::Escape($uri)
    $proj = if ($projectTmUri) { "<ProjectTranslationProviderItem Uri=`"$([System.Security.SecurityElement]::Escape($projectTmUri))`" Enabled=`"true`" />" } else { '' }
    "<CascadeEntryItem PerformConcordanceSearch=`"true`" Penalty=`"0`" PerformUpdate=`"false`" PerformNormalSearch=`"$lookup`"><MainTranslationProviderItem Uri=`"$u`" State=`"`" Enabled=`"$enabled`" />$proj</CascadeEntryItem>"
}
function Pair([string]$src, [string]$tgt, [string]$override, [string]$entries) {
    "<LanguageDirection Guid=`"$([guid]::NewGuid())`" TargetLanguageCode=`"$tgt`" SourceLanguageCode=`"$src`"><AutoSuggestDictionaries /><CascadeItem OverrideParent=`"$override`" StopSearchingWhenResultsFound=`"false`">$entries</CascadeItem></LanguageDirection>"
}
# A project folder with a project file; returns the path of a document in it.
function Project([string]$name, [string]$projectEntries, [string]$pairs) {
    $dir = Join-Path $temp $name
    New-Item -ItemType Directory -Path (Join-Path $dir 'en-GB') -Force | Out-Null
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><Project Version=`"4.0.0.0`"><LanguageDirections>$pairs</LanguageDirections><CascadeItem OverrideParent=`"true`" StopSearchingWhenResultsFound=`"false`">$projectEntries</CascadeItem><GeneralProjectInfo Name=`"PROJ-001`" /></Project>"
    [IO.File]::WriteAllText((Join-Path $dir "$name.sdlproj"), $xml, (New-Object System.Text.UTF8Encoding($true)))
    Join-Path $dir 'en-GB\Acme manual.docx.sdlxliff'
}
function Tm([string]$path) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllBytes($path, [byte[]]@())
    $path
}
function FileUri([string]$path) { 'sdltm.file:///' + $path }
function Find([string]$doc, [string]$src = 'nl-NL', [string]$tgt = 'en-GB') { $find.Invoke($null, @([string]$doc, [string]$src, [string]$tgt)) }
function Names($r) { (@($r.Tms) | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) -join ',' }
function Say($r) { [string]$sentence.Invoke($null, @([int]$r.Searched, [int]$r.Total)) }

try {
    $tms = Join-Path $temp 'TMs'
    $a = Tm (Join-Path $tms 'Acme main.sdltm')
    $b = Tm (Join-Path $tms 'Acme old.sdltm')
    $c = Tm (Join-Path $tms 'Acme reference.sdltm')
    $ownPair = Pair 'nl-NL' 'en-GB' 'false' ''

    Write-Host "1 One on, one with Enabled unticked, one with Lookup unticked"
    $doc = Project 'P1' ((Entry (FileUri $a)) + (Entry (FileUri $b) 'false') + (Entry (FileUri $c) 'true' 'false')) $ownPair
    $r = Find $doc
    Check ((Names $r) -eq 'Acme main') "only the memory that is on is searched ($(Names $r))"
    Check ($r.Total -eq 3 -and $r.Searched -eq 1) "1 of 3 counted ($($r.Searched) of $($r.Total))"
    Check ((Say $r) -eq 'TM matches: 1 of 3 project TMs searched; 2 are switched off or have Lookup unticked in Project Settings.') "the log says so: $(Say $r)"
    Check (@($findAll.Invoke($null, @([string]$doc))).Count -eq 3) "the list for SuperSearch and the MCP tools still names all three"
    $doc = Project 'P1b' (Entry (FileUri $a) 'false' 'false') $ownPair
    Check (@((Find $doc).Tms).Count -eq 0) "both boxes unticked: not searched"

    Write-Host "2 All on; all off"
    $doc = Project 'P2' ((Entry (FileUri $a)) + (Entry (FileUri $b)) + (Entry (FileUri $c))) $ownPair
    $r = Find $doc
    Check ((Names $r) -eq 'Acme main,Acme old,Acme reference' -and $r.Total -eq 3 -and $r.Searched -eq 3) "all three searched, in name order ($(Names $r))"
    Check ([string]::IsNullOrEmpty((Say $r))) "nothing skipped: no line in the log"
    $doc = Project 'P2b' ((Entry (FileUri $a) 'false') + (Entry (FileUri $b) 'true' 'false')) $ownPair
    $r = Find $doc
    Check (@($r.Tms).Count -eq 0 -and -not $r.EverTm -and $r.Total -eq 2 -and $r.Searched -eq 0) "all switched off: nothing searched"
    Check ((Say $r) -eq 'TM matches: 0 of 2 project TMs searched; 2 are switched off or have Lookup unticked in Project Settings.') "and the log says why there are no TM matches: $(Say $r)"
    $doc = Project 'P2c' ('<CascadeEntryItem><MainTranslationProviderItem Uri="' + (FileUri $a) + '" /></CascadeEntryItem>') $ownPair
    Check ((Names (Find $doc)) -eq 'Acme main') "a line that says nothing about Enabled or Lookup counts as on, as before"

    Write-Host "3 A .sdltm in the project's Tm folder"
    $doc = Project 'P3' (Entry (FileUri $a)) $ownPair
    $inFolder = Tm (Join-Path (Split-Path -Parent (Split-Path -Parent $doc)) 'Tm\nl-NL-en-GB\Acme leftover.sdltm')
    $r = Find $doc
    Check ((Names $r) -eq 'Acme main' -and $r.Total -eq 1) "one that is not in the project's list is not searched ($(Names $r))"
    $doc = Project 'P3b' ((Entry (FileUri $a)) + (Entry 'sdltm.file:///Tm\nl-NL-en-GB\Acme project.sdltm' 'false')) $ownPair
    Tm (Join-Path (Split-Path -Parent (Split-Path -Parent $doc)) 'Tm\nl-NL-en-GB\Acme project.sdltm') | Out-Null
    $r = Find $doc
    Check ((Names $r) -eq 'Acme main' -and $r.Total -eq 2 -and $r.Searched -eq 1) "one that is in the list but switched off is not searched because its file is there ($(Names $r))"

    Write-Host "4 Language pairs"
    # nl-NL > en-GB has no list of its own; the copy it carries still has 'Acme old' ticked.
    $stale = Pair 'nl-NL' 'en-GB' 'false' ((Entry (FileUri $a)) + (Entry (FileUri $b)))
    $own = Pair 'nl-NL' 'de-DE' 'true' (Entry (FileUri $c))
    $doc = Project 'P4' ((Entry (FileUri $a)) + (Entry (FileUri $b) 'false')) ($stale + $own)
    $r = Find $doc 'nl-NL' 'en-GB'
    Check ((Names $r) -eq 'Acme main' -and $r.Total -eq 2) "a pair without its own list follows All Language Pairs, not the copy in the project file ($(Names $r))"
    $r = Find $doc 'nl-NL' 'de-DE'
    Check ((Names $r) -eq 'Acme reference' -and $r.Total -eq 1) "a pair with its own list uses that list ($(Names $r))"
    $r = Find $doc 'NL-nl' 'DE-de'
    Check ((Names $r) -eq 'Acme reference') "language codes are compared without regard to case"
    $r = Find $doc 'nl-NL' 'fr-FR'
    Check ((Names $r) -eq 'Acme main') "a pair the project file does not name: All Language Pairs"
    $r = Find $doc $null $null
    Check ((Names $r) -eq 'Acme main') "language pair unknown: All Language Pairs"
    $doc = Project 'P4b' (Entry (FileUri $a)) (Pair 'nl-NL' 'en-GB' 'true' (Entry (FileUri $b) 'true' 'false'))
    $r = Find $doc 'nl-NL' 'en-GB'
    Check (@($r.Tms).Count -eq 0 -and $r.Total -eq 1) "the pair's own list has its memory's Lookup unticked: not searched, and the project's list is not used instead"
    $r = Find $doc 'nl' 'en'
    Check (@($r.Tms).Count -eq 0 -and $r.Total -eq 1) "a project with one language pair: that pair's list, however the languages are named"

    Write-Host "5 EverTM in the list"
    $doc = Project 'P5' ((Entry (FileUri $a)) + (Entry 'evertm:///')) $ownPair
    $r = Find $doc
    Check ($r.EverTm -and (Names $r) -eq 'Acme main' -and $r.Total -eq 2 -and $r.Searched -eq 2) "on: searched, through its own provider and not as a file"
    $doc = Project 'P5b' ((Entry (FileUri $a)) + (Entry 'evertm:///' 'false')) $ownPair
    $r = Find $doc
    Check (-not $r.EverTm -and $r.Total -eq 2 -and $r.Searched -eq 1) "Enabled unticked: not searched, and counted as skipped"
    $doc = Project 'P5c' (Entry 'evertm:///' 'true' 'false') $ownPair
    $r = Find $doc
    Check (-not $r.EverTm -and $r.Total -eq 1 -and $r.Searched -eq 0) "Lookup unticked: not searched"
    Check ((Say $r) -eq 'TM matches: 0 of 1 project TM searched; 1 is switched off or has Lookup unticked in Project Settings.') "one memory, in the singular: $(Say $r)"
    $doc = Project 'P5d' (Entry 'supervault:///') $ownPair
    Check ((Find $doc).EverTm) "its name from before the rename (supervault:///) still counts"
    $doc = Project 'P5e' (Entry 'evertm:///' 'false') (Pair 'nl-NL' 'en-GB' 'false' (Entry 'evertm:///'))
    Check (-not (Find $doc).EverTm) "switched off for All Language Pairs, still ticked in a pair's stale copy: not searched"

    Write-Host "6 What is not a TM, and a memory whose file is gone"
    $doc = Project 'P6' ((Entry (FileUri $a)) + (Entry 'acmemachinetranslation:///' 'false') + (Entry (FileUri (Join-Path $tms 'Acme deleted.sdltm')) 'false')) $ownPair
    $r = Find $doc
    Check ((Names $r) -eq 'Acme main' -and $r.Total -eq 1 -and $r.Searched -eq 1) "neither is counted as a project TM ($($r.Searched) of $($r.Total))"
    Check ([string]::IsNullOrEmpty((Say $r))) "so the log does not report them as switched off"

    Write-Host "7 GroupShare, a project TM beside its main TM, one memory twice"
    $server = 'sdltm.https://groupshare.acme.example/?orgPath=/Acme&tmName=Acme server'
    $doc = Project 'P7' ((Entry $server) + (Entry ($server + ' 2') 'true' 'false')) $ownPair
    $r = Find $doc
    Check (@($r.Tms).Count -eq 1 -and $r.Tms[0] -eq $server -and $r.Total -eq 2) "a GroupShare memory follows the same rule"
    $doc = Project 'P7b' (Entry (FileUri $a) 'true' 'true' 'sdltm.file:///Tm\nl-NL-en-GB\Acme main_nl-NL_en-GB.sdltm') $ownPair
    Tm (Join-Path (Split-Path -Parent (Split-Path -Parent $doc)) 'Tm\nl-NL-en-GB\Acme main_nl-NL_en-GB.sdltm') | Out-Null
    $r = Find $doc
    Check (@($r.Tms).Count -eq 2 -and $r.Total -eq 1 -and $r.Searched -eq 1) "a main TM with a project TM: both searched as before, counted as one"
    $doc = Project 'P7c' (Entry (FileUri $a) 'true' 'false' 'sdltm.file:///Tm\nl-NL-en-GB\Acme main_nl-NL_en-GB.sdltm') $ownPair
    Tm (Join-Path (Split-Path -Parent (Split-Path -Parent $doc)) 'Tm\nl-NL-en-GB\Acme main_nl-NL_en-GB.sdltm') | Out-Null
    $r = Find $doc
    Check (@($r.Tms).Count -eq 0 -and $r.Total -eq 1) "Lookup unticked on that line: neither is searched"
    $doc = Project 'P7d' ((Entry (FileUri $a) 'false') + (Entry (FileUri $a.ToUpperInvariant()))) $ownPair
    $r = Find $doc
    Check (@($r.Tms).Count -eq 1 -and $r.Total -eq 1 -and $r.Searched -eq 1) "the same memory on two lines, one of them on: searched once, counted once"

    Write-Host "8 Paths"
    $odd = Tm (Join-Path $temp 'Acme (before) folder\Acme TM (2026) v1.sdltm')
    $doc = Project 'P8 (before)' ((Entry ('sdltm.file:///' + $odd.Replace(' ', '%20'))) + (Entry 'sdltm.file:///Tm\Acme relative.sdltm')) $ownPair
    Tm (Join-Path (Split-Path -Parent (Split-Path -Parent $doc)) 'Tm\Acme relative.sdltm') | Out-Null
    $r = Find $doc
    Check ((Names $r) -eq 'Acme relative,Acme TM (2026) v1') "spaces, parentheses, percent-escapes and a path relative to the project all resolve ($(Names $r))"

    Write-Host "9 Failure modes"
    $r = Find (Join-Path $temp 'nothing here\en-GB\doc.sdlxliff')
    Check ($null -ne $r -and @($r.Tms).Count -eq 0 -and -not $r.EverTm -and $r.Total -eq 0) "no project file: nothing, no error"
    $r = Find ''
    Check ($null -ne $r -and $r.Total -eq 0) "no path at all: nothing, no error"
    $bad = Join-Path $temp 'P9'
    New-Item -ItemType Directory -Path $bad | Out-Null
    [IO.File]::WriteAllText((Join-Path $bad 'P9.sdlproj'), '<Project><CascadeItem><CascadeEntryItem')
    $r = Find (Join-Path $bad 'doc.sdlxliff')
    Check ($null -ne $r -and @($r.Tms).Count -eq 0 -and $r.Total -eq 0) "a project file that cannot be read: nothing, no error"
    New-Item -ItemType Directory -Path (Join-Path $temp 'P9b') | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'P9b\P9b.sdlproj'), '<Project><GeneralProjectInfo Name="PROJ-001" /></Project>')
    $r = Find (Join-Path $temp 'P9b\doc.sdlxliff')
    Check ($null -ne $r -and $r.Total -eq 0) "a project file without a TM list: nothing, no error"
    $doc = Project 'P9c' ('<CascadeEntryItem PerformNormalSearch="true" />' + '<CascadeEntryItem><MainTranslationProviderItem Enabled="true" /></CascadeEntryItem>' + (Entry (FileUri $a))) $ownPair
    Check ((Names (Find $doc)) -eq 'Acme main') "lines without a memory are passed over"

    Write-Host "10 The log sentence"
    function S([int]$searched, [int]$total) { [string]$sentence.Invoke($null, @($searched, $total)) }
    Check ((S 2 3) -eq 'TM matches: 2 of 3 project TMs searched; 1 is switched off or has Lookup unticked in Project Settings.') "one skipped: $(S 2 3)"
    Check ((S 1 4) -eq 'TM matches: 1 of 4 project TMs searched; 3 are switched off or have Lookup unticked in Project Settings.') "several skipped: $(S 1 4)"
    Check ([string]::IsNullOrEmpty((S 3 3)) -and [string]::IsNullOrEmpty((S 0 0)) -and [string]::IsNullOrEmpty((S 5 3))) "nothing skipped, no TMs, or counts that make no sense: no sentence"

    Write-Host "11 Scale: 2,000 memories, every other one switched off"
    $many = Join-Path $temp 'Many'
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt 2000; $i++) {
        $p = Tm (Join-Path $many ("Acme {0:D4}.sdltm" -f $i))
        [void]$sb.Append((Entry (FileUri $p) $(if ($i % 2 -eq 0) { 'true' } else { 'false' })))
    }
    $doc = Project 'P11' $sb.ToString() $ownPair
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Find $doc
    $sw.Stop()
    Check (@($r.Tms).Count -eq 1000 -and $r.Total -eq 2000 -and $r.Searched -eq 1000) "1,000 of 2,000 searched ($($r.Searched) of $($r.Total))"
    Check ($sw.ElapsedMilliseconds -lt 5000) "in $($sw.ElapsedMilliseconds) ms (limit 5,000)"
    Check ((Say $r) -eq 'TM matches: 1000 of 2000 project TMs searched; 1000 are switched off or have Lookup unticked in Project Settings.') "and the sentence counts them"
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
