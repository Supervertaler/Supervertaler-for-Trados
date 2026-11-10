# Bridge at Studio startup + MCP open_project - against the BUILT plugin DLL,
# without Studio.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\bridge_startup_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Studio19 Debug build)
#
#   A  open_project's lookup (Core/ProjectOpener): exact name, unique part,
#      several matches refused with the names, none, .sdlproj path registered /
#      not registered / missing, empty request, duplicate names, projects.xml
#      missing / malformed / relative paths.
#   B  open_project's file choice: project only, all files, named files with
#      and without .sdlxliff, folder\name, a missing or ambiguous name refuses
#      everything, several target languages need one, unknown language, the
#      50-file cap (50 opens, 51 refuses), and the time it takes for 5,000 files.
#   C  the bridge as Studio startup starts it (AiAssistantViewPart.EnsureBridgeStarted)
#      in TWO separate processes (two Studios): each writes its own handshake,
#      answers on its own port, starting again is a no-op that logs nothing new;
#      with no AI Assistant pane the Editor endpoints answer 503 with the plain
#      sentence and the disk/project endpoints answer; open_project refuses a
#      missing .sdlproj and an empty request without touching anything; one
#      Studio closing leaves the other answering.
#   E  list_projects' status filter (projects.xml says Started for InProgress), and
#      open_project's progress and running-note wording and patience.
#   D  failure path: a data folder that cannot be written - starting must not
#      throw (Studio must still start).
#
# Writes only under a new %TEMP%\sv-bridge-startup-test-* folder (deleted at the
# end); never the real data folder. The only real file read is none: open_project
# is driven with .sdlproj paths, which never look at Studio's projects.xml.
param(
    [string]$Dll = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\Supervertaler.Trados\bin\Studio19\Debug\Supervertaler.Trados.dll"),
    # Internal: child run = one simulated Studio.
    [switch]$Child,
    [string]$DataRoot,
    [string]$ReadyFile,
    [string]$StopFile,
    [switch]$NoLicence
)
$ErrorActionPreference = 'Stop'
$Dll = [IO.Path]::GetFullPath($Dll)
if (-not (Test-Path -LiteralPath $Dll)) { throw "Build the plugin first: $Dll not found" }
$studio = if ($Dll -like '*Studio19*') { 'C:\Program Files\Trados\Trados Studio\Studio19' }
          elseif ($Dll -like '*Studio17*') { 'C:\Program Files (x86)\Trados\Trados Studio\Studio17' }
          else { 'C:\Program Files (x86)\Trados\Trados Studio\Studio18' }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
public static class BstResolver
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
public static class BstHttp
{
    // Returns "status|body"; never throws for an HTTP error status.
    public static string Send(string method, string url, string token, string body, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Timeout = timeoutMs;
        req.Headers["Authorization"] = "Bearer " + token;
        req.Headers["X-Supervertaler-Mcp-Exe-Version"] = "3";
        if (!string.IsNullOrEmpty(body))
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentType = "application/json";
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        }
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException ex) { resp = ex.Response as HttpWebResponse; if (resp == null) return "0|" + ex.Message; }
        using (resp)
        using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return ((int)resp.StatusCode) + "|" + r.ReadToEnd();
    }
}
'@
[BstResolver]::Register(@((Split-Path $Dll), $studio))
Add-Type -AssemblyName System.Windows.Forms
$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Inst = [Reflection.BindingFlags]'Public,NonPublic,Instance'
$Part = $plugin.GetType('Supervertaler.Trados.AiAssistantViewPart', $true)

function UseDataRoot([string]$root) {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $plugin.GetType('Supervertaler.Core.SupervertalerPaths').GetMethod('Set').Invoke($null, @([string]$root))
}

