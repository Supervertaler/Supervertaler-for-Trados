using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Reads termbases from Supervertaler's SQLite database (supervertaler.db).
    /// This allows sharing the same termbases between Supervertaler and TermLens.
    ///
    /// Uses Microsoft.Data.Sqlite instead of System.Data.SQLite to avoid native
    /// interop DLL hash mismatches in Trados Studio's plugin environment.
    /// </summary>
    public class TermbaseReader : IDisposable
    {
        private SqliteConnection _connection;
        private readonly string _dbPath;
        private bool _disposed;
        private bool _hasNonTranslatableColumn;
        private bool _hasTermUuidColumn;
        private bool _hasAbbreviationColumns;
        private bool _hasUrlColumn;
        private bool _hasTermbaseCaseSensitive;

        public TermbaseReader(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        }

        /// <summary>
        /// Last exception message from Open(), or null if Open() succeeded.
        /// </summary>
        public string LastError { get; private set; }

        public bool Open()
        {
            LastError = null;

            if (!File.Exists(_dbPath))
            {
                LastError = $"File not found: {_dbPath}";
                return false;
            }

            try
            {
                // Mode=ReadOnly – we only run SELECTs; this also avoids WAL
                // locking issues when Supervertaler has the DB open.
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                _connection = new SqliteConnection(connStr);
                _connection.Open();
                _hasNonTranslatableColumn = HasColumn(_connection, "termbase_terms", "is_nontranslatable");
                _hasTermUuidColumn = HasColumn(_connection, "termbase_terms", "term_uuid");
                _hasAbbreviationColumns = HasColumn(_connection, "termbase_terms", "source_abbreviation");
                _hasUrlColumn = HasColumn(_connection, "termbase_terms", "url");
                _hasTermbaseCaseSensitive = HasColumn(_connection, "termbases", "case_sensitive");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _connection?.Dispose();
                _connection = null;
                return false;
            }
        }

        /// <summary>
        /// Gets all available termbases in the database.
        /// </summary>
        public List<TermbaseInfo> GetTermbases()
        {
            var result = new List<TermbaseInfo>();
            if (_connection == null) return result;

            var csCol = _hasTermbaseCaseSensitive ? ", tb.case_sensitive" : "";
            var sql = $@"
                SELECT tb.id, tb.name, tb.source_lang, tb.target_lang,
                       tb.is_project_termbase, tb.ranking,
                       COUNT(t.id) as term_count
                       {csCol}
                FROM termbases tb
                LEFT JOIN termbase_terms t ON CAST(t.termbase_id AS INTEGER) = tb.id
                GROUP BY tb.id
                ORDER BY tb.ranking ASC, tb.name ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var info = new TermbaseInfo
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        SourceLang = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        TargetLang = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        IsProjectTermbase = !reader.IsDBNull(4) && GetBool(reader, 4),
                        Ranking = reader.IsDBNull(5) ? 99 : reader.GetInt32(5),
                        TermCount = reader.GetInt32(6)
                    };
                    if (_hasTermbaseCaseSensitive && reader.FieldCount > 7 && !reader.IsDBNull(7))
                        info.CaseSensitive = reader.GetInt32(7);
                    result.Add(info);
                }
            }

            return result;
        }

        /// <summary>
        /// Every termbase's DECLARED direction, keyed by id. This is the
        /// canonical direction each entry inherits – see the rationale in
        /// <see cref="LoadAllTerms"/> for why the per-entry lang columns are
        /// not used for orientation decisions.
        /// </summary>
        public Dictionary<long, (string src, string tgt)> GetTermbaseDirections()
        {
            var result = new Dictionary<long, (string src, string tgt)>();
            if (_connection == null) return result;

            using (var cmd = new SqliteCommand("SELECT id, source_lang, target_lang FROM termbases", _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var src = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var tgt = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    result[id] = (src, tgt);
                }
            }
            return result;
        }

        /// <summary>
        /// Finds entries whose own source_lang names the termbase's target side
        /// and vice versa. Where the TEXT is reversed too, the row is indexed
        /// under the wrong language and can never match a source segment – it
        /// stays in the termbase, still answers lookups, and silently checks
        /// nothing. Where only the tags are wrong, the row works fine. Both
        /// shapes come back from here, because separating them needs the text's
        /// actual language and the plugin does not guess at that – see
        /// <see cref="LanguageUtils.EntryDirectionContradictsTermbase"/>.
        ///
        /// Reported rather than repaired for the same reason; the repair lives
        /// in <c>tools/repair_termbase_directions.py</c> (and its LLM-backed
        /// sibling), which can weigh the text as well as the tags.
        ///
        /// The SQL prunes to rows whose tags differ from their termbase's at
        /// all – on a real database that is a handful out of tens of thousands –
        /// and the language comparison itself is done in C# so it goes through
        /// the same name/BCP-47 normalisation as every other direction decision.
        /// </summary>
        /// <param name="disabledTermbaseIds">Termbase IDs to skip (Read tick off).</param>
        /// <param name="maxSamplesPerTermbase">How many affected rows to quote per termbase.</param>
        public List<TermbaseDirectionMismatch> GetDirectionMismatchedTerms(
            HashSet<long> disabledTermbaseIds = null, int maxSamplesPerTermbase = 5)
        {
            var result = new List<TermbaseDirectionMismatch>();
            if (_connection == null) return result;

            const string sql = @"
                SELECT t.source_term, t.target_term, t.source_lang, t.target_lang,
                       tb.id AS tb_id, tb.name AS tb_name,
                       tb.source_lang AS tb_src, tb.target_lang AS tb_tgt
                FROM termbase_terms t
                JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE COALESCE(t.source_lang, '') <> ''
                  AND COALESCE(t.target_lang, '') <> ''
                  AND LOWER(t.source_lang) <> LOWER(COALESCE(tb.source_lang, ''))
                ORDER BY tb.name, t.id";

            var byTermbase = new Dictionary<long, TermbaseDirectionMismatch>();
            using (var cmd = new SqliteCommand(sql, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var tbId = reader.GetInt64(reader.GetOrdinal("tb_id"));
                    if (disabledTermbaseIds != null && disabledTermbaseIds.Contains(tbId)) continue;

                    var tbSrc = GetStringByName(reader, "tb_src");
                    var tbTgt = GetStringByName(reader, "tb_tgt");
                    var srcTerm = GetStringByName(reader, "source_term");
                    var tgtTerm = GetStringByName(reader, "target_term");
                    if (!LanguageUtils.EntryDirectionContradictsTermbase(
                            srcTerm, tgtTerm,
                            GetStringByName(reader, "source_lang"),
                            GetStringByName(reader, "target_lang"),
                            tbSrc, tbTgt))
                        continue;

                    TermbaseDirectionMismatch group;
                    if (!byTermbase.TryGetValue(tbId, out group))
                    {
                        group = new TermbaseDirectionMismatch
                        {
                            TermbaseId = tbId,
                            TermbaseName = GetStringByName(reader, "tb_name"),
                            DeclaredDirection = $"{tbSrc} → {tbTgt}"
                        };
                        byTermbase[tbId] = group;
                        result.Add(group);
                    }

                    group.Count++;
                    if (group.Samples.Count < maxSamplesPerTermbase)
                        group.Samples.Add(srcTerm + " → " + tgtTerm);
                }
            }

            return result;
        }

        /// <summary>
        /// Searches for terms matching the given word/phrase across all active termbases.
        /// Mirrors Supervertaler's search_termbases() logic.
        ///
        /// Matches the SOURCE and TARGET columns alike. It originally matched
        /// source only, which made the MCP term lookup blind to any entry
        /// storing the queried text in its target column – for a query in the
        /// project's target language every hit came from reversed (corrupted)
        /// entries, which made the corruption itself invisible. Rows are
        /// returned in STORED orientation; callers that need project
        /// orientation swap for themselves (as LoadAllTerms does).
        /// </summary>
        public List<TermEntry> SearchTerm(string searchTerm)
        {
            var results = new List<TermEntry>();
            if (_connection == null || string.IsNullOrWhiteSpace(searchTerm))
                return results;

            var normalised = searchTerm.Trim();

            var ntCol = _hasNonTranslatableColumn ? ", t.is_nontranslatable" : "";
            var uuidCol = _hasTermUuidColumn ? ", t.term_uuid" : "";
            var abbrCol = _hasAbbreviationColumns ? ", t.source_abbreviation, t.target_abbreviation" : "";
            var urlCol = _hasUrlColumn ? ", t.url" : "";
            var sql = $@"
                SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                       t.source_lang, t.target_lang, t.definition, t.domain,
                       t.notes, t.forbidden, t.case_sensitive, t.client, t.project,
                       tb.name AS termbase_name,
                       tb.is_project_termbase,
                       COALESCE(tb.ranking, 99) AS ranking
                       {ntCol}
                       {uuidCol}
                       {abbrCol}
                       {urlCol}
                FROM termbase_terms t
                LEFT JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE (LOWER(TRIM(t.source_term)) = LOWER(@term)
                    OR LOWER(RTRIM(TRIM(t.source_term), '.!?,;:')) = LOWER(@term)
                    OR LOWER(@term) = LOWER(RTRIM(TRIM(t.source_term), '.!?,;:'))
                    OR LOWER(TRIM(t.target_term)) = LOWER(@term)
                    OR LOWER(RTRIM(TRIM(t.target_term), '.!?,;:')) = LOWER(@term)
                    OR LOWER(@term) = LOWER(RTRIM(TRIM(t.target_term), '.!?,;:')))
                ORDER BY ranking ASC, t.source_term ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@term", normalised);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var entry = ReadTermEntry(reader, _hasNonTranslatableColumn);
                        results.Add(entry);
                    }
                }
            }

            // Load synonyms for each result
            foreach (var entry in results)
            {
                entry.TargetSynonyms = GetTargetSynonyms(entry.Id);
            }

            return results;
        }

        /// <summary>
        /// Substring search across source AND target terms, for callers that
        /// can't guarantee the exact stored form (notably the MCP bridge's
        /// term-lookup, where an AI passes inflected or partial forms like
        /// "sluitkracht" for a stored "sluitkracht van de grendel"). Matches
        /// case-insensitively anywhere in either term. Shorter source terms
        /// rank first (closest to a whole-term match), then termbase ranking.
        /// </summary>
        public List<TermEntry> SearchTermSubstring(string searchTerm, int maxResults = 20)
        {
            var results = new List<TermEntry>();
            if (_connection == null || string.IsNullOrWhiteSpace(searchTerm))
                return results;

            // Escape LIKE wildcards in the user's text so they match literally.
            var pattern = "%" + searchTerm.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_") + "%";

            var ntCol = _hasNonTranslatableColumn ? ", t.is_nontranslatable" : "";
            var uuidCol = _hasTermUuidColumn ? ", t.term_uuid" : "";
            var abbrCol = _hasAbbreviationColumns ? ", t.source_abbreviation, t.target_abbreviation" : "";
            var urlCol = _hasUrlColumn ? ", t.url" : "";
            var sql = $@"
                SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                       t.source_lang, t.target_lang, t.definition, t.domain,
                       t.notes, t.forbidden, t.case_sensitive, t.client, t.project,
                       tb.name AS termbase_name,
                       tb.is_project_termbase,
                       COALESCE(tb.ranking, 99) AS ranking
                       {ntCol}
                       {uuidCol}
                       {abbrCol}
                       {urlCol}
                FROM termbase_terms t
                LEFT JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE t.source_term LIKE @pattern ESCAPE '\'
                   OR t.target_term LIKE @pattern ESCAPE '\'
                ORDER BY LENGTH(t.source_term) ASC, ranking ASC, t.source_term ASC
                LIMIT @limit";

            using (var cmd = new SqliteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@pattern", pattern);
                cmd.Parameters.AddWithValue("@limit", maxResults);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var entry = ReadTermEntry(reader, _hasNonTranslatableColumn);
                        results.Add(entry);
                    }
                }
            }

            foreach (var entry in results)
            {
                entry.TargetSynonyms = GetTargetSynonyms(entry.Id);
            }

            return results;
        }

        /// <summary>
        /// Bulk-loads all source terms for fast in-memory matching.
        /// Returns a dictionary mapping lowercased source term to list of entries.
        /// </summary>
        /// <param name="disabledTermbaseIds">
        /// Termbase IDs to exclude. Null or empty means load all termbases.
        /// </param>
        public Dictionary<string, List<TermEntry>> LoadAllTerms(HashSet<long> disabledTermbaseIds = null,
            bool globalCaseSensitive = false, string projectSourceLang = null)
        {
            var index = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            if (_connection == null) return index;

            // Load per-termbase case_sensitive settings
            var termbaseCaseSettings = new Dictionary<long, int>();
            if (_hasTermbaseCaseSensitive)
            {
                using (var csCmd = new SqliteCommand("SELECT id, case_sensitive FROM termbases", _connection))
                using (var csReader = csCmd.ExecuteReader())
                {
                    while (csReader.Read())
                    {
                        var tbId = csReader.GetInt64(0);
                        var cs = csReader.IsDBNull(1) ? -1 : csReader.GetInt32(1);
                        termbaseCaseSettings[tbId] = cs;
                    }
                }
            }

            // Load per-termbase declared direction (source_lang, target_lang). This is the
            // canonical direction for the termbase – every entry inside it inherits this.
            // Historically the inversion-decision used entry.source_lang, which is a copy
            // that legacy write bugs (pre-v4.19.13) could get wrong. Using the termbase's
            // declared direction here is resilient to those corrupted per-entry tags.
            var termbaseDirection = GetTermbaseDirections();

            var ntCol = _hasNonTranslatableColumn ? ", t.is_nontranslatable" : "";
            var uuidCol = _hasTermUuidColumn ? ", t.term_uuid" : "";
            var abbrCol = _hasAbbreviationColumns ? ", t.source_abbreviation, t.target_abbreviation" : "";
            var urlCol = _hasUrlColumn ? ", t.url" : "";
            var sql = $@"
                SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                       t.source_lang, t.target_lang, t.definition, t.domain,
                       t.notes, t.forbidden, t.case_sensitive, t.client, t.project,
                       tb.name AS termbase_name,
                       tb.is_project_termbase,
                       COALESCE(tb.ranking, 99) AS ranking
                       {ntCol}
                       {uuidCol}
                       {abbrCol}
                       {urlCol}
                FROM termbase_terms t
                LEFT JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE 1=1";

            if (disabledTermbaseIds != null && disabledTermbaseIds.Count > 0)
            {
                // Build explicit exclusion list – parameterised via positional args
                var placeholders = new List<string>();
                int i = 0;
                foreach (var _ in disabledTermbaseIds)
                    placeholders.Add($"@ex{i++}");
                sql += $" AND CAST(t.termbase_id AS INTEGER) NOT IN ({string.Join(",", placeholders)})";
            }

            sql += " ORDER BY ranking ASC";

            // First pass: load all term entries
            var allEntries = new List<TermEntry>();

            using (var cmd = new SqliteCommand(sql, _connection))
            {
                if (disabledTermbaseIds != null && disabledTermbaseIds.Count > 0)
                {
                    int i = 0;
                    foreach (var id in disabledTermbaseIds)
                        cmd.Parameters.AddWithValue($"@ex{i++}", id);
                }

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        allEntries.Add(ReadTermEntry(reader, _hasNonTranslatableColumn));
                    }
                }
            }

            // Second pass: bulk-load all target synonyms into a dictionary
            var synonymsByTermId = BulkLoadTargetSynonyms();

            // Third pass: bulk-load source synonyms for indexing
            var sourceSynonymsByTermId = BulkLoadSourceSynonyms();

            // Build the index and hydrate synonyms
            foreach (var entry in allEntries)
            {
                if (synonymsByTermId.TryGetValue(entry.Id, out var syns))
                    entry.TargetSynonyms = syns;

                // Resolve case sensitivity: per-termbase override > global default
                if (termbaseCaseSettings.TryGetValue(entry.TermbaseId, out var tbCs) && tbCs >= 0)
                    entry.CaseSensitive = tbCs == 1;
                else
                    entry.CaseSensitive = globalCaseSensitive;

                // If the project source language is the inverse of this termbase's source language
                // (e.g. NL→EN project using an EN→NL termbase), swap source and target so that
                // the TermMatcher indexes the correct language for segment lookup.
                //
                // Use the termbase's DECLARED source language (from the `termbases` table), not
                // the per-entry `source_lang` column. The per-entry column is a copy that legacy
                // write bugs could get wrong – trusting it here was the root cause of entries
                // silently not matching even though their text was fine. The termbase declaration
                // is the canonical direction every entry inherits by definition.
                List<string> srcSynsForIndex;
                string termbaseSrcLang = "";
                string termbaseTgtLang = "";
                if (termbaseDirection.TryGetValue(entry.TermbaseId, out var dir))
                {
                    termbaseSrcLang = dir.src ?? "";
                    termbaseTgtLang = dir.tgt ?? "";
                }

                // Read paths skip Unrelated entries to avoid indexing terms
                // under languages they have no content for. NotApplicable and
                // Aligned both mean "don't invert"; only Inverted swaps.
                var direction = LanguageUtils.CompareTermbaseDirection(
                    projectSourceLang, termbaseSrcLang, termbaseTgtLang);
                if (direction == LanguageUtils.TermbaseDirection.Unrelated)
                    continue;
                bool isInverted = direction == LanguageUtils.TermbaseDirection.Inverted;

                if (isInverted)
                {
                    var t = entry.SourceTerm; entry.SourceTerm = entry.TargetTerm; entry.TargetTerm = t;
                    var tl = entry.SourceLang; entry.SourceLang = entry.TargetLang; entry.TargetLang = tl;
                    var ta = entry.SourceAbbreviation; entry.SourceAbbreviation = entry.TargetAbbreviation; entry.TargetAbbreviation = ta;
                    // DB target synonyms (now the source language) become index keys;
                    // DB source synonyms become the displayed target alternatives.
                    srcSynsForIndex = entry.TargetSynonyms;
                    sourceSynonymsByTermId.TryGetValue(entry.Id, out var engSyns);
                    entry.TargetSynonyms = engSyns ?? new List<string>();
                }
                else
                {
                    sourceSynonymsByTermId.TryGetValue(entry.Id, out srcSynsForIndex);
                }

                // Hydrate SourceSynonyms so TermBlock can show the synonym indicator.
                // After inversion, srcSynsForIndex holds the project-source synonyms.
                if (srcSynsForIndex != null && srcSynsForIndex.Count > 0)
                {
                    entry.SourceSynonyms = new List<SynonymEntry>();
                    foreach (var synText in srcSynsForIndex)
                        entry.SourceSynonyms.Add(new SynonymEntry { Text = synText, Language = "source" });
                }

                var key = TermMatcher.NormalizeScriptChars(entry.SourceTerm.Trim().ToLowerInvariant());

                // Also index with trailing punctuation stripped
                var stripped = key.TrimEnd('.', '!', '?', ',', ';', ':');

                if (!index.ContainsKey(key))
                    index[key] = new List<TermEntry>();
                index[key].Add(entry);

                if (stripped != key && stripped.Length > 0)
                {
                    if (!index.ContainsKey(stripped))
                        index[stripped] = new List<TermEntry>();
                    index[stripped].Add(entry);
                }

                // Index source synonyms as additional keys pointing to the same entry
                if (srcSynsForIndex != null)
                {
                    foreach (var synText in srcSynsForIndex)
                    {
                        var synKey = TermMatcher.NormalizeScriptChars(synText.Trim().ToLowerInvariant());
                        if (string.IsNullOrEmpty(synKey) || synKey == key) continue;

                        if (!index.ContainsKey(synKey))
                            index[synKey] = new List<TermEntry>();
                        index[synKey].Add(entry);

                        var synStripped = synKey.TrimEnd('.', '!', '?', ',', ';', ':');
                        if (synStripped != synKey && synStripped.Length > 0)
                        {
                            if (!index.ContainsKey(synStripped))
                                index[synStripped] = new List<TermEntry>();
                            index[synStripped].Add(entry);
                        }
                    }
                }

                // Index source abbreviation variant(s) as additional keys
                foreach (var abbrVariant in entry.GetSourceAbbreviationVariants())
                {
                    var abbrKey = TermMatcher.NormalizeScriptChars(abbrVariant.Trim().ToLowerInvariant());
                    if (string.IsNullOrEmpty(abbrKey) || abbrKey == key) continue;

                    if (!index.ContainsKey(abbrKey))
                        index[abbrKey] = new List<TermEntry>();
                    index[abbrKey].Add(entry);

                    var abbrStripped = abbrKey.TrimEnd('.', '!', '?', ',', ';', ':');
                    if (abbrStripped != abbrKey && abbrStripped.Length > 0)
                    {
                        if (!index.ContainsKey(abbrStripped))
                            index[abbrStripped] = new List<TermEntry>();
                        index[abbrStripped].Add(entry);
                    }
                }
            }

            return index;
        }

        private List<string> GetTargetSynonyms(long termId)
        {
            var synonyms = new List<string>();
            if (_connection == null) return synonyms;

            const string sql = @"
                SELECT synonym_text FROM termbase_synonyms
                WHERE term_id = @termId AND language = 'target' AND forbidden = 0
                ORDER BY display_order ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@termId", termId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                            synonyms.Add(reader.GetString(0));
                    }
                }
            }

            return synonyms;
        }

        /// <summary>
        /// Bulk-loads all target synonyms in one query.
        /// Returns a dictionary mapping term_id → list of synonym texts.
        /// Used by LoadAllTerms() for efficient synonym hydration.
        /// </summary>
        private Dictionary<long, List<string>> BulkLoadTargetSynonyms()
        {
            var result = new Dictionary<long, List<string>>();
            if (_connection == null) return result;

            const string sql = @"
                SELECT term_id, synonym_text FROM termbase_synonyms
                WHERE language = 'target' AND forbidden = 0
                ORDER BY term_id, display_order ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;

                    var termId = reader.GetInt64(0);
                    var text = reader.GetString(1);

                    if (!result.ContainsKey(termId))
                        result[termId] = new List<string>();
                    result[termId].Add(text);
                }
            }

            return result;
        }

        /// <summary>
        /// Bulk-loads all source synonyms in one query.
        /// Returns a dictionary mapping term_id → list of synonym texts.
        /// Used by LoadAllTerms() to index source synonyms for matching.
        /// </summary>
        private Dictionary<long, List<string>> BulkLoadSourceSynonyms()
        {
            var result = new Dictionary<long, List<string>>();
            if (_connection == null) return result;

            // Check if the table exists (older databases might not have it)
            if (!HasTable(_connection, "termbase_synonyms"))
                return result;

            const string sql = @"
                SELECT term_id, synonym_text FROM termbase_synonyms
                WHERE language = 'source' AND forbidden = 0
                ORDER BY term_id, display_order ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;

                    var termId = reader.GetInt64(0);
                    var text = reader.GetString(1);

                    if (!result.ContainsKey(termId))
                        result[termId] = new List<string>();
                    result[termId].Add(text);
                }
            }

            return result;
        }

        /// <summary>
        /// Helper: SQLite stores booleans as integers (0/1). Microsoft.Data.Sqlite
        /// is stricter than System.Data.SQLite about type conversions, so we read
        /// the raw value and convert ourselves.
        /// </summary>
        private static bool GetBool(SqliteDataReader reader, int ordinal)
        {
            var val = reader.GetValue(ordinal);
            if (val is bool b) return b;
            if (val is long l) return l != 0;
            if (val is int i) return i != 0;
            if (val is string s) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            return Convert.ToBoolean(val);
        }

        /// <summary>
        /// Checks whether a column exists in a SQLite table using PRAGMA table_info.
        /// </summary>
        private static bool HasColumn(SqliteConnection conn, string table, string column)
        {
            using (var cmd = new SqliteCommand($"PRAGMA table_info({table})", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var name = reader.GetString(1); // column 1 = "name"
                    if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Ensures required columns exist on termbase_terms and backfills missing UUIDs.
        /// Idempotent – safe to call multiple times.
        /// </summary>
        private static void MigrateSchema(SqliteConnection conn)
        {
            if (!HasColumn(conn, "termbase_terms", "is_nontranslatable"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN is_nontranslatable BOOLEAN DEFAULT 0", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Ensure term_uuid column exists (for Supervertaler import/export compatibility)
            if (!HasColumn(conn, "termbase_terms", "term_uuid"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN term_uuid TEXT", conn))
                {
                    cmd.ExecuteNonQuery();
                }

                // Create unique index (same as Supervertaler Python app)
                using (var cmd = new SqliteCommand(
                    "CREATE UNIQUE INDEX IF NOT EXISTS idx_termbase_term_uuid ON termbase_terms(term_uuid)", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Ensure abbreviation columns exist
            if (!HasColumn(conn, "termbase_terms", "source_abbreviation"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN source_abbreviation TEXT DEFAULT ''", conn))
                {
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN target_abbreviation TEXT DEFAULT ''", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Ensure termbases table has case_sensitive column
            if (!HasColumn(conn, "termbases", "case_sensitive"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbases ADD COLUMN case_sensitive INTEGER DEFAULT -1", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Ensure url column exists
            if (!HasColumn(conn, "termbase_terms", "url"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN url TEXT DEFAULT ''", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Ensure project column exists – free-text "which project did this
            // term come from" bookkeeping field, mirrors the Workbench schema.
            if (!HasColumn(conn, "termbase_terms", "project"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN project TEXT DEFAULT ''", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Ensure client column exists – Workbench's schema has it; older
            // Trados-only databases may not.
            if (!HasColumn(conn, "termbase_terms", "client"))
            {
                using (var cmd = new SqliteCommand(
                    "ALTER TABLE termbase_terms ADD COLUMN client TEXT DEFAULT ''", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // Backfill missing UUIDs (same as Supervertaler's generate_missing_uuids)
            BackfillMissingUuids(conn);
        }

        /// <summary>
        /// Generates UUIDs for any termbase_terms rows that have NULL or empty term_uuid.
        /// Matches Supervertaler Python's generate_missing_uuids() behaviour.
        /// </summary>
        private static void BackfillMissingUuids(SqliteConnection conn)
        {
            // Quick check: any rows need backfill?
            using (var countCmd = new SqliteCommand(
                "SELECT COUNT(*) FROM termbase_terms WHERE term_uuid IS NULL OR term_uuid = ''", conn))
            {
                var count = Convert.ToInt64(countCmd.ExecuteScalar());
                if (count == 0) return;
            }

            using (var selectCmd = new SqliteCommand(
                "SELECT id FROM termbase_terms WHERE term_uuid IS NULL OR term_uuid = ''", conn))
            using (var reader = selectCmd.ExecuteReader())
            {
                var ids = new List<long>();
                while (reader.Read())
                    ids.Add(reader.GetInt64(0));
                reader.Close();

                foreach (var id in ids)
                {
                    using (var updateCmd = new SqliteCommand(
                        "UPDATE termbase_terms SET term_uuid = @uuid WHERE id = @id", conn))
                    {
                        updateCmd.Parameters.AddWithValue("@uuid", System.Guid.NewGuid().ToString());
                        updateCmd.Parameters.AddWithValue("@id", id);
                        updateCmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static TermEntry ReadTermEntry(SqliteDataReader reader, bool hasNonTranslatableColumn = false)
        {
            // Use column name lookup instead of hardcoded positions –
            // resilient to schema changes and optional column ordering.
            var entry = new TermEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SourceTerm = GetStringByName(reader, "source_term"),
                TargetTerm = GetStringByName(reader, "target_term"),
                TermbaseId = Convert.ToInt64(reader.GetValue(reader.GetOrdinal("termbase_id"))),
                SourceLang = GetStringByName(reader, "source_lang"),
                TargetLang = GetStringByName(reader, "target_lang"),
                Definition = GetStringByName(reader, "definition"),
                Domain = GetStringByName(reader, "domain"),
                Notes = GetStringByName(reader, "notes"),
                Forbidden = GetBoolByName(reader, "forbidden"),
                CaseSensitive = GetBoolByName(reader, "case_sensitive"),
                TermbaseName = GetStringByName(reader, "termbase_name"),
                IsProjectTermbase = GetBoolByName(reader, "is_project_termbase"),
                Ranking = GetIntByName(reader, "ranking", 99),
            };

            // Optional columns – use TryGetOrdinal to avoid exceptions
            int ord;
            if (hasNonTranslatableColumn && TryGetOrdinal(reader, "is_nontranslatable", out ord))
                entry.IsNonTranslatable = !reader.IsDBNull(ord) && GetBool(reader, ord);

            if (TryGetOrdinal(reader, "term_uuid", out ord) && !reader.IsDBNull(ord))
                entry.TermUuid = reader.GetString(ord);

            if (TryGetOrdinal(reader, "source_abbreviation", out ord) && !reader.IsDBNull(ord))
                entry.SourceAbbreviation = reader.GetString(ord);

            if (TryGetOrdinal(reader, "target_abbreviation", out ord) && !reader.IsDBNull(ord))
                entry.TargetAbbreviation = reader.GetString(ord);

            if (TryGetOrdinal(reader, "url", out ord) && !reader.IsDBNull(ord))
                entry.Url = reader.GetString(ord);

            if (TryGetOrdinal(reader, "client", out ord) && !reader.IsDBNull(ord))
                entry.Client = reader.GetString(ord);

            if (TryGetOrdinal(reader, "project", out ord) && !reader.IsDBNull(ord))
                entry.Project = reader.GetString(ord);

            // created_date is stored as TEXT (SQLite CURRENT_TIMESTAMP returns
            // 'YYYY-MM-DD HH:MM:SS' UTC) but Microsoft.Data.Sqlite may also
            // expose it as DateTime depending on version. Handle both shapes.
            if (TryGetOrdinal(reader, "created_date", out ord) && !reader.IsDBNull(ord))
            {
                try
                {
                    var raw = reader.GetValue(ord);
                    if (raw is DateTime dt)
                    {
                        entry.CreatedDate = dt;
                    }
                    else if (raw is string s
                        && DateTime.TryParse(
                            s,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal
                                | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        entry.CreatedDate = parsed.ToLocalTime();
                    }
                }
                catch { /* malformed date – leave null */ }
            }

            return entry;
        }

        /// <summary>
        /// Tries to find a column ordinal by name without throwing.
        /// Returns false if the column does not exist in the result set.
        /// </summary>
        private static bool TryGetOrdinal(SqliteDataReader reader, string columnName, out int ordinal)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = i;
                    return true;
                }
            }
            ordinal = -1;
            return false;
        }

        private static string GetStringByName(SqliteDataReader reader, string columnName)
        {
            var ord = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ord) ? "" : reader.GetString(ord);
        }

        private static bool GetBoolByName(SqliteDataReader reader, string columnName)
        {
            var ord = reader.GetOrdinal(columnName);
            return !reader.IsDBNull(ord) && GetBool(reader, ord);
        }

        private static int GetIntByName(SqliteDataReader reader, string columnName, int defaultValue)
        {
            var ord = reader.GetOrdinal(columnName);
            return reader.IsDBNull(ord) ? defaultValue : reader.GetInt32(ord);
        }

        /// <summary>
        /// Gets a single termbase's info by ID.
        /// </summary>
        public TermbaseInfo GetTermbaseById(long termbaseId)
        {
            if (_connection == null) return null;

            const string sql = @"
                SELECT tb.id, tb.name, tb.source_lang, tb.target_lang,
                       tb.is_project_termbase, tb.ranking,
                       COUNT(t.id) as term_count
                FROM termbases tb
                LEFT JOIN termbase_terms t ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE tb.id = @id
                GROUP BY tb.id";

            using (var cmd = new SqliteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@id", termbaseId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new TermbaseInfo
                        {
                            Id = reader.GetInt64(0),
                            Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            SourceLang = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            TargetLang = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            IsProjectTermbase = !reader.IsDBNull(4) && GetBool(reader, 4),
                            Ranking = reader.IsDBNull(5) ? 99 : reader.GetInt32(5),
                            TermCount = reader.GetInt32(6)
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Inserts a new term using a short-lived ReadWrite connection.
        /// Separate from the main ReadOnly connection to preserve WAL safety
        /// and minimise lock duration.
        /// </summary>
        /// <returns>The ID of the newly inserted term, or -1 on failure.</returns>
        // Trailing sentence punctuation stripped from translatable terms on save
        // (Scope 1). Deliberately excludes quotes and brackets/parens so wrapping
        // characters like "term" or (25) are preserved. Matches the trailing-
        // punctuation set used at match time (TrimEnd / RTRIM), keeping storage
        // and matching consistent.
        private static readonly char[] TermTrailingPunct = { '.', ',', ';', ':', '!', '?' };

        /// <summary>
        /// Folds Unicode space variants (no-break space, narrow no-break space,
        /// en/em/thin spaces, ideographic space – see TermMatcher.IsSpaceVariant)
        /// and tabs to a plain ASCII space, removes zero-width characters
        /// (ZWSP U+200B, word joiner U+2060, BOM U+FEFF), collapses runs of
        /// spaces and trims. Terms picked up from editor selections can carry
        /// these invisible characters (IDML-derived segments are a common
        /// source); stored verbatim they silently defeat term matching, so
        /// every term/synonym write path sanitises through here.
        /// </summary>
        internal static string SanitizeTermWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            bool prevSpace = false;
            foreach (var c in text)
            {
                if (c == '\u200B' || c == '\u2060' || c == '\uFEFF')
                    continue; // zero-width – drop entirely

                var ch = (c == '\t' || TermMatcher.IsSpaceVariant(c)) ? ' ' : c;
                if (ch == ' ')
                {
                    if (prevSpace) continue; // collapse runs
                    prevSpace = true;
                }
                else
                {
                    prevSpace = false;
                }
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Sanitise whitespace and strip trailing sentence punctuation
        /// from a term before storage (e.g. "circumference." -> "circumference").</summary>
        private static string NormalizeTermForSave(string text)
            => string.IsNullOrEmpty(text) ? text : SanitizeTermWhitespace(text).TrimEnd(TermTrailingPunct).Trim();

        public static long InsertTerm(string dbPath, long termbaseId,
            string sourceTerm, string targetTerm,
            string sourceLang, string targetLang,
            string definition = "", string domain = "", string notes = "",
            bool isNonTranslatable = false,
            string sourceAbbreviation = null, string targetAbbreviation = null,
            string url = null, string client = null, bool forbidden = false,
            string project = null, string partOfSpeech = null, string context = null)
        {
            // Strip trailing sentence punctuation from translatable terms so
            // "circumference." is stored as "circumference". Non-translatables are
            // left as-is (a trailing "." may be meaningful, e.g. "Inc.").
            if (!isNonTranslatable)
            {
                sourceTerm = NormalizeTermForSave(sourceTerm);
                targetTerm = NormalizeTermForSave(targetTerm);
            }
            else
            {
                // Non-translatables keep trailing punctuation (e.g. "Inc.")
                // but still get invisible-whitespace sanitising.
                sourceTerm = SanitizeTermWhitespace(sourceTerm);
                targetTerm = SanitizeTermWhitespace(targetTerm);
            }

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                // Skip if an entry representing the same concept already exists in this
                // termbase – check BOTH directions, so a term stored as
                // source=A target=B is detected when the user tries to add B→A too.
                // Handles the case where the same concept was previously stored with
                // source/target swapped (e.g. due to a direction-mismatched termbase).
                const string checkSql = @"
                    SELECT id FROM termbase_terms
                    WHERE CAST(termbase_id AS INTEGER) = @tbId
                      AND (
                        (LOWER(TRIM(source_term)) = LOWER(@source)
                         AND LOWER(TRIM(target_term)) = LOWER(@target))
                        OR
                        (LOWER(TRIM(source_term)) = LOWER(@target)
                         AND LOWER(TRIM(target_term)) = LOWER(@source))
                      )
                    LIMIT 1";

                using (var check = new SqliteCommand(checkSql, conn))
                {
                    check.Parameters.AddWithValue("@tbId", termbaseId);
                    check.Parameters.AddWithValue("@source", sourceTerm.Trim());
                    check.Parameters.AddWithValue("@target", targetTerm.Trim());

                    var existing = check.ExecuteScalar();
                    if (existing != null)
                        return -1; // duplicate – already exists (either direction)
                }

                const string sql = @"
                    INSERT INTO termbase_terms
                        (source_term, target_term, termbase_id, source_lang, target_lang,
                         definition, domain, notes, forbidden, case_sensitive, is_nontranslatable,
                         term_uuid, source_abbreviation, target_abbreviation, url, client, project,
                         part_of_speech, context)
                    VALUES
                        (@source, @target, @tbId, @srcLang, @tgtLang,
                         @def, @domain, @notes, @forbidden, 0, @nt,
                         @uuid, @srcAbbr, @tgtAbbr, @url, @client, @project,
                         @pos, @context);
                    SELECT last_insert_rowid();";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@source", sourceTerm.Trim());
                    cmd.Parameters.AddWithValue("@target", targetTerm.Trim());
                    cmd.Parameters.AddWithValue("@tbId", termbaseId);
                    cmd.Parameters.AddWithValue("@srcLang", sourceLang);
                    cmd.Parameters.AddWithValue("@tgtLang", targetLang);
                    cmd.Parameters.AddWithValue("@def", definition ?? "");
                    cmd.Parameters.AddWithValue("@domain", domain ?? "");
                    cmd.Parameters.AddWithValue("@notes", notes ?? "");
                    cmd.Parameters.AddWithValue("@nt", isNonTranslatable ? 1 : 0);
                    cmd.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                    cmd.Parameters.AddWithValue("@uuid", System.Guid.NewGuid().ToString());
                    cmd.Parameters.AddWithValue("@srcAbbr", sourceAbbreviation ?? "");
                    cmd.Parameters.AddWithValue("@tgtAbbr", targetAbbreviation ?? "");
                    cmd.Parameters.AddWithValue("@url", url ?? "");
                    cmd.Parameters.AddWithValue("@client", client ?? "");
                    cmd.Parameters.AddWithValue("@project", project ?? "");
                    cmd.Parameters.AddWithValue("@pos", partOfSpeech ?? "");
                    cmd.Parameters.AddWithValue("@context", context ?? "");

                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt64(result) : -1;
                }
            }
        }

        /// <summary>
        /// One flattened bilingual row to import into a Supervertaler termbase, produced
        /// by <see cref="TermbaseImporter"/> from a concept-oriented MultiTerm entry.
        /// </summary>
        public sealed class ImportTermRow
        {
            public string SourceTerm { get; set; }
            public string TargetTerm { get; set; }
            public string SourceLang { get; set; }
            public string TargetLang { get; set; }
            public string Definition { get; set; }
            public string Domain { get; set; }
            public string Notes { get; set; }
            public string Context { get; set; }
            public string PartOfSpeech { get; set; }
            public string Url { get; set; }
            public string Client { get; set; }
            public string Project { get; set; }
            public bool Forbidden { get; set; }
            public bool IsNonTranslatable { get; set; }
            public List<string> SourceSynonyms { get; set; } = new List<string>();
            public List<string> TargetSynonyms { get; set; } = new List<string>();
        }

        /// <summary>Outcome counts from <see cref="ImportRows"/>.</summary>
        public sealed class ImportWriteResult
        {
            public int Added { get; set; }
            public int Duplicates { get; set; }
            public int SynonymsAdded { get; set; }
        }

        /// <summary>
        /// Writes many flattened import rows into one termbase in a single transaction.
        /// Applies the same bidirectional duplicate check as <see cref="InsertTerm"/>
        /// (existing pair, either direction, is skipped) and appends each row's synonyms.
        /// The INSERT/dedup SQL mirrors <see cref="InsertTerm"/> — keep the two in sync.
        /// </summary>
        public static ImportWriteResult ImportRows(
            string dbPath, long termbaseId, IReadOnlyList<ImportTermRow> rows)
        {
            var result = new ImportWriteResult();
            if (rows == null || rows.Count == 0) return result;

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                using (var tx = conn.BeginTransaction())
                {
                    foreach (var row in rows)
                    {
                        if (row == null) continue;
                        var source = (row.SourceTerm ?? "").Trim();
                        var target = (row.TargetTerm ?? "").Trim();
                        if (source.Length == 0 || target.Length == 0) continue;

                        // Non-translatables keep their exact text; others are normalised.
                        if (!row.IsNonTranslatable)
                        {
                            source = NormalizeTermForSave(source);
                            target = NormalizeTermForSave(target);
                        }

                        // Bidirectional duplicate check (mirrors InsertTerm).
                        using (var check = new SqliteCommand(@"
                            SELECT id FROM termbase_terms
                            WHERE CAST(termbase_id AS INTEGER) = @tbId
                              AND (
                                (LOWER(TRIM(source_term)) = LOWER(@source)
                                 AND LOWER(TRIM(target_term)) = LOWER(@target))
                                OR
                                (LOWER(TRIM(source_term)) = LOWER(@target)
                                 AND LOWER(TRIM(target_term)) = LOWER(@source))
                              )
                            LIMIT 1", conn, tx))
                        {
                            check.Parameters.AddWithValue("@tbId", termbaseId);
                            check.Parameters.AddWithValue("@source", source);
                            check.Parameters.AddWithValue("@target", target);
                            if (check.ExecuteScalar() != null) { result.Duplicates++; continue; }
                        }

                        long termId;
                        using (var cmd = new SqliteCommand(@"
                            INSERT INTO termbase_terms
                                (source_term, target_term, termbase_id, source_lang, target_lang,
                                 definition, domain, notes, forbidden, case_sensitive, is_nontranslatable,
                                 term_uuid, url, client, project, part_of_speech, context)
                            VALUES
                                (@source, @target, @tbId, @srcLang, @tgtLang,
                                 @def, @domain, @notes, @forbidden, 0, @nt,
                                 @uuid, @url, @client, @project, @pos, @context);
                            SELECT last_insert_rowid();", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@source", source);
                            cmd.Parameters.AddWithValue("@target", target);
                            cmd.Parameters.AddWithValue("@tbId", termbaseId);
                            cmd.Parameters.AddWithValue("@srcLang", row.SourceLang ?? "");
                            cmd.Parameters.AddWithValue("@tgtLang", row.TargetLang ?? "");
                            cmd.Parameters.AddWithValue("@def", row.Definition ?? "");
                            cmd.Parameters.AddWithValue("@domain", row.Domain ?? "");
                            cmd.Parameters.AddWithValue("@notes", row.Notes ?? "");
                            cmd.Parameters.AddWithValue("@forbidden", row.Forbidden ? 1 : 0);
                            cmd.Parameters.AddWithValue("@nt", row.IsNonTranslatable ? 1 : 0);
                            cmd.Parameters.AddWithValue("@uuid", System.Guid.NewGuid().ToString());
                            cmd.Parameters.AddWithValue("@url", row.Url ?? "");
                            cmd.Parameters.AddWithValue("@client", row.Client ?? "");
                            cmd.Parameters.AddWithValue("@project", row.Project ?? "");
                            cmd.Parameters.AddWithValue("@pos", row.PartOfSpeech ?? "");
                            cmd.Parameters.AddWithValue("@context", row.Context ?? "");
                            termId = Convert.ToInt64(cmd.ExecuteScalar());
                        }
                        result.Added++;

                        result.SynonymsAdded += InsertSynonyms(conn, tx, termId, row.SourceSynonyms, "source");
                        result.SynonymsAdded += InsertSynonyms(conn, tx, termId, row.TargetSynonyms, "target");
                    }

                    tx.Commit();
                }
            }

            return result;
        }

        // Inserts distinct, non-empty synonyms for a term within an open transaction.
        private static int InsertSynonyms(
            SqliteConnection conn, SqliteTransaction tx,
            long termId, List<string> synonyms, string language)
        {
            if (synonyms == null || synonyms.Count == 0) return 0;
            int added = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (var raw in synonyms)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var text = NormalizeTermForSave(raw.Trim());
                if (text.Length == 0 || !seen.Add(text.ToLowerInvariant())) continue;

                using (var cmd = new SqliteCommand(@"
                    INSERT INTO termbase_synonyms
                        (term_id, synonym_text, language, display_order, forbidden)
                    VALUES (@termId, @text, @lang, @order, 0)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@termId", termId);
                    cmd.Parameters.AddWithValue("@text", text);
                    cmd.Parameters.AddWithValue("@lang", language);
                    cmd.Parameters.AddWithValue("@order", order++);
                    cmd.ExecuteNonQuery();
                }
                added++;
            }
            return added;
        }

        /// <summary>
        /// Inserts a term into multiple termbases using a single ReadWrite connection
        /// and a single transaction. Much faster than calling InsertTerm() per termbase.
        /// </summary>
        /// <returns>List of (termbaseId, newRowId) pairs for successful inserts.</returns>
        public static List<(long termbaseId, long newId)> InsertTermBatch(
            string dbPath, string sourceTerm, string targetTerm,
            string definition, List<TermbaseInfo> termbases,
            bool isNonTranslatable = false,
            string projectSourceLang = null)
        {
            // Legacy orientation semantics, preserved for the in-Studio quick-add
            // actions: swap only when the project source matches the termbase's
            // target language; NotApplicable / Aligned / Unrelated all store the
            // caller's input as-is. (Pre-v4.19.56 any "not aligned" was treated
            // as "inverted", which silently swapped data on its way into
            // termbases for unrelated language pairs.)
            var outcomes = InsertTermBatchCore(
                dbPath, sourceTerm, targetTerm, definition, "", "",
                termbases, isNonTranslatable,
                tb =>
                {
                    var direction = LanguageUtils.CompareTermbaseDirection(
                        projectSourceLang, tb.SourceLang, tb.TargetLang);
                    return (direction == LanguageUtils.TermbaseDirection.Inverted, null);
                });

            var results = new List<(long, long)>();
            foreach (var o in outcomes)
                if (o.Status == TermInsertOutcome.StatusAdded)
                    results.Add((o.TermbaseId, o.NewId));
            return results;
        }

        /// <summary>
        /// Per-termbase outcome of a batch term insert – built so callers can
        /// report exactly what was stored (and why something wasn't) instead of
        /// a bare list of row ids. Introduced for the MCP add_term path after a
        /// reversed pair was written into two termbases while the tool reported
        /// plain success.
        /// </summary>
        public sealed class TermInsertOutcome
        {
            public const string StatusAdded = "added";
            public const string StatusDuplicate = "duplicate";
            public const string StatusCannotOrient = "cannot-orient";

            public long TermbaseId;
            public string TermbaseName;
            /// <summary>One of the Status* constants.</summary>
            public string Status;
            public long NewId = -1;
            /// <summary>Exactly what went into the source_term column (added only).</summary>
            public string StoredSource;
            public string StoredTarget;
            /// <summary>True when the caller's pair was reoriented for this termbase.</summary>
            public bool Swapped;
            /// <summary>Human-readable reason for duplicate / cannot-orient.</summary>
            public string Detail;
            /// <summary>Duplicate only: the id and stored form of the entry that
            /// already matched, so the caller can see what it hit instead of
            /// taking "duplicate" on faith.</summary>
            public long ExistingId = -1;
            public string ExistingSource;
            public string ExistingTarget;
        }

        /// <summary>
        /// Batch insert with per-termbase orientation and full field support.
        /// Orientation policy (the caller's <c>sourceTerm</c>/<c>targetTerm</c>
        /// are placed into each termbase's columns according to that termbase's
        /// OWN declared direction):
        ///   1. Explicit wins – when <paramref name="explicitSourceLang"/> /
        ///      <paramref name="explicitTargetLang"/> are supplied, they say
        ///      which language each side of the pair is in; termbases whose
        ///      declared pair can't be related to them refuse with
        ///      cannot-orient rather than guessing.
        ///   2. Otherwise the pair is assumed to be in PROJECT direction
        ///      (source = the project's source language) and swapped per
        ///      termbase, as the in-Studio quick-add has always done.
        ///   3. No silent writes on ambiguity: no open document, or a termbase
        ///      for an unrelated language pair, refuses with cannot-orient.
        ///      Language detection is deliberately not attempted – in this
        ///      domain term pairs are routinely identical across languages
        ///      (radar, IFF, transponder), so a detector would guess, and a
        ///      wrong silent write is far worse than a refusal.
        /// </summary>
        public static List<TermInsertOutcome> InsertTermBatchDetailed(
            string dbPath, string sourceTerm, string targetTerm,
            string definition, string domain, string notes,
            List<TermbaseInfo> termbases,
            string projectSourceLang,
            string explicitSourceLang, string explicitTargetLang)
        {
            return InsertTermBatchCore(
                dbPath, sourceTerm, targetTerm, definition, domain, notes,
                termbases, isNonTranslatable: false,
                orient: tb => DecideOrientationStrict(
                    tb, projectSourceLang, explicitSourceLang, explicitTargetLang),
                // Store exactly what the caller sent. Trailing-punctuation
                // stripping exists for the in-Studio quick-add, where the term is
                // captured from a selection in running text and a final "." is
                // sentence punctuation. Here the caller named the string
                // deliberately, and abbreviation-with-period is a legitimate term
                // form: "Rev." -> "Rev." degenerated to "Rev" -> "Rev", losing the
                // decision the entry existed to record. Also seen: NOTE:, Doc.nr.,
                // PO-nr., SAFETY INFORMATION!. Lookup is unaffected - SearchTerm
                // RTRIMs the STORED term as well as the query, so "Rev." still
                // matches a search for "Rev".
                preserveTrailingPunctuation: true);
        }

        /// <summary>
        /// The strict per-termbase orientation decision documented on
        /// <see cref="InsertTermBatchDetailed"/>. Returns (swap, null) when
        /// orientation is established, or (false, reason) when this termbase
        /// must be refused.
        /// </summary>
        private static (bool swap, string cannotOrient) DecideOrientationStrict(
            TermbaseInfo tb, string projectSourceLang,
            string explicitSourceLang, string explicitTargetLang)
        {
            var tbPair = $"{tb.SourceLang} → {tb.TargetLang}";

            bool hasExplicit = !string.IsNullOrWhiteSpace(explicitSourceLang)
                || !string.IsNullOrWhiteSpace(explicitTargetLang);
            if (hasExplicit)
            {
                // CompareTermbaseDirection(lang, tbSrc, tbTgt): Aligned = lang
                // matches the termbase's source side, Inverted = its target side.
                var srcDir = LanguageUtils.CompareTermbaseDirection(
                    explicitSourceLang, tb.SourceLang, tb.TargetLang);
                var tgtDir = LanguageUtils.CompareTermbaseDirection(
                    explicitTargetLang, tb.SourceLang, tb.TargetLang);

                if (srcDir == LanguageUtils.TermbaseDirection.Unrelated
                    || tgtDir == LanguageUtils.TermbaseDirection.Unrelated)
                    return (false, "the supplied sourceLang/targetLang don't match this " +
                        $"termbase's declared pair ({tbPair})");

                // A lang that was omitted (or the termbase declares no languages)
                // comes back NotApplicable and simply doesn't vote.
                bool srcVotes = srcDir == LanguageUtils.TermbaseDirection.Aligned
                    || srcDir == LanguageUtils.TermbaseDirection.Inverted;
                bool tgtVotes = tgtDir == LanguageUtils.TermbaseDirection.Aligned
                    || tgtDir == LanguageUtils.TermbaseDirection.Inverted;
                if (!srcVotes && !tgtVotes)
                    return (false, "could not relate the supplied sourceLang/targetLang to this " +
                        $"termbase's declared pair ({tbPair})");

                // sourceLang matching the termbase TARGET side means the pair
                // arrives reversed for this termbase → swap. targetLang matching
                // the termbase SOURCE side means the same.
                bool srcSaysSwap = srcDir == LanguageUtils.TermbaseDirection.Inverted;
                bool tgtSaysSwap = tgtDir == LanguageUtils.TermbaseDirection.Aligned;
                if (srcVotes && tgtVotes && srcSaysSwap != tgtSaysSwap)
                    return (false, "sourceLang and targetLang resolve to the same side of this " +
                        $"termbase ({tbPair}) – check the two languages are the pair's, one each");

                return (srcVotes ? srcSaysSwap : tgtSaysSwap, null);
            }

            // No explicit languages: assume the pair is in PROJECT direction.
            var dir = LanguageUtils.CompareTermbaseDirection(
                projectSourceLang, tb.SourceLang, tb.TargetLang);
            switch (dir)
            {
                case LanguageUtils.TermbaseDirection.Aligned:
                    return (false, null);
                case LanguageUtils.TermbaseDirection.Inverted:
                    return (true, null);
                case LanguageUtils.TermbaseDirection.NotApplicable:
                    return (false, "no open document to infer term orientation from – " +
                        "pass sourceLang/targetLang");
                default:
                    return (false, $"the termbase's language pair ({tbPair}) matches neither side " +
                        $"of the project's source language ({projectSourceLang}) – " +
                        "pass sourceLang/targetLang");
            }
        }

        private static List<TermInsertOutcome> InsertTermBatchCore(
            string dbPath, string sourceTerm, string targetTerm,
            string definition, string domain, string notes,
            List<TermbaseInfo> termbases, bool isNonTranslatable,
            Func<TermbaseInfo, (bool swap, string cannotOrient)> orient,
            bool preserveTrailingPunctuation = false)
        {
            // Strip trailing sentence punctuation from translatable terms on save
            // (e.g. "circumference." -> "circumference"); non-translatables kept as-is.
            if (!isNonTranslatable && !preserveTrailingPunctuation)
            {
                sourceTerm = NormalizeTermForSave(sourceTerm);
                targetTerm = NormalizeTermForSave(targetTerm);
            }
            else
            {
                // Non-translatables keep trailing punctuation (e.g. "Inc.")
                // but still get invisible-whitespace sanitising.
                sourceTerm = SanitizeTermWhitespace(sourceTerm);
                targetTerm = SanitizeTermWhitespace(targetTerm);
            }

            var outcomes = new List<TermInsertOutcome>();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                using (var txn = conn.BeginTransaction())
                {
                    // Check BOTH directions when deduping. A concept already stored
                    // in the reverse direction (e.g. from an older buggy insert)
                    // counts as a duplicate and must not be added a second time.
                    const string checkSql = @"
                        SELECT id, source_term, target_term FROM termbase_terms
                        WHERE CAST(termbase_id AS INTEGER) = @tbId
                          AND (
                            (LOWER(TRIM(source_term)) = LOWER(@source)
                             AND LOWER(TRIM(target_term)) = LOWER(@target))
                            OR
                            (LOWER(TRIM(source_term)) = LOWER(@target)
                             AND LOWER(TRIM(target_term)) = LOWER(@source))
                          )
                        LIMIT 1";

                    const string sql = @"
                        INSERT INTO termbase_terms
                            (source_term, target_term, termbase_id, source_lang, target_lang,
                             definition, domain, notes, forbidden, case_sensitive, is_nontranslatable,
                             term_uuid)
                        VALUES
                            (@source, @target, @tbId, @srcLang, @tgtLang,
                             @def, @domain, @notes, 0, 0, @nt,
                             @uuid);
                        SELECT last_insert_rowid();";

                    foreach (var tb in termbases)
                    {
                        var outcome = new TermInsertOutcome
                        {
                            TermbaseId = tb.Id,
                            TermbaseName = tb.Name
                        };
                        outcomes.Add(outcome);

                        var decision = orient(tb);
                        if (decision.cannotOrient != null)
                        {
                            outcome.Status = TermInsertOutcome.StatusCannotOrient;
                            outcome.Detail = decision.cannotOrient;
                            continue;
                        }

                        var termForSourceColumn = decision.swap ? targetTerm : sourceTerm;
                        var termForTargetColumn = decision.swap ? sourceTerm : targetTerm;
                        outcome.Swapped = decision.swap;

                        // Skip if duplicate already exists in this termbase (either direction).
                        using (var check = new SqliteCommand(checkSql, conn, txn))
                        {
                            check.Parameters.AddWithValue("@tbId", tb.Id);
                            check.Parameters.AddWithValue("@source", termForSourceColumn.Trim());
                            check.Parameters.AddWithValue("@target", termForTargetColumn.Trim());

                            using (var existing = check.ExecuteReader())
                            {
                                if (existing.Read())
                                {
                                    outcome.Status = TermInsertOutcome.StatusDuplicate;
                                    outcome.ExistingId = existing.GetInt64(0);
                                    outcome.ExistingSource = existing.IsDBNull(1) ? "" : existing.GetString(1);
                                    outcome.ExistingTarget = existing.IsDBNull(2) ? "" : existing.GetString(2);
                                    outcome.Detail = $"an entry for this pair already exists in this termbase " +
                                        $"(id {outcome.ExistingId}: “{outcome.ExistingSource} → {outcome.ExistingTarget}”)";
                                    continue;
                                }
                            }
                        }

                        using (var cmd = new SqliteCommand(sql, conn, txn))
                        {
                            cmd.Parameters.AddWithValue("@source", termForSourceColumn.Trim());
                            cmd.Parameters.AddWithValue("@target", termForTargetColumn.Trim());
                            cmd.Parameters.AddWithValue("@tbId", tb.Id);
                            cmd.Parameters.AddWithValue("@srcLang", tb.SourceLang);
                            cmd.Parameters.AddWithValue("@tgtLang", tb.TargetLang);
                            cmd.Parameters.AddWithValue("@def", definition ?? "");
                            cmd.Parameters.AddWithValue("@domain", domain ?? "");
                            cmd.Parameters.AddWithValue("@notes", notes ?? "");
                            cmd.Parameters.AddWithValue("@nt", isNonTranslatable ? 1 : 0);
                            cmd.Parameters.AddWithValue("@uuid", System.Guid.NewGuid().ToString());

                            var result = cmd.ExecuteScalar();
                            var newId = result != null ? Convert.ToInt64(result) : -1;
                            if (newId > 0)
                            {
                                outcome.Status = TermInsertOutcome.StatusAdded;
                                outcome.NewId = newId;
                                outcome.StoredSource = termForSourceColumn.Trim();
                                outcome.StoredTarget = termForTargetColumn.Trim();
                            }
                            else
                            {
                                outcome.Status = TermInsertOutcome.StatusCannotOrient;
                                outcome.Detail = "insert failed (no row id returned)";
                            }
                        }
                    }
                    txn.Commit();
                }
            }

            return outcomes;
        }

        /// <summary>
        /// Inserts multiple non-translatable terms into a single termbase.
        /// Each term text is used as both source_term and target_term, with
        /// is_nontranslatable = 1. Uses a single connection and transaction.
        /// </summary>
        /// <returns>List of (termText, newRowId) pairs for successful inserts.</returns>
        public static List<(string term, long newId)> InsertNonTranslatableBatch(
            string dbPath, long termbaseId,
            string sourceLang, string targetLang,
            List<string> terms)
        {
            var results = new List<(string, long)>();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                using (var txn = conn.BeginTransaction())
                {
                    const string checkSql = @"
                        SELECT id FROM termbase_terms
                        WHERE CAST(termbase_id AS INTEGER) = @tbId
                          AND LOWER(TRIM(source_term)) = LOWER(@source)
                        LIMIT 1";

                    const string sql = @"
                        INSERT INTO termbase_terms
                            (source_term, target_term, termbase_id, source_lang, target_lang,
                             definition, domain, notes, forbidden, case_sensitive,
                             is_nontranslatable, term_uuid)
                        VALUES
                            (@source, @target, @tbId, @srcLang, @tgtLang,
                             '', '', '', 0, 0, 1, @uuid);
                        SELECT last_insert_rowid();";

                    foreach (var term in terms)
                    {
                        var trimmed = term.Trim();
                        if (string.IsNullOrEmpty(trimmed)) continue;

                        // Skip if this term already exists in the termbase (by source term, since NT has source = target)
                        using (var check = new SqliteCommand(checkSql, conn, txn))
                        {
                            check.Parameters.AddWithValue("@tbId", termbaseId);
                            check.Parameters.AddWithValue("@source", trimmed);

                            if (check.ExecuteScalar() != null)
                                continue; // duplicate – skip
                        }

                        using (var cmd = new SqliteCommand(sql, conn, txn))
                        {
                            cmd.Parameters.AddWithValue("@source", trimmed);
                            cmd.Parameters.AddWithValue("@target", trimmed);
                            cmd.Parameters.AddWithValue("@tbId", termbaseId);
                            cmd.Parameters.AddWithValue("@srcLang", sourceLang);
                            cmd.Parameters.AddWithValue("@tgtLang", targetLang);
                            cmd.Parameters.AddWithValue("@uuid", System.Guid.NewGuid().ToString());

                            var result = cmd.ExecuteScalar();
                            var newId = result != null ? Convert.ToInt64(result) : -1;
                            if (newId > 0)
                                results.Add((trimmed, newId));
                        }
                    }
                    txn.Commit();
                }
            }

            return results;
        }

        /// <summary>
        /// Updates an existing term's source, target, definition, domain, and notes
        /// using a short-lived ReadWrite connection (same pattern as InsertTerm).
        /// Throws InvalidOperationException if the edit would create a duplicate
        /// (same source+target in the same termbase, different row ID).
        /// </summary>
        /// <returns>True if the row was updated, false if the term ID was not found.</returns>
        public static bool UpdateTerm(string dbPath, long termId,
            string sourceTerm, string targetTerm,
            string definition = "", string domain = "", string notes = "",
            bool isNonTranslatable = false,
            string sourceAbbreviation = null, string targetAbbreviation = null,
            string url = null, string client = null, bool forbidden = false,
            string project = null)
        {
            // Strip trailing sentence punctuation from translatable terms on save
            // (e.g. "circumference." -> "circumference"); non-translatables kept as-is.
            if (!isNonTranslatable)
            {
                sourceTerm = NormalizeTermForSave(sourceTerm);
                targetTerm = NormalizeTermForSave(targetTerm);
            }
            else
            {
                // Non-translatables keep trailing punctuation (e.g. "Inc.")
                // but still get invisible-whitespace sanitising.
                sourceTerm = SanitizeTermWhitespace(sourceTerm);
                targetTerm = SanitizeTermWhitespace(targetTerm);
            }

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                // Check for duplicate: another entry with the same source+target in the same termbase
                const string dupSql = @"
                    SELECT COUNT(*) FROM termbase_terms
                    WHERE id <> @id
                      AND CAST(termbase_id AS INTEGER) = (
                          SELECT CAST(termbase_id AS INTEGER) FROM termbase_terms WHERE id = @id)
                      AND LOWER(TRIM(source_term)) = LOWER(@source)
                      AND LOWER(TRIM(target_term)) = LOWER(@target)";

                using (var dup = new SqliteCommand(dupSql, conn))
                {
                    dup.Parameters.AddWithValue("@id", termId);
                    dup.Parameters.AddWithValue("@source", sourceTerm.Trim());
                    dup.Parameters.AddWithValue("@target", targetTerm.Trim());

                    var count = Convert.ToInt64(dup.ExecuteScalar());
                    if (count > 0)
                        throw new InvalidOperationException(
                            "A term with the same source and target already exists in this termbase.");
                }

                const string sql = @"
                    UPDATE termbase_terms
                    SET source_term = @source,
                        target_term = @target,
                        definition  = @def,
                        domain      = @domain,
                        notes       = @notes,
                        is_nontranslatable = @nt,
                        forbidden   = @forbidden,
                        source_abbreviation = @srcAbbr,
                        target_abbreviation = @tgtAbbr,
                        url         = @url,
                        client      = @client,
                        project     = @project,
                        -- Without this an edited entry keeps reporting the date it
                        -- was created: a term whose note was rewritten today read as
                        -- untouched since June, which is exactly backwards when the
                        -- date is being used to work out what recently changed.
                        modified_date = @modified
                    WHERE id = @id";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@source", sourceTerm.Trim());
                    cmd.Parameters.AddWithValue("@target", targetTerm.Trim());
                    cmd.Parameters.AddWithValue("@def", definition ?? "");
                    cmd.Parameters.AddWithValue("@domain", domain ?? "");
                    cmd.Parameters.AddWithValue("@notes", notes ?? "");
                    cmd.Parameters.AddWithValue("@nt", isNonTranslatable ? 1 : 0);
                    cmd.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                    cmd.Parameters.AddWithValue("@srcAbbr", sourceAbbreviation ?? "");
                    cmd.Parameters.AddWithValue("@tgtAbbr", targetAbbreviation ?? "");
                    cmd.Parameters.AddWithValue("@url", url ?? "");
                    cmd.Parameters.AddWithValue("@client", client ?? "");
                    cmd.Parameters.AddWithValue("@project", project ?? "");
                    // Same format the insert path writes, so the two are comparable.
                    cmd.Parameters.AddWithValue("@modified",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@id", termId);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        /// <summary>
        /// Atomically reverses the direction of one or more term entries:
        /// swaps source_term ↔ target_term, source_lang ↔ target_lang,
        /// source_abbreviation ↔ target_abbreviation, and flips the language
        /// tag on every linked synonym ('source' ↔ 'target'). Runs in a single
        /// transaction so partial failures leave the DB unchanged.
        /// </summary>
        /// <returns>Number of term rows whose direction was reversed.</returns>
        public static int ReverseTermDirection(string dbPath, IEnumerable<long> termIds)
        {
            if (termIds == null) return 0;
            var idList = new List<long>();
            foreach (var id in termIds)
                if (id > 0) idList.Add(id);
            if (idList.Count == 0) return 0;

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            int reversed = 0;
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                // Swap term columns. source_abbreviation / target_abbreviation are
                // only present on DBs that were migrated – guarded by the optional
                // column check done at connection open time.
                var hasAbbr = HasColumn(conn, "termbase_terms", "source_abbreviation")
                              && HasColumn(conn, "termbase_terms", "target_abbreviation");
                var termSql = hasAbbr
                    ? @"UPDATE termbase_terms
                           SET source_term = target_term,
                               target_term = source_term,
                               source_lang = target_lang,
                               target_lang = source_lang,
                               source_abbreviation = target_abbreviation,
                               target_abbreviation = source_abbreviation
                         WHERE id = @id"
                    : @"UPDATE termbase_terms
                           SET source_term = target_term,
                               target_term = source_term,
                               source_lang = target_lang,
                               target_lang = source_lang
                         WHERE id = @id";

                // Flip synonym language tags: 'source' ↔ 'target'. A CASE expression
                // keeps this atomic and avoids needing two passes.
                const string synSql = @"
                    UPDATE termbase_synonyms
                       SET language = CASE language
                                        WHEN 'source' THEN 'target'
                                        WHEN 'target' THEN 'source'
                                        ELSE language
                                      END
                     WHERE term_id = @id";

                using (var txn = conn.BeginTransaction())
                using (var termCmd = new SqliteCommand(termSql, conn, txn))
                using (var synCmd = new SqliteCommand(synSql, conn, txn))
                {
                    var termParam = termCmd.Parameters.Add("@id", SqliteType.Integer);
                    var synParam = synCmd.Parameters.Add("@id", SqliteType.Integer);

                    foreach (var id in idList)
                    {
                        termParam.Value = id;
                        var affected = termCmd.ExecuteNonQuery();
                        if (affected > 0)
                        {
                            synParam.Value = id;
                            synCmd.ExecuteNonQuery();
                            reversed++;
                        }
                    }

                    txn.Commit();
                }
            }

            return reversed;
        }

        /// <summary>
        /// Deletes a single term by its ID using a short-lived ReadWrite connection.
        /// Synonyms are cascade-deleted via the FK constraint on termbase_synonyms.
        /// </summary>
        /// <returns>True if the row was deleted, false if the term ID was not found.</returns>
        public static bool DeleteTerm(string dbPath, long termId)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                // Enable foreign keys so CASCADE delete works for termbase_synonyms
                using (var pragma = new SqliteCommand("PRAGMA foreign_keys=ON;", conn))
                    pragma.ExecuteNonQuery();

                const string sql = "DELETE FROM termbase_terms WHERE id = @id";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", termId);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        /// <summary>
        /// Deletes many terms in a single connection and transaction, mirroring
        /// <see cref="InsertTermBatch"/> on the write side.
        ///
        /// Deleting a selection used to call <see cref="DeleteTerm"/> per row, so a
        /// few hundred rows meant a few hundred connections and transactions. That
        /// alone took the best part of a minute; combined with a full panel re-render
        /// per row it froze Studio outright.
        ///
        /// Returns the number of rows actually removed, so a caller can tell a
        /// partial failure from a clean run instead of assuming success.
        /// </summary>
        public static int DeleteTermBatch(string dbPath, IEnumerable<long> termIds)
        {
            if (termIds == null) return 0;

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            int deleted = 0;
            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                // Foreign keys on, so termbase_synonyms cascades as it does for a
                // single delete.
                using (var pragma = new SqliteCommand("PRAGMA foreign_keys=ON;", conn))
                    pragma.ExecuteNonQuery();

                using (var tx = conn.BeginTransaction())
                using (var cmd = new SqliteCommand(
                    "DELETE FROM termbase_terms WHERE id = @id", conn, tx))
                {
                    var p = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer);
                    foreach (var id in termIds)
                    {
                        if (id <= 0) continue;
                        p.Value = id;
                        deleted += cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            return deleted;
        }

        /// <summary>
        /// Toggles the is_nontranslatable flag on a term. When toggling on,
        /// also sets target_term = source_term so the term copies verbatim.
        /// </summary>
        public static bool SetNonTranslatable(string dbPath, long termId,
            bool isNonTranslatable, string sourceTerm = null)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                string sql;
                if (isNonTranslatable && sourceTerm != null)
                {
                    sql = @"UPDATE termbase_terms
                            SET is_nontranslatable = 1, target_term = @source
                            WHERE id = @id";
                }
                else
                {
                    sql = @"UPDATE termbase_terms
                            SET is_nontranslatable = @nt
                            WHERE id = @id";
                }

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", termId);
                    cmd.Parameters.AddWithValue("@nt", isNonTranslatable ? 1 : 0);
                    if (sourceTerm != null)
                        cmd.Parameters.AddWithValue("@source", sourceTerm);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        /// <summary>
        /// Updates the case_sensitive setting on the termbases table.
        /// -1 = use global default, 0 = force case-insensitive, 1 = force case-sensitive.
        /// </summary>
        public static void SetTermbaseCaseSensitive(string dbPath, long termbaseId, int caseSensitive)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                using (var cmd = new SqliteCommand(
                    "UPDATE termbases SET case_sensitive = @cs WHERE id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@cs", caseSensitive);
                    cmd.Parameters.AddWithValue("@id", termbaseId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Renames a termbase. The name column has a UNIQUE constraint, so this
        /// will throw if the new name already exists.
        /// </summary>
        public static void RenameTermbase(string dbPath, long termbaseId, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("Termbase name cannot be empty.");

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    "UPDATE termbases SET name = @name WHERE id = @id", conn))
                {
                    cmd.Parameters.AddWithValue("@name", newName.Trim());
                    cmd.Parameters.AddWithValue("@id", termbaseId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Loads all terms belonging to a specific termbase, for use in the
        /// Termbase Editor dialog. Uses a short-lived ReadOnly connection.
        /// </summary>
        public static List<TermEntry> GetAllTermsByTermbaseId(string dbPath, long termbaseId)
        {
            var results = new List<TermEntry>();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                var hasNtCol = HasColumn(conn, "termbase_terms", "is_nontranslatable");
                var hasUuidCol = HasColumn(conn, "termbase_terms", "term_uuid");
                var ntCol = hasNtCol ? ", t.is_nontranslatable" : "";
                var uuidCol = hasUuidCol ? ", t.term_uuid" : "";
                var sql = $@"
                    SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                           t.source_lang, t.target_lang, t.definition, t.domain,
                           t.notes, t.forbidden, t.case_sensitive,
                           t.created_date,
                           tb.name AS termbase_name,
                           tb.is_project_termbase,
                           COALESCE(tb.ranking, 99) AS ranking
                           {ntCol}
                           {uuidCol}
                    FROM termbase_terms t
                    LEFT JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                    WHERE CAST(t.termbase_id AS INTEGER) = @tbId
                    ORDER BY t.source_term ASC";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@tbId", termbaseId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(ReadTermEntry(reader, hasNtCol));
                        }
                    }
                }
            }

            return results;
        }

        // ==================================================================
        //  Static database management methods (short-lived connections)
        // ==================================================================

        /// <summary>
        /// Creates a new Supervertaler-compatible SQLite database at the given path.
        /// Sets up all required tables, indexes, and pragmas (WAL, foreign keys).
        /// </summary>
        public static void CreateDatabase(string dbPath)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                // WAL must be set outside a transaction (auto-commits)
                using (var pragma = new SqliteCommand("PRAGMA journal_mode=WAL;", conn))
                    pragma.ExecuteNonQuery();

                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand { Connection = conn, Transaction = tx })
                    {
                        cmd.CommandText = "PRAGMA foreign_keys=ON;";
                        cmd.ExecuteNonQuery();

                        // --- termbases ---
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS termbases (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                name TEXT NOT NULL UNIQUE,
                                description TEXT,
                                source_lang TEXT,
                                target_lang TEXT,
                                project_id INTEGER,
                                is_global BOOLEAN DEFAULT 1,
                                is_project_termbase BOOLEAN DEFAULT 0,
                                priority INTEGER DEFAULT 50,
                                ranking INTEGER,
                                read_only BOOLEAN DEFAULT 1,
                                created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                ai_inject BOOLEAN DEFAULT 0
                            );";
                        cmd.ExecuteNonQuery();

                        // --- termbase_terms ---
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS termbase_terms (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                source_term TEXT NOT NULL,
                                target_term TEXT NOT NULL,
                                source_lang TEXT DEFAULT 'unknown',
                                target_lang TEXT DEFAULT 'unknown',
                                termbase_id TEXT NOT NULL,
                                priority INTEGER DEFAULT 99,
                                project_id TEXT,
                                synonyms TEXT,
                                forbidden_terms TEXT,
                                definition TEXT,
                                context TEXT,
                                part_of_speech TEXT,
                                domain TEXT,
                                case_sensitive BOOLEAN DEFAULT 0,
                                forbidden BOOLEAN DEFAULT 0,
                                is_nontranslatable BOOLEAN DEFAULT 0,
                                tm_source_id INTEGER,
                                created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                usage_count INTEGER DEFAULT 0,
                                notes TEXT,
                                note TEXT,
                                project TEXT,
                                client TEXT,
                                term_uuid TEXT
                            );";
                        cmd.ExecuteNonQuery();

                        // --- termbase_synonyms ---
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS termbase_synonyms (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                term_id INTEGER NOT NULL,
                                synonym_text TEXT NOT NULL,
                                language TEXT NOT NULL CHECK(language IN ('source', 'target')),
                                display_order INTEGER DEFAULT 0,
                                forbidden INTEGER DEFAULT 0,
                                created_date TEXT DEFAULT (datetime('now')),
                                modified_date TEXT DEFAULT (datetime('now')),
                                FOREIGN KEY (term_id) REFERENCES termbase_terms(id) ON DELETE CASCADE
                            );";
                        cmd.ExecuteNonQuery();

                        // --- Legacy tables for Supervertaler compatibility ---
                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS glossaries (
                                id INTEGER PRIMARY KEY AUTOINCREMENT,
                                name TEXT NOT NULL,
                                source_lang TEXT,
                                target_lang TEXT,
                                created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                            );";
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS termbase_activation (
                                termbase_id INTEGER NOT NULL,
                                project_id INTEGER NOT NULL,
                                is_active BOOLEAN DEFAULT 1,
                                activated_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                priority INTEGER,
                                PRIMARY KEY (termbase_id, project_id),
                                FOREIGN KEY (termbase_id) REFERENCES termbases(id) ON DELETE CASCADE
                            );";
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = @"
                            CREATE TABLE IF NOT EXISTS termbase_project_activation (
                                termbase_id INTEGER NOT NULL,
                                project_id INTEGER NOT NULL,
                                activated_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                                PRIMARY KEY (termbase_id, project_id),
                                FOREIGN KEY (termbase_id) REFERENCES termbases(id) ON DELETE CASCADE
                            );";
                        cmd.ExecuteNonQuery();

                        // --- Indexes ---
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_gt_source_term ON termbase_terms(source_term);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_gt_termbase_id ON termbase_terms(termbase_id);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_gt_project_id ON termbase_terms(project_id);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_gt_domain ON termbase_terms(domain);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_synonyms_term_id ON termbase_synonyms(term_id);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_synonyms_text ON termbase_synonyms(synonym_text);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_synonyms_language ON termbase_synonyms(language);";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_termbase_term_uuid ON termbase_terms(term_uuid);";
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }

                // FTS5 virtual table – may not be available in all builds
                try
                {
                    using (var fts = new SqliteCommand(@"
                        CREATE VIRTUAL TABLE IF NOT EXISTS termbase_terms_fts USING fts5(
                            source_term, target_term, definition, notes,
                            content='termbase_terms',
                            content_rowid='id'
                        );", conn))
                    {
                        fts.ExecuteNonQuery();
                    }
                }
                catch
                {
                    // FTS5 not available in this SQLite build – non-critical
                }
            }
        }

        /// <summary>
        /// Creates a new termbase in an existing database.
        /// </summary>
        /// <returns>The ID of the newly created termbase.</returns>
        public static long CreateTermbase(string dbPath, string name, string sourceLang, string targetLang)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                const string sql = @"
                    INSERT INTO termbases (name, source_lang, target_lang, is_global, read_only, ranking)
                    VALUES (@name, @srcLang, @tgtLang, 1, 0,
                            (SELECT COALESCE(MAX(ranking), 0) + 1 FROM termbases));
                    SELECT last_insert_rowid();";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@name", name.Trim());
                    cmd.Parameters.AddWithValue("@srcLang", sourceLang.Trim());
                    cmd.Parameters.AddWithValue("@tgtLang", targetLang.Trim());

                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt64(result) : -1;
                }
            }
        }

        /// <summary>
        /// Deletes a termbase and all its terms from the database.
        /// Synonyms are cascade-deleted via FK constraint on termbase_synonyms.
        /// </summary>
        public static void DeleteTermbase(string dbPath, long termbaseId)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                using (var pragma = new SqliteCommand("PRAGMA foreign_keys=ON;", conn))
                    pragma.ExecuteNonQuery();

                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand { Connection = conn, Transaction = tx })
                    {
                        // Delete terms (cascades to synonyms)
                        cmd.CommandText = "DELETE FROM termbase_terms WHERE CAST(termbase_id AS INTEGER) = @id;";
                        cmd.Parameters.AddWithValue("@id", termbaseId);
                        cmd.ExecuteNonQuery();

                        // Clean up activation tables
                        cmd.CommandText = "DELETE FROM termbase_activation WHERE termbase_id = @id;";
                        cmd.ExecuteNonQuery();

                        cmd.CommandText = "DELETE FROM termbase_project_activation WHERE termbase_id = @id;";
                        cmd.ExecuteNonQuery();

                        // Delete the termbase record itself
                        cmd.CommandText = "DELETE FROM termbases WHERE id = @id;";
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
            }
        }

        // ==================================================================
        //  TSV Import / Export
        // ==================================================================

        /// <summary>
        /// Imports terms from a TSV file into the specified termbase.
        /// Handles pipe-delimited synonyms, [!forbidden] markers, and UUID tracking.
        /// </summary>
        /// <returns>Number of terms imported/updated.</returns>
        public static int ImportTsv(string dbPath, long termbaseId, string tsvPath,
            string sourceLang, string targetLang, IProgress<int> progress = null,
            Dictionary<string, int> columnMap = null)
        {
            // Read all lines
            string[] lines;
            using (var sr = new StreamReader(tsvPath, new UTF8Encoding(true)))
            {
                var lineList = new List<string>();
                string line;
                while ((line = sr.ReadLine()) != null)
                    lineList.Add(line);
                lines = lineList.ToArray();
            }

            if (lines.Length < 2)
                throw new InvalidOperationException("TSV file must contain a header row and at least one data row.");

            // Parse headers. A mapping chosen by the user (#94) replaces the guess
            // from the header names; it has the same shape.
            var headers = lines[0].Split('\t');
            var colMap = columnMap != null
                ? new Dictionary<string, int>(columnMap)
                : MapTsvColumns(headers, sourceLang, targetLang);

            if (!colMap.ContainsKey("source") || !colMap.ContainsKey("target"))
                throw new InvalidOperationException(
                    "TSV file must contain at least Source and Target columns.\n" +
                    "Recognized header names: 'Source Term', 'Target Term', 'Source', 'Target', " +
                    "or the source/target language name.");

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            int count = 0;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                using (var pragma = new SqliteCommand("PRAGMA foreign_keys=ON;", conn))
                    pragma.ExecuteNonQuery();

                using (var tx = conn.BeginTransaction())
                {
                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;

                        var fields = lines[i].Split('\t');

                        // Unescape backslash-encoded newlines/tabs/backslashes
                        // that ExportTsv writes for fields that would
                        // otherwise break TSV's one-record-per-line shape.
                        // No-op on TSVs that don't contain any backslashes,
                        // so old exports and hand-built TSVs without escapes
                        // import unchanged.
                        var sourceCell = UnescapeTsvField(GetField(fields, colMap, "source"));
                        var targetCell = UnescapeTsvField(GetField(fields, colMap, "target"));
                        if (string.IsNullOrWhiteSpace(sourceCell) || string.IsNullOrWhiteSpace(targetCell))
                            continue;

                        // Parse pipe-delimited cells
                        var (srcMain, srcSynonyms) = ParsePipeDelimitedCell(sourceCell);
                        var (tgtMain, tgtSynonyms) = ParsePipeDelimitedCell(targetCell);
                        if (string.IsNullOrWhiteSpace(srcMain) || string.IsNullOrWhiteSpace(tgtMain))
                            continue;

                        // TSV cells can carry invisible whitespace (no-break
                        // spaces, zero-width chars) just like editor selections.
                        srcMain = SanitizeTermWhitespace(srcMain);
                        tgtMain = SanitizeTermWhitespace(tgtMain);

                        // Optional metadata
                        var uuid = UnescapeTsvField(GetField(fields, colMap, "uuid"));
                        var priority = ParseInt(GetField(fields, colMap, "priority"), 99);
                        var domain = UnescapeTsvField(GetField(fields, colMap, "domain") ?? "");
                        var definition = UnescapeTsvField(GetField(fields, colMap, "definition") ?? "");
                        var notes = UnescapeTsvField(GetField(fields, colMap, "notes") ?? "");
                        var project = UnescapeTsvField(GetField(fields, colMap, "project") ?? "");
                        var client = UnescapeTsvField(GetField(fields, colMap, "client") ?? "");
                        var forbidden = ParseBool(GetField(fields, colMap, "forbidden"));

                        // UUID: does this term already exist IN THE DESTINATION?
                        //
                        // Scoped to termbaseId deliberately. Unscoped, an export of
                        // termbase X imported into termbase Y matched X's rows and the
                        // UPDATE below - which keys on id and never sets termbase_id -
                        // rewrote them where they stood. Y stayed empty while X was
                        // silently overwritten, and the caller still reported success.
                        long termId = -1;
                        if (!string.IsNullOrWhiteSpace(uuid))
                        {
                            using (var qry = new SqliteCommand(
                                @"SELECT id FROM termbase_terms
                                  WHERE term_uuid = @uuid
                                    AND CAST(termbase_id AS INTEGER) = @tbId", conn, tx))
                            {
                                qry.Parameters.AddWithValue("@uuid", uuid);
                                qry.Parameters.AddWithValue("@tbId", termbaseId);
                                var existing = qry.ExecuteScalar();
                                if (existing != null)
                                    termId = Convert.ToInt64(existing);
                            }

                            // Not in the destination, but term_uuid is globally UNIQUE:
                            // if the identity belongs to a term elsewhere, this row is a
                            // new term here and needs a new one. Reusing it would break
                            // the index and abort the whole import.
                            if (termId <= 0)
                            {
                                using (var owned = new SqliteCommand(
                                    "SELECT 1 FROM termbase_terms WHERE term_uuid = @uuid LIMIT 1",
                                    conn, tx))
                                {
                                    owned.Parameters.AddWithValue("@uuid", uuid);
                                    if (owned.ExecuteScalar() != null)
                                        uuid = Guid.NewGuid().ToString();
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(uuid))
                            uuid = Guid.NewGuid().ToString();

                        if (termId > 0)
                        {
                            // UPDATE existing term
                            using (var upd = new SqliteCommand(@"
                                UPDATE termbase_terms SET
                                    source_term = @src, target_term = @tgt,
                                    source_lang = @srcLang, target_lang = @tgtLang,
                                    priority = @prio, domain = @domain, definition = @definition, notes = @notes,
                                    project = @project, client = @client, forbidden = @forbidden,
                                    modified_date = CURRENT_TIMESTAMP
                                WHERE id = @id", conn, tx))
                            {
                                upd.Parameters.AddWithValue("@src", srcMain);
                                upd.Parameters.AddWithValue("@tgt", tgtMain);
                                upd.Parameters.AddWithValue("@srcLang", sourceLang);
                                upd.Parameters.AddWithValue("@tgtLang", targetLang);
                                upd.Parameters.AddWithValue("@prio", priority);
                                upd.Parameters.AddWithValue("@domain", domain);
                                upd.Parameters.AddWithValue("@definition", definition);
                                upd.Parameters.AddWithValue("@notes", notes);
                                upd.Parameters.AddWithValue("@project", project);
                                upd.Parameters.AddWithValue("@client", client);
                                upd.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                                upd.Parameters.AddWithValue("@id", termId);
                                upd.ExecuteNonQuery();
                            }

                            // Delete old synonyms before re-inserting
                            using (var del = new SqliteCommand(
                                "DELETE FROM termbase_synonyms WHERE term_id = @id", conn, tx))
                            {
                                del.Parameters.AddWithValue("@id", termId);
                                del.ExecuteNonQuery();
                            }
                        }
                        else
                        {
                            // Check for duplicate by source+target (case-insensitive) – skip if exists
                            using (var dup = new SqliteCommand(@"
                                SELECT id FROM termbase_terms
                                WHERE CAST(termbase_id AS INTEGER) = @tbId
                                  AND LOWER(TRIM(source_term)) = LOWER(@src)
                                  AND LOWER(TRIM(target_term)) = LOWER(@tgt)
                                LIMIT 1", conn, tx))
                            {
                                dup.Parameters.AddWithValue("@tbId", termbaseId);
                                dup.Parameters.AddWithValue("@src", srcMain);
                                dup.Parameters.AddWithValue("@tgt", tgtMain);

                                if (dup.ExecuteScalar() != null)
                                    continue; // duplicate – skip
                            }

                            // INSERT new term
                            using (var ins = new SqliteCommand(@"
                                INSERT INTO termbase_terms
                                    (source_term, target_term, termbase_id, source_lang, target_lang,
                                     priority, domain, definition, notes, project, client, forbidden, case_sensitive, term_uuid)
                                VALUES
                                    (@src, @tgt, @tbId, @srcLang, @tgtLang,
                                     @prio, @domain, @definition, @notes, @project, @client, @forbidden, 0, @uuid);
                                SELECT last_insert_rowid();", conn, tx))
                            {
                                ins.Parameters.AddWithValue("@src", srcMain);
                                ins.Parameters.AddWithValue("@tgt", tgtMain);
                                ins.Parameters.AddWithValue("@tbId", termbaseId);
                                ins.Parameters.AddWithValue("@srcLang", sourceLang);
                                ins.Parameters.AddWithValue("@tgtLang", targetLang);
                                ins.Parameters.AddWithValue("@prio", priority);
                                ins.Parameters.AddWithValue("@domain", domain);
                                ins.Parameters.AddWithValue("@definition", definition);
                                ins.Parameters.AddWithValue("@notes", notes);
                                ins.Parameters.AddWithValue("@project", project);
                                ins.Parameters.AddWithValue("@client", client);
                                ins.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                                ins.Parameters.AddWithValue("@uuid", uuid);

                                var result = ins.ExecuteScalar();
                                termId = result != null ? Convert.ToInt64(result) : -1;
                            }
                        }

                        // Insert synonyms (source + target)
                        if (termId > 0)
                        {
                            InsertSynonyms(conn, tx, termId, "source", srcSynonyms);
                            InsertSynonyms(conn, tx, termId, "target", tgtSynonyms);
                        }

                        count++;

                        // Report progress every 50 rows to avoid UI overhead
                        if (progress != null && count % 50 == 0)
                            progress.Report(count);
                    }

                    tx.Commit();

                    // Final progress report
                    progress?.Report(count);
                }
            }

            return count;
        }

        /// <summary>
        /// Exports all terms from a termbase to a TSV file with full metadata.
        /// Uses UTF-8 BOM encoding and pipe-delimited synonym format.
        /// </summary>
        /// <returns>Number of terms exported.</returns>
        public static int ExportTsv(string dbPath, long termbaseId, string tsvPath)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            int count = 0;

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                // Load all terms
                var terms = new List<(long id, string source, string target, int priority,
                    string domain, string notes, string project, string client,
                    bool forbidden, string uuid)>();

                using (var cmd = new SqliteCommand(@"
                    SELECT id, source_term, target_term,
                           COALESCE(priority, 99), COALESCE(domain, ''),
                           COALESCE(notes, ''), COALESCE(project, ''),
                           COALESCE(client, ''), COALESCE(forbidden, 0),
                           COALESCE(term_uuid, '')
                    FROM termbase_terms
                    WHERE CAST(termbase_id AS INTEGER) = @tbId
                    ORDER BY source_term ASC", conn))
                {
                    cmd.Parameters.AddWithValue("@tbId", termbaseId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            terms.Add((
                                reader.GetInt64(0),
                                reader.IsDBNull(1) ? "" : reader.GetString(1),
                                reader.IsDBNull(2) ? "" : reader.GetString(2),
                                reader.GetInt32(3),
                                reader.GetString(4),
                                reader.GetString(5),
                                reader.GetString(6),
                                reader.GetString(7),
                                GetBool(reader, 8),
                                reader.GetString(9)
                            ));
                        }
                    }
                }

                // Bulk-load all synonyms for this termbase
                var synonyms = new Dictionary<long, List<(string text, string language, bool forbidden)>>();
                using (var cmd = new SqliteCommand(@"
                    SELECT s.term_id, s.synonym_text, s.language, s.forbidden
                    FROM termbase_synonyms s
                    INNER JOIN termbase_terms t ON s.term_id = t.id
                    WHERE CAST(t.termbase_id AS INTEGER) = @tbId
                    ORDER BY s.term_id, s.language, s.display_order ASC", conn))
                {
                    cmd.Parameters.AddWithValue("@tbId", termbaseId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var termId = reader.GetInt64(0);
                            var text = reader.GetString(1);
                            var lang = reader.GetString(2);
                            var forb = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;

                            if (!synonyms.ContainsKey(termId))
                                synonyms[termId] = new List<(string, string, bool)>();
                            synonyms[termId].Add((text, lang, forb));
                        }
                    }
                }

                // The termbase's own declared pair, for the headers below.
                string srcLangCode = "", tgtLangCode = "";
                using (var langCmd = new SqliteCommand(
                    "SELECT source_lang, target_lang FROM termbases WHERE CAST(id AS INTEGER) = @tbId", conn))
                {
                    langCmd.Parameters.AddWithValue("@tbId", termbaseId);
                    using (var langReader = langCmd.ExecuteReader())
                    {
                        if (langReader.Read())
                        {
                            srcLangCode = langReader.IsDBNull(0) ? "" : langReader.GetString(0);
                            tgtLangCode = langReader.IsDBNull(1) ? "" : langReader.GetString(1);
                        }
                    }
                }

                // Headers name the LANGUAGE of each column, the way memoQ and
                // MultiTerm write theirs. They used to be a fixed "Source"/"Target",
                // reasoned as language-neutral so the file could always be
                // reimported whatever the destination's pair was - but the importer
                // already falls back to file order when it cannot match a language,
                // so that cost the safety and bought nothing. With the language
                // named, importing into a termbase pointing the other way swaps the
                // columns to match instead of filing English terms as Dutch (#93).
                //
                // A termbase with no declared language keeps the old neutral header,
                // so nothing about such a file changes. LocaleToEnglishName falls
                // back to the raw code for a locale .NET does not know, which still
                // matches: MatchesLanguage compares codes as well as names.
                var srcHeader = LanguageUtils.LocaleToEnglishName(srcLangCode) ?? "Source";
                var tgtHeader = LanguageUtils.LocaleToEnglishName(tgtLangCode) ?? "Target";

                // Write TSV
                using (var sw = new StreamWriter(tsvPath, false, new UTF8Encoding(true)))
                {
                    sw.WriteLine($"Term UUID\t{srcHeader}\t{tgtHeader}\tPriority\tDomain\tNotes\tProject\tClient\tForbidden");

                    foreach (var term in terms)
                    {
                        // Build source cell with synonyms
                        var srcSyns = new List<(string text, bool forbidden)>();
                        var tgtSyns = new List<(string text, bool forbidden)>();
                        if (synonyms.TryGetValue(term.id, out var synList))
                        {
                            foreach (var s in synList)
                            {
                                if (s.language == "source")
                                    srcSyns.Add((s.text, s.forbidden));
                                else if (s.language == "target")
                                    tgtSyns.Add((s.text, s.forbidden));
                            }
                        }

                        var sourceCell = BuildPipeDelimitedCell(term.source, srcSyns);
                        var targetCell = BuildPipeDelimitedCell(term.target, tgtSyns);

                        // Escape newlines / tabs / backslashes so a notes
                        // field containing a multi-paragraph AI response
                        // (or any other multi-line content the user pasted
                        // in via the editor) doesn't break the
                        // one-record-per-line invariant TSV requires. The
                        // matching ImportTsv unescapes these sequences on
                        // the way back in, so the round-trip preserves the
                        // original formatting exactly.
                        sw.WriteLine(
                            $"{EscapeTsvField(term.uuid)}\t" +
                            $"{EscapeTsvField(sourceCell)}\t" +
                            $"{EscapeTsvField(targetCell)}\t" +
                            $"{term.priority}\t" +
                            $"{EscapeTsvField(term.domain)}\t" +
                            $"{EscapeTsvField(term.notes)}\t" +
                            $"{EscapeTsvField(term.project)}\t" +
                            $"{EscapeTsvField(term.client)}\t" +
                            $"{(term.forbidden ? "TRUE" : "FALSE")}");

                        count++;
                    }
                }
            }

            return count;
        }

        // ==================================================================
        //  TSV helpers
        // ==================================================================

        /// <summary>
        /// Escapes characters that would break TSV's one-record-per-line
        /// shape (or its tab-delimited columns) into backslash-prefixed
        /// sequences. Backslash itself is escaped first so the unescape
        /// pass can reliably tell a real backslash from an escape lead-in.
        ///
        /// The output stays human-readable (notes look like ``line one\nline two``
        /// in the file, not Base64 or quoted) and is symmetric with
        /// <see cref="UnescapeTsvField"/>. Excel and other TSV viewers
        /// won't unescape the sequences, but they also won't mis-split the
        /// row across multiple lines, which is the more important property.
        /// </summary>
        private static string EscapeTsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            // Backslash MUST be escaped first so subsequent replacements
            // don't double-escape the leading backslash they introduce.
            return value
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        /// <summary>
        /// Inverse of <see cref="EscapeTsvField"/>. Walks the string and
        /// expands backslash escape sequences back to their real
        /// characters. Unknown sequences (e.g. <c>\q</c>) are left as-is
        /// so a hand-edited file with a stray backslash isn't silently
        /// mangled. A trailing lone backslash is also passed through.
        /// </summary>
        private static string UnescapeTsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? "";
            if (value.IndexOf('\\') < 0) return value;
            var sb = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    var next = value[i + 1];
                    switch (next)
                    {
                        case 'n': sb.Append('\n'); i++; break;
                        case 'r': sb.Append('\r'); i++; break;
                        case 't': sb.Append('\t'); i++; break;
                        case '\\': sb.Append('\\'); i++; break;
                        default: sb.Append(c); break;  // unknown escape – preserve
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses a pipe-delimited cell: "main|syn1|[!forbidden_syn]"
        /// Returns the main term and a list of synonyms with forbidden flags.
        /// </summary>
        private static (string mainTerm, List<(string text, bool forbidden)> synonyms) ParsePipeDelimitedCell(string cell)
        {
            var empty = new List<(string, bool)>();
            if (string.IsNullOrWhiteSpace(cell))
                return ("", empty);

            var parts = SplitOnUnescapedPipes(cell);
            var mainTerm = UnescapePipeSegment(parts[0].Trim());
            var synonyms = new List<(string text, bool forbidden)>();

            for (int i = 1; i < parts.Count; i++)
            {
                var part = parts[i].Trim();
                if (string.IsNullOrEmpty(part)) continue;

                if (part.StartsWith("[!") && part.EndsWith("]") && part.Length > 3)
                {
                    synonyms.Add((UnescapePipeSegment(part.Substring(2, part.Length - 3).Trim()), true));
                }
                else
                {
                    synonyms.Add((UnescapePipeSegment(part), false));
                }
            }

            return (mainTerm, synonyms);
        }

        /// <summary>
        /// Splits a cell on pipes that are NOT preceded by an escaping backslash.
        /// A term may legitimately contain a pipe – "DC| mode" and "CV MANUAL| mode"
        /// are real entries in a real termbase – and before this the delimiter and
        /// the character were indistinguishable, so such a term came back from a
        /// round trip split into a phantom synonym (issue #61).
        /// </summary>
        private static List<string> SplitOnUnescapedPipes(string cell)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < cell.Length; i++)
            {
                var c = cell[i];
                if (c == '\\' && i + 1 < cell.Length)
                {
                    // Keep the escape sequence intact for UnescapePipeSegment;
                    // consuming both characters here is what stops an escaped
                    // pipe from being seen as a delimiter.
                    sb.Append(c).Append(cell[i + 1]);
                    i++;
                    continue;
                }
                if (c == '|') { parts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>Reverses <see cref="EscapePipeSegment"/>: <c>\\</c> → <c>\</c>,
        /// <c>\|</c> → <c>|</c>, <c>\[</c> → <c>[</c>.</summary>
        private static string UnescapePipeSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment.IndexOf('\\') < 0) return segment ?? "";
            var sb = new StringBuilder(segment.Length);
            for (int i = 0; i < segment.Length; i++)
            {
                if (segment[i] == '\\' && i + 1 < segment.Length &&
                    (segment[i + 1] == '|' || segment[i + 1] == '\\' || segment[i + 1] == '['))
                {
                    sb.Append(segment[i + 1]);
                    i++;
                    continue;
                }
                sb.Append(segment[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a term's own backslashes and pipes so the pipe layer can tell
        /// them from its delimiter. Backslash first, so the escape it introduces
        /// isn't escaped again by the pipe pass.
        ///
        /// This nests inside the TSV field escaping applied afterwards, which
        /// doubles backslashes again; the import unwinds them in the mirror order
        /// (<c>UnescapeTsvField</c> then <c>UnescapePipeSegment</c>), so the two
        /// layers compose.
        /// </summary>
        private static string EscapePipeSegment(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (text.IndexOf('\\') < 0 && text.IndexOf('|') < 0) return text;
            return text.Replace("\\", "\\\\").Replace("|", "\\|");
        }

        /// <summary>
        /// Disambiguates a synonym that looks like the forbidden-synonym marker.
        /// "[!x]" as a synonym's own text is indistinguishable from the wrapper
        /// the writer puts around a forbidden one, so it would come back as a
        /// forbidden synonym "x" with its brackets eaten (issue #61, the same
        /// round-trip fault as the unescaped pipe).
        ///
        /// Only a segment that both starts "[!" and ends "]" is ambiguous, so
        /// only that one gets a backslash. Escaping every bracket would litter
        /// ordinary terms like "[abc]" for nothing.
        ///
        /// Applied to synonyms only: ParsePipeDelimitedCell tests the marker on
        /// parts[1..], never on the main term, so parts[0] stays bare.
        /// </summary>
        private static string EscapeForbiddenMarker(string escapedSegment)
        {
            if (string.IsNullOrEmpty(escapedSegment)) return escapedSegment ?? "";
            var s = escapedSegment.Trim();
            if (!(s.StartsWith("[!") && s.EndsWith("]") && s.Length > 3))
                return escapedSegment;

            // Against the first non-space character, not the front of the string:
            // the parser trims before testing, and a backslash followed by a space
            // is not an escape sequence, so a leading-space synonym would keep the
            // backslash as literal text. Such synonyms are real - the phantom ones
            // in issue #61 read " mode".
            int lead = 0;
            while (lead < escapedSegment.Length && char.IsWhiteSpace(escapedSegment[lead]))
                lead++;
            return escapedSegment.Substring(0, lead) + "\\" + escapedSegment.Substring(lead);
        }

        /// <summary>
        /// Builds a pipe-delimited cell: "main|syn1|[!forbidden_syn]"
        /// </summary>
        private static string BuildPipeDelimitedCell(string mainTerm, List<(string text, bool forbidden)> synonyms)
        {
            // Escape even with no synonyms: a lone term containing a pipe would
            // otherwise be split on the way back in (issue #61).
            if (synonyms == null || synonyms.Count == 0)
                return EscapePipeSegment(mainTerm);

            var sb = new StringBuilder(EscapePipeSegment(mainTerm));
            foreach (var (text, forbidden) in synonyms)
            {
                sb.Append('|');
                var escaped = EscapePipeSegment(text);
                // The forbidden branch writes the marker, so its own brackets stay
                // bare; only a plain synonym that merely LOOKS like one is escaped.
                sb.Append(forbidden ? $"[!{escaped}]" : EscapeForbiddenMarker(escaped));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps TSV header names to standardized column keys (case-insensitive).
        /// </summary>
        /// <summary>
        /// How the source and target columns of a TSV were identified. Reported so
        /// the caller can tell the user what is about to happen: a file pointing the
        /// other way is handled correctly, but silently doing so is how a reversed
        /// termbase goes unnoticed (#93).
        /// </summary>
        internal enum TsvColumnOrigin
        {
            /// <summary>No source/target columns found at all.</summary>
            Unresolved,
            /// <summary>Headers said "Source"/"Target" - no language information, file order used.</summary>
            NamedColumns,
            /// <summary>Language headers matched the destination in the file's own order.</summary>
            Aligned,
            /// <summary>Language headers matched the destination reversed - the columns are swapped.</summary>
            Swapped,
            /// <summary>Languages could not be matched; the first two language-looking columns were used in file order.</summary>
            Positional,
            /// <summary>Both sides of the termbase are the same base language, so which column is which cannot be told apart.</summary>
            Ambiguous,
            /// <summary>Both header languages were read, and they are not this termbase's pair. The file does not belong here.</summary>
            Mismatch
        }

        private static Dictionary<string, int> MapTsvColumns(string[] headers, string sourceLang, string targetLang)
        {
            return MapTsvColumns(headers, sourceLang, targetLang, out _);
        }

        private static Dictionary<string, int> MapTsvColumns(string[] headers, string sourceLang,
            string targetLang, out TsvColumnOrigin origin)
        {
            var map = new Dictionary<string, int>();

            // Columns that are neither metadata nor an explicit Source/Target, in the
            // order they appear. Collected rather than assigned on sight: which column
            // is the source depends on what the OTHER one turns out to be, so nothing
            // can be decided until both have been read (#93).
            var languageCols = new List<int>();

            for (int i = 0; i < headers.Length; i++)
            {
                var h = headers[i].Trim().ToLowerInvariant();

                if (h == "term uuid" || h == "uuid" || h == "term id" || h == "id" || h == "term_uuid" || h == "termid")
                    map["uuid"] = i;
                else if (h == "source term" || h == "source" || h == "src" || h == "term (source)" || h == "source language")
                    map["source"] = i;
                else if (h == "target term" || h == "target" || h == "tgt" || h == "term (target)" || h == "target language")
                    map["target"] = i;
                else if (h == "priority" || h == "prio" || h == "rank")
                    map["priority"] = i;
                else if (h == "domain" || h == "subject" || h == "field" || h == "category")
                    map["domain"] = i;
                else if (h == "definition" || h == "def")
                    map["definition"] = i;   // its own column since #94; used to be folded into notes
                else if (h == "notes" || h == "note" || h == "comment" || h == "comments" || h == "description")
                    map["notes"] = i;
                else if (h == "project" || h == "proj")
                    map["project"] = i;
                else if (h == "client" || h == "customer")
                    map["client"] = i;
                else if (h == "forbidden" || h == "do not use" || h == "prohibited" || h == "banned")
                    map["forbidden"] = i;
                else if (ResolveHeaderLanguage(h) != null || IsKnownLanguage(h)
                         || MatchesLanguage(h, sourceLang) || MatchesLanguage(h, targetLang))
                    languageCols.Add(i);
            }

            // Both columns already named outright - no language information to weigh.
            if (map.ContainsKey("source") && map.ContainsKey("target"))
            {
                origin = TsvColumnOrigin.NamedColumns;
                return map;
            }

            // Exactly one named: fill its partner from the first language column that
            // is not already spoken for.
            if (map.ContainsKey("source") || map.ContainsKey("target"))
            {
                int taken = map.ContainsKey("source") ? map["source"] : map["target"];
                foreach (var col in languageCols)
                {
                    if (col == taken) continue;
                    if (!map.ContainsKey("source")) map["source"] = col;
                    else map["target"] = col;
                    break;
                }
                origin = map.ContainsKey("source") && map.ContainsKey("target")
                    ? TsvColumnOrigin.NamedColumns
                    : TsvColumnOrigin.Unresolved;
                return map;
            }

            if (languageCols.Count < 2)
            {
                origin = TsvColumnOrigin.Unresolved;
                return map;
            }

            // Judge the two as a PAIR. Assigning whichever matched first is what put
            // Dutch terms into a German termbase: "English" matched that termbase's
            // target, "Dutch" matched nothing, and the leftover slot made Dutch the
            // source. A column's role is only knowable once both are read.
            int a = languageCols[0], b = languageCols[1];
            var langA = ResolveHeaderLanguage(headers[a].Trim());
            var langB = ResolveHeaderLanguage(headers[b].Trim());
            var destSrc = ResolveHeaderLanguage(sourceLang);
            var destTgt = ResolveHeaderLanguage(targetLang);

            bool bothKnown = langA != null && langB != null && destSrc != null && destTgt != null;
            bool sameSides = bothKnown && string.Equals(destSrc, destTgt, StringComparison.OrdinalIgnoreCase);

            // Default placement is file order; only a confident match reorders it.
            map["source"] = a;
            map["target"] = b;

            if (!bothKnown)
                origin = TsvColumnOrigin.Positional;
            else if (!sameSides
                     && string.Equals(langA, destSrc, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(langB, destTgt, StringComparison.OrdinalIgnoreCase))
                origin = TsvColumnOrigin.Aligned;
            else if (!sameSides
                     && string.Equals(langA, destTgt, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(langB, destSrc, StringComparison.OrdinalIgnoreCase))
            {
                // The file points the other way. Swap, so each term still lands on
                // the side that speaks its language.
                map["source"] = b;
                map["target"] = a;
                origin = TsvColumnOrigin.Swapped;
            }
            else if (sameSides
                     && string.Equals(langA, destSrc, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(langB, destTgt, StringComparison.OrdinalIgnoreCase))
                // en-US to en-GB: both columns are the same base language as both
                // sides, so a match carries no information and direction must not be
                // claimed from it.
                origin = TsvColumnOrigin.Ambiguous;
            else
                // Both languages read, and they are not this termbase's pair. Saying
                // "the columns will be read in order" here would be technically true
                // and useless: the real news is that the file belongs somewhere else.
                origin = TsvColumnOrigin.Mismatch;

            return map;
        }

        /// <summary>
        /// Resolves a column header or a stored locale to a two-letter language code:
        /// "English" and "English (United States)" and "en-US" all to "en". Null when
        /// it is not a language at all, which is the signal that a direction cannot
        /// be judged rather than an invitation to guess one.
        /// </summary>
        private static string ResolveHeaderLanguage(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            var text = header.Trim();

            // A locale code, with or without a region.
            var baseCode = text.Split('-', '_')[0];
            if (baseCode.Length == 2 || baseCode.Length == 3)
            {
                try
                {
                    var culture = System.Globalization.CultureInfo.GetCultureInfo(baseCode);
                    var two = culture.TwoLetterISOLanguageName;
                    if (!string.IsNullOrEmpty(two) && two != "iv")
                        return two.ToLowerInvariant();
                }
                catch { /* not a locale - try it as a name below */ }
            }

            if (LanguageNameToCode.Value.TryGetValue(StripParenthesisedRegion(text), out var byName))
                return byName;
            return LanguageNameToCode.Value.TryGetValue(text, out var exact) ? exact : null;
        }

        /// <summary>
        /// Language name to two-letter code, built once from every culture .NET knows
        /// plus the Dutch exonyms that appear in Workbench exports.
        /// </summary>
        private static readonly Lazy<Dictionary<string, string>> LanguageNameToCode =
            new Lazy<Dictionary<string, string>>(() =>
            {
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var culture in System.Globalization.CultureInfo.GetCultures(
                                 System.Globalization.CultureTypes.NeutralCultures))
                    {
                        var two = culture.TwoLetterISOLanguageName;
                        if (string.IsNullOrEmpty(two) || two == "iv") continue;
                        foreach (var name in new[] { culture.EnglishName, culture.NativeName })
                        {
                            if (string.IsNullOrEmpty(name)) continue;
                            var key = StripParenthesisedRegion(name);
                            if (!names.ContainsKey(key)) names[key] = two.ToLowerInvariant();
                        }
                    }
                }
                catch { }

                foreach (var pair in new[]
                {
                    new[] { "nederlands", "nl" }, new[] { "engels", "en" }, new[] { "duits", "de" },
                    new[] { "frans", "fr" }, new[] { "spaans", "es" }, new[] { "italiaans", "it" },
                    new[] { "portugees", "pt" }
                })
                {
                    if (!names.ContainsKey(pair[0])) names[pair[0]] = pair[1];
                }
                return names;
            });

        /// <summary>What a TSV's header row says about the languages in it.</summary>
        internal sealed class TsvHeaderInfo
        {
            /// <summary>The header text over the column that will be read as source, verbatim.</summary>
            public string SourceHeader;
            /// <summary>The header text over the column that will be read as target, verbatim.</summary>
            public string TargetHeader;
            public TsvColumnOrigin Origin;
        }

        /// <summary>
        /// What the column-mapping dialog needs (#94): the headers, a few data rows
        /// to show beside them, the mapping the importer would guess and how it got
        /// there, and the data row count. One read of the file.
        /// </summary>
        internal sealed class TsvPreview
        {
            public string[] Headers = new string[0];
            public List<string[]> SampleRows = new List<string[]>();
            public Dictionary<string, int> SuggestedMap = new Dictionary<string, int>();
            public TsvColumnOrigin Origin = TsvColumnOrigin.Unresolved;
            public int DataRowCount;
            /// <summary>The header over the suggested source / target column, verbatim, or null.</summary>
            public string SourceHeader, TargetHeader;
        }

        /// <summary>Never throws: an unreadable file yields an empty preview and the
        /// import itself produces the real error.</summary>
        internal static TsvPreview InspectTsv(string tsvPath, string sourceLang, string targetLang, int sampleRows = 3)
        {
            var preview = new TsvPreview();
            try
            {
                using (var sr = new StreamReader(tsvPath, new UTF8Encoding(true)))
                {
                    var first = sr.ReadLine();
                    if (string.IsNullOrEmpty(first)) return preview;
                    preview.Headers = System.Linq.Enumerable.ToArray(
                        System.Linq.Enumerable.Select(first.Split('\t'), h => h.Trim()));

                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        preview.DataRowCount++;
                        if (preview.SampleRows.Count < sampleRows)
                            preview.SampleRows.Add(System.Linq.Enumerable.ToArray(
                                System.Linq.Enumerable.Select(line.Split('\t'), f => UnescapeTsvField(f.Trim()))));
                    }
                }

                TsvColumnOrigin origin;
                preview.SuggestedMap = MapTsvColumns(preview.Headers, sourceLang, targetLang, out origin);
                preview.Origin = origin;
                if (preview.SuggestedMap.TryGetValue("source", out var si) && si < preview.Headers.Length)
                    preview.SourceHeader = preview.Headers[si];
                if (preview.SuggestedMap.TryGetValue("target", out var ti) && ti < preview.Headers.Length)
                    preview.TargetHeader = preview.Headers[ti];
            }
            catch { }
            return preview;
        }

        /// <summary>
        /// Reads only the header row and reports how its columns will be matched
        /// against a destination termbase, so the user can be told - and can stop -
        /// before anything is written. Never throws: an unreadable or headerless
        /// file reports Unresolved and the import itself produces the real error.
        /// </summary>
        internal static TsvHeaderInfo InspectTsvHeader(string tsvPath, string sourceLang, string targetLang)
        {
            var info = new TsvHeaderInfo { Origin = TsvColumnOrigin.Unresolved };
            try
            {
                string first;
                using (var sr = new StreamReader(tsvPath, new UTF8Encoding(true)))
                    first = sr.ReadLine();
                if (string.IsNullOrEmpty(first)) return info;

                var headers = first.Split('\t');
                TsvColumnOrigin origin;
                var map = MapTsvColumns(headers, sourceLang, targetLang, out origin);
                info.Origin = origin;
                if (map.TryGetValue("source", out var si) && si < headers.Length)
                    info.SourceHeader = headers[si].Trim();
                if (map.TryGetValue("target", out var ti) && ti < headers.Length)
                    info.TargetHeader = headers[ti].Trim();
            }
            catch { }
            return info;
        }

        /// <summary>
        /// True when two locale codes name the same base language, regardless of
        /// region: "en" and "en-GB" yes, "en-GB" and "nl-BE" no. Used to recognise
        /// a termbase whose direction cannot be derived from language names at all.
        /// </summary>
        private static bool SameBaseLanguage(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            var baseA = a.Trim().Split('-', '_')[0];
            var baseB = b.Trim().Split('-', '_')[0];
            return string.Equals(baseA, baseB, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesLanguage(string header, string langCode)
        {
            if (string.IsNullOrEmpty(langCode)) return false;
            var lc = langCode.ToLowerInvariant();

            // Direct match: header is the code itself (e.g., "en-us")
            if (header == lc) return true;

            // Code starts with header (e.g., header "en", code "en-us")
            if (lc.StartsWith(header)) return true;

            // Header starts with the code's language part (e.g., header "english", code "en")
            if (header.StartsWith(lc.Split('-')[0])) return true;

            // Strip parenthesised region from header for matching – handles
            // export headers like "Dutch (BE)" or "English (US)" produced by
            // LanguageUtils.ShortenLanguageName().
            var baseName = StripParenthesisedRegion(header);

            // Try resolving the display name to a culture code via .NET
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultures(
                    System.Globalization.CultureTypes.AllCultures);
                foreach (var ci in culture)
                {
                    if (string.Equals(ci.EnglishName, baseName, System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ci.NativeName, baseName, System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ci.TwoLetterISOLanguageName, baseName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        // Check if this culture matches the target language code
                        var ciCode = ci.Name.ToLowerInvariant();
                        if (ciCode == lc || lc.StartsWith(ciCode) || ciCode.StartsWith(lc.Split('-')[0]))
                            return true;
                    }
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Strips a parenthesised suffix from a header, e.g. "Dutch (BE)" → "dutch".
        /// Returns the input unchanged if no parenthesis is found.
        /// </summary>
        private static string StripParenthesisedRegion(string header)
        {
            var parenIdx = header.IndexOf('(');
            return parenIdx > 0 ? header.Substring(0, parenIdx).Trim() : header;
        }

        private static readonly HashSet<string> KnownLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dutch", "english", "german", "french", "spanish", "italian", "portuguese",
            "russian", "chinese", "japanese", "korean", "arabic", "hebrew", "turkish",
            "polish", "czech", "hungarian", "romanian", "bulgarian", "swedish", "danish",
            "norwegian", "finnish", "greek", "thai", "vietnamese", "indonesian", "malay",
            "hindi", "bengali", "ukrainian", "croatian", "serbian", "slovak", "slovenian",
            "estonian", "latvian", "lithuanian", "catalan", "basque", "galician",
            "nederlands", "engels", "duits", "frans", "spaans", "italiaans", "portugees"
        };

        /// <summary>
        /// Checks if a header looks like a language name. Handles parenthesised
        /// region suffixes (e.g., "Dutch (BE)") by stripping them before lookup.
        /// </summary>
        /// <summary>
        /// Every language name .NET knows, region stripped, built once.
        /// KnownLanguages above stays as a supplement because it carries Dutch
        /// exonyms ("engels", "duits") that CultureInfo's English names do not.
        /// Without this the hardcoded list decided what counted as a language
        /// column, so a file headed "Icelandic"/"Welsh" matched neither the
        /// destination nor the positional fallback and the import threw (#93).
        /// </summary>
        private static readonly Lazy<HashSet<string>> CultureLanguageNames =
            new Lazy<HashSet<string>>(() =>
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var ci in System.Globalization.CultureInfo.GetCultures(
                                 System.Globalization.CultureTypes.NeutralCultures))
                    {
                        if (!string.IsNullOrEmpty(ci.EnglishName))
                            set.Add(StripParenthesisedRegion(ci.EnglishName));
                        if (!string.IsNullOrEmpty(ci.NativeName))
                            set.Add(StripParenthesisedRegion(ci.NativeName));
                    }
                }
                catch { }
                return set;
            });

        private static bool IsKnownLanguage(string header)
        {
            if (KnownLanguages.Contains(header) || CultureLanguageNames.Value.Contains(header))
                return true;
            var baseName = StripParenthesisedRegion(header);
            return baseName != header
                && (KnownLanguages.Contains(baseName) || CultureLanguageNames.Value.Contains(baseName));
        }

        private static string GetField(string[] fields, Dictionary<string, int> colMap, string key)
        {
            if (!colMap.TryGetValue(key, out var idx) || idx >= fields.Length) return null;
            var val = fields[idx].Trim();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        private static int ParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            if (int.TryParse(value, out var result))
                return Math.Max(1, Math.Min(99, result));
            return defaultValue;
        }

        private static bool ParseBool(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim().ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes" || v == "y" || v == "forbidden" || v == "prohibited";
        }

        private static void InsertSynonyms(SqliteConnection conn, SqliteTransaction tx,
            long termId, string language, List<(string text, bool forbidden)> synonyms)
        {
            if (synonyms == null || synonyms.Count == 0) return;

            for (int i = 0; i < synonyms.Count; i++)
            {
                using (var cmd = new SqliteCommand(@"
                    INSERT INTO termbase_synonyms (term_id, synonym_text, language, display_order, forbidden)
                    VALUES (@termId, @text, @lang, @order, @forbidden)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@termId", termId);
                    cmd.Parameters.AddWithValue("@text", NormalizeTermForSave(synonyms[i].text));
                    cmd.Parameters.AddWithValue("@lang", language);
                    cmd.Parameters.AddWithValue("@order", i);
                    cmd.Parameters.AddWithValue("@forbidden", synonyms[i].forbidden ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ==================================================================
        //  Synonym management (static, short-lived connections)
        // ==================================================================

        /// <summary>
        /// Loads a single term entry by its database row ID.
        /// Used to open the Term Entry Editor after a merge operation.
        /// </summary>
        public static TermEntry GetTermById(string dbPath, long termId)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                bool hasDomain = HasColumn(conn, "termbase_terms", "domain");
                bool hasNotes = HasColumn(conn, "termbase_terms", "notes");
                bool hasNt = HasColumn(conn, "termbase_terms", "is_nontranslatable");
                bool hasClient = HasColumn(conn, "termbase_terms", "client");
                bool hasForbidden = HasColumn(conn, "termbase_terms", "forbidden");

                var cols = "id, source_term, target_term, source_lang, target_lang, termbase_id, definition";
                if (hasDomain) cols += ", domain";
                if (hasNotes) cols += ", notes";
                if (hasNt) cols += ", is_nontranslatable";
                if (hasClient) cols += ", client";
                if (hasForbidden) cols += ", forbidden";

                var sql = $"SELECT {cols} FROM termbase_terms WHERE id = @id";
                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", termId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;

                        int col = 0;
                        var entry = new TermEntry
                        {
                            Id = reader.GetInt64(col++),
                            SourceTerm = reader.IsDBNull(col) ? "" : reader.GetString(col), // 1
                            TargetTerm = reader.IsDBNull(col + 1) ? "" : reader.GetString(col + 1), // 2
                            SourceLang = reader.IsDBNull(col + 2) ? "" : reader.GetString(col + 2), // 3
                            TargetLang = reader.IsDBNull(col + 3) ? "" : reader.GetString(col + 3), // 4
                            TermbaseId = reader.IsDBNull(col + 4) ? 0 : reader.GetInt64(col + 4), // 5
                            Definition = reader.IsDBNull(col + 5) ? "" : reader.GetString(col + 5)  // 6
                        };
                        col += 6;

                        if (hasDomain)
                        {
                            entry.Domain = reader.IsDBNull(col) ? "" : reader.GetString(col);
                            col++;
                        }
                        if (hasNotes)
                        {
                            entry.Notes = reader.IsDBNull(col) ? "" : reader.GetString(col);
                            col++;
                        }
                        if (hasNt)
                        {
                            entry.IsNonTranslatable = !reader.IsDBNull(col) && GetBool(reader, col);
                            col++;
                        }
                        if (hasClient)
                        {
                            entry.Client = reader.IsDBNull(col) ? "" : reader.GetString(col);
                            col++;
                        }
                        if (hasForbidden)
                        {
                            entry.Forbidden = !reader.IsDBNull(col) && GetBool(reader, col);
                            col++;
                        }

                        return entry;
                    }
                }
            }
        }

        /// <summary>
        /// Loads all synonyms (source and target) for a single term.
        /// Used by the Term Entry Editor dialog.
        /// </summary>
        public static List<SynonymEntry> GetSynonyms(string dbPath, long termId)
        {
            var results = new List<SynonymEntry>();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                // Check if the table exists (older databases might not have it)
                if (!HasTable(conn, "termbase_synonyms"))
                    return results;

                const string sql = @"
                    SELECT id, synonym_text, language, display_order, forbidden
                    FROM termbase_synonyms
                    WHERE term_id = @termId
                    ORDER BY language ASC, display_order ASC";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@termId", termId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new SynonymEntry
                            {
                                Id = reader.GetInt64(0),
                                Text = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Language = reader.IsDBNull(2) ? "target" : reader.GetString(2),
                                DisplayOrder = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                                Forbidden = !reader.IsDBNull(4) && GetBool(reader, 4)
                            });
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Appends a single synonym to an existing term entry.
        /// Unlike SaveSynonyms (which deletes all and reinserts), this only adds one row.
        /// </summary>
        public static void AddSynonym(string dbPath, long termId, string text, string language)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (language != "source" && language != "target")
                throw new ArgumentException("Language must be 'source' or 'target'.", nameof(language));

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                // Get the next display_order for this term + language
                int nextOrder = 0;
                using (var maxCmd = new SqliteCommand(@"
                    SELECT COALESCE(MAX(display_order), -1) + 1
                    FROM termbase_synonyms
                    WHERE term_id = @termId AND language = @lang", conn))
                {
                    maxCmd.Parameters.AddWithValue("@termId", termId);
                    maxCmd.Parameters.AddWithValue("@lang", language);
                    var result = maxCmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        nextOrder = Convert.ToInt32(result);
                }

                // Check if synonym already exists (case-insensitive)
                using (var checkCmd = new SqliteCommand(@"
                    SELECT COUNT(*) FROM termbase_synonyms
                    WHERE term_id = @termId
                      AND language = @lang
                      AND LOWER(TRIM(synonym_text)) = LOWER(@text)", conn))
                {
                    checkCmd.Parameters.AddWithValue("@termId", termId);
                    checkCmd.Parameters.AddWithValue("@lang", language);
                    checkCmd.Parameters.AddWithValue("@text", NormalizeTermForSave(text));
                    var count = Convert.ToInt64(checkCmd.ExecuteScalar());
                    if (count > 0) return; // Already exists – skip silently
                }

                using (var cmd = new SqliteCommand(@"
                    INSERT INTO termbase_synonyms
                        (term_id, synonym_text, language, display_order, forbidden)
                    VALUES (@termId, @text, @lang, @order, 0)", conn))
                {
                    cmd.Parameters.AddWithValue("@termId", termId);
                    cmd.Parameters.AddWithValue("@text", NormalizeTermForSave(text));
                    cmd.Parameters.AddWithValue("@lang", language);
                    cmd.Parameters.AddWithValue("@order", nextOrder);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Saves all synonyms for a term using delete-all-then-reinsert pattern.
        /// Matches the desktop Supervertaler's save_synonyms() approach.
        /// </summary>
        public static void SaveSynonyms(string dbPath, long termId, List<SynonymEntry> synonyms)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                using (var tx = conn.BeginTransaction())
                {
                    // Delete all existing synonyms for this term
                    using (var delCmd = new SqliteCommand(
                        "DELETE FROM termbase_synonyms WHERE term_id = @termId", conn, tx))
                    {
                        delCmd.Parameters.AddWithValue("@termId", termId);
                        delCmd.ExecuteNonQuery();
                    }

                    // Re-insert with updated order
                    if (synonyms != null)
                    {
                        for (int i = 0; i < synonyms.Count; i++)
                        {
                            var syn = synonyms[i];
                            if (string.IsNullOrWhiteSpace(syn.Text)) continue;

                            using (var cmd = new SqliteCommand(@"
                                INSERT INTO termbase_synonyms
                                    (term_id, synonym_text, language, display_order, forbidden)
                                VALUES (@termId, @text, @lang, @order, @forbidden)", conn, tx))
                            {
                                cmd.Parameters.AddWithValue("@termId", termId);
                                cmd.Parameters.AddWithValue("@text", NormalizeTermForSave(syn.Text));
                                cmd.Parameters.AddWithValue("@lang", syn.Language ?? "target");
                                cmd.Parameters.AddWithValue("@order", i);
                                cmd.Parameters.AddWithValue("@forbidden", syn.Forbidden ? 1 : 0);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    tx.Commit();
                }
            }
        }

        /// <summary>
        /// Returns synonym counts per term for a given termbase. Used by the Termbase Editor
        /// to show a "3 syn." column without loading every synonym text.
        /// </summary>
        public static Dictionary<long, int> GetSynonymCounts(string dbPath, long termbaseId)
        {
            var result = new Dictionary<long, int>();

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                if (!HasTable(conn, "termbase_synonyms"))
                    return result;

                const string sql = @"
                    SELECT s.term_id, COUNT(*) FROM termbase_synonyms s
                    JOIN termbase_terms t ON t.id = s.term_id
                    WHERE CAST(t.termbase_id AS INTEGER) = @tbId
                    GROUP BY s.term_id";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@tbId", termbaseId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result[reader.GetInt64(0)] = reader.GetInt32(1);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Merges multiple terms (same source term) into a single entry with synonyms.
        /// The primary term keeps its ID; target terms from others become target synonyms.
        /// Existing synonyms from merged entries are preserved and appended.
        /// </summary>
        public static void MergeTerms(string dbPath, long primaryTermId, List<long> mergeTermIds)
        {
            if (mergeTermIds == null || mergeTermIds.Count == 0) return;

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                MigrateSchema(conn);

                using (var pragma = new SqliteCommand("PRAGMA foreign_keys=ON;", conn))
                    pragma.ExecuteNonQuery();

                using (var tx = conn.BeginTransaction())
                {
                    // Get the current max display_order for the primary term's target synonyms
                    int maxOrder = 0;
                    using (var cmd = new SqliteCommand(@"
                        SELECT COALESCE(MAX(display_order), -1) FROM termbase_synonyms
                        WHERE term_id = @id AND language = 'target'", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", primaryTermId);
                        var val = cmd.ExecuteScalar();
                        if (val != null && val != DBNull.Value)
                            maxOrder = Convert.ToInt32(val) + 1;
                    }

                    // Get primary term's target_term for deduplication
                    string primaryTarget = "";
                    using (var cmd = new SqliteCommand(
                        "SELECT target_term FROM termbase_terms WHERE id = @id", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", primaryTermId);
                        var val = cmd.ExecuteScalar();
                        if (val != null && val != DBNull.Value)
                            primaryTarget = val.ToString();
                    }

                    // Collect existing synonym texts for deduplication
                    var existingTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    existingTexts.Add(primaryTarget);
                    using (var cmd = new SqliteCommand(@"
                        SELECT synonym_text FROM termbase_synonyms
                        WHERE term_id = @id AND language = 'target'", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", primaryTermId);
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                existingTexts.Add(reader.GetString(0));
                        }
                    }

                    foreach (var mergeId in mergeTermIds)
                    {
                        if (mergeId == primaryTermId) continue;

                        // Get this term's target_term → add as synonym if not duplicate
                        using (var cmd = new SqliteCommand(
                            "SELECT target_term FROM termbase_terms WHERE id = @id", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", mergeId);
                            var val = cmd.ExecuteScalar();
                            if (val != null && val != DBNull.Value)
                            {
                                var target = val.ToString().Trim();
                                if (!string.IsNullOrEmpty(target) && existingTexts.Add(target))
                                {
                                    using (var ins = new SqliteCommand(@"
                                        INSERT INTO termbase_synonyms
                                            (term_id, synonym_text, language, display_order, forbidden)
                                        VALUES (@termId, @text, 'target', @order, 0)", conn, tx))
                                    {
                                        ins.Parameters.AddWithValue("@termId", primaryTermId);
                                        ins.Parameters.AddWithValue("@text", target);
                                        ins.Parameters.AddWithValue("@order", maxOrder++);
                                        ins.ExecuteNonQuery();
                                    }
                                }
                            }
                        }

                        // Move this term's existing synonyms to the primary term
                        using (var cmd = new SqliteCommand(@"
                            SELECT synonym_text, language, forbidden FROM termbase_synonyms
                            WHERE term_id = @id ORDER BY language, display_order", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", mergeId);
                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    var text = reader.GetString(0);
                                    var lang = reader.GetString(1);
                                    var forbidden = !reader.IsDBNull(2) && GetBool(reader, 2);

                                    if (lang == "target" && existingTexts.Add(text))
                                    {
                                        using (var ins = new SqliteCommand(@"
                                            INSERT INTO termbase_synonyms
                                                (term_id, synonym_text, language, display_order, forbidden)
                                            VALUES (@termId, @text, @lang, @order, @forbidden)", conn, tx))
                                        {
                                            ins.Parameters.AddWithValue("@termId", primaryTermId);
                                            ins.Parameters.AddWithValue("@text", text);
                                            ins.Parameters.AddWithValue("@lang", lang);
                                            ins.Parameters.AddWithValue("@order", maxOrder++);
                                            ins.Parameters.AddWithValue("@forbidden", forbidden ? 1 : 0);
                                            ins.ExecuteNonQuery();
                                        }
                                    }
                                }
                            }
                        }

                        // Delete the merged term (cascade deletes its synonyms)
                        using (var cmd = new SqliteCommand(
                            "DELETE FROM termbase_terms WHERE id = @id", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", mergeId);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }
        }

        /// <summary>
        /// Checks whether a table exists in the database.
        /// </summary>
        private static bool HasTable(SqliteConnection conn, string tableName)
        {
            using (var cmd = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name", conn))
            {
                cmd.Parameters.AddWithValue("@name", tableName);
                var result = cmd.ExecuteScalar();
                return result != null && Convert.ToInt64(result) > 0;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
}
