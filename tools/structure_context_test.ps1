# #109: structure context - the .docx numbering reader, the sentinel helpers and
# the prompt rule, checked against the built plugin DLL (core is compiled in).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\structure_context_test.ps1
#
# Everything runs on a synthetic document built here, so the checks are exact and
# the repo carries no client file. If SV_STRUCTURE_SAMPLE names an sdlxliff on this
# machine, the embedded original is read through the plugin's own path and its
# marker sequence is compared too; the expected sequence is the shape of the
# numbering only, which identifies nobody.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$PluginDll = Join-Path $root 'src\Supervertaler.Trados\bin\Studio18\Release\Supervertaler.Trados.dll'
if (-not (Test-Path $PluginDll)) { $PluginDll = Join-Path $root 'src\Supervertaler.Trados\bin\Studio19\Release\Supervertaler.Trados.dll' }
if (-not (Test-Path $PluginDll)) { throw "Build the plugin first: $PluginDll not found" }

# The plugin references Studio assemblies; resolve them from an install so the
# types load. Core types need none of this, the resolver class does.
$studioDirs = @()
foreach ($pf in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
    foreach ($v in @('Studio18', 'Studio19')) {
        $d = Join-Path $pf "Trados\Trados Studio\$v"
        if (Test-Path $d) { $studioDirs += $d }
    }
}
$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in $studioDirs) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})

