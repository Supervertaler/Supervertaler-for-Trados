using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using Sdl.FileTypeSupport.Framework.BilingualApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Notices when trimming a selection to word edges dropped CONTENT rather
    /// than an edge, and asks instead of saving silently.
    ///
    /// <para>Every legitimate trim removes punctuation or whitespace at the
    /// edges - a trailing comma, a wrapping bracket - or ADDS letters by
    /// expanding a partial selection to the whole word. None of them removes a
    /// letter, digit or symbol the user selected. When one does, the trimming
    /// has decided that a character is not part of the word, and Unicode has
    /// many more of those than the trimming knows: a subscript three was one
    /// (2026-09-18, "O₃" saved as "O"), and prime marks, degree signs and Greek
    /// letters in formulas are waiting. Each such case becomes a question here
    /// rather than a silently damaged term.</para>
    ///
    /// <para>Content is compared after the matcher's script normalisation, so a
    /// selection of a formatted "ClO3-" saved as the Unicode "ClO₃⁻" is not a
    /// loss: the characters are the same, only their form changed.</para>
    /// </summary>
    public static class TermSaveGuard
    {
        /// <summary>
        /// True when <paramref name="saved"/> lacks a letter, digit or symbol
        /// that <paramref name="selection"/> contained. Multiset comparison:
        /// one of two identical characters going missing counts too.
        /// </summary>
        public static bool LostContent(string selection, string saved)
        {
            if (string.IsNullOrWhiteSpace(selection)) return false;
            var have = Count(TermMatcher.NormalizeScriptChars(saved ?? ""));
            foreach (var kv in Count(TermMatcher.NormalizeScriptChars(selection)))
            {
                int n;
                if (!have.TryGetValue(kv.Key, out n) || n < kv.Value) return true;
            }
            return false;
        }

        private static Dictionary<char, int> Count(string text)
        {
            var d = new Dictionary<char, int>();
            foreach (var c in text)
            {
                if (!IsContent(c)) continue;
                int n;
                d[c] = d.TryGetValue(c, out n) ? n + 1 : 1;
            }
            return d;
        }

        private static bool IsContent(char c)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                case UnicodeCategory.MathSymbol:
                case UnicodeCategory.CurrencySymbol:
                case UnicodeCategory.ModifierSymbol:
                case UnicodeCategory.OtherSymbol:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The selection exactly as made, whitespace aside, in Unicode form.</summary>
        public static string Exact(ISegment segment, string fullText, string selection)
        {
            var s = (selection ?? "").Trim();
            return ScriptFormatting.Apply(segment, fullText, s);
        }

        /// <summary>
        /// For the shortcuts that save without a dialog. Returns the text to
        /// save: the trimmed text when nothing was lost or the user chooses it,
        /// the exact selection when they choose that, null to save nothing.
        /// </summary>
        public static string Confirm(string selection, string trimmed, ISegment segment, string fullText, string title)
        {
            if (!LostContent(selection, trimmed)) return trimmed;
            var exact = Exact(segment, fullText, selection);
            if (string.Equals(exact, trimmed, StringComparison.Ordinal)) return trimmed;

            var answer = MessageBox.Show(
                "The selection\n\n    " + exact + "\n\nwas trimmed to\n\n    " + trimmed + "\n\n"
                + "which drops a letter, digit or symbol. Trimming is meant for edges such as a "
                + "trailing comma, so this is probably not what you want.\n\n"
                + "Yes – save the selection as it is\n"
                + "No – save the trimmed text\n"
                + "Cancel – save nothing",
                title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.Yes) return exact;
            if (answer == DialogResult.No) return trimmed;
            return null;
        }

        /// <summary>
        /// For the shortcut that opens the term editor: no question, the editor
        /// shows the text. The exact selection when content was lost, else the
        /// trimmed text.
        /// </summary>
        public static string Prefer(string selection, string trimmed, ISegment segment, string fullText)
        {
            return LostContent(selection, trimmed) ? Exact(segment, fullText, selection) : trimmed;
        }
    }
}
