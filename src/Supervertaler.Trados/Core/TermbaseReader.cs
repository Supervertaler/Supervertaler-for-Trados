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
        /// Searches for terms matching the given word/phrase across all active termbases.
        /// Mirrors Supervertaler's search_termbases() logic.
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
                WHERE (LOWER(t.source_term) = LOWER(@term)
                    OR LOWER(RTRIM(t.source_term, '.!?,;:')) = LOWER(@term)
                    OR LOWER(@term) = LOWER(RTRIM(t.source_term, '.!?,;:')))
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
            var termbaseDirection = new Dictionary<long, (string src, string tgt)>();
            using (var dirCmd = new SqliteCommand("SELECT id, source_lang, target_lang FROM termbases", _connection))
            using (var dirReader = dirCmd.ExecuteReader())
            {
                while (dirReader.Read())
                {
                    var tbId = dirReader.GetInt64(0);
                    var src = dirReader.IsDBNull(1) ? "" : dirReader.GetString(1);
                    var tgt = dirReader.IsDBNull(2) ? "" : dirReader.GetString(2);
                    termbaseDirection[tbId] = (src, tgt);
                }
            }

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
                    entry.SourceSynonyms = new List<Models.SynonymEntry>();
                    foreach (var synText in srcSynsForIndex)
                        entry.SourceSynonyms.Add(new Models.SynonymEntry { Text = synText, Language = "source" });
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

        /// <summary>Strip surrounding whitespace and trailing sentence punctuation
        /// from a term before storage (e.g. "circumference." -> "circumference").</summary>
        private static string NormalizeTermForSave(string text)
            => string.IsNullOrEmpty(text) ? text : text.Trim().TrimEnd(TermTrailingPunct).Trim();

        public static long InsertTerm(string dbPath, long termbaseId,
            string sourceTerm, string targetTerm,
            string sourceLang, string targetLang,
            string definition = "", string domain = "", string notes = "",
            bool isNonTranslatable = false,
            string sourceAbbreviation = null, string targetAbbreviation = null,
            string url = null, string client = null, bool forbidden = false,
            string project = null)
        {
            // Strip trailing sentence punctuation from translatable terms so
            // "circumference." is stored as "circumference". Non-translatables are
            // left as-is (a trailing "." may be meaningful, e.g. "Inc.").
            if (!isNonTranslatable)
            {
                sourceTerm = NormalizeTermForSave(sourceTerm);
                targetTerm = NormalizeTermForSave(targetTerm);
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
                         term_uuid, source_abbreviation, target_abbreviation, url, client, project)
                    VALUES
                        (@source, @target, @tbId, @srcLang, @tgtLang,
                         @def, @domain, @notes, @forbidden, 0, @nt,
                         @uuid, @srcAbbr, @tgtAbbr, @url, @client, @project);
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

                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt64(result) : -1;
                }
            }
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
            // Strip trailing sentence punctuation from translatable terms on save
            // (e.g. "circumference." -> "circumference"); non-translatables kept as-is.
            if (!isNonTranslatable)
            {
                sourceTerm = NormalizeTermForSave(sourceTerm);
                targetTerm = NormalizeTermForSave(targetTerm);
            }

            // projectSourceLang: language of `sourceTerm` as supplied by the caller.
            //   When provided, this method makes a PER-TERMBASE decision about
            //   whether the text needs to be swapped to match that termbase's
            //   stored direction. This fixes the long-standing bug where the
            //   caller (QuickAddTermAction) made ONE swap decision based on the
            //   first termbase and applied it to every termbase in the batch –
            //   which corrupted any write termbases whose direction happened
            //   to differ from the first one's.
            //
            //   When null (legacy callers), no per-termbase swap is done and the
            //   caller's pre-swap semantics are preserved.
            var results = new List<(long, long)>();

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

                    const string sql = @"
                        INSERT INTO termbase_terms
                            (source_term, target_term, termbase_id, source_lang, target_lang,
                             definition, domain, notes, forbidden, case_sensitive, is_nontranslatable,
                             term_uuid)
                        VALUES
                            (@source, @target, @tbId, @srcLang, @tgtLang,
                             @def, '', '', 0, 0, @nt,
                             @uuid);
                        SELECT last_insert_rowid();";

                    foreach (var tb in termbases)
                    {
                        // Decide per-termbase whether to swap so the text lands in the
                        // right columns FOR THIS termbase's declared direction.
                        // Only invert when project source matches the termbase's
                        // *target* language. NotApplicable / Aligned / Unrelated
                        // all leave the user's input as-is – pre-v4.19.56 the check
                        // treated any "not aligned" as "inverted", which silently
                        // swapped data on its way into termbases for unrelated
                        // language pairs.
                        string termForSourceColumn = sourceTerm;
                        string termForTargetColumn = targetTerm;
                        var direction = LanguageUtils.CompareTermbaseDirection(
                            projectSourceLang, tb.SourceLang, tb.TargetLang);
                        if (direction == LanguageUtils.TermbaseDirection.Inverted)
                        {
                            termForSourceColumn = targetTerm;
                            termForTargetColumn = sourceTerm;
                        }

                        // Skip if duplicate already exists in this termbase (either direction).
                        using (var check = new SqliteCommand(checkSql, conn, txn))
                        {
                            check.Parameters.AddWithValue("@tbId", tb.Id);
                            check.Parameters.AddWithValue("@source", termForSourceColumn.Trim());
                            check.Parameters.AddWithValue("@target", termForTargetColumn.Trim());

                            if (check.ExecuteScalar() != null)
                                continue; // duplicate – skip this termbase
                        }

                        using (var cmd = new SqliteCommand(sql, conn, txn))
                        {
                            cmd.Parameters.AddWithValue("@source", termForSourceColumn.Trim());
                            cmd.Parameters.AddWithValue("@target", termForTargetColumn.Trim());
                            cmd.Parameters.AddWithValue("@tbId", tb.Id);
                            cmd.Parameters.AddWithValue("@srcLang", tb.SourceLang);
                            cmd.Parameters.AddWithValue("@tgtLang", tb.TargetLang);
                            cmd.Parameters.AddWithValue("@def", definition ?? "");
                            cmd.Parameters.AddWithValue("@nt", isNonTranslatable ? 1 : 0);
                            cmd.Parameters.AddWithValue("@uuid", System.Guid.NewGuid().ToString());

                            var result = cmd.ExecuteScalar();
                            var newId = result != null ? Convert.ToInt64(result) : -1;
                            if (newId > 0)
                                results.Add((tb.Id, newId));
                        }
                    }
                    txn.Commit();
                }
            }

            return results;
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
                        project     = @project
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
            string sourceLang, string targetLang, IProgress<int> progress = null)
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

            // Parse headers
            var headers = lines[0].Split('\t');
            var colMap = MapTsvColumns(headers, sourceLang, targetLang);

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

                        // Optional metadata
                        var uuid = UnescapeTsvField(GetField(fields, colMap, "uuid"));
                        var priority = ParseInt(GetField(fields, colMap, "priority"), 99);
                        var domain = UnescapeTsvField(GetField(fields, colMap, "domain") ?? "");
                        var notes = UnescapeTsvField(GetField(fields, colMap, "notes") ?? "");
                        var project = UnescapeTsvField(GetField(fields, colMap, "project") ?? "");
                        var client = UnescapeTsvField(GetField(fields, colMap, "client") ?? "");
                        var forbidden = ParseBool(GetField(fields, colMap, "forbidden"));

                        // UUID: check for existing term or generate new
                        long termId = -1;
                        if (!string.IsNullOrWhiteSpace(uuid))
                        {
                            using (var qry = new SqliteCommand(
                                "SELECT id FROM termbase_terms WHERE term_uuid = @uuid", conn, tx))
                            {
                                qry.Parameters.AddWithValue("@uuid", uuid);
                                var existing = qry.ExecuteScalar();
                                if (existing != null)
                                    termId = Convert.ToInt64(existing);
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
                                    priority = @prio, domain = @domain, notes = @notes,
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
                                     priority, domain, notes, project, client, forbidden, case_sensitive, term_uuid)
                                VALUES
                                    (@src, @tgt, @tbId, @srcLang, @tgtLang,
                                     @prio, @domain, @notes, @project, @client, @forbidden, 0, @uuid);
                                SELECT last_insert_rowid();", conn, tx))
                            {
                                ins.Parameters.AddWithValue("@src", srcMain);
                                ins.Parameters.AddWithValue("@tgt", tgtMain);
                                ins.Parameters.AddWithValue("@tbId", termbaseId);
                                ins.Parameters.AddWithValue("@srcLang", sourceLang);
                                ins.Parameters.AddWithValue("@tgtLang", targetLang);
                                ins.Parameters.AddWithValue("@prio", priority);
                                ins.Parameters.AddWithValue("@domain", domain);
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

                // Fixed headers – language-neutral so the exported TSV can
                // always be reimported regardless of the target termbase's
                // language pair.
                const string srcHeader = "Source";
                const string tgtHeader = "Target";

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

            var parts = cell.Split('|');
            var mainTerm = parts[0].Trim();
            var synonyms = new List<(string text, bool forbidden)>();

            for (int i = 1; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (string.IsNullOrEmpty(part)) continue;

                if (part.StartsWith("[!") && part.EndsWith("]") && part.Length > 3)
                {
                    synonyms.Add((part.Substring(2, part.Length - 3).Trim(), true));
                }
                else
                {
                    synonyms.Add((part, false));
                }
            }

            return (mainTerm, synonyms);
        }

        /// <summary>
        /// Builds a pipe-delimited cell: "main|syn1|[!forbidden_syn]"
        /// </summary>
        private static string BuildPipeDelimitedCell(string mainTerm, List<(string text, bool forbidden)> synonyms)
        {
            if (synonyms == null || synonyms.Count == 0)
                return mainTerm;

            var sb = new StringBuilder(mainTerm);
            foreach (var (text, forbidden) in synonyms)
            {
                sb.Append('|');
                sb.Append(forbidden ? $"[!{text}]" : text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps TSV header names to standardized column keys (case-insensitive).
        /// </summary>
        private static Dictionary<string, int> MapTsvColumns(string[] headers, string sourceLang, string targetLang)
        {
            var map = new Dictionary<string, int>();
            int firstLangCol = -1;
            int secondLangCol = -1;

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
                else if (h == "notes" || h == "note" || h == "definition" || h == "comment" || h == "comments" || h == "description")
                    map["notes"] = i;
                else if (h == "project" || h == "proj")
                    map["project"] = i;
                else if (h == "client" || h == "customer")
                    map["client"] = i;
                else if (h == "forbidden" || h == "do not use" || h == "prohibited" || h == "banned")
                    map["forbidden"] = i;
                else
                {
                    // Try matching language names as column headers
                    if (!map.ContainsKey("source") && MatchesLanguage(h, sourceLang))
                        map["source"] = i;
                    else if (!map.ContainsKey("target") && MatchesLanguage(h, targetLang))
                        map["target"] = i;
                    else if (firstLangCol < 0 && IsKnownLanguage(h))
                        firstLangCol = i;
                    else if (secondLangCol < 0 && IsKnownLanguage(h))
                        secondLangCol = i;
                }
            }

            // Fallback: if source/target not yet mapped, use first two language columns
            if (!map.ContainsKey("source") && firstLangCol >= 0)
                map["source"] = firstLangCol;
            if (!map.ContainsKey("target") && secondLangCol >= 0)
                map["target"] = secondLangCol;

            return map;
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
        private static bool IsKnownLanguage(string header)
        {
            if (KnownLanguages.Contains(header)) return true;
            var baseName = StripParenthesisedRegion(header);
            return baseName != header && KnownLanguages.Contains(baseName);
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
