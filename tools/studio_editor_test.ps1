# Core/StudioEditor.cs reaches two non-public parts of Studio by reflection
# (StudioPluginManager.Resolve<T> and ExtensionsFactory.Create<IViewPart>): a plugin may
# not reference Sdl.Desktop.Platform, or Studio refuses to load it. If Studio renamed
# either part, StudioEditor would fall back to the old behaviour (the first-document
# crash) with one log line and nothing else to show for it. This finds both, with the
# plugin's own lookup code, in the DLLs of the Studio the plugin was built for.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\studio_editor_test.ps1 -Dll <Supervertaler.Trados.dll>
#
# The Studio 2024 build is x86: run it with the 32-bit PowerShell,
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe
# Nothing is invoked in Studio's code, nothing is written. Not for the Studio 2022
# build, which keeps GetController.
param([Parameter(Mandatory = $true)][string]$Dll)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Dll)) { throw "no such DLL: $Dll" }

$fails = 0; $checks = 0
function Check($ok, $label) {
    $script:checks++
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# Which Studio was this build made for? Its reference to the integration API says.
$bytes = [IO.File]::ReadAllBytes($Dll)
$probe = [Reflection.Assembly]::ReflectionOnlyLoad($bytes)
$api = $probe.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'Sdl.Desktop.IntegrationApi' } | Select-Object -First 1
if (-not $api) { throw 'the DLL does not reference Sdl.Desktop.IntegrationApi' }
$major = $api.Version.Major
$studioDir = $null
foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    $d = Join-Path $pf "Trados\Trados Studio\Studio$major"
    if (Test-Path (Join-Path $d 'Sdl.Desktop.Platform.dll')) { $studioDir = $d; break }
}
if (-not $studioDir) { throw "Studio $major is not installed here" }
Write-Host "DLL: $Dll"
Write-Host "Studio: $studioDir"
if ($major -eq 17) { Write-Host 'Studio 2022 build: StudioEditor keeps GetController there; nothing to check.'; exit 0 }

Check (-not ($probe.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'Sdl.Desktop.Platform' })) `
    'the plugin does not reference Sdl.Desktop.Platform'

# Everything else from that Studio's folder, the way Studio itself would load it.
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $c = Join-Path $studioDir ((($e.Name -split ',')[0]) + '.dll')
    if (Test-Path $c) { return [Reflection.Assembly]::LoadFrom($c) }
    $c = Join-Path (Split-Path $Dll) ((($e.Name -split ',')[0]) + '.dll')
    if (Test-Path $c) { return [Reflection.Assembly]::LoadFrom($c) }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$se = $plugin.GetType('Supervertaler.Trados.Core.StudioEditor', $true)
$Static = [Reflection.BindingFlags]'NonPublic,Static'
$findResolve = $se.GetMethod('FindResolve', $Static)
$findCreate = $se.GetMethod('FindCreateViewPart', $Static)
Check ($findResolve -and $findCreate) 'StudioEditor has FindResolve and FindCreateViewPart'

# Before Studio's platform is loaded, the lookups must answer null, not throw.
$platformLoaded = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Sdl.Desktop.Platform' }
if (-not $platformLoaded) {
    Check ($null -eq $findResolve.Invoke($null, $null)) 'without Sdl.Desktop.Platform loaded: FindResolve is null'
    Check ($null -eq $findCreate.Invoke($null, $null)) 'without Sdl.Desktop.Platform loaded: FindCreateViewPart is null'
}

[void][Reflection.Assembly]::LoadFrom((Join-Path $studioDir 'Sdl.Desktop.Platform.dll'))

$resolve = $findResolve.Invoke($null, $null)
Check ($null -ne $resolve) 'StudioPluginManager.Resolve<T>() found'
if ($resolve) {
    Check ($resolve.DeclaringType.FullName -eq 'Sdl.Desktop.Platform.PluginManagement.StudioPluginManager') `
        "  declared by $($resolve.DeclaringType.FullName)"
    $editorType = [Reflection.Assembly]::LoadFrom((Join-Path $studioDir 'Sdl.TranslationStudioAutomation.IntegrationApi.dll')).
        GetType('Sdl.TranslationStudioAutomation.IntegrationApi.EditorController', $true)
    $closed = $resolve.MakeGenericMethod($editorType)
    Check ($closed.ReturnType -eq $editorType) '  closes over EditorController and returns it'
}

$create = $findCreate.Invoke($null, $null)
Check ($null -ne $create) 'ExtensionsFactory.Create<IViewPart>(IExtension, ...) found'
if ($create) {
    $p = $create.GetParameters()
    Check ($create.GetGenericArguments()[0].FullName -eq 'Sdl.Desktop.Platform.IViewPart') "  closed over $($create.GetGenericArguments()[0].FullName)"
    Check ($p[0].ParameterType.FullName -eq 'Sdl.Core.PluginFramework.IExtension') "  first parameter is IExtension"
    $rest = @($p | Select-Object -Skip 1)
    Check (@($rest | Where-Object { -not $_.HasDefaultValue }).Count -eq 0) "  the other $($rest.Count) parameter(s) have defaults"
    Check ($create.DeclaringType.Assembly.Location -like "$studioDir*") "  from Studio's own $($create.DeclaringType.Assembly.GetName().Name) $($create.DeclaringType.Assembly.GetName().Version)"
}

Write-Host ""
Write-Host "$checks checks, $fails failed"
if ($fails -gt 0) { exit 1 }
