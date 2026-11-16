# The plugin holds no payment company's address: the Buy and Manage subscription
# links open pages on supervertaler.com, which forward to whoever takes the
# payments, and the licence check goes to licence.supervertaler.com. A released
# plugin cannot be given new addresses, and Lemon Squeezy closes on 31 March 2027.
#
# Reads the source; nothing is run and nothing is opened.
#
#   powershell -NoProfile -File tools\licence_links_test.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$script:pass = 0; $script:fail = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { $script:pass++; Write-Host "  ok   $what" }
    else { $script:fail++; Write-Host "  FAIL $what" }
}

$panel = [IO.File]::ReadAllText((Join-Path $root 'src\Supervertaler.Trados\Licensing\LicensePanel.cs'))
Check ($panel -match 'const string PurchaseUrl = "https://supervertaler\.com/buy";') "Buy opens supervertaler.com/buy"
Check ($panel -match 'const string ManageUrl = "https://supervertaler\.com/manage";') "Manage subscription opens supervertaler.com/manage"
Check ([regex]::Matches($panel, 'ProcessStartInfo\((PurchaseUrl|ManageUrl)\)').Count -eq 2) "both links are opened from those two constants"

# No address at the payment company anywhere in the plugin's or core's source.
$hits = @()
foreach ($dir in 'src', 'core\src') {
    Get-ChildItem -Path (Join-Path $root $dir) -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $n = 0
            foreach ($line in [IO.File]::ReadAllLines($_.FullName)) {
                $n++
                # A comment may name the old address; code may not.
                if ($line -match '"[^"]*lemonsqueezy\.com[^"]*"') { $hits += "$($_.Name):$n" }
            }
        }
}
Check ($hits.Count -eq 0) "no lemonsqueezy.com address in any string in the plugin or core ($($hits -join ', '))"

$core = [IO.File]::ReadAllText((Join-Path $root 'core\src\SupervertalerLicence.cs'))
Check ($core -match 'const string LicenceServer = "https://licence\.supervertaler\.com/v1/licenses";') "the licence check goes to licence.supervertaler.com"

Write-Host ""
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
