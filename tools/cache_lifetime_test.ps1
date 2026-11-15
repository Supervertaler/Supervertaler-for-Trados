# The one-hour prompt cache (208): who asks for it, and what the usage log keeps.
# Checked against the built plugin DLL, outside Studio. No AI request is made and
# no data folder is written: everything here is in memory or in a temp folder.
#
#   1  BatchTranslator.KeepsCacheForAnHour: one segment yes; several, none, or a
#      SuperBench contender no
#   2  the source (read as text; the run needs a Claude server to watch it):
#      every request of a BatchTranslator run - first, re-ask, retry - passes the
#      run's one choice; SuperBench marks its translator a benchmark; Alt+T does
#      not; the chat asks for it exactly when it caches; Batch Proofread, the
#      memory bank question and QuickLauncher/AutoPrompt do not ask
#   3  the request the plugin's own LlmClient builds for one segment to Claude,
#      and for a batch, byte for byte
#   4  the usage log: a record from before 208 (no in_cache_write_1h) loads with
#      0 and keeps its cost; a new one keeps the hour; the CSV export has the
#      column; the cost of 23,000 tokens written for an hour is 2x
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\cache_lifetime_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\cache_lifetime_test.ps1
#
# What it cannot reach: Anthropic itself - that it accepts "ttl":"1h" and reports
# ephemeral_1h_input_tokens. The core tests (PromptCacheLifetimeTests) check the
# request shape and the parsing of that report.
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

