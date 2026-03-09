using System;
using System.Collections.Generic;
using System.Linq;
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

        // Pattern for splitting words: captures words, decimals, percentages, units
        private static readonly Regex WordPattern = new Regex(
            @"(?<!\w)[\w.,%-/]+(?!\w)",
            RegexOptions.Compiled);

        // Tags from various CAT tools to strip before display
        private static readonly Regex TagPattern = new Regex(
            @"</?(?:b|i|u|bi|sub|sup|li-[ob]|\d+)/?>|[\[{]\d+[}\]]|\{\d{5}\}|\[[^\[\]]*\}|\{[^\{\}]*\]",
            RegexOptions.Compiled);

        private Dictionary<string, List<TermEntry>> _termIndex;

        /// <summary>
        /// Loads the term index for fast in-memory matching.
        /// Call this once when a termbase is loaded or changed.
        /// </summary>
        public void LoadIndex(Dictionary<string, List<TermEntry>> termIndex)
        {
            _termIndex = termIndex ?? new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
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

            var key = entry.SourceTerm.Trim().ToLowerInvariant();
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
        /// Tokenizes source text into a list of SegmentTokens with term matches populated.
        /// This is the main entry point — equivalent to Supervertaler's update_with_matches().
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
            var allSpans = new List<(int start, int end, string text, List<TermEntry> entries)>();

            // Add multi-word matches
            allSpans.AddRange(multiWordMatches);

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
                    var entries = LookupTerm(text);
                    allSpans.Add((start, end, text, entries));
                }
            }

            // Sort by position
            allSpans.Sort((a, b) => a.start.CompareTo(b.start));

            foreach (var (start, end, text, entries) in allSpans)
            {
                var token = new SegmentToken { Text = text };
                if (entries != null)
                    token.Matches = entries;
                tokens.Add(token);
            }

            return tokens;
        }

        private void FindMultiWordMatches(string line, HashSet<int> usedPositions,
            List<(int start, int end, string text, List<TermEntry> entries)> matches)
        {
            if (_termIndex == null) return;

            // Get multi-word terms from index (terms containing spaces), sorted longest first
            var multiWordTerms = _termIndex.Keys
                .Where(k => k.Contains(" "))
                .OrderByDescending(k => k.Length)
                .ToList();

            var lineLower = line.ToLowerInvariant();

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

                    if (startBoundary && endBoundary)
                    {
                        // Check no overlap with existing matches
                        bool overlaps = false;
                        for (int p = idx; p < endIdx; p++)
                        {
                            if (usedPositions.Contains(p))
                            {
                                overlaps = true;
                                break;
                            }
                        }

                        if (!overlaps)
                        {
                            var entries = _termIndex[term];
                            var matchedText = line.Substring(idx, term.Length);
                            matches.Add((idx, endIdx, matchedText, entries));

                            for (int p = idx; p < endIdx; p++)
                                usedPositions.Add(p);
                        }
                    }

                    searchStart = idx + 1;
                }
            }
        }

        private List<TermEntry> LookupTerm(string word)
        {
            if (_termIndex == null || string.IsNullOrWhiteSpace(word))
                return new List<TermEntry>();

            var normalised = word.Trim();
            var stripped = normalised.TrimEnd(PunctChars).TrimStart(PunctChars);

            // Try exact match first, then stripped
            if (_termIndex.TryGetValue(normalised, out var exact))
                return exact;
            if (stripped.Length > 0 && stripped != normalised && _termIndex.TryGetValue(stripped, out var strippedMatch))
                return strippedMatch;

            return new List<TermEntry>();
        }
    }
}
