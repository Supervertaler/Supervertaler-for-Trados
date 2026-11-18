# A model per kind of task (18.20.209 / 19.20.209; docs/design/model-per-task.md).
# Checked against the built plugin DLL, outside Studio:
#
#   A  the resolver (AiSettings.GetModelFor / ForTask): nothing chosen means the
#      Translation model for every task; a choice is kept per provider; a model
#      the provider no longer lists falls back to Translation; a local server
#      and a custom profile have no models per task; a settings file from before
#      209 reads as "same as Translation"; a copy made for a task leaves the
#      real settings as they were.
#   B  the AI Settings tab: three lists in the order Light tasks, Translation,
#      AutoPrompt; the first entry of the outer two names the Translation model
#      and follows it; a choice survives a provider switch and back; saving
#      writes it; a provider without task models greys the two lists out.
#   C  every feature names its kind of task in the source (read, not run), so a
#      new call to the AI cannot quietly land on the wrong model.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\model_per_task_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Release build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\model_per_task_test.ps1
#
# Uses a data folder of its own under %TEMP%, deleted at the end. Makes no AI
# request and reads no real settings or key file.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    # Release builds only: a Debug build left behind is usually stale.
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Release') } else { @('Studio18\Release', 'Studio17\Release') }
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

$protected = @(
    'D:\Supervertaler\settings\api-keys.json',
    (Join-Path $env:APPDATA 'Supervertaler\api-keys.locked.json'),
    (Join-Path $env:APPDATA 'Supervertaler\config.json')
)
function Snapshot { $protected | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { $i = Get-Item -LiteralPath $_; "$($i.FullName)|$($i.Length)|$($i.LastWriteTimeUtc.Ticks)" } }
$before = @(Snapshot)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class ModelPerTaskResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[ModelPerTaskResolver]::Install(@((Split-Path -Parent $Dll), $studio))
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

