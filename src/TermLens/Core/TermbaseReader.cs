using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TermLens.Models;

namespace TermLens.Core
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
                // Mode=ReadOnly — we only run SELECTs; this also avoids WAL
                // locking issues when Supervertaler has the DB open.
                var connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                _connection = new SqliteConnection(connStr);
                _connection.Open();
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

            const string sql = @"
                SELECT tb.id, tb.name, tb.source_lang, tb.target_lang,
                       tb.is_project_termbase, tb.ranking,
                       COUNT(t.id) as term_count
                FROM termbases tb
                LEFT JOIN termbase_terms t ON CAST(t.termbase_id AS INTEGER) = tb.id
                GROUP BY tb.id
                ORDER BY tb.ranking ASC, tb.name ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    result.Add(new TermbaseInfo
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        SourceLang = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        TargetLang = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        IsProjectTermbase = !reader.IsDBNull(4) && GetBool(reader, 4),
                        Ranking = reader.IsDBNull(5) ? 99 : reader.GetInt32(5),
                        TermCount = reader.GetInt32(6)
                    });
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

            const string sql = @"
                SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                       t.source_lang, t.target_lang, t.definition, t.domain,
                       t.notes, t.forbidden, t.case_sensitive,
                       tb.name AS termbase_name,
                       tb.is_project_termbase,
                       COALESCE(tb.ranking, 99) AS ranking
                FROM termbase_terms t
                LEFT JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE (LOWER(t.source_term) = LOWER(@term)
                    OR LOWER(RTRIM(t.source_term, '.!?,;:')) = LOWER(@term)
                    OR LOWER(@term) = LOWER(RTRIM(t.source_term, '.!?,;:')))
                  AND COALESCE(t.forbidden, 0) = 0
                ORDER BY ranking ASC, t.source_term ASC";

            using (var cmd = new SqliteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@term", normalised);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var entry = ReadTermEntry(reader);
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
        /// Bulk-loads all source terms for fast in-memory matching.
        /// Returns a dictionary mapping lowercased source term to list of entries.
        /// </summary>
        /// <param name="disabledTermbaseIds">
        /// Termbase IDs to exclude. Null or empty means load all termbases.
        /// </param>
        public Dictionary<string, List<TermEntry>> LoadAllTerms(HashSet<long> disabledTermbaseIds = null)
        {
            var index = new Dictionary<string, List<TermEntry>>(StringComparer.OrdinalIgnoreCase);
            if (_connection == null) return index;

            var sql = @"
                SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                       t.source_lang, t.target_lang, t.definition, t.domain,
                       t.notes, t.forbidden, t.case_sensitive,
                       tb.name AS termbase_name,
                       tb.is_project_termbase,
                       COALESCE(tb.ranking, 99) AS ranking
                FROM termbase_terms t
                LEFT JOIN termbases tb ON CAST(t.termbase_id AS INTEGER) = tb.id
                WHERE COALESCE(t.forbidden, 0) = 0";

            if (disabledTermbaseIds != null && disabledTermbaseIds.Count > 0)
            {
                // Build explicit exclusion list — parameterised via positional args
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
                        allEntries.Add(ReadTermEntry(reader));
                    }
                }
            }

            // Second pass: bulk-load all target synonyms into a dictionary
            var synonymsByTermId = BulkLoadTargetSynonyms();

            // Build the index and hydrate synonyms
            foreach (var entry in allEntries)
            {
                if (synonymsByTermId.TryGetValue(entry.Id, out var syns))
                    entry.TargetSynonyms = syns;

                var key = entry.SourceTerm.Trim().ToLowerInvariant();

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

        private static TermEntry ReadTermEntry(SqliteDataReader reader)
        {
            return new TermEntry
            {
                Id = reader.GetInt64(0),
                SourceTerm = reader.IsDBNull(1) ? "" : reader.GetString(1),
                TargetTerm = reader.IsDBNull(2) ? "" : reader.GetString(2),
                TermbaseId = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3)),
                SourceLang = reader.IsDBNull(4) ? "" : reader.GetString(4),
                TargetLang = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Definition = reader.IsDBNull(6) ? "" : reader.GetString(6),
                Domain = reader.IsDBNull(7) ? "" : reader.GetString(7),
                Notes = reader.IsDBNull(8) ? "" : reader.GetString(8),
                Forbidden = !reader.IsDBNull(9) && GetBool(reader, 9),
                CaseSensitive = !reader.IsDBNull(10) && GetBool(reader, 10),
                TermbaseName = reader.IsDBNull(11) ? "" : reader.GetString(11),
                IsProjectTermbase = !reader.IsDBNull(12) && GetBool(reader, 12),
                Ranking = reader.IsDBNull(13) ? 99 : reader.GetInt32(13)
            };
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
        public static long InsertTerm(string dbPath, long termbaseId,
            string sourceTerm, string targetTerm,
            string sourceLang, string targetLang,
            string definition = "", string domain = "", string notes = "")
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
                    INSERT INTO termbase_terms
                        (source_term, target_term, termbase_id, source_lang, target_lang,
                         definition, domain, notes, forbidden, case_sensitive)
                    VALUES
                        (@source, @target, @tbId, @srcLang, @tgtLang,
                         @def, @domain, @notes, 0, 0);
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

                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt64(result) : -1;
                }
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
