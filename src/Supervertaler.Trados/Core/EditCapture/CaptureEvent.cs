using System;

namespace Supervertaler.Trados.Core.EditCapture
{
    /// <summary>
    /// One observed event. Deliberately a plain record of what was seen, with no
    /// interpretation: pairing a proposal to a final, and deciding what counts as
    /// a confirmation, happen offline where the rule can change without
    /// re-capturing anything.
    ///
    /// <para><b>Do not give an event a name that encodes a conclusion.</b> An
    /// earlier draft called the departure event <c>confirmed</c>, which asserts
    /// something capture never observes: Studio can raise the confirmation-level
    /// event <em>after</em> the cursor has already moved on, so a record named
    /// "confirmed" could carry a level saying otherwise. <c>Left</c> is what
    /// actually happened.</para>
    /// </summary>
    internal sealed class CaptureEvent
    {
        /// <summary>Target box received content from any source: MT, TM, AI, auto-propagate, paste.</summary>
        public const string Populated = "populated";

        /// <summary>The cursor left this segment. Values are as read at that moment.</summary>
        public const string Left = "left";

        /// <summary>Read during a document-wide pass at save or close. Later than any Left, so it wins.</summary>
        public const string Sweep = "sweep";

        public string Event { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        // Identity. unit_id + seg_id come from the sdlxliff structure, so they
        // survive the file being closed and reopened.
        public string FileId { get; set; }
        public string UnitId { get; set; }
        public string SegId { get; set; }

        public string Source { get; set; }
        public string Target { get; set; }
        public string Origin { get; set; }
        public int? MatchPercent { get; set; }

        /// <summary>Confirmation level as read, never filtered on. An unconfirmed visit is still data.</summary>
        public string ConfLevel { get; set; }

        /// <summary>Verbatim. Never summarised or classified at capture time.</summary>
        public string Comment { get; set; }

        /// <summary>Severity, author and date as JSON, when a comment is present.</summary>
        public string CommentMeta { get; set; }

        public string Project { get; set; }
        public string Client { get; set; }
        public string CaseRef { get; set; }
        public string SrcLang { get; set; }
        public string TgtLang { get; set; }
    }
}
