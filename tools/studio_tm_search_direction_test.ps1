# MCP search_studio_tm on a TM stored the other way round (18.20.206 / 19.20.206).
#
# search_studio_tm searched every memory as stored: in an NL>EN project with an
# EN>NL memory attached, searchIn "source" ran over that memory's ENGLISH side,
# and its matches came back with the English as "source". Now "source" and
# "target" are the project's languages, and every match comes back the
# project's way round - as SuperSearch already did.
#
# Builds three scratch .sdltm files in %TEMP% - NL>EN, EN>NL (reversed) and
# NL>DE (another pair) - and runs the plugin's own search_studio_tm search
# (AiAssistantViewPart.SearchStudioTms) for an NL>EN document. On a build from
# before the fix, which has no SearchStudioTms, it runs the call that build's
# bridge made instead (TmSearcher.Search with no document languages), so the
# same checks show the old behaviour.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\studio_tm_search_direction_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Studio19 Debug build)
#
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\studio_tm_search_direction_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Reads no real TM; writes nothing outside its scratch folder, which it deletes.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Debug', 'Studio19\Release') } else { @('Studio18\Debug', 'Studio18\Release', 'Studio17\Debug') }
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
Write-Host "Plugin: $Dll"

Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.IO; using System.Reflection;
public static class TmDirTestResolver {
    static readonly Dictionary<string, Assembly> L = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    public static void Register(string[] dirs) {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e) {
            var n = new AssemblyName(e.Name).Name;
            lock (L) {
                Assembly a;
                if (L.TryGetValue(n, out a)) return a;
                L[n] = null;
                foreach (var d in dirs) { var c = Path.Combine(d, n + ".dll"); if (File.Exists(c)) { try { a = Assembly.LoadFrom(c); } catch { } break; } }
                L[n] = a; return a;
            }
        };
    }
}
'@
[TmDirTestResolver]::Register(@((Split-Path $Dll), $studio))
foreach ($d in 'Sdl.Core.Globalization', 'Sdl.LanguagePlatform.Core', 'Sdl.LanguagePlatform.TranslationMemory', 'Sdl.LanguagePlatform.TranslationMemoryApi') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $studio "$d.dll"))
}
$asm = [Reflection.Assembly]::LoadFrom($Dll)