Add-Type -AssemblyName System.IO.Compression
$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0; $checks = 0
function Check($ok, $label) {
    $script:checks++
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$Structure = $plugin.GetType('Supervertaler.Core.DocxStructure')
$Numbering = $plugin.GetType('Supervertaler.Core.DocxNumbering')
$Sentinel  = $plugin.GetType('Supervertaler.Core.StructureContext')
$ModeT     = $plugin.GetType('Supervertaler.Core.StructureContextMode')
$Prompt    = $plugin.GetType('Supervertaler.Core.TranslationPrompt')

# ---- a synthetic document ------------------------------------------------------------
$W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'
function P($text, $numId, $lvl, $style, $extra) {
    $ppr = ''
    if ($style) { $ppr += "<w:pStyle w:val=`"$style`"/>" }
    if ($numId -ne $null) { $ppr += "<w:numPr><w:ilvl w:val=`"$lvl`"/><w:numId w:val=`"$numId`"/></w:numPr>" }
    if ($extra) { $ppr += $extra }
    $body = if ($text -ne $null) { "<w:r><w:t xml:space=`"preserve`">$text</w:t></w:r>" } else { '' }
    if ($ppr) { return "<w:p><w:pPr>$ppr</w:pPr>$body</w:p>" }
    return "<w:p>$body</w:p>"
}
$paras = @(
    (P 'Title' $null 0 $null)                                   # 0 unnumbered
    (P 'Claim one' 1 0)                                          # 1 -> 1.
    (P 'feature' 2 0)                                            # 2 -> bullet (F0B7)
    (P 'feature two' 2 0)                                        # 3 -> bullet
    (P 'Claim two' 1 0)                                          # 4 -> 2.
    (P 'step one' 3 0)                                           # 5 -> a)
    (P 'step two' 3 0)                                           # 6 -> b)
    (P 'Claim three' 1 0)                                        # 7 -> 3.
    (P 'step three, same list continues' 3 0)                    # 8 -> c)
    (P 'Restarted claim' 4 0)                                    # 9 -> 11.  (abstract start=11)
    (P 'Next restarted claim' 4 0)                               # 10 -> 12.
    (P 'Override start' 5 0)                                     # 11 -> 7.  (num-level startOverride 7)
    (P 'Styled item' $null 0 'ListNumber')                       # 12 -> 1.  (numbering from the style)
    (P 'Styled item two' $null 0 'ListNumber')                   # 13 -> 2.
    (P $null 1 0)                                                # 14 -> 4.  empty numbered paragraph still counts
    (P 'Claim after empty' 1 0)                                  # 15 -> 5.
    (P 'Level one' 6 0)                                          # 16 -> 1.
    (P 'Level two' 6 1)                                          # 17 -> 1.1
    (P 'Level two again' 6 1)                                    # 18 -> 1.2
    (P 'Level one again' 6 0)                                    # 19 -> 2.
    (P 'Level two after reset' 6 1)                              # 20 -> 2.1
    (P 'Dash bullet' 7 0)                                        # 21 -> -
    (P 'No list' 0 0)                                            # 22 -> null (numId 0)
    (P 'Tracked old numbering' $null 0 $null '<w:pPrChange w:id="1"><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr></w:pPrChange>')  # 23 -> null
    (P 'Entities &amp; &lt;tags&gt;' $null 0 $null)             # 24 text decoded
    '<w:p><w:r><w:t>Outer before</w:t></w:r><w:r><w:pict><w:txbxContent><w:p><w:r><w:t>Inside box</w:t></w:r></w:p></w:txbxContent></w:pict></w:r><w:r><w:t> after</w:t></w:r></w:p>'  # 25 outer, 26 nested
    (P 'Roman' 8 0)                                              # 27 -> iv) (start 4 lowerRoman)
    (P 'Letters wrap' 9 0)                                       # 28 -> aa. (start 27 lowerLetter)
)
$docXml = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><w:document $W><w:body>" + ($paras -join '') + "</w:body></w:document>"

function Abs($id, $fmt, $text, $start, $lvl1) {
    $l1 = if ($lvl1) { $lvl1 } else { '' }
    return "<w:abstractNum w:abstractNumId=`"$id`"><w:lvl w:ilvl=`"0`"><w:start w:val=`"$start`"/><w:numFmt w:val=`"$fmt`"/><w:lvlText w:val=`"$text`"/></w:lvl>$l1</w:abstractNum>"
}
$bullet = [char]0xF0B7
$numXml = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><w:numbering $W>" +
    (Abs 10 'decimal' '%1.' 1) +
    (Abs 11 'bullet' "$bullet" 1) +
    (Abs 12 'lowerLetter' '%1)' 1) +
    (Abs 13 'decimal' '%1.' 11) +
    (Abs 14 'decimal' '%1.' 1) +
    (Abs 15 'decimal' '%1.' 1 '<w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1.%2"/></w:lvl>') +
    (Abs 16 'bullet' '-' 1) +
    (Abs 17 'lowerRoman' '%1)' 4) +
    (Abs 18 'lowerLetter' '%1.' 27) +
    (Abs 19 'decimal' '%1.' 1) +
    '<w:num w:numId="1"><w:abstractNumId w:val="10"/></w:num>' +
    '<w:num w:numId="2"><w:abstractNumId w:val="11"/></w:num>' +
    '<w:num w:numId="3"><w:abstractNumId w:val="12"/></w:num>' +
    '<w:num w:numId="4"><w:abstractNumId w:val="13"/></w:num>' +
    '<w:num w:numId="5"><w:abstractNumId w:val="14"/><w:lvlOverride w:ilvl="0"><w:startOverride w:val="7"/></w:lvlOverride></w:num>' +
    '<w:num w:numId="6"><w:abstractNumId w:val="15"/></w:num>' +
    '<w:num w:numId="7"><w:abstractNumId w:val="16"/></w:num>' +
    '<w:num w:numId="8"><w:abstractNumId w:val="17"/></w:num>' +
    '<w:num w:numId="9"><w:abstractNumId w:val="18"/></w:num>' +
    '<w:num w:numId="20"><w:abstractNumId w:val="19"/></w:num>' +
    '</w:numbering>'
$stylesXml = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><w:styles $W><w:style w:type=`"paragraph`" w:styleId=`"ListNumber`"><w:pPr><w:numPr><w:ilvl w:val=`"0`"/><w:numId w:val=`"20`"/></w:numPr></w:pPr></w:style><w:style w:type=`"paragraph`" w:styleId=`"Normal`"/></w:styles>"

function MakeDocx([string[]]$entries) {
    $ms = New-Object IO.MemoryStream
    $zip = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Create, $true)
    for ($i = 0; $i -lt $entries.Length; $i += 2) {
        $e = $zip.CreateEntry($entries[$i])
        $s = $e.Open(); $b = [Text.Encoding]::UTF8.GetBytes($entries[$i + 1]); $s.Write($b, 0, $b.Length); $s.Dispose()
    }
    $zip.Dispose()
    return $ms.ToArray()
}
$docxBytes = MakeDocx @('word/document.xml', $docXml, 'word/numbering.xml', $numXml, 'word/styles.xml', $stylesXml)

$read = $Structure.GetMethod('ReadParagraphs', [type[]]@([IO.Stream]))
$paragraphs = $read.Invoke($null, @([IO.Stream](New-Object IO.MemoryStream(,$docxBytes))))
Check ($paragraphs -ne $null -and $paragraphs.Count -eq 29) "reader: 29 paragraphs, the one inside the text box included (got $($paragraphs.Count))"
$expected = @($null,'1.',"$([char]0x2022)","$([char]0x2022)",'2.','a)','b)','3.','c)','11.','12.','7.','1.','2.','4.','5.','1.','1.1','1.2','2.','2.1','-',$null,$null,$null,$null,$null,'iv)','aa.')
for ($i = 0; $i -lt $expected.Count; $i++) {
    $got = $paragraphs[$i].Marker
    Check ($got -eq $expected[$i]) "marker[$i] = $(if ($expected[$i]) {$expected[$i]} else {'(none)'}) (got $(if ($got) {$got} else {'(none)'}))"
}
Check ($paragraphs[2].IsBullet -and -not $paragraphs[1].IsBullet) 'bullets are told apart from counted items'
Check ($paragraphs[24].Text -eq 'Entities & <tags>') "text decodes entities (got '$($paragraphs[24].Text)')"
Check ($paragraphs[25].Text -eq 'Outer before after') "text of a paragraph holding a text box excludes the box (got '$($paragraphs[25].Text)')"
Check ($paragraphs[26].Text -eq 'Inside box') "nested paragraph has its own text (got '$($paragraphs[26].Text)')"
Check ($paragraphs[14].Text -eq '') 'empty numbered paragraph has empty text'
$allOffsets = $true
foreach ($p in $paragraphs) { if ($docXml.Substring($p.StartOffset, 4) -ne '<w:p') { $allOffsets = $false } }
Check $allOffsets 'every StartOffset points at a <w:p element'
Check ($docXml.Substring($paragraphs[1].EndOffset, 6) -eq '</w:p>') 'EndOffset points at the closing tag'

# wrapper zip (Studio's embedding shape): a zip holding the .docx
function MakeWrapped([byte[]]$inner) {
    $ms = New-Object IO.MemoryStream
    $zip = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Create, $true)
    $e = $zip.CreateEntry('abc123.docx'); $s = $e.Open(); $s.Write($inner, 0, $inner.Length); $s.Dispose(); $zip.Dispose()
    return $ms.ToArray()
}
$wrapped = MakeWrapped $docxBytes
$p2 = $read.Invoke($null, @([IO.Stream](New-Object IO.MemoryStream(,$wrapped))))
Check ($p2 -ne $null -and $p2.Count -eq 29 -and $p2[9].Marker -eq '11.') "a zip wrapping the .docx is unwrapped (got $(if ($p2) {$p2.Count} else {'null'}))"
$notDocx = MakeDocx @('readme.txt', 'hello')
$p3 = $read.Invoke($null, @([IO.Stream](New-Object IO.MemoryStream(,$notDocx))))
Check ($p3 -eq $null) 'a zip that is not a Word package returns null'
$noNumbering = MakeDocx @('word/document.xml', $docXml)
$p4 = $read.Invoke($null, @([IO.Stream](New-Object IO.MemoryStream(,$noNumbering))))
Check ($p4 -ne $null -and $p4.Count -eq 29 -and ($p4 | Where-Object { $_.Marker }).Count -eq 0) 'a document without numbering.xml reads with no markers'

