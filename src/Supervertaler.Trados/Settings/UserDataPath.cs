using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // ── Memory banks (multi-bank layout) ─────────────────────────
        //
        // The Supervertaler Assistant supports several memory banks side by side,
        // each one a self-contained Obsidian-compatible vault. The on-disk layout is:
        //
        //     <Root>/memory-banks/<bank-name>/
        //
        // where <bank-name> is a filesystem-safe identifier (lowercase letters,
        // digits, hyphens, underscores). The Python Supervertaler Assistant uses
        // the same layout and the same naming rules, so banks created on either
        // side are immediately visible to the other.
        //
        // Backward compatibility:
        //   * Legacy installations have a single-bank layout at one of:
        //         <Root>/memory-bank/     (v1 rename target)
        //         <Root>/supermemory/     (original "SuperMemory" name)
        //     These are detected via <see cref="HasLegacySingleBank"/> and surfaced
        //     by the first-run migration dialog, which moves the whole folder into
        //     <Root>/memory-banks/<user-chosen-name>/ on the user's first session
        //     with a multi-bank-aware build.
        //
        //   * The obsolete single-bank property <see cref="MemoryBankDir"/> still
        //     exists so that any out-of-tree callers keep compiling during the
        //     transition. New code must use <see cref="GetMemoryBankDir"/> with an
        //     explicit bank name (normally <c>AiSettings.ActiveMemoryBankName</c>).

        /// <summary>Default bank name created on fresh installs with no legacy folder.</summary>
        public const string DefaultMemoryBankName = "default";

        /// <summary>
        /// Full spec-standard folder skeleton created inside every freshly made
        /// memory bank. Mirrors <c>SKELETON_FOLDERS</c> in the Python
        /// <c>supervertaler_assistant.memory_bank</c> module exactly — deviating
        /// would silently break cross-product compatibility because banks are
        /// shared between Workbench, the Python Assistant and this plugin via
        /// the same <c>memory-banks/</c> root.
        /// </summary>
        public static readonly string[] SkeletonFolders = new[]
        {
            "00_INBOX",
            "01_CLIENTS",
            "02_TERMINOLOGY",
            "03_DOMAINS",
            "04_STYLE",
            "05_INDICES",
            "06_TEMPLATES",
        };

        /// <summary>
        /// Root folder containing all memory banks: <c>&lt;Root&gt;/memory-banks/</c>.
        /// Individual banks live in subfolders named after their sanitized bank name.
        /// </summary>
        public static string MemoryBanksRoot => Path.Combine(Root, "memory-banks");

        /// <summary>
        /// Resolves the on-disk path for a specific memory bank. The returned path
        /// may or may not exist — callers should check with <see cref="Directory.Exists"/>
        /// and surface a user-facing message if it does not.
        /// </summary>
        /// <param name="bankName">
        /// Bank identifier. If null, empty or whitespace, falls back to
        /// <see cref="DefaultMemoryBankName"/>. The name is sanitized via
        /// <see cref="SanitizeBankName"/> to avoid accidental path traversal.
        /// </param>
        public static string GetMemoryBankDir(string bankName)
        {
            var safe = SanitizeBankName(bankName);
            if (string.IsNullOrEmpty(safe))
                safe = DefaultMemoryBankName;
            return Path.Combine(MemoryBanksRoot, safe);
        }

        /// <summary>
        /// Enumerates the names of memory banks currently present under
        /// <see cref="MemoryBanksRoot"/>. Returns an empty list if the root does not
        /// exist yet. The list is sorted alphabetically (case-insensitive) so the
        /// toolbar dropdown shows banks in a stable order.
        /// </summary>
        public static List<string> ListMemoryBanks()
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(MemoryBanksRoot))
                    return result;

                foreach (var dir in Directory.GetDirectories(MemoryBanksRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;
                    // Skip hidden/system folders (e.g. .trash created by Obsidian)
                    if (name.StartsWith(".")) continue;
                    result.Add(name);
                }
            }
            catch
            {
                // Non-fatal — return whatever we managed to enumerate
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Normalises a user-typed bank name into a filesystem-safe identifier.
        /// Mirrors the Python Assistant's rules exactly: converts to lowercase,
        /// replaces whitespace with hyphens, and strips any character that is not
        /// a lowercase letter, digit, hyphen or underscore. Returns an empty string
        /// if nothing valid remains.
        /// </summary>
        public static string SanitizeBankName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) && ch < 128)
                    sb.Append(ch);
                else if (ch == '-' || ch == '_')
                    sb.Append(ch);
                else if (char.IsWhiteSpace(ch))
                    sb.Append('-');
                // Everything else (punctuation, non-ASCII letters, …) is dropped.
            }

            // Collapse runs of hyphens/underscores and trim them from the ends.
            var cleaned = sb.ToString().Trim('-', '_');
            return cleaned;
        }

        /// <summary>
        /// Path to the legacy single-bank folder, if one exists. Checks both the
        /// v1 rename target (<c>memory-bank/</c>) and the original
        /// <c>supermemory/</c> name. Returns null if neither is present.
        /// </summary>
        public static string LegacySingleBankPath
        {
            get
            {
                var v1 = Path.Combine(Root, "memory-bank");
                if (Directory.Exists(v1)) return v1;

                var v0 = Path.Combine(Root, "supermemory");
                if (Directory.Exists(v0)) return v0;

                return null;
            }
        }

        /// <summary>True when a legacy single-bank folder is present on disk.</summary>
        public static bool HasLegacySingleBank => LegacySingleBankPath != null;

        /// <summary>
        /// True when the plugin should prompt the user to name their existing
        /// single-bank vault and move it into the new multi-bank layout. This is
        /// only the case when a legacy folder exists AND the multi-bank root does
        /// not (to avoid asking again if the user already migrated from Python).
        /// </summary>
        public static bool NeedsLegacyBankMigration =>
            HasLegacySingleBank && !Directory.Exists(MemoryBanksRoot);

        /// <summary>
        /// Moves the legacy single-bank folder into the new multi-bank layout at
        /// <c>&lt;Root&gt;/memory-banks/&lt;newName&gt;/</c>. The operation is atomic
        /// from the user's perspective: on success the legacy folder no longer
        /// exists and <see cref="ListMemoryBanks"/> includes the new name. On
        /// failure the legacy folder is left untouched and <paramref name="error"/>
        /// describes what went wrong.
        /// </summary>
        public static bool TryMigrateLegacySingleBank(string newName, out string error)
        {
            error = null;
            var src = LegacySingleBankPath;
            if (src == null)
            {
                error = "No legacy memory-bank folder was found to migrate.";
                return false;
            }

            var safeName = SanitizeBankName(newName);
            if (string.IsNullOrEmpty(safeName))
            {
                error = "The name must contain at least one lowercase letter, digit, hyphen or underscore.";
                return false;
            }

            var dst = GetMemoryBankDir(safeName);
            if (Directory.Exists(dst))
            {
                error = "A memory bank named '" + safeName + "' already exists at:\n  " + dst;
                return false;
            }

            try
            {
                Directory.CreateDirectory(MemoryBanksRoot);
                // Directory.Move fails across volumes, but src and dst live under the
                // same Root so this is always a plain rename on the same volume.
                Directory.Move(src, dst);
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not move\n  " + src + "\nto\n  " + dst + "\n\n" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Creates a fresh memory bank at <c>&lt;Root&gt;/memory-banks/&lt;name&gt;/</c>
        /// with the full <see cref="SkeletonFolders"/> layout. The user-supplied
        /// name is sanitised via <see cref="SanitizeBankName"/>, and the resulting
        /// identifier is returned via <paramref name="sanitisedName"/> so the
        /// caller can pre-select it in the toolbar dropdown afterwards.
        /// </summary>
        /// <param name="rawName">Name typed by the user; may contain spaces, mixed case, etc.</param>
        /// <param name="sanitisedName">
        /// On success, the filesystem-safe identifier actually used for the
        /// folder name. On failure, an empty string.
        /// </param>
        /// <param name="error">Human-readable error message on failure, otherwise null.</param>
        /// <returns>True if the bank folder and its skeleton were created; false otherwise.</returns>
        public static bool TryCreateMemoryBank(string rawName, out string sanitisedName, out string error)
        {
            sanitisedName = string.Empty;
            error = null;

            var safeName = SanitizeBankName(rawName);
            if (string.IsNullOrEmpty(safeName))
            {
                error = "The name must contain at least one lowercase letter, digit, hyphen or underscore.";
                return false;
            }

            var target = GetMemoryBankDir(safeName);
            if (Directory.Exists(target))
            {
                error = "A memory bank named '" + safeName + "' already exists at:\n  " + target;
                return false;
            }

            try
            {
                Directory.CreateDirectory(MemoryBanksRoot);
                Directory.CreateDirectory(target);
                foreach (var folder in SkeletonFolders)
                {
                    Directory.CreateDirectory(Path.Combine(target, folder));
                }

                // Populate 06_TEMPLATES with the bundled template files so the
                // new bank is immediately usable by Process Inbox, Health Check,
                // and the Obsidian-side query / translate-with-KB helpers.
                // Failures here do not roll the bank back — the skeleton is the
                // minimum viable state, and missing templates can be healed later
                // via TryWriteMissingMemoryBankTemplates.
                try
                {
                    string writeError;
                    WriteMemoryBankTemplates(target, overwrite: false, out writeError);
                }
                catch { }

                sanitisedName = safeName;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not create memory bank at\n  " + target + "\n\n" + ex.Message;
                return false;
            }
        }

        // ── Memory bank templates (embedded resources) ───────────────────

        /// <summary>
        /// Logical-name prefix used for memory bank template embedded resources.
        /// Defined in <c>Supervertaler.Trados.csproj</c> via the
        /// <c>&lt;LogicalName&gt;</c> metadata of the <c>EmbeddedResource</c>
        /// glob under <c>Resources/memory-bank-templates/</c>. A distinct
        /// prefix is used so that one of the bundled templates can contain
        /// spaces and parentheses in its filename without interfering with
        /// the normal assembly-qualified resource naming.
        /// </summary>
        private const string TemplateResourcePrefix = "MemoryBankTemplate.";

        /// <summary>
        /// Canonical template filenames that downstream features depend on.
        /// <see cref="GetMissingCanonicalTemplates"/> uses this list to decide
        /// whether an existing bank needs its <c>06_TEMPLATES/</c> folder healed.
        /// The bundled resource set may contain additional (non-canonical) files
        /// as Obsidian-side helpers; those are still written on fresh create but
        /// their absence on an existing bank does not trigger a heal prompt.
        /// </summary>
        public static readonly string[] CanonicalTemplateFiles = new[]
        {
            "compile.md",  // required by Process Inbox
            "lint.md",     // required by Health Check
        };

        /// <summary>
        /// Streams every embedded memory bank template resource into the
        /// <c>06_TEMPLATES/</c> sub-folder of <paramref name="bankDir"/>.
        /// Creates the folder if it does not yet exist.
        /// </summary>
        /// <param name="bankDir">Absolute path to the bank root directory.</param>
        /// <param name="overwrite">
        /// When true, existing template files are replaced. When false
        /// (the default on both create and heal paths), files that already
        /// exist on disk are left untouched — user edits to templates are
        /// per-bank and must not be clobbered by a plugin upgrade.
        /// </param>
        /// <param name="error">
        /// Human-readable error on total failure (e.g. cannot create the
        /// <c>06_TEMPLATES/</c> folder). Individual per-file write failures
        /// are swallowed so that one bad file does not prevent the rest
        /// from being written.
        /// </param>
        /// <returns>
        /// The number of files successfully written. Zero is not an error
        /// when <paramref name="overwrite"/> is false and every file already
        /// exists on disk.
        /// </returns>
        public static int WriteMemoryBankTemplates(string bankDir, bool overwrite, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(bankDir))
            {
                error = "Bank directory path is empty.";
                return 0;
            }

            var templatesDir = Path.Combine(bankDir, "06_TEMPLATES");
            try
            {
                Directory.CreateDirectory(templatesDir);
            }
            catch (Exception ex)
            {
                error = "Could not create templates folder at\n  " + templatesDir + "\n\n" + ex.Message;
                return 0;
            }

            int written = 0;
            var asm = typeof(UserDataPath).Assembly;
            var allResources = asm.GetManifestResourceNames();

            foreach (var resource in allResources)
            {
                if (!resource.StartsWith(TemplateResourcePrefix, StringComparison.Ordinal))
                    continue;

                var fileName = resource.Substring(TemplateResourcePrefix.Length);
                var destPath = Path.Combine(templatesDir, fileName);

                if (File.Exists(destPath) && !overwrite)
                    continue;

                try
                {
                    using (var src = asm.GetManifestResourceStream(resource))
                    {
                        if (src == null) continue;
                        using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                        {
                            src.CopyTo(dst);
                        }
                    }
                    written++;
                }
                catch
                {
                    // Skip this file — individual failures must not block the rest.
                }
            }

            return written;
        }

        /// <summary>
        /// Inspects <paramref name="bankDir"/> and returns the names of any
        /// <see cref="CanonicalTemplateFiles"/> that are missing from its
        /// <c>06_TEMPLATES/</c> sub-folder. An empty list means the bank is
        /// structurally sound; a non-empty list is the payload for the
        /// heal-on-activation prompt in the AI Assistant view part.
        /// </summary>
        public static List<string> GetMissingCanonicalTemplates(string bankDir)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(bankDir)) return missing;

            var templatesDir = Path.Combine(bankDir, "06_TEMPLATES");
            foreach (var name in CanonicalTemplateFiles)
            {
                var path = Path.Combine(templatesDir, name);
                if (!File.Exists(path))
                    missing.Add(name);
            }
            return missing;
        }

        /// <summary>
        /// Legacy single-bank property kept so out-of-tree callers compile during
        /// the multi-bank transition. New code must call <see cref="GetMemoryBankDir"/>
        /// with an explicit bank name (normally <c>AiSettings.ActiveMemoryBankName</c>).
        /// This getter returns the legacy path when one exists, otherwise the
        /// default bank under the new layout.
        /// </summary>
        [Obsolete("Use GetMemoryBankDir(bankName) with AiSettings.ActiveMemoryBankName. This shim exists only for the multi-bank transition.")]
        public static string MemoryBankDir =>
            LegacySingleBankPath ?? GetMemoryBankDir(DefaultMemoryBankName);

        /// <summary>
        /// Legacy alias for <see cref="MemoryBankDir"/>. Kept so existing callers compile
        /// unchanged during the gradual SuperMemory → memory bank rename. New code should
        /// use <see cref="GetMemoryBankDir"/> with an explicit bank name.
        /// </summary>
        [Obsolete("Use GetMemoryBankDir(bankName) instead. This alias exists for the SuperMemory → memory bank rename transition.")]
#pragma warning disable CS0618
        public static string SuperMemoryDir => MemoryBankDir;
#pragma warning restore CS0618

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
