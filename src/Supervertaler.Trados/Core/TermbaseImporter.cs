using System;
using System.Collections.Generic;
using System.Linq;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Where an external MultiTerm descriptive field is written in the Supervertaler
    /// termbase. <see cref="AppendToNotes"/> preserves any field with no dedicated column
    /// by prefixing it into the notes; <see cref="Ignore"/> drops it.
    /// </summary>
    public enum ImportFieldTarget
    {
        Ignore = 0,
        Definition,
        Domain,
        Notes,
        Context,
        PartOfSpeech,
        Url,
        Client,
        Project,
        ForbiddenFlag,
        AppendToNotes,
        /// <summary>
        /// The term's abbreviation. A value in the SOURCE language's fields becomes
        /// the source abbreviation and one in the TARGET language's fields the
        /// target abbreviation, so each lands on its own side. A concept-level value
        /// says nothing about which language it is in, so it is appended to the
        /// notes instead of being guessed onto a side.
        /// </summary>
        Abbreviation
    }

    /// <summary>User choices that drive one import run.</summary>
    public sealed class ImportOptions
    {
        /// <summary><see cref="ImportLanguage.Id"/> of the language to use as source.</summary>
        public int SourceLanguageId { get; set; }

        /// <summary><see cref="ImportLanguage.Id"/> of the language to use as target.</summary>
        public int TargetLanguageId { get; set; }

        /// <summary>Destination Supervertaler termbase (termbases.id).</summary>
        public long DestinationTermbaseId { get; set; }

        /// <summary>MultiTerm field name → where it goes. Missing keys default to Ignore.</summary>
        public Dictionary<string, ImportFieldTarget> FieldMap { get; set; }
            = new Dictionary<string, ImportFieldTarget>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Outcome of an import run, for the completion summary.</summary>
    public sealed class ImportSummary
    {
        public int ConceptsTotal { get; set; }
        public int RowsBuilt { get; set; }
        public int SkippedNoSourceTerm { get; set; }
        public int SkippedNoTargetTerm { get; set; }
        public int Added { get; set; }
        public int Duplicates { get; set; }
        public int SynonymsAdded { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Flattens a concept-oriented <see cref="ImportedTermbase"/> into bilingual rows for a
    /// chosen language pair and writes them to a Supervertaler termbase via
    /// <see cref="TermbaseReader.ImportRows"/>. Pure transform + persist; no UI.
    /// </summary>
    public static class TermbaseImporter
    {
        // Status/usage values that mean "do not use" → map to the forbidden flag.
        private static readonly HashSet<string> ForbiddenStatusValues =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "forbidden", "deprecated", "prohibited", "banned", "obsolete", "do not use", "avoid", "rejected" };

        /// <summary>
        /// Suggests a default field mapping from discovered field names, matching by name
        /// (same idea as the TSV importer's header synonyms). Unrecognised fields default to
        /// <see cref="ImportFieldTarget.AppendToNotes"/> so nothing is silently lost.
        /// </summary>
        public static Dictionary<string, ImportFieldTarget> SuggestFieldMap(IEnumerable<string> fieldNames)
        {
            var map = new Dictionary<string, ImportFieldTarget>(StringComparer.OrdinalIgnoreCase);
            if (fieldNames == null) return map;

            foreach (var name in fieldNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                map[name] = Suggest(name.Trim().ToLowerInvariant());
            }
            return map;
        }

        private static ImportFieldTarget Suggest(string n)
        {
            switch (n)
            {
                case "definition": return ImportFieldTarget.Definition;
                case "subject":
                case "subject field":
                case "domain":
                case "field":
                case "category":
                case "discipline": return ImportFieldTarget.Domain;
                case "note":
                case "notes":
                case "comment":
                case "comments":
                case "remark": return ImportFieldTarget.Notes;
                case "context":
                case "example": return ImportFieldTarget.Context;
                case "part of speech":
                case "pos":
                case "word class":
                case "grammatical category": return ImportFieldTarget.PartOfSpeech;
                case "status":
                case "usage":
                case "usage status":
                case "term status": return ImportFieldTarget.ForbiddenFlag;
                case "url":
                case "link": return ImportFieldTarget.Url;
                case "client":
                case "customer": return ImportFieldTarget.Client;
                case "project": return ImportFieldTarget.Project;
                case "abbreviation":
                case "abbreviations":
                case "abbr":
                case "abbr.":
                case "abbrev":
                case "abbrev.":
                case "acronym":
                case "short form": return ImportFieldTarget.Abbreviation;
                // "Source" (a term's reference/source) and anything unrecognised are kept
                // in notes rather than dropped.
                default: return ImportFieldTarget.AppendToNotes;
            }
        }

        /// <summary>
        /// Builds the flattened rows for the chosen language pair without writing anything.
        /// Useful for the dialog's preview. Populates skip counts on <paramref name="summary"/>.
        /// </summary>
        public static List<TermbaseReader.ImportTermRow> BuildRows(
            ImportedTermbase tb, ImportOptions options, ImportSummary summary)
        {
            var rows = new List<TermbaseReader.ImportTermRow>();
            if (tb == null || options == null) return rows;

            var srcLang = tb.LanguageById(options.SourceLanguageId);
            var tgtLang = tb.LanguageById(options.TargetLanguageId);
            var srcCode = LanguageUtils.CanonicalLocale(LocaleOrName(srcLang));
            var tgtCode = LanguageUtils.CanonicalLocale(LocaleOrName(tgtLang));

            summary.ConceptsTotal = tb.Concepts.Count;

            foreach (var concept in tb.Concepts)
            {
                var srcTerms = concept.TermsFor(options.SourceLanguageId);
                var tgtTerms = concept.TermsFor(options.TargetLanguageId);

                if (srcTerms.Count == 0) { summary.SkippedNoSourceTerm++; continue; }
                if (tgtTerms.Count == 0) { summary.SkippedNoTargetTerm++; continue; }

                var row = new TermbaseReader.ImportTermRow
                {
                    SourceTerm = srcTerms[0],
                    TargetTerm = tgtTerms[0],
                    SourceLang = srcCode,
                    TargetLang = tgtCode,
                    SourceSynonyms = srcTerms.Skip(1).ToList(),
                    TargetSynonyms = tgtTerms.Skip(1).ToList()
                };

                ApplyFields(concept, options, row);
                rows.Add(row);
            }

            summary.RowsBuilt = rows.Count;
            return rows;
        }

        /// <summary>
        /// Flattens and writes the import into the destination termbase. Returns a summary
        /// with concept/row/skip counts and write outcome (added / duplicates / synonyms).
        /// </summary>
        /// <param name="dryRun">
        /// Flatten and map exactly as a real run would, and report the counts, but write
        /// nothing. Added for the MCP <c>import_project_termbase</c> tool so an assistant
        /// can show the user what a conversion would do before it touches their termbase –
        /// the whole operation is one call, so there is otherwise no point at which to
        /// look. <see cref="ImportSummary.Added"/> / <see cref="ImportSummary.Duplicates"/>
        /// stay 0 on a dry run: whether a row is a duplicate is decided by the database,
        /// and answering that would mean doing the query anyway.
        /// </param>
        public static ImportSummary Import(ImportedTermbase tb, ImportOptions options, string dbPath,
            bool dryRun = false)
        {
            var summary = new ImportSummary();
            var rows = BuildRows(tb, options, summary);

            if (summary.SkippedNoTargetTerm > 0)
                summary.Warnings.Add(
                    $"{summary.SkippedNoTargetTerm} concept(s) had no term in the target language and were skipped.");
            if (summary.SkippedNoSourceTerm > 0)
                summary.Warnings.Add(
                    $"{summary.SkippedNoSourceTerm} concept(s) had no term in the source language and were skipped.");

            if (rows.Count > 0 && !dryRun)
            {
                var write = TermbaseReader.ImportRows(dbPath, options.DestinationTermbaseId, rows);
                summary.Added = write.Added;
                summary.Duplicates = write.Duplicates;
                summary.SynonymsAdded = write.SynonymsAdded;
            }

            return summary;
        }

        // Resolves each mapped field's value (from concept-level + source/target language
        // fields) and routes it to the row's columns per the mapping.
        private static void ApplyFields(ImportConcept concept, ImportOptions options, TermbaseReader.ImportTermRow row)
        {
            var notesParts = new List<string>();
            var defParts = new List<string>();
            var domainParts = new List<string>();
            var contextParts = new List<string>();
            var posParts = new List<string>();

            foreach (var kv in options.FieldMap)
            {
                var target = kv.Value;
                if (target == ImportFieldTarget.Ignore) continue;

                if (target == ImportFieldTarget.Abbreviation)
                {
                    // Read per language rather than through ResolveFieldValue, which
                    // merges both languages into one string: a Dutch and an English
                    // abbreviation joined together would be right on neither side.
                    row.SourceAbbreviation = MergeVariants(row.SourceAbbreviation,
                        FieldValue(concept.FieldsForLanguage(options.SourceLanguageId), kv.Key));
                    row.TargetAbbreviation = MergeVariants(row.TargetAbbreviation,
                        FieldValue(concept.FieldsForLanguage(options.TargetLanguageId), kv.Key));
                    var unsided = FieldValue(concept.Fields, kv.Key);
                    if (unsided != null) notesParts.Add($"{kv.Key}: {unsided}");
                    continue;
                }

                var value = ResolveFieldValue(concept, kv.Key, options.SourceLanguageId, options.TargetLanguageId);
                if (string.IsNullOrWhiteSpace(value)) continue;

                switch (target)
                {
                    case ImportFieldTarget.Definition: defParts.Add(value); break;
                    case ImportFieldTarget.Domain: domainParts.Add(value); break;
                    case ImportFieldTarget.Notes: notesParts.Add(value); break;
                    case ImportFieldTarget.Context: contextParts.Add(value); break;
                    case ImportFieldTarget.PartOfSpeech: posParts.Add(value); break;
                    case ImportFieldTarget.Url: if (string.IsNullOrEmpty(row.Url)) row.Url = value; break;
                    case ImportFieldTarget.Client: if (string.IsNullOrEmpty(row.Client)) row.Client = value; break;
                    case ImportFieldTarget.Project: if (string.IsNullOrEmpty(row.Project)) row.Project = value; break;
                    case ImportFieldTarget.ForbiddenFlag:
                        if (IsForbiddenValue(value)) row.Forbidden = true;
                        break;
                    case ImportFieldTarget.AppendToNotes:
                        notesParts.Add($"{kv.Key}: {value}");
                        break;
                }
            }

            if (defParts.Count > 0) row.Definition = string.Join("; ", defParts);
            if (domainParts.Count > 0) row.Domain = string.Join("; ", domainParts);
            if (contextParts.Count > 0) row.Context = string.Join("; ", contextParts);
            if (posParts.Count > 0) row.PartOfSpeech = string.Join("; ", posParts);
            if (notesParts.Count > 0) row.Notes = string.Join(Environment.NewLine, notesParts);
        }

        // Gathers distinct non-empty values for a field name across concept-level and the
        // source/target language fields (a field may live at concept, language or term level).
        private static string ResolveFieldValue(ImportConcept concept, string fieldName, int srcId, int tgtId)
        {
            var values = new List<string>();
            void Take(Dictionary<string, string> dict)
            {
                if (dict != null && dict.TryGetValue(fieldName, out var v)
                    && !string.IsNullOrWhiteSpace(v)
                    && !values.Contains(v, StringComparer.OrdinalIgnoreCase))
                    values.Add(v.Trim());
            }
            Take(concept.Fields);
            Take(concept.FieldsForLanguage(srcId));
            Take(concept.FieldsForLanguage(tgtId));
            return values.Count == 0 ? null : string.Join("; ", values);
        }

        private static string FieldValue(Dictionary<string, string> dict, string fieldName)
        {
            if (dict == null || !dict.TryGetValue(fieldName, out var v) || string.IsNullOrWhiteSpace(v))
                return null;
            return v.Trim();
        }

        /// <summary>
        /// Adds abbreviation variants to a pipe-separated field, skipping repeats.
        /// The readers join several values of one field with "; " (two terms in a
        /// language each carrying an abbreviation), and a delimited cell may use
        /// either separator, so both become the "|" the abbreviation fields use.
        /// </summary>
        private static string MergeVariants(string existing, string add)
        {
            if (string.IsNullOrWhiteSpace(add)) return existing;
            var variants = new List<string>();
            foreach (var part in ((existing ?? "") + "|" + add).Split('|', ';'))
            {
                var t = part.Trim();
                if (t.Length > 0 && !variants.Contains(t, StringComparer.OrdinalIgnoreCase))
                    variants.Add(t);
            }
            return string.Join("|", variants);
        }

        private static bool IsForbiddenValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            if (ForbiddenStatusValues.Contains(v)) return true;
            // Tolerate compound values like "deprecated; preferred" by token check.
            return v.Split(';', ',', '/', '|')
                    .Select(t => t.Trim())
                    .Any(t => ForbiddenStatusValues.Contains(t));
        }

        private static string LocaleOrName(ImportLanguage lang)
        {
            if (lang == null) return "";
            return !string.IsNullOrWhiteSpace(lang.Locale) ? lang.Locale : lang.Name;
        }
    }
}
