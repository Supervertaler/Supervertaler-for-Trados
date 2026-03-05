using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        /// <summary>
        /// Updates an existing term's source, target, definition, domain, and notes
        /// using a short-lived ReadWrite connection (same pattern as InsertTerm).
        /// </summary>
        /// <returns>True if the row was updated, false if the term ID was not found.</returns>
        public static bool UpdateTerm(string dbPath, long termId,
            string sourceTerm, string targetTerm,
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
                    UPDATE termbase_terms
                    SET source_term = @source,
                        target_term = @target,
                        definition  = @def,
                        domain      = @domain,
                        notes       = @notes
                    WHERE id = @id";

                using (var cmd = new SqliteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@source", sourceTerm.Trim());
                    cmd.Parameters.AddWithValue("@target", targetTerm.Trim());
                    cmd.Parameters.AddWithValue("@def", definition ?? "");
                    cmd.Parameters.AddWithValue("@domain", domain ?? "");
                    cmd.Parameters.AddWithValue("@notes", notes ?? "");
                    cmd.Parameters.AddWithValue("@id", termId);

                    return cmd.ExecuteNonQuery() > 0;
                }
            }
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
        /// Loads all terms belonging to a specific glossary, for use in the
        /// Glossary Editor dialog. Uses a short-lived ReadOnly connection.
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

                const string sql = @"
                    SELECT t.id, t.source_term, t.target_term, t.termbase_id,
                           t.source_lang, t.target_lang, t.definition, t.domain,
                           t.notes, t.forbidden, t.case_sensitive,
                           tb.name AS termbase_name,
                           tb.is_project_termbase,
                           COALESCE(tb.ranking, 99) AS ranking
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
                            results.Add(ReadTermEntry(reader));
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

                // FTS5 virtual table — may not be available in all builds
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
                    // FTS5 not available in this SQLite build — non-critical
                }
            }
        }

        /// <summary>
        /// Creates a new glossary (termbase entry) in an existing database.
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
        /// Deletes a glossary and all its terms from the database.
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
        /// Imports terms from a TSV file into the specified glossary.
        /// Handles pipe-delimited synonyms, [!forbidden] markers, and UUID tracking.
        /// </summary>
        /// <returns>Number of terms imported/updated.</returns>
        public static int ImportTsv(string dbPath, long termbaseId, string tsvPath,
            string sourceLang, string targetLang)
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

                        var sourceCell = GetField(fields, colMap, "source");
                        var targetCell = GetField(fields, colMap, "target");
                        if (string.IsNullOrWhiteSpace(sourceCell) || string.IsNullOrWhiteSpace(targetCell))
                            continue;

                        // Parse pipe-delimited cells
                        var (srcMain, srcSynonyms) = ParsePipeDelimitedCell(sourceCell);
                        var (tgtMain, tgtSynonyms) = ParsePipeDelimitedCell(targetCell);
                        if (string.IsNullOrWhiteSpace(srcMain) || string.IsNullOrWhiteSpace(tgtMain))
                            continue;

                        // Optional metadata
                        var uuid = GetField(fields, colMap, "uuid");
                        var priority = ParseInt(GetField(fields, colMap, "priority"), 99);
                        var domain = GetField(fields, colMap, "domain") ?? "";
                        var notes = GetField(fields, colMap, "notes") ?? "";
                        var project = GetField(fields, colMap, "project") ?? "";
                        var client = GetField(fields, colMap, "client") ?? "";
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
                    }

                    tx.Commit();
                }
            }

            return count;
        }

        /// <summary>
        /// Exports all terms from a glossary to a TSV file with full metadata.
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

                // Write TSV
                using (var sw = new StreamWriter(tsvPath, false, new UTF8Encoding(true)))
                {
                    sw.WriteLine("Term UUID\tSource Term\tTarget Term\tPriority\tDomain\tNotes\tProject\tClient\tForbidden");

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

                        sw.WriteLine($"{term.uuid}\t{sourceCell}\t{targetCell}\t{term.priority}\t" +
                                     $"{term.domain}\t{term.notes}\t{term.project}\t{term.client}\t" +
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
            return header == lc || lc.StartsWith(header) || header.StartsWith(lc.Split('-')[0]);
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

        private static bool IsKnownLanguage(string header)
        {
            return KnownLanguages.Contains(header);
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
                    cmd.Parameters.AddWithValue("@text", synonyms[i].text);
                    cmd.Parameters.AddWithValue("@lang", language);
                    cmd.Parameters.AddWithValue("@order", i);
                    cmd.Parameters.AddWithValue("@forbidden", synonyms[i].forbidden ? 1 : 0);
                    cmd.ExecuteNonQuery();
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
