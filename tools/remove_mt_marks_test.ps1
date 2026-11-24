# Remove MT marks (batch task "Supervertaler: Remove MT marks", 18.20.210).
#
# Runs the BUILT plugin's own rule, scan, processor and report outside Studio.
# Every .sdlxliff case goes through Studio's OWN file-type pipeline (its
# .sdlxliff reader and writer, DefaultFileTypeManager), the same one a batch
# task hands the processor to, so what is checked is what Studio writes.
# Invented Acme text only. No Studio is started; no real file is touched.
#
#   A  the cases, through Studio's reader and writer (fixtures\remove-mt-marks\cases-A):
#      MT edited afterwards (faded AT), nmt with a comment, amt, an MT link
#      in front of a TM match with a tracked change, a locked MT segment, MT
#      now and earlier on an approved segment; the counts and engines; and
#      nothing else in the file differs from Studio's own round trip
#   B  what is not MT stays (cases-B): TM, interactive after TM, TM with MT far
#      back in its chain, created_by kept, a Draft status kept, nmt with no engine
#   C  the scan that decides which files the task opens: the four fixtures, a
#      self-closing seg before one with content, a locked-only file, and files
#      it cannot read (missing, not XML, opened exclusively by another program)
#   D  a file with nothing to remove stays byte for byte: the task skips it,
#      and Studio's round trip WOULD have changed it
#   E  running it twice changes nothing the second time
#   F  the report: summary wording, a file that was not finished, the XML
#   G  a damaged file: Studio refuses it and the original is untouched
#   H  the manifest entry matches the task's attributes
#   I  scale: -Segments segments (default 20,000) through the scan and the pipeline
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\remove_mt_marks_test.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\remove_mt_marks_test.ps1 -Dll <Supervertaler.Trados.dll> -Segments 100000
# A Studio 2024 / 2022 build is 32-bit: the script starts the 32-bit PowerShell
# for it by itself.
#
# What it cannot reach: Studio's batch-task runner itself (choosing files,
# ShouldProcessFile/OnFileComplete calls, a file open in the Editor, cancelling,
# server check-out, where the report appears). Those need the live test.
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Debug\Supervertaler.Trados.dll"),
    [string]$Root = (Join-Path $env:TEMP "sv-remove-mt-marks-test"),
    [int]$Segments = 20000,
    [switch]$Child
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
if ($Root -like 'D:\Supervertaler*' -or $Root.Length -lt 10) { throw "Refusing to use $Root as the test root." }
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "No plugin build at $Dll" }

if (-not $Child) {
    $real = @('D:\Supervertaler\trados\logs\diagnostic.log', 'D:\Supervertaler\trados\settings\settings.json')
    function Stamp { ($real | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } else { "$_|absent" } }) -join "`n" }
    $before = Stamp
    $ps = if ($Dll -like '*Studio19*') { Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe' }
          else { Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe' }
    Write-Host "Plugin: $Dll"
    & $ps -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Child -Dll $Dll -Root $Root -Segments $Segments
    $code = $LASTEXITCODE
    if ((Stamp) -ne $before) { Write-Host "NOTE  a real settings or log file changed during the test (a running Studio writes to them too; the test's own files are under $Root)" }
    if ($code -ne 0) { Write-Host "RESULT: FAILED"; exit 1 }
    Write-Host "RESULT: all passed"
    exit 0
}

# -- Child -------------------------------------------------------------------
if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'data') | Out-Null
$studio = if ($Dll -like '*Studio19*') { 'C:\Program Files\Trados\Trados Studio\Studio19' }
          elseif ($Dll -like '*Studio17*') { 'C:\Program Files (x86)\Trados\Trados Studio\Studio17' }
          else { 'C:\Program Files (x86)\Trados\Trados Studio\Studio18' }
$fixtures = Join-Path $PSScriptRoot 'fixtures\remove-mt-marks'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class RmtResolver
{
    private static readonly HashSet<string> Tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static void Register(string[] dirs)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
        {
            var n = new AssemblyName(e.Name).Name;
            lock (Tried) { if (!Tried.Add(n)) return null; }
            foreach (var d in dirs)
            {
                var c = Path.Combine(d, n + ".dll");
                if (File.Exists(c)) { try { return Assembly.LoadFrom(c); } catch { return null; } }
            }
            return null;
        };
    }
}
'@
[RmtResolver]::Register(@((Split-Path $Dll), $studio))
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$plugin.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, @([string](Join-Path $Root 'data')))
$logType = $plugin.GetType("Supervertaler.Trados.Core.DiagnosticLog", $true)
$logPath = [string]$logType.GetProperty("LogFilePath").GetValue($null)
if (-not $logPath.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "The diagnostic log would be written to $logPath, outside the test root." }
$logType.GetProperty("Enabled").SetValue($null, $true)   # opt-in, as the translator's setting does

