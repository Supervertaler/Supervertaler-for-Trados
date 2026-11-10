# Type-to-filter (18.20.206): the prompt and memory-bank pickers and the termbase
# list in Settings - checked against the built plugin DLL, outside Studio.
#
#  Part A  FilterText: any part of the name, any order of words, case and accents
#          ignored, empty filter = everything, pinned rows always kept, order kept.
#  Part B  The picker popup (ComboFilter), built but never shown: typing filters
#          without touching the combo; Up/Down/PageUp/PageDown/Enter pick; Escape
#          clears, a second Escape cancels; the combo's selection changes exactly
#          once, and only on a pick; pinned rows ("+ New memory bank...") survive
#          any filter; Enter on no match changes nothing.
#  Part C  The termbase grid (TermbaseGridFilter): a ticked termbase the filter
#          hides stays ticked and is still read back as ticked (= saved, = used);
#          a pending checkbox click is committed, not lost; a hidden row is
#          deselected so Remove/Export can't act on it; header bulk ticks reach
#          visible rows only; clearing the filter brings every row back unchanged.
#  Scale   2,000 names / 2,000 grid rows, timed per keystroke.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\type_to_filter_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\type_to_filter_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Touches no files: nothing here reads or writes settings or the database.
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

Add-Type -AssemblyName System.Windows.Forms
Add-Type -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing', $Dll) -TypeDefinition @"
using System; using System.IO; using System.Reflection; using System.Diagnostics;
using System.Collections.Generic; using System.Windows.Forms;
using Supervertaler.Trados.Core; using Supervertaler.Trados.Controls;