$temp = Join-Path $env:TEMP ("sv-model-per-task-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null
$paths = $asm.GetType('Supervertaler.Core.SupervertalerPaths', $false)
if (-not $paths) { throw "SupervertalerPaths not found: the test will not run against the real data folder" }
$paths.GetMethod('Set').Invoke($null, @([string]$temp)) | Out-Null
$rlf = $asm.GetType("Supervertaler.Core.ApiKeyStore").GetMethod("RedirectLockedFile", [Reflection.BindingFlags]"Static,NonPublic")
if ($rlf) { [void]$rlf.Invoke($null, [object[]]@([string](Join-Path $temp "api-keys.locked.json"))) }
Check ([string]::Equals([string]$paths.GetProperty('Root').GetValue($null, $null), $temp, [StringComparison]::OrdinalIgnoreCase)) "data folder is the temp folder"

try {
    $settingsType = $asm.GetType('Supervertaler.Trados.Settings.AiSettings', $true)
    $taskType = $asm.GetType('Supervertaler.Trados.Settings.AiTask', $true)
    $Translation = [Enum]::Parse($taskType, 'Translation'); $Light = [Enum]::Parse($taskType, 'Light'); $Prompt = [Enum]::Parse($taskType, 'PromptWriting')
    function NewAi { $a = [Activator]::CreateInstance($settingsType); $a.SelectedProvider = 'claude'; $a.ClaudeModel = 'claude-sonnet-5-5'; $a }
    function ToJson($a) {
        $ser = New-Object System.Runtime.Serialization.Json.DataContractJsonSerializer($settingsType)
        $ms = New-Object System.IO.MemoryStream; $ser.WriteObject($ms, $a); [Text.Encoding]::UTF8.GetString($ms.ToArray())
    }
    function FromJson([string]$json) {
        $ser = New-Object System.Runtime.Serialization.Json.DataContractJsonSerializer($settingsType)
        $ms = New-Object System.IO.MemoryStream(, [Text.Encoding]::UTF8.GetBytes($json)); $ser.ReadObject($ms)
    }

    Write-Host "A  the resolver"
    $ai = NewAi
    Check (($ai.GetModelFor($Translation) -eq 'claude-sonnet-5-5') -and ($ai.GetModelFor($Light) -eq 'claude-sonnet-5-5') -and ($ai.GetModelFor($Prompt) -eq 'claude-sonnet-5-5')) "A nothing chosen: every task uses the Translation model"
    Check ([object]::ReferenceEquals($ai.ForTask($Light), $ai)) "A nothing chosen: the settings are handed on as they are, no copy"

    $ai.SetTaskModel('claude', $Light, 'claude-opus-5-5')
    $ai.SetTaskModel('claude', $Prompt, 'claude-fable-5-2')
    Check (($ai.GetModelFor($Light) -eq 'claude-opus-5-5') -and ($ai.GetModelFor($Prompt) -eq 'claude-fable-5-2') -and ($ai.GetModelFor($Translation) -eq 'claude-sonnet-5-5')) "A a model chosen per task: each task gets its own, Translation is untouched"
    Check ($ai.GetSelectedModel() -eq 'claude-sonnet-5-5') "A GetSelectedModel still answers with the Translation model"

    $ai.ClaudeModel = 'claude-opus-5-5'
    Check (($ai.GetModelFor($Translation) -eq 'claude-opus-5-5') -and ($ai.GetModelFor($Prompt) -eq 'claude-fable-5-2')) "A changing Translation (the quick pickers do) leaves a chosen task model alone"
    $ai.SetTaskModel('claude', $Prompt, '')
    Check ($ai.GetModelFor($Prompt) -eq 'claude-opus-5-5') "A back on 'same as Translation': the task follows Translation again"
    $ai.ClaudeModel = 'claude-sonnet-5-5'

    $ai.SelectedProvider = 'openai'
    Check ($ai.GetModelFor($Light) -eq $ai.OpenAiModel) "A another provider has its own choices: none made, so its Translation model"
    $ai.SelectedProvider = 'claude'
    Check ($ai.GetModelFor($Light) -eq 'claude-opus-5-5') "A back on the first provider: its choice is still there"

    $ai.SetTaskModel('claude', $Light, 'claude-retired-model-1')
    Check ($ai.GetModelFor($Light) -eq 'claude-sonnet-5-5') "A a model the provider no longer lists: the task uses Translation, never a failed request"
    Check ($ai.GetTaskModelChoice('claude', $Light) -eq 'claude-retired-model-1') "A the stale choice is not thrown away by reading it"
    $fetched = [Activator]::CreateInstance($asm.GetType('Supervertaler.Trados.Settings.FetchedModelEntry', $true))
    $fetched.Provider = 'claude'; $fetched.Id = 'claude-retired-model-1'
    $ai.FetchedModels.Add($fetched)
    Check ($ai.GetModelFor($Light) -eq 'claude-retired-model-1') "A a model only in the list fetched from the provider counts as listed"

    foreach ($p in 'ollama', 'custom_openai') {
        $x = NewAi; $x.SelectedProvider = $p
        $x.SetTaskModel($p, $Light, 'anything')
        Check ($x.GetModelFor($Light) -eq $x.GetSelectedModel()) "A ${p}: one endpoint, one model - a task never gets another"
    }
    $x = NewAi; $x.TaskModels = $null
    Check (($x.GetModelFor($Light) -eq 'claude-sonnet-5-5') -and ($x.GetTaskModelChoice('claude', $Light) -eq '')) "A no list at all (a settings object from an old file): reads as 'same as Translation'"
    $x.SetTaskModel('claude', $Light, 'claude-opus-5-5')
    Check ($x.GetModelFor($Light) -eq 'claude-opus-5-5') "A and a choice can still be stored in it"
    $x.SetTaskModel('claude', $Translation, 'claude-opus-5-5'); $x.SetTaskModel('', $Light, 'claude-opus-5-5')
    Check ($x.TaskModels.Count -eq 1) "A Translation is not a task model, and a blank provider stores nothing"

    $old = FromJson '{"selectedProvider":"claude","claudeModel":"claude-opus-5-5"}'
    Check (($old.GetModelFor($Light) -eq 'claude-opus-5-5') -and ($old.GetModelFor($Prompt) -eq 'claude-opus-5-5')) "A a settings file written before 209: every task on the model it had"
    $ai = NewAi; $ai.SetTaskModel('claude', $Light, 'claude-opus-5-5')
    $json = ToJson $ai
    $back = FromJson $json
    Check (($json -match '"taskModels"') -and ($back.GetModelFor($Light) -eq 'claude-opus-5-5')) "A a choice is saved and read back"
    Check (-not ((ToJson (NewAi)) -match '"taskModels":\[\{')) "A with nothing chosen, nothing about tasks is written"

    $ai.ApiKeys.Claude = 'test-key-not-real'
    $copy = $ai.ForTask($Light)
    Check ((-not [object]::ReferenceEquals($copy, $ai)) -and ($copy.GetSelectedModel() -eq 'claude-opus-5-5') -and ($copy.SelectedProvider -eq 'claude')) "A settings for a Light task: a copy on the task's model, same provider"
    Check (($ai.ClaudeModel -eq 'claude-sonnet-5-5') -and ($ai.GetSelectedModel() -eq 'claude-sonnet-5-5')) "A the real settings still say the Translation model"
    Check (($copy.ApiKeys.Claude -eq 'test-key-not-real') -and ($copy.BatchSize -eq $ai.BatchSize)) "A the copy carries the rest of the settings"
    Check ([object]::ReferenceEquals($ai.ForTask($Translation), $ai)) "A settings for Translation: the settings themselves"

    Write-Host "B  the AI Settings tab"
    $panelType = $asm.GetType('Supervertaler.Trados.Controls.AiSettingsPanel', $true)
    $private = [System.Reflection.BindingFlags]'Instance,NonPublic'
    $form = New-Object System.Windows.Forms.Form
    $form.StartPosition = 'Manual'; $form.Location = New-Object System.Drawing.Point(-32000, -32000); $form.Size = New-Object System.Drawing.Size(1000, 1400)
    $panel = [Activator]::CreateInstance($panelType); $panel.Dock = 'Fill'
    $form.Controls.Add($panel); $form.Show(); [System.Windows.Forms.Application]::DoEvents()
    function F([string]$n) { $panelType.GetField($n, $private).GetValue($panel) }
    $cmbProvider = F '_cmbProvider'; $cmbLight = F '_cmbLightModel'; $cmbModel = F '_cmbModel'; $cmbPrompt = F '_cmbPromptModel'
    function SelectProvider([string]$key) {
        for ($i = 0; $i -lt $cmbProvider.Items.Count; $i++) { if ($cmbProvider.Items[$i].Key -eq $key) { $cmbProvider.SelectedIndex = $i; break } }
        [System.Windows.Forms.Application]::DoEvents()
    }
    function SelectId($cmb, [string]$id) { for ($i = 0; $i -lt $cmb.Items.Count; $i++) { if ($cmb.Items[$i].Id -eq $id) { $cmb.SelectedIndex = $i; return $true } }; return $false }

    # The labels, top to bottom, as the tab lays them out.
    $labels = New-Object System.Collections.Generic.List[object]
    function Walk($c) { foreach ($k in $c.Controls) { if ($k -is [System.Windows.Forms.Label]) { $labels.Add($k) }; Walk $k } }
    Walk $panel
    function LabelTop([string]$text) { $l = $labels | Where-Object { $_.Text -eq $text } | Select-Object -First 1; if ($l) { $l.PointToScreen([System.Drawing.Point]::Empty).Y } else { $null } }
    $yProvider = LabelTop 'Provider:'; $yLight = LabelTop 'Light tasks:'; $yTrans = LabelTop 'Translation:'; $yPrompt = LabelTop 'AutoPrompt:'
    Check (($null -ne $yProvider) -and ($null -ne $yLight) -and ($null -ne $yTrans) -and ($null -ne $yPrompt) -and ($yProvider -lt $yLight) -and ($yLight -lt $yTrans) -and ($yTrans -lt $yPrompt)) "B the order on the tab: Provider, Light tasks, Translation, AutoPrompt ($yProvider, $yLight, $yTrans, $yPrompt)"
    Check (-not ($labels | Where-Object { $_.Text -eq 'Model:' -and $_.Visible })) "B no list is called 'Model:' any more"

    $ai = NewAi
    $panel.PopulateFromSettings($ai); [System.Windows.Forms.Application]::DoEvents()
    Check (($cmbLight.SelectedIndex -eq 0) -and ($cmbPrompt.SelectedIndex -eq 0)) "B nothing chosen: both lists stand on their first entry"
    Check (($cmbLight.Items[0].ToString() -eq 'Claude Sonnet 5.5 (same as Translation)') -and ($cmbPrompt.Items[0].ToString() -eq 'Claude Sonnet 5.5 (same as Translation)')) "B that entry names the Translation model: '$($cmbLight.Items[0])'"
    Check (($cmbLight.Items.Count -eq $cmbModel.Items.Count + 1) -and $cmbLight.Enabled -and $cmbPrompt.Enabled) "B below it, the provider's models ($($cmbLight.Items.Count - 1))"

    [void](SelectId $cmbModel 'claude-opus-5-5'); [System.Windows.Forms.Application]::DoEvents()
    Check (($cmbLight.Items[0].ToString() -eq 'Claude Opus 5.5 (same as Translation)') -and ($cmbLight.SelectedIndex -eq 0)) "B Translation changed: the first entry follows it, the selection stays"
    $saved = NewAi; $panel.ApplyToSettings($saved)
    Check (($saved.ClaudeModel -eq 'claude-opus-5-5') -and ($saved.GetModelFor($Light) -eq 'claude-opus-5-5') -and (($saved.TaskModels | Measure-Object).Count -eq 0)) "B saved with nothing chosen: no task model stored"

    Check (SelectId $cmbLight 'claude-sonnet-5-5') "B a model can be picked for Light tasks"
    Check (SelectId $cmbPrompt 'claude-fable-5-2') "B and one for AutoPrompt"
    [void](SelectId $cmbModel 'claude-sonnet-5-5'); [System.Windows.Forms.Application]::DoEvents()
    Check (($cmbLight.SelectedItem.Id -eq 'claude-sonnet-5-5') -and ($cmbPrompt.SelectedItem.Id -eq 'claude-fable-5-2')) "B a picked model stays picked when Translation changes"
    SelectProvider 'openai'
    Check (($cmbLight.SelectedIndex -eq 0) -and ($cmbPrompt.SelectedIndex -eq 0) -and ($cmbLight.Items[0].ToString() -like '* (same as Translation)')) "B another provider: its own lists, nothing chosen there"
    SelectProvider 'ollama'
    Check ((-not $cmbLight.Enabled) -and (-not $cmbPrompt.Enabled) -and ($cmbLight.Items.Count -eq 1)) "B a local server: the two lists are greyed out, on 'same as Translation'"
    SelectProvider 'claude'
    Check (($cmbLight.SelectedItem.Id -eq 'claude-sonnet-5-5') -and ($cmbPrompt.SelectedItem.Id -eq 'claude-fable-5-2') -and $cmbLight.Enabled) "B back on the first provider: both choices are still there"
    [void](SelectId $cmbModel 'claude-opus-5-5')
    $saved = NewAi; $panel.ApplyToSettings($saved)
    Check (($saved.GetModelFor($Translation) -eq 'claude-opus-5-5') -and ($saved.GetModelFor($Light) -eq 'claude-sonnet-5-5') -and ($saved.GetModelFor($Prompt) -eq 'claude-fable-5-2')) "B saved: Translation, Light tasks and AutoPrompt each on its model"

    $panel.PopulateFromSettings($saved); [System.Windows.Forms.Application]::DoEvents()
    Check (($cmbLight.SelectedItem.Id -eq 'claude-sonnet-5-5') -and ($cmbPrompt.SelectedItem.Id -eq 'claude-fable-5-2') -and ($cmbModel.SelectedItem.Id -eq 'claude-opus-5-5')) "B opened again: the three lists show what was saved"
    $cmbLight.SelectedIndex = 0
    $saved2 = NewAi; $saved2.SetTaskModel('claude', $Light, 'claude-sonnet-5-5'); $panel.ApplyToSettings($saved2)
    Check (($saved2.GetTaskModelChoice('claude', $Light) -eq '') -and ($saved2.GetModelFor($Light) -eq 'claude-opus-5-5')) "B put back on the first entry and saved: the stored choice is removed"

    $stale = NewAi; $stale.SetTaskModel('claude', $Light, 'claude-retired-model-1')
    $panel.PopulateFromSettings($stale); [System.Windows.Forms.Application]::DoEvents()
    Check ($cmbLight.SelectedIndex -eq 0) "B a stored model the provider no longer lists: the list shows 'same as Translation'"

    $form.Close(); $form.Dispose()

    Write-Host "C  every feature names its kind of task (source)"
    $src = Join-Path $root 'src\Supervertaler.Trados'
    if (Test-Path $src) {
        $view = [IO.File]::ReadAllText((Join-Path $src 'AiAssistantViewPart.cs'))
        $ff = [IO.File]::ReadAllText((Join-Path $src 'AiAssistantViewPart.FuzzyFixer.cs'))
        $term = [IO.File]::ReadAllText((Join-Path $src 'AddTermWithAbbreviationAction.cs'))
        $bank = [IO.File]::ReadAllText((Join-Path $src 'Core\TranslationBankContext.cs'))
        Check ($ff.Contains('Ai = ai.ForTask(AiTask.Light)')) "C FuzzyFixer: Light"
        Check ($term.Contains('aiSettings.GetModelFor(AiTask.Light)')) "C Add term with abbreviation: Light"
        Check ($bank.Contains('ai.GetModelFor(AiTask.Light)') -and $view.Contains('CreateLlmClient(out _, AiTask.Light)')) "C choosing articles from the bank: Light"
        Check ($view.Contains('PromptLogFeature.PromptGeneration ? AiTask.PromptWriting : AiTask.Translation')) "C the chat path: AutoPrompt's own request is PromptWriting, everything else Translation"
        Check ($view.Contains('string model = aiSettings.GetModelFor(AiTask.PromptWriting);') -and $view.Contains('new LlmClient(provider, lightModel, apiKey, baseUrl)')) "C AutoPrompt: its cost question on the AutoPrompt model, its domain question on the Light one"
        Check (([regex]::Matches($view, 'aiSettings\.GetModelFor\(AiTask\.Light\)')).Count -eq 3) "C AutoPrompt's two helpers and AutoTagger: Light (3 places)"
        $left = ([regex]::Matches($view, '\.GetSelectedModel\(\)')).Count
        Check ($left -eq 7) "C the view part still reads the Translation model in 7 places, each known (Batch Translate, Alt+T twice, Proofread, the two displays, the unused agent): $left"
    } else { Write-Host "  (source not beside this script: section C skipped)" }
}
finally {
    try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

$after = @(Snapshot)
Check (-not (Compare-Object $before $after)) "the real settings and API-key files are as they were"
Write-Host ""
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
