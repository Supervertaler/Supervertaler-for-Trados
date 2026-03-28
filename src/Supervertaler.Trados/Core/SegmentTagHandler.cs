using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.FileTypeSupport.Framework.BilingualApi;

namespace Supervertaler.Trados.Core
{
    // ─── Data Types ──────────────────────────────────────────

    public enum TagType
    {
        Paired,
        Standalone
    }

    /// <summary>
    /// Stores information about a single tag encountered during serialization.
    /// Used to map numbered placeholders back to original Trados markup.
    /// </summary>
    public class TagInfo
    {
        public TagType TagType { get; set; }

        /// <summary>
        /// Reference to the original IAbstractMarkupData (ITagPair or IPlaceholderTag).
        /// Used for cloning when reconstructing the target segment.
        /// </summary>
        public IAbstractMarkupData OriginalMarkup { get; set; }

        /// <summary>
        /// True when this standalone tag represents a line break (soft return).
        /// Used to recover from LLMs that emit a literal '\n' instead of the &lt;tN/&gt; placeholder.
        /// </summary>
        public bool IsLineBreak { get; set; }
    }

    /// <summary>
    /// Result of serializing a Trados segment into text with tag placeholders.
    /// </summary>
    public class SegmentSerializationResult
    {
        public string SerializedText { get; set; }
        public bool HasTags { get; set; }
        public Dictionary<int, TagInfo> TagMap { get; set; } = new Dictionary<int, TagInfo>();
    }

    // ─── Parsed elements (intermediate representation) ───────

    internal abstract class ParsedElement { }

    internal class ParsedText : ParsedElement
    {
        public string Text { get; set; }
    }

    internal class ParsedOpenTag : ParsedElement
    {
        public int TagNumber { get; set; }
        public List<ParsedElement> Children { get; set; } = new List<ParsedElement>();
    }

    internal class ParsedStandaloneTag : ParsedElement
    {
        public int TagNumber { get; set; }
    }

    // ─── Main Handler ────────────────────────────────────────

    /// <summary>
    /// Handles serialization of Trados ISegment content into LLM-friendly text
    /// with numbered tag placeholders, and reconstruction of target segments
    /// from LLM responses back into proper Trados markup.
    ///
    /// Tag placeholder format:
    ///   Paired tags:     &lt;t1&gt;content&lt;/t1&gt;
    ///   Standalone tags: &lt;t2/&gt;
    ///
    /// This allows LLMs to reposition tags naturally in the target language
    /// while preserving the exact Trados tag objects (formatting, field codes, etc.).
    /// </summary>
    public static class SegmentTagHandler
    {
        // Regex for parsing tag placeholders in LLM output
        // Matches: <t1>, </t1>, <t2/>
        private static readonly Regex TagPlaceholderPattern =
            new Regex(@"<t(\d+)\s*/>|</t(\d+)>|<t(\d+)>", RegexOptions.Compiled);

        // ─── Serialization ───────────────────────────────────

        /// <summary>
        /// Serializes a Trados ISegment into plain text with numbered tag placeholders.
        /// Walks the segment tree depth-first, replacing ITagPair and IPlaceholderTag
        /// with &lt;tN&gt;...&lt;/tN&gt; and &lt;tN/&gt; respectively.
        /// </summary>
        public static SegmentSerializationResult Serialize(ISegment segment)
        {
            var result = new SegmentSerializationResult();
            if (segment == null)
            {
                result.SerializedText = "";
                return result;
            }

            var sb = new StringBuilder();
            int tagCounter = 0;

            SerializeContainer(segment, sb, result.TagMap, ref tagCounter);

            result.SerializedText = sb.ToString();
            result.HasTags = tagCounter > 0;
            return result;
        }

