using System;
using System.IO;
using System.Text;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Central resolver for all file-system paths used by the Supervertaler for Trados plugin.
    ///
    /// Both Supervertaler Workbench and this plugin share a single user-data root folder
    /// (default: ~/Supervertaler/).  The root is stored as "user_data_path" in a shared
    /// config pointer at %APPDATA%\Supervertaler\config.json — the same file Workbench
    /// reads and writes.
    ///
    /// Folder layout under the root:
    ///   prompt_library/     — prompt .md files shared between both products
    ///   resources/          — supervertaler.db (shared termbase, if present)
    ///   workbench/          — Supervertaler Workbench-specific data
    ///     settings/         — Workbench settings files
    ///   trados/
    ///     settings/         — Trados plugin settings
    ///       settings.json   — plugin preferences
    ///       license.json    — license activation state
    ///       chat_history.json — AI Assistant chat history
    ///     projects/         — per-project settings overlays
    ///
    /// Call <see cref="NeedsFirstRunSetup"/> before any path access to check whether the
    /// user has ever configured a data folder.  The first-run dialog calls
    /// <see cref="SetRoot"/> once to persist the chosen path and reset cached values.
    /// </summary>
    public static class UserDataPath
    {
        // Shared config pointer — same file used by Supervertaler Workbench
        private static readonly string ConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Supervertaler", "config.json");

        // Legacy plugin-only directory (pre-unification)
        internal static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler.Trados");

        // Lazily resolved root; reset to null by SetRoot()
        private static string _root;

        // ── Root ─────────────────────────────────────────────────────

        /// <summary>
        /// Root of the shared Supervertaler user-data folder.
        /// Reads from %APPDATA%\Supervertaler\config.json when available;
        /// falls back to ~/Supervertaler/.
        /// </summary>
        public static string Root
        {
            get
            {
                if (_root == null)
                    _root = ResolveRoot();
                return _root;
            }
        }

        /// <summary>
        /// True when no config.json pointer exists yet (first run, no folder chosen).
        /// The caller should show <see cref="Controls.SetupDialog"/> in this case.
        /// </summary>
        public static bool NeedsFirstRunSetup => !File.Exists(ConfigFile);

        // ── Shared directories ───────────────────────────────────────

        /// <summary>Prompt .md files shared between Workbench and the Trados plugin.</summary>
        public static string PromptLibraryDir => Path.Combine(Root, "prompt_library");

        /// <summary>Shared resources folder (supervertaler.db lives here).</summary>
        public static string ResourcesDir => Path.Combine(Root, "resources");

        // ── Trados-specific sub-directory ────────────────────────────

        /// <summary>Trados-specific sub-folder inside the shared root.</summary>
        public static string TradosDir => Path.Combine(Root, "trados");

        /// <summary>Settings sub-folder inside the Trados directory.</summary>
        public static string TradosSettingsDir => Path.Combine(TradosDir, "settings");

        /// <summary>Path to the plugin settings file.</summary>
        public static string SettingsFilePath => Path.Combine(TradosSettingsDir, "settings.json");

        /// <summary>Path to the license activation file.</summary>
        public static string LicenseFilePath => Path.Combine(TradosSettingsDir, "license.json");

        /// <summary>Path to the persisted AI Assistant chat history file.</summary>
        public static string ChatHistoryFilePath => Path.Combine(TradosSettingsDir, "chat_history.json");

        /// <summary>Folder containing per-project settings overlays.</summary>
        public static string ProjectsDir => Path.Combine(TradosDir, "projects");

        // ── Configuration ────────────────────────────────────────────

        /// <summary>
        /// Persists <paramref name="path"/> as "user_data_path" in the shared config.json
        /// and resets the cached root so subsequent accesses use the new value.
        /// </summary>
        public static void SetRoot(string path)
        {
            _root = path;
            WriteConfigJson(path);
        }

        /// <summary>
        /// Returns the default root path proposed to new users:
        /// ~/Supervertaler/ if Workbench is already installed there,
        /// otherwise ~/Supervertaler/ as the canonical default.
        /// </summary>
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Supervertaler");

        /// <summary>
        /// Returns the Workbench data path read from config.json, or null if not found.
        /// Used by the first-run dialog to surface an existing installation.
        /// </summary>
        public static string DetectWorkbenchRoot()
        {
            try
            {
                if (!File.Exists(ConfigFile)) return null;
                var json = File.ReadAllText(ConfigFile, Encoding.UTF8);
                var path = ExtractJsonString(json, "user_data_path");
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
            catch { }
            return null;
        }

        // ── Migration ────────────────────────────────────────────────

        /// <summary>
        /// One-time migration from the legacy %LocalAppData%\Supervertaler.Trados\ folder
        /// to the new unified location.  A .migrated flag file prevents re-running.
        /// After successful migration, removes the legacy folder.
        /// Also cleans up other stale AppData folders on every startup.
        /// Safe to call on every startup.
        /// </summary>
        public static void MigrateIfNeeded()
        {
            var flagFile = Path.Combine(TradosDir, ".migrated");

            // Run migration if legacy dir exists and hasn't been migrated yet
            if (Directory.Exists(LegacyDir) && !File.Exists(flagFile))
            {
                try
                {
                    Directory.CreateDirectory(TradosDir);

                    MigrateFile(
                        Path.Combine(LegacyDir, "settings.json"),
                        SettingsFilePath);

                    MigrateFile(
                        Path.Combine(LegacyDir, "license.json"),
                        LicenseFilePath);

                    MigrateDirectory(
                        Path.Combine(LegacyDir, "projects"),
                        ProjectsDir);

                    // Legacy plugin prompts → shared prompt_library
                    MigrateDirectory(
                        Path.Combine(LegacyDir, "prompts"),
                        PromptLibraryDir);

                    File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                }
                catch
                {
                    // Non-fatal — legacy files remain in place as a fallback
                }
            }

            // v2 layout migration: move trados/{settings,license,chat_history}.json
            // into trados/settings/ subfolder
            MigrateToSettingsSubfolder();

            // Clean up legacy/stale AppData folders (safe to run every startup)
            CleanupLegacyFolders();
        }

        /// <summary>
        /// v2 layout migration: moves settings.json, license.json and chat_history.json
        /// from trados/ into trados/settings/.  Gated on a .migrated_v2 flag file.
        /// Safe to call on every startup.
        /// </summary>
        private static void MigrateToSettingsSubfolder()
        {
            var flagFile = Path.Combine(TradosDir, ".migrated_v2");
            if (File.Exists(flagFile)) return;

            // Only migrate if old-layout files exist at the trados/ level
            var oldSettings    = Path.Combine(TradosDir, "settings.json");
            var oldLicense     = Path.Combine(TradosDir, "license.json");
            var oldChatHistory = Path.Combine(TradosDir, "chat_history.json");

            if (!File.Exists(oldSettings) && !File.Exists(oldLicense) && !File.Exists(oldChatHistory))
            {
                // Nothing to migrate — probably a fresh install.  Write the flag
                // so we don't check again, then create the settings dir.
                try
                {
                    Directory.CreateDirectory(TradosSettingsDir);
                    File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
                }
                catch { }
                return;
            }

            try
            {
                Directory.CreateDirectory(TradosSettingsDir);

                MigrateFile(oldSettings,    SettingsFilePath);
                MigrateFile(oldLicense,     LicenseFilePath);
                MigrateFile(oldChatHistory, ChatHistoryFilePath);

                // Delete old files after successful copy
                TryDelete(oldSettings);
                TryDelete(oldLicense);
                TryDelete(oldChatHistory);

                File.WriteAllText(flagFile, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
            }
            catch
            {
                // Non-fatal — old files remain usable at the old paths
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Removes legacy AppData folders that are no longer used:
        ///   %LocalAppData%\Supervertaler.Trados\  — old plugin settings (migrated)
        ///   %LocalAppData%\Supervertaler\          — stale Workbench artifact on Windows
        /// Only deletes if the migration flag exists (data has been safely copied).
        /// </summary>
        private static void CleanupLegacyFolders()
        {
            var flagFile = Path.Combine(TradosDir, ".migrated");
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Remove %LocalAppData%\Supervertaler.Trados\ (old plugin-only dir)
            // Only if migration has completed (flag exists)
            if (File.Exists(flagFile) && Directory.Exists(LegacyDir))
            {
                try { Directory.Delete(LegacyDir, true); } catch { }
            }

            // Remove %LocalAppData%\Supervertaler\ (stale Workbench artifact, not used by any current code)
            var staleWorkbenchDir = Path.Combine(localAppData, "Supervertaler");
            if (Directory.Exists(staleWorkbenchDir))
            {
                try { Directory.Delete(staleWorkbenchDir, true); } catch { }
            }
        }

        // ── Private helpers ──────────────────────────────────────────

        private static string ResolveRoot()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile, Encoding.UTF8);
                    var path = ExtractJsonString(json, "user_data_path");
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
            }
            catch { }

            return DefaultRoot;
        }

        private static void WriteConfigJson(string userDataPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigFile);
                if (dir != null) Directory.CreateDirectory(dir);

                // Preserve any existing keys and only update user_data_path
                string existing = "";
                if (File.Exists(ConfigFile))
                    existing = File.ReadAllText(ConfigFile, Encoding.UTF8);

                var escaped = userDataPath
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"");

                string updated;
                var key = "\"user_data_path\"";
                var idx = existing.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Replace existing value
                    var valStart = existing.IndexOf('"', idx + key.Length + 1);
                    var valEnd   = existing.IndexOf('"', valStart + 1);
                    if (valStart >= 0 && valEnd > valStart)
                        updated = existing.Substring(0, valStart + 1) + escaped + existing.Substring(valEnd);
                    else
                        updated = "{\n  \"user_data_path\": \"" + escaped + "\"\n}";
                }
                else
                {
                    // No existing entry — write minimal JSON
                    updated = "{\n  \"user_data_path\": \"" + escaped + "\"\n}";
                }

                File.WriteAllText(ConfigFile, updated, Encoding.UTF8);
            }
            catch { }
        }

        private static string ExtractJsonString(string json, string key)
        {
            var searchKey = "\"" + key + "\"";
            var idx = json.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var valStart = json.IndexOf('"', idx + searchKey.Length + 1);
            if (valStart < 0) return null;

            var valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return null;

            return json.Substring(valStart + 1, valEnd - valStart - 1)
                       .Replace("\\\\", "\\")
                       .Replace("\\\"", "\"");
        }

        private static void MigrateFile(string src, string dst)
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst);
            }
            catch { }
        }

        private static void MigrateDirectory(string srcDir, string dstDir)
        {
            if (!Directory.Exists(srcDir)) return;
            try
            {
                Directory.CreateDirectory(dstDir);
                foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                {
                    var rel = file.Substring(srcDir.Length)
                                  .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var dst = Path.Combine(dstDir, rel);
                    if (!File.Exists(dst))
                    {
                        var dstParent = Path.GetDirectoryName(dst);
                        if (dstParent != null) Directory.CreateDirectory(dstParent);
                        File.Copy(file, dst);
                    }
                }
            }
            catch { }
        }
    }
}
