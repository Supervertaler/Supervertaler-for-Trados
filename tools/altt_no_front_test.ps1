# Alt+T must not bring the Supervertaler Assistant pane to the front, nor save
# the document; a Batch Translate run still does both at its end.
#
# Both share OnBatchCompleted. The pull to the front (Activate) and the save
# need a running Studio, so this checks the source: in OnBatchCompleted every
# Activate() and the BridgeSaveDocument() call sit under isBatchRun, which is
# true only for a translator with a DocumentRef; the Batch Translate run sets
# DocumentRef, Alt+T's translator does not. (Michael, 16 Nov 2026.)
#
#   powershell -NoProfile -File tools\altt_no_front_test.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Get-Content -Raw -LiteralPath (Join-Path $root 'src\Supervertaler.Trados\AiAssistantViewPart.cs')
$pass = 0; $fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" } else { $script:fail++; Write-Host "  FAIL $what" }
}

# The handler's body: from its signature to the next method at the same indent.
$start = $src.IndexOf('private void OnBatchCompleted(object sender, BatchCompletedEventArgs e)')
Check ($start -ge 0) "OnBatchCompleted found"
$next = [regex]::Match($src.Substring($start + 10), '\r?\n        (private|public|internal|protected) ')
$body = $src.Substring($start, 10 + $next.Index)

Write-Host "1 What tells a run from Alt+T"
Check ($body -match 'bool isBatchRun = \(sender as BatchTranslator\)\?\.DocumentRef != null;') "isBatchRun is set from the translator's DocumentRef"

Write-Host "2 The pull to the front is for runs only"
$acts = [regex]::Matches($body, 'Activate\(\)')
Check ($acts.Count -eq 2) "two Activate() calls in the handler ($($acts.Count))"
$guard = $body.IndexOf('if (isBatchRun)')
Check ($guard -ge 0) "an 'if (isBatchRun)' block exists"
if ($guard -ge 0) {
    # The block's end: the matching closing brace.
    $open = $body.IndexOf('{', $guard); $depth = 0; $end = -1
    for ($i = $open; $i -lt $body.Length; $i++) {
        if ($body[$i] -eq '{') { $depth++ } elseif ($body[$i] -eq '}') { $depth--; if ($depth -eq 0) { $end = $i; break } }
    }
    $inside = $true
    foreach ($m in $acts) { if ($m.Index -lt $open -or $m.Index -gt $end) { $inside = $false } }
    Check $inside "both Activate() calls are inside that block"
}

Write-Host "3 The save is for runs only"
Check ($body -match 'if \(isBatchRun && e\.Translated > 0\)\s*\{\s*var saveSw = System\.Diagnostics\.Stopwatch\.StartNew\(\);\s*var saveResult = BridgeSaveDocument\(\);') "BridgeSaveDocument() is under 'isBatchRun && e.Translated > 0'"
Check ([regex]::Matches($body, 'BridgeSaveDocument\(\)').Count -eq 1) "and is called nowhere else in the handler"

Write-Host "4 Who sets DocumentRef"
$sets = [regex]::Matches($src, 'DocumentRef = ')
Check ($sets.Count -eq 1) "DocumentRef is set in one place ($($sets.Count))"
$run = [regex]::Match($src, '_batchTranslator = new BatchTranslator\s*\{[^}]*DocumentRef = runDocument')
Check $run.Success "that place is the Batch Translate run"
Check ($src -match 'instance\._batchTranslator = new BatchTranslator \{ LogFeature = PromptLogFeature\.Translate \};') "Alt+T creates its translator without one, logged as Translate active segment"

Write-Host "5 Alt+T is logged as itself, not as Batch Translate"
Check ($src -match 'var worker = new BatchTranslator \{ LogFeature = PromptLogFeature\.Translate \};') "Alt+T without the pane is logged the same way"
$bt = Get-Content -Raw -LiteralPath (Join-Path $root 'src\Supervertaler.Trados\Core\BatchTranslator.cs')
Check (-not ($bt -match 'PromptLogFeature\.BatchTranslate,')) "the translator names no feature itself; every log entry uses LogFeature"
Check ([regex]::Matches($bt, '[Ff]eature ?[:=] ?LogFeature,').Count -eq 5) "all five log entries use LogFeature ($([regex]::Matches($bt, '[Ff]eature ?[:=] ?LogFeature,').Count))"

Write-Host ""
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
