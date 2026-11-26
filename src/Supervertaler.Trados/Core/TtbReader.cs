using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Reads terms from a Trados Studio 2026 .ttb termbase file (SQLite 3 + FTS5).
    /// Read-only access using Microsoft.Data.Sqlite.
    ///
    /// Schema (mirrors the public MultiTermReader contract so callers can dispatch
    /// on file extension and reuse the same downstream pipeline):
    ///   mtIndexes        – language index definitions (Id, Name, Locale)
    ///                      Locale is uppercase: "EN", "DE", "EN-GB", "NL-BE"
    ///   mtTerms          – terms (TermId, TermText, IndexId, ConceptId)
    ///   mtConcepts       – one row per concept (ConceptId, Text, Incomplete)
    ///   mtFields         – field definitions (Name, Type)
    ///   mtFieldsValues   – field values, addressed by Path against mtSystem.definition
    ///   mtSystem         – key/value, includes the MultiTerm-style termbase
    ///                      definition XML under Name='definition'
    ///   terms_fts        – FTS5 virtual table over mtTerms.TermText, unicode61,
    ///                      prefix indexes at 2/3 chars (unused here; reserved for
    ///                      future SuperSearch concordance / autosuggest paths).
    /// </summary>
    public class TtbReader : ITermbaseReader
    {
        private SqliteConnection _connection;
        private readonly string _filePath;
        private bool _disposed;

        public string LastError { get; private set; }
        public string TermbaseName { get; private set; }

        public TtbReader(string ttbPath)
        {
            _filePath = ttbPath ?? throw new ArgumentNullException(nameof(ttbPath));
            TermbaseName = Path.GetFileNameWithoutExtension(ttbPath);
        }

        /// <summary>
        /// Opens the .ttb file in read-only mode. Honours WAL — SQLite ReadOnly mode
        /// reads the most recent committed state even if Trados is currently writing.
        /// </summary>
        public bool Open()
        {
            if (!File.Exists(_filePath))
            {
                LastError = $"File not found: {_filePath}";
                return false;
            }

            try
            {
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = _filePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Shared,
                    // Pooling off: a pooled connection keeps the .ttb OPEN after
                    // Dispose, for as long as Studio runs - a client's termbase held
                    // by us long after the read (TermLens loads, list_terms pages).
                    // Same lesson as the .sdltb's OLE DB pooling (#36).
                    Pooling = false
                }.ToString();

                _connection = new SqliteConnection(connStr);
                _connection.Open();
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"Failed to open .ttb file: {ex.Message}";
                _connection?.Dispose();
                _connection = null;
                return false;
            }
        }

        /// <summary>
        /// Returns the language indexes defined in this termbase.
        /// Each entry is (IndexName, LocaleCode), e.g. ("English", "EN"),
        /// ("English (United Kingdom)", "EN-GB").
        /// </summary>
        public List<(string Name, string Locale)> GetLanguageIndexes()
        {
            var result = new List<(string, string)>();
            if (_connection == null) return result;

            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Name, Locale FROM mtIndexes ORDER BY Id";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                            var locale = reader.IsDBNull(1) ? "" : reader.GetString(1);
                            result.Add((name, locale));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"Error reading mtIndexes: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Bulk-loads all term pairs for the specified source/target language indexes.
        /// The index parameters accept either the human-readable Name from mtIndexes
        /// (e.g. "English") or the Locale code (e.g. "EN" / "EN-GB"). Matching is
        /// case-insensitive — Trados Studio uses BCP-47 mixed case ("en-GB") while
        /// .ttb storage uses upper-case ("EN-GB").
        ///
        /// Returns a TermMatcher-compatible index dictionary keyed by source term
        /// (case-insensitive).
        /// </summary>
        public Dictionary<string, List<TermEntry>> LoadAllTerms(
            string sourceIndexName, string targetIndexName,
            long termbaseId, string termbaseName)
        {
            var index = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            if (_connection == null) return index;

            try
            {
                var srcIndexId = ResolveIndexId(sourceIndexName);
                var tgtIndexId = ResolveIndexId(targetIndexName);
                if (srcIndexId == null || tgtIndexId == null)
                {
                    LastError = $"Index not found for '{sourceIndexName}' or '{targetIndexName}'";
                    System.Diagnostics.Debug.WriteLine($"[TtbReader] {LastError}");
                    return index;
                }

                // Step 1: source terms grouped by ConceptId (ordered by TermId so the
                // first row per concept is the canonical "primary" term)
                var sourceTermsByConcept = LoadTermsByConcept(srcIndexId.Value);
                System.Diagnostics.Debug.WriteLine(
                    $"[TtbReader] Loaded {sourceTermsByConcept.Count} source concepts from index {srcIndexId.Value}");
                if (sourceTermsByConcept.Count == 0) return index;

                // Step 2: target terms grouped by ConceptId
                var targetTermsByConcept = LoadTermsByConcept(tgtIndexId.Value);

                // Step 3: build TermEntry objects and index them
                long entryIdCounter = 0;
                foreach (var kvp in sourceTermsByConcept)
                {
                    var conceptId = kvp.Key;
                    var sourceTerms = kvp.Value;

                    if (!targetTermsByConcept.TryGetValue(conceptId, out var targetTerms))
                        continue; // No target terms for this concept

                    var primarySource = sourceTerms[0];
                    var primaryTarget = targetTerms[0];
                    var targetSynonyms = targetTerms.Skip(1).ToList();

                    var entry = new TermEntry
                    {
                        Id = termbaseId * -100000 - (++entryIdCounter),
                        SourceTerm = primarySource,
                        TargetTerm = primaryTarget,
                        TargetSynonyms = targetSynonyms,
                        TermbaseId = termbaseId,
                        TermbaseName = termbaseName,
                        ConceptId = conceptId,
                        IsMultiTerm = true, // existing flag, treated as "read-only termbase term"
                        Ranking = 50
                    };

                    AddToIndex(index, primarySource, entry);

                    for (int i = 1; i < sourceTerms.Count; i++)
                    {
                        AddToIndex(index, sourceTerms[i], entry);
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"Error loading terms: {ex.Message}";
            }

            return index;
        }

        /// <summary>
        /// Returns metadata about this termbase for display in the settings grid.
        /// </summary>
        public MultiTermTermbaseInfo GetTermbaseInfo(
            string sourceIndexName, string targetIndexName, long syntheticId)
        {
            int termCount = 0;
            try
            {
                // ENTRIES, not source terms: an entry with two source terms
                // (a synonym) counted twice, so list_resources said 273 where
                // list_terms, which counts entries, said 272. A Supervertaler
                // termbase's count is rows, i.e. entries, too.
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM mtConcepts";
                    var n = cmd.ExecuteScalar();
                    if (n != null && n != DBNull.Value)
                        termCount = Convert.ToInt32(n);
                }
            }
            catch { /* ignore count errors */ }

            return new MultiTermTermbaseInfo
            {
                SyntheticId = syntheticId,
                FilePath = _filePath,
                Name = TermbaseName,
                SourceIndexName = sourceIndexName,
                TargetIndexName = targetIndexName,
                TermCount = termCount,
                LoadMode = MultiTermLoadMode.DirectAccess
            };
        }

        /// <summary>
        /// Loads all languages, concepts (terms grouped by language index) and
        /// concept-level descriptive fields for the import feature. Read-only.
        /// </summary>
        public ImportedTermbase LoadForImport()
        {
            var tb = new ImportedTermbase
            {
                FilePath = _filePath,
                Format = "ttb",
                Name = TermbaseName
            };
            if (_connection == null) return tb;

            try
            {
                // Languages (numeric Id keys the per-concept term lists)
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Id, Name, Locale FROM mtIndexes ORDER BY Id";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var id = reader.GetInt32(0);
                            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                            var locale = reader.IsDBNull(2) ? "" : reader.GetString(2);
                            tb.Languages.Add(new ImportLanguage
                            {
                                Id = id,
                                Name = name,
                                Locale = locale,
                                IndexName = name
                            });
                        }
                    }
                }

                // Terms grouped by concept and language index, in entry order
                var concepts = new Dictionary<int, ImportConcept>();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT ConceptId, IndexId, TermText FROM mtTerms " +
                        "ORDER BY ConceptId, IndexId, TermId";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var conceptId = reader.GetInt32(0);
                            var indexId = reader.GetInt32(1);
                            var text = reader.IsDBNull(2) ? "" : reader.GetString(2);
                            if (string.IsNullOrWhiteSpace(text)) continue;

                            if (!concepts.TryGetValue(conceptId, out var concept))
                            {
                                concept = new ImportConcept { ConceptId = conceptId };
                                concepts[conceptId] = concept;
                            }
                            if (!concept.TermsByLanguageId.TryGetValue(indexId, out var list))
                            {
                                list = new List<string>();
                                concept.TermsByLanguageId[indexId] = list;
                            }
                            list.Add(text);
                        }
                    }
                }

                tb.Concepts.AddRange(concepts.Values);
                LoadConceptFields(tb, concepts);
                tb.DiscoveredFields = MultiTermConceptXml.CollectFieldNames(tb.Concepts);
            }
            catch (Exception ex)
            {
                LastError = $"Error loading .ttb for import: {ex.Message}";
            }

            return tb;
        }

        // Reads mtConcepts.Text (MultiTerm compact XML) and attributes its descriptive
        // fields to each concept — concept-level fields plus per-language (and that
        // language's term-level) fields such as Definition, Note and Status. Studio
        // 2026 .ttb stores the same field XML MultiTerm has always used, so this shares
        // MultiTermConceptXml with the .sdltb reader. Concept fields are optional —
        // any failure is ignored.
        private void LoadConceptFields(ImportedTermbase tb, Dictionary<int, ImportConcept> concepts)
        {
            if (concepts.Count == 0) return;
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT ConceptId, Text FROM mtConcepts";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var conceptId = reader.GetInt32(0);
                            if (reader.IsDBNull(1)) continue;
                            if (!concepts.TryGetValue(conceptId, out var concept)) continue;

                            MultiTermConceptXml.ApplyToConcept(concept, reader.GetString(1), tb.Languages);
                        }
                    }
                }
            }
            catch { /* concept-level fields are optional */ }
        }

        /// <summary>
        /// One page of the termbase's entries, in entry-number order, for list_terms.
        /// Paged in SQL (LIMIT/OFFSET over mtConcepts), so the cost is one page
        /// whatever the termbase's size. <paramref name="contains"/> keeps entries
        /// with a source or target term containing it, case-insensitively.
        /// Read-only, like everything else here.
        /// </summary>
        public TermListPage ListConcepts(string sourceIndexName, string targetIndexName,
            int offset, int limit, string contains)
        {
            var page = new TermListPage();
            if (_connection == null) { page.Error = LastError ?? "the termbase is not open"; return page; }

            try
            {
                var src = ResolveIndex(sourceIndexName);
                var tgt = ResolveIndex(targetIndexName);
                if (src == null || tgt == null)
                {
                    page.Error = $"the termbase has no index for '{(src == null ? sourceIndexName : targetIndexName)}'. " +
                                 "Its languages: " + string.Join(", ", GetLanguageIndexes().Select(i => i.Name));
                    return page;
                }

                bool filter = !string.IsNullOrWhiteSpace(contains);
                var where = "";
                if (filter)
                {
                    _connection.CreateFunction("sv_contains",
                        (string text, string needle) => TermbaseListing.Contains(text, needle), isDeterministic: true);
                    where = " WHERE ConceptId IN (SELECT ConceptId FROM mtTerms " +
                            "WHERE IndexId IN ($src, $tgt) AND sv_contains(TermText, $q))";
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM mtConcepts" + where;
                    AddListParams(cmd, src.Value.Id, tgt.Value.Id, contains);
                    page.Total = Convert.ToInt32(cmd.ExecuteScalar());
                }

                var srcTokens = new[] { src.Value.Name, src.Value.Locale };
                var tgtTokens = new[] { tgt.Value.Name, tgt.Value.Locale };
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT ConceptId, Text FROM mtConcepts" + where +
                                      " ORDER BY ConceptId LIMIT $limit OFFSET $offset";
                    AddListParams(cmd, src.Value.Id, tgt.Value.Id, contains);
                    cmd.Parameters.AddWithValue("$limit", limit);
                    cmd.Parameters.AddWithValue("$offset", offset);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            page.Entries.Add(TermbaseListing.FromConceptXml(
                                reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                                srcTokens, tgtTokens));
                    }
                }
            }
            catch (Exception ex)
            {
                page.Error = "could not read the termbase: " + ex.Message;
            }
            return page;
        }

        private static void AddListParams(SqliteCommand cmd, int srcId, int tgtId, string contains)
        {
            if (string.IsNullOrWhiteSpace(contains)) return;
            cmd.Parameters.AddWithValue("$src", srcId);
            cmd.Parameters.AddWithValue("$tgt", tgtId);
            cmd.Parameters.AddWithValue("$q", contains.Trim());
        }

        // An index by Name or Locale (any case), with both, for matching the
        // language groups inside the concept XML.
        private (int Id, string Name, string Locale)? ResolveIndex(string nameOrLocale)
        {
            if (string.IsNullOrWhiteSpace(nameOrLocale)) return null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT Id, Name, Locale FROM mtIndexes " +
                    "WHERE LOWER(Name) = LOWER($v) OR LOWER(Locale) = LOWER($v) LIMIT 1";
                cmd.Parameters.AddWithValue("$v", nameOrLocale);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return (r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2));
                }
            }
        }

        // Resolve a caller-supplied index identifier (Name or Locale, any case)
        // to the mtIndexes.Id. Returns null if no match.
        private int? ResolveIndexId(string nameOrLocale)
        {
            if (string.IsNullOrWhiteSpace(nameOrLocale)) return null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT Id FROM mtIndexes " +
                    "WHERE LOWER(Name) = LOWER($v) OR LOWER(Locale) = LOWER($v) " +
                    "LIMIT 1";
                cmd.Parameters.AddWithValue("$v", nameOrLocale);
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value) return null;
                return Convert.ToInt32(result);
            }
        }

        // Returns ConceptId → ordered list of TermText for the given index.
        // Order is by TermId ascending, so the first term per concept is the
        // canonical "primary" term as entered.
        private Dictionary<int, List<string>> LoadTermsByConcept(int indexId)
        {
            var result = new Dictionary<int, List<string>>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT ConceptId, TermText FROM mtTerms " +
                    "WHERE IndexId = $idx " +
                    "ORDER BY ConceptId, TermId";
                cmd.Parameters.AddWithValue("$idx", indexId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var conceptId = reader.GetInt32(0);
                        var text = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        if (!result.TryGetValue(conceptId, out var list))
                        {
                            list = new List<string>();
                            result[conceptId] = list;
                        }
                        list.Add(text);
                    }
                }
            }
            return result;
        }

        private static void AddToIndex(
            Dictionary<string, List<TermEntry>> index, string key, TermEntry entry)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            // Normalise space variants / script digits so keys line up with the
            // segment-side normalisation in TermMatcher (matches LoadAllTerms).
            key = TermMatcher.NormalizeScriptChars(key.Trim());
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
            try { _connection?.Close(); } catch { /* ignore */ }
            _connection?.Dispose();
            _connection = null;
        }
    }
}
