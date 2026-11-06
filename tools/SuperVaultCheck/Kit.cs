using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Data.Sqlite;

/// <summary>
/// <c>SuperVaultCheck.exe kit &lt;folder&gt; [db]</c>: the files for the acceptance test in Studio (a TEST project only).
///
///   SuperVault acceptance.docx   Dutch sentences taken from the Workbench database (read-only), bold where the unit has &lt;b&gt;
///   client TM.tmx                nl-BE > en-US translations of most of them, as a translator's normal TM would hold them
///   expected.txt                 which sentence is in which group, and what Analyze should show
///
/// Groups: A formatted, same translation in the client TM; B plain, same; C different translation in the client TM;
/// D only in SuperVault; E in neither (control).
/// </summary>
static class Kit
{
    sealed class Unit { public string Group, Nl, En, ClientEn; }

    public static int Run(string folder, string db)
    {
        Directory.CreateDirectory(folder);
        var formatted = Pick(db, true, 12);
        var plain = Pick(db, false, 10);
        if (formatted.Count < 12 || plain.Count < 10) { Console.WriteLine($"Not enough suitable units: {formatted.Count} formatted, {plain.Count} plain."); return 1; }

        var units = new List<Unit>();
        units.AddRange(formatted.Take(8).Select(u => new Unit { Group = "A", Nl = u.nl, En = u.en, ClientEn = u.en }));
        units.AddRange(plain.Take(4).Select(u => new Unit { Group = "B", Nl = u.nl, En = u.en, ClientEn = u.en }));
        units.AddRange(formatted.Skip(8).Take(2).Concat(plain.Skip(4).Take(2)).Select(u => new Unit { Group = "C", Nl = u.nl, En = u.en, ClientEn = Different(u.en) }));
        units.AddRange(formatted.Skip(10).Take(2).Concat(plain.Skip(6).Take(2)).Select(u => new Unit { Group = "D", Nl = u.nl, En = u.en, ClientEn = null }));
        foreach (var s in new[] { "Deze zin staat in geen enkel geheugen, ook niet in SuperVault.", "Controlezin twee voor de acceptatietest van SuperVault.",
                                  "De derde controlezin heeft ook geen <b>enkele</b> treffer.", "Tot slot een vierde zin zonder treffers." })
            units.Add(new Unit { Group = "E", Nl = s, En = null, ClientEn = null });

        WriteDocx(Path.Combine(folder, "SuperVault acceptance.docx"), units);
        WriteTmx(Path.Combine(folder, "client TM.tmx"), units.Where(u => u.ClientEn != null));
        var sb = new StringBuilder();
        sb.AppendLine("SuperVault acceptance test - what is in the test document (paragraph order)");
        sb.AppendLine("A = formatted, client TM has the same translation; B = plain, same; C = client TM has a different translation;");
        sb.AppendLine("D = only in SuperVault; E = in neither.");
        sb.AppendLine();
        sb.AppendLine("Expected: the client TM's 100% / Context Match counts are identical with SuperVault on and off.");
        sb.AppendLine("With SuperVault on, group D moves from No Match to 100%. Groups A-C keep the client TM's match, listed first.");
        sb.AppendLine();
        int i = 1;
        foreach (var u in units) sb.AppendLine($"{i++,2}. {u.Group}  {u.Nl}");
        File.WriteAllText(Path.Combine(folder, "expected.txt"), sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Kit written to {folder}: {units.Count} paragraphs, {units.Count(u => u.ClientEn != null)} client TM units.");
        return 0;
    }

    static string Different(string en) => Regex.Replace(en, @"[.!?:;]?\s*$", m => " (client wording)" + m.Value.Trim());

    // One formatting pair (<b>, <i>, <u>, <sub>, <sup>, <pt x="1">) inside the sentence (not around all of it), nothing else marked
    // up, one sentence, 40-160 characters.
    static readonly Regex OneBold = new Regex(@"^[^<>{}\[\]]+<(b|i|u|sub|sup|pt)(?: x=""\d+"")?>[^<>{}\[\]]+</\1>[^<>{}\[\]]*$");
    static readonly Regex Code = new Regex(@"(</?(?:b|i|u|sub|sup|pt)(?: x=""\d+"")?>)");   // <pt x="1">: a CAT tool's paired tag; shown as bold
    static readonly Regex Plain = new Regex(@"^[^<>{}\[\]]+$");
    static readonly Regex LooksEnglish = new Regex(@"\b(the|and|with|which|is)\b", RegexOptions.IgnoreCase);
    static readonly Regex TwoSentences = new Regex(@"[.!?]\s+\p{Lu}");

    static List<(string nl, string en)> Pick(string db, bool formatted, int count)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>();
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using (var c = new SqliteConnection(cs))
        {
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT source_text, target_text FROM translation_units WHERE source_lang LIKE 'nl%' AND target_lang LIKE 'en%' " +
                                  "AND tm_id IN ('patents', 'patents_canonical_boilerplate') " +   // published patent text only: no client documents in a test project
                                  "AND length(source_text) BETWEEN 40 AND 160 " + (formatted ? "AND source_text LIKE '%</%>%' " : "AND id % 13 = 7 ") +
                                  "ORDER BY id LIMIT 20000";
                using (var r = cmd.ExecuteReader())
                    while (r.Read() && list.Count < count)
                    {
                        string nl = r.GetString(0).Trim(), en = r.GetString(1).Trim();
                        var rx = formatted ? OneBold : Plain;
                        if (!rx.IsMatch(nl) || !rx.IsMatch(en) || TwoSentences.IsMatch(nl) || nl.Contains("\t") || LooksEnglish.IsMatch(nl) || !seen.Add(nl)) continue;
                        list.Add((nl, en));
                    }
            }
        }
        return list;
    }

    static void WriteDocx(string path, List<Unit> units)
    {
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var u in units)
            {
                var p = new Paragraph();
                foreach (var (text, code) in Runs(u.Nl))
                {
                    var run = new Run();
                    if (code == "b") run.AppendChild(new RunProperties(new Bold()));
                    else if (code == "i") run.AppendChild(new RunProperties(new Italic()));
                    else if (code == "u") run.AppendChild(new RunProperties(new Underline { Val = UnderlineValues.Single }));
                    else if (code == "sub") run.AppendChild(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript }));
                    else if (code == "sup") run.AppendChild(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }));
                    run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                    p.AppendChild(run);
                }
                body.AppendChild(p);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
    }

    static IEnumerable<(string, string)> Runs(string s)
    {
        string code = null;
        foreach (var p in Code.Split(s))
        {
            if (Code.IsMatch(p)) { code = p.StartsWith("</") ? null : p.Trim('<', '>').Split(' ')[0]; if (code == "pt") code = "b"; continue; }
            if (p.Length > 0) yield return (p, code);
        }
    }

    static void WriteTmx(string path, IEnumerable<Unit> units)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<tmx version=\"1.4\"><header creationtool=\"SuperVaultCheck\" creationtoolversion=\"1\" segtype=\"sentence\" o-tmf=\"none\" adminlang=\"en-US\" srclang=\"nl-BE\" datatype=\"plaintext\"/><body>");
        foreach (var u in units)
        {
            sb.Append("<tu><tuv xml:lang=\"nl-BE\"><seg>").Append(Seg(u.Nl)).Append("</seg></tuv>");
            sb.Append("<tuv xml:lang=\"en-US\"><seg>").Append(Seg(u.ClientEn)).AppendLine("</seg></tuv></tu>");
        }
        sb.AppendLine("</body></tmx>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static string Seg(string s) => string.Concat(Code.Split(s).Select(p =>
        !Code.IsMatch(p) ? SecurityElement.Escape(p)
        : p.StartsWith("</") ? "<ept i=\"1\">" + SecurityElement.Escape(p) + "</ept>"
        : "<bpt i=\"1\">" + SecurityElement.Escape(p) + "</bpt>"));
}
