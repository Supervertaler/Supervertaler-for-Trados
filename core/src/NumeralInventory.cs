using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Reference numerals cited in a patent's text, and how they reconcile
    /// against the drawings.
    ///
    /// <para>Step 2 of the reference-image feature, and useful before any of the
    /// rest exists. Extracting the numerals is cheap and exact, and it turns
    /// "describe these drawings" into a task that can be CHECKED: a vision pass
    /// handed a checklist can be told it missed something, where one asked an
    /// open question can only be believed.</para>
    ///
    /// <para>It also stands alone as QA. "Numerals in your drawings that never
    /// appear in the text, and vice versa" is a real finding for a patent
    /// translator — usually a drafting defect in the source, occasionally a
    /// missed sentence in the translation.</para>
    /// </summary>
    public static class NumeralInventory
    {
        /// <summary>
        /// Matches parenthesised numerals as patents cite them: <c>(12)</c>, and
        /// lists like <c>(12, 14, 16)</c>.
        ///
        /// <para>Bounded to 1-3 digits deliberately. Longer runs of digits in
        /// brackets are dates, standards references and claim counts, not part
        /// numerals, and admitting them fills the inventory with noise that the
        /// reconciliation then reports as "missing from the drawings".</para>
        /// </summary>
        private static readonly Regex NumeralRe = new Regex(
            @"\((\d{1,3}(?:\s*,\s*\d{1,3})*)\)", RegexOptions.Compiled);

        /// <summary>
        /// The same part written "N°7" rather than "(7)". Not a separate class:
        /// on one real job "Scharnierpunt tussen onderdelen N°2 en N°3" means parts 2
        /// and 3, the very ones cited as (2) and (3) elsewhere. Kept apart it
        /// would list the same part twice under two spellings.
        /// </summary>
        private static readonly Regex NumeroRe = new Regex(
            @"\bN[\u00B0\u00BA]\s?(\d{1,3})\b", RegexOptions.Compiled);

        /// <summary>
        /// Lettered points: (A), and comma lists like (G,H) - the letter
        /// analogue of (12, 14). SINGLE letters only, deliberately: widening
        /// this to any letters in brackets also catches (TPE) and (PU), which
        /// are material abbreviations, and (nog), which is a Dutch word.
        /// </summary>
        private static readonly Regex LetterPointRe = new Regex(
            @"\(([A-Z](?:\s*,\s*[A-Z])*)\)", RegexOptions.Compiled);

        /// <summary>
        /// A label series such as ST 01: two or three capitals then two digits.
        /// The separator may be an ordinary space or U+00A0 - real documents use both
        /// for ST 03, which a naive scan reports as two distinct signs.
        /// </summary>
        private static readonly Regex LabelSeriesRe = new Regex(
            @"\b([A-Z]{2,3})[\s\u00A0]?(\d{2})\b", RegexOptions.Compiled);

        /// <summary>
        /// Every distinct numeral cited in <paramref name="text"/>, ascending,
        /// with the sentences that cite each one.
        ///
        /// <para>The sentences matter as much as the numbers: they are what lets a
        /// vision pass tie a numeral to a part without guessing, and what lets a
        /// human check the result.</para>
        /// </summary>
        public static NumeralReport Extract(IEnumerable<string> segments)
        {
            var report = new NumeralReport();
            if (segments == null) return report;

            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment)) continue;

                foreach (Match m in NumeralRe.Matches(segment))
                {
                    foreach (var part in m.Groups[1].Value.Split(','))
                    {
                        if (!int.TryParse(part.Trim(), out var n)) continue;

                        if (!report.Citations.TryGetValue(n, out var list))
                        {
                            list = new List<string>();
                            report.Citations[n] = list;
                        }
                        // One citation per numeral per segment; a numeral repeated
                        // in a sentence is one use of it, not several.
                        if (!list.Contains(segment)) list.Add(segment);
                    }
                }

                // "N\u00b07" is part 7, the same part "(7)" names. Merged into the
                // numerals rather than kept apart, or the same part appears
                // twice under two spellings.
                foreach (Match m in NumeroRe.Matches(segment))
                {
                    int n;
                    if (!int.TryParse(m.Groups[1].Value, out n)) continue;
                    List<string> list;
                    if (!report.Citations.TryGetValue(n, out list))
                    {
                        list = new List<string>();
                        report.Citations[n] = list;
                    }
                    if (!list.Contains(segment)) list.Add(segment);
                }

                foreach (Match m in LetterPointRe.Matches(segment))
                    foreach (var part in m.Groups[1].Value.Split(','))
                        AddSign(report.LetterPoints, part.Trim(), segment);

                // Normalise the separator: one document writes ST 03 with an
                // ordinary space in one place and U+00A0 in another, which a
                // naive scan counts as two distinct signs.
                foreach (Match m in LabelSeriesRe.Matches(segment))
                    AddSign(report.LabelSeries,
                        m.Groups[1].Value + " " + m.Groups[2].Value, segment);
            }

            return report;
        }

        /// <summary>One citation per sign per segment.</summary>
        private static void AddSign(SortedDictionary<string, List<string>> map,
            string sign, string segment)
        {
            if (string.IsNullOrEmpty(sign)) return;
            List<string> list;
            if (!map.TryGetValue(sign, out list))
            {
                list = new List<string>();
                map[sign] = list;
            }
            if (!list.Contains(segment)) list.Add(segment);
        }

        /// <summary>
        /// Compares numerals cited in the text against numerals found in the
        /// drawings, and reports the three-way difference.
        ///
        /// <para>Reporting the differences is the point. A pass that quietly
        /// dropped the numerals it could not place would look identical to one
        /// that placed them all.</para>
        /// </summary>
        /// <param name="inText">Numerals extracted from the source text.</param>
        /// <param name="inDrawings">Numerals a vision pass reported seeing, or
        /// null when no vision pass has run.</param>
        public static ReconciliationResult Reconcile(
            IEnumerable<int> inText, IEnumerable<int> inDrawings)
        {
            var text = new SortedSet<int>(inText ?? Enumerable.Empty<int>());
            var drawn = new SortedSet<int>(inDrawings ?? Enumerable.Empty<int>());

            return new ReconciliationResult
            {
                InBoth = new SortedSet<int>(text.Intersect(drawn)),
                // Cited but not drawn: either the vision pass is incomplete, or
                // the numeral genuinely is not in any figure.
                TextOnly = new SortedSet<int>(text.Except(drawn)),
                // Drawn but never cited: a source defect worth surfacing. The
                // worked example (Acme (PROJ-001) had exactly one.
                DrawingsOnly = new SortedSet<int>(drawn.Except(text))
            };
        }

        /// <summary>Renders the report as Markdown for the chat panel or a file.</summary>
        public static string Format(NumeralReport report, ReconciliationResult reconciliation)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Reference numerals");
            sb.AppendLine();

            if (report == null || report.Citations.Count == 0)
            {
                sb.AppendLine("No parenthesised reference numerals found in the source text.");
                return sb.ToString().TrimEnd();
            }

            sb.AppendLine("**" + report.Citations.Count + " distinct numerals** cited in the source, "
                        + "from " + report.Numerals.Min() + " to " + report.Numerals.Max() + ".");
            sb.AppendLine();

            if (reconciliation != null)
            {
                if (reconciliation.DrawingsOnly.Count > 0)
                {
                    sb.AppendLine("### In the drawings but never cited in the text");
                    sb.AppendLine();
                    sb.AppendLine("Usually a drafting defect in the source, and worth raising with the client: "
                                + string.Join(", ", reconciliation.DrawingsOnly));
                    sb.AppendLine();
                }

                if (reconciliation.TextOnly.Count > 0)
                {
                    sb.AppendLine("### Cited in the text but not found in the drawings");
                    sb.AppendLine();
                    sb.AppendLine("Either the figure analysis is incomplete, or these numerals genuinely "
                                + "appear in no figure: " + string.Join(", ", reconciliation.TextOnly));
                    sb.AppendLine();
                }

                if (reconciliation.DrawingsOnly.Count == 0 && reconciliation.TextOnly.Count == 0)
                {
                    sb.AppendLine("Every numeral in the text appears in the drawings, and vice versa.");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("### Citations");
            sb.AppendLine();
            // A list rather than a table: this is read in a narrow docked
            // panel, where three columns are the wrong shape however they are
            // rendered, and repeating the column headers on every row is most
            // of the noise.
            foreach (var n in report.Numerals)
            {
                var cites = report.Citations[n];
                var first = cites.Count > 0 ? cites[0] : "";
                first = WindowAround(first, n, 160);
                first = first.Replace("\r", " ").Replace("\n", " ");

                sb.AppendLine("**(" + n + ")**  \u00b7  cited " + cites.Count
                    + (cites.Count == 1 ? " time" : " times"));
                sb.AppendLine();
                sb.AppendLine(first);
                sb.AppendLine();
            }

            AppendSignSection(sb, "Lettered points", report.LetterPoints);
            AppendSignSection(sb, "Label-series signs", report.LabelSeries);

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Render one class of non-numeral reference sign. Omitted entirely when
        /// the document uses none, rather than printing an empty heading.
        /// </summary>
        private static void AppendSignSection(StringBuilder sb, string title,
            SortedDictionary<string, List<string>> map)
        {
            if (map == null || map.Count == 0) return;

            sb.AppendLine();
            sb.AppendLine("### " + title);
            sb.AppendLine();
            sb.AppendLine("**" + map.Count + " distinct**: "
                + string.Join(", ", map.Keys) + ".");
            sb.AppendLine();
        }

        /// <summary>
        /// A <paramref name="width"/>-character window of <paramref name="text"/>
        /// centred on where numeral <paramref name="n"/> is cited, with an
        /// ellipsis on whichever side was cut.
        ///
        /// <para>Truncating from the start instead put the numeral past the cut
        /// in 4 of 20 rows on a real patent, so the row for numeral 15 showed a
        /// snippet containing only 9 and 10 - a table you had to cross-reference
        /// against the document to read.</para>
        /// </summary>
        private static string WindowAround(string text, int n, int width)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= width) return text ?? "";

            // Where is this numeral actually cited? Parenthesised, possibly in a
            // list: (15) or (12, 15, 18).
            var hit = Regex.Match(text, @"\(\s*\d{1,3}(?:\s*,\s*\d{1,3})*\s*\)");
            int at = -1;
            while (hit.Success)
            {
                foreach (var part in hit.Value.Trim('(', ')').Split(','))
                {
                    int v;
                    if (int.TryParse(part.Trim(), out v) && v == n) { at = hit.Index; break; }
                }
                if (at >= 0) break;
                hit = hit.NextMatch();
            }

            if (at < 0) return text.Substring(0, width - 3) + "\u2026";

            var start = Math.Max(0, at - width / 2);
            var len = Math.Min(width, text.Length - start);
            var slice = text.Substring(start, len);

            if (start > 0) slice = "\u2026" + slice;
            if (start + len < text.Length) slice = slice + "\u2026";
            return slice;
        }
    }

    /// <summary>Numerals cited in the source, with the sentences citing them.</summary>
    public class NumeralReport
    {
        /// <summary>Numeral → the distinct segments citing it, in document order.</summary>
        public SortedDictionary<int, List<string>> Citations { get; }
            = new SortedDictionary<int, List<string>>();

        /// <summary>Lettered points – (A), (B) … – to the segments citing them.</summary>
        public SortedDictionary<string, List<string>> LetterPoints { get; }
            = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>Label-series signs – ST 01, ST 02 … – to the segments citing
        /// them, normalised to a single ordinary space.</summary>
        public SortedDictionary<string, List<string>> LabelSeries { get; }
            = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>Every cited numeral, ascending.</summary>
        public IEnumerable<int> Numerals => Citations.Keys;

        public bool HasAny => Citations.Count > 0;
    }

    /// <summary>The three-way split between text and drawings.</summary>
    public class ReconciliationResult
    {
        public SortedSet<int> InBoth { get; set; } = new SortedSet<int>();

        /// <summary>Cited in the text, not seen in any drawing.</summary>
        public SortedSet<int> TextOnly { get; set; } = new SortedSet<int>();

        /// <summary>Seen in a drawing, never cited in the text — a source defect.</summary>
        public SortedSet<int> DrawingsOnly { get; set; } = new SortedSet<int>();

        public bool IsClean => TextOnly.Count == 0 && DrawingsOnly.Count == 0;
    }
}
