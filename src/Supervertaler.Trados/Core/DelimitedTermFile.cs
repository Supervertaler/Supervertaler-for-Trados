using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.Trados.Core
{
    /// <summary>One term pair read out of a delimited file.</summary>
    public class DelimitedTermRow
    {
        /// <summary>1-based line number in the file, for reporting a bad row.</summary>
        public int Line { get; set; }

        public string Source { get; set; }
        public string Target { get; set; }
        public string Definition { get; set; }
        public string Domain { get; set; }
        public string Notes { get; set; }

        /// <summary>Abbreviation(s) of the source / target term, pipe-separated,
        /// from columns whose header names the side or the language.</summary>
        public string SourceAbbreviation { get; set; }
        public string TargetAbbreviation { get; set; }

        /// <summary>Extra spellings for either side, from synonym columns.</summary>
        public List<string> SourceSynonyms { get; set; } = new List<string>();
        public List<string> TargetSynonyms { get; set; } = new List<string>();
    }

    /// <summary>What the parser made of a file, before anything is written.</summary>
    public class DelimitedTermFileResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; }

        /// <summary>The delimiter actually used, named for a human: "tab", "comma"…</summary>
        public string Delimiter { get; set; }

        public List<string> Headers { get; set; } = new List<string>();

        /// <summary>Header name to the field it was taken as - source, target,
        /// definition, domain, notes, sourceSynonyms, targetSynonyms,
        /// sourceAbbreviation, targetAbbreviation, ignore.
        /// This is what a dry run must show: in a format with no schema, the
        /// inferred mapping is the thing most likely to be wrong.</summary>
        public List<string> Mapping { get; set; } = new List<string>();

        public List<DelimitedTermRow> Rows { get; set; } = new List<DelimitedTermRow>();

        /// <summary>Rows skipped, with the reason. Reported rather than dropped:
        /// a file that silently loses eleven rows looks like a file with eleven
        /// fewer terms.</summary>
        public List<string> Skipped { get; set; } = new List<string>();

        /// <summary>Things worth saying before a write, none of them fatal.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Reads a tab/comma/semicolon-delimited glossary export into term pairs.
    ///
    /// <para>Exists because client glossaries often arrive only as memoQ or Excel
    /// exports, never as <c>.ttb</c> or <c>.sdltb</c>, and the alternative was one
    /// <c>add_term</c> call per row.</para>
    ///
    /// <para>Nothing here writes. The parse and its report are separate from the
    /// import on purpose: a delimited file carries no schema, so the dry run is
    /// the only place a mistake can be caught before it is in a termbase.</para>
    /// </summary>
    public static class DelimitedTermFile
    {
        private static readonly string[] Extensions = { ".csv", ".tsv", ".txt" };

        /// <summary>Does this path look like a delimited file rather than a
        /// Trados/MultiTerm termbase?</summary>
        public static bool LooksDelimited(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return Extensions.Contains(ext);
        }

        /// <summary>
        /// Parse <paramref name="path"/>.
        /// </summary>
        /// <param name="sourceLang">Required. A delimited file carries no
        /// language metadata, so the caller must say which side is which - and
        /// naming the languages also lets a column headed "Dutch" be recognised.</param>
        /// <param name="columnMap">Overrides as <c>"Header=field"</c>, the same
        /// convention <c>fieldMap</c> already uses for MultiTerm imports.</param>
        public static DelimitedTermFileResult Parse(
            string path, string sourceLang, string targetLang, IEnumerable<string> columnMap = null)
        {
            var result = new DelimitedTermFileResult();

            if (!File.Exists(path))
            {
                result.Error = "file not found: " + path;
                return result;
            }

            string[] lines;
            try
            {
                // utf-8-sig equivalent: memoQ and Excel both write a BOM, and a
                // BOM left on the first header turns "Dutch" into "﻿Dutch",
                // which then matches nothing.
                lines = File.ReadAllLines(path, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                result.Error = "could not read the file: " + ex.Message;
                return result;
            }

            lines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (lines.Length < 2)
            {
                result.Error = "the file has no data rows (a header plus at least one row is needed)";
                return result;
            }

            var header = lines[0].TrimStart('﻿');
            var delim = SniffDelimiter(header);
            result.Delimiter = DelimiterName(delim);

            result.Headers = header.Split(delim).Select(h => h.Trim()).ToList();
            if (result.Headers.Count < 2)
            {
                result.Error = "only one column was found - the delimiter could not be determined. "
                             + "Expected tab, comma or semicolon.";
                return result;
            }

            var fields = MapColumns(result.Headers, sourceLang, targetLang, columnMap, result);
            var srcCol = Array.IndexOf(fields, "source");
            var tgtCol = Array.IndexOf(fields, "target");

            if (srcCol < 0 || tgtCol < 0)
            {
                result.Error = "could not tell which columns hold the source and target terms. "
                             + "Headers are: " + string.Join(", ", result.Headers)
                             + ". Pass an override such as \"" + result.Headers[0] + "=source\", \""
                             + result.Headers[Math.Min(1, result.Headers.Count - 1)] + "=target\".";
                return result;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                var cells = lines[i].Split(delim);
                var lineNo = i + 1;

                string Cell(int c) => (c >= 0 && c < cells.Length) ? cells[c].Trim() : "";

                var row = new DelimitedTermRow
                {
                    Line = lineNo,
                    Source = Cell(srcCol),
                    Target = Cell(tgtCol),
                };

                if (row.Source.Length == 0 || row.Target.Length == 0)
                {
                    result.Skipped.Add("line " + lineNo + ": "
                        + (row.Source.Length == 0 ? "no source term" : "no target term"));
                    continue;
                }

                for (int c = 0; c < fields.Length && c < cells.Length; c++)
                {
                    var v = Cell(c);
                    if (v.Length == 0) continue;
                    switch (fields[c])
                    {
                        case "definition": row.Definition = v; break;
                        case "domain": row.Domain = v; break;
                        case "notes": row.Notes = v; break;
                        case "sourceSynonyms": row.SourceSynonyms.AddRange(SplitSynonyms(v)); break;
                        case "targetSynonyms": row.TargetSynonyms.AddRange(SplitSynonyms(v)); break;
                        case "sourceAbbreviation": row.SourceAbbreviation = JoinVariants(row.SourceAbbreviation, v); break;
                        case "targetAbbreviation": row.TargetAbbreviation = JoinVariants(row.TargetAbbreviation, v); break;
                    }
                }

                result.Rows.Add(row);
            }

            AddWarnings(result);
            result.Ok = result.Rows.Count > 0;
            if (!result.Ok && result.Error == null)
                result.Error = "no usable rows were found";
            return result;
        }

        /// <summary>
        /// Whichever candidate appears most often in the header line. Counting on
        /// the header rather than the whole file keeps a comma inside a term from
        /// outvoting the real delimiter.
        /// </summary>

        /// <summary>
        /// Turn a parse into the same <see cref="Models.ImportedTermbase"/> the
        /// .ttb and .sdltb readers produce, so a delimited file rejoins the
        /// existing import path rather than getting one of its own - and
        /// inherits its destination resolution, Write gate, dry run and
        /// duplicate handling unchanged.
        /// </summary>
        public static Models.ImportedTermbase ToImportedTermbase(
            DelimitedTermFileResult parsed, string path, string sourceLang, string targetLang)
        {
            const int SrcId = 1;
            const int TgtId = 2;

            var tb = new Models.ImportedTermbase
            {
                FilePath = path,
                Format = "delimited (" + (parsed.Delimiter ?? "tab") + "-separated)",
                Name = Path.GetFileNameWithoutExtension(path),
            };

            tb.Languages.Add(new Models.ImportLanguage
            {
                Id = SrcId,
                Name = sourceLang,
                Locale = LanguageUtils.CanonicalLocale(sourceLang),
            });
            tb.Languages.Add(new Models.ImportLanguage
            {
                Id = TgtId,
                Name = targetLang,
                Locale = LanguageUtils.CanonicalLocale(targetLang),
            });

            var conceptId = 0;
            foreach (var row in parsed.Rows)
            {
                var concept = new Models.ImportConcept { ConceptId = ++conceptId };

                // Synonyms ride with their own side's term list, which is how
                // the readers present alternative spellings of one concept.
                var srcTerms = new List<string> { row.Source };
                srcTerms.AddRange(row.SourceSynonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
                var tgtTerms = new List<string> { row.Target };
                tgtTerms.AddRange(row.TargetSynonyms.Where(s => !string.IsNullOrWhiteSpace(s)));

                concept.TermsByLanguageId[SrcId] = srcTerms;
                concept.TermsByLanguageId[TgtId] = tgtTerms;

                if (!string.IsNullOrWhiteSpace(row.Definition)) concept.Fields["Definition"] = row.Definition;
                if (!string.IsNullOrWhiteSpace(row.Domain)) concept.Fields["Domain"] = row.Domain;
                if (!string.IsNullOrWhiteSpace(row.Notes)) concept.Fields["Notes"] = row.Notes;

                // Abbreviations are LANGUAGE fields, not concept fields: that is
                // what lets the importer put each one on its own side.
                if (!string.IsNullOrWhiteSpace(row.SourceAbbreviation))
                    LanguageFields(concept, SrcId)[AbbreviationField] = row.SourceAbbreviation;
                if (!string.IsNullOrWhiteSpace(row.TargetAbbreviation))
                    LanguageFields(concept, TgtId)[AbbreviationField] = row.TargetAbbreviation;

                tb.Concepts.Add(concept);
            }

            foreach (var f in new[] { "Definition", "Domain", "Notes" })
                if (tb.Concepts.Any(c => c.Fields.ContainsKey(f)))
                    tb.DiscoveredFields.Add(f);
            if (tb.Concepts.Any(c => c.LanguageFields.Values.Any(d => d.ContainsKey(AbbreviationField))))
                tb.DiscoveredFields.Add(AbbreviationField);

            return tb;
        }

        /// <summary>The field name abbreviation columns are filed under; the
        /// importer's automatic mapping sends it to the abbreviation fields.</summary>
        private const string AbbreviationField = "Abbreviation";

        private static Dictionary<string, string> LanguageFields(Models.ImportConcept concept, int langId)
        {
            if (!concept.LanguageFields.TryGetValue(langId, out var d))
            {
                d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                concept.LanguageFields[langId] = d;
            }
            return d;
        }

        private static string JoinVariants(string existing, string add)
        {
            var parts = (existing ?? "").Split('|').Concat(add.Split('|', ';'))
                .Select(s => s.Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return string.Join("|", parts);
        }

        private static char SniffDelimiter(string header)
        {
            var counts = new[] { '\t', ';', ',' }
                .Select(c => new { c, n = header.Count(x => x == c) })
                .OrderByDescending(x => x.n)
                .ToList();
            return counts[0].n > 0 ? counts[0].c : '\t';
        }

        private static string DelimiterName(char c)
        {
            switch (c)
            {
                case '\t': return "tab";
                case ';': return "semicolon";
                case ',': return "comma";
                default: return "'" + c + "'";
            }
        }

        /// <summary>
        /// Decide what each column holds.
        ///
        /// <para>Order: an explicit override always wins; then a header that
        /// names the field outright; then a header that names one of the two
        /// LANGUAGES, because real exports are headed "Dutch"/"English" rather
        /// than "source"/"target"; then, only if source and target are still
        /// unknown, the first two columns.</para>
        /// </summary>
        private static string[] MapColumns(List<string> headers, string sourceLang, string targetLang,
            IEnumerable<string> columnMap, DelimitedTermFileResult result)
        {
            var fields = new string[headers.Count];

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in columnMap ?? Enumerable.Empty<string>())
            {
                var eq = (entry ?? "").LastIndexOf('=');
                if (eq <= 0) continue;
                overrides[entry.Substring(0, eq).Trim()] = entry.Substring(eq + 1).Trim();
            }

            for (int i = 0; i < headers.Count; i++)
            {
                var h = headers[i];
                string mapped;

                if (overrides.TryGetValue(h, out mapped))
                {
                    // Known field names in any letter case: "Abbr=TargetAbbreviation"
                    // must not quietly become a column nothing reads.
                    fields[i] = KnownFields.FirstOrDefault(k =>
                        string.Equals(k, mapped, StringComparison.OrdinalIgnoreCase)) ?? mapped;
                    continue;
                }

                var low = h.ToLowerInvariant();
                if (low == "source" || low == "term") fields[i] = "source";
                else if (low == "target" || low == "translation") fields[i] = "target";
                else if (low == "definition" || low == "def") fields[i] = "definition";
                else if (low == "domain" || low == "subject") fields[i] = "domain";
                else if (low == "notes" || low == "note" || low == "comment") fields[i] = "notes";
                else if (IsSynonymHeader(low, sourceLang)) fields[i] = "sourceSynonyms";
                else if (IsSynonymHeader(low, targetLang)) fields[i] = "targetSynonyms";
                // Before the language-name tests, which would otherwise take a
                // "Dutch abbreviation" column for the Dutch TERM column.
                else if (IsAbbreviationHeader(low))
                {
                    if (NamesLanguage(low, sourceLang) || low.StartsWith("source")) fields[i] = "sourceAbbreviation";
                    else if (NamesLanguage(low, targetLang) || low.StartsWith("target")) fields[i] = "targetAbbreviation";
                    else
                    {
                        // Which language a bare "Abbreviation" column is in cannot be
                        // read off the file, and guessing wrong would put a Dutch
                        // abbreviation in the English field of every entry. Leave it
                        // out, loudly, with the one-line fix.
                        fields[i] = "ignore";
                        result.Warnings.Add(
                            "Column \"" + h + "\" holds abbreviations but does not say which language, so it "
                            + "was NOT imported. Pass \"" + h + "=sourceAbbreviation\" or \"" + h
                            + "=targetAbbreviation\" in fieldMap to import it.");
                    }
                }
                else if (NamesLanguage(low, sourceLang)) fields[i] = "source";
                else if (NamesLanguage(low, targetLang)) fields[i] = "target";
                else fields[i] = "ignore";
            }

            // Nothing recognised: fall back to position, and say so, because a
            // silent positional guess is how the wrong column ends up as a term.
            if (Array.IndexOf(fields, "source") < 0 && Array.IndexOf(fields, "target") < 0
                && headers.Count >= 2)
            {
                fields[0] = "source";
                fields[1] = "target";
                result.Warnings.Add(
                    "No header named the languages or the fields, so the first two columns were "
                    + "taken as source and target. Check the sample pairs before importing.");
            }

            for (int i = 0; i < headers.Count; i++)
                result.Mapping.Add(headers[i] + " -> " + fields[i]);

            return fields;
        }

        private static readonly string[] KnownFields =
        {
            "source", "target", "definition", "domain", "notes",
            "sourceSynonyms", "targetSynonyms", "sourceAbbreviation", "targetAbbreviation", "ignore"
        };

        private static readonly HashSet<string> AbbreviationWords = new HashSet<string>(
            new[] { "abbreviation", "abbreviations", "abbr", "abbrev", "acronym", "acronyms" },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>A header naming abbreviations, judged word by word so that
        /// "abbr" is not found inside some longer word.</summary>
        private static bool IsAbbreviationHeader(string header)
        {
            if (header.Contains("short form")) return true;
            return header.Split(new[] { ' ', '.', '(', ')', '-', '_', '/', ':' },
                                StringSplitOptions.RemoveEmptyEntries)
                         .Any(w => AbbreviationWords.Contains(w));
        }

        private static bool IsSynonymHeader(string header, string lang)
        {
            if (!header.Contains("synonym")) return false;
            return NamesLanguage(header, lang);
        }

        /// <summary>
        /// Does this header name the given language? Accepts the code ("nl",
        /// "nl-NL") and the common English names, since that is what exports use.
        /// </summary>
        private static bool NamesLanguage(string header, string lang)
        {
            if (string.IsNullOrWhiteSpace(lang) || string.IsNullOrWhiteSpace(header)) return false;

            var code = lang.Split('-')[0].ToLowerInvariant();
            if (header == code || header.StartsWith(code + " ") || header.StartsWith(code + "-")
                || header.Contains("(" + code + ")"))
                return true;

            string[] names;
            switch (code)
            {
                case "nl": names = new[] { "dutch", "nederlands", "flemish" }; break;
                case "en": names = new[] { "english", "engels" }; break;
                case "de": names = new[] { "german", "deutsch", "duits" }; break;
                case "fr": names = new[] { "french", "francais", "français", "frans" }; break;
                case "es": names = new[] { "spanish", "espanol", "español" }; break;
                case "it": names = new[] { "italian", "italiano" }; break;
                case "pt": names = new[] { "portuguese", "portugues", "português" }; break;
                default: names = new string[0]; break;
            }
            return names.Any(n => header.Contains(n));
        }

        private static IEnumerable<string> SplitSynonyms(string value)
        {
            return value.Split(';', '|')
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0);
        }

        /// <summary>
        /// Things a reviewer should see before writing, none of them fatal.
        /// </summary>
        private static void AddWarnings(DelimitedTermFileResult result)
        {
            // Worth mentioning, NOT a failure. TermMatcher.NormalizeScriptChars
            // already folds U+00A0 - along with Ogham, en/em/thin/hair, narrow
            // no-break, medium-mathematical and ideographic spaces - so such a
            // term DOES match text typed with an ordinary space. Verified by
            // looking it up after an import.
            //
            // It is still worth saying, because the character is invisible in
            // the source file and a reader comparing this termbase against the
            // client's spreadsheet by eye will not see why two identical-looking
            // terms differ. An earlier version of this warning claimed the term
            // would never match; that was asserted without checking what this
            // codebase already does, and it does handle it.
            // ' ' spelt as an escape, not as a literal: a literal
            // non-breaking space in source is invisible, and any tool that
            // normalises whitespace would turn this check into one that
            // flags every multi-word term instead.
            const char Nbsp = ' ';
            var nbsp = result.Rows.Count(r =>
                (r.Source ?? "").IndexOf(Nbsp) >= 0 || (r.Target ?? "").IndexOf(Nbsp) >= 0);
            if (nbsp > 0)
                result.Warnings.Add(nbsp + " term(s) contain a non-breaking space rather than an "
                    + "ordinary one. Matching handles it, so the term will still be found - but "
                    + "the character is invisible, so two terms that look identical may differ.");

            var dupes = result.Rows.GroupBy(r => (r.Source ?? "").Trim().ToLowerInvariant())
                                   .Where(g => g.Count() > 1).ToList();
            if (dupes.Count > 0)
                result.Warnings.Add(dupes.Count + " source term(s) appear more than once in the file: "
                    + string.Join(", ", dupes.Take(5).Select(g => "\"" + g.Key + "\""))
                    + (dupes.Count > 5 ? " and others" : "") + ".");

            if (result.Skipped.Count > 0)
                result.Warnings.Add(result.Skipped.Count + " row(s) were skipped for missing a term.");
        }
    }
}