$work = Join-Path $env:TEMP ("sv-tm-direction-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
[void](New-Item -ItemType Directory -Path $work -Force)
# Keep the plugin's data root (logs, settings) inside the scratch folder.
$dataRoot = Join-Path $work "data-root"
[void](New-Item -ItemType Directory -Path $dataRoot -Force)
$asm.GetType("Supervertaler.Core.SupervertalerPaths").GetMethod("Set").Invoke($null, [object[]]@([string]$dataRoot))
$rlf = $asm.GetType("Supervertaler.Core.ApiKeyStore").GetMethod("RedirectLockedFile", [Reflection.BindingFlags]"Static,NonPublic")
if ($rlf) { [void]$rlf.Invoke($null, [object[]]@([string](Join-Path $dataRoot "api-keys.locked.json"))) }

$fbtm = [Sdl.LanguagePlatform.TranslationMemoryApi.FileBasedTranslationMemory]
$ctor = $fbtm.GetConstructors() | Where-Object { $_.GetParameters().Count -eq 8 } | Select-Object -First 1

function Lang($type, [string]$name) {
    if ($type -eq [Globalization.CultureInfo]) { return [Globalization.CultureInfo]::GetCultureInfo($name) }
    return [Activator]::CreateInstance($type, [object[]]@($name))
}

function New-Tm([string]$file, [string]$src, [string]$tgt, [string]$srcText, [string]$tgtText) {
    $path = Join-Path $work $file
    $ps = $ctor.GetParameters()
    $fuzzy = [Sdl.LanguagePlatform.TranslationMemory.FuzzyIndexes]"SourceWordBased,SourceCharacterBased,TargetWordBased,TargetCharacterBased"
    # Unwrapped one by one: an array built inline hands Invoke PSObject wrappers.
    $a8 = New-Object 'object[]' 8
    $a8[0] = [string]$path
    $a8[1] = "search_studio_tm direction test"
    $a8[2] = (Lang $ps[2].ParameterType $src).psobject.BaseObject
    $a8[3] = (Lang $ps[3].ParameterType $tgt).psobject.BaseObject
    $a8[4] = $fuzzy
    $a8[5] = [Enum]::ToObject($ps[5].ParameterType, 0)
    $a8[6] = [Enum]::ToObject($ps[6].ParameterType, 0)
    $a8[7] = [Enum]::ToObject($ps[7].ParameterType, 0)
    $tm = $ctor.Invoke($a8)
    $ld = $tm.LanguageDirection
    $tu = New-Object Sdl.LanguagePlatform.TranslationMemory.TranslationUnit
    $tu.SourceSegment = New-Object Sdl.LanguagePlatform.Core.Segment([Globalization.CultureInfo]::GetCultureInfo($src))
    [void]$tu.SourceSegment.Add($srcText)
    $tu.TargetSegment = New-Object Sdl.LanguagePlatform.Core.Segment([Globalization.CultureInfo]::GetCultureInfo($tgt))
    [void]$tu.TargetSegment.Add($tgtText)
    $tus = New-Object 'Sdl.LanguagePlatform.TranslationMemory.TranslationUnit[]' 1
    $tus[0] = $tu
    $mask = New-Object 'bool[]' 1
    $mask[0] = $true
    [void]$ld.AddTranslationUnitsMasked($tus, (New-Object Sdl.LanguagePlatform.TranslationMemory.ImportSettings), $mask)
    return $path
}

$script:fails = 0; $script:passes = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "PASS  $what"; $script:passes++ }
    else { Write-Host "FAIL  $what" -ForegroundColor Red; $script:fails++ }
}