        private static void SerializeContainer(
            IAbstractMarkupDataContainer container,
            StringBuilder sb,
            Dictionary<int, TagInfo> tagMap,
            ref int tagCounter)
        {
            foreach (var item in container)
            {
                if (item is IText textItem)
                {
                    sb.Append(textItem.Properties.Text);
                }
                else if (item is ITagPair tagPair)
                {
                    tagCounter++;
                    int tagNum = tagCounter;
                    tagMap[tagNum] = new TagInfo
                    {
                        TagType = TagType.Paired,
                        OriginalMarkup = tagPair
                    };

                    sb.Append("<t").Append(tagNum).Append('>');
                    SerializeContainer(tagPair, sb, tagMap, ref tagCounter);
                    sb.Append("</t").Append(tagNum).Append('>');
                }
                else if (item is IPlaceholderTag placeholder)
                {
                    tagCounter++;
                    int tagNum = tagCounter;
                    tagMap[tagNum] = new TagInfo
                    {
                        TagType = TagType.Standalone,
                        OriginalMarkup = placeholder,
                        IsLineBreak = IsLineBreakTag(placeholder)
                    };

                    sb.Append("<t").Append(tagNum).Append("/>");
                }
                else if (item is IRevisionMarker revision)
                {
                    // Tracked changes: skip deleted text, include inserted/unchanged
                    if (revision.Properties.RevisionType != RevisionType.Delete)
                    {
                        SerializeContainer(revision, sb, tagMap, ref tagCounter);
                    }
                }
                else if (item is IAbstractMarkupDataContainer nestedContainer)
                {
                    // ILockedContent, etc. — treat as paired tag
                    tagCounter++;
                    int tagNum = tagCounter;
                    tagMap[tagNum] = new TagInfo
                    {
                        TagType = TagType.Paired,
                        OriginalMarkup = item
                    };

                    sb.Append("<t").Append(tagNum).Append('>');
                    SerializeContainer(nestedContainer, sb, tagMap, ref tagCounter);
                    sb.Append("</t").Append(tagNum).Append('>');
                }
                else
                {
                    // Unknown markup data — emit as standalone placeholder
                    tagCounter++;
                    int tagNum = tagCounter;
                    tagMap[tagNum] = new TagInfo
                    {
                        TagType = TagType.Standalone,
                        OriginalMarkup = item
                    };

                    sb.Append("<t").Append(tagNum).Append("/>");
                }
            }
        }

        // ─── Line-break Detection ────────────────────────────

