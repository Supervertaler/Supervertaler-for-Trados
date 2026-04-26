using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Represents a merge-candidate match found in the termbase.
    /// </summary>
    public class MergeMatch
    {
        /// <summary>Row ID of the existing term entry.</summary>
        public long TermId { get; set; }

        /// <summary>Existing entry's source term.</summary>
        public string SourceTerm { get; set; }

        /// <summary>Existing entry's target term.</summary>
        public string TargetTerm { get; set; }

        /// <summary>Termbase ID the match was found in.</summary>
        public long TermbaseId { get; set; }

        /// <summary>Display name of the termbase.</summary>
        public string TermbaseName { get; set; }

        /// <summary>
        /// "source" if the existing entry's source_term column matched, "target"
        /// if its target_term column matched. Always relative to the termbase's
        /// own storage direction, NOT the project direction — see TermbaseInverted.
        /// </summary>
        public string MatchType { get; set; }

        /// <summary>
        /// True when this match's termbase is declared in the inverse direction
        /// of the project (e.g. EN→NL termbase, NL→EN project). Callers use this
        /// to decide which of (project source, project target) to write into the
        /// existing entry's source-language vs target-language synonym slots.
        /// </summary>
        public bool TermbaseInverted { get; set; }
    }

    /// <summary>
    /// Checks whether a new source/target pair partially overlaps with an existing
    /// termbase entry (same source but different target, or same target but different source).
    /// Used to offer a "merge as synonym" prompt instead of creating near-duplicates.
    /// </summary>
    public static class TermMergeChecker
    {
        /// <summary>
        /// Finds existing entries that share the same source term (but different target)
        /// or the same target term (but different source) across the given write termbases.
        /// Returns an empty list when there are no merge candidates.
        /// </summary>
        /// <param name="projectSourceLang">
        /// Language of <paramref name="sourceTerm"/> as supplied by the caller.
        /// When provided, this method makes a PER-TERMBASE decision about whether
        /// the search needs to look at the swapped columns to match how that
        /// termbase stores its rows. Without this, reverse-direction termbases
        /// silently miss every match because the SQL would compare DB English
        /// columns against project Dutch text and vice versa. Mirrors the per-
        /// termbase swap that <see cref="TermbaseReader.InsertTermBatch"/> does.
        /// When null, the search runs in caller-supplied direction (legacy behaviour).
        /// </param>
        public static List<MergeMatch> FindMergeMatches(
            string dbPath, string sourceTerm, string targetTerm,
            List<TermbaseInfo> termbases,
            string projectSourceLang = null)
        {
            var matches = new List<MergeMatch>();

            if (string.IsNullOrWhiteSpace(dbPath) ||
                string.IsNullOrWhiteSpace(sourceTerm) ||
                string.IsNullOrWhiteSpace(targetTerm) ||
                termbases == null || termbases.Count == 0)
                return matches;

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();

                foreach (var tb in termbases)
                {
                    // Decide per-termbase whether this termbase stores rows in the
                    // inverse direction of the project. If so, swap the search
                    // parameters so the SQL compares the right columns.
                    string searchSource = sourceTerm;
                    string searchTarget = targetTerm;
                    bool isInverted = false;
                    if (!string.IsNullOrEmpty(projectSourceLang)
                        && !string.IsNullOrEmpty(tb.SourceLang))
                    {
                        var projNorm = LanguageUtils.ShortenLanguageName(projectSourceLang);
                        var tbNorm = LanguageUtils.ShortenLanguageName(tb.SourceLang);
                        bool directionMatches =
                            !string.IsNullOrEmpty(projNorm) && !string.IsNullOrEmpty(tbNorm) &&
                            (projNorm.StartsWith(tbNorm, StringComparison.OrdinalIgnoreCase) ||
                             tbNorm.StartsWith(projNorm, StringComparison.OrdinalIgnoreCase));
                        if (!directionMatches)
                        {
                            isInverted = true;
                            searchSource = targetTerm;
                            searchTarget = sourceTerm;
                        }
                    }

                    const string sql = @"
                        SELECT id, source_term, target_term
                        FROM termbase_terms
                        WHERE CAST(termbase_id AS INTEGER) = @tbId
                          AND (
                            (LOWER(TRIM(source_term)) = LOWER(@source)
                             AND LOWER(TRIM(target_term)) != LOWER(@target))
                            OR
                            (LOWER(TRIM(target_term)) = LOWER(@target)
                             AND LOWER(TRIM(source_term)) != LOWER(@source))
                          )";

                    using (var cmd = new SqliteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@tbId", tb.Id);
                        cmd.Parameters.AddWithValue("@source", searchSource.Trim());
                        cmd.Parameters.AddWithValue("@target", searchTarget.Trim());

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var existingSource = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                var existingTarget = reader.IsDBNull(2) ? "" : reader.GetString(2);

                                // MatchType is relative to termbase storage direction.
                                bool sourceMatched = string.Equals(
                                    existingSource.Trim(), searchSource.Trim(),
                                    StringComparison.OrdinalIgnoreCase);

                                matches.Add(new MergeMatch
                                {
                                    TermId = reader.GetInt64(0),
                                    SourceTerm = existingSource,
                                    TargetTerm = existingTarget,
                                    TermbaseId = tb.Id,
                                    TermbaseName = tb.Name ?? "",
                                    MatchType = sourceMatched ? "source" : "target",
                                    TermbaseInverted = isInverted
                                });
                            }
                        }
                    }
                }
            }

            return matches;
        }
    }
}
