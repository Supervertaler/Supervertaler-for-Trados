using System;
using System.Collections.Generic;

namespace Supervertaler.Trados.Models
{
    /// <summary>
    /// A language index found in an external MultiTerm termbase (.sdltb / .ttb).
    /// </summary>
    public class ImportLanguage
    {
        /// <summary>
        /// Reader-assigned stable identifier used as the key into
        /// <see cref="ImportConcept.TermsByLanguageId"/>. For .ttb this is the
        /// numeric <c>mtIndexes.Id</c>; for .sdltb (which keys terms by the
        /// <c>I_{name}</c> table rather than a numeric id) it is a 0-based ordinal
        /// assigned in <c>mtIndexes</c> order.
        /// </summary>
        public int Id { get; set; }

        /// <summary>Human-readable index name, e.g. "English (United Kingdom)".</summary>
        public string Name { get; set; }

        /// <summary>
        /// Locale code as stored in the file. MultiTerm stores these upper-case
        /// (e.g. "EN", "EN-GB", "NL-BE"); normalise with
        /// <c>LanguageUtils.CanonicalLocale</c> before writing to a Supervertaler termbase.
        /// </summary>
        public string Locale { get; set; }

        /// <summary>
        /// The raw index name used to locate per-language storage. For .sdltb this is
        /// the <c>I_{IndexName}</c> table name suffix; for .ttb it equals <see cref="Name"/>.
        /// </summary>
        public string IndexName { get; set; }

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Locale) ? (Name ?? "") : $"{Name} ({Locale})";
    }

    /// <summary>
    /// A single concept (entry) from an external MultiTerm termbase. MultiTerm is
    /// concept-oriented: one concept holds terms in many languages plus descriptive
    /// fields. The importer flattens this into bilingual Supervertaler rows.
    /// </summary>
    public class ImportConcept
    {
        public int ConceptId { get; set; }

        /// <summary>
        /// Terms grouped by <see cref="ImportLanguage.Id"/>, in entry order. The first
        /// term in each list is the concept's primary term for that language; the rest
        /// become synonyms on import.
        /// </summary>
        public Dictionary<int, List<string>> TermsByLanguageId { get; set; }
            = new Dictionary<int, List<string>>();

        /// <summary>
        /// Concept-level (entry-level) descriptive fields (field name → value), e.g.
        /// a "Note" that applies to the whole concept. These apply to every flattened
        /// bilingual row regardless of direction.
        /// </summary>
        public Dictionary<string, string> Fields { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Language-attributed descriptive fields: language id → (field name → value).
        /// Captures both language-level fields (e.g. an English "Definition") and
        /// term-level fields of that language's primary term (e.g. a "Status" of
        /// "preferred"/"forbidden", or a term "Note"). Keyed by
        /// <see cref="ImportLanguage.Id"/>; the importer reads the source/target
        /// language's fields when flattening a pair.
        /// </summary>
        public Dictionary<int, Dictionary<string, string>> LanguageFields { get; set; }
            = new Dictionary<int, Dictionary<string, string>>();

        /// <summary>Ordered terms for a language id, or an empty list if none.</summary>
        public List<string> TermsFor(int languageId) =>
            TermsByLanguageId.TryGetValue(languageId, out var list) ? list : new List<string>();

        /// <summary>Descriptive fields attributed to a language id, or an empty dict.</summary>
        public Dictionary<string, string> FieldsForLanguage(int languageId) =>
            LanguageFields.TryGetValue(languageId, out var d)
                ? d
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A read-only, in-memory projection of an external MultiTerm termbase
    /// (.sdltb or .ttb), produced by <c>ITermbaseReader.LoadForImport</c> and consumed
    /// by the import mapping dialog and the persist stage. Intentionally decoupled from
    /// the on-disk schema so both readers can produce the same shape.
    /// </summary>
    public class ImportedTermbase
    {
        public string FilePath { get; set; }

        /// <summary>"sdltb" or "ttb".</summary>
        public string Format { get; set; }

        public string Name { get; set; }

        public List<ImportLanguage> Languages { get; set; } = new List<ImportLanguage>();

        public List<ImportConcept> Concepts { get; set; } = new List<ImportConcept>();

        /// <summary>
        /// Distinct concept-level field names discovered across all concepts, sorted.
        /// Drives the rows of the field-mapping grid in the import dialog.
        /// </summary>
        public List<string> DiscoveredFields { get; set; } = new List<string>();

        public int ConceptCount => Concepts.Count;

        public ImportLanguage LanguageById(int id) =>
            Languages.Find(l => l.Id == id);
    }
}
