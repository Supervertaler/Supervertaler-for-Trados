using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Tokenizes source segment text and matches terms against a termbase index.
    /// Port of Supervertaler's tokenize_with_multiword_terms() and term matching logic.
    /// </summary>
    public class TermMatcher
    {
        private static readonly char[] PunctChars = ".!?,;:\"'\u201C\u201D\u201E\u00AB\u00BB\u2018\u2019\u201A\u2039\u203A()[]".ToCharArray();

        // Pattern for splitting words: captures words, decimals, percentages, units.
        // Includes subscript digits (₀-₉, U+2080-U+2089) and superscript digits
        // (⁰¹²³⁴⁵⁶⁷⁸⁹) so that chemical/scientific tokens like H₂O or CO₂ are
        // captured as a single word rather than split at the subscript character.
        private static readonly Regex WordPattern = new Regex(
            @"(?<!\w)[\w.,%-/\u2080-\u2089\u2070\u00B9\u00B2\u00B3\u2074-\u2079]+(?!\w)",
            RegexOptions.Compiled);

        // Tags from various CAT tools to strip before display
        private static readonly Regex TagPattern = new Regex(
            @"</?(?:b|i|u|bi|sub|sup|li-[ob]|\d+)/?>|[\[{]\d+[}\]]|\{\d{5}\}|\[[^\[\]]*\}|\{[^\{\}]*\]",
            RegexOptions.Compiled);

        private Dictionary<string, List<TermEntry>> _termIndex;

        // Cached multi-word term list (keys containing spaces, sorted longest first).
        // Invalidated when the index changes to avoid rebuilding on every segment.
        private List<string> _multiWordTermsCache;

        /// <summary>
        /// Suffix-tolerant matching for languages where grammatical particles
        /// attach directly to a noun with no intervening space (Korean,
        /// Japanese). When true, a single CJK token matches the longest term
        /// that is a PREFIX of it (값 ↦ 값으로), and a multi-word term matches
        /// when its final CJK token is a prefix of the segment token
        /// (제2 전압 값 ↦ 제2 전압 값으로). Off by default; set per source
        /// language / user setting. Only CJK-script tokens are prefix-matched,
        /// so Latin words in the same document are never affected.
        /// </summary>
        public bool SuffixTolerant { get; set; } = false;

        private static bool IsCjkChar(char ch)
        {
            return (ch >= '가' && ch <= '힣')   // Hangul syllables
                || (ch >= '぀' && ch <= 'ヿ')   // Hiragana + Katakana
                || (ch >= '㐀' && ch <= '䶿')   // CJK Unified Ideographs Ext A
                || (ch >= '一' && ch <= '鿿');  // CJK Unified Ideographs
        }

        private static bool ContainsCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var ch in s)
                if (IsCjkChar(ch)) return true;
            return false;
        }

        /// <summary>
        /// Suffix-tolerant single-token lookup: returns the longest term that is
        /// a PREFIX of <paramref name="token"/> (the remainder being an attached
        /// particle, e.g. 값 inside 값으로). Longest prefix wins so the most
        /// specific term is chosen. Cheap — at most token-length index probes.
        /// </summary>
        private (List<TermEntry> entries, HashSet<long> abbrMatchIds) LookupTermPrefix(string token)
        {
            var empty = (new List<TermEntry>(), new HashSet<long>());
            if (_termIndex == null || string.IsNullOrWhiteSpace(token))
                return empty;
            var normalised = NormalizeScriptChars(token.Trim());
            for (int len = normalised.Length - 1; len >= 1; len--)
            {
                if (_termIndex.ContainsKey(normalised.Substring(0, len)))
                    return LookupTerm(normalised.Substring(0, len));
            }
            return empty;
        }

        /// <summary>
        /// Loads the term index for fast in-memory matching.
        /// Call this once when a termbase is loaded or changed.
        /// </summary>
        public void LoadIndex(Dictionary<string, List<TermEntry>> termIndex)
        {
            _termIndex = termIndex ?? new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            _multiWordTermsCache = null;
        }

        /// <summary>
        /// Merges additional term entries into the existing index.
        /// Used to add MultiTerm terms alongside Supervertaler terms.
        /// Entries are appended to existing key lists (not replaced).
        /// </summary>
        public void MergeIndex(Dictionary<string, List<TermEntry>> additionalIndex)
        {
            if (additionalIndex == null || additionalIndex.Count == 0) return;

            if (_termIndex == null)
                _termIndex = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in additionalIndex)
            {
                if (_termIndex.TryGetValue(kvp.Key, out var existing))
                    existing.AddRange(kvp.Value);
                else
                    _termIndex[kvp.Key] = new List<TermEntry>(kvp.Value);
            }
            _multiWordTermsCache = null;
        }

        /// <summary>
        /// Removes all entries from the index that have the IsMultiTerm flag set.
        /// Used to clear MultiTerm entries before reloading (e.g. project switch).
        /// </summary>
        public void RemoveMultiTermEntries()
        {
            if (_termIndex == null) return;

            var keysToClean = new List<string>();
            foreach (var kvp in _termIndex)
            {
                kvp.Value.RemoveAll(e => e.IsMultiTerm);
                if (kvp.Value.Count == 0)
                    keysToClean.Add(kvp.Key);
            }
            foreach (var key in keysToClean)
                _termIndex.Remove(key);
            _multiWordTermsCache = null;
        }

        /// <summary>
        /// Adds a single entry to the in-memory index without rebuilding.
        /// Mirrors the indexing logic from TermbaseReader.LoadAllTerms():
        /// indexes by lowercase source term and by stripped-punctuation variant.
        /// </summary>
        public void AddEntry(TermEntry entry)
        {
            if (_termIndex == null || entry == null || string.IsNullOrWhiteSpace(entry.SourceTerm))
                return;

            var key = NormalizeScriptChars(entry.SourceTerm.Trim().ToLowerInvariant());
            var stripped = key.TrimEnd('.', '!', '?', ',', ';', ':');

            if (!_termIndex.TryGetValue(key, out var list))
            {
                list = new List<TermEntry>();
                _termIndex[key] = list;
            }
            list.Add(entry);

            if (stripped != key && stripped.Length > 0)
            {
                if (!_termIndex.TryGetValue(stripped, out var strippedList))
                {
                    strippedList = new List<TermEntry>();
                    _termIndex[stripped] = strippedList;
                }
                strippedList.Add(entry);
            }

            // Index source abbreviation variant(s) as additional lookup keys
            foreach (var abbrVariant in entry.GetSourceAbbreviationVariants())
            {
                var abbrKey = NormalizeScriptChars(abbrVariant.Trim().ToLowerInvariant());
                if (string.IsNullOrEmpty(abbrKey) || abbrKey == key) continue;

                if (!_termIndex.TryGetValue(abbrKey, out var abbrList))
                {
                    abbrList = new List<TermEntry>();
                    _termIndex[abbrKey] = abbrList;
                }
                abbrList.Add(entry);

                var abbrStripped = abbrKey.TrimEnd('.', '!', '?', ',', ';', ':');
                if (abbrStripped != abbrKey && abbrStripped.Length > 0)
                {
                    if (!_termIndex.TryGetValue(abbrStripped, out var abbrStrippedList))
                    {
                        abbrStrippedList = new List<TermEntry>();
                        _termIndex[abbrStripped] = abbrStrippedList;
                    }
                    abbrStrippedList.Add(entry);
                }
            }
            _multiWordTermsCache = null;
        }

        /// <summary>
        /// Removes all entries with the given term ID from the in-memory index.
        /// Used after deleting a term to avoid a full reload.
        /// </summary>
        public void RemoveEntry(long termId)
        {
            if (_termIndex == null) return;

            var keysToClean = new List<string>();
            foreach (var kvp in _termIndex)
            {
                kvp.Value.RemoveAll(e => e.Id == termId);
                if (kvp.Value.Count == 0)
                    keysToClean.Add(kvp.Key);
            }
            foreach (var key in keysToClean)
                _termIndex.Remove(key);
            _multiWordTermsCache = null;
        }

        /// <summary>
        /// Returns all unique term entries from the in-memory index.
        /// Used for termbase injection into AI translation prompts.
        /// </summary>
        public List<TermEntry> GetAllEntries()
        {
            if (_termIndex == null)
                return new List<TermEntry>();

            var seen = new HashSet<long>();
            var result = new List<TermEntry>();
            foreach (var list in _termIndex.Values)
            {
                foreach (var entry in list)
                {
                    if (seen.Add(entry.Id))
                        result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>
        /// True for Unicode space characters that render like a plain space but are
        /// distinct code points: no-break space (U+00A0), Ogham space mark (U+1680),
        /// en/em/thin/hair spaces (U+2000–U+200A), narrow no-break space (U+202F),
        /// medium mathematical space (U+205F) and ideographic space (U+3000).
        /// </summary>
        internal static bool IsSpaceVariant(char c)
        {
            return c == '\u00A0' || c == '\u1680'
                || (c >= '\u2000' && c <= '\u200A')
                || c == '\u202F' || c == '\u205F' || c == '\u3000';
        }

        /// <summary>
        /// Normalises characters that have visually-equivalent Unicode variants so terms
        /// like H₂O or mm² and multi-word phrases can be matched regardless of which
        /// variant the stored term or the segment text uses.
        /// Subscript digits  ₀-₉  (U+2080–U+2089) → 0-9
        /// Superscript digits ⁰¹²³⁴⁵⁶⁷⁸⁹ (U+2070, U+00B9, U+00B2, U+00B3, U+2074–U+2079) → 0-9
        /// Unicode space variants (see <see cref="IsSpaceVariant"/>) → ASCII space.
        /// The space folding matters for multi-word terms: IDML-derived segments can
        /// carry a no-break space inside a phrase ("display panel"), which would
        /// otherwise silently defeat the substring search in FindMultiWordMatches.
        /// All other characters are left unchanged.  The method is length-preserving
        /// (each variant maps to exactly one ASCII char), so character positions remain valid.
        /// </summary>
        internal static string NormalizeScriptChars(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Fast path: skip allocation if no script chars or space variants are present
            bool hasScript = false;
            foreach (char c in text)
            {
                if ((c >= '\u2080' && c <= '\u2089') || c == '\u2070' ||
                    c == '\u00B9' || c == '\u00B2' || c == '\u00B3' ||
                    (c >= '\u2074' && c <= '\u2079') || IsSpaceVariant(c))
                {
                    hasScript = true;
                    break;
                }
            }
            if (!hasScript) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if      (c >= '\u2080' && c <= '\u2089') sb.Append((char)('0' + (c - '\u2080'))); // ₀-₉
                else if (c == '\u2070')                  sb.Append('0');  // ⁰
                else if (c == '\u00B9')                  sb.Append('1');  // ¹
                else if (c == '\u00B2')                  sb.Append('2');  // ²
                else if (c == '\u00B3')                  sb.Append('3');  // ³
                else if (c >= '\u2074' && c <= '\u2079') sb.Append((char)('0' + (c - '\u2070'))); // ⁴-⁹
                else if (IsSpaceVariant(c))              sb.Append(' ');
                else                                     sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tokenizes source text into a list of SegmentTokens with term matches populated.
        /// This is the main entry point – equivalent to Supervertaler's update_with_matches().
        /// </summary>
        public List<SegmentToken> Tokenize(string sourceText)
        {
            if (string.IsNullOrEmpty(sourceText))
                return new List<SegmentToken>();

            // Strip CAT tool tags for clean display
            var displayText = TagPattern.Replace(sourceText, "").Trim();

            var tokens = new List<SegmentToken>();

            // Split by newlines, preserving line breaks
            var lines = displayText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    tokens.Add(new SegmentToken { IsLineBreak = true });

                var lineTokens = TokenizeLine(lines[i].Trim());
                tokens.AddRange(lineTokens);
            }

            return tokens;
        }

        private List<SegmentToken> TokenizeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return new List<SegmentToken>();

            // Track which character positions are "used" by multi-word matches
            var usedPositions = new HashSet<int>();
            var multiWordMatches = new List<(int start, int end, string text, List<TermEntry> entries)>();

            // Pass 1: Find multi-word term matches (longest first)
            if (_termIndex != null)
            {
                FindMultiWordMatches(line, usedPositions, multiWordMatches);
            }

            // Pass 2: Extract all word positions
            var wordPositions = new List<(int start, int end, string text)>();
            foreach (Match m in WordPattern.Matches(line))
            {
                wordPositions.Add((m.Index, m.Index + m.Length, m.Value));
            }

            // Build final token list in order
            var tokens = new List<SegmentToken>();
            var allSpans = new List<(int start, int end, string text, List<TermEntry> entries, HashSet<long> abbrIds)>();

            // Add multi-word matches (with abbreviation detection)
            foreach (var mw in multiWordMatches)
            {
                var abbrIds = new HashSet<long>();
                var matchKey = mw.text.Trim().ToLowerInvariant();
                foreach (var entry in mw.entries)
                {
                    if (string.Equals(entry.SourceTerm.Trim(), matchKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var variant in entry.GetSourceAbbreviationVariants())
                    {
                        if (string.Equals(variant.Trim(), matchKey, StringComparison.OrdinalIgnoreCase))
                        {
                            abbrIds.Add(entry.Id);
                            break;
                        }
                    }
                }
                allSpans.Add((mw.start, mw.end, mw.text, mw.entries, abbrIds));
            }

            // Add single words not covered by multi-word matches
            foreach (var (start, end, text) in wordPositions)
            {
                bool covered = false;
                for (int pos = start; pos < end; pos++)
                {
                    if (usedPositions.Contains(pos))
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    var (entries, abbrIds) = LookupTerm(text);
                    // Suffix-tolerant fallback: a CJK token with no exact match
                    // (값으로) matches the longest term that is a prefix of it (값).
                    if ((entries == null || entries.Count == 0)
                        && SuffixTolerant && ContainsCjk(text))
                    {
                        var pfx = LookupTermPrefix(text);
                        entries = pfx.entries;
                        abbrIds = pfx.abbrMatchIds;
                    }
                    allSpans.Add((start, end, text, entries, abbrIds));
                }
            }

            // Sort by position
            allSpans.Sort((a, b) => a.start.CompareTo(b.start));

            foreach (var (start, end, text, entries, abbrIds) in allSpans)
            {
                var token = new SegmentToken { Text = text };
                if (entries != null)
                    token.Matches = entries;
                if (abbrIds != null && abbrIds.Count > 0)
                    token.AbbreviationMatchIds = abbrIds;
                tokens.Add(token);
            }

            return tokens;
        }

        private void FindMultiWordMatches(string line, HashSet<int> usedPositions,
            List<(int start, int end, string text, List<TermEntry> entries)> matches)
        {
            if (_termIndex == null) return;

            // Use cached multi-word term list (rebuilt only when index changes)
            if (_multiWordTermsCache == null)
            {
                _multiWordTermsCache = _termIndex.Keys
                    .Where(k => k.Contains(" "))
                    .OrderByDescending(k => k.Length)
                    .ToList();
            }
            var multiWordTerms = _multiWordTermsCache;

            // Normalise subscript/superscript so "H₂O solution" matches a key of "h2o solution"
            var lineLower = NormalizeScriptChars(line.ToLowerInvariant());

            foreach (var term in multiWordTerms)
            {
                var termLower = term.ToLowerInvariant();
                int searchStart = 0;

                while (searchStart < lineLower.Length)
                {
                    int idx = lineLower.IndexOf(termLower, searchStart, StringComparison.Ordinal);
                    if (idx < 0) break;

                    int endIdx = idx + term.Length;

                    // Check word boundaries
                    bool startBoundary = idx == 0 || !char.IsLetterOrDigit(lineLower[idx - 1]);
                    bool endBoundary = endIdx >= lineLower.Length || !char.IsLetterOrDigit(lineLower[endIdx]);

                    // Suffix-tolerant: allow the term's final CJK token to be a
                    // prefix of a longer segment token (제2 전압 값 ↦ 제2 전압 값으로).
                    // Extend the highlighted span across the attached particle to
                    // the next whitespace so the displayed token stays intact.
                    int spanEnd = endIdx;
                    if (!endBoundary && SuffixTolerant
                        && IsCjkChar(lineLower[endIdx])
                        && endIdx > 0 && IsCjkChar(lineLower[endIdx - 1]))
                    {
                        while (spanEnd < lineLower.Length && !char.IsWhiteSpace(lineLower[spanEnd]))
                            spanEnd++;
                        endBoundary = true;
                    }

                    if (startBoundary && endBoundary)
                    {
                        // Check no overlap with existing matches
                        bool overlaps = false;
                        for (int p = idx; p < spanEnd; p++)
                        {
                            if (usedPositions.Contains(p))
                            {
                                overlaps = true;
                                break;
                            }
                        }

                        if (!overlaps)
                        {
                            var rawEntries = _termIndex[term];
                            // Term portion only — used for the case-sensitive /
                            // abbreviation comparison below (not the displayed span).
                            var matchedText = line.Substring(idx, term.Length);

                            // Post-filter case-sensitive entries for multi-word matches
                            var filteredEntries = new List<TermEntry>(rawEntries.Count);
                            foreach (var entry in rawEntries)
                            {
                                if (entry.CaseSensitive)
                                {
                                    // Normalise both sides so a space variant in either
                                    // the stored term or the segment text (e.g. an IDML
                                    // no-break space) doesn't fail the ordinal comparison.
                                    bool caseMatch = string.Equals(
                                        NormalizeScriptChars(entry.SourceTerm.Trim()),
                                        NormalizeScriptChars(matchedText.Trim()),
                                        StringComparison.Ordinal);
                                    if (!caseMatch)
                                    {
                                        foreach (var variant in entry.GetSourceAbbreviationVariants())
                                        {
                                            if (string.Equals(variant.Trim(), matchedText.Trim(),
                                                StringComparison.Ordinal))
                                            {
                                                caseMatch = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (!caseMatch) continue;
                                }
                                filteredEntries.Add(entry);
                            }

                            if (filteredEntries.Count > 0)
                            {
                                // Display span covers any attached particle so no
                                // text is dropped from the rendered segment.
                                var spanText = spanEnd == endIdx
                                    ? matchedText
                                    : line.Substring(idx, spanEnd - idx);
                                matches.Add((idx, spanEnd, spanText, filteredEntries));

                                for (int p = idx; p < spanEnd; p++)
                                    usedPositions.Add(p);
                            }
                        }
                    }

                    searchStart = idx + 1;
                }
            }
        }

        /// <summary>
        /// Looks up a word in the term index and returns matching entries plus a set
        /// of entry IDs that matched via their SourceAbbreviation (not SourceTerm).
        /// Entries marked as case-sensitive are filtered out if the case doesn't match.
        /// </summary>
        private (List<TermEntry> entries, HashSet<long> abbrMatchIds) LookupTerm(string word)
        {
            var emptyResult = (new List<TermEntry>(), new HashSet<long>());
            if (_termIndex == null || string.IsNullOrWhiteSpace(word))
                return emptyResult;

            // Normalise subscript/superscript digits so "H₂O" → "H2O" before index lookup
            var normalised = NormalizeScriptChars(word.Trim());
            var stripped = normalised.TrimEnd(PunctChars).TrimStart(PunctChars);

            List<TermEntry> entries = null;

            // Try exact match first, then stripped
            if (_termIndex.TryGetValue(normalised, out var exact))
                entries = exact;
            else if (stripped.Length > 0 && stripped != normalised && _termIndex.TryGetValue(stripped, out var strippedMatch))
                entries = strippedMatch;

            if (entries == null || entries.Count == 0)
                return emptyResult;

            // Post-filter: for case-sensitive entries, verify that the original text matches
            var originalText = stripped.Length > 0 ? stripped : normalised;
            var filtered = new List<TermEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry.CaseSensitive)
                {
                    // Check if any indexed key matches the original case
                    bool caseMatch = string.Equals(entry.SourceTerm.Trim(), originalText, StringComparison.Ordinal);
                    if (!caseMatch)
                    {
                        // Also check abbreviation variants
                        foreach (var variant in entry.GetSourceAbbreviationVariants())
                        {
                            if (string.Equals(variant.Trim(), originalText, StringComparison.Ordinal))
                            {
                                caseMatch = true;
                                break;
                            }
                        }
                    }
                    if (!caseMatch) continue; // skip this entry – case doesn't match
                }
                filtered.Add(entry);
            }

            if (filtered.Count == 0)
                return emptyResult;

            // Determine which entries matched via abbreviation (any pipe-separated variant)
            var abbrIds = new HashSet<long>();
            var matchKey = originalText.ToLowerInvariant();
            foreach (var entry in filtered)
            {
                if (string.Equals(entry.SourceTerm.Trim(), matchKey, StringComparison.OrdinalIgnoreCase))
                    continue; // matched via full term, not abbreviation

                foreach (var variant in entry.GetSourceAbbreviationVariants())
                {
                    if (string.Equals(variant.Trim(), matchKey, StringComparison.OrdinalIgnoreCase))
                    {
                        abbrIds.Add(entry.Id);
                        break;
                    }
                }
            }

            return (filtered, abbrIds);
        }

    }
}
