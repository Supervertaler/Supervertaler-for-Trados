namespace Supervertaler.Core.Models
{
    /// <summary>
    /// Represents a Translation Memory fuzzy match for AI context injection.
    /// </summary>
    public class TmMatch
    {
        /// <summary>Source text from the TM match.</summary>
        public string SourceText { get; set; }

        /// <summary>Target text from the TM match.</summary>
        public string TargetText { get; set; }

        /// <summary>Match percentage (0-100).</summary>
        public int MatchPercentage { get; set; }

        /// <summary>Name of the translation memory this match came from.</summary>
        public string TmName { get; set; }
    }
}
