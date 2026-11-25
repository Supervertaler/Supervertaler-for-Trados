using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Supervertaler.Trados.Core
{
    // ─── The shipped data (Resources\example-project\example-project.json) ───

    [DataContract]
    public sealed class ExampleData
    {
        [DataMember(Name = "version")] public int Version { get; set; }
        [DataMember(Name = "sourceLanguage")] public string SourceLanguage { get; set; }
        [DataMember(Name = "document")] public List<ExampleBlock> Document { get; set; } = new List<ExampleBlock>();
        [DataMember(Name = "tm")] public List<ExampleTmEntry> Tm { get; set; } = new List<ExampleTmEntry>();
        [DataMember(Name = "terms")] public List<ExampleTerm> Terms { get; set; } = new List<ExampleTerm>();
        [DataMember(Name = "languages")] public Dictionary<string, ExampleLanguage> Languages { get; set; } = new Dictionary<string, ExampleLanguage>();
    }

    /// <summary>One block of the document. Which fields are used depends on <see cref="Type"/>:
    /// title, subtitle, heading, para (Id, Text); bullets, steps (Items); table (Rows, Header).</summary>
    [DataContract]
    public sealed class ExampleBlock
    {
        [DataMember(Name = "type")] public string Type { get; set; }
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "text")] public string Text { get; set; }
        [DataMember(Name = "items")] public List<ExampleBlock> Items { get; set; }
        [DataMember(Name = "rows")] public List<List<ExampleBlock>> Rows { get; set; }
        [DataMember(Name = "header")] public bool Header { get; set; }
    }

    [DataContract]
    public sealed class ExampleTmEntry
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "for")] public string For { get; set; }
        [DataMember(Name = "band")] public string Band { get; set; }
        [DataMember(Name = "source")] public string Source { get; set; }
    }

    [DataContract]
    public sealed class ExampleTerm
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "source")] public string Source { get; set; }
    }

    /// <summary>One prepared language: every text by id.</summary>
    [DataContract]
    public sealed class ExampleLanguage
    {
        [DataMember(Name = "document")] public Dictionary<string, string> Document { get; set; } = new Dictionary<string, string>();
        [DataMember(Name = "tm")] public Dictionary<string, string> Tm { get; set; } = new Dictionary<string, string>();
        [DataMember(Name = "terms")] public Dictionary<string, string> Terms { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>What the user chose: the project's two languages, and which prepared data (if any) goes with them.</summary>
    public sealed class ExampleDirection
    {
        /// <summary>Studio language codes of the project, e.g. en-US and de-AT.</summary>
        public string Source { get; set; }
        public string Target { get; set; }
        /// <summary>The non-English side's key in <see cref="ExampleData.Languages"/>, or null when that language is not prepared.</summary>
        public string DataKey { get; set; }
        /// <summary>True for "into English from …": the translated document is the source.</summary>
        public bool IntoEnglish { get; set; }

        public bool Prepared => DataKey != null;
    }

    /// <summary>The marker file in an example project's folder.</summary>
    [DataContract]
    public sealed class ExampleMarker
    {
        [DataMember(Name = "source")] public string Source { get; set; }
        [DataMember(Name = "target")] public string Target { get; set; }
        [DataMember(Name = "dataVersion")] public int DataVersion { get; set; }
        [DataMember(Name = "termbase")] public string Termbase { get; set; }
        /// <summary>The memory bank's folder name; null in projects made before it existed.</summary>
        [DataMember(Name = "memoryBank", EmitDefaultValue = false)] public string MemoryBank { get; set; }
    }

    /// <summary>
    /// "Open example project" (first-run plan, step 1), the part without Studio:
    /// the shipped data, the language choice, the project's name and folder, the
    /// document, the TM entries and the terms. Nothing here grows with the user's
    /// data: one fixed document, about ten TM entries, fifteen terms; the only
    /// look at the user's disk is a handful of candidate folders by name.
    /// </summary>
    public static class ExampleProject
    {
        public const string ResourceName = "ExampleProject.example-project.json";
        public const string MarkerFileName = "supervertaler-example.json";
        public const string NamePrefix = "Supervertaler example";
        /// <summary>How many "(2)", "(3)" … names are tried before giving up.</summary>
        public const int MaxSuffix = 50;

        // ─── Loading ────────────────────────────────────────────────

        private static ExampleData _cached;

        public static ExampleData Load()
        {
            var c = _cached;
            if (c != null) return c;
            using (var s = typeof(ExampleProject).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) throw new InvalidOperationException("The example project's data is missing from the plugin.");
                c = Parse(s);
            }
            _cached = c;
            return c;
        }

        public static ExampleData Parse(Stream s)
        {
            var ser = new DataContractJsonSerializer(typeof(ExampleData),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            var d = (ExampleData)ser.ReadObject(s);
            if (d.Languages == null) d.Languages = new Dictionary<string, ExampleLanguage>();
            return d;
        }

        public static ExampleData Parse(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) return Parse(ms);
        }

        // ─── Languages ──────────────────────────────────────────────

        /// <summary>
        /// Which prepared data serves <paramref name="culture"/>: its own key if the
        /// data has one (pt-BR), Chinese by script (zh-CN, zh-SG → zh-Hans; zh-TW,
        /// zh-HK → zh-Hant), otherwise the bare language (de-AT → de). Null when
        /// nothing fits, or when it is English.
        /// </summary>
        public static string DataKeyFor(ExampleData data, string culture)
        {
            if (string.IsNullOrWhiteSpace(culture)) return null;
            var c = culture.Trim();
            if (c.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (c.Length == 2 || c[2] == '-')) return null;
            foreach (var k in data.Languages.Keys)
                if (string.Equals(k, c, StringComparison.OrdinalIgnoreCase)) return k;

            var lang = c.Split('-')[0].ToLowerInvariant();
            if (lang == "zh")
            {
                var u = c.ToUpperInvariant();
                bool traditional = u.Contains("HANT") || u.EndsWith("-TW") || u.EndsWith("-HK") || u.EndsWith("-MO");
                var want = traditional ? "zh-Hant" : "zh-Hans";
                return data.Languages.Keys.FirstOrDefault(k => string.Equals(k, want, StringComparison.OrdinalIgnoreCase));
            }
            // Brazil and Portugal write differently: neither serves the other.
            if (lang == "pt")
            {
                bool brazil = c.EndsWith("-BR", StringComparison.OrdinalIgnoreCase);
                var want = brazil ? "pt-BR" : "pt";
                return data.Languages.Keys.FirstOrDefault(k => string.Equals(k, want, StringComparison.OrdinalIgnoreCase));
            }
            return data.Languages.Keys.FirstOrDefault(k => string.Equals(k, lang, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The language's missing texts, by kind ("document d7", "tm tm4", "term w3").
        /// Empty means complete: every document text, TM entry and term is there.
        /// </summary>
        public static List<string> Missing(ExampleData data, string key)
        {
            var missing = new List<string>();
            if (key == null || !data.Languages.TryGetValue(key, out var l) || l == null)
            {
                missing.Add("language " + key);
                return missing;
            }
            foreach (var id in DocumentIds(data))
                if (!Has(l.Document, id)) missing.Add("document " + id);
            foreach (var t in data.Tm)
                if (!Has(l.Tm, t.Id)) missing.Add("tm " + t.Id);
            foreach (var t in data.Terms)
                if (!Has(l.Terms, t.Id)) missing.Add("term " + t.Id);
            return missing;
        }

        private static bool Has(Dictionary<string, string> d, string id) =>
            d != null && d.TryGetValue(id, out var v) && !string.IsNullOrWhiteSpace(v);

        /// <summary>
        /// The direction for a choice in the dialog. A language whose data is
        /// incomplete counts as not prepared (document only), never as half
        /// prepared. "Into English" needs the translated document, so it is
        /// refused for a language that is not prepared.
        /// </summary>
        public static ExampleDirection Direction(ExampleData data, string otherLanguage, bool intoEnglish, string english, out string error)
        {
            error = null;
            var other = Canonical(otherLanguage);
            var en = Canonical(string.IsNullOrWhiteSpace(english) ? data.SourceLanguage : english);
            if (other == null) { error = "Choose a language."; return null; }
            if (en == null || !en.StartsWith("en", StringComparison.OrdinalIgnoreCase)) { error = "Choose a variant of English."; return null; }
            if (other.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (other.Length == 2 || other[2] == '-'))
            { error = "The example is in English: choose the other language."; return null; }

            var key = DataKeyFor(data, other);
            if (key != null && Missing(data, key).Count > 0) key = null;
            if (intoEnglish && key == null)
            { error = "The example is not yet available in " + EnglishName(other) + ", so it can only go from English into " + EnglishName(other) + "."; return null; }

            return intoEnglish
                ? new ExampleDirection { Source = other, Target = en, DataKey = key, IntoEnglish = true }
                : new ExampleDirection { Source = en, Target = other, DataKey = key, IntoEnglish = false };
        }

        /// <summary>
        /// The dialog's question as a translator asks it: a source and a target
        /// language. One of them has to be English, because the example document
        /// is written in English; the rest is <see cref="Direction"/>.
        /// </summary>
        public static ExampleDirection FromPair(ExampleData data, string source, string target, out string error)
        {
            error = null;
            bool IsEn(string c) => c != null && c.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (c.Length == 2 || c[2] == '-');
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            { error = "Choose a source and a target language."; return null; }
            bool se = IsEn(source), te = IsEn(target);
            if (se && te) { error = "Choose a language other than English on one side."; return null; }
            if (!se && !te) { error = "One of the two languages has to be English: the example document is written in English."; return null; }
            return se
                ? Direction(data, target, false, source, out error)
                : Direction(data, source, true, target, out error);
        }

        /// <summary>A real, specific Windows culture's name (de-AT), or null.</summary>
        public static string Canonical(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            try
            {
                var ci = CultureInfo.GetCultureInfo(code.Trim());
                if (ci.LCID == CultureInfo.InvariantCulture.LCID || string.IsNullOrEmpty(ci.Name)) return null;
                if ((ci.CultureTypes & CultureTypes.UserCustomCulture) != 0) return null;
                if (ci.IsNeutralCulture) return null;
                return ci.Name;
            }
            catch (CultureNotFoundException) { return null; }
        }

        /// <summary>"German", "Portuguese (Brazil)", "Chinese (Simplified)": the language
        /// without its country, except where the variants differ in writing.</summary>
        public static string EnglishName(string culture)
        {
            try
            {
                var ci = CultureInfo.GetCultureInfo(culture);
                var lang = ci.TwoLetterISOLanguageName;
                if (lang == "pt" && !ci.IsNeutralCulture) return ci.EnglishName;
                if (lang == "zh")
                {
                    var u = ci.Name.ToUpperInvariant();
                    return u.Contains("HANT") || u.EndsWith("-TW") || u.EndsWith("-HK") || u.EndsWith("-MO")
                        ? "Chinese (Traditional)" : "Chinese (Simplified)";
                }
                var n = ci.IsNeutralCulture ? ci : ci.Parent;
                var name = n?.EnglishName ?? ci.EnglishName;
                return string.IsNullOrEmpty(name) ? culture : name;
            }
            catch { return culture; }
        }

        /// <summary>
        /// A first guess for the dialog: the open project's non-English language
        /// (into English if its target is English), else Windows' display
        /// language if it is not English, else nothing. Reads no files.
        /// </summary>
        public static (string language, bool intoEnglish) Guess(string currentSource, string currentTarget, string uiCulture)
        {
            bool IsEn(string c) => c != null && c.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            var s = Canonical(currentSource);
            var t = Canonical(currentTarget);
            if (t != null && !IsEn(t)) return (t, false);
            if (t != null && IsEn(t) && s != null && !IsEn(s)) return (s, true);
            var ui = Canonical(uiCulture);
            if (ui != null && !IsEn(ui)) return (ui, false);
            return (null, false);
        }

        // ─── Name and folder ────────────────────────────────────────

        public static string ProjectName(ExampleDirection d) =>
            NamePrefix + " (" + EnglishName(d.Source) + " to " + EnglishName(d.Target) + ")";

        // ─── The memory bank ────────────────────────────────────────

        /// <summary>
        /// The example's memory bank: named like the project, as the banks are
        /// (SanitizeBankName, so "Supervertaler example (Dutch to English)" gives
        /// "supervertaler-example-dutch-to-english"), which is also the name
        /// Supervertaler matches a project's bank by. Empty if nothing usable remains.
        /// </summary>
        public static string MemoryBankName(string projectName) =>
            Settings.UserDataPath.SanitizeBankName(projectName ?? "");

        /// <summary>
        /// The bank's three files (name, contents). Written in English and true for
        /// every language: a brief, two client terms the termbase does not hold,
        /// and style rules the document actually tests (units, the ON/OFF button,
        /// the E1-E3 codes, safety notices). Short on purpose: the bank goes with
        /// every AI request in the project.
        /// </summary>
        public static List<KeyValuePair<string, string>> MemoryBankFiles(string projectName)
        {
            const string nl = "\r\n";
            var brief =
                "# " + projectName + nl + nl +
                "Made by \"Open example project\" in Supervertaler for Trados. It shows what a" + nl +
                "memory bank does: Supervertaler sends these files with every AI request in" + nl +
                "this project (Translate, Batch Translate, Proofread, QuickLauncher), so the AI" + nl +
                "follows them without being told each time. Change something here and translate" + nl +
                "a segment again to see the difference. \"Start afresh\" puts the files back." + nl + nl +
                "## Client" + nl + nl +
                "Acme (an invented company) makes water heaters. This job is the installation" + nl +
                "and operating guide for the AquaLine AL-50, read by installers and home owners." + nl + nl +
                "## Standing instructions" + nl + nl +
                "- Keep the brand \"Acme\" and the product name \"AquaLine AL-50\" exactly as they are." + nl +
                "- Translate safety instructions and warnings in full. Never shorten or soften them." + nl +
                "- Address the reader directly, in the polite form where the language has one." + nl + nl +
                "## Files" + nl + nl +
                "- [terminology.md](terminology.md) - term decisions" + nl +
                "- [style.md](style.md) - how things are written" + nl;
            var terminology =
                "# Terminology - " + projectName + nl + nl +
                "The termbase holds the technical terms; this table holds the client's own" + nl +
                "decisions." + nl + nl +
                "| Source | Target | Scope | Note |" + nl +
                "|---|---|---|---|" + nl +
                "| Acme | Acme | client | Company name: never translated |" + nl +
                "| AquaLine AL-50 | AquaLine AL-50 | client | Product name: never translated; \"AL-50\" on its own as well |" + nl;
            var style =
                "# Style - " + projectName + nl + nl +
                "## 1. Instructions" + nl + nl +
                "Steps are direct instructions to the reader (\"Switch the appliance on\")," + nl +
                "not descriptions (\"The appliance is switched on\")." + nl + nl +
                "## 2. Numbers and units" + nl + nl +
                "A space between number and unit (5.5 kW, 6 mm, 6 bar). Use the decimal" + nl +
                "separator of the target language." + nl + nl +
                "## 3. What the appliance shows" + nl + nl +
                "Button labels and display codes stay as they are printed on the appliance:" + nl +
                "ON/OFF, E1, E2, E3." + nl;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("brief.md", brief),
                new KeyValuePair<string, string>("terminology.md", terminology),
                new KeyValuePair<string, string>("style.md", style),
            };
        }

        public sealed class FolderPlan
        {
            /// <summary>The name and folder to make, when a new project is needed.</summary>
            public string Name;
            public string Folder;
            /// <summary>An earlier example project for exactly this direction, if any.</summary>
            public string ExistingProjectFile;
            public string ExistingName;
            public string ExistingFolder;
            public string Error;
        }

        /// <summary>
        /// Where the example goes in <paramref name="projectsFolder"/>. Tries the
        /// name, then "(2)", "(3)" …: a folder holding an example project for the
        /// same languages (marker file plus a .sdlproj) is the existing one; any
        /// other folder that exists is the user's and is never touched; the first
        /// name with neither a folder nor a registered project is the new one.
        /// <paramref name="registeredNames"/> are the names in Studio's Projects
        /// list (case-insensitive), which a project somewhere else may hold.
        /// </summary>
        public static FolderPlan PlanFolder(string projectsFolder, ExampleDirection d, ICollection<string> registeredNames)
        {
            var plan = new FolderPlan();
            if (string.IsNullOrWhiteSpace(projectsFolder))
            {
                plan.Error = "Studio's Projects folder could not be found.";
                return plan;
            }
            var baseName = ProjectName(d);
            var registered = new HashSet<string>(registeredNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            // All of them: 50 folder checks are nothing, and an earlier example at
            // "(3)" must be found even when "(2)" has since been deleted.
            for (int i = 1; i <= MaxSuffix; i++)
            {
                var name = i == 1 ? baseName : baseName + " (" + i + ")";
                var folder = Path.Combine(projectsFolder, name);
                if (Directory.Exists(folder))
                {
                    var m = ReadMarker(folder);
                    var sdlproj = m == null ? null : ProjectFileIn(folder);
                    if (m != null && sdlproj != null && plan.ExistingProjectFile == null
                        && string.Equals(m.Source, d.Source, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Target, d.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.ExistingProjectFile = sdlproj;
                        plan.ExistingName = name;
                        plan.ExistingFolder = folder;
                    }
                    continue;
                }
                if (registered.Contains(name)) continue;
                if (plan.Name == null)
                {
                    plan.Name = name;
                    plan.Folder = folder;
                }
            }
            if (plan.Name == null)
                plan.Error = "There are already " + MaxSuffix + " folders named '" + baseName + "' in " + projectsFolder + ".";
            return plan;
        }

        private static string ProjectFileIn(string folder)
        {
            try { return Directory.GetFiles(folder, "*.sdlproj", SearchOption.TopDirectoryOnly).FirstOrDefault(); }
            catch { return null; }
        }

        public static ExampleMarker ReadMarker(string folder)
        {
            try
            {
                var p = Path.Combine(folder, MarkerFileName);
                if (!File.Exists(p)) return null;
                using (var s = File.OpenRead(p))
                    return (ExampleMarker)new DataContractJsonSerializer(typeof(ExampleMarker)).ReadObject(s);
            }
            catch { return null; }
        }

        public static void WriteMarker(string folder, ExampleMarker m)
        {
            using (var s = File.Create(Path.Combine(folder, MarkerFileName)))
                new DataContractJsonSerializer(typeof(ExampleMarker)).WriteObject(s, m);
        }

        // ─── Texts for a direction ──────────────────────────────────

        public static IEnumerable<string> DocumentIds(ExampleData data)
        {
            foreach (var b in data.Document)
            {
                if (b.Id != null) yield return b.Id;
                if (b.Items != null) foreach (var i in b.Items) if (i.Id != null) yield return i.Id;
                if (b.Rows != null) foreach (var r in b.Rows) foreach (var c in r) if (c.Id != null) yield return c.Id;
            }
        }

        /// <summary>The document's text for <paramref name="id"/> in the project's source language.</summary>
        public static string SourceText(ExampleData data, ExampleDirection d, string id, string english)
        {
            if (!d.IntoEnglish) return english;
            return data.Languages[d.DataKey].Document[id];
        }

        /// <summary>The TM's units in project order (source, target), plain text.
        /// Empty when the language is not prepared.</summary>
        public static List<KeyValuePair<string, string>> TmUnits(ExampleData data, ExampleDirection d)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (!d.Prepared) return list;
            var l = data.Languages[d.DataKey];
            foreach (var t in data.Tm)
            {
                var en = PlainText(t.Source);
                var other = PlainText(l.Tm[t.Id]);
                list.Add(d.IntoEnglish ? new KeyValuePair<string, string>(other, en) : new KeyValuePair<string, string>(en, other));
            }
            return list;
        }

        /// <summary>The termbase's entries (source term, target term). Empty when not prepared.</summary>
        public static List<KeyValuePair<string, string>> TermPairs(ExampleData data, ExampleDirection d)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (!d.Prepared) return list;
            var l = data.Languages[d.DataKey];
            foreach (var t in data.Terms)
            {
                var other = l.Terms[t.Id].Trim();
                list.Add(d.IntoEnglish ? new KeyValuePair<string, string>(other, t.Source) : new KeyValuePair<string, string>(t.Source, other));
            }
            return list;
        }

        // ─── Inline markup: **bold**, *italic*, [text](url) ──────────

        public sealed class Run
        {
            public string Text;
            public bool Bold;
            public bool Italic;
            public string Link;
        }

        private static readonly Regex Token = new Regex(@"\*\*(.+?)\*\*|\*(.+?)\*|\[(.+?)\]\((.+?)\)", RegexOptions.Compiled);

        public static List<Run> ParseInline(string text)
        {
            var runs = new List<Run>();
            if (string.IsNullOrEmpty(text)) return runs;
            int pos = 0;
            foreach (Match m in Token.Matches(text))
            {
                if (m.Index > pos) runs.Add(new Run { Text = text.Substring(pos, m.Index - pos) });
                if (m.Groups[1].Success) runs.Add(new Run { Text = m.Groups[1].Value, Bold = true });
                else if (m.Groups[2].Success) runs.Add(new Run { Text = m.Groups[2].Value, Italic = true });
                else runs.Add(new Run { Text = m.Groups[3].Value, Link = m.Groups[4].Value });
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) runs.Add(new Run { Text = text.Substring(pos) });
            return runs;
        }

        public static string PlainText(string text) => string.Concat(ParseInline(text).Select(r => r.Text));

        // ─── The Word document ──────────────────────────────────────

        /// <summary>
        /// Writes the document in the project's source language to
        /// <paramref name="path"/>: headings as Word headings, lists as Word
        /// lists, tables as tables, bold, italic and a hyperlink as runs, so
        /// Studio shows them as inline tags.
        /// </summary>
        public static void WriteDocx(string path, ExampleData data, ExampleDirection d)
        {
            using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body());
                AddStyles(main);
                AddNumbering(main);
                var body = main.Document.Body;

                foreach (var b in data.Document)
                {
                    switch (b.Type)
                    {
                        case "title": body.Append(Para(main, BlockText(data, d, b), "Title")); break;
                        case "subtitle": body.Append(Para(main, BlockText(data, d, b), "Subtitle")); break;
                        case "heading": body.Append(Para(main, BlockText(data, d, b), "Heading1")); break;
                        case "para": body.Append(Para(main, BlockText(data, d, b), null)); break;
                        case "bullets":
                        case "steps":
                            int numId = b.Type == "bullets" ? 1 : NewStepsInstance(main);
                            foreach (var i in b.Items ?? new List<ExampleBlock>())
                            {
                                var p = Para(main, BlockText(data, d, i), "ListParagraph");
                                p.ParagraphProperties.Append(new NumberingProperties(
                                    new NumberingLevelReference { Val = 0 }, new NumberingId { Val = numId }));
                                body.Append(p);
                            }
                            break;
                        case "table": body.Append(BuildTable(main, data, d, b)); body.Append(new Paragraph()); break;
                    }
                }
                body.Append(new SectionProperties(
                    new PageSize { Width = 11906U, Height = 16838U },
                    new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U }));
                main.Document.Save();
            }
        }

        private static string BlockText(ExampleData data, ExampleDirection d, ExampleBlock b) => SourceText(data, d, b.Id, b.Text);

        private static Paragraph Para(MainDocumentPart main, string text, string style)
        {
            var p = new Paragraph(new ParagraphProperties());
            if (style != null) p.ParagraphProperties.Append(new ParagraphStyleId { Val = style });
            foreach (var r in ParseInline(text))
            {
                var rp = new RunProperties();
                if (r.Bold) rp.Append(new Bold());
                if (r.Italic) rp.Append(new Italic());
                var run = new DocumentFormat.OpenXml.Wordprocessing.Run();
                if (rp.HasChildren) run.Append(rp);
                run.Append(new Text(r.Text) { Space = SpaceProcessingModeValues.Preserve });
                if (r.Link != null)
                {
                    var rel = main.AddHyperlinkRelationship(new Uri(r.Link, UriKind.Absolute), true);
                    run.RunProperties = run.RunProperties ?? new RunProperties();
                    run.RunProperties.Append(new RunStyle { Val = "Hyperlink" });
                    p.Append(new Hyperlink(run) { Id = rel.Id });
                }
                else p.Append(run);
            }
            return p;
        }

        private static Table BuildTable(MainDocumentPart main, ExampleData data, ExampleDirection d, ExampleBlock b)
        {
            var t = new Table(new TableProperties(
                new TableStyle { Val = "TableGrid" },
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));
            int columns = (b.Rows ?? new List<List<ExampleBlock>>()).Select(r => r.Count).DefaultIfEmpty(1).Max();
            var grid = new TableGrid();
            for (int i = 0; i < columns; i++) grid.Append(new GridColumn { Width = (9026 / columns).ToString(CultureInfo.InvariantCulture) });
            t.Append(grid);
            bool first = true;
            foreach (var row in b.Rows ?? new List<List<ExampleBlock>>())
            {
                var tr = new TableRow();
                if (first && b.Header) tr.Append(new TableRowProperties(new TableHeader()));
                foreach (var cell in row)
                {
                    var p = Para(main, BlockText(data, d, cell), null);
                    if (first && b.Header)
                        foreach (var r in p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>())
                        {
                            r.RunProperties = r.RunProperties ?? new RunProperties();
                            r.RunProperties.Append(new Bold());
                        }
                    tr.Append(new TableCell(p));
                }
                t.Append(tr);
                first = false;
            }
            return t;
        }

        private static void AddStyles(MainDocumentPart main)
        {
            var part = main.AddNewPart<StyleDefinitionsPart>();
            Style S(string id, string name, StyleValues type, int sizeHalfPts, bool bold, string basedOn = null)
            {
                var s = new Style { Type = type, StyleId = id };
                s.Append(new StyleName { Val = name });
                if (basedOn != null) s.Append(new BasedOn { Val = basedOn });
                if (type == StyleValues.Paragraph)
                    s.Append(new StyleParagraphProperties(new SpacingBetweenLines { After = "120" }));
                var rp = new StyleRunProperties();
                if (bold) rp.Append(new Bold());
                if (sizeHalfPts > 0) rp.Append(new FontSize { Val = sizeHalfPts.ToString(CultureInfo.InvariantCulture) });
                s.Append(rp);
                return s;
            }
            var normal = S("Normal", "Normal", StyleValues.Paragraph, 21, false);
            normal.Default = true;
            normal.StyleRunProperties.PrependChild(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" });
            var link = new Style { Type = StyleValues.Character, StyleId = "Hyperlink" };
            link.Append(new StyleName { Val = "Hyperlink" });
            link.Append(new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }));
            var grid = new Style { Type = StyleValues.Table, StyleId = "TableGrid" };
            grid.Append(new StyleName { Val = "Table Grid" });
            grid.Append(new StyleTableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 }, new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
            part.Styles = new Styles(normal,
                S("Title", "Title", StyleValues.Paragraph, 40, true, "Normal"),
                S("Subtitle", "Subtitle", StyleValues.Paragraph, 28, false, "Normal"),
                S("Heading1", "heading 1", StyleValues.Paragraph, 28, true, "Normal"),
                S("ListParagraph", "List Paragraph", StyleValues.Paragraph, 0, false, "Normal"),
                link, grid);
        }

        /// <summary>Numbering: abstract 0 = bullets (instance 1), abstract 1 =
        /// decimal steps; every steps list gets its own instance so it starts at 1.</summary>
        private static void AddNumbering(MainDocumentPart main)
        {
            var part = main.AddNewPart<NumberingDefinitionsPart>();
            AbstractNum Abs(int id, NumberFormatValues fmt, string text) => new AbstractNum(
                new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = fmt }, new LevelText { Val = text },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(new Indentation { Left = "720", Hanging = "360" })) { LevelIndex = 0 })
            { AbstractNumberId = id };
            part.Numbering = new Numbering(
                Abs(0, NumberFormatValues.Bullet, "•"),
                Abs(1, NumberFormatValues.Decimal, "%1."),
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
        }

        private static int NewStepsInstance(MainDocumentPart main)
        {
            var n = main.NumberingDefinitionsPart.Numbering;
            int id = n.Elements<NumberingInstance>().Max(i => i.NumberID.Value) + 1;
            n.Append(new NumberingInstance(new AbstractNumId { Val = 1 },
                new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 }) { NumberID = id });
            return id;
        }
    }
}
