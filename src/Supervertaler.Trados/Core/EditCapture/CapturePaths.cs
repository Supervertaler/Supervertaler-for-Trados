using System;
using System.IO;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core.EditCapture
{
    /// <summary>
    /// Where the capture database lives, and the refusal that keeps it out of a
    /// synced folder.
    ///
    /// <para>The database holds verbatim client source and target text under
    /// NDA. It must never reach Google Drive, OneDrive, Dropbox, iCloud or Box.
    /// Saying so in a specification is not enough: the Supervertaler data root
    /// is user-configurable (<c>user_data_path</c>), so a user can point it at a
    /// synced folder without ever thinking about this feature. The check is
    /// therefore at runtime and it refuses rather than warns — a warning about a
    /// confidentiality breach that has already happened is not much use.</para>
    ///
    /// <para>This exact failure happened on 2026-09-19 in the sandbox where the
    /// design was written: a database of real client text sat in Google Drive
    /// behind a <c>.gitignore</c> that was inert because the folder was not a
    /// git repository at all. The rule was written down and still broken. Hence
    /// a mechanism.</para>
    /// </summary>
    internal static class CapturePaths
    {
        /// <summary>Folder names that mean "this is synchronised to someone else's server".</summary>
        private static readonly string[] SyncedMarkers =
        {
            "google drive", "googledrive", "onedrive", "dropbox",
            "icloud", "icloudcrive", "box sync", "boxsync", "pcloud", "mega sync"
        };

        /// <summary>The capture folder, under the Supervertaler data root.</summary>
        public static string CaptureDir => Path.Combine(UserDataPath.TradosDir, "capture");

        /// <summary>The database file.</summary>
        public static string DatabasePath => Path.Combine(CaptureDir, "edits.db");

        /// <summary>
        /// True when <paramref name="path"/> sits under a folder whose name says
        /// it is synchronised. Matched on a path segment, not a substring, so a
        /// project legitimately called "Dropbox migration" is not caught.
        /// </summary>
        public static bool LooksSynced(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { full = path; }

            var parts = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                   StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var p = part.Trim().ToLowerInvariant();
                foreach (var marker in SyncedMarkers)
                    if (string.Equals(p, marker, StringComparison.Ordinal))
                        return true;
            }
            return false;
        }

        /// <summary>
        /// The reason capture cannot run, or null when it can. Checked before the
        /// writer is started and again before the file is created.
        /// </summary>
        public static string RefusalReason()
        {
            try
            {
                var dir = CaptureDir;
                if (LooksSynced(dir))
                    return "the Supervertaler data folder is inside a synchronised folder (" + dir + "). "
                         + "Edit capture stores client text and will not write there. Move the data folder, "
                         + "or leave capture switched off.";
                return null;
            }
            catch (Exception ex)
            {
                return "the capture location could not be resolved: " + ex.Message;
            }
        }

        /// <summary>Creates the folder. Returns false and logs when it cannot, never throws.</summary>
        public static bool EnsureDir()
        {
            try
            {
                Directory.CreateDirectory(CaptureDir);
                return true;
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.Log("EditCapture", "could not create " + CaptureDir + ": " + ex.Message); }
                catch { }
                return false;
            }
        }
    }
}
