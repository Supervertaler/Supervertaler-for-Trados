using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Copies the data folder to a new, empty location for Settings → General →
    /// Move…. Copies rather than moves: the old folder stays exactly as it was
    /// until the user deletes it, so a failure part-way costs nothing - the
    /// partial copy is removed and the old folder is still the one in use.
    /// Switching config.json over is the caller's step, after this succeeds.
    ///
    /// Runs while Trados Studio has the data folder open, so SQLite databases
    /// are copied with SQLite's own backup (a consistent copy of an open, WAL-mode
    /// database), never as plain files. Settings store absolute paths into the
    /// data folder (the termbase, prompts, per-project settings), so those are
    /// rewritten in the copy's JSON files to point at the new location.
    /// </summary>
    internal static class DataFolderMover
    {
        internal sealed class Result
        {
            public int Files;
            public long Bytes;
            public int Databases;
            public int RewrittenFiles;
            /// <summary>The target folder did not exist before the copy - for
            /// <see cref="RemoveCopy"/>, if a later step fails.</summary>
            public bool CreatedTarget;
        }

        /// <summary>Why <paramref name="to"/> cannot take the data folder at
        /// <paramref name="from"/>, or null when it can.</summary>
        internal static string Problem(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(to) || !Regex.IsMatch(to.Trim(), @"^([A-Za-z]:\\|\\\\)"))
                return "Choose a full path, such as E:\\Work\\Supervertaler.";
            try { return ProblemWith(Normalise(from), Normalise(to.Trim())); }
            catch (Exception ex) { return "That folder cannot be used: " + ex.Message; }
        }

        private static string ProblemWith(string f, string t)
        {
            if (!Directory.Exists(f))
                return "Your data folder cannot be found:\n" + f;
            if (IsDriveRoot(f))
                return "Your data folder is a whole drive (" + f + "), so it has to be moved by hand.";
            if (string.Equals(f, t, StringComparison.OrdinalIgnoreCase))
                return "That is your data folder already.";
            if (t.StartsWith(WithSeparator(f), StringComparison.OrdinalIgnoreCase))
                return "The new folder cannot be inside your current data folder.";
            if (Directory.Exists(t) && Directory.EnumerateFileSystemEntries(t).Any())
                return "Choose an empty folder: " + t + " already has files or folders in it.";
            return null;
        }

        /// <summary>
        /// True when <paramref name="folder"/> already holds a Supervertaler data
        /// folder - the old one after a move, a restored backup, one copied from
        /// another computer - so Move can offer to switch to it as it is, instead
        /// of refusing it for not being empty.
        /// </summary>
        internal static bool LooksLikeDataFolder(string folder)
        {
            try
            {
                return new[] { "trados", "licence", "workbench", "memoq" }.Any(d => Directory.Exists(Path.Combine(folder, d)))
                       || File.Exists(Path.Combine(folder, "resources", "supervertaler.db"));
            }
            catch { return false; }
        }

        /// <summary>Why the data folder at <paramref name="from"/> cannot be
        /// switched to the existing one at <paramref name="to"/>, or null.</summary>
        internal static string SwitchProblem(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(to) || !Regex.IsMatch(to.Trim(), @"^([A-Za-z]:\\|\\\\)"))
                return "Choose a full path, such as E:\\Work\\Supervertaler.";
            try
            {
                var f = Normalise(from);
                var t = Normalise(to.Trim());
                if (string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) return "That is your data folder already.";
                if (t.StartsWith(WithSeparator(f), StringComparison.OrdinalIgnoreCase) ||
                    f.StartsWith(WithSeparator(t), StringComparison.OrdinalIgnoreCase))
                    return "One data folder cannot be inside the other.";
                if (!LooksLikeDataFolder(t)) return t + " does not hold a Supervertaler data folder.";
                return null;
            }
            catch (Exception ex) { return "That folder cannot be used: " + ex.Message; }
        }

        /// <summary>True for a UNC path or a mapped network drive: the termbase
        /// database is not reliable over a network share.</summary>
        internal static bool IsNetworkPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\")) return true;
                return new DriveInfo(Path.GetPathRoot(full)).DriveType == DriveType.Network;
            }
            catch { return false; }
        }

        /// <summary>
        /// Copies everything in <paramref name="from"/> to <paramref name="to"/>,
        /// which <see cref="Problem"/> has accepted. Throws on failure or
        /// cancellation, after removing whatever it copied.
        /// </summary>
        internal static Result Copy(string from, string to, IProgress<string> progress, CancellationToken ct)
        {
            from = Normalise(from);
            to = Normalise(to);

            var dirs = Directory.GetDirectories(from, "*", SearchOption.AllDirectories);
            var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
            var databases = new HashSet<string>(files.Where(IsSqliteDatabase), StringComparer.OrdinalIgnoreCase);
            // A database's own journal files are folded into its backup copy.
            var companions = new HashSet<string>(
                databases.SelectMany(d => new[] { d + "-wal", d + "-shm", d + "-journal" }),
                StringComparer.OrdinalIgnoreCase);
            var toCopy = files.Where(p => !companions.Contains(p)).ToList();
            long totalBytes = toCopy.Sum(p => new FileInfo(p).Length);

            var free = FreeBytes(to);
            if (free.HasValue && free.Value < totalBytes)
                throw new IOException("There is not enough free space: the data folder needs " +
                    FormatBytes(totalBytes) + ", and " + FormatBytes(free.Value) + " is free there.");

            bool createdTarget = !Directory.Exists(to);
            Directory.CreateDirectory(to);
            try
            {
                foreach (var dir in dirs)
                    Directory.CreateDirectory(Path.Combine(to, Relative(from, dir)));

                var result = new Result { CreatedTarget = createdTarget };
                int lastReport = 0;
                foreach (var file in toCopy)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Relative(from, file);
                    // Throttled: a folder of tens of thousands of small files would
                    // otherwise post one UI update per file.
                    if (result.Files == 0 || unchecked(Environment.TickCount - lastReport) > 100)
                    {
                        lastReport = Environment.TickCount;
                        progress?.Report(string.Format("Copying {0:N0} of {1:N0} files…", result.Files + 1, toCopy.Count));
                    }
                    var dest = Path.Combine(to, rel);
                    try
                    {
                        if (databases.Contains(file))
                        {
                            BackupDatabase(file, dest);
                            result.Databases++;
                        }
                        else
                        {
                            File.Copy(file, dest, false);
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        throw new IOException(rel + " could not be copied: " + ex.Message, ex);
                    }
                    result.Files++;
                    result.Bytes += new FileInfo(file).Length;
                }

                progress?.Report("Checking the copied databases…");
                foreach (var db in databases)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Relative(from, db);
                    var check = QuickCheck(Path.Combine(to, rel));
                    if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The copy of " + rel + " did not pass SQLite's check: " + check);
                }

                progress?.Report("Updating paths in the copied settings…");
                result.RewrittenFiles = RewritePathsInJsonFiles(to, from);
                return result;
            }
            catch
            {
                RemoveCopy(to, createdTarget);
                throw;
            }
        }

        /// <summary>
        /// Points absolute paths into <paramref name="from"/> at <paramref name="to"/>
        /// in every JSON file under <paramref name="to"/>. Returns how many files changed.
        /// Each file keeps its own encoding (with or without a BOM).
        /// </summary>
        internal static int RewritePathsInJsonFiles(string to, string from)
        {
            int changed = 0;
            var strictUtf8 = new UTF8Encoding(false, true);
            foreach (var json in Directory.GetFiles(to, "*.json", SearchOption.AllDirectories)
                         .Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = File.ReadAllBytes(json);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text;
                try { text = strictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)); }
                catch (DecoderFallbackException) { continue; }   // not UTF-8: leave it alone rather than mangle it
                var updated = RewritePaths(text, from, to);
                if (updated == text) continue;
                var attrs = File.GetAttributes(json);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(json, attrs & ~FileAttributes.ReadOnly);
                File.WriteAllText(json, updated, new UTF8Encoding(bom));
                File.SetAttributes(json, attrs);
                changed++;
            }
            return changed;
        }

        /// <summary>
        /// Rewrites JSON string values that are, or start with, the old data folder:
        /// as JSON writes it (backslashes doubled) and with forward slashes, as some
        /// programs store paths. Matches only a whole folder name - "D:\Supervertaler2"
        /// is not inside "D:\Supervertaler" - and ignores case, as Windows does.
        /// </summary>
        internal static string RewritePaths(string json, string from, string to)
        {
            from = Normalise(from);
            to = Normalise(to);
            var toTrimmed = to.TrimEnd('\\');   // "F:\" before a separator becomes "F:"

            string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

            // Old folder followed by an escaped backslash, or by the end of the value.
            var backslashed = new Regex(Regex.Escape(Escape(from)) + @"(?=\\\\|"")", RegexOptions.IgnoreCase);
            json = backslashed.Replace(json, m =>
                json[m.Index + m.Length] == '"' ? Escape(to) : Escape(toTrimmed));

            var fromSlashed = from.Replace('\\', '/');
            var slashed = new Regex(Regex.Escape(fromSlashed) + @"(?=/|"")", RegexOptions.IgnoreCase);
            json = slashed.Replace(json, m =>
                json[m.Index + m.Length] == '"' ? Escape(to.Replace('\\', '/')) : Escape(toTrimmed.Replace('\\', '/')));
            return json;
        }

        private static void BackupDatabase(string source, string destination)
        {
            // Pooling off: a pooled connection keeps the file open after Dispose,
            // which would block removing the copy if a later step fails.
            var src = new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            var dst = new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
            using (var s = new SqliteConnection(src.ToString()))
            using (var d = new SqliteConnection(dst.ToString()))
            {
                s.Open();
                d.Open();
                s.BackupDatabase(d);
            }
        }

        private static string QuickCheck(string database)
        {
            // Read-write, on our own copy: a read-only connection to a WAL-mode
            // database cannot clean its -wal/-shm files up on close.
            var cs = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
            using (var c = new SqliteConnection(cs.ToString()))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA quick_check";
                    return Convert.ToString(cmd.ExecuteScalar());
                }
            }
        }

        private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

        /// <summary>By content, not extension: termbases and TMs do not all end in .db.</summary>
        internal static bool IsSqliteDatabase(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = new byte[SqliteHeader.Length];
                    return fs.Read(head, 0, head.Length) == head.Length && head.SequenceEqual(SqliteHeader);
                }
            }
            catch { return false; }   // unreadable: the copy itself will report it
        }

        /// <summary>Removes a copy that will not be used. The target was empty (or
        /// did not exist) before the copy started, so everything in it is ours.
        /// False when something could not be removed.</summary>
        internal static bool RemoveCopy(string to, bool createdTarget)
        {
            try
            {
                to = Normalise(to);
                if (!Directory.Exists(to)) return true;
                foreach (var file in Directory.GetFiles(to, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);   // a read-only file blocks Delete
                if (createdTarget)
                {
                    Directory.Delete(to, true);
                }
                else
                {
                    foreach (var file in Directory.GetFiles(to)) File.Delete(file);
                    foreach (var dir in Directory.GetDirectories(to)) Directory.Delete(dir, true);
                }
                return true;
            }
            catch { return false; }   // the caller's message names the folder
        }

        private static long? FreeBytes(string folder)
        {
            try
            {
                if (folder.StartsWith(@"\\")) return null;   // DriveInfo does not take UNC paths
                return new DriveInfo(Path.GetPathRoot(folder)).AvailableFreeSpace;
            }
            catch { return null; }
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return Math.Max(1, (bytes + 1023) / 1024) + " KB";
        }

        /// <summary>Full path without a trailing separator, except a drive root ("E:\").</summary>
        private static string Normalise(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            return full.EndsWith(":") ? full + "\\" : full;
        }

        private static bool IsDriveRoot(string normalised) => normalised.EndsWith(":\\");

        private static string WithSeparator(string normalised) =>
            normalised.EndsWith("\\") ? normalised : normalised + "\\";

        private static string Relative(string from, string path) =>
            path.Substring(WithSeparator(from).Length);
    }
}
