using System;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>What the request tells the model about document structure (Trados #109, memoQ #7).</summary>
    public enum StructureContextMode
    {
        /// <summary>Nothing said, nothing sent. The pre-#109 prompt, byte for byte.</summary>
        Off,
        /// <summary>Segments carry sentinels and the preamble rule explains them.</summary>
        Markers,
        /// <summary>
        /// The user wants structure context but this file cannot supply it (not a Word
        /// document, no numbering, no original to read). The fallback rule tells the
        /// model that numbering exists and is not in the text, so it neither invents
        /// one nor flags its absence.
        /// </summary>
        Unavailable,
    }

    /// <summary>
    /// The sentinel that carries a document's list marker into the source string sent
    /// to the model, and the two rules that go with it. Shared by both products so
    /// the format, the rule and the strip are one decision.
    ///
    /// <para>Format, exactly: <c>[#</c>, the marker as Word renders it, <c>]</c>, then
    /// the text with no space between - <c>[#e)]het fixeren…</c>. Single brackets on
    /// purpose: <c>[[TC: …]]</c> is a translator comment the memoQ prompts require the
    /// model to emit, and <c>&lt;t1&gt;</c> is an inline tag; the sentinel collides
    /// with neither, and the strip matches only its own shape.</para>
    ///
    /// <para>The marker is inside the string the model sees, so the model can echo
    /// it; <see cref="Strip"/> on every returned target is mandatory whenever markers
    /// were sent, and the caller logs when it fires - that is the per-model evidence
    /// on whether "never include it in your output" is obeyed.</para>
    /// </summary>
    public static class StructureContext
    {
        public const string Open = "[#";
        public const string Close = "]";

        /// <summary>
        /// The strip pattern both products use, verbatim: a sentinel at the very start
        /// of the text and any whitespace after it. Nothing broader - "brackets at the
        /// start of the line" would eat a legitimate <c>[[TC:…]]</c>.
        /// </summary>
        public const string StripPattern = @"^\[#[^\]]*\]\s*";

        private static readonly Regex StripRegex = new Regex(StripPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary><c>[#e)]</c> for <c>e)</c>; null for no marker.</summary>
        public static string Sentinel(string marker)
        {
            return string.IsNullOrEmpty(marker) ? null : Open + marker + Close;
        }

        /// <summary>The source string to send: sentinel then text, unchanged when there is no marker.</summary>
        public static string Prefix(string marker, string text)
        {
            var s = Sentinel(marker);
            return s == null ? text : s + (text ?? "");
        }

        /// <summary>
        /// Removes a sentinel the model echoed at the start of a target. <paramref name="fired"/>
        /// says whether anything was removed, so the caller can log it. A target that
        /// legitimately begins with <c>a)</c> is untouched: only the <c>[#…]</c> shape matches.
        /// </summary>
        public static string Strip(string text, out bool fired)
        {
            fired = false;
            if (string.IsNullOrEmpty(text) || text.IndexOf(Open, StringComparison.Ordinal) < 0) return text;
            var m = StripRegex.Match(text);
            if (!m.Success)
            {
                // A model that adds a leading space before the echoed sentinel.
                var trimmed = text.TrimStart();
                m = StripRegex.Match(trimmed);
                if (!m.Success) return text;
                fired = true;
                return trimmed.Substring(m.Length);
            }
            fired = true;
            return text.Substring(m.Length);
        }

        /// <summary>Ships in the plugin, with every request that carries markers. Not in a prompt template: it must reach prompts the plugin did not author.</summary>
        public const string PreambleRule =
            "Text enclosed in `[#` and `]` at the start of a segment is structural information about the document, not content in it - " +
            "the list marker the document supplies for that paragraph (a claim number, a letter, a bullet), or another structural fact about the segment. " +
            "Use it to resolve cross-references such as \"steps a. to f.\" or \"according to claim 2\", and to keep list items grammatically parallel. " +
            "Never translate it, never alter it, and never include it in your output. Everything outside the brackets is content and is translated normally.";

        /// <summary>Ships whenever structure context is wanted but unavailable for the file.</summary>
        public const string FallbackRule =
            "List numbering is supplied by the document, not by the segment text, and will not appear in the segment you receive. " +
            "Never conclude from its absence that the source omitted it, never supply one, and never flag a missing number or letter as a defect.";

        /// <summary>The rule for a mode, or null for <see cref="StructureContextMode.Off"/>.</summary>
        public static string RuleFor(StructureContextMode mode)
        {
            switch (mode)
            {
                case StructureContextMode.Markers: return PreambleRule;
                case StructureContextMode.Unavailable: return FallbackRule;
                default: return null;
            }
        }
    }
}
