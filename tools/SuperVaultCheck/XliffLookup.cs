using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Supervertaler.Core.SuperVault;

/// <summary>
/// <c>SuperVaultCheck.exe lookup-xliff &lt;file.sdlxliff&gt; [SuperVault folder]</c>: looks up every segment of a Studio
/// file in a SuperVault, read-only, and prints the best match per segment. For answering "did SuperVault really find
/// nothing?" on a real project without touching Studio. Tags are left out of the lookup (text only).
/// </summary>
static class XliffLookup
{
    public static int Run(string sdlxliff, string root)
    {
        var lib = VaultLibrary.For(root ?? VaultLibrary.DefaultRoot);
        var idx = lib.Current;
        if (idx == null) { Console.WriteLine("No usable index in " + lib.Root + ": " + lib.Status); return 1; }

        var doc = XDocument.Load(sdlxliff);
        XNamespace x = "urn:oasis:names:tc:xliff:document:1.2";
        var file = doc.Root.Element(x + "file");
        string src = VaultMarkup.BaseLanguage((string)file.Attribute("source-language"));
        string trg = VaultMarkup.BaseLanguage((string)file.Attribute("target-language"));
        Console.WriteLine($"{Path.GetFileName(sdlxliff)}: {src} > {trg}; index {lib.Status.Units:N0} units");

        int n = 0, found = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var mrk in doc.Descendants(x + "seg-source").SelectMany(s => s.Descendants(x + "mrk")).Where(m => (string)m.Attribute("mtype") == "seg"))
        {
            string text = Regex.Replace(string.Concat(mrk.DescendantNodes().OfType<XText>().Select(t => t.Value)), @"\s+", " ").Trim();
            if (text.Length == 0) continue;
            n++;
            var best = VaultLookup.Search(idx, new[] { new VaultPiece { Text = text } }, src, trg, 30, 1).FirstOrDefault();
            if (best != null) found++;
            string Short(string s) => s.Length > 90 ? s.Substring(0, 90) + "…" : s;
            Console.WriteLine($"{(string)mrk.Attribute("mid"),4} {(best == null ? "  -" : best.Score + "%"),5}  {Short(text)}");
            if (best != null) Console.WriteLine($"            vault: {Short(VaultLookup.PlainOf(best.Source))}");
        }
        Console.WriteLine($"{found} of {n} segments have a SuperVault match of 30% or more; {sw.ElapsedMilliseconds / Math.Max(1, n)} ms a segment");
        return 0;
    }
}