public static class TypeToFilterTest {
    static string[] _dirs;
    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }

    public static int Pass, Fail;
    static void Check(bool ok, string what) {
        if (ok) { Pass++; Console.WriteLine("  ok   " + what); }
        else { Fail++; Console.WriteLine("  FAIL " + what); }
    }

    // ── Part A ────────────────────────────────────────────────────────
    public static void PartA() {
        Console.WriteLine("Part A  FilterText");
        Check(FilterText.Matches("CONTOSO_XYZ Machines", "xyz"), "any part of the name, any case");
        Check(FilterText.Matches("CONTOSO_XYZ Machines", "mach xyz"), "several words, any order");
        Check(!FilterText.Matches("CONTOSO_XYZ Machines", "xyz widgets"), "every word must match");
        Check(FilterText.Matches("EXAMPLE BUR\u00D3 (1234)", "buro"), "accents ignored (BUR\u00D3 / buro)");
        Check(FilterText.Matches("Dutch \u2192 English", "DUTCH"), "language column text");
        Check(FilterText.Matches("anything", "") && FilterText.Matches("anything", "   "), "empty / blank filter matches all");
        Check(FilterText.Matches("", "") && !FilterText.Matches("", "x") && !FilterText.Matches(null, "x"), "empty text only matches empty filter");
        var list = new List<string> { "acme-alpha", "(match by project name)", "globex_initech", "acme-beta", "+ New memory bank" };
        var hits = FilterText.MatchingIndices(list, "acme", i => i == 1 || i == 4);
        Check(string.Join(",", hits) == "0,1,3,4", "pinned rows kept, original order kept (" + string.Join(",", hits) + ")");
        Check(FilterText.MatchingIndices(list, "zzz").Count == 0, "no match = empty");
        Check(FilterText.MatchingIndices(null, "x").Count == 0, "null list = empty, no throw");
    }

    // ── Part B ────────────────────────────────────────────────────────
    static MethodInfo _create;
    static Form Popup(ComboBox c, Func<object, bool> pinned, string initial) {
        if (_create == null) _create = typeof(ComboFilter).GetMethod("CreatePopupForTest", BindingFlags.NonPublic | BindingFlags.Static);
        return (Form)_create.Invoke(null, new object[] { c, pinned, initial });
    }
    static object Get(Form p, string prop) { return p.GetType().GetProperty(prop, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(p, null); }
    static void Set(Form p, string prop, object v) { p.GetType().GetProperty(prop, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(p, v, null); }
    static void Key(Form p, Keys k) { p.GetType().GetMethod("KeyForTest", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(p, new object[] { k }); }

    public static void PartB() {
        Console.WriteLine("Part B  picker popup");
        var host = new Form();
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        host.Controls.Add(combo);
        var banks = new[] { "(no project bank \u2013 shared only)", "umbrella-po", "hooli", "acme-alpha-001", "acme-beta-001",
                            "CONTOSO_XYZ Machines", "CONTOSO_Widgets", "globex_initech", "(match by project name)", "+ New memory bank\u2026" };
        foreach (var b in banks) combo.Items.Add(b);
        combo.SelectedIndex = 2; // hooli
        int changes = 0; combo.SelectedIndexChanged += delegate { changes++; };
        Func<object, bool> pinned = o => { var s = o as string; return s != null && (s.StartsWith("(") || s.StartsWith("+")); };

        var p = Popup(combo, pinned, null);
        Check((int)Get(p, "VisibleCountForTest") == banks.Length, "opens with every row");
        Check((string)Get(p, "HighlightedForTest") == "hooli", "current choice highlighted on open");
        Set(p, "FilterTextForTest", "contoso");
        Check((int)Get(p, "VisibleCountForTest") == 5, "typing 'contoso': 2 banks + 3 pinned rows");
        Check(combo.SelectedIndex == 2 && changes == 0, "typing changes nothing in the combo");
        Check((string)Get(p, "HighlightedForTest") == "CONTOSO_XYZ Machines", "first real match highlighted, not a pinned row");
        Key(p, Keys.Down);
        Check((string)Get(p, "HighlightedForTest") == "CONTOSO_Widgets", "Down moves");
        Key(p, Keys.Up);
        Check((string)Get(p, "HighlightedForTest") == "CONTOSO_XYZ Machines", "Up moves back");
        Key(p, Keys.Escape);
        Check((string)Get(p, "FilterTextForTest") == "" && (int)Get(p, "VisibleCountForTest") == banks.Length, "Escape clears the filter, full list back");
        Check(!p.IsDisposed && combo.SelectedIndex == 2 && changes == 0, "first Escape keeps it open, nothing changed");
        Set(p, "FilterTextForTest", "xyz mach");
        Key(p, Keys.Enter);
        Check(combo.SelectedIndex == 5 && changes == 1, "Enter picks: selection changed exactly once (" + changes + ")");
        Check(p.IsDisposed, "closed after the pick");

        changes = 0;
        p = Popup(combo, pinned, "b");
        Check((string)Get(p, "FilterTextForTest") == "b", "typing on the closed combo opens with that letter");
        Key(p, Keys.Escape); Key(p, Keys.Escape);
        Check(p.IsDisposed && combo.SelectedIndex == 5 && changes == 0, "Escape, Escape: closed, nothing changed");

        p = Popup(combo, null, "zzz");
        Check((int)Get(p, "VisibleCountForTest") == 0, "no match: empty list");
        Key(p, Keys.Enter);
        Check(combo.SelectedIndex == 5 && changes == 0, "Enter on no match changes nothing");

        p = Popup(combo, pinned, "zzz");
        Check((int)Get(p, "VisibleCountForTest") == 3, "no match still shows the pinned rows (+ New memory bank...)");
        p.Close();

        p = Popup(combo, pinned, "xyz");
        Key(p, Keys.Enter);
        Check(changes == 0, "picking the row already chosen raises no change");

        p = Popup(combo, null, null);
        Key(p, Keys.PageDown); Key(p, Keys.PageDown); Key(p, Keys.PageDown);
        Check((string)Get(p, "HighlightedForTest") == banks[banks.Length - 1], "PageDown stops at the last row");
        Key(p, Keys.PageUp); Key(p, Keys.PageUp); Key(p, Keys.PageUp);
        Check((string)Get(p, "HighlightedForTest") == banks[0], "PageUp stops at the first row");
        p.Close();

        // Scale: 2,000 entries, a typed word one letter at a time.
        var big = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        host.Controls.Add(big);
        for (int i = 0; i < 2000; i++) big.Items.Add("client-" + i.ToString("0000") + (i % 7 == 0 ? " patents" : " manuals"));
        big.SelectedIndex = 0;
        var sw = Stopwatch.StartNew();
        p = Popup(big, null, null);
        var open = sw.ElapsedMilliseconds;
        long worst = 0;
        foreach (var t in new[] { "p", "pa", "pat", "pate", "paten", "patent", "patents", "patents 1", "patents 19", "" }) {
            var k = Stopwatch.StartNew(); Set(p, "FilterTextForTest", t); worst = Math.Max(worst, k.ElapsedMilliseconds);
        }
        Set(p, "FilterTextForTest", "patents");
        Check((int)Get(p, "VisibleCountForTest") == 286, "2,000 entries: 'patents' finds 286");
        Check(worst < 150, "2,000 entries: open " + open + " ms, slowest keystroke " + worst + " ms (< 150)");
        p.Close();
        host.Dispose();
    }

    // ── Part C ────────────────────────────────────────────────────────
    static DataGridView Grid(Form host, int rows) {
        var g = new DataGridView { AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "colRead" });
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "colWrite" });
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "colProject" });
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "colAi" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "colName" });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "colLanguages" });
        host.Controls.Add(g);
        host.CreateControl(); g.CreateControl();
        return g;
    }
    static bool Ticked(DataGridViewRow r, string c) { var v = r.Cells[c].Value as bool?; return v ?? false; }
    static readonly string[] Ticks = { "colRead", "colWrite", "colProject", "colAi" };

    public static void PartC() {
        Console.WriteLine("Part C  termbase grid");
        var host = new Form { Width = 800, Height = 600 };
        var g = Grid(host, 0);
        g.Rows.Add(false, false, false, false, "EuroVoc_Dutch-English", "Dutch \u2192 English");
        g.Rows.Add(true,  true,  true,  true,  "CONTOSO_XYZ Machines",   "English \u2192 Dutch");
        g.Rows.Add(false, false, false, false, "Glossar Musterwerk",    "German \u2192 English");
        g.Rows.Add(true,  false, false, false, "LEGAL",                 "Dutch \u2192 English");
        host.Show();
        g.CurrentCell = g.Rows[1].Cells["colName"]; g.Rows[1].Selected = true;

        var r = TermbaseGridFilter.Apply(g, "german", "colName", "colLanguages", Ticks);
        Check(r.Total == 4 && r.Shown == 1, "'german' shows 1 of 4 (by language)");
        Check(r.HiddenTicked == 2, "2 hidden termbases still ticked are counted (" + r.HiddenTicked + ")");
        Check(!g.Rows[1].Visible && Ticked(g.Rows[1], "colRead") && Ticked(g.Rows[1], "colWrite") && Ticked(g.Rows[1], "colProject") && Ticked(g.Rows[1], "colAi"),
              "hidden CONTOSO keeps Read/Write/Project/AI ticked");
        Check(!g.Rows[1].Selected && g.SelectedRows.Count == 0, "the hidden selected row is deselected (Remove can't hit it)");
        Check(g.CurrentRow == null || g.CurrentRow.Visible, "current cell is not on a hidden row");

        // What the Settings OK button does: walk every row and read the ticks.
        int readTicked = 0; foreach (DataGridViewRow row in g.Rows) if (Ticked(row, "colRead")) readTicked++;
        Check(readTicked == 2, "saving reads hidden rows too: 2 Read ticks, as before filtering");

        // Header bulk tick acts on visible rows only.
        foreach (var row in TermbaseGridFilter.BulkTargets(g)) row.Cells["colRead"].Value = true;
        Check(Ticked(g.Rows[2], "colRead") && !Ticked(g.Rows[0], "colRead"), "bulk tick reaches the visible row, not the hidden one");
        int targets = 0; foreach (var row in TermbaseGridFilter.BulkTargets(g)) targets++;
        Check(targets == 1, "bulk targets = visible rows only");

        r = TermbaseGridFilter.Apply(g, "", "colName", "colLanguages", Ticks);
        Check(r.Shown == 4 && r.HiddenTicked == 0, "clearing the filter shows all 4");
        Check(Ticked(g.Rows[1], "colProject") && Ticked(g.Rows[3], "colRead") && !Ticked(g.Rows[0], "colRead"), "every tick as it was");

        r = TermbaseGridFilter.Apply(g, "dutch", "colName", "colLanguages", Ticks);
        Check(r.Shown == 3, "'dutch' matches either side of the pair (3)");
        r = TermbaseGridFilter.Apply(g, "nothing-like-this", "colName", "colLanguages", Ticks);
        Check(r.Shown == 0 && r.HiddenTicked == 3, "no match: all hidden, 3 still ticked");
        TermbaseGridFilter.Apply(g, "", "colName", "colLanguages", Ticks);

        // A checkbox clicked but not yet committed must survive its row being hidden.
        g.CurrentCell = g.Rows[0].Cells["colAi"];
        g.BeginEdit(false);
        // What a click on an unticked box does before the grid commits it.
        ((DataGridViewCheckBoxCell)g.Rows[0].Cells["colAi"]).EditingCellFormattedValue = true;
        g.NotifyCurrentCellDirty(true);
        TermbaseGridFilter.Apply(g, "legal", "colName", "colLanguages", Ticks);
        Check(Ticked(g.Rows[0], "colAi") && !g.Rows[0].Visible, "a pending checkbox click is kept when its row is hidden");
        TermbaseGridFilter.Apply(g, "", "colName", "colLanguages", Ticks);
        host.Close(); host.Dispose();

        Scale(1000, 500);
        Scale(2000, 1000);
    }

    // Scale: the worst keystroke is the one that hides or shows half the rows.
    static void Scale(int n, long limit) {
        var host2 = new Form { Width = 800, Height = 600 };
        var big = Grid(host2, 0);
        for (int i = 0; i < n; i++) big.Rows.Add(i % 3 == 0, false, false, i % 5 == 0, "Client " + i.ToString("0000"), i % 2 == 0 ? "English \u2192 Dutch" : "German \u2192 English");
        host2.Show();
        long worst = 0;
        foreach (var t in new[] { "g", "ge", "ger", "germ", "germa", "german", "german 1", "german 19", "german 199", "" }) {
            var k = Stopwatch.StartNew(); TermbaseGridFilter.Apply(big, t, "colName", "colLanguages", Ticks); worst = Math.Max(worst, k.ElapsedMilliseconds);
        }
        var r = TermbaseGridFilter.Apply(big, "german", "colName", "colLanguages", Ticks);
        Check(r.Shown == n / 2, n + " termbases: 'german' shows " + (n / 2));
        Check(worst < limit, n + " termbases: slowest keystroke " + worst + " ms (< " + limit + ")");
        host2.Close(); host2.Dispose();
    }
}
"@

[TypeToFilterTest]::Install([string[]]@((Split-Path $Dll), $studio))
[TypeToFilterTest]::PartA()
[TypeToFilterTest]::PartB()
[TypeToFilterTest]::PartC()
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [TypeToFilterTest]::Pass, [TypeToFilterTest]::Fail)
if ([TypeToFilterTest]::Fail -gt 0) { exit 1 }
