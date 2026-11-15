# Translate active segment (Alt+T): the same memory bank with the Supervertaler
# Assistant pane and without it - checked against the built plugin DLL, outside
# Studio.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\altt_same_context_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\altt_same_context_test.ps1
#
# Alt+T has two paths in AiAssistantViewPart.cs: one for when the pane has been
# set up, one for when it has not. Until 208 the second sent no memory bank at
# all, so the same translator with the same settings got different translations
# depending on whether the pane had been set up. Both now get the bank from ONE
# routine, TranslationBankContext.ForDocument, and the bank a project uses from
# ONE rule, TranslationBankContext.BankForProject. This pins both: the routine
# and the rule are what is tested here, and the source is read to see that each
# path calls them and that nothing else in the view part builds a bank block for
# a translation.
#
# What this cannot reach, outside Studio: the key press itself, the pane object,
# the Trados document (its segments, languages and project are handed in as
# plain strings here), and the request that is finally sent.
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set, team
# folder included), deleted at the end. No AI request is made: the article
# choice a large bank would ask a model for gets no client, which is the
# "no key" case and keeps every article.
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

Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class AltTContextTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[AltTContextTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Any    = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

$paths   = $asm.GetType('Supervertaler.Core.SupervertalerPaths', $true)
$readerT = $asm.GetType('Supervertaler.Core.MemoryBankReader', $true)
$tbc     = $asm.GetType('Supervertaler.Trados.Core.TranslationBankContext', $true)
$aiT     = $asm.GetType('Supervertaler.Trados.Settings.AiSettings', $true)
$diagT   = $asm.GetType('Supervertaler.Trados.Core.DiagnosticLog', $true)

# Its own data folder, team folder included, before anything of the plugin runs.
$temp = Join-Path $env:TEMP ("sv-altt-context-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$paths.GetMethod('Set', $Static).Invoke($null, @([string]$temp)) | Out-Null
$paths.GetField('_contentRoot', $Static).SetValue($null, [string]$temp)
$banks = $paths.GetProperty('MemoryBanksDir', $Static).GetValue($null)
if ($banks -ne (Join-Path $temp 'memory-banks')) { throw "Not pointed at the temp folder: $banks" }
$diagPath = [string]$diagT.GetProperty('LogFilePath', $Static).GetValue($null)
if (-not $diagPath.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw "The diagnostic log is not in the temp folder: $diagPath" }
Check $true "data folder and diagnostic log are under the temp folder"

function Method($type, $name, $count) {
    $m = @($type.GetMethods($Static) | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $count })
    if ($m.Count -ne 1) { throw "$($m.Count) methods named $name take $count arguments" }
    return $m[0]
}
function Utf8($path, $text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}
function Settings([bool]$on) {
    $ai = [Activator]::CreateInstance($aiT)
    $ai.IncludeSuperMemoryContext = $on
    return $ai
}
function Field($obj, $name) { if ($null -eq $obj) { return $null }; return $obj.GetType().GetField($name, $Any).GetValue($obj) }

# The one routine: TranslationBankContext.ForDocument, as both Alt+T paths (and
# Batch Translate, Proofread, Preview prompt and SuperBench) call it. Returns
# the task; Block/LogOf read what it holds.
$forDocument = Method $tbc 'ForDocument' 9
function ForDocumentTask($ai, $bank, $target, $docKey, $docText) {
    $argv = New-Object object[] 9
    $argv[0] = $ai; $argv[1] = $bank; $argv[2] = 'PROJ-001'; $argv[3] = 'English (United Kingdom)'; $argv[4] = [string]$target
    $argv[5] = [string]$docKey; $argv[6] = 'Acme manual.docx.sdlxliff'
    $state = $script:state
    $argv[7] = [Func[string]] { $state.Asked++; return [string]$docText }.GetNewClosure()
    $argv[8] = $null   # no client: no AI request, every article kept
    return $forDocument.Invoke($null, $argv)
}
function ResultOf($task) {
    if (-not $task.Wait(60000)) { throw "the bank block was not ready within a minute" }
    return $task.GetType().GetProperty('Result').GetValue($task, $null)
}
function Block($task) { return [string](Field (ResultOf $task) 'Block') }
function LogOf($task) { $r = ResultOf $task; if ($null -eq $r) { return @() }; return @(Field $r 'Log') }
$script:state = @{ Asked = 0 }   # how often the routine asked for the document's text
$script:docN = 0
function Send($ai, $bank, $target, $docText) {
    $script:docN++
    return ForDocumentTask $ai $bank $target ("doc-" + $script:docN) $docText
}

# What the pane path sent before the two were joined, rebuilt from the same
# building blocks its code called then: the bank loaded untrimmed for
# translation, then TranslationBankContext.Build.
$build = Method $tbc 'Build' 9
function Reference($bank, $target, $docText) {
    $dir = Join-Path $banks $bank
    $reader = [Activator]::CreateInstance($readerT, @([string]$dir))
    if (-not $reader.VaultExists) { return '' }
    $full = $reader.LoadContext('PROJ-001', $null, 'English (United Kingdom)', [string]$target, 0, $null, $true)
    if ($null -eq $full) { return '' }
    $argv = New-Object object[] 9
    $argv[0] = $full; $argv[1] = $docText; $argv[2] = 'English (United Kingdom)'; $argv[3] = [string]$target
    $argv[7] = 'reference'; $argv[8] = 'reference - ' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    return [string](Field ($build.Invoke($null, $argv)) 'Block')
}

# The one rule for which bank a project reads.
$bankForProject = Method $tbc 'BankForProject' 3
function BankFor($recorded, $projectName) {
    $argv = New-Object object[] 3
    $argv[0] = $recorded; $argv[1] = $projectName; $argv[2] = $false
    $name = $bankForProject.Invoke($null, $argv)
    return @{ Name = [string]$name; Matched = [bool]$argv[2] }
}

$on = Settings $true
$off = Settings $false
$nl = 'Dutch (Netherlands)'

try {
    Write-Host "1. A project with its own bank"
    Utf8 "$banks\acme-legal\brief.md" "# Acme`r`n`r`nAlways 'operating manual', never 'user guide'.`r`n"
    Utf8 "$banks\acme-legal\terminology.md" "# Terms`r`n`r`n| EN | NL | Note |`r`n|---|---|---|`r`n| widget | dinges | PROJ-001 decision |`r`n"
    $b = BankFor 'acme-legal' 'PROJ-001'
    Check ($b.Name -eq 'acme-legal' -and -not $b.Matched) "the bank recorded for the project is the one read"
    $block = Block (Send $on $b.Name $nl 'A widget.')
    Check ($block -match 'operating manual' -and $block -match 'dinges') "its brief and its terminology are in the block"
    Check ($block -ceq (Reference 'acme-legal' $nl 'A widget.')) "byte for byte what the pane path's building blocks give"
    Check ($block -notmatch '_shared') "and nothing of a shared bank that does not exist"

    Write-Host "2. The bank a project reads, with no pane to have applied it"
    $b = BankFor $null 'PROJ-001'
    Check ($b.Name -eq '' -and -not $b.Matched) "no bank recorded and none named after the project: none of its own"
    Utf8 "$banks\proj-001\brief.md" "# PROJ-001`r`n`r`nThe project's own bank, found by its name.`r`n"
    $b = BankFor $null 'PROJ-001'
    Check ($b.Name -eq 'proj-001' -and $b.Matched) "a bank named after the project is read, and reported as matched by name"
    $b = BankFor '' 'PROJ-001'
    Check ($b.Name -eq 'proj-001' -and $b.Matched) "an empty record counts as none"
    $b = BankFor 'acme-legal' 'PROJ-001'
    Check ($b.Name -eq 'acme-legal' -and -not $b.Matched) "a recorded bank wins over the one named after the project"
    $b = BankFor $null 'Proj 001'
    Check ($b.Name -eq 'proj-001' -and $b.Matched) "the match is on the folder name New memory bank makes of the project's name ('Proj 001' is proj-001)"
    $b = BankFor $null 'PROJ-0011'
    Check ($b.Name -eq '') "a near miss is not a match"
    $b = BankFor $null $null
    Check ($b.Name -eq '') "no project name: none, not an error"
    Utf8 "$banks\_shared\brief.md" "# House defaults`r`n`r`nHouse rule: thousands separator is a thin space.`r`n"
    $b = BankFor $null '_shared'
    Check ($b.Name -ne '_shared') "a project called _shared does not get the shared bank as its own"
    $block = Block (Send $on 'proj-001' $nl 'Text.')
    Check ($block -match 'found by its name') "the matched bank's content is what goes"

    Write-Host "3. Both: the project's bank and the shared bank"
    $block = Block (Send $on 'acme-legal' $nl 'A widget.')
    Check ($block -match 'operating manual' -and $block -match 'thin space') "the project's brief and the shared brief are both in the block"
    Check ($block -ceq (Reference 'acme-legal' $nl 'A widget.')) "byte for byte what the pane path's building blocks give"

    Write-Host "4. Only the shared bank"
    # A project with no bank of its own reads the default bank. With a default
    # bank on disk, _shared goes on top of it.
    Utf8 "$banks\default\brief.md" "# Default`r`n`r`nDefault bank note: use British spelling.`r`n"
    $block = Block (Send $on '' $nl 'Text.')
    Check ($block -match 'British spelling' -and $block -match 'thin space') "no bank of its own, a default bank on disk: default and shared both go"
    Check ($block -ceq (Reference 'default' $nl 'Text.')) "byte for byte what the pane path's building blocks give"
    $block = Block (Send $on '   ' $nl 'Text.')
    Check ($block -match 'British spelling') "a blank bank name is the default bank"
    # With NO default bank folder, nothing goes - not even _shared. That is what
    # the pane path has always done (its check that the active bank exists comes
    # before the load), so it is what both paths do; whether it should be is for
    # the owner to decide, and one line in ForDocument when he does.
    Remove-Item -Recurse -Force "$banks\default"
    $task = Send $on '' $nl 'Text.'
    Check ($null -eq (ResultOf $task)) "no bank of its own and no default bank on disk: nothing is sent, shared included (as the pane path)"

    Write-Host "5. Files for one target language"
    Utf8 "$banks\acme-legal\style.nl-BE.md" "# Style, Belgium`r`n`r`nBelgian rule: 'gsm', not 'mobieltje'.`r`n"
    Utf8 "$banks\acme-legal\style.fr-FR.md" "# Style, France`r`n`r`nFrench rule: espace avant les deux-points.`r`n"
    Utf8 "$banks\_shared\style.nl.md" "# House style, Dutch`r`n`r`nDutch house rule: no capital after a colon.`r`n"
    $be = Block (Send $on 'acme-legal' 'Dutch (Belgium)' 'A widget.')
    Check ($be -match 'gsm' -and $be -match 'no capital after a colon' -and $be -notmatch 'deux-points') "into Dutch (Belgium): style.nl-BE.md and the shared style.nl.md go, style.fr-FR.md does not"
    Check ($be -ceq (Reference 'acme-legal' 'Dutch (Belgium)' 'A widget.')) "byte for byte what the pane path's building blocks give"
    $fr = Block (Send $on 'acme-legal' 'French (France)' 'A widget.')
    Check ($fr -match 'deux-points' -and $fr -notmatch 'gsm' -and $fr -notmatch 'no capital after a colon') "into French (France): only style.fr-FR.md of the three"
    Check ($fr -ceq (Reference 'acme-legal' 'French (France)' 'A widget.')) "byte for byte what the pane path's building blocks give"
    $nlBlock = Block (Send $on 'acme-legal' $nl 'A widget.')
    Check ($nlBlock -match 'no capital after a colon' -and $nlBlock -notmatch 'gsm' -and $nlBlock -notmatch 'deux-points') "into Dutch (Netherlands): the shared style.nl.md only"

    Write-Host "6. The setting switched off"
    $task = Send $off 'acme-legal' $nl 'A widget.'
    Check ($task.IsCompleted -and $null -eq (ResultOf $task)) "'Include SuperMemory knowledge base in AI context' off: no block, at once"
    $task = Send $null 'acme-legal' $nl 'A widget.'
    Check ((Block $task) -match 'operating manual') "no settings object at all is not 'off' (as the pane path reads it)"

    Write-Host "7. No bank at all"
    $task = Send $on 'no-such-bank' $nl 'Text.'
    Check ($task.IsCompleted -and $null -eq (ResultOf $task)) "a bank that is not on disk: no block, and no error"
    New-Item -ItemType Directory -Force -Path "$banks\empty-bank" | Out-Null
    $task = Send $on 'empty-bank' $nl 'Text.'
    Check ($null -eq (ResultOf $task)) "a bank folder with no files: no block"
    $task = Send $on '..\..\outside' $nl 'Text.'
    Check ($null -eq (ResultOf $task)) "a bank name that is not a folder name reads nothing outside the banks folder"

    Write-Host "8. A bank file that cannot be read"
    $before = Block (Send $on 'acme-legal' $nl 'A widget.')
    $lock = [IO.File]::Open("$banks\acme-legal\brief.md", 'Open', 'ReadWrite', 'None')
    try {
        $task = Send $on 'acme-legal' $nl 'A widget.'
        $locked = Block $task
        $ref = Reference 'acme-legal' $nl 'A widget.'
    } finally { $lock.Close() }
    Check ($locked -notmatch 'operating manual' -and $locked -match 'dinges' -and $locked -match 'thin space') "a locked brief.md is left out; the terminology and the shared bank still go"
    Check ($locked -ceq $ref) "byte for byte what the pane path's building blocks give for the same locked file"
    $after = Block (Send $on 'acme-legal' $nl 'A widget.')
    Check ($after -ceq $before) "and once it can be read again, the next document gets it back"

    # Anything that throws while the block is prepared: no block, the
    # translation goes on, and the diagnostic log says so whatever the logging
    # setting. Thrown here from the one thing a test can make throw, the read of
    # the document for a bank large enough to need it.
    $rows = New-Object Text.StringBuilder
    [void]$rows.Append("# Terms`r`n`r`n| EN | NL | Note |`r`n|---|---|---|`r`n")
    for ($i = 1; $i -le 1500; $i++) { [void]$rows.Append("| gadget$i | apparaat$i | decided for PROJ-001 |`r`n") }
    Utf8 "$banks\big-bank\terminology.md" $rows.ToString()
    Utf8 "$banks\big-bank\brief.md" "# Big`r`n`r`nBig bank brief: formal register.`r`n"
    $diagT.GetProperty('Enabled', $Static).SetValue($null, $false, $null)
    $argv = New-Object object[] 9
    $argv[0] = $on; $argv[1] = 'big-bank'; $argv[2] = 'PROJ-001'; $argv[3] = 'English (United Kingdom)'; $argv[4] = $nl
    $argv[5] = 'doc-throws'; $argv[6] = 'Acme manual.docx.sdlxliff'
    $argv[7] = [Func[string]] { throw 'the document went away' }
    $threw = $false
    try { $task = $forDocument.Invoke($null, $argv) } catch { $threw = $true }
    Check ((-not $threw) -and $task.IsCompleted -and $null -eq (ResultOf $task)) "a failure while preparing the block gives no block and does not throw"
    $diag = if (Test-Path $diagPath) { [IO.File]::ReadAllText($diagPath) } else { '' }
    Check ($diag -match 'Could not prepare the bank for translation' -and $diag -match 'the document went away') "and it is written to the diagnostic log, with logging switched off"

    Write-Host "9. A bank over the threshold, and over the budget"
    $doc = "The gadget7 and the gadget1200 are fitted. gadget33 is optional."
    Check ($script:state.Asked -eq 0) "a bank at or under the threshold never asks for the document's text ($($script:docN) calls so far)"
    $task = ForDocumentTask $on 'big-bank' $nl 'doc-big' $doc
    $big = Block $task
    Check ($script:state.Asked -eq 1) "a bank over the threshold reads it, once"
    Check ($big -match 'apparaat7 ' -and $big -match 'apparaat1200' -and $big -match 'apparaat33' -and $big -notmatch 'apparaat8 ' -and $big -notmatch 'apparaat1500') "only the terminology rows whose terms occur in the document go"
    Check ($big -match 'formal register' -and $big -match 'thin space') "the bank's brief and the shared bank still go"
    Check ($big -ceq (Reference 'big-bank' $nl $doc)) "byte for byte what the pane path's building blocks give"
    $log = (LogOf $task) -join "`n"
    Check ($log -match 'SuperMemory: sending [\d,]+ tokens of the bank''s [\d,]+ tokens, selected for this document') "the log lines say how much of the bank goes: $((LogOf $task)[0])"
    $extracts = Join-Path $temp 'trados\bank-extracts'
    Check ((Test-Path $extracts) -and @(Get-ChildItem $extracts -Filter 'Acme manual.docx - *.md').Count -ge 1 -and $log -match [regex]::Escape($extracts)) "what was sent is written under the temp data folder, and the log names the file"

    # Over the 24,000-token budget even after selecting: four notes of about
    # 10,000 tokens each, of which the budget has room for two.
    $para = "Paragraph about the gadget7 and how it is described in Acme manuals. "
    foreach ($n in 1..4) {
        $note = New-Object Text.StringBuilder
        [void]$note.Append("# Long note $n`r`n`r`n")
        while ($note.Length -lt 40000) { [void]$note.Append($para) }
        Utf8 "$banks\big-bank\long-note-$n.md" $note.ToString()
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $task = ForDocumentTask $on 'big-bank' $nl 'doc-big' $doc
    $callMs = $timer.ElapsedMilliseconds   # what the caller's thread - Studio's UI thread - spends
    $over = Block $task
    $timer.Stop()
    $budget = [int]$tbc.GetField('TokenBudget', $Static).GetValue($null)
    $sent = [int]($over.Length / 4)
    Check ($sent -gt 10000 -and $sent -le ($budget + 1500)) "a bank over the budget is cut to it: about $sent tokens sent of some 60,000, budget $budget"
    Check ($callMs -lt 2000) "reading it took the caller $callMs ms, and the block was ready after $($timer.ElapsedMilliseconds) ms (the selection runs on another thread)"
    Check ($over -match 'formal register') "the brief survives the cut"
    Check ($over -ceq (Reference 'big-bank' $nl $doc)) "byte for byte what the pane path's building blocks give"
    Check ($over -cne $big) "and a changed bank made a new block for the same document"

    Write-Host "10. One block per document, for both paths"
    $t1 = ForDocumentTask $on 'acme-legal' $nl 'doc-shared-by-both' 'A widget.'
    $t2 = ForDocumentTask $on 'acme-legal' $nl 'doc-shared-by-both' 'A widget.'
    Check ([object]::ReferenceEquals($t1, $t2)) "a second caller for the same document, bank and languages gets the very same block"
    $t3 = ForDocumentTask $on 'acme-legal' 'Dutch (Belgium)' 'doc-shared-by-both' 'A widget.'
    Check (-not [object]::ReferenceEquals($t1, $t3) -and (Block $t3) -match 'gsm') "another target language gets its own"
    Start-Sleep -Milliseconds 50
    Utf8 "$banks\acme-legal\brief.md" "# Acme`r`n`r`nAlways 'operating manual'. Edited: never 'handbook' either.`r`n"
    $t4 = ForDocumentTask $on 'acme-legal' $nl 'doc-shared-by-both' 'A widget.'
    Check ((Block $t4) -match 'handbook') "an edited bank file is picked up on the next press"
    $slow = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 200; $i++) { [void](ForDocumentTask $on 'big-bank' $nl 'doc-big' $doc) }
    $slow.Stop()
    Check ($slow.ElapsedMilliseconds -lt 4000) "200 presses in one document re-read nothing: $($slow.ElapsedMilliseconds) ms in all, on a bank of $([int]((Get-ChildItem "$banks\big-bank" -File | Measure-Object Length -Sum).Sum / 1024)) KB"

    Write-Host "11. Both Alt+T paths call that routine, and nothing else builds the block"
    $src = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\AiAssistantViewPart.cs'))
    function Body($name) {
        $at = $src.IndexOf($name)
        if ($at -lt 0) { throw "$name not found in AiAssistantViewPart.cs" }
        $end = $src.IndexOf("`n        }", $at)
        return $src.Substring($at, $end - $at)
    }
    $pane = Body 'public static void HandleTranslateActiveSegment()'
    $alone = Body 'private static void TranslateActiveSegmentStandalone()'
    Check ($pane -match 'KbContextForTranslation\(' -and $pane -match 'AwaitKbForTranslation\(' -and $pane -match 'docSegments, kbContext') "with the pane: the bank block is made, waited for and handed to the translator"
    Check ($alone -match 'KbContextForTranslation\(' -and $alone -match 'AwaitKbForTranslation\(' -and $alone -match 'docSegments, kbContext') "without the pane: the same three steps"
    Check ($alone -match 'ProjectBankNameFor\(') "without the pane, the project's bank is read from disk"
    Check ($pane -match 'CollectDocumentContext\(structureMode\)' -and $alone -match 'CollectDocumentContext\(doc, structureMode, null\)') "both collect the document context with the same routine, list markers included"
    Check ($pane -match 'PrewarmFallbackTermsFor\(' -and $alone -match 'PrewarmFallbackTermsFor\(' -and $pane -match 'TermsForPrompt\(' -and $alone -match 'TermsForPrompt\(') "both look the segment's terms up the same way"
    Check ($pane -match 'ResolveCustomPromptContent\(' -and $alone -match 'PromptContentFor\(' -and $alone -match 'BatchPromptChoice\.Stored\(') "both resolve the prompt with the same rule, the project's own prompt first"
    Check (([regex]::Matches($src, 'TranslationBankContext\.ForDocument\(')).Count -eq 1) "the view part calls TranslationBankContext.ForDocument in exactly one place"
    Check (([regex]::Matches($src, 'TranslationBankContext\.Build\(')).Count -eq 0) "and builds no translation bank block of its own anywhere else"
    Check (([regex]::Matches($src, 'TranslationBankContext\.BankForProject\(')).Count -eq 2) "the rule for a project's bank is called by the pane and by Alt+T without it, and written once"
}
finally {
    $paths.GetMethod('Reset', $Static).Invoke($null, @()) | Out-Null
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host
if ($script:fail -gt 0) { Write-Host "$($script:fail) of $($script:pass + $script:fail) check(s) FAILED"; exit 1 }
Write-Host "All $($script:pass) checks passed."
