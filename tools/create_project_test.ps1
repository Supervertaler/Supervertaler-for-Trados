# MCP create_project: every refusal, the clean-up after a failed run, and the
# scale of the checks - against the built plugin DLL, without Studio.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\create_project_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
#   ... -ScaleFiles 500                            (files in the scale check)
#
# Covers Core/ProjectCreator.cs (the part that needs no Studio). The Studio part
# (Core/ProjectCreatorStudio.cs: the Project Automation API, the licence check,
# the Projects list) can only be tested inside Studio - see the hand-off.
#
# Writes only into a new folder under %TEMP%, deleted at the end. Test data is
# synthetic (Acme / PROJ-001).
param(
    [string]$Dll,
    [int]$ScaleFiles = 500
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    foreach ($c in @('Studio18\Debug', 'Studio18\Release', 'Studio17\Debug', 'Studio19\Debug')) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
Write-Host "DLL: $Dll"

$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$PC = $plugin.GetType('Supervertaler.Trados.Core.ProjectCreator')
if (-not $PC) { throw 'ProjectCreator not found in the DLL' }
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$mPlan = $PC.GetMethod('Plan', $Static)
$mClean = $PC.GetMethod('CleanUp', $Static)
$mGroup = $PC.GetMethod('GroupBySubfolder', $Static)
$mNames = $PC.GetMethod('RegisteredProjectNames', $Static)
$mLang = $PC.GetMethod('IsKnownLanguage', $Static)

$fails = 0; $checks = 0
function Check($ok, $label) {
    $script:checks++
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function L([string[]]$items) {
    $l = New-Object 'System.Collections.Generic.List[string]'
    foreach ($i in $items) { $l.Add($i) }
    return , $l
}
$noNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Plan($name, $src, $tgts, $files, $template, $location, $default, $names = $noNames) {
    $a = New-Object 'object[]' 8
    function S($v) { if ($null -eq $v) { return $null } else { return [string]$v } }
    $a[0] = S $name; $a[1] = S $src; $a[4] = S $template; $a[5] = S $location; $a[6] = S $default; $a[7] = $names
    foreach ($i in 0, 1, 4, 5, 6) { if ($null -ne $a[$i]) { $a[$i] = $a[$i].psobject.BaseObject } }
    $a[2] = [System.Collections.Generic.List[string]]::new(); foreach ($t in $tgts) { $a[2].Add([string]$t) }
    $a[3] = [System.Collections.Generic.List[string]]::new(); foreach ($f in $files) { $a[3].Add([string]$f) }
    return $mPlan.Invoke($null, $a)
}
function Errs($plan) { return ($plan.Errors -join ' | ') }
function HasErr($plan, [string]$like) { return [bool]($plan.Errors | Where-Object { $_ -like "*$like*" }) }

$work = Join-Path $env:TEMP ("sv-create-project-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
function Snapshot { (Get-ChildItem -LiteralPath $work -Recurse -Force | ForEach-Object { $_.FullName + '|' + $_.Length }) -join "`n" }

try {
    # ---- fixtures ----------------------------------------------------------------
    $src = Join-Path $work 'source'
    New-Item -ItemType Directory -Path (Join-Path $src 'manuals') | Out-Null
    $doc1 = Join-Path $src 'Acme PROJ-001 spec.docx'; Set-Content -LiteralPath $doc1 -Value 'Acme spec' -Encoding UTF8
    $doc2 = Join-Path $src 'manuals\intro.txt'; Set-Content -LiteralPath $doc2 -Value 'Acme intro' -Encoding UTF8
    $doc3 = Join-Path $src 'intro.txt'; Set-Content -LiteralPath $doc3 -Value 'Acme intro top' -Encoding UTF8
    $empty = Join-Path $src 'empty.docx'; New-Item -ItemType File -Path $empty | Out-Null
    $lockf = Join-Path $src '~$me PROJ-001 spec.docx'; Set-Content -LiteralPath $lockf -Value 'x'
    $xliff = Join-Path $src 'already.docx.sdlxliff'; Set-Content -LiteralPath $xliff -Value '<xliff/>'
    $pkg = Join-Path $src 'job.sdlppx'; Set-Content -LiteralPath $pkg -Value 'PK'
    $tpl = Join-Path $work 'Acme legal.sdltpl'
    Set-Content -LiteralPath $tpl -Encoding UTF8 -Value '<?xml version="1.0"?><ProjectTemplate><LanguageDirections><LanguageDirection SourceLanguageCode="nl-NL" TargetLanguageCode="en-GB" /></LanguageDirections></ProjectTemplate>'
    # Like the real PATENTS template of 10 Nov: two pairs, a TM and a termbase that are gone.
    $tmOk = Join-Path $work 'TMs\Acme (nl-NL→en-GB).sdltm'
    New-Item -ItemType Directory -Path (Split-Path $tmOk) | Out-Null
    Set-Content -LiteralPath $tmOk -Value 'tm'
    $tmOkUri = 'sdltm.file:///' + ($tmOk -replace '\\', '/' -replace ' ', '%20')
    $tmGone = Join-Path $work 'TMs\Acme gone.sdltm'
    $tmGoneUri = 'sdltm.file:///' + ($tmGone -replace '\\', '/')
    $tbGone = Join-Path $work 'TBs\Acme gone.sdltb'
    $tpl2 = Join-Path $work 'Acme PATENTS (two pairs).sdltpl'
    $tbXml = [Security.SecurityElement]::Escape("<?xml version=`"1.0`" encoding=`"utf-16`"?><TermbaseSettings><Path>$tbGone</Path><Local>true</Local></TermbaseSettings>")
    Set-Content -LiteralPath $tpl2 -Encoding UTF8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<ProjectTemplate Guid="00000000-0000-0000-0000-000000000001">
  <LanguageDirections>
    <LanguageDirection TargetLanguageCode="nl-BE" SourceLanguageCode="en-US"><CascadeItem OverrideParent="false" /></LanguageDirection>
    <LanguageDirection TargetLanguageCode="en-GB" SourceLanguageCode="nl-NL"><CascadeItem OverrideParent="false" /></LanguageDirection>
  </LanguageDirections>
  <TermbaseConfiguration><Termbases><Name>Acme gone</Name><SettingsXml>$tbXml</SettingsXml><Enabled>true</Enabled></Termbases></TermbaseConfiguration>
  <CascadeItem OverrideParent="true">
    <CascadeEntryItem PerformUpdate="true"><MainTranslationProviderItem Uri="$tmOkUri" Enabled="true" /></CascadeEntryItem>
    <CascadeEntryItem PerformUpdate="true"><MainTranslationProviderItem Uri="$tmGoneUri" Enabled="true" /></CascadeEntryItem>
  </CascadeItem>
</ProjectTemplate>
"@
    $badTpl = Join-Path $work 'broken.sdltpl'; Set-Content -LiteralPath $badTpl -Value 'this is not xml <<<'
    $loc = Join-Path $work 'Projects'
    New-Item -ItemType Directory -Path $loc | Out-Null

    # ---- a good request ------------------------------------------------------------
    $before = Snapshot
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1, $doc2, $doc3, $doc1) $null $loc $null
    Check ($p.Errors.Count -eq 0) "good request has no errors ($(Errs $p))"
    Check ($p.ProjectFolder -eq (Join-Path $loc 'Acme PROJ-001')) "project folder is <location>\<name> ($($p.ProjectFolder))"
    Check (-not $p.FolderExisted) 'folder marked as new'
    Check ($p.Files.Count -eq 3) "a file listed twice is added once ($($p.Files.Count))"
    Check ($p.FileRoot -eq $src) "common folder of the files ($($p.FileRoot))"
    $groups = $mGroup.Invoke($null, @($p))
    $keys = @($groups | ForEach-Object { $_.Key })
    Check (($keys -contains '') -and ($keys -contains 'manuals') -and $keys.Count -eq 2) "files grouped by subfolder: [$($keys -join ', ')] (two intro.txt do not clash)"
    Check ((Snapshot) -eq $before) 'checking writes nothing'

    $p = Plan 'Acme PROJ-001' 'EN-gb' @('nl-nl, de-DE', 'NL-NL') @($doc1) $null $loc $null
    Check ($p.SourceLanguage -eq 'en-GB' -and ($p.TargetLanguages -join ',') -eq 'nl-NL,de-DE') "language codes canonicalised, comma lists split, duplicates dropped ($($p.SourceLanguage) -> $($p.TargetLanguages -join ','))"

    $p = Plan 'Acme PROJ-001' $null @() @($doc1) $tpl $loc $null
    Check ($p.Errors.Count -eq 0 -and $p.TemplatePath -eq $tpl) "with a template, languages may be left out ($(Errs $p))"
    Check ($p.SourceLanguage -eq 'nl-NL' -and ($p.TargetLanguages -join ',') -eq 'en-GB') "...and are taken from its one language pair, so Studio never guesses ($($p.SourceLanguage) -> $($p.TargetLanguages -join ','))"

    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) $null $null (Join-Path $work 'StudioDefault')
    Check ($p.ProjectFolder -eq (Join-Path $work 'StudioDefault\Acme PROJ-001')) 'no location: the default (Studio''s Projects folder) is used'

    $emptyExisting = Join-Path $loc 'Acme PROJ-002'; New-Item -ItemType Directory -Path $emptyExisting | Out-Null
    $p = Plan 'Acme PROJ-002' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check ($p.Errors.Count -eq 0 -and $p.FolderExisted) 'an existing EMPTY folder is accepted and marked as existing'

    $p = Plan 'Ácme – PROJ 003 (中文)' 'nl-NL' @('zh-CN') @($doc1) $null $loc $null
    Check ($p.Errors.Count -eq 0) "non-ASCII project name accepted ($(Errs $p))"

    # ---- refusals: name ------------------------------------------------------------
    $p = Plan '  ' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'No project name') 'refuses: no name'
    $p = Plan 'Acme: PROJ-001' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'cannot be used in a folder name') 'refuses: name with a colon'
    $p = Plan 'CON' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'cannot be used as a folder name') 'refuses: reserved Windows name CON'
    $p = Plan 'Acme.' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'cannot be used as a folder name') 'refuses: name ending in a dot'
    $p = Plan ('A' * 101) 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'longer than 100') 'refuses: name over 100 characters'
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$names.Add('Acme PROJ-001')
    $p = Plan 'acme proj-001' 'nl-NL' @('en-GB') @($doc1) $null $loc $null $names
    Check (HasErr $p 'already has a project called') 'refuses: name already in Studio''s project list (case-insensitive)'
    $full = Join-Path $loc 'Acme PROJ-004'; New-Item -ItemType Directory -Path $full | Out-Null
    Set-Content -LiteralPath (Join-Path $full 'keep.txt') -Value 'not ours'
    $p = Plan 'Acme PROJ-004' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'already exists and is not empty') 'refuses: folder exists and is not empty'
    Set-Content -LiteralPath (Join-Path $loc 'Acme PROJ-005') -Value 'a file, not a folder'
    $p = Plan 'Acme PROJ-005' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p 'is a file') 'refuses: a FILE of the project''s name is in the way'

    # ---- refusals: files -----------------------------------------------------------
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @() $null $loc $null
    Check (HasErr $p 'No files given') 'refuses: no files'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @((Join-Path $src 'missing.docx')) $null $loc $null
    Check (HasErr $p 'does not exist') 'refuses: missing file'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @('Acme.docx') $null $loc $null
    Check (HasErr $p 'not a full path') 'refuses: relative path'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @((Join-Path $src 'manuals')) $null $loc $null
    Check (HasErr $p 'is a folder') 'refuses: a folder given as a file'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($empty) $null $loc $null
    Check (HasErr $p 'is empty') 'refuses: zero-byte file'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($lockf) $null $loc $null
    Check (HasErr $p 'Office lock file') 'refuses: ~$ Office lock file'
    $zip = Join-Path $src 'extra.zip'; Set-Content -LiteralPath $zip -Value 'PK'
    $exe = Join-Path $src 'setup.exe'; Set-Content -LiteralPath $exe -Value 'MZ'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1, $zip, $exe) $null $loc $null
    Check ((HasErr $p '"extra.zip" is a .zip file') -and (HasErr $p 'unpack it') -and (HasErr $p '"setup.exe" is an .exe file') -and $p.Errors.Count -eq 2) "refuses up front: .zip and .exe, which Studio only warns about and copies unconverted ($(Errs $p))"
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($xliff, $pkg) $null $loc $null
    Check ((HasErr $p '.sdlxliff file') -and (HasErr $p '.sdlppx file')) 'refuses: .sdlxliff and .sdlppx (bilingual / package)'
    $fs = [IO.File]::Open($doc1, 'Open', 'ReadWrite', 'None')   # like a program holding it exclusively
    try {
        $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
        Check (HasErr $p 'another program may have it open') 'refuses: file held open exclusively by another program'
    } finally { $fs.Dispose() }
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1, (Join-Path $src 'missing.docx'), $empty) $null $loc $null
    Check ($p.Errors.Count -eq 2) "every problem is reported at once, not just the first ($($p.Errors.Count))"
    $p = Plan 'source' 'nl-NL' @('en-GB') @($doc1) $null $work $null
    Check ((HasErr $p 'already exists and is not empty')) 'refuses: project folder would be the source folder itself'

    # ---- refusals: languages -------------------------------------------------------
    $p = Plan 'Acme PROJ-001' 'xx-YY' @('en-GB') @($doc1) $null $loc $null
    Check (HasErr $p '"xx-YY" is not a language') 'refuses: unknown source language xx-YY'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('Klingon') @($doc1) $null $loc $null
    Check (HasErr $p '"Klingon" is not a language') 'refuses: target given as a name, not a code'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('nl-NL') @($doc1) $null $loc $null
    Check (HasErr $p 'cannot also be a target') 'refuses: target equal to source'
    $p = Plan 'Acme PROJ-001' $null @() @($doc1) $null $loc $null
    Check ((HasErr $p 'No source language') -and (HasErr $p 'No target language')) 'refuses: no languages and no template'
    foreach ($c in @('nl-NL', 'en-GB', 'en-US', 'de-DE', 'fr-BE', 'zh-CN', 'pt-BR', 'sr-Latn-RS', 'es-419', 'nl')) {
        $out = $null; $ok = $mLang.Invoke($null, @($c, $out))
        Check $ok "known language: $c"
    }
    foreach ($c in @('', 'english', 'en-GB-x', 'zz', 'qq-QQ', '12-34')) {
        Check (-not $mLang.Invoke($null, @($c, $null))) "unknown language refused: '$c'"
    }

    # ---- refusals: template --------------------------------------------------------
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) (Join-Path $work 'gone.sdltpl') $loc $null
    Check (HasErr $p 'does not exist') 'refuses: template that does not exist'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) $doc3 $loc $null
    Check (HasErr $p 'is not a project template') 'refuses: template that is not .sdltpl'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) 'Acme legal.sdltpl' $loc $null
    Check (HasErr $p 'full path to an .sdltpl') 'refuses: template given by name, not path'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) $badTpl $loc $null
    Check (HasErr $p 'could not be read as a Studio project template') 'refuses: template that is not XML'

    # ---- templates with several language pairs (Studio 2024 finding, 10 Nov) -------
    $p = Plan 'Acme PROJ-001' $null @() @($doc1) $tpl2 $loc $null
    Check ((HasErr $p 'more than one language pair') -and (HasErr $p 'nl-NL → en-GB') -and (HasErr $p 'en-US → nl-BE')) "refuses up front: two-pair template, no languages - and lists the pairs ($(Errs $p))"
    $p = Plan 'Acme PROJ-001' 'en-US' @() @($doc1) $tpl2 $loc $null
    Check ($p.Errors.Count -eq 0 -and ($p.TargetLanguages -join ',') -eq 'nl-BE') "two-pair template + source en-US: target nl-BE taken from the template ($(Errs $p))"
    $p = Plan 'Acme PROJ-001' $null @('en-GB') @($doc1) $tpl2 $loc $null
    Check ($p.Errors.Count -eq 0 -and $p.SourceLanguage -eq 'nl-NL') "two-pair template + target en-GB: source nl-NL taken from the template ($(Errs $p))"
    $p = Plan 'Acme PROJ-001' 'de-DE' @() @($doc1) $tpl2 $loc $null
    Check (HasErr $p 'no language pair from de-DE') "refuses: source the template has no pair for, and names its pairs ($(Errs $p))"
    $p = Plan 'Acme PROJ-001' 'de-DE' @('fr-FR') @($doc1) $tpl2 $loc $null
    Check ($p.Errors.Count -eq 0) 'languages given in full: used as given, template pairs not consulted'
    $p = Plan 'Acme PROJ-001' $null @() @($doc1) $tpl2 $loc $null
    Check ((@($p.Warnings | Where-Object { $_ -like "*Acme gone.sdltm*" }).Count -eq 1) -and (@($p.Warnings | Where-Object { $_ -like "*Acme gone.sdltb*" }).Count -eq 1)) "warns: the template's missing TM and missing termbase are named ($($p.Warnings -join ' | '))"
    Check (-not ($p.Warnings | Where-Object { $_ -like "*nl-NL→en-GB*" })) 'no warning for the template TM that exists (URI with %20 and an arrow decoded)'
    $mUri = $PC.GetMethod('TmPathFromUri', $Static)
    $decoded = $mUri.Invoke($null, [object[]]@(([string]$tmOkUri).psobject.BaseObject))
    Check ($decoded -eq $tmOk) "sdltm.file URI decoded to the path ($decoded)"
    $mSent = $PC.GetMethod('Sentence', $Static)
    Check ($mSent.Invoke($null, [object[]]@('template..', $true)) -eq 'template.') 'messages end in one full stop, not two'

    # ---- refusals: location --------------------------------------------------------
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) $null 'Projects' $null
    Check (HasErr $p 'is not a full path') 'refuses: relative location'
    $p = Plan 'Acme PROJ-001' 'nl-NL' @('en-GB') @($doc1) $null $null $null
    Check (HasErr $p 'No location given') 'refuses: no location and no Studio Projects folder'
    $p = Plan 'source' 'nl-NL' @('en-GB') @($doc1) $null $work $null
    Check ($p.Errors.Count -gt 0) 'refuses: a project folder that holds the source files'

    # ---- registered project names ---------------------------------------------------
    $xml = Join-Path $work 'projects.xml'
    Set-Content -LiteralPath $xml -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<ProjectServer><Projects>
  <ProjectListItem ProjectFilePath="Acme PROJ-001\Acme PROJ-001.sdlproj"><ProjectInfo Name="Acme PROJ-001" /></ProjectListItem>
  <ProjectListItem ProjectFilePath="x.sdlproj"><ProjectInfo Name="Acme legal 2026" /></ProjectListItem>