# ---- the sentinel ---------------------------------------------------------------------
$prefix = $Sentinel.GetMethod('Prefix', $Static)
$strip  = $Sentinel.GetMethod('Strip', $Static)
function Strip($s) { $args = [object[]]@($s, $false); $r = $strip.Invoke($null, $args); return @($r, $args[1]) }
Check ($prefix.Invoke($null, @('e)', 'het fixeren')) -eq '[#e)]het fixeren') 'prefix: [#e)] then the text, no space'
Check ($prefix.Invoke($null, @($null, 'plain')) -eq 'plain') 'prefix: no marker, text unchanged'
$r = Strip '[#e)]fixing and sealing'; Check ($r[0] -eq 'fixing and sealing' -and $r[1]) 'strip: echoed sentinel removed and reported'
$r = Strip '[#e)] fixing'; Check ($r[0] -eq 'fixing' -and $r[1]) 'strip: whitespace after the sentinel goes too'
$r = Strip ' [#9.]A slide valve'; Check ($r[0] -eq 'A slide valve' -and $r[1]) 'strip: leading space before an echoed sentinel'
$r = Strip '[[TC: "stappen a. tot f." preserved]] text'; Check ($r[0] -eq '[[TC: "stappen a. tot f." preserved]] text' -and -not $r[1]) 'strip: a translator comment is untouched'
$r = Strip 'a) and b) are equal'; Check ($r[0] -eq 'a) and b) are equal' -and -not $r[1]) 'strip: content that begins with a) is untouched'
$r = Strip 'text [#e)] inside'; Check ($r[0] -eq 'text [#e)] inside' -and -not $r[1]) 'strip: only at the start'
$r = Strip ''; Check ($r[0] -eq '' -and -not $r[1]) 'strip: empty in, empty out'
$r = Strip $null; Check ($r[0] -eq $null -and -not $r[1]) 'strip: null in, null out'
Check ($Sentinel.GetField('StripPattern').GetValue($null) -eq '^\[#[^\]]*\]\s*') 'strip pattern is the agreed one, verbatim'

