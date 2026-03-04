using System.Collections.Generic;

namespace TermLens.Models
{
    /// <summary>
    /// A single term pair from a Supervertaler termbase.
    /// </summary>
    public class TermEntry
    {
        public long Id { get; set; }
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
        public bool Forbidden { get; set; }
        public bool CaseSensitive { get; set; }
        public List<string> TargetSynonyms { get; set; } = new List<string>();
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
        /// True if the user marked this term's glossary as a project glossary.
        /// Used for pink/blue coloring in the Term Picker dialog.
        /// </summary>
        public bool IsProjectGlossary { get; set; }

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
    }
}
