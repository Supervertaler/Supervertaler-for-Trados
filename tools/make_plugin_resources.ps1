# Builds the plugin resource bundle Trados reads for pane names and icons.
#
# Why this exists instead of PluginResources.resx: an SDK-style project cannot
# compile a .resx holding images without switching to the preserialized
# resource format, which requires System.Resources.Extensions.dll to be
# loadable at the moment the resource is READ. Studio reads it during plugin
# discovery, which can precede our AppInitializer's AssemblyResolve hook - so a
# missing assembly there would stop the plugin loading rather than merely lose
# an icon.
#
# Windows PowerShell is .NET Framework 4.8, so System.Resources.ResourceWriter
# here emits the classic format Studio has always read. Must be run with
# powershell.exe, NOT pwsh (PowerShell 7 is .NET 8 and writes the new format).
#
# The icons go in as System.Drawing.Icon, from .ico files, NOT as Bitmap from
# .png. Sdl.Desktop.Platform's AbstractViewPart.Icon is declared
# System.Drawing.Icon; a bundle holding Bitmaps loads fine outside Studio and
# then kills the editor with a NullReferenceException deep in Studio's own tab
# machinery. Learned the hard way on 2026-09-09.
param(
    [Parameter(Mandatory = $true)][string] $Out,
    [Parameter(Mandatory = $true)][string] $IconsDir,
    [string] $PluginName = 'Supervertaler for Trados'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Resource name -> file stem in $IconsDir. The names are what the [ViewPart]
# attributes and Supervertaler.Trados.plugin.xml refer to; changing one means
# changing all three.
$icons = [ordered]@{
    'TermLensIcon'    = 'termlens'
    'TermPickerIcon'  = 'termpicker'
    'SuperSearchIcon' = 'supersearch'
    'AssistantIcon'   = 'assistant'
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$held = @()
$writer = New-Object System.Resources.ResourceWriter($Out)
try {
    $writer.AddResource('Plugin_Name', $PluginName)
    foreach ($name in $icons.Keys) {
        $path = Join-Path $IconsDir ($icons[$name] + '.ico')
        if (-not (Test-Path $path)) { throw "Icon not found: $path" }
        # Load through a MemoryStream: Icon(path) keeps the file locked for the
        # life of the object, which breaks a rebuild.
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $stream = New-Object System.IO.MemoryStream(, $bytes)
        $ico = New-Object System.Drawing.Icon($stream)
        $held += $ico
        $writer.AddResource($name, $ico)
    }
    $writer.Generate()
}
finally {
    $writer.Close()
    foreach ($i in $held) { $i.Dispose() }
}

Write-Host ("Wrote {0} ({1} bytes): Plugin_Name + {2} icon(s)" -f $Out, (Get-Item $Out).Length, $icons.Count)