# ---- the rule in the system prompt --------------------------------------------------------
$build = $Prompt.GetMethod('BuildSystemPrompt', $Static)
function Sys($mode) {
    $m = [Enum]::Parse($ModeT, $mode)
    return $build.Invoke($null, @('Dutch', 'English', $null, $null, $null, $null, 500, $true, $null, $m))
}
$off = Sys 'Off'; $markers = Sys 'Markers'; $unavailable = Sys 'Unavailable'
$legacy = $build.Invoke($null, @('Dutch', 'English', $null, $null, $null, $null, 500, $true, $null, [Enum]::Parse($ModeT, 'Off')))
Check ($off -notmatch 'DOCUMENT STRUCTURE') 'Off: no structure section'
Check ($off -eq $legacy) 'Off: byte-identical to the pre-#109 prompt'
Check ($markers -match '# DOCUMENT STRUCTURE' -and $markers.Contains($Sentinel.GetField('PreambleRule').GetValue($null))) 'Markers: the preamble rule ships'
Check ($unavailable.Contains($Sentinel.GetField('FallbackRule').GetValue($null)) -and -not $unavailable.Contains('[#')) 'Unavailable: the fallback rule ships, no sentinel mentioned'
Check ($markers.IndexOf('# DOCUMENT STRUCTURE') -lt $markers.Length) 'rule sits in the plugin preamble, ahead of any custom prompt'
$withCustom = $build.Invoke($null, @('Dutch', 'English', 'MY OWN PROMPT', $null, $null, $null, 500, $true, $null, [Enum]::Parse($ModeT, 'Markers')))
Check ($withCustom.IndexOf('# DOCUMENT STRUCTURE') -lt $withCustom.IndexOf('MY OWN PROMPT')) 'rule precedes the user prompt, so it reaches prompts the plugin did not write'

# ---- a real file, when one is at hand -------------------------------------------------------
$sample = $env:SV_STRUCTURE_SAMPLE
if ($sample -and (Test-Path $sample)) {
    $Resolver = $plugin.GetType('Supervertaler.Trados.Core.StructureContextResolver')
    $embedded = $Resolver.GetMethod('ReadEmbeddedOriginal', $Static).Invoke($null, @($sample))
    Check ($embedded -ne $null) 'sample: embedded original found in the sdlxliff header'
    $rp = $read.Invoke($null, @([IO.Stream]$embedded))
    $seq = ($rp | Where-Object { $_.Marker } | ForEach-Object { $_.Marker }) -join ' '
    $want = $env:SV_STRUCTURE_EXPECTED
    if ($want) { Check ($seq -eq $want) "sample: marker sequence matches ($seq)" }
    else { Write-Host "sample: $($rp.Count) paragraphs, markers: $seq" }
} else {
    Write-Host 'sample: SV_STRUCTURE_SAMPLE not set - skipped'
}

Write-Host "`n$checks checks, $fails failures"
if ($fails -gt 0) { exit 1 }