# Studio's pipeline, driven the way a batch task drives it.
$refs = @('Sdl.FileTypeSupport.Framework.Core', 'Sdl.FileTypeSupport.Framework.Core.Utilities', 'Sdl.Core.Globalization',
          'Sdl.Core.PluginFramework') | ForEach-Object { Join-Path $studio "$_.dll" }
Add-Type -ReferencedAssemblies ($refs + 'System.Xml', 'netstandard') -TypeDefinition @'
using System;
using System.Reflection;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.Core.Utilities.BilingualApi;
using Sdl.FileTypeSupport.Framework.Core.Utilities.IntegrationApi;
using Sdl.FileTypeSupport.Framework.IntegrationApi;
public static class RmtPipeline
{
    private static IFileTypeManager _manager;
    private static IFileTypeManager Manager { get { return _manager ?? (_manager = DefaultFileTypeManager.CreateInstance(true)); } }

    /// <summary>Studio reads input and writes output; processor (or none) sees every paragraph in between.</summary>
    public static void Run(string input, string output, object processor)
    {
        var converter = Manager.GetConverterToDefaultBilingual(input, output, null);
        if (processor != null)
            converter.AddBilingualProcessor(new BilingualContentHandlerAdapter((IBilingualContentHandler)processor));
        converter.Parse();
    }
}
'@