try {
    $nlen = New-Tm "Acme NL-EN.sdltm" "nl-NL" "en-GB" "De dakopstand wordt verhoogd." "The roof upstand is raised."
    $ennl = New-Tm "Acme EN-NL.sdltm" "en-GB" "nl-NL" "The roof upstand is lowered." "De dakopstand wordt verlaagd."
    $nlde = New-Tm "Acme NL-DE.sdltm" "nl-NL" "de-DE" "De dakopstand wordt verlengd." "Die Dachaufkantung wird verlaengert."
    $tms = [System.Collections.Generic.List[string]]::new()
    $tms.Add([string]$nlen); $tms.Add([string]$ennl); $tms.Add([string]$nlde)

    $NP = [Reflection.BindingFlags]"NonPublic,Public,Static"
    $view = $asm.GetType("Supervertaler.Trados.AiAssistantViewPart")
    $helper = $view.GetMethod("SearchStudioTms", $NP)
    $queryT = $asm.GetType("Supervertaler.Trados.Core.BridgeStudioTmQuery")
    $searcher = $asm.GetType("Supervertaler.Trados.Core.TmSearcher")
    $scopeT = $asm.GetType("Supervertaler.Trados.Core.SearchScope")
    if ($helper) { Write-Host "Using AiAssistantViewPart.SearchStudioTms" }
    else { Write-Host "No SearchStudioTms in this build: running its bridge's old call (no document languages)" -ForegroundColor Yellow }

    # Returns the matches as search_studio_tm hands them to the MCP client.
    function Search([string]$text, [string]$in, $docSrc, $docTgt) {
        if ($helper) {
            $q = [Activator]::CreateInstance($queryT)
            $q.Query = $text; $q.In = $in; $q.Limit = 10
            $a = New-Object 'object[]' 4
            $a[0] = $tms.psobject.BaseObject; $a[1] = $q
            $a[2] = if ($docSrc) { [string]$docSrc } else { $null }
            $a[3] = if ($docTgt) { [string]$docTgt } else { $null }
            $resp = $helper.Invoke($null, $a)
            if (-not $resp.Ok) { throw "search failed: $($resp.Error)" }
            return @($resp.Matches | ForEach-Object { [pscustomobject]@{ Tm = $_.TmName; Source = $_.Source; Target = $_.Target } })
        }
        $scope = switch ($in) { "source" { "SourceOnly" } "target" { "TargetOnly" } default { "SourceAndTarget" } }
        $a = New-Object 'object[]' 10
        $a[0] = $tms.psobject.BaseObject; $a[1] = [string]$text; $a[2] = [Enum]::Parse($scopeT, $scope)
        $a[3] = $false; $a[4] = $false; $a[5] = $false
        $a[6] = $null; $a[7] = $null; $a[8] = $null; $a[9] = [Threading.CancellationToken]::None
        $r = $searcher.GetMethod("Search").Invoke($null, $a)
        return @($r | ForEach-Object { [pscustomobject]@{ Tm = $_.FileName; Source = $_.SourceText; Target = $_.TargetText } })
    }
    function Hit($rows, [string]$tm) { @($rows | Where-Object { $_.Tm -eq $tm }) | Select-Object -First 1 }

    Write-Host "--- NL>EN project, searchIn 'source', Dutch text"
    $r = Search "dakopstand" "source" "nl-NL" "en-GB"
    $h = Hit $r "Acme NL-EN"
    Check ($h -and $h.Source -eq "De dakopstand wordt verhoogd.") "NL>EN memory: found, Dutch as source"
    $h = Hit $r "Acme EN-NL"
    Check ($h -and $h.Source -eq "De dakopstand wordt verlaagd." -and $h.Target -eq "The roof upstand is lowered.") "EN>NL memory: found on its Dutch side, returned Dutch as source"
    Check ((Hit $r "Acme NL-DE") -ne $null) "NL>DE memory: searched as stored, found"

    Write-Host "--- searchIn 'source' with English text must find nothing in English"
    $r = Search "roof upstand" "source" "nl-NL" "en-GB"
    Check (-not (Hit $r "Acme EN-NL")) "EN>NL memory: English is the project's TARGET, so 'source' does not search it"
    Check (-not (Hit $r "Acme NL-EN")) "NL>EN memory: no English on its source side"

    Write-Host "--- searchIn 'target', English text"
    $r = Search "roof upstand" "target" "nl-NL" "en-GB"
    $h = Hit $r "Acme EN-NL"
    Check ($h -and $h.Target -eq "The roof upstand is lowered." -and $h.Source -eq "De dakopstand wordt verlaagd.") "EN>NL memory: found on its English side, returned English as target"
    $h = Hit $r "Acme NL-EN"
    Check ($h -and $h.Target -eq "The roof upstand is raised.") "NL>EN memory: found as before"

    Write-Host "--- searchIn 'both' (default): rows face the project"
    $r = Search "dakopstand" "both" "nl-NL" "en-GB"
    $h = Hit $r "Acme EN-NL"
    Check ($h -and $h.Source -eq "De dakopstand wordt verlaagd.") "EN>NL memory: returned Dutch as source"

    Write-Host "--- EN>NL project: the NL>EN memory is now the reversed one"
    $r = Search "roof upstand" "source" "en-GB" "nl-NL"
    $h = Hit $r "Acme NL-EN"
    Check ($h -and $h.Source -eq "The roof upstand is raised." -and $h.Target -eq "De dakopstand wordt verhoogd.") "NL>EN memory: turned round"
    $h = Hit $r "Acme EN-NL"
    Check ($h -and $h.Source -eq "The roof upstand is lowered.") "EN>NL memory: as stored"

    Write-Host "--- No document languages known: as stored (the old behaviour, kept as the fallback)"
    $r = Search "dakopstand" "source" $null $null
    Check (-not (Hit $r "Acme EN-NL")) "EN>NL memory: searched on its own (English) source side"
}
finally {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "$script:passes passed, $script:fails failed"
if ($script:fails -gt 0) { exit 1 }
