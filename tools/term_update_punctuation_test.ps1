# Editing a term keeps its trailing punctuation (18.20.206 / 19.20.206).
#
# TermbaseReader.UpdateTerm - behind MCP update_term, the Term Entry Editor's
# Save and a Termbase Editor cell edit - stripped trailing . , ; : ! ? from both
# terms on every update, so "Rev." became "Rev" and could not be stored by
# editing at all. The strip belongs to the ADD paths only (a term captured from
# a selection, "circumference." with the sentence's full stop); this checks that
# adding still strips and editing no longer does.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\term_update_punctuation_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's Studio19 Debug build)
#
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\term_update_punctuation_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Works on a fresh database in %TEMP%, deleted afterwards. Never opens the real
# supervertaler.db or the real settings.
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

Add-Type -TypeDefinition @"
using System; using System.IO; using System.Reflection;
public static class TermPunctTestResolver {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
}
"@
[TermPunctTestResolver]::Install([string[]]@((Split-Path $Dll), $studio))

$plugin = [Reflection.Assembly]::LoadFrom($Dll)
$batteries = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $Dll) "SQLitePCLRaw.batteries_v2.dll"))
[void]$batteries.GetType("SQLitePCL.Batteries_V2").GetMethod("Init").Invoke($null, @())
$R = $plugin.GetType("Supervertaler.Trados.Core.TermbaseReader")

$work = Join-Path $env:TEMP ("sv-term-punct-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$db = Join-Path $work "test.db"

$script:fails = 0; $script:passes = 0
function Check([bool]$ok, [string]$what, [string]$got) {
    if ($ok) { Write-Host "PASS  $what"; $script:passes++ }
    else { Write-Host "FAIL  $what  (got: $got)" -ForegroundColor Red; $script:fails++ }
}
function Get($id) { $R::GetTermById($db, [long]$id) }

try {
    $R::CreateDatabase($db)
    $tb = $R::CreateTermbase($db, "Acme test", "nl-NL", "en-GB")

    # Adding still strips: a selection's sentence full stop is not part of the term.
    $id = $R::InsertTerm($db, [long]$tb, "omtrek.", "circumference.", "nl-NL", "en-GB")
    $e = Get $id
    Check ($e.SourceTerm -eq "omtrek" -and $e.TargetTerm -eq "circumference") "add: trailing full stop still stripped" "$($e.SourceTerm) / $($e.TargetTerm)"

    # 1. Edit to an abbreviation: the full stop is the spelling.
    $id = $R::InsertTerm($db, [long]$tb, "revisie", "revision", "nl-NL", "en-GB")
    [void]$R::UpdateTerm($db, [long]$id, "Rev.", "Rev.")
    $e = Get $id
    Check ($e.SourceTerm -eq "Rev." -and $e.TargetTerm -eq "Rev.") "update: 'Rev.' kept on both sides" "$($e.SourceTerm) / $($e.TargetTerm)"

    # 2. Editing only the notes leaves the stop alone (every Save writes every field).
    [void]$R::UpdateTerm($db, [long]$id, $e.SourceTerm, $e.TargetTerm, "", "", "new note")
    $e = Get $id
    Check ($e.SourceTerm -eq "Rev." -and $e.TargetTerm -eq "Rev." -and $e.Notes -eq "new note") "update of the note only: 'Rev.' unchanged" "$($e.SourceTerm) / $($e.TargetTerm) / $($e.Notes)"

    # 3. Every punctuation mark the add path strips is kept by an edit.
    $id2 = $R::InsertTerm($db, [long]$tb, "vraag", "question", "nl-NL", "en-GB")
    [void]$R::UpdateTerm($db, [long]$id2, "wat?", "what?!")
    $e = Get $id2
    Check ($e.SourceTerm -eq "wat?" -and $e.TargetTerm -eq "what?!") "update: '?' and '!' kept" "$($e.SourceTerm) / $($e.TargetTerm)"
    [void]$R::UpdateTerm($db, [long]$id2, "zie:", "see;")
    $e = Get $id2
    Check ($e.SourceTerm -eq "zie:" -and $e.TargetTerm -eq "see;") "update: ':' and ';' kept" "$($e.SourceTerm) / $($e.TargetTerm)"

    # 4. Invisible whitespace is still cleaned on an edit.
    [void]$R::UpdateTerm($db, [long]$id2, ("  fig." + [char]0x200B + " "), ("Fig." + [char]0x00A0 + "1"))
    $e = Get $id2
    Check ($e.SourceTerm -eq "fig." -and $e.TargetTerm -eq "Fig. 1") "update: zero-width and no-break spaces still sanitised" "[$($e.SourceTerm)] / [$($e.TargetTerm)]"

    # 5. Non-translatables were already kept; still are.
    $id3 = $R::InsertTerm($db, [long]$tb, "Acme Inc.", "Acme Inc.", "nl-NL", "en-GB", "", "", "", $true)
    [void]$R::UpdateTerm($db, [long]$id3, "Acme Ltd.", "Acme Ltd.", "", "", "", $true)
    $e = Get $id3
    Check ($e.SourceTerm -eq "Acme Ltd." -and $e.IsNonTranslatable) "update of a non-translatable: 'Acme Ltd.' kept" "$($e.SourceTerm)"

    # 6. Failure path: the duplicate check sees the term as stored, stop included.
    $id4 = $R::InsertTerm($db, [long]$tb, "figuur", "figure", "nl-NL", "en-GB")
    $threw = $false
    try { [void]$R::UpdateTerm($db, [long]$id4, "Rev.", "Rev.") } catch { $threw = $true }
    Check $threw "update onto an existing 'Rev.' pair: refused as a duplicate" "no exception"
    $e = Get $id4
    Check ($e.SourceTerm -eq "figuur") "refused update left the entry unchanged" "$($e.SourceTerm)"
    # "Rev" (no stop) is a different term now, not a duplicate of "Rev.".
    try { [void]$R::UpdateTerm($db, [long]$id4, "Rev", "Rev") } catch { }
    $e = Get $id4
    Check ($e.SourceTerm -eq "Rev") "'Rev' without a stop can sit beside 'Rev.'" "$($e.SourceTerm)"

    # 7. Unknown id: false, nothing thrown.
    $ok = $R::UpdateTerm($db, [long]999999, "x.", "y.")
    Check (-not $ok) "update of an id that does not exist returns false" "$ok"
}
finally {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    try { $plugin.GetType("Microsoft.Data.Sqlite.SqliteConnection") | Out-Null } catch { }
    try { [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools() } catch { }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "$script:passes passed, $script:fails failed"
if ($script:fails -gt 0) { exit 1 }
