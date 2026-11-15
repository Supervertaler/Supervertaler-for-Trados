# AutoPrompt: the question asked before a very long document is sent
# ("Large document - AutoPrompt") - checked against the built plugin DLL,
# outside Studio, with the real AutoPromptSizeCheck and the real price list.
#
# What it must do: say nothing below 100,000 estimated input tokens; at or above,
# give the segments, the words, the tokens, the model by its display name and a
# cost that agrees with the price list; give no dollar figure for a model that
# is not in the list or that runs on the translator's own computer; and take
# any number without failing. Also: recognise a provider's "too large" refusal
# and nothing else.
#
# What it cannot reach: the message box itself and the AutoPrompt handler in
# AiAssistantViewPart (its order, and that "No" leaves the chat untouched).
# Those need Trados Studio. No AI request is made.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\autoprompt_size_check_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\autoprompt_size_check_test.ps1
#
# Uses a data folder of its own under %TEMP% (SupervertalerPaths.Set), deleted
# at the end, so the price list is the one built into the plugin and not a
# pricing.json of the translator's. Reads and writes no settings file.
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
public static class AutoPromptSizeTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[AutoPromptSizeTestResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

# A type of Plugin Core, wherever this build keeps it.
function CoreType([string]$name) {
    foreach ($a in [AppDomain]::CurrentDomain.GetAssemblies()) { $t = $a.GetType($name, $false); if ($t) { return $t } }
    $t = $asm.GetType($name, $false)
    if ($t) { return $t }
    $coreDll = Join-Path (Split-Path -Parent $Dll) 'Supervertaler.Core.dll'
    if (Test-Path $coreDll) { return [System.Reflection.Assembly]::LoadFrom($coreDll).GetType($name, $false) }
    return $null
}

# Its own data folder, before anything of the plugin runs.
$temp = Join-Path $env:TEMP ("sv-autoprompt-size-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$paths = CoreType 'Supervertaler.Core.SupervertalerPaths'
if (-not $paths) { throw "SupervertalerPaths not found: the test will not run against the real data folder" }
$paths.GetMethod('Set').Invoke($null, @([string]$temp)) | Out-Null
Check ([string]::Equals([string]$paths.GetProperty('Root').GetValue($null, $null), $temp, [StringComparison]::OrdinalIgnoreCase)) "data folder is the temp folder"

try {
    $check = $asm.GetType('Supervertaler.Trados.Core.AutoPromptSizeCheck', $true)
    $mFor = $check.GetMethod('For'); $mMessage = $check.GetMethod('Message')
    $mRefusal = $check.GetMethod('IsSizeRefusal'); $mAsks = $check.GetMethod('Asks')
    $threshold = [int]$check.GetField('ThresholdTokens').GetValue($null)
    $reply = [int]$check.GetField('AssumedReplyTokens').GetValue($null)
    $estimator = CoreType 'Supervertaler.Core.TokenEstimator'
    $pricing = CoreType 'Supervertaler.Core.PricingTable'
    $models = CoreType 'Supervertaler.Core.LlmModels'
    if (-not $estimator -or -not $pricing -or -not $models) { throw "Plugin Core types not found" }

    # PowerShell wraps some values; reflection wants the value itself.
    function Raw($v) { if ($null -eq $v) { return $null } return $v.PSObject.BaseObject }
    function For2([int]$segments, [int]$words, $prompt, $provider, $model) {
        return $mFor.Invoke($null, [object[]]@($segments, $words, (Raw $prompt), (Raw $provider), (Raw $model)))
    }
    function Message2([int]$segments, [int]$words, [int]$tokens, $name, [bool]$local, $cost) {
        return $mMessage.Invoke($null, [object[]]@($segments, $words, $tokens, (Raw $name), $local, (Raw $cost)))
    }
    # A request of exactly this many estimated tokens (four characters each).
    function MakePrompt([int]$tokens) { return New-Object string('x', ($tokens * 4)) }
    # The price list's own rates, and the cost worked out from them here.
    function Rates([string]$model) {
        $a = [object[]]@($model, $null)
        if (-not [bool]$pricing.GetMethod('TryGet').Invoke($null, $a)) { return $null }
        return $a[1]
    }
    function Expected([string]$model, [int]$tokens) {
        $r = Rates $model
        return ([decimal]$tokens * [decimal]$r.Item1 / [decimal]1000000) + ([decimal]$reply * [decimal]$r.Item2 / [decimal]1000000)
    }
    function Dollars([decimal]$cost) { return 'about $' + $cost.ToString('0.00') }
    function Name([string]$model) { return [string]$models.GetMethod('FindModel').Invoke($null, @($model)).DisplayName }
    function N([long]$number) { return $number.ToString('N0') }

    Write-Host "1 Below the threshold nothing is asked"
    Check ($threshold -eq 100000) "the threshold is 100,000 estimated input tokens ($threshold)"
    Check ($null -eq (For2 4200 61000 (MakePrompt ($threshold - 1)) 'claude' 'claude-fable-5-2')) "one token below: no message, even on the dearest model"
    Check ($null -eq (For2 4200 61000 (New-Object string('x', ($threshold * 4 - 4))) 'claude' 'claude-opus-5-5')) "399,996 characters: no message"
    Check ($null -eq (For2 12 300 'A short document.' 'openai' 'gpt-6.1-sol')) "an ordinary document: no message"
    Check ($null -eq (Message2 4200 61000 ($threshold - 1) 'Claude Opus 5.5' $false ([decimal]1.2))) "from the numbers, one below: no message"
    Check (-not [bool]$mAsks.Invoke($null, @([int]($threshold - 1))) -and [bool]$mAsks.Invoke($null, @([int]$threshold))) "Asks: no at 99,999, yes at 100,000"

    Write-Host "2 At the threshold: segments, words, tokens, the model's name and a cost"
    $at = [string](For2 4200 61000 (MakePrompt $threshold) 'claude' 'claude-opus-5-5')
    $costAt = Expected 'claude-opus-5-5' $threshold
    Check ($at.Length -gt 0) "exactly 100,000 tokens: there is a message"
    Check ($at.Contains("$(N 4200) segments")) "it gives the segments ($(N 4200))"
    Check ($at.Contains("($(N 61000) words)")) "it gives the words ($(N 61000))"
    Check ($at.Contains("about $(N 100000) tokens")) "it gives the tokens (about $(N 100000))"
    Check ($at.Contains("with Claude Opus 5.5.") -and -not $at.Contains('claude-opus-5-5')) "it names the model as Claude Opus 5.5, not by its id"
    Check ($costAt -gt 0 -and $at.Contains("Estimated cost: $(Dollars $costAt) with")) "the cost is the price list's: $(Dollars $costAt)"
    Check ($at.Contains("a reply of about $(N $reply) tokens") -and $at.Contains('This is an estimate')) "it says the cost is an estimate, and what reply it counts"
    Check ($at.Contains('AutoPrompt reads all of it')) "it says AutoPrompt reads the whole document"
    Check ($at.EndsWith('Generate the prompt anyway?')) "it ends with the question"
    Check ([string]$check.GetField('Title').GetValue($null) -eq ("Large document " + [char]0x2013 + " AutoPrompt")) "the title is 'Large document - AutoPrompt' with the dash of the other two boxes"
    $three = [string](For2 4200 61000 (New-Object string('x', ($threshold * 4 - 3))) 'claude' 'claude-opus-5-5')
    Check ($three.Length -gt 0) "399,997 characters round up to 100,000 tokens: asked"

    Write-Host "3 Above it: the token figure is rounded, the cost follows the model"
    $tokens = 131417
    $cheapId = 'gpt-6-luna'; $dearId = 'claude-fable-5-2'
    $cheap = [string](For2 9000 120000 (MakePrompt $tokens) 'openai' $cheapId)
    $dear = [string](For2 9000 120000 (MakePrompt $tokens) 'claude' $dearId)
    $cheapCost = Expected $cheapId $tokens; $dearCost = Expected $dearId $tokens
    Check ($cheap.Contains("about $(N 131000) tokens") -and -not $cheap.Contains((N 131417))) "131,417 tokens are shown as about $(N 131000)"
    Check ($cheap.Contains("$(Dollars $cheapCost) with $(Name $cheapId).")) "$(Name $cheapId): $(Dollars $cheapCost)"
    Check ($dear.Contains("$(Dollars $dearCost) with $(Name $dearId).")) "$(Name $dearId): $(Dollars $dearCost)"
    Check ($dearCost -gt $cheapCost -and (Dollars $dearCost) -ne (Dollars $cheapCost) -and -not $cheap.Contains((Dollars $dearCost))) "the two costs differ, the dearer model the higher"
    $viaEstimator = [decimal]$estimator.GetMethod('EstimateCost').Invoke($null, [object[]]@($dearId, [int]$tokens, [int]$reply))
    Check ($viaEstimator -eq $dearCost) "the same figure TokenEstimator.EstimateCost gives ($viaEstimator)"
    $routed = [string](For2 9000 120000 (MakePrompt $tokens) 'openrouter' 'anthropic/claude-opus-5.5')
    Check ($routed.Contains("$(Dollars (Expected 'anthropic/claude-opus-5.5' $tokens)) with Claude Opus 5.5.")) "a model reached through OpenRouter is priced and named too"
    $tiny = [string](Message2 9000 120000 $tokens 'Acme Small' $false ([decimal]0.004))
    Check ($tiny.Contains('Estimated cost: less than $0.01 with Acme Small.')) "a cost under one cent is 'less than `$0.01', not `$0.00"

    Write-Host "4 A model that is not in the price list: no made-up figure"
    Check ($null -eq (Rates 'acme-model-1')) "acme-model-1 is not in the price list"
    $unknown = [string](For2 9000 120000 (MakePrompt $tokens) 'custom_openai' 'acme-model-1')
    Check ($unknown.Contains('The cost is not known: acme-model-1 is not in the price list.')) "it says the cost is not known, and for which model"
    Check (-not $unknown.Contains('$')) "there is no dollar figure in it"
    Check ($unknown.Contains("$(N 9000) segments") -and $unknown.Contains("about $(N 131000) tokens") -and $unknown.EndsWith('Generate the prompt anyway?')) "the size and the question are still there"
    $unknownCloud = [string](For2 9000 120000 (MakePrompt $tokens) 'mistral' 'mistral-large-latest')
    if ($null -eq (Rates 'mistral-large-latest')) {
        Check ($unknownCloud.Contains('The cost is not known: Mistral Large is not in the price list.') -and -not $unknownCloud.Contains('$')) "a listed model without a price is named by its display name, with no figure"
    } else {
        Check ($unknownCloud.Contains("$(Dollars (Expected 'mistral-large-latest' $tokens)) with Mistral Large.")) "Mistral Large is priced from the list"
    }

    Write-Host "5 A model on the translator's own computer"
    $local = [string](For2 9000 120000 (MakePrompt $tokens) 'ollama' 'translategemma:12b')
    Check ($local.Contains('TranslateGemma 12B runs on your own computer, so this costs nothing')) "it says the model runs on your own computer and costs nothing"
    Check ($local.Contains('may be slow, or more than the model can take in')) "and that the request may be slow or too much for it"
    Check (-not $local.Contains('$') -and -not $local.Contains('not known')) "no dollar figure, and not 'not known'"
    $localOther = [string](For2 9000 120000 (MakePrompt $tokens) 'ollama' 'acme-local:7b')
    Check ($localOther.Contains('acme-local:7b runs on your own computer') -and -not $localOther.Contains('$')) "the same for a local model that is in no list"
    $free = [string](Message2 9000 120000 $tokens 'Acme Free' $false ([decimal]0))
    Check ($free.Contains('Acme Free is free of charge, so this costs nothing.') -and -not $free.Contains('$')) "a model listed at a price of nothing is called free"

    Write-Host "6 Very large numbers"
    $hugeTokens = 10000000   # some 5,000,000 words
    $huge = [string](For2 400000 5000000 (MakePrompt $hugeTokens) 'claude' $dearId)
    Check ($huge.Contains("$(N 400000) segments ($(N 5000000) words)") -and $huge.Contains("about $(N 10000000) tokens")) "5,000,000 words, 10,000,000 tokens: the figures are whole"
    Check ($huge.Contains("$(Dollars (Expected $dearId $hugeTokens)) with")) "and the cost is the price list's: $(Dollars (Expected $dearId $hugeTokens))"
    $max = [int]::MaxValue
    $top = [string](Message2 $max $max $max 'Acme Large' $false ([decimal]::MaxValue))
    Check ($top.Contains("$(N $max) segments") -and $top.Contains("about $(N 2147484000) tokens")) "the largest numbers there are do not overflow"

    Write-Host "7 Zero, negative and missing values"
    $threw = $false
    try {
        $zero = Message2 0 0 0 $null $false $null
        $neg = Message2 -5 -100 -1 'Acme' $false ([decimal]-3)
        $min = Message2 ([int]::MinValue) ([int]::MinValue) ([int]::MinValue) '' $true $null
        $noPrompt = For2 0 0 $null $null $null
        $emptyPrompt = For2 -1 -1 '' '' ''
        $odd = [string](Message2 -5 -100 150000 $null $false $null)
        $oddFor = [string](For2 -5 -100 (MakePrompt $threshold) $null $null)
        $negCost = [string](Message2 10 10 150000 'Acme' $false ([decimal]-3))
    } catch { $threw = $true; Write-Host "  $_" }
    Check (-not $threw) "none of them throws"
    Check ($null -eq $zero -and $null -eq $neg -and $null -eq $min) "zero or negative tokens: no message"
    Check ($null -eq $noPrompt -and $null -eq $emptyPrompt) "no request text: no message"
    Check ($odd.Contains('This document has 0 segments (0 words)') -and $odd.Contains('this model is not in the price list')) "negative counts are shown as 0, a missing model name as 'this model'"
    Check ($oddFor.Contains('this model is not in the price list') -and -not $oddFor.Contains('$')) "no provider and no model: asked, with no figure"
    Check (-not $negCost.Contains('$-') -and -not $negCost.Contains('-$')) "a negative cost is never shown as one"

    Write-Host "8 A provider's refusal for size is recognised, and nothing else is"
    function Refusal($text) { return [bool]$mRefusal.Invoke($null, [object[]]@((Raw $text))) }
    Check (Refusal 'Claude API error 400: {"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 213548 tokens > 200000 maximum"}}') "Claude 400 'prompt is too long'"
    Check (Refusal 'Claude API error 413: {"type":"error","error":{"type":"request_too_large","message":"Request exceeds the maximum size"}}') "any 413"
    Check (Refusal "OpenAI API error 400: {""error"":{""message"":""This model's maximum context length is 1050000 tokens."",""code"":""context_length_exceeded""}}") "OpenAI 400 'context_length_exceeded'"
    Check (Refusal 'Gemini API error 400: {"error":{"code":400,"message":"The input token count (1200000) exceeds the maximum number of tokens allowed (1048576)."}}') "Gemini 400 'input token count'"
    Check (Refusal 'Acme API error 400: the request exceeds the Context Window of this model') "an OpenAI-compatible server's 'context window', whatever the case"
    Check (-not (Refusal 'Claude API error 400: {"error":{"message":"max_tokens: 65536 > 64000, which is the maximum allowed number of output tokens for this model"}}')) "a 400 about the OUTPUT limit is not one"
    Check (-not (Refusal 'OpenAI API error 400: {"error":{"message":"Unsupported value: temperature"}}')) "another 400 is not one"
    Check (-not (Refusal 'Claude API error 401: {"error":{"message":"invalid x-api-key: prompt is too long"}}')) "a 401 is not one, whatever its text"
    Check (-not (Refusal 'Claude API error 429: rate limit, context window')) "a 429 is not one"
    Check (-not (Refusal 'The request timed out. prompt is too long')) "a message that is not an API error is not one"
    Check (-not (Refusal $null) -and -not (Refusal '') -and -not (Refusal 'API error ') -and -not (Refusal 'API error 4')) "nothing, an empty text and a cut-off one do not throw and are not one"
    Check (([string]$check.GetField('SizeRefusalLead').GetValue($null)).StartsWith('The document is too large for this model')) "the sentence put before it starts 'The document is too large for this model'"
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
