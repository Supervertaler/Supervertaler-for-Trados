using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Supervertaler.Core
{
    /// <summary>
    /// One paragraph of a Word document as the structure reader sees it (Trados #109,
    /// memoQ #7): where it sits in <c>word/document.xml</c>, its text, and the list
    /// marker Word would render in front of it.
    /// </summary>
    public sealed class DocxParagraph
    {
        /// <summary>0-based position among the document's paragraphs, in document order.</summary>
        public int Index { get; set; }

        /// <summary>
        /// Character offset of the paragraph's <c>&lt;w:p</c> element in the decoded
        /// <c>word/document.xml</c>. Trados Studio publishes the same number as the
        /// <c>StartsAt</c> metadata of every paragraph unit's location context, so a
        /// Studio paragraph unit maps to a paragraph here by this key alone.
        /// </summary>
        public int StartOffset { get; set; }

        /// <summary>Offset of the paragraph's closing tag (Studio's <c>EndsAt</c>).</summary>
        public int EndOffset { get; set; }

        /// <summary>The paragraph's text, run formatting dropped, XML entities decoded.</summary>
        public string Text { get; set; }

        /// <summary>Paragraph style id, or null.</summary>
        public string StyleId { get; set; }

        /// <summary>The <c>w:numId</c> in force (own or from the style), or null when unnumbered.</summary>
        public string NumId { get; set; }

        /// <summary>The list level (<c>w:ilvl</c>), 0-based.</summary>
        public int Level { get; set; }

        /// <summary>
        /// Exactly what Word renders in front of the paragraph - <c>9.</c>, <c>e)</c>,
        /// <c>•</c> - or null for an unnumbered paragraph.
        /// </summary>
        public string Marker { get; set; }

        /// <summary>True when the marker is a bullet rather than a counted number or letter.</summary>
        public bool IsBullet { get; set; }

        /// <summary>
        /// The paragraph's own character ranges in <c>document.xml</c>: its element
        /// minus the paragraphs nested inside it (a text box's paragraphs are their
        /// own). Start/end pairs, in order. Filled by the scanner.
        /// </summary>
        internal List<int> OwnSpans { get; } = new List<int>();
    }

    /// <summary>
    /// Reads Word's list numbering out of a .docx and renders each paragraph's marker
    /// the way Word does at display time (Trados #109, memoQ #7).
    ///
    /// <para>Word stores no <c>a)</c> in the text: a paragraph carries a <c>w:numPr</c>
    /// (list id and level), the list id resolves through <c>numbering.xml</c> to an
    /// abstract definition per level (number format, level text such as <c>%1)</c>,
    /// start value), and the value is counted in document order with restarts. So a
    /// marker can only be computed for the whole document, never for one paragraph:
    /// <see cref="ReadParagraphs(Stream)"/> always walks everything and callers pick
    /// out what they need afterwards.</para>
    ///
    /// <para>Scale: one pass over <c>document.xml</c> as a string, one dictionary per
    /// numbering definition. A 300-paragraph patent takes milliseconds; a 30,000-
    /// paragraph document is a few hundred milliseconds and the size of the XML in
    /// memory once. Nothing is kept between calls.</para>
    /// </summary>
    public static class DocxStructure
    {
        /// <summary>
        /// Opens a .docx package from a stream. The stream may hold the .docx itself
        /// or a zip that wraps it - Trados Studio embeds the original in the sdlxliff
        /// header as a zip containing the .docx - and one level of wrapping is
        /// unwrapped. Returns null when neither shape is found.
        /// </summary>
        public static ZipArchive OpenPackage(Stream stream)
        {
            if (stream == null) return null;
            ZipArchive outer;
            try { outer = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false); }
            catch (InvalidDataException) { return null; }   // not a zip at all
            if (outer.GetEntry("word/document.xml") != null) return outer;

            // A wrapper: exactly one entry that is itself a package.
            ZipArchiveEntry inner = null;
            foreach (var e in outer.Entries)
            {
                if (e.Length == 0 || e.FullName.EndsWith("/")) continue;
                if (inner != null) { inner = null; break; }
                inner = e;
            }
            if (inner == null) { outer.Dispose(); return null; }

            var ms = new MemoryStream((int)Math.Min(inner.Length, int.MaxValue));
            using (var s = inner.Open()) s.CopyTo(ms);
            outer.Dispose();
            ms.Position = 0;
            ZipArchive unwrapped;
            try { unwrapped = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false); }
            catch (InvalidDataException) { ms.Dispose(); return null; }   // the one entry is not a zip
            if (unwrapped.GetEntry("word/document.xml") != null) return unwrapped;
            unwrapped.Dispose();
            return null;
        }

        /// <summary>Every paragraph of the document in order, markers rendered. Null when the stream is not a Word package.</summary>
        public static List<DocxParagraph> ReadParagraphs(Stream stream)
        {
            using (var pkg = OpenPackage(stream))
            {
                return pkg == null ? null : ReadParagraphs(pkg);
            }
        }

        /// <summary>Every paragraph of the document in order, markers rendered.</summary>
        public static List<DocxParagraph> ReadParagraphs(ZipArchive package)
        {
            var doc = ReadEntry(package, "word/document.xml");
            if (doc == null) return null;
            var numbering = DocxNumbering.Load(ReadEntry(package, "word/numbering.xml"), ReadEntry(package, "word/styles.xml"));
            var paragraphs = ParseParagraphs(doc);
            numbering.Apply(paragraphs);
            return paragraphs;
        }

        /// <summary>True when at least one paragraph carries a marker.</summary>
        public static bool HasMarkers(IEnumerable<DocxParagraph> paragraphs)
        {
            if (paragraphs == null) return false;
            foreach (var p in paragraphs) if (p.Marker != null) return true;
            return false;
        }

        internal static string ReadEntry(ZipArchive package, string name)
        {
            var entry = package?.GetEntry(name);
            if (entry == null) return null;
            using (var s = entry.Open())
            using (var r = new StreamReader(s, Encoding.UTF8, true))
                return r.ReadToEnd();
        }

        // ---- document.xml ------------------------------------------------------------------

        private static readonly Regex ParagraphTag = new Regex("<(/?)w:p(?=[ >/])", RegexOptions.Compiled);
        private static readonly Regex TextRun = new Regex("<w:t(?:\\s[^>]*)?>([^<]*)</w:t>|<w:tab/>|<w:br/>", RegexOptions.Compiled);
        private static readonly Regex NumIdAttr = new Regex("<w:numId\\s+w:val=\"(\\d+)\"", RegexOptions.Compiled);
        private static readonly Regex LevelAttr = new Regex("<w:ilvl\\s+w:val=\"(\\d+)\"", RegexOptions.Compiled);
        private static readonly Regex StyleAttr = new Regex("<w:pStyle\\s+w:val=\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex PropChange = new Regex("<w:pPrChange\\b.*?</w:pPrChange>", RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Paragraph starts, ends, properties and text from the raw XML. A scanner
        /// rather than an XML reader because the offsets are the join key to Studio
        /// and an XML reader does not report character positions. Paragraphs inside
        /// text boxes nest inside a run of the paragraph that holds the box; the
        /// stack keeps each paragraph's own range.
        /// </summary>
        internal static List<DocxParagraph> ParseParagraphs(string xml)
        {
            var result = new List<DocxParagraph>();
            var open = new Stack<DocxParagraph>();
            foreach (Match m in ParagraphTag.Matches(xml))
            {
                bool closing = m.Groups[1].Value == "/";
                if (!closing)
                {
                    int tagEnd = xml.IndexOf('>', m.Index);
                    if (tagEnd < 0) break;
                    var p = new DocxParagraph { Index = result.Count, StartOffset = m.Index };
                    result.Add(p);
                    if (xml[tagEnd - 1] == '/')
                    {
                        // <w:p/>: an empty paragraph, no properties
                        p.EndOffset = tagEnd + 1;
                        p.Text = "";
                        continue;
                    }
                    ReadProperties(xml, tagEnd + 1, p);
                    open.Push(p);
                }
                else if (open.Count > 0)
                {
                    var p = open.Pop();
                    p.EndOffset = m.Index;
                    p.Text = ExtractText(xml, p, result);
                }
            }
            while (open.Count > 0) { var p = open.Pop(); p.EndOffset = xml.Length; p.Text = ExtractText(xml, p, result); }
            return DropFallbacks(xml, result);
        }

        private static readonly Regex FallbackTag = new Regex("<(/?)mc:Fallback[ >]", RegexOptions.Compiled);

        /// <summary>
        /// Word writes some content twice - DrawingML in mc:Choice and legacy VML in
        /// mc:Fallback - and renders the Choice. Paragraphs in a Fallback are the
        /// same paragraphs again; counting them doubled a document's figure labels
        /// and put its numbering out by the duplicates. Dropped here, after the
        /// texts were taken (a host paragraph's text already excludes them as
        /// nested), and the survivors renumbered.
        /// </summary>
        private static List<DocxParagraph> DropFallbacks(string xml, List<DocxParagraph> all)
        {
            var ranges = new List<int>();   // start,end pairs
            var stack = new Stack<int>();
            foreach (Match m in FallbackTag.Matches(xml))
            {
                if (m.Groups[1].Value != "/") stack.Push(m.Index);
                else if (stack.Count > 0) { int s = stack.Pop(); if (stack.Count == 0) { ranges.Add(s); ranges.Add(m.Index); } }
            }
            if (ranges.Count == 0) return all;

            var kept = new List<DocxParagraph>(all.Count);
            foreach (var p in all)
            {
                bool inside = false;
                for (int i = 0; i < ranges.Count; i += 2)
                    if (p.StartOffset >= ranges[i] && p.StartOffset < ranges[i + 1]) { inside = true; break; }
                if (inside) continue;
                p.Index = kept.Count;
                kept.Add(p);
            }
            return kept;
        }

        private static void ReadProperties(string xml, int from, DocxParagraph p)
        {
            int i = from;
            while (i < xml.Length && char.IsWhiteSpace(xml[i])) i++;
            if (string.CompareOrdinal(xml, i, "<w:pPr", 0, 6) != 0) return;
            int tagEnd = xml.IndexOf('>', i);
            if (tagEnd < 0) return;
            if (xml[tagEnd - 1] == '/') return;   // <w:pPr/>
            // The block can nest a second w:pPr inside w:pPrChange (the properties
            // before a tracked change), so the first closing tag is not necessarily ours.
            int end = -1, depth = 1, pos = tagEnd + 1;
            while (depth > 0)
            {
                int nextOpen = xml.IndexOf("<w:pPr", pos, StringComparison.Ordinal);
                int nextClose = xml.IndexOf("</w:pPr>", pos, StringComparison.Ordinal);
                if (nextClose < 0) return;
                if (nextOpen >= 0 && nextOpen < nextClose)
                {
                    // "<w:pPr" is also the start of "<w:pPrChange"; only the exact
                    // element name, and not a self-closing one, opens a level.
                    int nameEnd = nextOpen + 6;
                    bool isPPr = nameEnd < xml.Length && (xml[nameEnd] == '>' || xml[nameEnd] == ' ' || xml[nameEnd] == '/');
                    int close = xml.IndexOf('>', nextOpen);
                    bool selfClosing = close > 0 && xml[close - 1] == '/';
                    if (isPPr && !selfClosing) depth++;
                    pos = nameEnd;
                }
                else
                {
                    depth--; pos = nextClose + 8;
                    if (depth == 0) end = nextClose;
                }
            }
            var pPr = xml.Substring(i, end - i);
            // A tracked change of paragraph properties keeps the OLD numbering inside
            // w:pPrChange; only the live properties count.
            pPr = PropChange.Replace(pPr, "");

            var style = StyleAttr.Match(pPr);
            if (style.Success) p.StyleId = XmlUnescape(style.Groups[1].Value);

            int numPr = pPr.IndexOf("<w:numPr", StringComparison.Ordinal);
            if (numPr >= 0)
            {
                int numPrEnd = pPr.IndexOf("</w:numPr>", numPr, StringComparison.Ordinal);
                var block = numPrEnd > numPr ? pPr.Substring(numPr, numPrEnd - numPr) : pPr.Substring(numPr);
                var id = NumIdAttr.Match(block);
                if (id.Success) p.NumId = id.Groups[1].Value;
                var lvl = LevelAttr.Match(block);
                if (lvl.Success) p.Level = int.Parse(lvl.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        private static string ExtractText(string xml, DocxParagraph p, List<DocxParagraph> all)
        {
            // Text of this paragraph only: runs of paragraphs nested inside it (text
            // boxes) belong to those paragraphs. The same spans serve the image
            // extractor, so they are kept on the paragraph.
            p.OwnSpans.Clear();
            int pos = p.StartOffset;
            int end = p.EndOffset;
            for (int k = p.Index + 1; k < all.Count; k++)
            {
                var nested = all[k];
                if (nested.StartOffset >= end) break;
                if (nested.EndOffset <= 0 || nested.EndOffset > end) continue;
                if (nested.StartOffset > pos) { p.OwnSpans.Add(pos); p.OwnSpans.Add(nested.StartOffset); }
                pos = Math.Max(pos, nested.EndOffset);
            }
            if (end > pos) { p.OwnSpans.Add(pos); p.OwnSpans.Add(end); }

            var sb = new StringBuilder();
            for (int i = 0; i < p.OwnSpans.Count; i += 2) AppendText(xml, p.OwnSpans[i], p.OwnSpans[i + 1], sb);
            return sb.ToString().Trim();
        }

        private static void AppendText(string xml, int from, int to, StringBuilder sb)
        {
            if (to <= from) return;
            foreach (Match m in TextRun.Matches(xml.Substring(from, to - from)))
            {
                if (m.Value.StartsWith("<w:tab", StringComparison.Ordinal)) sb.Append('\t');
                else if (m.Value.StartsWith("<w:br", StringComparison.Ordinal)) sb.Append('\n');
                else sb.Append(XmlUnescape(m.Groups[1].Value));
            }
        }

        internal static string XmlUnescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('&') < 0) return s;
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'")
                    .Replace("&#xA;", "\n").Replace("&#xa;", "\n").Replace("&#10;", "\n").Replace("&amp;", "&");
        }
    }

    /// <summary>
    /// The numbering definitions of one document (<c>numbering.xml</c> plus the list
    /// styles in <c>styles.xml</c>) and the stateful counter that renders markers
    /// from them. Load once per document; <see cref="Apply"/> walks the paragraphs
    /// in order and must be given all of them - a restart at claim 11 depends on the
    /// paragraphs before it, and a list that continues across two claims carries its
    /// letters over (claim 9 <c>a)</c>-<c>f)</c>, claim 10 <c>g)</c>-<c>m)</c>) only
    /// when the whole sequence is counted.
    /// </summary>
    public sealed class DocxNumbering
    {
        private sealed class LevelDef
        {
            public string Format = "decimal";
            public string Text = "%1.";
            public int Start = 1;
        }

        private sealed class NumDef
        {
            public string AbstractId;
            /// <summary>Level → start override from w:lvlOverride/w:startOverride.</summary>
            public Dictionary<int, int> StartOverrides = new Dictionary<int, int>();
        }

        private readonly Dictionary<string, Dictionary<int, LevelDef>> _abstract = new Dictionary<string, Dictionary<int, LevelDef>>(StringComparer.Ordinal);
        private readonly Dictionary<string, NumDef> _nums = new Dictionary<string, NumDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, int>> _styleNumbering = new Dictionary<string, KeyValuePair<string, int>>(StringComparer.Ordinal);

        /// <summary>True when the document defines at least one list.</summary>
        public bool HasLists => _nums.Count > 0;

        public static DocxNumbering Load(string numberingXml, string stylesXml)
        {
            var n = new DocxNumbering();
            if (!string.IsNullOrEmpty(numberingXml)) n.ParseNumbering(numberingXml);
            if (!string.IsNullOrEmpty(stylesXml)) n.ParseStyles(stylesXml);
            return n;
        }

        /// <summary>The list a style puts its paragraphs on, or null.</summary>
        public bool TryGetStyleNumbering(string styleId, out string numId, out int level)
        {
            numId = null; level = 0;
            if (styleId == null || !_styleNumbering.TryGetValue(styleId, out var kv)) return false;
            numId = kv.Key; level = kv.Value;
            return true;
        }

        /// <summary>
        /// Renders every paragraph's marker in document order. Paragraphs with no
        /// numbering of their own take their style's; <c>numId 0</c> means "none".
        /// </summary>
        public void Apply(IList<DocxParagraph> paragraphs)
        {
            var counter = new Counter(this);
            foreach (var p in paragraphs)
            {
                if (p == null) continue;
                var numId = p.NumId;
                int level = p.Level;
                if (numId == null && p.StyleId != null && TryGetStyleNumbering(p.StyleId, out var sn, out var sl))
                {
                    numId = sn; level = sl;
                }
                if (numId == null || numId == "0" || !_nums.ContainsKey(numId))
                {
                    p.Marker = null; p.IsBullet = false;
                    continue;
                }
                p.NumId = numId; p.Level = level;
                p.Marker = counter.Next(numId, level, out var bullet);
                p.IsBullet = bullet;
            }
        }

        /// <summary>
        /// The stateful part: current values per abstract definition and level. Two
        /// list ids that share an abstract definition continue one sequence, as in
        /// Word, unless the list id overrides the start.
        /// </summary>
        private sealed class Counter
        {
            private readonly DocxNumbering _n;
            private readonly Dictionary<string, Dictionary<int, int>> _values = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            private readonly HashSet<string> _seenNums = new HashSet<string>(StringComparer.Ordinal);

            public Counter(DocxNumbering n) { _n = n; }

            public string Next(string numId, int level, out bool isBullet)
            {
                isBullet = false;
                var num = _n._nums[numId];
                if (!_n._abstract.TryGetValue(num.AbstractId, out var levels)) levels = new Dictionary<int, LevelDef>();
                if (!_values.TryGetValue(num.AbstractId, out var current))
                {
                    current = new Dictionary<int, int>();
                    _values[num.AbstractId] = current;
                }
                if (_seenNums.Add(numId))
                {
                    // First paragraph on this list id: its start overrides take effect.
                    foreach (var kv in num.StartOverrides) current[kv.Key] = kv.Value - 1;
                }

                var def = Level(levels, level);
                if (def.Format == "bullet")
                {
                    isBullet = true;
                    return BulletText(def.Text);
                }
                if (def.Format == "none") return null;

                if (!current.TryGetValue(level, out var v)) v = def.Start - 1;
                current[level] = v + 1;
                // A new item at this level restarts every deeper level.
                var deeper = new List<int>();
                foreach (var k in current.Keys) if (k > level) deeper.Add(k);
                foreach (var k in deeper) current.Remove(k);

                var text = def.Text ?? "";
                for (int l = 0; l <= level; l++)
                {
                    var ld = Level(levels, l);
                    int lv = current.TryGetValue(l, out var cv) ? cv : ld.Start;
                    text = text.Replace("%" + (l + 1).ToString(CultureInfo.InvariantCulture), FormatValue(ld.Format, lv));
                }
                return text;
            }

            private static LevelDef Level(Dictionary<int, LevelDef> levels, int level)
            {
                return levels.TryGetValue(level, out var d) ? d : new LevelDef();
            }
        }

        // ---- rendering --------------------------------------------------------------------

        internal static string FormatValue(string format, int value)
        {
            switch (format)
            {
                case "lowerLetter": return Letters(value, 'a');
                case "upperLetter": return Letters(value, 'A');
                case "lowerRoman": return Roman(value).ToLowerInvariant();
                case "upperRoman": return Roman(value);
                case "decimalZero": return value < 10 ? "0" + value : value.ToString(CultureInfo.InvariantCulture);
                case "none": return "";
                default: return value.ToString(CultureInfo.InvariantCulture);   // decimal and every format Word renders as digits
            }
        }

        private static string Letters(int value, char first)
        {
            if (value < 1) return "";
            // Word: a..z, then aa..zz, then aaa - the same letter repeated, not base-26.
            var c = (char)(first + (value - 1) % 26);
            return new string(c, (value - 1) / 26 + 1);
        }

        private static string Roman(int value)
        {
            if (value < 1 || value > 3999) return value.ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            int[] v = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] s = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            for (int i = 0; i < v.Length; i++) while (value >= v[i]) { sb.Append(s[i]); value -= v[i]; }
            return sb.ToString();
        }

        /// <summary>
        /// Bullet glyphs come from symbol fonts as private-use characters that mean
        /// nothing outside Word; the model gets a plain bullet instead. Real
        /// characters (a hyphen, "o") are kept.
        /// </summary>
        internal static string BulletText(string lvlText)
        {
            if (string.IsNullOrEmpty(lvlText)) return "•";
            var sb = new StringBuilder();
            foreach (var ch in lvlText)
            {
                if (ch >= '\uF000' && ch <= '\uF0FF')
                {
                    switch (ch)
                    {
                        case '\uF0A7': sb.Append('▪'); break;   // small square
                        case '\uF0D8': sb.Append('➢'); break;   // arrowhead
                        case '\uF0FC': sb.Append('✓'); break;   // tick
                        default: sb.Append('•'); break;
                    }
                }
                else sb.Append(ch);
            }
            var t = sb.ToString().Trim();
            return t.Length == 0 ? "•" : t;
        }

        // ---- parsing ----------------------------------------------------------------------

        private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        private void ParseNumbering(string xml)
        {
            using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true }))
            {
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.NamespaceURI != W) continue;
                    if (r.LocalName == "abstractNum")
                    {
                        var id = r.GetAttribute("abstractNumId", W);
                        var levels = new Dictionary<int, LevelDef>();
                        if (id != null) _abstract[id] = levels;
                        if (r.IsEmptyElement) continue;
                        var sub = r.ReadSubtree();
                        while (sub.Read())
                        {
                            if (sub.NodeType == XmlNodeType.Element && sub.NamespaceURI == W && sub.LocalName == "lvl")
                                ReadLevel(sub, levels);
                        }
                    }
                    else if (r.LocalName == "num")
                    {
                        var id = r.GetAttribute("numId", W);
                        var def = new NumDef();
                        if (id != null) _nums[id] = def;
                        if (r.IsEmptyElement) continue;
                        var sub = r.ReadSubtree();
                        int overrideLevel = -1;
                        while (sub.Read())
                        {
                            if (sub.NodeType != XmlNodeType.Element || sub.NamespaceURI != W) continue;
                            switch (sub.LocalName)
                            {
                                case "abstractNumId": def.AbstractId = sub.GetAttribute("val", W); break;
                                case "lvlOverride":
                                    overrideLevel = ParseInt(sub.GetAttribute("ilvl", W), -1);
                                    break;
                                case "startOverride":
                                    if (overrideLevel >= 0) def.StartOverrides[overrideLevel] = ParseInt(sub.GetAttribute("val", W), 1);
                                    break;
                            }
                        }
                    }
                }
            }
            // A list whose abstract definition is missing still counts from 1.
            foreach (var kv in _nums)
                if (kv.Value.AbstractId == null) kv.Value.AbstractId = "num:" + kv.Key;
        }

        private static void ReadLevel(XmlReader lvl, Dictionary<int, LevelDef> levels)
        {
            int level = ParseInt(lvl.GetAttribute("ilvl", W), 0);
            var def = new LevelDef();
            levels[level] = def;
            if (lvl.IsEmptyElement) return;
            var sub = lvl.ReadSubtree();
            while (sub.Read())
            {
                if (sub.NodeType != XmlNodeType.Element || sub.NamespaceURI != W) continue;
                switch (sub.LocalName)
                {
                    case "start": def.Start = ParseInt(sub.GetAttribute("val", W), 1); break;
                    case "numFmt": def.Format = sub.GetAttribute("val", W) ?? "decimal"; break;
                    case "lvlText": def.Text = sub.GetAttribute("val", W) ?? ""; break;
                }
            }
        }

        private void ParseStyles(string xml)
        {
            using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true }))
            {
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element || r.NamespaceURI != W || r.LocalName != "style") continue;
                    var styleId = r.GetAttribute("styleId", W);
                    if (styleId == null || r.IsEmptyElement) continue;
                    var sub = r.ReadSubtree();
                    string numId = null; int level = 0; bool inNumPr = false;
                    while (sub.Read())
                    {
                        if (sub.NodeType == XmlNodeType.Element && sub.NamespaceURI == W)
                        {
                            if (sub.LocalName == "numPr") inNumPr = true;
                            else if (inNumPr && sub.LocalName == "numId") numId = sub.GetAttribute("val", W);
                            else if (inNumPr && sub.LocalName == "ilvl") level = ParseInt(sub.GetAttribute("val", W), 0);
                        }
                        else if (sub.NodeType == XmlNodeType.EndElement && sub.LocalName == "numPr") inNumPr = false;
                    }
                    if (numId != null) _styleNumbering[styleId] = new KeyValuePair<string, int>(numId, level);
                }
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