        /// <summary>
        /// Returns true when a placeholder tag represents a soft return (line break),
        /// as opposed to a formatting tag, field code, etc.
        /// Checks TextEquivalent for newline characters and TagContent for br markup.
        /// </summary>
        private static bool IsLineBreakTag(IPlaceholderTag placeholder)
        {
            try
            {
                var props = placeholder.Properties;
                if (props == null) return false;

                // TextEquivalent is the most reliable indicator: soft returns have "\n" or "\r\n"
                var te = props.TextEquivalent;
                if (!string.IsNullOrEmpty(te) && (te.Contains("\n") || te.Contains("\r")))
                    return true;

                // TagContent fallback: Word soft returns contain "w:br",
                // HTML line breaks contain "<br"
                var tc = props.TagContent;
                if (!string.IsNullOrEmpty(tc) &&
                    (tc.IndexOf("w:br", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tc.IndexOf("<br", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        // ─── Reconstruction ──────────────────────────────────

        /// <summary>
        /// Reconstructs a target ISegment from the LLM's translated text.
        /// Parses tag placeholders, clones the corresponding source tags,
        /// and builds the target segment tree.
        ///
        /// Returns true if reconstruction succeeded (tags were found and placed).
        /// Returns false if parsing failed — caller should fall back to plain-text insertion.
        /// </summary>
        public static bool ReconstructTarget(
            ISegment targetSegment,
            ISegment sourceSegment,
            string translatedText,
            Dictionary<int, TagInfo> tagMap)
        {
            if (targetSegment == null || string.IsNullOrEmpty(translatedText) || tagMap == null)
                return false;

            try
            {
                // Parse the translated text into a tree of elements
                var elements = ParseTranslation(translatedText, tagMap);
                if (elements == null)
                    return false;

                // Find an IText node to use as a clone template
                IText textTemplate = FindFirstText(sourceSegment);
                if (textTemplate == null)
                {
                    // No text node in source — cannot create text in target via cloning
                    return false;
                }

                // Clear the target segment
                targetSegment.Clear();

                // Add parsed elements to the target
                AddElementsToContainer(targetSegment, elements, tagMap, textTemplate);

                return true;
            }
            catch
            {
                // Any reconstruction failure → caller falls back to plain text
                return false;
            }
        }

        /// <summary>
        /// Parses LLM translation output containing tag placeholders into a tree
        /// of ParsedElement objects. Handles nesting (paired tags containing text
        /// and other tags).
        /// </summary>
        internal static List<ParsedElement> ParseTranslation(
            string text, Dictionary<int, TagInfo> tagMap)
        {
            var tokens = Tokenize(text);
            if (tokens == null) return null;

            // Build tree using a stack for nesting
            var rootChildren = new List<ParsedElement>();
            var stack = new Stack<ParsedOpenTag>();

            List<ParsedElement> CurrentList() =>
                stack.Count > 0 ? stack.Peek().Children : rootChildren;

            foreach (var token in tokens)
            {
                if (token is TokenText tt)
                {
                    if (!string.IsNullOrEmpty(tt.Text))
                        CurrentList().Add(new ParsedText { Text = tt.Text });
                }
                else if (token is TokenStandaloneTag st)
                {
                    CurrentList().Add(new ParsedStandaloneTag { TagNumber = st.TagNumber });
                }
                else if (token is TokenOpenTag ot)
                {
                    var node = new ParsedOpenTag { TagNumber = ot.TagNumber };
                    CurrentList().Add(node);
                    stack.Push(node);
                }
                else if (token is TokenCloseTag ct)
                {
                    if (stack.Count > 0 && stack.Peek().TagNumber == ct.TagNumber)
                    {
                        stack.Pop();
                    }
                    else
                    {
                        // Mismatched close tag — LLM error; treat remainder as plain text
                        // Pop until we find the matching open tag, or give up
                        bool found = false;
                        var tempStack = new Stack<ParsedOpenTag>();
                        while (stack.Count > 0)
                        {
                            var top = stack.Pop();
                            if (top.TagNumber == ct.TagNumber)
                            {
                                found = true;
                                break;
                            }
                            tempStack.Push(top);
                        }

                        if (!found)
                        {
                            // Restore stack and ignore the close tag
                            while (tempStack.Count > 0)
                                stack.Push(tempStack.Pop());
                        }
                        // If found, the mismatched inner tags are left as children
                    }
                }
            }

            // Any unclosed tags remain in the tree as-is (best effort)
            return rootChildren;
        }

        // ─── Tokenizer ──────────────────────────────────────

        internal abstract class Token { }
        internal class TokenText : Token { public string Text; }
        internal class TokenOpenTag : Token { public int TagNumber; }
        internal class TokenCloseTag : Token { public int TagNumber; }
        internal class TokenStandaloneTag : Token { public int TagNumber; }

        /// <summary>
        /// Tokenizes a translated string into a flat list of text and tag tokens.
        /// </summary>
        internal static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int lastEnd = 0;

            foreach (Match m in TagPlaceholderPattern.Matches(text))
            {
                // Text before this match
                if (m.Index > lastEnd)
                {
                    tokens.Add(new TokenText
                    {
                        Text = text.Substring(lastEnd, m.Index - lastEnd)
                    });
                }

                if (m.Groups[1].Success)
                {
                    // Standalone: <tN/>
                    tokens.Add(new TokenStandaloneTag
                    {
                        TagNumber = int.Parse(m.Groups[1].Value)
                    });
                }
                else if (m.Groups[2].Success)
                {
                    // Close: </tN>
                    tokens.Add(new TokenCloseTag
                    {
                        TagNumber = int.Parse(m.Groups[2].Value)
                    });
                }
                else if (m.Groups[3].Success)
                {
                    // Open: <tN>
                    tokens.Add(new TokenOpenTag
                    {
                        TagNumber = int.Parse(m.Groups[3].Value)
                    });
                }

                lastEnd = m.Index + m.Length;
            }

            // Trailing text
            if (lastEnd < text.Length)
            {
                tokens.Add(new TokenText
                {
                    Text = text.Substring(lastEnd)
                });
            }

            return tokens;
        }

        // ─── Segment Building ────────────────────────────────

        /// <summary>
        /// Adds a list of parsed elements to a Trados markup data container (ISegment or ITagPair).
        /// Creates IText by cloning the template, and clones source tags from the tag map.
        /// </summary>
        private static void AddElementsToContainer(
            IAbstractMarkupDataContainer container,
            List<ParsedElement> elements,
            Dictionary<int, TagInfo> tagMap,
            IText textTemplate)
        {
            foreach (var element in elements)
            {
                if (element is ParsedText pt)
                {
                    if (!string.IsNullOrEmpty(pt.Text))
                    {
                        // If the LLM emitted a literal newline instead of a <tN/> placeholder,
                        // split it and re-insert the appropriate line-break tag from the source.
                        if (pt.Text.IndexOf('\n') >= 0 || pt.Text.IndexOf('\r') >= 0)
                            InsertTextWithLineBreaks(container, pt.Text, tagMap, textTemplate);
                        else
                        {
                            var textClone = (IText)textTemplate.Clone();
                            textClone.Properties.Text = pt.Text;
                            container.Add(textClone);
                        }
                    }
                }
                else if (element is ParsedStandaloneTag st)
                {
                    TagInfo tagInfo;
                    if (tagMap.TryGetValue(st.TagNumber, out tagInfo) &&
                        tagInfo.OriginalMarkup != null)
                    {
                        var clone = (IAbstractMarkupData)tagInfo.OriginalMarkup.Clone();
                        container.Add(clone);
                    }
                    // If tag not found in map (LLM invented a tag), skip silently
                }
                else if (element is ParsedOpenTag ot)
                {
                    TagInfo tagInfo;
                    if (tagMap.TryGetValue(ot.TagNumber, out tagInfo) &&
                        tagInfo.OriginalMarkup != null)
                    {
                        // Clone the tag pair and clear its content — we'll rebuild inside
                        var clone = (IAbstractMarkupData)tagInfo.OriginalMarkup.Clone();

                        if (clone is IAbstractMarkupDataContainer tagContainer)
                        {
                            tagContainer.Clear();
                            // Add child elements inside the cloned tag pair
                            AddElementsToContainer(tagContainer, ot.Children, tagMap, textTemplate);
                        }

                        container.Add(clone);
                    }
                    else
                    {
                        // Unknown tag number — add children as plain content (skip the tag wrapper)
                        AddElementsToContainer(container, ot.Children, tagMap, textTemplate);
                    }
                }
            }
        }

        /// <summary>
        /// Splits text at newline characters and inserts a cloned source line-break tag
        /// between each piece. Called when the LLM emits a literal '\n' instead of the
        /// &lt;tN/&gt; placeholder for a soft return.
        ///
        /// If the source segment contained no line-break tags, the newlines are simply
        /// dropped (no bare '\n' is ever written into an IText node).
        /// </summary>
        private static void InsertTextWithLineBreaks(
            IAbstractMarkupDataContainer container,
            string text,
            Dictionary<int, TagInfo> tagMap,
            IText textTemplate)
        {
            // Find the first line-break tag in the source tag map
            TagInfo lineBreakInfo = null;
            foreach (var kv in tagMap)
            {
                if (kv.Value.IsLineBreak)
                {
                    lineBreakInfo = kv.Value;
                    break;
                }
            }

            // Normalise \r\n and bare \r to \n, then split
            var normalised = text.Replace("\r\n", "\n").Replace("\r", "\n");
            var parts = normalised.Split('\n');

            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i]))
                {
                    var textClone = (IText)textTemplate.Clone();
                    textClone.Properties.Text = parts[i];
                    container.Add(textClone);
                }

                // Insert line-break tag between parts — not after the last one
                if (i < parts.Length - 1 && lineBreakInfo != null)
                {
                    var tagClone = (IAbstractMarkupData)lineBreakInfo.OriginalMarkup.Clone();
                    container.Add(tagClone);
                }
            }
        }

        // ─── Tracked Changes ────────────────────────────────

        /// <summary>
        /// Returns the final (accepted) plain text of a segment, stripping deleted
        /// tracked changes and keeping only inserted/current text.
        /// Use this instead of segment.ToString() when tracked changes may be present.
        /// </summary>
        public static string GetFinalText(ISegment segment)
        {
            if (segment == null) return "";
            var sb = new StringBuilder();
            AppendFinalText(segment, sb);
            return sb.ToString();
        }

        private static void AppendFinalText(IAbstractMarkupDataContainer container, StringBuilder sb)
        {
            foreach (var item in container)
            {
                if (item is IRevisionMarker revision)
                {
                    // Skip deleted text entirely; include inserted/unchanged text
                    if (revision.Properties.RevisionType != RevisionType.Delete)
                    {
                        AppendFinalText(revision, sb);
                    }
                }
                else if (item is IText textItem)
                {
                    sb.Append(textItem.Properties.Text);
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    AppendFinalText(nested, sb);
                }
                // Standalone tags, placeholders, etc. — skip (plain text only)
            }
        }

        // ─── Helpers ─────────────────────────────────────────

        /// <summary>
        /// Finds the first IText node in a segment (depth-first).
        /// Used as a clone template for creating new text nodes in the target.
        /// </summary>
        public static IText FindFirstText(ISegment segment)
        {
            if (segment == null) return null;

            foreach (var item in segment.AllSubItems)
            {
                if (item is IText text)
                    return text;
            }

            return null;
        }

        /// <summary>
        /// Strips all tag placeholders from text, returning only the human-readable content.
        /// Useful for fallback scenarios.
        /// </summary>
        public static string StripTagPlaceholders(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return TagPlaceholderPattern.Replace(text, "");
        }

        /// <summary>
        /// Checks whether a translated string still contains all expected tag placeholders.
        /// Returns true if all tags from the map are present in the translation.
        /// </summary>
        public static bool ValidateTagsPresent(string translation, Dictionary<int, TagInfo> tagMap)
        {
            if (tagMap == null || tagMap.Count == 0) return true;
            if (string.IsNullOrEmpty(translation)) return false;

            foreach (var kvp in tagMap)
            {
                int n = kvp.Key;
                var info = kvp.Value;

                if (info.TagType == TagType.Paired)
                {
                    if (!translation.Contains("<t" + n + ">") ||
                        !translation.Contains("</t" + n + ">"))
                        return false;
                }
                else
                {
                    if (!translation.Contains("<t" + n + "/>"))
                        return false;
                }
            }

            return true;
        }
    }
}
