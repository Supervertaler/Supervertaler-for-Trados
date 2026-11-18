using System.Text;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Proofread active segment: the AI proofreader for the one segment the
    /// cursor is in, without leaving the grid. The third of the Editor's
    /// one-segment actions, beside Translate active segment (Alt+T) and
    /// FuzzyFixer (Ctrl+Alt+F).
    ///
    /// <para>This class is the part with no Studio in it: what opens the
    /// request, what the reply amounts to, and how a finding reads in the
    /// window. The request itself is the Batch tab's proofreading request for
    /// one segment (<see cref="BatchProofreader"/>), so the proofreading prompt,
    /// the memory bank, the output contract, the reply's parser and the checks
    /// on a correction (<see cref="ProofreadCorrection"/>) are the ones a full
    /// run uses, and Plugin Core is not changed.</para>
    ///
    /// <para><b>Document context.</b> It never sends the whole document, whatever
    /// "Include document context" says: it sends <see cref="FuzzyFixer.NeighbourCount"/>
    /// segments before and after. So it cannot check what needs the rest of the
    /// document (a term translated differently forty segments earlier), and the
    /// request says so, or a model reports inconsistencies it has imagined.</para>
    ///
    /// <para><b>Cost.</b> What differs from one segment to the next - the terms
    /// that occur in it, the segments around it, the segment - opens the user
    /// prompt (<see cref="Lead"/>). The system prompt, with the proofreading
    /// prompt and the memory bank, is then the same bytes for every segment and
    /// the provider's prompt cache serves it. A full run has its terms in the
    /// system prompt; here they would change it for every segment.</para>
    ///
    /// <para><b>Scale.</b> One segment and one AI request per use; the Editor
    /// waits behind the window meanwhile, so requests cannot pile up. Nothing
    /// is kept between uses, and no proofreading report is made or changed.</para>
    /// </summary>
    internal static class SegmentProofread
    {
        public const string Title = "Proofread active segment";

        /// <summary>
        /// The number the segment goes under in the request and is matched back
        /// by: the number Studio shows when that is a whole number, else 1 (a
        /// split segment's "331 a"). One segment per request, so any number is
        /// unambiguous.
        /// </summary>
        public static int NumberFor(string studioNumber)
        {
            int n;
            return int.TryParse((studioNumber ?? "").Trim(), out n) && n > 0 ? n : 1;
        }

        /// <summary>
        /// What opens the user prompt: that this is one segment and not a
        /// document, then the termbase terms that occur in it
        /// (<see cref="ProofreadingPrompt.TermbaseBlock"/>), then the segments
        /// around it (<see cref="FuzzyFixer.NeighbourLines"/>). Either may be
        /// null.
        /// </summary>
        public static string Lead(int number, string termbaseBlock, string neighbourLines)
        {
            var sb = new StringBuilder();
            sb.Append("## One segment (this request)\n");
            sb.Append("This request holds one segment, [SEGMENT ").Append(number.ToString("D4")).Append("], and not the ")
              .Append("document it comes from. Review that segment as instructed and give one verdict, for it alone. ")
              .Append("There is no # DOCUMENT CONTENT section: do not report an inconsistency with text you have not ")
              .Append("been shown, and cite no segment in an Evidence line that is not in this request.");

            if (!string.IsNullOrWhiteSpace(termbaseBlock))
                sb.Append("\n\n").Append(termbaseBlock.Trim());

            if (!string.IsNullOrWhiteSpace(neighbourLines))
            {
                sb.Append("\n\n## Surrounding segments (context only)\n");
                sb.Append("The segments just before and after it in the document, with their current translations where ")
                  .Append("they have one (these may be unrevised drafts). They are here so that you can judge the segment ")
                  .Append("in its context: do not review them, and give no verdict for them.\n");
                sb.Append(neighbourLines.Trim());
            }
            return sb.ToString();
        }

        public enum Outcome
        {
            /// <summary>The reply held no verdict that could be read: nothing is known.</summary>
            NoVerdict,
            /// <summary>The AI found nothing wrong.</summary>
            Clean,
            /// <summary>An issue, with a correction that can be written.</summary>
            Writable,
            /// <summary>An issue, and no correction (or one that reads as the target does).</summary>
            NoCorrection,
            /// <summary>An issue, with a correction that must not be written.</summary>
            NotWritable
        }

        /// <summary>
        /// What the reply amounts to. <paramref name="issue"/> is what
        /// <see cref="BatchProofreader"/> raised for the segment, null when it
        /// raised nothing (the request failed); <paramref name="noVerdict"/> is
        /// its "reads OK only by default". <paramref name="whyNot"/> says, for
        /// an issue that cannot be written, why not - in the words Apply on the
        /// Reports tab uses.
        /// </summary>
        public static Outcome Judge(ProofreadingIssue issue, bool noVerdict, out string whyNot)
        {
            whyNot = null;
            if (issue == null || noVerdict) return Outcome.NoVerdict;
            if (issue.IsOk) return Outcome.Clean;

            if (issue.CorrectedTarget == null)
            {
                // Normalise gives null for no Corrected line and for one that
                // changes nothing.
                whyNot = "the AI gave no corrected translation for it, or one that reads exactly as the translation does now.";
                return Outcome.NoCorrection;
            }

            whyNot = ProofreadCorrection.Problem(issue.SourceText, issue.TargetText, issue.CorrectedTarget);
            return whyNot == null ? Outcome.Writable : Outcome.NotWritable;
        }

        /// <summary>
        /// The finding in words, for the window's text box: the issue, the
        /// evidence when there is any, and the suggestion, each on a line of its
        /// own. Any length: the box scrolls.
        /// </summary>
        public static string Findings(ProofreadingIssue issue)
        {
            var sb = new StringBuilder();
            sb.Append("Issue: ").Append(OneLine(issue?.IssueDescription, "(the AI did not describe it)"));
            if (!string.IsNullOrWhiteSpace(issue?.Evidence))
                sb.Append("\nEvidence: ").Append(OneLine(issue.Evidence, ""));
            if (!string.IsNullOrWhiteSpace(issue?.Suggestion))
                sb.Append("\nSuggestion: ").Append(OneLine(issue.Suggestion, ""));
            return sb.ToString();
        }

        private static string OneLine(string text, string ifEmpty) =>
            string.IsNullOrWhiteSpace(text) ? ifEmpty : text.Trim();

        /// <summary>
        /// The text box for an issue whose correction can be written: the
        /// finding, then the line that introduces the changes shown under it.
        /// </summary>
        public static string WritableText(ProofreadingIssue issue)
        {
            bool sameWords = FuzzyFixer.Plain(issue.TargetText) == FuzzyFixer.Plain(issue.CorrectedTarget);
            return Findings(issue) + "\n\n"
                + (sameWords
                    ? "Corrected translation (it changes only tags, spacing or line breaks, which are not shown here):"
                    : "Corrected translation (red is removed, green is added):");
        }

        /// <summary>
        /// The text box for an issue with nothing to write: the finding, why
        /// nothing can be written, and the correction the AI gave when it gave
        /// one, to copy by hand.
        /// </summary>
        public static string NotWritableText(ProofreadingIssue issue, string whyNot)
        {
            var sb = new StringBuilder(Findings(issue));
            sb.Append("\n\nNothing can be written: ").Append(whyNot);
            if (!string.IsNullOrWhiteSpace(issue?.CorrectedTarget))
                sb.Append("\n\nThe AI's corrected translation was:\n").Append(issue.CorrectedTarget.Trim());
            return sb.ToString();
        }
    }
}