# ---------------------------------------------------------------- child: one Studio
if ($Child) {
    UseDataRoot $DataRoot
    # A synthetic activated licence, validated now, written in THIS temporary
    # data folder only (never the real one) - a made-up key and instance id that
    # no server knows; nothing here contacts the licence server. It makes the
    # bridge start as it does for a licensed user. Skipped with -NoLicence.
    if (-not $NoLicence) {
        $lic = Join-Path $DataRoot 'licence'
        New-Item -ItemType Directory -Force -Path $lic | Out-Null
        $ms = [long]([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalMilliseconds
        $json = '{"licenseKey":"TEST-KEY-NOT-REAL","instanceId":"test-instance","status":"active","lastValidatedAt":"\/Date(' + $ms + ')\/"}'
        [IO.File]::WriteAllText((Join-Path $lic 'licence.json'), $json)
    }
    $Part.GetMethod('EnsureBridgeStarted', $Static).Invoke($null, @('studio-startup'))
    $Part.GetMethod('EnsureBridgeStarted', $Static).Invoke($null, @('studio-startup'))   # second call: no-op
    $running = [bool]$Part.GetProperty('IsBridgeRunning', $Static).GetValue($null)
    Set-Content -LiteralPath $ReadyFile -Value "$PID|$running"
    $deadline = (Get-Date).AddMinutes(3)
    while (-not (Test-Path -LiteralPath $StopFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    exit 0
}

# ---------------------------------------------------------------- parent
$fails = 0; $checks = 0
function Check($ok, $label) {
    $script:checks++
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$work = Join-Path $env:TEMP ("sv-bridge-startup-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$children = @()
Write-Host "DLL: $Dll"
Write-Host "Work: $work"

try {
    $PO = $plugin.GetType('Supervertaler.Trados.Core.ProjectOpener', $true)
    $EntryT = $PO.GetNestedType('Entry', [Reflection.BindingFlags]'NonPublic,Public')
    $FileT = $PO.GetNestedType('FileRef', [Reflection.BindingFlags]'NonPublic,Public')
    $mMatch = $PO.GetMethod('Match', $Static)
    $mRead = $PO.GetMethod('ReadProjectList', $Static)
    $mPick = $PO.GetMethod('PickFiles', $Static)
    $cap = [int]$PO.GetField('MaxFilesToOpen', $Static).GetValue($null)

    function Entries($pairs) {
        $l = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($EntryT))
        foreach ($p in $pairs) { $e = [Activator]::CreateInstance($EntryT, $true); $e.Name = $p[0]; $e.ProjectFile = $p[1]; $l.Add($e) }
        return , $l
    }
    function Files($specs) {
        $l = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($FileT))
        foreach ($s in $specs) { $f = [Activator]::CreateInstance($FileT, $true); $f.Name = $s[0]; $f.Folder = $s[1]; $f.Language = $s[2]; $l.Add($f) }
        return , $l
    }
    function Strs([string[]]$items) { $l = New-Object 'System.Collections.Generic.List[string]'; foreach ($i in $items) { $l.Add($i) }; return , $l }
    function M($q, $list) { $a = New-Object 'object[]' 2; $a[0] = [string]$q; $a[1] = $list.psobject.BaseObject; return $mMatch.Invoke($null, $a) }
    function P($files, [bool]$all, $names, $lang) {
        $a = New-Object 'object[]' 4
        $a[0] = $files.psobject.BaseObject; $a[1] = $all; $a[2] = (Strs $names).psobject.BaseObject
        $a[3] = if ($null -eq $lang) { $null } else { [string]$lang }
        return $mPick.Invoke($null, $a)
    }

    # ---- A: lookup ---------------------------------------------------------------
    $projDir = Join-Path $work 'Projects'
    New-Item -ItemType Directory -Path $projDir | Out-Null
    $acme1 = Join-Path $projDir 'Acme PROJ-001\Acme PROJ-001.sdlproj'
    New-Item -ItemType Directory -Path (Split-Path $acme1) | Out-Null; Set-Content -LiteralPath $acme1 -Value '<x/>'
    $loose = Join-Path $work 'Loose\Loose.sdlproj'
    New-Item -ItemType Directory -Path (Split-Path $loose) | Out-Null; Set-Content -LiteralPath $loose -Value '<x/>'
    $list = Entries @(
        @('Acme PROJ-001', $acme1),
        @('Acme PROJ-002', (Join-Path $projDir 'Acme PROJ-002\Acme PROJ-002.sdlproj')),
        @('Acme PROJ-001 revision', (Join-Path $projDir 'Acme PROJ-001 revision\x.sdlproj')),
        @('Globex manual', (Join-Path $projDir 'Globex\Globex.sdlproj')),
        @('Twin', (Join-Path $projDir 'TwinA\Twin.sdlproj')),
        @('twin', (Join-Path $projDir 'TwinB\Twin.sdlproj')))

    $r = M 'Acme PROJ-001' $list
    Check ($r.Project.Name -eq 'Acme PROJ-001') 'A1 exact name wins over a longer name containing it'
    $r = M 'acme proj-002' $list
    Check ($r.Project.Name -eq 'Acme PROJ-002') 'A2 exact name ignores case'
    $r = M 'globex' $list
    Check ($r.Project.Name -eq 'Globex manual') 'A3 a part only one name contains'
    $r = M 'Acme' $list
    Check ($null -eq $r.Project -and $r.Error -like '*3 projects match*Acme PROJ-001*Acme PROJ-002*') "A4 several matches refused with the names: $($r.Error)"
    $r = M 'Initech' $list
    Check ($null -eq $r.Project -and $r.Error -like '*No project*Initech*') 'A5 no match'
    $r = M '   ' $list
    Check ($null -eq $r.Project -and $r.Error -like '*Say which project*') 'A6 empty request'
    $r = M ('"' + $acme1.ToUpperInvariant() + '"') $list
    Check ($r.Project.Name -eq 'Acme PROJ-001') 'A7 .sdlproj path (quoted, other case) finds the registered project'
    $r = M $loose $list
    Check ($null -eq $r.Project -and $r.Error -like "*not in this Studio's Projects list*") 'A8 existing .sdlproj not in the list: refused, says to add it'
    $r = M (Join-Path $work 'nope\nope.sdlproj') $list
    Check ($null -eq $r.Project -and $r.Error -like '*does not exist*') 'A9 missing .sdlproj'
    $r = M 'Twin' $list
    Check ($null -eq $r.Project -and $r.Error -like '*2 projects*called*Twin*sdlproj*') 'A10 two projects with the same name: asks for the .sdlproj'
    $r = M 'x' (Entries @())
    Check ($null -eq $r.Project -and $r.Error -like '*No project*') 'A11 empty project list'

    $xml = Join-Path $projDir 'projects.xml'
    Set-Content -LiteralPath $xml -Encoding UTF8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<ProjectServer><Projects>
  <ProjectListItem ProjectFilePath="Acme PROJ-001\Acme PROJ-001.sdlproj"><ProjectInfo Name="Acme PROJ-001" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="$loose"><ProjectInfo Name="Loose" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="no-name.sdlproj"><ProjectInfo /></ProjectListItem>
</Projects></ProjectServer>
"@
    $read = $mRead.Invoke($null, @([string]$xml))
    Check ($read.Count -eq 2) "A12 projects.xml read: 2 named projects (one without a name skipped) - got $($read.Count)"
    Check ($read[0].ProjectFile -eq $acme1) 'A13 relative ProjectFilePath resolved against the Projects folder'
    Check ($read[1].ProjectFile -eq $loose) 'A14 absolute ProjectFilePath kept'
    Check ($mRead.Invoke($null, @([string](Join-Path $work 'missing.xml'))).Count -eq 0) 'A15 missing projects.xml = empty list'
    $bad = Join-Path $work 'bad.xml'; Set-Content -LiteralPath $bad -Value '<ProjectServer><unclosed>'
    Check ($mRead.Invoke($null, @([string]$bad)).Count -eq 0) 'A16 malformed projects.xml = empty list, no exception'
    Check ($mRead.Invoke($null, @([string]$null)).Count -eq 0) 'A17 no path = empty list'

    # ---- B: files ------------------------------------------------------------------
    $one = Files @(
        @('spec.docx.sdlxliff', '', 'en-GB'),
        @('intro.txt.sdlxliff', 'manuals\', 'en-GB'),
        @('intro.txt.sdlxliff', '', 'en-GB'))
    $two = Files @(
        @('spec.docx.sdlxliff', '', 'en-GB'),
        @('spec.docx.sdlxliff', '', 'de-DE'))

    $p = P $one $false @() $null
    Check ($null -eq $p.Error -and $p.Files.Count -eq 0) 'B1 no files asked: project only'
    $p = P $one $true @() $null
    Check ($null -eq $p.Error -and $p.Files.Count -eq 3 -and $p.Language -eq 'en-GB') 'B2 allFiles: every file of the one language'
    $p = P $one $false @('spec.docx') $null
    Check ($null -eq $p.Error -and $p.Files.Count -eq 1 -and $p.Files[0].Name -eq 'spec.docx.sdlxliff') 'B3 name without .sdlxliff'
    $p = P $one $false @('SPEC.DOCX.SDLXLIFF', 'spec.docx') $null
    Check ($null -eq $p.Error -and $p.Files.Count -eq 1) 'B4 same file named twice opens once; case ignored'
    $p = P $one $false @('manuals/intro.txt') $null
    Check ($null -eq $p.Error -and $p.Files.Count -eq 1 -and $p.Files[0].Folder -eq 'manuals\') 'B5 folder/name (forward slash) picks the subfolder file'
    $p = P $one $false @('intro.txt') $null
    Check ($p.Error -like '*More than one file matches*intro.txt*give the folder*' -and $p.Files.Count -eq 0) 'B6 ambiguous name refused, nothing opened'
    $p = P $one $false @('spec.docx', 'missing.docx') $null
    Check ($p.Error -like '*Not in the project*missing.docx*Its files:*spec.docx*' -and $p.Files.Count -eq 0) 'B7 one missing name refuses ALL (no partial open), lists the files'
    $p = P $two $true @() $null
    Check ($p.Error -like '*several target languages*en-GB*de-DE*' -and $p.Files.Count -eq 0) 'B8 several languages, none given: refused with the languages'
    $p = P $two $true @() 'DE-de'
    Check ($null -eq $p.Error -and $p.Files.Count -eq 1 -and $p.Language -eq 'de-DE') 'B9 language given (any case)'
    $p = P $two $true @() 'fr-FR'
    Check ($p.Error -like '*no fr-FR files*en-GB, de-DE*') 'B10 unknown language refused with the project languages'
    $p = P (Files @()) $true @() $null
    Check ($p.Error -like '*no target-language files*') 'B11 project without target files'

    $specs = @(); for ($i = 1; $i -le $cap; $i++) { $specs += , @("f$i.docx.sdlxliff", '', 'en-GB') }
    $p = P (Files $specs) $true @() $null
    Check ($null -eq $p.Error -and $p.Files.Count -eq $cap) "B12 exactly the cap ($cap) opens"
    $specs += , @("extra.docx.sdlxliff", '', 'en-GB')
    $p = P (Files $specs) $true @() $null
    Check ($p.Error -like "*$($cap + 1) files*at most $cap*Name the files*" -and $p.Files.Count -eq 0) 'B13 one over the cap: refused, says the cap and why'

    # Scale: a 5,000-file project, 3 languages (15,000 files), asking for 40 named files.
    $big = @(); foreach ($lang in 'en-GB', 'de-DE', 'fr-FR') { for ($i = 1; $i -le 5000; $i++) { $big += , @("doc$i.docx.sdlxliff", "part$($i % 50)\", $lang) } }
    $bigFiles = Files $big
    $names = @(); for ($i = 1; $i -le 40; $i++) { $names += "part$($i % 50)\doc$i.docx" }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = P $bigFiles $false $names 'en-GB'
    $sw.Stop()
    Check ($null -eq $p.Error -and $p.Files.Count -eq 40) "B14 15,000 project files, 40 named: picked in $($sw.ElapsedMilliseconds) ms (off the UI thread)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = P $bigFiles $true @() 'en-GB'
    $sw.Stop()
    Check ($p.Error -like '*5000 files*at most*') "B15 allFiles on 5,000 files refused by the cap in $($sw.ElapsedMilliseconds) ms"

    # ---- E: list_projects status, open_project progress wording ------------------------
    $TT = $plugin.GetType('Supervertaler.Trados.Core.TradosTools', $true)
    $mCanon = $TT.GetMethod('CanonicalStatus', $Static)
    $canon = @{ 'Started' = 'InProgress'; 'InProgress' = 'InProgress'; 'In progress' = 'InProgress'; 'in_progress' = 'InProgress';
                'completed' = 'Completed'; 'Archived' = 'Archived'; 'Pending' = 'Pending'; 'Detached' = 'Detached' }
    $ok = $true
    foreach ($k in $canon.Keys) { if ($mCanon.Invoke($null, @([string]$k)) -ne $canon[$k]) { $ok = $false; Write-Host "  $k -> $($mCanon.Invoke($null, @([string]$k)))" } }
    Check ($ok -and $null -eq $mCanon.Invoke($null, @('Open')) -and $null -eq $mCanon.Invoke($null, @([string]$null))) 'E1 status names: Started = InProgress = In progress; unknown = none'

    $reg = Join-Path $work 'Studio 2026 Release\Projects'
    New-Item -ItemType Directory -Force -Path $reg | Out-Null
    $regXml = Join-Path $reg 'projects.xml'
    Set-Content -LiteralPath $regXml -Encoding UTF8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<ProjectServer><Projects>
  <ProjectListItem ProjectFilePath="A\A.sdlproj"><ProjectInfo Name="Acme A" Status="Started" CreatedAt="2026-11-01T10:00:00Z" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="B\B.sdlproj"><ProjectInfo Name="Acme B" Status="Started" CreatedAt="2026-11-02T10:00:00Z" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="C\C.sdlproj"><ProjectInfo Name="Acme C" Status="Completed" CreatedAt="2026-10-01T10:00:00Z" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="D\D.sdlproj"><ProjectInfo Name="Acme D" Status="Archived" CreatedAt="2026-09-01T10:00:00Z" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="E\E.sdlproj"><ProjectInfo Name="Acme E" Status="Pending" CreatedAt="2026-09-01T10:00:00Z" /></ProjectListItem>
</Projects></ProjectServer>
"@
    $mList = $TT.GetMethod('ListProjects', $Static, $null, [Type[]]@([string], [System.Collections.Generic.List[string]]), $null)
    $regList = New-Object 'System.Collections.Generic.List[string]'
    $regList.Add([string]$regXml)
    function ListProj($f) {
        $a = New-Object 'object[]' 2
        $a[0] = if ($null -eq $f) { $null } else { [string]$f }
        $a[1] = $regList.psobject.BaseObject
        $raw = [string]$mList.Invoke($null, $a)
        if ($env:SV_BST_DEBUG) { Write-Host "  ListProj($f): $raw" }
        return ($raw | ConvertFrom-Json)
    }
    $r = ListProj 'InProgress'
    Check ($r.total -eq 2 -and (@($r.projects | ForEach-Object status) -join ',') -eq 'InProgress,InProgress') "E2 status 'InProgress' finds the 2 'Started' projects and reports them as InProgress (was 0) - got $($r.total)"
    Check ((ListProj 'Started').total -eq 2 -and (ListProj 'in progress').total -eq 2) "E3 'Started' and 'in progress' work too"
    Check ((ListProj 'Completed').total -eq 1 -and (ListProj 'archived').total -eq 1 -and (ListProj 'Pending').total -eq 1) 'E4 Completed, Archived, Pending: 1 each'
    Check ((ListProj $null).total -eq 5 -and (ListProj '  ').total -eq 5) 'E5 no filter: all 5'
    $r = ListProj 'Open'
    Check ($r.error -like "*Unknown status 'Open'*InProgress*" -and $null -eq $r.projects) 'E6 an unknown status is an error naming the valid ones, not a silent 0'

    $POS = $plugin.GetType('Supervertaler.Trados.Core.ProjectOpenerStudio', $true)
    $mPat = $POS.GetMethod('Patience', $Static); $mText = $POS.GetMethod('ProgressText', $Static)
    $pat2 = $mPat.Invoke($null, @([int]2))
    Check ($pat2.TotalSeconds -eq 34 -and $mPat.Invoke($null, @([int]50)).TotalSeconds -eq 130 -and $mPat.Invoke($null, @([int]0)).TotalSeconds -eq 30) 'E7 patience: 30 s + 2 s per file (34 s for 2 files, 130 s for 50)'
    $t12 = $mText.Invoke($null, @('opening 2 file(s) in the Editor', [TimeSpan]::FromSeconds(12), $pat2))
    Check ($t12 -eq 'opening 2 file(s) in the Editor (12 s)') "E8 the Studio 2022 case (2 files, 12 s): neutral '$t12'"
    $t40 = $mText.Invoke($null, @('opening 2 file(s) in the Editor', [TimeSpan]::FromSeconds(40), $pat2))
    Check ($t40 -like '*(40 s)*may be showing a message*answer it there*') "E9 past the patience it mentions a message: '$t40'"
    $note = $Part.GetMethod('OpenProjectRunningNote', $Static).Invoke($null, @([string]$t12, 'abc12345'))
    Check ($note -notlike '*Studio is waiting*' -and $note -notlike '*Studio is opening*' -and $note -like 'Not finished yet: opening 2 file(s) in the Editor (12 s). Poll get_task_status*abc12345*') "E10 running note reads cleanly: '$note'"
    $note0 = $Part.GetMethod('OpenProjectRunningNote', $Static).Invoke($null, @([string]$null, 'x'))
    Check ($note0 -like 'Not finished yet: Studio is busy.*') 'E11 running note without progress'

    # ---- C: two Studios ---------------------------------------------------------------
    $data = Join-Path $work 'data'
    $Bridge = $plugin.GetType('Supervertaler.Trados.Core.SupervertalerBridge', $true)
    $notReady = [string]$Bridge.GetField('EditorNotReadyMessage', $Static).GetValue($null)
    $mNeeds = $Bridge.GetMethod('NeedsNoEditor', $Static)
    Check (-not $mNeeds.Invoke($null, @('/v1/segments')) -and -not $mNeeds.Invoke($null, @('/v1/new-endpoint')) -and -not $mNeeds.Invoke($null, @($null))) 'C1 Editor endpoints and unknown paths are gated (safe by default)'
    Check ($mNeeds.Invoke($null, @('/v1/projects')) -and $mNeeds.Invoke($null, @('/v1/open-project')) -and $mNeeds.Invoke($null, @('/v1/create-project'))) 'C2 project/disk endpoints are not gated'

    $self = $PSCommandPath
    function StartStudio($n) {
        $ready = Join-Path $work "ready$n.txt"; $stop = Join-Path $work "stop$n.txt"
        $proc = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$self`"", '-Child',
            '-Dll', "`"$Dll`"", '-DataRoot', "`"$data`"", '-ReadyFile', "`"$ready`"", '-StopFile', "`"$stop`"")
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Path -LiteralPath $ready) -and -not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        Start-Sleep -Milliseconds 200
        $line = if (Test-Path -LiteralPath $ready) { (Get-Content -LiteralPath $ready -Raw).Trim() } else { '' }
        return [pscustomobject]@{ Proc = $proc; Stop = $stop; Line = $line }
    }
    function Handshake($procId) {
        $f = Join-Path $data "trados\runtime\instances\bridge-$procId.json"
        if (-not (Test-Path -LiteralPath $f)) { return $null }
        return (Get-Content -LiteralPath $f -Raw | ConvertFrom-Json)
    }
    function Call($hs, $method, $path, $body) {
        $res = [BstHttp]::Send($method, "http://127.0.0.1:$($hs.port)$path", $hs.token, $body, 30000)
        $i = $res.IndexOf('|')
        return [pscustomobject]@{ Status = [int]$res.Substring(0, $i); Body = $res.Substring($i + 1) }
    }

    $s1 = StartStudio 1; $children += $s1
    $s2 = StartStudio 2; $children += $s2
    Check ($s1.Line -like '*|True' -and $s2.Line -like '*|True') "C3 both simulated Studios started a bridge with no pane and no document ($($s1.Line); $($s2.Line))"
    $h1 = Handshake $s1.Proc.Id; $h2 = Handshake $s2.Proc.Id
    Check ($null -ne $h1 -and $null -ne $h2 -and $h1.port -ne $h2.port -and $h1.token -ne $h2.token) 'C4 one handshake per Studio, own port and token'
    Check ($null -eq $h1.projectName -and $null -eq $h1.activeFile) 'C5 handshake names no project or document (none open)'
    $shared = Get-Content -LiteralPath (Join-Path $data 'trados\runtime\bridge.json') -Raw | ConvertFrom-Json
    Check ($shared.pid -eq $s2.Proc.Id) 'C6 shared bridge.json belongs to the Studio that started last'

    $diag = Get-ChildItem -LiteralPath $data -Recurse -Filter 'diagnostic.log' | Select-Object -First 1
    $startedLines = if ($diag) { @(Select-String -LiteralPath $diag.FullName -Pattern '\[Bridge\] started').Count } else { 0 }
    Check ($startedLines -eq 2) "C7 'started' logged once per Studio although EnsureBridgeStarted ran twice in each (found $startedLines)"

    foreach ($pair in @(@($h1, 'Studio 1'), @($h2, 'Studio 2'))) {
        $h = $pair[0]; $n = $pair[1]
        $r = Call $h 'GET' '/v1/segments' $null
        Check ($r.Status -eq 503 -and ($r.Body | ConvertFrom-Json).error -eq $notReady) "C8 $n get_segments with no pane: 503 + the plain sentence"
        $r = Call $h 'GET' '/v1/project' $null
        Check ($r.Status -eq 503) "C9 $n get_active_project with no pane: 503"
        $r = Call $h 'GET' '/v1/project-templates' $null
        Check ($r.Status -eq 200) "C10 $n list_project_templates answers ($($r.Status))"
        $r = Call $h 'GET' '/v1/supermemory-banks' $null
        Check ($r.Status -eq 200) "C11 $n list_supermemory_banks answers with no pane ($($r.Status))"
        $r = Call $h 'GET' '/v1/task-status?id=nope' $null
        Check ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).found -eq $false) "C12 $n get_task_status answers with no pane"
    }

    $r = Call $h1 'POST' '/v1/open-project' '{"project":""}'
    $j = $r.Body | ConvertFrom-Json
    Check ($r.Status -eq 200 -and $j.ok -eq $false -and $j.error -like '*Say which project*') 'C13 open_project with no project: refused at once'
    $missing = (Join-Path $work 'Nope\Nope.sdlproj') -replace '\\', '\\'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Call $h1 'POST' '/v1/open-project' ('{"project":"' + $missing + '","allFiles":true}')
    $sw.Stop()
    $j = $r.Body | ConvertFrom-Json
    Check ($r.Status -eq 200 -and $j.ok -eq $false -and $j.status -eq 'failed' -and $j.error -like '*does not exist*') "C14 open_project with a missing .sdlproj: failed, says so ($($sw.ElapsedMilliseconds) ms): $($j.error)"
    Check (-not (Test-Path -LiteralPath (Split-Path ($missing -replace '\\\\', '\')))) 'C15 nothing created for it'
    $r = Call $h1 'POST' '/v1/open-project' 'not json'
    Check ($r.Status -eq 400) 'C16 malformed body: 400'
    $r = Call $h1 'GET' ('/v1/task-status?id=' + $j.jobId) $null
    Check (($r.Body | ConvertFrom-Json).status -eq 'failed') 'C17 the failed open is in get_task_status too'

    # One Studio closes: the other keeps answering, and keeps its own handshake.
    Set-Content -LiteralPath $s1.Stop -Value 'stop'
    $s1.Proc.WaitForExit(30000) | Out-Null
    $r = Call $h2 'GET' '/v1/projects' $null
    Check ($r.Status -eq 200) 'C18 Studio 1 closed: Studio 2 still answers'
    $r = Call $h1 'GET' '/v1/projects' $null
    Check ($r.Status -eq 0) 'C19 Studio 1 closed: its port no longer answers'

    # ---- D: failure path ---------------------------------------------------------------
    # The data folder's "trados" is a FILE: the runtime folder (handshake, bridge.log)
    # cannot be made. Starting must not throw - Studio must still start.
    $data3 = Join-Path $work 'data3'
    New-Item -ItemType Directory -Path $data3 | Out-Null
    Set-Content -LiteralPath (Join-Path $data3 'trados') -Value 'x'
    $ready = Join-Path $work 'ready3.txt'; $stop = Join-Path $work 'stop3.txt'
    Set-Content -LiteralPath $stop -Value 'stop'   # exit as soon as started
    $proc = Start-Process powershell -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $work 'child3.err') -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$self`"", '-Child',
        '-Dll', "`"$Dll`"", '-DataRoot', "`"$data3`"", '-ReadyFile', "`"$ready`"", '-StopFile', "`"$stop`"")
    $null = $proc.Handle   # without this, Start-Process with a redirect reports no ExitCode
    $proc.WaitForExit(60000) | Out-Null
    $err = Get-Content -LiteralPath (Join-Path $work 'child3.err') -Raw -ErrorAction SilentlyContinue
    $line3 = if (Test-Path -LiteralPath $ready) { (Get-Content -LiteralPath $ready -Raw).Trim() } else { '' }
    Check ($proc.ExitCode -eq 0 -and $line3 -ne '') "D1 runtime folder cannot be made: EnsureBridgeStarted did not throw, the process carried on (exit $($proc.ExitCode), '$line3') $err"

    # No licence: no bridge, no exception, one note in bridge.log.
    $data4 = Join-Path $work 'data4'
    $ready = Join-Path $work 'ready4.txt'; $stop = Join-Path $work 'stop4.txt'
    Set-Content -LiteralPath $stop -Value 'stop'
    $proc = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$self`"", '-Child', '-NoLicence',
        '-Dll', "`"$Dll`"", '-DataRoot', "`"$data4`"", '-ReadyFile', "`"$ready`"", '-StopFile', "`"$stop`"")
    $proc.WaitForExit(60000) | Out-Null
    $line4 = if (Test-Path -LiteralPath $ready) { (Get-Content -LiteralPath $ready -Raw).Trim() } else { '' }
    $rt4 = Join-Path $data4 'trados\runtime'
    $notes = @(Select-String -LiteralPath (Join-Path $rt4 'bridge.log') -Pattern 'HasAssistantAccess=false' -ErrorAction SilentlyContinue).Count
    Check ($proc.ExitCode -eq 0 -and $line4 -like '*|False' -and -not (Test-Path -LiteralPath (Join-Path $rt4 'bridge.json'))) "D2 no licence: no bridge, no handshake, no exception ($line4)"
    Check ($notes -eq 1) "D3 no licence: noted once in bridge.log although asked twice (found $notes)"
}
finally {
    foreach ($c in $children) {
        try { Set-Content -LiteralPath $c.Stop -Value 'stop' } catch { }
        try { if (-not $c.Proc.HasExited) { $c.Proc.WaitForExit(15000) | Out-Null } } catch { }
        try { if (-not $c.Proc.HasExited) { $c.Proc.Kill() } } catch { }
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ("Removed work folder: " + (-not (Test-Path -LiteralPath $work)))
}

Write-Host ""
Write-Host "$($checks - $fails)/$checks checks passed"
if ($fails -gt 0) { exit 1 }
