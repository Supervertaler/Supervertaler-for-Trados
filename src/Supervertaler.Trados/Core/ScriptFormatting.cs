using System;
using System.Text;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Turns Studio's subscript / superscript FORMATTING into the equivalent
    /// Unicode characters, for the text a term is saved from.
    ///
    /// <para>Documents write chemical formulas both ways, in the same segment:
    /// H₂O₂ as Unicode subscript characters, and ClO₃⁻ as a plain "3" and "-"
    /// inside subscript / superscript formatting tags. The formatting is markup,
    /// not text, so reading the selection dropped it and "ClO₃⁻" was saved as
    /// "ClO3-", while "H₂O₂" kept its subscripts. A termbase that records the
    /// same formula in two spellings depending on how a client happened to type
    /// it is what this ends: a term is stored in the Unicode form whichever way
    /// the document wrote it. The document itself is never touched.</para>
    ///
    /// <para>Only digits, the signs + - =, and parentheses are converted; the
    /// Unicode blocks have those. Letters in sub/superscript are left as they
    /// are.</para>
    /// </summary>
    public static class ScriptFormatting
    {
        public enum Position { Normal, Superscript, Subscript }

        /// <summary>
        /// The text with every convertible character rendered at
        /// <paramref name="position"/>. Same length as the input: every
        /// conversion is one character for one, which is what lets a span found
        /// in the plain text be lifted from the rendered text by index.
        /// </summary>
        public static string ToUnicode(string text, Position position)
        {
            if (string.IsNullOrEmpty(text) || position == Position.Normal) return text ?? "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text) sb.Append(Convert(c, position));
            return sb.ToString();
        }

        private static char Convert(char c, Position position)
        {
            var sup = position == Position.Superscript;
            if (c >= '0' && c <= '9')
            {
                if (!sup) return (char)('₀' + (c - '0'));
                switch (c)
                {
                    case '1': return '¹';
                    case '2': return '²';
                    case '3': return '³';
                    case '0': return '⁰';
                    default: return (char)('⁰' + (c - '0'));   // ⁴-⁹ are contiguous from U+2074
                }
            }
            switch (c)
            {
                case '+': return sup ? '⁺' : '₊';
                case '-': return sup ? '⁻' : '₋';
                case '−': return sup ? '⁻' : '₋';   // a real minus sign, the same way
                case '=': return sup ? '⁼' : '₌';
                case '(': return sup ? '⁽' : '₍';
                case ')': return sup ? '⁾' : '₎';
                default: return c;
            }
        }

        /// <summary>
        /// The segment's final text (as <see cref="SegmentTagHandler.GetFinalText"/>
        /// gives it, deleted revisions skipped) with the text inside subscript /
        /// superscript formatting rendered as Unicode. Character-for-character
        /// aligned with the plain text.
        /// </summary>
        public static string RenderWithUnicode(ISegment segment)
        {
            if (segment == null) return "";
            var sb = new StringBuilder();
            Append(segment, sb, Position.Normal);
            return sb.ToString();
        }

        private static void Append(IAbstractMarkupDataContainer container, StringBuilder sb, Position position)
        {
            foreach (var item in container)
            {
                if (item is IRevisionMarker revision)
                {
                    if (revision.Properties.RevisionType != RevisionType.Delete)
                        Append(revision, sb, position);
                }
                else if (item is IText textItem)
                {
                    sb.Append(ToUnicode(textItem.Properties.Text, position));
                }
                else if (item is ITagPair pair)
                {
                    // The innermost formatting wins; a pair that says nothing
                    // about text position inherits its parent's.
                    var here = DetectPosition(pair);
                    Append(pair, sb, here ?? position);
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    Append(nested, sb, position);
                }
            }
        }

        /// <summary>
        /// Whether a tag pair sets the text position, and to what. Null when it
        /// does not. Two probes, like the export tag namer: the typed formatting
        /// group first, then the raw tag content for filters that do not fill it.
        /// </summary>
        internal static Position? DetectPosition(ITagPair pair)
        {
            if (pair?.StartTagProperties == null) return null;
            try
            {
                var formatting = pair.StartTagProperties.Formatting;
                if (formatting != null)
                {
                    foreach (var key in formatting.Keys)
                    {
                        if ((key ?? "").IndexOf("position", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var item = formatting[key];
                        var value = item?.StringValue ?? item?.ToString() ?? "";
                        var p = Parse(value);
                        if (p != null) return p;
                    }
                }
            }
            catch { }
            try
            {
                var content = pair.StartTagProperties.TagContent ?? "";
                var p = Parse(content);
                if (p != null) return p;
            }
            catch { }
            return null;
        }

        private static Position? Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.IndexOf("superscript", StringComparison.OrdinalIgnoreCase) >= 0) return Position.Superscript;
            if (value.IndexOf("subscript", StringComparison.OrdinalIgnoreCase) >= 0) return Position.Subscript;
            return null;
        }

        /// <summary>
        /// <paramref name="expandedPlain"/> - a selection already expanded to
        /// word edges against <paramref name="plainText"/> - in its Unicode
        /// form. The span is located in the plain text and lifted from the
        /// aligned rendering; when it cannot be located, or nothing in the
        /// segment is formatted, the input comes back unchanged.
        /// </summary>
        public static string Apply(ISegment segment, string plainText, string expandedPlain)
        {
            if (segment == null || string.IsNullOrEmpty(expandedPlain) || string.IsNullOrEmpty(plainText))
                return expandedPlain;
            try
            {
                var rendered = RenderWithUnicode(segment);
                if (rendered.Length != plainText.Length || string.Equals(rendered, plainText, StringComparison.Ordinal))
                    return expandedPlain;
                var idx = plainText.IndexOf(expandedPlain, StringComparison.Ordinal);
                if (idx < 0) idx = plainText.IndexOf(expandedPlain, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return expandedPlain;
                return rendered.Substring(idx, expandedPlain.Length);
            }
            catch { return expandedPlain; }
        }
    }
}
