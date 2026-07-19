using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Produces a WAL-safe copy of a termbase file to read for import.
    ///
    /// A live or recently-edited Studio 2026 <c>.ttb</c> keeps its newest changes in a
    /// <c>-wal</c> sidecar that has not yet been checkpointed into the main file. A
    /// read-only SQLite connection can miss those changes (and opening a hot-WAL database
    /// read-only is itself unreliable, since SQLite needs write access to the <c>-shm</c>
    /// wal-index). To avoid importing stale data, this copies the <c>.ttb</c> together with
    /// its <c>-wal</c>/<c>-shm</c> sidecars to a temp file, checkpoints the WAL into the copy,
    /// then removes the copy's sidecars so the reader opens a single consistent file.
    ///
    /// The original file is never modified. <c>.sdltb</c> files, and <c>.ttb</c> files with no
    /// <c>-wal</c>, are returned as-is. Dispose deletes the temp copy.
    /// </summary>
    internal sealed class TtbImportSnapshot : IDisposable
    {
        private readonly string _tempPath;

        /// <summary>The path the caller should open (temp copy, or the original).</summary>
        public string ReadPath { get; }

        private TtbImportSnapshot(string readPath, string tempPath)
        {
            ReadPath = readPath;
            _tempPath = tempPath;
        }

        public static TtbImportSnapshot Prepare(string sourcePath)
        {
            try
            {
                if (sourcePath != null &&
                    sourcePath.EndsWith(".ttb", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(sourcePath + "-wal"))
                {
                    var temp = Path.Combine(
                        Path.GetTempPath(),
                        "sv_import_" + Guid.NewGuid().ToString("N") + ".ttb");

                    File.Copy(sourcePath, temp, true);
                    CopyIfExists(sourcePath + "-wal", temp + "-wal");
                    CopyIfExists(sourcePath + "-shm", temp + "-shm");

                    Checkpoint(temp);

                    // Data is now consolidated in the main file; drop the sidecars so the
                    // reader opens a single clean file with no WAL state to negotiate.
                    TryDelete(temp + "-wal");
                    TryDelete(temp + "-shm");

                    return new TtbImportSnapshot(temp, temp);
                }
            }
            catch
            {
                // Fall back to reading the original in place (e.g. copy blocked); a live
                // read-only connection against an open Studio DB still works via shared cache.
            }
            return new TtbImportSnapshot(sourcePath, null);
        }

        private static void CopyIfExists(string src, string dst)
        {
            if (File.Exists(src)) File.Copy(src, dst, true);
        }

        private static void Checkpoint(string ttbPath)
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = ttbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var conn = new SqliteConnection(connStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            if (_tempPath == null) return;
            TryDelete(_tempPath);
            TryDelete(_tempPath + "-wal");
            TryDelete(_tempPath + "-shm");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
