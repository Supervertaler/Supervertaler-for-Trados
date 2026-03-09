using System;
using System.Collections.Generic;
using System.Linq;
using Sdl.Terminology.TerminologyProvider.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Fallback for per-segment term lookup when the ACE OLEDB driver is unavailable.
    /// Uses Trados Studio's built-in ITerminologyProvider API to search .sdltb termbases.
    ///
    /// This fallback is only activated for MultiTerm termbases that cannot be opened
    /// directly via OleDb. It provides per-segment search instead of bulk loading.
    ///
    /// Limitations:
    /// - Searches are per-segment (slower than bulk index)
    /// - Requires Trados's internal TerminologyProviderManager to be resolved at runtime
    /// - Results are cached (LRU, 200 segments) for performance
    /// </summary>
    public class TerminologyProviderFallback : IDisposable
    {
        private ITerminologyProvider _provider;
        private ILanguage _sourceLanguage;
        private ILanguage _targetLanguage;
        private readonly string _termbaseName;
        private readonly long _syntheticId;
        private readonly Dictionary<string, List<TermEntry>> _cache;
        private readonly LinkedList<string> _cacheOrder;
        private const int MaxCacheSize = 200;
        private bool _disposed;

        public bool IsAvailable => _provider != null;
        public string LastError { get; private set; }

        /// <summary>
        /// Creates a terminology provider fallback using Trados's built-in API.
        /// </summary>
        /// <param name="provider">An already-resolved ITerminologyProvider (from project config).</param>
        /// <param name="sourceLangName">Source language name (e.g. "English").</param>
        /// <param name="targetLangName">Target language name (e.g. "Dutch").</param>
        /// <param name="termbaseName">Display name for term entries.</param>
        /// <param name="syntheticId">Negative synthetic ID for MultiTerm entries.</param>
        public TerminologyProviderFallback(
            ITerminologyProvider provider,
            string sourceLangName, string targetLangName,
            string termbaseName, long syntheticId)
        {
            _termbaseName = termbaseName;
            _syntheticId = syntheticId;
            _cache = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            _cacheOrder = new LinkedList<string>();

            try
            {
                _provider = provider;

                if (_provider != null && !_provider.IsInitialized)
                    _provider.Initialize();

                if (_provider != null)
                {
                    // Find source and target languages from the provider's available languages
                    var languages = _provider.GetLanguages();
                    if (languages != null)
                    {
                        foreach (var lang in languages)
                        {
                            if (_sourceLanguage == null && LanguageMatches(lang, sourceLangName))
                                _sourceLanguage = lang;
                            else if (_targetLanguage == null && LanguageMatches(lang, targetLangName))
                                _targetLanguage = lang;
                        }
                    }

                    if (_sourceLanguage == null || _targetLanguage == null)
                    {
                        LastError = $"Could not match source/target languages ({sourceLangName}/{targetLangName}) " +
                                    "in provider's available languages.";
                        _provider = null;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"Failed to initialize terminology provider: {ex.Message}";
                _provider = null;
            }
        }

        /// <summary>
        /// Searches the termbase for terms matching the given segment text.
        /// Results are cached per segment for performance.
        /// Returns a dictionary compatible with TermMatcher.MergeIndex().
        /// </summary>
        public Dictionary<string, List<TermEntry>> SearchSegment(string segmentText)
        {
            var result = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            if (_provider == null || string.IsNullOrWhiteSpace(segmentText))
                return result;

            // Check cache
            if (_cache.TryGetValue(segmentText, out var cached))
            {
                // Build index from cached entries
                foreach (var entry in cached)
                    AddToIndex(result, entry.SourceTerm, entry);
                return result;
            }

            try
            {
                var searchResults = _provider.Search(
                    segmentText,
                    _sourceLanguage,
                    _targetLanguage,
                    100,
                    SearchMode.Fuzzy,
                    true);

                if (searchResults == null || searchResults.Count == 0)
                {
                    CacheResult(segmentText, new List<TermEntry>());
                    return result;
                }

                var entries = new List<TermEntry>();
                long entryIdCounter = 0;

                foreach (var sr in searchResults)
                {
                    try
                    {
                        var entry = _provider.GetEntry(sr.Id);
                        if (entry == null) continue;

                        // Find source and target languages in the entry
                        EntryLanguage sourceLang = null;
                        EntryLanguage targetLang = null;

                        if (entry.Languages != null)
                        {
                            foreach (var lang in entry.Languages)
                            {
                                if (sourceLang == null && LanguageMatches(lang, _sourceLanguage))
                                    sourceLang = lang;
                                else if (targetLang == null && LanguageMatches(lang, _targetLanguage))
                                    targetLang = lang;
                            }
                        }

                        if (sourceLang?.Terms == null || sourceLang.Terms.Count == 0)
                            continue;
                        if (targetLang?.Terms == null || targetLang.Terms.Count == 0)
                            continue;

                        var sourceTermText = sourceLang.Terms[0].Value;
                        var targetTermText = targetLang.Terms[0].Value;

                        if (string.IsNullOrWhiteSpace(sourceTermText) ||
                            string.IsNullOrWhiteSpace(targetTermText))
                            continue;

                        var targetSynonyms = new List<string>();
                        for (int i = 1; i < targetLang.Terms.Count; i++)
                        {
                            if (!string.IsNullOrWhiteSpace(targetLang.Terms[i].Value))
                                targetSynonyms.Add(targetLang.Terms[i].Value);
                        }

                        // Extract definition from entry-level fields
                        string definition = null;
                        if (entry.Fields != null)
                        {
                            foreach (var field in entry.Fields)
                            {
                                if (string.Equals(field.Name, "Definition",
                                    StringComparison.OrdinalIgnoreCase))
                                {
                                    definition = field.Value;
                                    break;
                                }
                            }
                        }

                        var termEntry = new TermEntry
                        {
                            Id = _syntheticId * -100000 - (++entryIdCounter),
                            SourceTerm = sourceTermText,
                            TargetTerm = targetTermText,
                            TargetSynonyms = targetSynonyms,
                            TermbaseId = _syntheticId,
                            TermbaseName = _termbaseName,
                            Definition = definition,
                            IsMultiTerm = true,
                            Ranking = 50
                        };

                        entries.Add(termEntry);
                        AddToIndex(result, sourceTermText, termEntry);
                    }
                    catch
                    {
                        // Skip individual entries that fail to parse
                    }
                }

                CacheResult(segmentText, entries);
            }
            catch (Exception ex)
            {
                LastError = $"Search failed: {ex.Message}";
            }

            return result;
        }

        private bool LanguageMatches(ILanguage providerLang, string langName)
        {
            if (providerLang == null || string.IsNullOrEmpty(langName))
                return false;
            return string.Equals(providerLang.Name, langName, StringComparison.OrdinalIgnoreCase);
        }

        private bool LanguageMatches(EntryLanguage entryLang, ILanguage providerLang)
        {
            if (entryLang == null || providerLang == null)
                return false;
            if (entryLang.Locale != null && providerLang.Locale != null)
                return string.Equals(entryLang.Locale.ToString(), providerLang.Locale.ToString(),
                    StringComparison.OrdinalIgnoreCase);
            return string.Equals(entryLang.Name, providerLang.Name,
                StringComparison.OrdinalIgnoreCase);
        }

        private void CacheResult(string key, List<TermEntry> entries)
        {
            if (_cache.Count >= MaxCacheSize)
            {
                // Evict oldest entry
                var oldest = _cacheOrder.First;
                if (oldest != null)
                {
                    _cache.Remove(oldest.Value);
                    _cacheOrder.RemoveFirst();
                }
            }

            _cache[key] = entries;
            _cacheOrder.AddLast(key);
        }

        private static void AddToIndex(Dictionary<string, List<TermEntry>> index,
            string sourceTerm, TermEntry entry)
        {
            var key = sourceTerm.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key)) return;

            if (!index.TryGetValue(key, out var list))
            {
                list = new List<TermEntry>();
                index[key] = list;
            }
            list.Add(entry);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_provider != null)
                {
                    _provider.Uninitialize();
                }
            }
            catch { /* ignore cleanup errors */ }

            _cache.Clear();
            _cacheOrder.Clear();
        }
    }
}
