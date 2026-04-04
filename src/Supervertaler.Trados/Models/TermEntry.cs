using System.Collections.Generic;

namespace Supervertaler.Trados.Models
{
    /// <summary>
    /// A synonym entry from the termbase_synonyms table.
    /// Used by the Term Entry Editor for full synonym CRUD.
    /// </summary>
    public class SynonymEntry
    {
        public long Id { get; set; }
        public string Text { get; set; }
        public string Language { get; set; } // "source" or "target"
        public int DisplayOrder { get; set; }
        public bool Forbidden { get; set; }
    }

    /// <summary>
    /// A single term pair from a Supervertaler or MultiTerm termbase.
    /// </summary>
    public class TermEntry
    {
        public long Id { get; set; }
        public string TermUuid { get; set; }
        public string SourceTerm { get; set; }
        public string TargetTerm { get; set; }
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
        public long TermbaseId { get; set; }
        public string TermbaseName { get; set; }
        public bool IsProjectTermbase { get; set; }
        public int Ranking { get; set; }
        public string Definition { get; set; }
        public string Domain { get; set; }
        public string Notes { get; set; }
        public string Url { get; set; }
        public bool Forbidden { get; set; }
        public bool CaseSensitive { get; set; }
        public bool IsNonTranslatable { get; set; }

        /// <summary>
        /// Client code — a short identifier for the client this term belongs to
        /// (e.g. "ACME", "GLOBEX"). Used to filter KB context and match
        /// SuperMemory client profiles. Stored in the termbase_terms table.
        /// </summary>
        public string Client { get; set; }

        /// <summary>
        /// Abbreviated form(s) of the source term (e.g., "GC" for "gas chromatography").
        /// Multiple variants can be pipe-separated: "GC|G.C.|gc".
        /// Each variant is indexed for matching — if any variant appears in a segment,
        /// TermLens will show the abbreviation chip with the full term in the +N tooltip.
        /// </summary>
        public string SourceAbbreviation { get; set; }

        /// <summary>
        /// Abbreviated form(s) of the target term (e.g., "GC" for "gaschromatografie").
        /// Multiple variants can be pipe-separated: "GC|G.C.".
        /// The first variant is used as the primary display when matched via abbreviation.
        /// </summary>
        public string TargetAbbreviation { get; set; }

        /// <summary>
        /// Returns the individual source abbreviation variants (split on pipe).
        /// </summary>
        public string[] GetSourceAbbreviationVariants()
        {
            if (string.IsNullOrWhiteSpace(SourceAbbreviation)) return System.Array.Empty<string>();
            return SourceAbbreviation.Split('|');
        }

        /// <summary>
        /// Returns the primary (first) target abbreviation for display/insertion.
        /// </summary>
        public string PrimaryTargetAbbreviation
        {
            get
            {
                if (string.IsNullOrWhiteSpace(TargetAbbreviation)) return null;
                var idx = TargetAbbreviation.IndexOf('|');
                return idx >= 0 ? TargetAbbreviation.Substring(0, idx).Trim() : TargetAbbreviation.Trim();
            }
        }

        /// <summary>
        /// True if this term comes from a MultiTerm .sdltb termbase (read-only).
        /// MultiTerm terms have negative IDs and cannot be edited or deleted.
        /// </summary>
        public bool IsMultiTerm { get; set; }

        /// <summary>
        /// Simple list of non-forbidden target synonym texts, used for display/matching.
        /// Populated by BulkLoadTargetSynonyms() during LoadAllTerms().
        /// </summary>
        public List<string> TargetSynonyms { get; set; } = new List<string>();

        /// <summary>
        /// Rich synonym entries (source language) — only populated by the editor.
        /// </summary>
        public List<SynonymEntry> SourceSynonyms { get; set; } = new List<SynonymEntry>();

        /// <summary>
        /// Rich synonym entries (target language) — only populated by the editor.
        /// </summary>
        public List<SynonymEntry> TargetSynonymEntries { get; set; } = new List<SynonymEntry>();
    }

    /// <summary>
    /// A matched term found in the current source segment, ready for display.
    /// </summary>
    public class TermMatch
    {
        public TermEntry Entry { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public string MatchedText { get; set; }
    }

    /// <summary>
    /// A token in the source segment — either a matched term or a plain word.
    /// </summary>
    public class SegmentToken
    {
        public string Text { get; set; }
        public bool IsLineBreak { get; set; }
        public List<TermEntry> Matches { get; set; } = new List<TermEntry>();
        public bool HasMatch => Matches.Count > 0;

        /// <summary>
        /// Set of TermEntry IDs that were matched via their SourceAbbreviation
        /// rather than their SourceTerm. Used by the display layer to show the
        /// abbreviation pair as primary and the full term in the +N tooltip.
        /// </summary>
        public HashSet<long> AbbreviationMatchIds { get; set; } = new HashSet<long>();
    }

    /// <summary>
    /// A matched source term with all its entries, used by the Term Picker dialog.
    /// </summary>
    public class TermPickerMatch
    {
        public int Index { get; set; }
        public string SourceText { get; set; }
        public TermEntry PrimaryEntry { get; set; }
        public List<TermEntry> AllEntries { get; set; } = new List<TermEntry>();

        /// <summary>
        /// True if the user marked this term's termbase as a project termbase.
        /// Used for pink/blue coloring in the Term Picker dialog.
        /// </summary>
        public bool IsProjectTermbase { get; set; }

        /// <summary>
        /// Gets all unique target options (primary + synonyms from all entries).
        /// The first item is always the primary target.
        /// </summary>
        public List<TermTargetOption> GetAllTargets()
        {
            var results = new List<TermTargetOption>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var entry in AllEntries)
            {
                if (!string.IsNullOrEmpty(entry.TargetTerm) && seen.Add(entry.TargetTerm))
                {
                    results.Add(new TermTargetOption
                    {
                        TargetTerm = entry.TargetTerm,
                        TermbaseName = entry.TermbaseName,
                        Ranking = entry.Ranking
                    });
                }

                // Include primary abbreviation as a target option
                var primaryAbbr = entry.PrimaryTargetAbbreviation;
                if (!string.IsNullOrEmpty(primaryAbbr) && seen.Add(primaryAbbr))
                {
                    results.Add(new TermTargetOption
                    {
                        TargetTerm = primaryAbbr,
                        TermbaseName = entry.TermbaseName + " (abbr)",
                        Ranking = entry.Ranking
                    });
                }

                foreach (var syn in entry.TargetSynonyms)
                {
                    if (!string.IsNullOrEmpty(syn) && seen.Add(syn))
                    {
                        results.Add(new TermTargetOption
                        {
                            TargetTerm = syn,
                            TermbaseName = entry.TermbaseName,
                            Ranking = entry.Ranking
                        });
                    }
                }
            }

            return results;
        }
    }

    /// <summary>
    /// A single target translation option within a TermPickerMatch.
    /// </summary>
    public class TermTargetOption
    {
        public string TargetTerm { get; set; }
        public string TermbaseName { get; set; }
        public int Ranking { get; set; }
    }

    /// <summary>
    /// Metadata about a loaded termbase.
    /// </summary>
    public class TermbaseInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
        public bool IsProjectTermbase { get; set; }
        public int Ranking { get; set; }
        public int TermCount { get; set; }

        /// <summary>
        /// Per-termbase case sensitivity setting.
        /// -1 = use global default, 0 = force case-insensitive, 1 = force case-sensitive.
        /// </summary>
        public int CaseSensitive { get; set; } = -1;
    }
}