</Projects></ProjectServer>
'@
    $set = $mNames.Invoke($null, [object[]]@(([string]$xml).psobject.BaseObject))
    Check ($set.Count -eq 2 -and $set.Contains('ACME PROJ-001')) "projects.xml names read, case-insensitive ($($set.Count))"
    Check ($mNames.Invoke($null, [object[]]@(([string](Join-Path $work 'none.xml')).psobject.BaseObject)).Count -eq 0) 'missing projects.xml: empty set, no exception'
    Check ($mNames.Invoke($null, [object[]]@(([string]$badTpl).psobject.BaseObject)).Count -eq 0) 'malformed projects.xml: empty set, no exception'

    # ---- clean-up after a failed run -----------------------------------------------
    $p = Plan 'Acme PROJ-010' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    New-Item -ItemType Directory -Path (Join-Path $p.ProjectFolder 'en-GB') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $p.ProjectFolder 'Acme PROJ-010.sdlproj') -Value '<Project/>'
    Set-Content -LiteralPath (Join-Path $p.ProjectFolder 'en-GB\spec.docx.sdlxliff') -Value '<xliff/>'
    (Get-Item (Join-Path $p.ProjectFolder 'Acme PROJ-010.sdlproj')).IsReadOnly = $true
    $why = $mClean.Invoke($null, @($p))
    Check ($null -eq $why -and -not (Test-Path $p.ProjectFolder)) "clean-up: a folder we created is removed entirely ($why)"
    Check ((Test-Path $doc1) -and (Get-Content -LiteralPath $doc1) -eq 'Acme spec') 'clean-up: the source file is untouched'

    $p = Plan 'Acme PROJ-002' 'nl-NL' @('en-GB') @($doc1) $null $loc $null   # existing empty folder
    New-Item -ItemType Directory -Path (Join-Path $p.ProjectFolder 'nl-NL') | Out-Null
    Set-Content -LiteralPath (Join-Path $p.ProjectFolder 'Acme PROJ-002.sdlproj') -Value '<Project/>'
    $why = $mClean.Invoke($null, @($p))
    Check ($null -eq $why -and (Test-Path $p.ProjectFolder) -and -not (Get-ChildItem -LiteralPath $p.ProjectFolder -Force)) 'clean-up: a folder that was there (empty) is emptied but kept'

    $p = Plan 'Acme PROJ-011' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    Check ($null -eq $mClean.Invoke($null, @($p))) 'clean-up: nothing made yet (licence refusal) - nothing to do, no error'

    $p = Plan 'Acme PROJ-012' 'nl-NL' @('en-GB') @($doc1) $null $loc $null
    New-Item -ItemType Directory -Path $p.ProjectFolder | Out-Null
    $held = Join-Path $p.ProjectFolder 'held.sdlxliff'; Set-Content -LiteralPath $held -Value 'x'
    $fs = [IO.File]::Open($held, 'Open', 'Read', 'None')
    try { $why = $mClean.Invoke($null, @($p)) } finally { $fs.Dispose() }
    Check ($why -like '*could not remove*') "clean-up: a file still held open is reported, not hidden ($why)"
    [void]$mClean.Invoke($null, @($p))
    Check (-not (Test-Path $p.ProjectFolder)) 'clean-up: succeeds once the file is released'

    Check (Test-Path (Join-Path $loc 'Acme PROJ-004\keep.txt')) 'the non-empty folder that was refused is untouched'

    # ---- scale -----------------------------------------------------------------------
    $big = Join-Path $work 'big'
    $files = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $ScaleFiles; $i++) {
        $d = Join-Path $big ("chapter{0:D2}" -f ($i % 25))
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
        $f = Join-Path $d ("Acme part {0:D4}.txt" -f $i)
        [IO.File]::WriteAllText($f, "Acme PROJ-001 part $i")
        $files.Add($f)
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Plan 'Acme PROJ-100' 'nl-NL' @('en-GB', 'de-DE', 'fr-FR') $files $null $loc $null
    $sw.Stop()
    $g = $mGroup.Invoke($null, @($p))
    Check ($p.Errors.Count -eq 0 -and $p.Files.Count -eq $ScaleFiles) "scale: $ScaleFiles files in 25 subfolders checked without errors"
    Check (@($g).Count -eq 25) "scale: $ScaleFiles files -> $(@($g).Count) AddFiles calls (one per subfolder, not per file)"
    Check ($sw.ElapsedMilliseconds -lt 5000) "scale: checking $ScaleFiles files took $($sw.ElapsedMilliseconds) ms (runs on the bridge thread, not the UI thread)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Check (-not (Test-Path $work)) 'temp folder removed'
Write-Host ""
Write-Host "$($checks - $fails)/$checks passed"
if ($fails -gt 0) { exit 1 }
