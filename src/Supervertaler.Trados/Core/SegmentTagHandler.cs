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
        /// <summary>
        /// Optional diagnostic callback. When set, fires a string message when
        /// line-break detection fails so the batch translate log can show it.
        /// </summary>
        public static Action<string> DiagnosticMessage { get; set; }

        // Regex for parsing tag placeholders in LLM output.
        // Canonical forms: <t1>, </t1>, <t2/>. The pattern is deliberately
        // whitespace- and case-tolerant (< t1 >, </ t1 >, <T1/>, <t1 />) so that
        // a model which drifts slightly from the canonical form (observed with
        // Mistral Large) is still recognised and reconstructed — instead of the
        // literal placeholder text leaking into the target because neither the
        // tokenizer nor StripTagPlaceholders matched it.
        private static readonly Regex TagPlaceholderPattern =
            new Regex(@"<\s*t\s*(\d+)\s*/\s*>|<\s*/\s*t\s*(\d+)\s*>|<\s*t\s*(\d+)\s*>",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// A segment's text as the model should read it: numbered placeholders for
        /// inline tags - the same notation Batch Translate sends the segments in -
        /// never Studio's native markup. <c>ISegment.ToString()</c> renders that
        /// markup (<c>&lt;cf bold=True&gt;</c> and friends), and every rendering of
        /// document CONTEXT used it, so one request carried the document in two
        /// notations at once and AutoPrompt described tags the batch never sends
        /// (#97). Every prompt-bound rendering goes through here now.
        ///
        /// Not the bridge's semantic naming (<c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>): that
        /// is a third notation, for MCP clients. Context and segments in one request
        /// must read the same, and the segments use plain placeholders.
        ///
        /// Line and paragraph separators (U+2028/9) become spaces, as the chat path
        /// already did for its own reasons. Never throws: a serialisation quirk
        /// falls back to the plain text, and the plain text falls back to empty -
        /// but never to ToString(), which is the thing being kept out.
        /// </summary>
        public static string ToModelText(ISegment segment)
        {
            if (segment == null) return "";
            string text;
            try { text = Serialize(segment).SerializedText ?? ""; }
            catch
            {
                try { text = GetFinalText(segment) ?? ""; }
                catch { return ""; }
            }
            return text.Replace("\u2028", " ").Replace("\u2029", " ");
        }

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
                else if (item is ICommentMarker comment)
                {
                    // A comment is NOT an inline tag. It wraps target text as
                    // markup, but Studio renders it as a coloured highlight and
                    // shows no tag in the editor - so emitting <tN> here invented
                    // a tag the translator cannot see and did not put there.
                    //
                    // What that cost before this branch existed: every commented
                    // segment failed check_tags as "source has 0 inline tag(s),
                    // target has 1" (field report: a 213-segment patent with 15
                    // comments produced exactly 15 phantom findings, and the
                    // count-vs-comment correlation was the only way to spot it).
                    // It also handed the AI a <tN> to faithfully reproduce in its
                    // translation, in text the user sees as unmarked.
                    //
                    // Skipping it here loses nothing: comments live only in the
                    // target and are already carried across a write out-of-band
                    // by CaptureCommentMarkers/OpenCommentMarkers, and
                    // CollectTagIds likewise walks through them without
                    // collecting an id.
                    SerializeContainer(comment, sb, tagMap, ref tagCounter);
                }
                else if (item is IAbstractMarkupDataContainer nestedContainer)
                {
                    // ILockedContent, etc. – treat as paired tag
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
                    // Unknown markup data – emit as standalone placeholder
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

                // TextEquivalent: soft returns typically have "\n" or "\r\n"
                var te = props.TextEquivalent;
                if (!string.IsNullOrEmpty(te) && (te.Contains("\n") || te.Contains("\r")))
                    return true;

                // DisplayText: Trados displays soft returns as "↵" (U+21B5, downwards arrow with corner leftwards).
                // This is the most reliable indicator in practice for SDLXLIFF/DOCX sources.
                var dt = props.DisplayText;
                if (!string.IsNullOrEmpty(dt) && dt.Contains("\u21b5"))
                    return true;

                // TagContent: raw markup from the source format.
                // DOCX soft return: contains "w:br"
                // HTML line break: contains "<br"
                // SDLXLIFF ph inner content: may contain "↵" (U+21B5) or "ctype=\"lb\""
                var tc = props.TagContent;
                if (!string.IsNullOrEmpty(tc) &&
                    (tc.IndexOf("w:br", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tc.IndexOf("<br", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tc.Contains("\u21b5") ||
                     tc.IndexOf("ctype=\"lb\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     tc.IndexOf("ctype='lb'", StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns a debug string describing a placeholder tag's key properties.
        /// Used to diagnose IsLineBreakTag failures via the batch translate log.
        /// </summary>
        internal static string DescribePlaceholderTag(IPlaceholderTag placeholder)
        {
            try
            {
                var p = placeholder.Properties;
                if (p == null) return "(null props)";
                return $"TextEquivalent={Repr(p.TextEquivalent)} DisplayText={Repr(p.DisplayText)} TagContent={Repr(p.TagContent)}";
            }
            catch (Exception ex) { return $"(error: {ex.Message})"; }
        }

        private static string Repr(string s)
        {
            if (s == null) return "null";
            if (s.Length == 0) return "\"\"";
            // Show control chars and keep it short
            var sb = new StringBuilder("\"");
            foreach (var c in s.Length > 40 ? s.Substring(0, 40) + "…" : s)
            {
                if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ─── Comment preservation across a target rewrite ────

        /// <summary>
        /// The comment markers currently on a target, outermost first.
        ///
        /// Comments live ONLY in the target's markup (a Studio comment is not
        /// part of the source), so any rebuild that clears the target and
        /// replays the SOURCE's tags drops them. That made the ordinary
        /// delivery workflow — read the comments, fix the segments they refer
        /// to — delete its own comments one segment at a time, silently, with
        /// the write still reporting success. Field report: job QLEV-008-BE,
        /// segment 50, verified gone from the saved SDLXLIFF.
        /// </summary>
        public static List<ICommentMarker> CaptureCommentMarkers(IAbstractMarkupDataContainer container)
        {
            var list = new List<ICommentMarker>();
            CollectCommentMarkers(container, list);
            return list;
        }

        private static void CollectCommentMarkers(
            IAbstractMarkupDataContainer container, List<ICommentMarker> list)
        {
            if (container == null) return;
            foreach (var item in container)
            {
                if (item is ICommentMarker marker)
                {
                    list.Add(marker);
                    CollectCommentMarkers(marker, list);
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    CollectCommentMarkers(nested, list);
                }
            }
        }

        /// <summary>
        /// Rebuilds the captured comment markers into a freshly cleared target
        /// and returns the container the new content must be written into (the
        /// innermost marker), so the content ends up wrapped by them.
        ///
        /// Re-anchoring is deliberately coarse: whatever span a marker covered
        /// before, it now covers the whole new target. The old span cannot
        /// survive a rewrite that replaces the text it pointed into, and a
        /// comment attached to slightly too much text is vastly better than a
        /// comment silently deleted. Returns <paramref name="target"/> itself
        /// when there is nothing to restore or restoration fails, so a write
        /// never fails on account of a comment.
        /// </summary>
        public static IAbstractMarkupDataContainer OpenCommentMarkers(
            IAbstractMarkupDataContainer target, List<ICommentMarker> markers)
        {
            if (target == null || markers == null || markers.Count == 0) return target;
            try
            {
                ICommentMarker outermost = null, innermost = null;
                foreach (var original in markers)
                {
                    var clone = original?.Clone() as ICommentMarker;
                    if (clone == null) continue;
                    clone.Clear(); // keep the Comments, drop the stale content
                    if (outermost == null) { outermost = clone; innermost = clone; }
                    else { innermost.Add(clone); innermost = clone; }
                }
                if (outermost == null) return target;
                target.Add(outermost);
                return innermost;
            }
            catch
            {
                return target;
            }
        }

        /// <summary>True when every captured comment survived into the target –
        /// the post-write check behind update_segments' comment warning.</summary>
        public static bool CommentsPreserved(IAbstractMarkupDataContainer target, int expected)
        {
            if (expected <= 0) return true;
            try { return CaptureCommentMarkers(target).Count >= expected; }
            catch { return false; }
        }

        // ─── Reconstruction ──────────────────────────────────

        /// <summary>
        /// Reconstructs a target ISegment from the LLM's translated text.
        /// Parses tag placeholders, clones the corresponding source tags,
        /// and builds the target segment tree.
        ///
        /// Returns true if reconstruction succeeded (tags were found and placed).
        /// Returns false if parsing failed – caller should fall back to plain-text insertion.
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
                    // No text node in source – cannot create text in target via cloning
                    return false;
                }

                // Check whether the source stores line breaks as literal \n in IText nodes
                // (e.g. Visio, Excel) rather than as separate IPlaceholderTag elements (DOCX).
                bool sourceHasTextNewlines = SourceTextContainsNewlines(sourceSegment);

                // Comments live only on the target, and Clear() below would take
                // them with it – see CaptureCommentMarkers. Capture, then rebuild
                // the marker chain and write the new content INSIDE it.
                var commentMarkers = CaptureCommentMarkers(targetSegment);

                // Clear the target segment
                targetSegment.Clear();

                var destination = OpenCommentMarkers(targetSegment, commentMarkers);

                // Each tag number may be materialised at most once. Cloning the
                // same TagInfo twice produces two Trados tags carrying the SAME
                // underlying tag id, which Studio's Tag Verifier reports as
                // "Duplicated tag with id 'N'" — and which no tag *count* check
                // will ever catch, because the count is right. A second <tN> is
                // an authoring error (LLM repeated a marker); degrade by dropping
                // the wrapper and keeping its content inline, which is what the
                // unknown-tag-number branch already does.
                var usedTagNumbers = new HashSet<int>();

                // Add parsed elements to the target (inside the comment markers
                // when the segment carried any).
                AddElementsToContainer(destination, elements, tagMap, textTemplate,
                    sourceHasTextNewlines, usedTagNumbers);

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
                        // Mismatched close tag – LLM error; treat remainder as plain text
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
        /// <param name="usedTagNumbers">Tag numbers already materialised in this
        /// reconstruction. Null disables the guard (kept for callers that have not
        /// been updated); ReconstructTarget always supplies one.</param>
        private static void AddElementsToContainer(
            IAbstractMarkupDataContainer container,
            List<ParsedElement> elements,
            Dictionary<int, TagInfo> tagMap,
            IText textTemplate,
            bool sourceHasTextNewlines = false,
            HashSet<int> usedTagNumbers = null)
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
                            InsertTextWithLineBreaks(container, pt.Text, tagMap, textTemplate,
                                sourceHasTextNewlines);
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
                    // usedTagNumbers.Add returns false when this number was already
                    // materialised – see the note in ReconstructTarget.
                    if (tagMap.TryGetValue(st.TagNumber, out tagInfo) &&
                        tagInfo.OriginalMarkup != null &&
                        (usedTagNumbers == null || usedTagNumbers.Add(st.TagNumber)))
                    {
                        var clone = (IAbstractMarkupData)tagInfo.OriginalMarkup.Clone();
                        container.Add(clone);
                    }
                    // If tag not found in map (LLM invented a tag), or already used,
                    // skip silently – a standalone tag has no content to preserve.
                }
                else if (element is ParsedOpenTag ot)
                {
                    TagInfo tagInfo;
                    if (tagMap.TryGetValue(ot.TagNumber, out tagInfo) &&
                        tagInfo.OriginalMarkup != null &&
                        (usedTagNumbers == null || usedTagNumbers.Add(ot.TagNumber)))
                    {
                        // Clone the tag pair and clear its content – we'll rebuild inside
                        var clone = (IAbstractMarkupData)tagInfo.OriginalMarkup.Clone();

                        if (clone is IAbstractMarkupDataContainer tagContainer)
                        {
                            tagContainer.Clear();
                            // Add child elements inside the cloned tag pair
                            AddElementsToContainer(tagContainer, ot.Children, tagMap, textTemplate,
                                sourceHasTextNewlines, usedTagNumbers);
                        }

                        container.Add(clone);
                    }
                    else
                    {
                        // Unknown or already-used tag number – add children as plain
                        // content (skip the tag wrapper). Losing one bold run beats
                        // writing a duplicate tag id, which fails verification.
                        AddElementsToContainer(container, ot.Children, tagMap, textTemplate,
                            sourceHasTextNewlines, usedTagNumbers);
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
            IText textTemplate,
            bool sourceHasTextNewlines = false)
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

            if (lineBreakInfo == null)
            {
                // Check whether the source had any standalone tags at all.
                bool hasAnyStandalone = false;
                foreach (var kv in tagMap)
                {
                    if (kv.Value.TagType == TagType.Standalone)
                    {
                        hasAnyStandalone = true;
                        break;
                    }
                }

                if (!hasAnyStandalone || sourceHasTextNewlines)
                {
                    // Either no standalone tags exist, or the source IText nodes already
                    // contain literal \n characters (e.g. Visio, Excel, plain text).
                    // In both cases, the file format stores line breaks as text content,
                    // not as separate placeholder tags. Preserve the \n so the file type
                    // handler can write it back correctly to the target format.
                    var textClone = (IText)textTemplate.Clone();
                    textClone.Properties.Text = text.Replace("\r\n", "\n").Replace("\r", "\n");
                    container.Add(textClone);
                    return;
                }

                // Standalone tags exist but none were identified as line breaks,
                // and the source text didn't have embedded newlines either.
                // Emit a diagnostic so we can tune detection, and drop the bare \n
                // (better to lose the line break than write a \n into IText for a DOCX
                // segment, which Trados would convert to a paragraph break).
                var sb2 = new StringBuilder("[LineBreak diag] No IsLineBreak tag found. Standalone tags in map: ");
                foreach (var kv in tagMap)
                {
                    if (kv.Value.TagType != TagType.Standalone) continue;
                    var markup = kv.Value.OriginalMarkup;
                    if (markup is IPlaceholderTag ph)
                        sb2.Append($"t{kv.Key}=IPlaceholderTag({DescribePlaceholderTag(ph)}) ");
                    else if (markup != null)
                        sb2.Append($"t{kv.Key}={markup.GetType().Name}(no props) ");
                    else
                        sb2.Append($"t{kv.Key}=null ");
                }
                DiagnosticMessage?.Invoke(sb2.ToString());
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

                // Insert line-break tag between parts – not after the last one
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
                // Standalone tags, placeholders, etc. – skip (plain text only)
            }
        }

        /// <summary>
        /// Returns the original (pre-edit) plain text of a segment as it stood
        /// before its tracked changes were made: deleted text is kept, inserted
        /// text is dropped. The counterpart of <see cref="GetFinalText"/> –
        /// together they turn a segment with revisions into a (before, after)
        /// pair.
        /// </summary>
        public static string GetOriginalText(ISegment segment)
        {
            if (segment == null) return "";
            var sb = new StringBuilder();
            AppendOriginalText(segment, sb);
            return sb.ToString();
        }

        private static void AppendOriginalText(IAbstractMarkupDataContainer container, StringBuilder sb)
        {
            foreach (var item in container)
            {
                if (item is IRevisionMarker revision)
                {
                    // Skip inserted text entirely; include deleted/unchanged text
                    if (revision.Properties.RevisionType != RevisionType.Insert)
                    {
                        AppendOriginalText(revision, sb);
                    }
                }
                else if (item is IText textItem)
                {
                    sb.Append(textItem.Properties.Text);
                }
                else if (item is IAbstractMarkupDataContainer nested)
                {
                    AppendOriginalText(nested, sb);
                }
                // Standalone tags, placeholders, etc. – skip (plain text only)
            }
        }

        /// <summary>
        /// Collects revision metadata (distinct authors, latest revision date)
        /// from a segment's tracked changes. Returns false when the segment
        /// carries no revision markers at all.
        /// </summary>
        public static bool TryCollectRevisionInfo(ISegment segment, out List<string> authors, out DateTime? lastDate)
        {
            authors = null;
            lastDate = null;
            if (segment == null) return false;

            bool found = false;
            foreach (var item in segment.AllSubItems)
            {
                var revision = item as IRevisionMarker;
                if (revision == null) continue;
                found = true;

                var props = revision.Properties;
                var author = props?.Author;
                if (!string.IsNullOrWhiteSpace(author))
                {
                    if (authors == null) authors = new List<string>();
                    if (!authors.Contains(author)) authors.Add(author);
                }
                var date = props?.Date;
                if (date != null && (lastDate == null || date > lastDate))
                    lastDate = date;
            }
            return found;
        }

        // ─── Helpers ─────────────────────────────────────────

        /// <summary>
        /// Returns true if any IText node in the source segment contains a literal
        /// newline character (\n or \r). This indicates the file format (e.g. Visio,
        /// Excel) stores line breaks as text content rather than as separate
        /// IPlaceholderTag elements (as DOCX does with w:br).
        /// </summary>
        private static bool SourceTextContainsNewlines(ISegment segment)
        {
            if (segment == null) return false;
            foreach (var item in segment.AllSubItems)
            {
                if (item is IText textItem)
                {
                    var t = textItem.Properties.Text;
                    if (t != null && (t.IndexOf('\n') >= 0 || t.IndexOf('\r') >= 0))
                        return true;
                }
            }
            return false;
        }

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