$NP = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$ST = [Reflection.BindingFlags]'Public,NonPublic,Static'
$countT = $plugin.GetType('Supervertaler.Trados.Core.MtMarks.MtMarkCount', $true)
$procT = $plugin.GetType('Supervertaler.Trados.BatchTasks.RemoveMtMarksProcessor', $true)
$scanT = $plugin.GetType('Supervertaler.Trados.Core.MtMarks.MtMarksScan', $true)
$taskT = $plugin.GetType('Supervertaler.Trados.BatchTasks.RemoveMtMarksTask', $true)
$fileResT = $taskT.GetNestedType('FileResult', [Reflection.BindingFlags]'Public,NonPublic')
$scanPath = $scanT.GetMethod('NeedsCleaning', $ST, $null, [Type[]]@([string]), $null)
$scanStream = $scanT.GetMethod('NeedsCleaning', $ST, $null, [Type[]]@([IO.Stream]), $null)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function Unwrap($e) {
    while ($e.InnerException -and ($e -is [Management.Automation.MethodInvocationException] -or
            $e -is [Reflection.TargetInvocationException])) { $e = $e.InnerException }
    return $e
}
function NewCount { [Activator]::CreateInstance($countT, $true) }
function Clean($in, $out) {
    $count = NewCount
    $proc = [Activator]::CreateInstance($procT, $NP, $null, [object[]]@($count), $null)
    [RmtPipeline]::Run($in, $out, $proc)
    return $count
}
function Field($o, $n) { $o.GetType().GetField($n).GetValue($o) }
function Engines($count) { $d = Field $count 'Engines'; ($d.Keys | Sort-Object | ForEach-Object { "$_=$($d[$_])" }) -join '; ' }
$ns = @{ x = 'urn:oasis:names:tc:xliff:document:1.2'; sdl = 'http://sdl.com/FileTypes/SdlXliff/1.0' }
function Doc($path) { $d = New-Object Xml.XmlDocument; $d.PreserveWhitespace = $true; $d.Load($path); return $d }
function Seg($doc, $id) {
    $m = New-Object Xml.XmlNamespaceManager($doc.NameTable); $m.AddNamespace('sdl', $ns.sdl)
    return $doc.SelectSingleNode("//sdl:seg[@id='$id']", $m)
}
function Target($doc, $id) {
    $m = New-Object Xml.XmlNamespaceManager($doc.NameTable); $m.AddNamespace('x', $ns.x)
    return NoIds $doc.SelectSingleNode("//x:target/x:mrk[@mtype='seg' and @mid='$id']", $m).InnerXml
}
# Studio gives comments and tracked changes new IDs every time it writes a file,
# in a plain round trip too, so they are compared without them.
function NoIds($t) { [Text.RegularExpressions.Regex]::Replace($t, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', 'ID') }
function Prevs($seg) { @($seg.SelectNodes('.//*[local-name()="prev-origin"]')) }
# The file as Studio wrote it with the seg definitions and Studio's random
# names for the embedded original taken out: what must be equal between a plain
# round trip and a cleaning one.
function Rest($path) {
    $t = [IO.File]::ReadAllText($path)
    $t = [Text.RegularExpressions.Regex]::Replace($t, '<sdl:seg-defs>.*?</sdl:seg-defs>', '', 'Singleline')
    $t = [Text.RegularExpressions.Regex]::Replace($t, '<internal-file form="base64">.*?</internal-file>', '', 'Singleline')
    return NoIds ([Text.RegularExpressions.Regex]::Replace($t, ' name="[^"]*\.docx"', ''))
}
function Same([byte[]]$a, [byte[]]$b) {
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}
function Work($name) { $p = Join-Path $Root $name; Copy-Item -LiteralPath (Join-Path $fixtures $name) $p; return $p }

# ---- A ----------------------------------------------------------------------
Write-Host "-- A  the cases, through Studio's own reader and writer"
$inA = Work 'cases-A.sdlxliff'
$plainA = Join-Path $Root 'cases-A.plain.sdlxliff'
$outA = Join-Path $Root 'cases-A.clean.sdlxliff'
[RmtPipeline]::Run($inA, $plainA, $null)
$cA = Clean $inA $outA
$p = Doc $plainA; $o = Doc $outA

$s = Seg $o 1
Check ($s.origin -eq 'interactive' -and @(Prevs $s).Count -eq 0) "1 edited after MT: interactive, the MT earlier origin gone: $($s.OuterXml)"
$s = Seg $o 2
Check ($s.origin -eq 'interactive' -and -not $s.HasAttribute('origin-system') -and $s.conf -eq 'Translated') "2 nmt: interactive, no engine, still Translated: $($s.OuterXml)"
Check ((Target $o 2) -like '*x-sdl-comment*Never fill*' -and ([IO.File]::ReadAllText($outA)) -like '*Check this term with Acme*') "2 the comment and its text are still there"
$s = Seg $o 3
Check ($s.origin -eq 'interactive' -and -not $s.HasAttribute('origin-system')) "3 amt: interactive: $($s.OuterXml)"
$s = Seg $o 4; $pv = @(Prevs $s)
Check ($s.origin -eq 'interactive' -and $pv.Count -eq 1 -and $pv[0].origin -eq 'tm' -and $pv[0].percent -eq '75' -and $pv[0].'origin-system' -eq 'Acme TM') "4 MT in front of a TM match: the MT link goes, the TM match moves up and stays: $($s.OuterXml)"
Check ((Target $o 4) -like '*x-sdl-added*power *' -and ([IO.File]::ReadAllText($outA)) -like '*<rev-def *author="Tester"*') "4 the tracked change is still there"
$s = Seg $o 5
Check ($s.origin -eq 'mt' -and $s.locked -eq 'true') "5 locked MT: left as it is: $($s.OuterXml)"
$s = Seg $o 6
Check ($s.origin -eq 'interactive' -and @(Prevs $s).Count -eq 0 -and $s.conf -eq 'ApprovedTranslation' -and -not $s.HasAttribute('origin-system')) "6 MT now and earlier, approved: both go, still approved: $($s.OuterXml)"
Check ((Field $cA 'FullAt') -eq 3 -and (Field $cA 'FadedAt') -eq 2 -and (Field $cA 'LockedLeft') -eq 1) "counts: 3 unedited MT, 2 edited after MT, 1 locked left: $(Field $cA 'FullAt')/$(Field $cA 'FadedAt')/$(Field $cA 'LockedLeft')"
$e = Engines $cA
Check ($e -eq 'AdaptiveMT=1; DeepL=1; Google=1; Language Weaver=2; MT / Supervertaler=1') "engines: $e"
$sameText = $true
foreach ($i in 1..6) { if ((Target $p $i) -ne (Target $o $i)) { $sameText = $false } }
Check $sameText "every target text, tags, comment and tracked change as in Studio's own round trip"
Check ((Rest $plainA) -eq (Rest $outA)) "apart from the seg definitions, the file is exactly what Studio's own round trip writes"
foreach ($i in 1..6) {
    $a = (Seg $p $i); $b = (Seg $o $i)
    if ($a.conf -ne $b.conf -or $a.locked -ne $b.locked) { Check $false "status or lock changed on segment $i" }
}

# ---- B ----------------------------------------------------------------------
Write-Host "-- B  what is not MT stays as it was"
$inB = Work 'cases-B.sdlxliff'
$plainB = Join-Path $Root 'cases-B.plain.sdlxliff'
$outB = Join-Path $Root 'cases-B.clean.sdlxliff'
[RmtPipeline]::Run($inB, $plainB, $null)
$cB = Clean $inB $outB
$p = Doc $plainB; $o = Doc $outB
Check ((Seg $o 1).OuterXml -eq (Seg $p 1).OuterXml -and (Seg $o 1).origin -eq 'tm') "1 TM match: untouched"
Check ((Seg $o 2).OuterXml -eq (Seg $p 2).OuterXml) "2 interactive after a TM match: untouched"
$s = Seg $o 3; $pv = @(Prevs $s)
Check ($s.origin -eq 'tm' -and $s.percent -eq '100' -and $pv.Count -eq 1 -and $pv[0].origin -eq 'interactive') "3 TM with MT far back: still a TM match, only the MT link goes: $($s.OuterXml)"
$s = Seg $o 4
Check ($s.origin -eq 'interactive' -and $s.OuterXml -like '*created_by*ACME\tester*' -and $s.OuterXml -like '*created_on*') "4 MT with who and when: interactive, created_by and created_on kept: $($s.OuterXml)"
$s = Seg $o 5
Check ($s.origin -eq 'interactive' -and $s.conf -eq 'Draft') "5 MT in Draft: interactive, still Draft"
$s = Seg $o 6
Check ($s.origin -eq 'interactive') "6 nmt without an engine: interactive"
$e = Engines $cB
Check ((Field $cB 'FullAt') -eq 3 -and (Field $cB 'FadedAt') -eq 1 -and $e -eq 'DeepL=1; machine translation=3') "counts 3 + 1; engines (segment 3's MT link has no engine name): $e"
Check ((Rest $plainB) -eq (Rest $outB)) "apart from the seg definitions, the file is Studio's own round trip"

# ---- C ----------------------------------------------------------------------
Write-Host "-- C  the scan"
function Scan($path) { $scanPath.Invoke($null, [object[]]@([string]$path)) }
function ScanText($xml) {
    $m = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($xml))
    try { return $scanStream.Invoke($null, [object[]]@([IO.Stream]$m)) } finally { $m.Dispose() }
}
Check ((Scan (Join-Path $fixtures 'cases-A.sdlxliff')) -and (Scan (Join-Path $fixtures 'cases-B.sdlxliff'))) "the two case files need cleaning"
Check (-not (Scan (Join-Path $fixtures 'no-mt.sdlxliff'))) "a file without MT does not"
Check (-not (Scan (Join-Path $fixtures 'locked-only.sdlxliff'))) "a file whose only MT is locked does not"
Check (-not (Scan $outA) -and -not (Scan $outB)) "a cleaned file does not"
$wrap = '<xliff xmlns:sdl="http://sdl.com/FileTypes/SdlXliff/1.0"><sdl:seg-defs>{0}</sdl:seg-defs></xliff>'
Check (ScanText ($wrap -f '<sdl:seg id="1" origin="tm" percent="100" /><sdl:seg id="2" origin="interactive"><sdl:prev-origin origin="mt" /></sdl:seg>')) "a self-closing seg does not hide the MT in the next one"
Check (-not (ScanText ($wrap -f '<sdl:seg id="1" origin="mt" locked="true"><sdl:prev-origin origin="nmt" /></sdl:seg><sdl:seg id="2" origin="tm" />'))) "a locked seg's MT earlier origin is not counted either"
Check (ScanText ($wrap -f '<sdl:seg id="1" origin="interactive"><sdl:prev-origin origin="tm"><sdl:prev-origin origin="amt" /></sdl:prev-origin></sdl:seg>')) "MT two links back is found"
Check (-not (ScanText ($wrap -f '<sdl:seg id="1" origin="automatic-translation" /><sdl:seg id="2" origin="MT" />'))) "only mt, nmt and amt count (not automatic-translation, not upper case)"
Check (-not (ScanText '<xliff><seg origin="mt" /></xliff>')) "a seg outside the SDL namespace is not one"
$logBefore = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
Check (Scan (Join-Path $Root 'missing.sdlxliff')) "a missing file is handed to Studio, which reports it"
$bad = Join-Path $Root 'not-xml.sdlxliff'; [IO.File]::WriteAllText($bad, 'this is not XML <<')
Check (Scan $bad) "a file that is not XML is handed to Studio too"
$held = Join-Path $Root 'held.sdlxliff'; Copy-Item (Join-Path $fixtures 'no-mt.sdlxliff') $held
$lock = [IO.File]::Open($held, 'Open', 'ReadWrite', 'None')
try { Check (Scan $held) "a file another program holds exclusively is handed to Studio" } finally { $lock.Dispose() }
$ro = Join-Path $Root 'readonly.sdlxliff'; Copy-Item (Join-Path $fixtures 'no-mt.sdlxliff') $ro
Set-ItemProperty $ro -Name IsReadOnly -Value $true
Check (-not (Scan $ro)) "a read-only file is read normally"
Set-ItemProperty $ro -Name IsReadOnly -Value $false
$logText = if (Test-Path $logPath) { [IO.File]::ReadAllText($logPath) } else { '' }
Check ((Test-Path $logPath) -and (Get-Item $logPath).Length -gt $logBefore -and $logText -like '*missing.sdlxliff*' -and $logText -like '*held.sdlxliff*') "each file it could not read is named in the test's own diagnostic log"

# ---- D ----------------------------------------------------------------------
Write-Host "-- D  nothing to remove: byte for byte"
foreach ($name in 'no-mt.sdlxliff', 'locked-only.sdlxliff') {
    $f = Work $name
    $bytes = [IO.File]::ReadAllBytes($f)
    $skip = -not (Scan $f)
    Check ($skip -and (Same $bytes ([IO.File]::ReadAllBytes($f)))) "${name}: the task skips it, and it is unchanged"
    $rt = Join-Path $Root "$name.roundtrip"
    [RmtPipeline]::Run($f, $rt, $null)
    Check (-not (Same $bytes ([IO.File]::ReadAllBytes($rt)))) "${name}: Studio's own round trip would have rewritten it (why the task skips it)"
}

# ---- E ----------------------------------------------------------------------
Write-Host "-- E  twice"
$twice = Join-Path $Root 'cases-A.twice.sdlxliff'
$c2 = Clean $outA $twice
Check ((Field $c2 'FullAt') -eq 0 -and (Field $c2 'FadedAt') -eq 0 -and (Field $c2 'LockedLeft') -eq 1 -and (Engines $c2) -eq '') "a second run removes nothing (the locked one is counted again, and left)"
$t1 = Doc $outA; $t2 = Doc $twice
$sameSegs = $true; foreach ($i in 1..6) { if ((Seg $t1 $i).OuterXml -ne (Seg $t2 $i).OuterXml) { $sameSegs = $false } }
Check $sameSegs "and every segment definition is as after the first"

# ---- F ----------------------------------------------------------------------
Write-Host "-- F  the report"
function FileRes($name, $lang, $full, $faded, $locked, [bool]$nothing, [bool]$finished, $engines) {
    $r = [Activator]::CreateInstance($fileResT, $NP, $null, [object[]]@([string]"C:\Acme\$lang\$name", [string]$lang), $null)
    $c = Field $r 'Count'
    $countT.GetField('FullAt').SetValue($c, $full); $countT.GetField('FadedAt').SetValue($c, $faded); $countT.GetField('LockedLeft').SetValue($c, $locked)
    foreach ($k in $engines.Keys) { (Field $c 'Engines')[$k] = $engines[$k] }
    $fileResT.GetField('NothingToRemove').SetValue($r, $nothing); $fileResT.GetField('Finished').SetValue($r, $finished)
    return $r
}
$listT = [Collections.Generic.List`1].MakeGenericType($fileResT)
function Report($items) {
    $l = [Activator]::CreateInstance($listT); foreach ($i in $items) { $l.Add($i) }
    return @{ Summary = $taskT.GetMethod('Summary', $ST).Invoke($null, @(,$l)); Xml = $taskT.GetMethod('ReportXml', $ST).Invoke($null, @(,$l)) }
}
$r = Report @((FileRes 'A.docx.sdlxliff' 'en-GB' 3 2 1 $false $true @{ 'DeepL' = 4; 'machine translation' = 1 }),
              (FileRes 'B.docx.sdlxliff' 'en-GB' 0 0 0 $true $false @{}))
Check ($r.Summary -eq 'MT marks removed from 5 segments in 1 file; 1 locked segment left as it is.') "summary: $($r.Summary)"
$x = [xml]$r.Xml
Check ($x.RemoveMtMarksReport.File.Count -eq 2 -and $x.RemoveMtMarksReport.File[0].Result -eq 'Cleaned' -and $x.RemoveMtMarksReport.File[0].UneditedMT -eq '3' -and
       $x.RemoveMtMarksReport.File[0].Engine.Count -eq 2 -and $x.RemoveMtMarksReport.File[1].Result -eq 'Nothing to remove') "the XML report lists each file, its counts and engines"
$r = Report @((FileRes 'A.docx.sdlxliff' 'de-DE' 3 0 0 $false $true @{ 'DeepL' = 3 }),
              (FileRes 'C.docx.sdlxliff' 'de-DE' 7 0 2 $false $false @{ 'DeepL' = 7 }))
Check ($r.Summary -eq 'MT marks removed from 3 segments in 1 file; 1 file was not finished.') "a file Studio did not finish is said, and its counts are not added: $($r.Summary)"
Check (([xml]$r.Xml).RemoveMtMarksReport.File[1].Result -eq 'Not finished') "and the report marks it Not finished"
$r = Report @((FileRes 'N.docx.sdlxliff' 'fr-FR' 0 0 0 $true $false @{}))
Check ($r.Summary -eq 'No MT marks removed.') "nothing to remove: $($r.Summary)"
$r = Report @((FileRes "L&x'y.sdlxliff" 'fr-FR' 1 0 3 $false $true @{ 'A & B <"MT">' = 1 }))
$rx = [xml]$r.Xml
Check ($r.Summary -like '*3 locked segments left as they are.' -and $rx.RemoveMtMarksReport.File.Name -eq "L&x'y.sdlxliff" -and $rx.RemoveMtMarksReport.File.Engine.Name -eq 'A & B <"MT">') "names with &, ', < and quotes come out as valid XML"

# ---- G ----------------------------------------------------------------------
Write-Host "-- G  a damaged file"
$dam = Join-Path $Root 'damaged.sdlxliff'
$full = [IO.File]::ReadAllBytes((Join-Path $fixtures 'cases-A.sdlxliff'))
[IO.File]::WriteAllBytes($dam, $full[0..([int]($full.Length * 0.6))])
$damBytes = [IO.File]::ReadAllBytes($dam)
$damOut = Join-Path $Root 'damaged.clean.sdlxliff'
$err = $null
try { Clean $dam $damOut | Out-Null } catch { $err = Unwrap $_.Exception }
Check ($err -ne $null) "Studio refuses a cut-off file: $(if ($err) { $err.GetType().Name + ': ' + $err.Message.Split([char]10)[0] })"
Check (Same $damBytes ([IO.File]::ReadAllBytes($dam))) "and the file itself is untouched"
Check (Scan $dam) "the scan hands it to Studio rather than skipping it"

# ---- H ----------------------------------------------------------------------
Write-Host "-- H  the manifest"
$manifest = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Supervertaler.Trados\Supervertaler.Trados.plugin.xml'
$mx = New-Object Xml.XmlDocument; $mx.Load($manifest)
$ext = $mx.SelectSingleNode("//extension[starts-with(@type, 'Supervertaler.Trados.BatchTasks.RemoveMtMarksTask,')]")
$id = $taskT.GetField('TaskId', $ST).GetValue($null); $nm = $taskT.GetField('TaskName', $ST).GetValue($null); $ds = $taskT.GetField('TaskDescription', $ST).GetValue($null)
$args = @($ext.extensionAttribute.constructorArgs.arg | ForEach-Object { $_.'#text' })
Check ($ext -ne $null -and $ext.extensionAttribute.type -like 'Sdl.ProjectAutomation.AutomaticTasks.AutomaticTaskAttribute,*') "the manifest declares the task"
Check ($args.Count -eq 3 -and $args[0] -eq $id -and $args[1] -eq $nm -and $args[2] -eq $ds) "with the same id, name and description as the code"
Check (($ext.extensionAttribute.properties.property | Where-Object name -eq 'GeneratedFileType').'#text' -eq '3' -and
       $ext.auxiliaryExtensionAttributes.auxiliaryExtensionAttribute.constructorArgs.arg.'#text' -eq '3') "and bilingual target files (3) on both attributes"
$attr = $taskT.GetCustomAttributes($false) | Where-Object { $_.GetType().Name -eq 'AutomaticTaskAttribute' }
Check ($attr -ne $null -and [int]$attr.GeneratedFileType -eq 3) "the class carries the attribute too"
Check ($ds -like '*cannot be undone*' -and $ds -like '*declare machine translation*') "the description warns that it is final and about declaring MT"
$built = Join-Path (Split-Path $Dll) 'Supervertaler.Trados.plugin.xml'
if (Test-Path $built) {
    $bx = [IO.File]::ReadAllText($built)
    $major = if ($Dll -like '*Studio19*') { '19' } elseif ($Dll -like '*Studio17*') { '17' } else { '18' }
    Check ($bx -like "*RemoveMtMarksTask*Sdl.ProjectAutomation.AutomaticTasks, Version=$major.0.0.0*") "the built manifest points at this Studio's AutomaticTasks ($major.0.0.0)"
}

# ---- I ----------------------------------------------------------------------
Write-Host "-- I  scale: $Segments segments"
$template = [IO.File]::ReadAllText((Join-Path $fixtures 'cases-B.sdlxliff'))
$bodyStart = $template.IndexOf('<body>') + 6; $bodyEnd = $template.IndexOf('</body>')
$groups = $template.Substring($bodyStart, $bodyEnd - $bodyStart)
$big = Join-Path $Root 'big.sdlxliff'
$w = New-Object IO.StreamWriter($big, $false, (New-Object Text.UTF8Encoding($true)))
try {
    $w.Write($template.Substring(0, $bodyStart))
    $copies = [Math]::Ceiling($Segments / 6)
    for ($k = 0; $k -lt $copies; $k++) {
        $g = [Text.RegularExpressions.Regex]::Replace($groups, '(mid="|<sdl:seg id=")(\d+)"', { param($m) $m.Groups[1].Value + ([int]$m.Groups[2].Value + 6 * $k) + '"' })
        $g = [Text.RegularExpressions.Regex]::Replace($g, '<trans-unit id="[^"]+"', { param($m) '<trans-unit id="' + [Guid]::NewGuid() + '"' })
        $w.Write($g)
    }
    $w.Write($template.Substring($bodyEnd))
} finally { $w.Dispose() }
$n = $copies * 6
$mb = [Math]::Round((Get-Item $big).Length / 1MB, 1)
[GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect(); $mem0 = [Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64
$sw = [Diagnostics.Stopwatch]::StartNew(); $need = Scan $big; $scanMs = $sw.ElapsedMilliseconds
Check $need "the scan finds MT in a $n-segment file ($mb MB) in $scanMs ms"
# Studio's own round trip first, with no processor: what any batch task over
# this file costs, so the cleaning's own share can be read off.
$sw.Restart(); [RmtPipeline]::Run($big, (Join-Path $Root 'big.plain.sdlxliff'), $null); $plainS = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
$peakPlainMb = [Math]::Round(([Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64) / 1MB)
Remove-Item (Join-Path $Root 'big.plain.sdlxliff')
$sw.Restart(); $cBig = Clean $big (Join-Path $Root 'big.clean.sdlxliff'); $runS = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
$peakMb = [Math]::Round(([Diagnostics.Process]::GetCurrentProcess().PeakWorkingSet64) / 1MB)
Check ((Field $cBig 'FullAt') -eq 3 * $copies -and (Field $cBig 'FadedAt') -eq $copies) "every copy cleaned: $(Field $cBig 'FullAt') unedited MT, $(Field $cBig 'FadedAt') edited after MT"
Write-Host "      time: Studio's plain round trip $plainS s, with the cleaning $runS s; peak working set $peakPlainMb MB after the plain one, $peakMb MB after the cleaning"
$cBig = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
$memAfter = [Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64
Write-Host "      memory held after the run, once collected: $([Math]::Round(($memAfter - $mem0) / 1MB)) MB more than before it"
$sw.Restart(); $after = Scan (Join-Path $Root 'big.clean.sdlxliff')
Check (-not $after) "and the cleaned file scans clean ($($sw.ElapsedMilliseconds) ms)"

Write-Host ""
if ($fails -gt 0) { Write-Host "$fails check(s) FAILED"; exit 1 }
Write-Host "All checks passed"
exit 0