Add-Type -AssemblyName System.Runtime.Serialization
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class CacheLifetimeTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[CacheLifetimeTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}
$any = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$temp = Join-Path $env:TEMP ("sv-cache-lifetime-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    # -- 1 -----------------------------------------------------------------
    Write-Host "1 Who asks for the hour"
    $bt = $asm.GetType('Supervertaler.Trados.Core.BatchTranslator', $true)
    $keeps = $bt.GetMethod('KeepsCacheForAnHour', $any)
    function Keeps([int]$n, [bool]$bench) { [bool]$keeps.Invoke($null, [object[]]@($n, $bench)) }
    Check (Keeps 1 $false) "one segment (Translate active segment): an hour"
    Check (-not (Keeps 2 $false)) "two segments (a batch): five minutes"
    Check (-not (Keeps 500 $false)) "500 segments: five minutes"
    Check (-not (Keeps 0 $false)) "no segments: five minutes"
    Check (-not (Keeps 1 $true)) "a SuperBench contender with one segment: five minutes"
    Check (-not [bool]$bt.GetProperty('IsBenchmark').GetValue([Activator]::CreateInstance($bt), $null)) "a new BatchTranslator is not a benchmark"

    # -- 2 -----------------------------------------------------------------
    Write-Host "2 The source"
    $src = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\Core\BatchTranslator.cs'))
    $calls = [regex]::Matches($src, 'SendPromptAsync\((?:[^;])*;')
    Check ($calls.Count -eq 3) "BatchTranslator sends from three places: first request, re-ask, retry ($($calls.Count))"
    foreach ($c in $calls) {
        Check ($c.Value -match 'enablePromptCaching: true,\s*longCache: longCache\)') "each passes the run's one choice: $(($c.Value -replace '\s+', ' ').Substring(0, [Math]::Min(70, ($c.Value -replace '\s+', ' ').Length)))..."
    }
    Check (([regex]::Matches($src, 'var longCache = KeepsCacheForAnHour\(segments\.Count, IsBenchmark\);')).Count -eq 1) "the choice is made once per run, from its segment count"
    $first = $src.IndexOf('var longCache ='); $firstCall = $src.IndexOf('SendPromptAsync(')
    Check ($first -ge 0 -and $first -lt $firstCall) "and before any request"

    $bench = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\Core\SuperBenchRunner.cs'))
    Check ($bench -match 'new BatchTranslator \{ IsBenchmark = true \}') "SuperBench's translator is a benchmark"
    $view = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\AiAssistantViewPart.cs'))
    Check (-not ($view -match 'IsBenchmark')) "Alt+T and Batch Translate are not"
    Check ($view -match 'var cacheChat = !isStandalone;') "the chat caches except for QuickLauncher and AutoPrompt"
    $chatCalls = [regex]::Matches($view, '(SendChatWithToolsAsync|SendChatAsync)\((?:[^;])*;')
    Check ($chatCalls.Count -eq 2) "the chat sends from two places ($($chatCalls.Count))"
    foreach ($c in $chatCalls) {
        Check ($c.Value -match 'enablePromptCaching: cacheChat, stableSystemPromptLength: stableSystemLength,\s*longCache: cacheChat\)') "$($c.Groups[1].Value): the hour exactly when it caches"
    }
    foreach ($f in @('Core\BatchProofreader.cs', 'Core\TranslationBankContext.cs')) {
        $t = [IO.File]::ReadAllText((Join-Path $root "src\Supervertaler.Trados\$f"))
        Check (-not ($t -match 'longCache')) "$f does not ask for the hour"
    }
    $longCacheUsers = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter *.cs | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'longCache:' } | ForEach-Object { $_.Name }
    Check ((($longCacheUsers | Sort-Object) -join ',') -eq 'AiAssistantViewPart.cs,BatchTranslator.cs') "only BatchTranslator and the chat ask for it ($($longCacheUsers -join ', '))"

    # -- 3 -----------------------------------------------------------------
    Write-Host "3 The request, as this build's LlmClient writes it"
    $llm = $asm.GetType('Supervertaler.Core.LlmClient', $true)
    $client = [Activator]::CreateInstance($llm, [object[]]@('claude', 'claude-sonnet-5-5', 'k', $null, 4096, 0))
    $build = $llm.GetMethod('BuildClaudePromptBody', $any)
    $one = [string]$build.Invoke($client, [object[]]@('Het huis.', 'SYSTEM', 4096, $true, (Keeps 1 $false)))
    $many = [string]$build.Invoke($client, [object[]]@('Het huis.', 'SYSTEM', 4096, $true, (Keeps 20 $false)))
    Check ($one -eq '{"model":"claude-sonnet-5-5","max_tokens":4096,"system":[{"type":"text","text":"SYSTEM","cache_control":{"type":"ephemeral","ttl":"1h"}}],"messages":[{"role":"user","content":"Het huis."}],"stream":true}') "one segment: $one"
    Check ($many -eq '{"model":"claude-sonnet-5-5","max_tokens":4096,"system":[{"type":"text","text":"SYSTEM","cache_control":{"type":"ephemeral"}}],"messages":[{"role":"user","content":"Het huis."}],"stream":true}') "a batch: $many"

    # -- 4 -----------------------------------------------------------------
    Write-Host "4 The usage log"
    $recType = $asm.GetType('Supervertaler.Trados.Core.UsageRecord', $true)
    $ser = New-Object System.Runtime.Serialization.Json.DataContractJsonSerializer($recType)
    function FromJson([string]$json) { $ms = New-Object IO.MemoryStream (, [Text.Encoding]::UTF8.GetBytes($json)); try { $ser.ReadObject($ms) } finally { $ms.Dispose() } }
    function ToJson($r) { $ms = New-Object IO.MemoryStream; try { $ser.WriteObject($ms, $r); [Text.Encoding]::UTF8.GetString($ms.ToArray()) } finally { $ms.Dispose() } }

    $oldLine = '{"id":"a","ts":"2026-11-01T10:00:00Z","product":"trados","app_version":"18.20.207.0","task":"BatchTranslate","provider":"claude","model":"claude-sonnet-5-5","project":null,"project_key":null,"file":null,"client":null,"src_lang":"nl","tgt_lang":"en","in_regular":12,"in_cache_read":0,"in_cache_write":23000,"out":40,"source":"actual","cost_usd":0.057924,"cost_known":true,"duration_s":2.5,"ok":true,"error":null}'
    $old = FromJson $oldLine
    Check ($old.InputCacheWrite1h -eq 0) "a record from before 208 loads, with no hour"
    Check ($old.CostUsd -eq [decimal]0.057924 -and $old.InputCacheWrite -eq 23000) "and its cost and write count as they were ($($old.CostUsd))"
    $sessionCost = $asm.GetType('Supervertaler.Trados.Core.SessionCost', $true)
    $recordCost = $sessionCost.GetMethod('RecordCost', $any)
    $args1 = [object[]]@($old, $false)
    Check ([decimal]$recordCost.Invoke($null, $args1) -eq [decimal]0.057924) "the Reports tab counts it at its logged cost"

    $unknownLine = $oldLine.Replace('claude-sonnet-5-5', 'vendor/unlisted').Replace('0.057924', '0')
    $unk = FromJson $unknownLine
    $te = $asm.GetType('Supervertaler.Core.TokenEstimator', $true)
    $ceil = $te.GetMethod('CostCeiling')
    $expected = [decimal]$ceil.Invoke($null, [object[]]@('claude', 'vendor/unlisted', 12, 0, 23000, 40, 0))
    $args2 = [object[]]@($unk, $false)
    Check ([decimal]$recordCost.Invoke($null, $args2) -eq $expected -and $args2[1]) "an old record of an unlisted model: the same 'up to' as before ($expected)"

    $new = FromJson $oldLine
    $new.InputCacheWrite1h = 23000
    $json = ToJson $new
    Check ($json -match '"in_cache_write_1h":23000') "a new record keeps the hour"
    Check (-not ((ToJson $old) -match 'in_cache_write_1h')) "and a record without one writes no field (old readers see what they always saw)"
    Check ((FromJson $json).InputCacheWrite1h -eq 23000) "and reads it back"

    $export = $asm.GetType('Supervertaler.Trados.Core.UsageExport', $true)
    $listType = [System.Collections.Generic.List``1].MakeGenericType($recType)
    $list = [Activator]::CreateInstance($listType); $list.Add($old); $list.Add($new)
    $csv = [string](Join-Path $temp 'usage.csv')
    $export.GetMethod('WriteCsv').Invoke($null, [object[]]@($csv, $list))
    $lines = [IO.File]::ReadAllLines($csv)
    $head = $lines[0].Split(','); $i1h = [Array]::IndexOf($head, 'input_cache_write_1h'); $iTot = [Array]::IndexOf($head, 'input_total')
    Check ($i1h -eq [Array]::IndexOf($head, 'input_cache_write') + 1) "the CSV has input_cache_write_1h after input_cache_write"
    Check ($lines[1].Split(',')[$i1h] -eq '0' -and $lines[2].Split(',')[$i1h] -eq '23000') "0 for the old record, 23000 for the new"
    Check ($lines[2].Split(',')[$iTot] -eq '23012') "input_total is not counted twice (23012)"
    $xlsx = [string](Join-Path $temp 'usage.xlsx')
    $export.GetMethod('WriteXlsx').Invoke($null, [object[]]@($xlsx, $list))
    Check ((Get-Item -LiteralPath $xlsx).Length -gt 0) "the XLSX export still writes"

    $cost = $te.GetMethod('ComputeActualCost')
    $c5 = [decimal]$cost.Invoke($null, [object[]]@('claude-sonnet-5-5', 0, 0, 23000, 0, 0))
    $c1 = [decimal]$cost.Invoke($null, [object[]]@('claude-sonnet-5-5', 0, 0, 23000, 0, 23000))
    $cr = [decimal]$cost.Invoke($null, [object[]]@('claude-sonnet-5-5', 0, 23000, 0, 0, 0))
    Check ($c5 -eq [decimal]0.0575 -and $c1 -eq [decimal]0.092 -and $cr -eq [decimal]0.0023) "Sonnet 5.5, 23,000 tokens: write 5m $c5, 1h $c1, read $cr"
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
