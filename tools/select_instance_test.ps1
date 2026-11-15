# The MCP server's choice of Trados Studio (select_trados_instance,
# list_trados_instances, a call's own `instance`). Seen in 207: with "2024"
# selected and Studio 2024 closed, select_trados_instance "2026" was refused
# until the selection had been cleared with "".
#
# The MCP server is a .NET 8 program, which Windows PowerShell cannot load, so
# the checks are a small program of their own, tools\McpServerCheck: this
# script builds and runs it. It writes handshake files to a temporary runtime
# folder, with child processes of its own standing in for running Studios, and
# never reads or writes the real one (D:\Supervertaler\trados\runtime). No
# Studio is started or contacted.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\select_instance_test.ps1
#
# The MCP server is one 64-bit program whatever Studio it talks to: there is no
# 32-bit run of this test.
#
# What it cannot reach: a real Studio writing its handshake, and the MCP
# protocol around the tools.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tools\McpServerCheck\McpServerCheck.csproj'

$build = & dotnet build $project -c Debug -v q --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    $build | ForEach-Object { Write-Host $_ }
    Write-Host "RESULT: FAILED (tools\McpServerCheck does not build)"
    exit 1
}
$exe = Join-Path $root 'tools\McpServerCheck\bin\Debug\net8.0\McpServerCheck.exe'
if (-not (Test-Path -LiteralPath $exe)) { Write-Host "RESULT: FAILED (no $exe)"; exit 1 }

& $exe
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Host "RESULT: FAILED"; exit 1 }
Write-Host "RESULT: all passed"
exit 0
