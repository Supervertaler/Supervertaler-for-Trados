using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// Pulls the locked-terms table(s) out of a prompt and turns them into a
    /// Supervertaler glossary: tab-separated, <c>source&lt;TAB&gt;target</c>, with
    /// a third column <c>forbidden</c> for banned renderings.
    ///
    /// AutoPrompt writes its PROJECT-SPECIFIC GLOSSARY as a Markdown table —
    /// <c>| source | locked target | notes |</c> — and that table is precisely
    /// the project glossary a terminology check should run against: a dozen
    /// terms chosen for this job, not the thousands of a general glossary
    /// whose other senses flag every paragraph. So the prompt becomes the
    /// glossary, rather than the translator retyping it.
    ///
    /// Notes of the form <c>never "apparatus"</c> yield forbidden rows: the
    /// generator writes those where a rendering is explicitly ruled out, and
    /// they are the part of a glossary a check can enforce absolutely.
    /// </summary>
    public static class PromptGlossaryExtractor
    {
        public sealed class Entry
        {
            public string Source { get; set; }
            public string Target { get; set; }
            public bool Forbidden { get; set; }
            public string Note { get; set; }
        }

        /// <summary>
        /// A row is any line carrying a pipe. Outer pipes are optional because a
        /// real generated glossary frequently has none: the prompt this was
        /// widened for writes
        /// <c>voederadditief | feed additive | Claim term; never "fodder additive"</c>
        /// with no leading pipe and no separator row, and the stricter pattern
        /// rejected the whole table with "no glossary table found".
        ///
        /// Prose is kept out by the header test rather than by punctuation: a
        /// table is only read when its first row names a source and a target
        /// column.
        /// </summary>
        private static readonly Regex TableRow = new Regex(@"^\s*\|?([^|]*\|.*?)\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex SeparatorRow = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex NeverPattern = new Regex(@"\bnever\s+[""“']([^""”']+)[""”']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Markup = new Regex(@"[`*_]", RegexOptions.Compiled);

        /// <summary>
        /// Every table in the prompt whose header names a source and a target
        /// column. Tables under other headings (a defect list, a style table)
        /// are ignored by that test rather than by their section title, so a
        /// prompt written by hand in a different layout still works.
        /// </summary>
        public static List<Entry> Extract(string promptContent)
        {
            var entries = new List<Entry>();
            if (string.IsNullOrWhiteSpace(promptContent)) return entries;

            var lines = promptContent.Replace("\r\n", "\n").Split('\n');
            var i = 0;
            while (i < lines.Length)
            {
                var header = TableRow.Match(lines[i]);
                if (!header.Success || SeparatorRow.IsMatch(lines[i])) { i++; continue; }

                var columns = SplitCells(header.Groups[1].Value).Select(c => c.ToLowerInvariant()).ToList();
                var srcCol = columns.FindIndex(c => c.Contains("source"));
                var tgtCol = columns.FindIndex(c => c.Contains("target"));
                var noteCol = columns.FindIndex(c => c.Contains("note") || c.Contains("comment"));

                // A glossary table names its columns; anything else is not one.
                if (srcCol < 0 || tgtCol < 0) { i++; continue; }

                // The separator is optional. Markdown wants one and the memoQ
                // generator writes one; a model writing the same table by hand
                // often does not, and that is not a reason to lose the glossary.
                i++;
                if (i < lines.Length && SeparatorRow.IsMatch(lines[i])) i++;
                while (i < lines.Length)
                {
                    var row = TableRow.Match(lines[i]);
                    if (!row.Success || SeparatorRow.IsMatch(lines[i])) break;
                    i++;

                    var cells = SplitCells(row.Groups[1].Value);
                    if (cells.Count <= Math.Max(srcCol, tgtCol)) continue;

                    var source = Clean(cells[srcCol]);
                    var target = Clean(cells[tgtCol]);
                    var note = noteCol >= 0 && noteCol < cells.Count ? Clean(cells[noteCol]) : "";
                    if (source.Length == 0 || target.Length == 0) continue;

                    // "term (sense)" qualifiers the generator adds to disambiguate
                    // are not part of the term memoQ will see in a segment.
                    source = StripQualifier(source);
                    if (source.Length == 0) continue;

                    // "a / b" in a locked-target cell means the generator failed
                    // to lock; take the first and say so in the note.
                    if (target.Contains(" / "))
                    {
                        note = (note.Length > 0 ? note + "; " : "") + "prompt listed alternatives: " + target;
                        target = target.Split(new[] { " / " }, StringSplitOptions.None)[0].Trim();
                    }

                    entries.Add(new Entry { Source = source, Target = target, Note = note });

                    foreach (Match never in NeverPattern.Matches(note))
                    {
                        var banned = never.Groups[1].Value.Trim();
                        if (banned.Length > 0 && !string.Equals(banned, target, StringComparison.OrdinalIgnoreCase))
                            entries.Add(new Entry { Source = source, Target = banned, Forbidden = true, Note = "from prompt note" });
                    }
                }
            }

            // Same source+target twice (one prompt often has a main table and an
            // "additional terms" table) collapses to one row.
            return entries
                .GroupBy(e => (e.Source.ToLowerInvariant(), e.Target.ToLowerInvariant(), e.Forbidden))
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>The glossary file text, in the format the memoQ terminology plugin reads.</summary>
        public static string ToGlossaryText(IEnumerable<Entry> entries, string title,
            string sourceLang = null, string targetLang = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + (title ?? "Project glossary"));

            // Machine-readable, and deliberately a different marker from the prose
            // comments below it. Without this the file's direction lived only in
            // its filename, which nothing read, and pointing a glossary the wrong
            // way produced no hits and no explanation.
            if (!string.IsNullOrWhiteSpace(sourceLang) && !string.IsNullOrWhiteSpace(targetLang))
                sb.AppendLine("#! source=" + sourceLang.Trim() + " target=" + targetLang.Trim());
            sb.AppendLine("# Exported from the prompt library by Supervertaler. Tab-separated: source, target, optional 'forbidden'.");
            sb.AppendLine("# Edit freely; the terminology plugin re-reads the file whenever it changes.");
            sb.AppendLine();
            foreach (var e in entries)
            {
                sb.Append(e.Source).Append('\t').Append(e.Target);
                if (e.Forbidden) sb.Append("\tforbidden");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static List<string> SplitCells(string inner)
        {
            // Cells may contain an escaped pipe; the generator does not emit
            // them, but a hand-edited prompt might.
            var sentinel = ((char)1).ToString();   // never appears in prompt text
            return inner.Replace("\\|", sentinel).Split('|')
                .Select(c => c.Replace(sentinel, "|").Trim())
                .ToList();
        }

        private static string Clean(string cell) => Markup.Replace(cell ?? "", "").Trim();

        private static string StripQualifier(string source)
        {
            var i = source.IndexOf(" (", StringComparison.Ordinal);
            return i > 0 ? source.Substring(0, i).Trim() : source;
        }
    }
}
