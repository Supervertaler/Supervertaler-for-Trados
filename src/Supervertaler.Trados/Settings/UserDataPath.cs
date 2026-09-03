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
    /// config pointer at %APPDATA%\Supervertaler\config.json – the same file Workbench
    /// reads and writes.
    ///
    /// Folder layout under the root:
    ///   prompt_library/     – prompt .md files shared between both products
    ///   resources/          – supervertaler.db (shared termbase, if present)
    ///   workbench/          – Supervertaler Workbench-specific data
    ///     settings/         – Workbench settings files
    ///   trados/
    ///     settings/         – Trados plugin settings
    ///       settings.json   – plugin preferences
    ///       license.json    – license activation state
    ///       chat_history.json – AI Assistant chat history
    ///     projects/         – per-project settings overlays
    ///
    /// Call <see cref="NeedsFirstRunSetup"/> before any path access to check whether the
    /// user has ever configured a data folder.  The first-run dialog calls
    /// <see cref="SetRoot"/> once to persist the chosen path and reset cached values.
    /// </summary>
    public static class UserDataPath
    {
        // Shared config pointer – same file used by Supervertaler Workbench
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
        ///
        /// Delegates to Supervertaler.Core rather than resolving it here: the
        /// memoQ plugin needs the same answer, and two implementations of "where
        /// does this user keep their data" is exactly the pair that would
        /// eventually disagree.
        /// </summary>
        public static string Root => SupervertalerPaths.Root;

        /// <summary>
        /// True when no config.json pointer exists yet (first run, no folder chosen).
        /// The caller should show <see cref="Controls.SetupDialog"/> in this case.
        /// </summary>
        public static bool NeedsFirstRunSetup => !File.Exists(ConfigFile);

        // ── Shared directories ───────────────────────────────────────

        /// <summary>Prompt .md files shared between Workbench and the Trados plugin.</summary>
        public static string PromptLibraryDir => SupervertalerPaths.PromptLibraryDir;

        /// <summary>Shared resources folder (supervertaler.db lives here).</summary>
        public static string ResourcesDir => SupervertalerPaths.ResourcesDir;

        /// <summary>
        /// Per-product runtime state for the Trados plugin (bridge handshake
        /// files, lockfiles, ephemeral state). Cleared on every plugin start
        /// and stop – nothing persistent should live here.
        /// </summary>
        public static string TradosRuntimeDir => Path.Combine(Root, "trados", "runtime");

        /// <summary>
        /// Handshake file written by <c>SupervertalerBridge</c> while the bridge
        /// is listening. Contains <c>port</c>, <c>token</c>, <c>pid</c>, and
        /// <c>startedAt</c> so external clients (notably Supervertaler
        /// Workbench's Sidekick Chat) can discover and authenticate to the
        /// bridge. Deleted on plugin shutdown; readers must validate
        /// <c>pid</c> liveness before trusting it.
        /// </summary>
        public static string SupervertalerBridgeFile => Path.Combine(TradosRuntimeDir, "bridge.json");

        /// <summary>
        /// One handshake file per live Studio process, so two Studio versions
        /// running side by side stay tellable apart. <see cref="SupervertalerBridgeFile"/>
        /// is still written (last writer wins, as before) for older MCP server
        /// exes and for Workbench's Sidekick Chat; new clients enumerate this
        /// folder instead and get <c>studioVersion</c> / <c>projectName</c> with
        /// each entry so they can name the instance they are talking to.
        /// See issue #72.
        /// </summary>
        public static string TradosInstancesDir => Path.Combine(TradosRuntimeDir, "instances");

        /// <summary>Handshake file for one Studio process: <c>bridge-&lt;pid&gt;.json</c>.</summary>
        public static string SupervertalerBridgeInstanceFile(int pid)
            => Path.Combine(TradosInstancesDir, "bridge-" + pid + ".json");

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
        /// <c>supervertaler_assistant.memory_bank</c> module exactly – deviating
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
        /// may or may not exist – callers should check with <see cref="Directory.Exists"/>
        /// and surface a user-facing message if it does not.
        /// </summary>
        /// <param name="bankName">
        /// Bank identifier. If null, empty or whitespace, falls back to
        /// <see cref="DefaultMemoryBankName"/>. The name is sanitized via
        /// <see cref="SanitizeBankName"/> to avoid accidental path traversal –
        /// except for the reserved shared bank, see <see cref="IsSharedBankName"/>.
        /// </param>
        public static string GetMemoryBankDir(string bankName)
        {
            // The shared overlay bank is the one name sanitisation must NOT
            // touch. Its leading underscore is exactly what SanitizeBankName
            // strips - deliberately, so nobody can create a colliding bank from
            // the New-bank dialog - but running an on-disk name through the
            // user-input rule turned "_shared" into "shared" and pointed every
            // caller at a folder that does not exist. Symptoms that fixing this
            // removes: _shared reporting 0 articles in list_supermemory_banks,
            // and selecting it in the toolbar silently emptying the active bank.
            if (IsSharedBankName(bankName))
                return Path.Combine(MemoryBanksRoot, MemoryBankReader.SharedBankName);

            var safe = SanitizeBankName(bankName);
            if (string.IsNullOrEmpty(safe))
                safe = DefaultMemoryBankName;
            return Path.Combine(MemoryBanksRoot, safe);
        }

        /// <summary>
        /// True when the name refers to the reserved shared bank
        /// (<see cref="MemoryBankReader.SharedBankName"/>), which is loaded
        /// alongside every other bank rather than being a bank of its own.
        /// Deliberately does NOT sanitize: the whole point is that this name
        /// survives the sanitiser unchanged.
        /// </summary>
        public static bool IsSharedBankName(string bankName)
        {
            return !string.IsNullOrWhiteSpace(bankName) &&
                   string.Equals(bankName.Trim(), MemoryBankReader.SharedBankName,
                                 StringComparison.OrdinalIgnoreCase);
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
                // Non-fatal – return whatever we managed to enumerate
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
                WriteNewBankSkeleton(target, safeName);

                sanitisedName = safeName;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not create memory bank at\n  " + target + "\n\n" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Renames a memory bank by moving its folder.
        ///
        /// <para><b>The caller must also update
        /// <c>AiSettings.ActiveMemoryBankName</c> if this bank was the active
        /// one.</b> That setting stores a NAME, so a rename leaves it pointing
        /// at a folder that no longer exists - and the reader treats a missing
        /// bank as an empty one, so SuperMemory would quietly contribute nothing
        /// to every prompt. Nothing would say so.</para>
        ///
        /// <para><c>_shared</c> cannot be renamed: the reader looks it up by
        /// that exact name to load it alongside the active bank.</para>
        /// </summary>
        public static bool TryRenameMemoryBank(string oldName, string rawNewName,
            out string sanitisedName, out string error)
        {
            sanitisedName = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(oldName))
            {
                error = "No memory bank was selected.";
                return false;
            }

            if (string.Equals(oldName, MemoryBankReader.SharedBankName,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The '" + MemoryBankReader.SharedBankName + "' bank cannot be renamed - "
                      + "it is loaded alongside every other bank by that exact name.";
                return false;
            }

            var safeName = SanitizeBankName(rawNewName);
            if (string.IsNullOrEmpty(safeName))
            {
                error = "The name must contain at least one lowercase letter, digit, hyphen or underscore.";
                return false;
            }

            if (string.Equals(safeName, oldName, StringComparison.Ordinal))
            {
                error = null;              // nothing to do, and not a failure
                sanitisedName = oldName;
                return true;
            }

            var source = GetMemoryBankDir(oldName);
            var target = GetMemoryBankDir(safeName);

            if (!Directory.Exists(source))
            {
                error = "That memory bank no longer exists at\n  " + source;
                return false;
            }

            // Case-only renames would fail the Exists check on Windows, so let
            // those through: Directory.Move handles them.
            if (Directory.Exists(target) &&
                !string.Equals(safeName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                error = "A memory bank named '" + safeName + "' already exists.";
                return false;
            }

            try
            {
                Directory.Move(source, target);
                sanitisedName = safeName;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not rename the memory bank.\n\n" + ex.Message
                      + "\n\nIf Obsidian or another program has a file open in it, close that first.";
                return false;
            }
        }

        /// <summary>
        /// Removes a memory bank by moving it into <c>memory-banks/.trash/</c>.
        ///
        /// <para>Moved rather than deleted, and deliberately not to the Recycle
        /// Bin. A bank is months of accumulated decisions; recovering it should
        /// not depend on the Recycle Bin being enabled for that drive, or on the
        /// user finding it there. <see cref="ListMemoryBanks"/> already skips
        /// dot-prefixed folders, so the bank disappears from every list while
        /// staying restorable by renaming the folder back.</para>
        ///
        /// <para>Timestamped, so deleting two banks of the same name does not
        /// destroy the first.</para>
        /// </summary>
        public static bool TryDeleteMemoryBank(string name, out string movedTo, out string error)
        {
            movedTo = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(name))
            {
                error = "No memory bank was selected.";
                return false;
            }

            if (string.Equals(name, MemoryBankReader.SharedBankName,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "The '" + MemoryBankReader.SharedBankName + "' bank cannot be deleted - "
                      + "it holds the defaults loaded alongside every other bank.";
                return false;
            }

            var source = GetMemoryBankDir(name);
            if (!Directory.Exists(source))
            {
                error = "That memory bank no longer exists at\n  " + source;
                return false;
            }

            try
            {
                var trashRoot = Path.Combine(MemoryBanksRoot, ".trash");
                Directory.CreateDirectory(trashRoot);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var target = Path.Combine(trashRoot, name + "-" + stamp);

                Directory.Move(source, target);
                movedTo = target;
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not remove the memory bank.\n\n" + ex.Message
                      + "\n\nIf Obsidian or another program has a file open in it, close that first.";
                return false;
            }
        }

        /// <summary>
        /// Writes a fresh bank's three files plus <c>reference/</c>.
        ///
        /// Starter content matters more than it looks: an empty bank teaches the
        /// user nothing about the shape, and the shape is the whole design. Each
        /// file therefore arrives with its headings and a one-line explanation of
        /// what belongs in it. Failures are swallowed - a bank with a missing
        /// file still works, and the user can create it by hand.
        /// </summary>
        private static void WriteNewBankSkeleton(string bankDir, string bankName)
        {
            void Write(string fileName, string body)
            {
                try
                {
                    var path = Path.Combine(bankDir, fileName);
                    if (!File.Exists(path))
                        File.WriteAllText(path, body, new UTF8Encoding(false));
                }
                catch { }
            }

            Write("brief.md", Supervertaler.Core.MemoryBanks.SkeletonBody("brief.md", bankName));
            Write("terminology.md", Supervertaler.Core.MemoryBanks.SkeletonBody("terminology.md", bankName));
            Write("style.md", Supervertaler.Core.MemoryBanks.SkeletonBody("style.md", bankName));

            WriteReferenceReadme(bankDir);
        }


        /// <summary>Seeds <c>reference/README.md</c>. Never read by the prompt builder.</summary>
        private static void WriteReferenceReadme(string bankDir)
        {
            try
            {
                var refDir = Path.Combine(bankDir, MemoryBankReader.ReferenceFolder);
                Directory.CreateDirectory(refDir);
                var readme = Path.Combine(refDir, "README.md");
                if (!File.Exists(readme))
                {
                    File.WriteAllText(readme,
                        "# reference/\r\n\r\n" +
                        "Source material, kept **unmodified**: client style guides, PDFs,\r\n" +
                        "glossaries, tracked-changes harvests.\r\n\r\n" +
                        "Everything in brief.md, terminology.md and style.md is derived from\r\n" +
                        "what is in here. Keeping the original is what lets you check a rule\r\n" +
                        "that looks wrong - and find out whether it was mis-derived or the\r\n" +
                        "source really does say that.\r\n\r\n" +
                        "Nothing reads this folder automatically. It is the audit trail, not\r\n" +
                        "an inbox.\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        // ── Legacy (pre-2026-08-08) bank layout ──────────────────────────
        //
        // Banks used to be a seven-folder wiki with one file per fact. The new
        // reader only looks for brief/terminology/style at the bank root, so an
        // unconverted bank contributes NOTHING to a prompt - and would do so
        // silently, which is the one outcome worth engineering against. Detect
        // it, tell the user, offer to convert.

        /// <summary>
        /// True when the folder looks like an old seven-folder bank that has not
        /// been converted: it has at least one legacy content folder and none of
        /// the three new files.
        /// </summary>
        public static bool IsLegacyBankLayout(string bankDir)
        {
            try
            {
                if (!Directory.Exists(bankDir)) return false;
                if (MemoryBankReader.BankFiles.Any(
                        f => File.Exists(Path.Combine(bankDir, f))))
                    return false;

                return SkeletonFolders.Any(f => Directory.Exists(Path.Combine(bankDir, f)));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Names of banks under the root still on the legacy layout.</summary>
        public static List<string> ListLegacyBanks()
        {
            var result = new List<string>();
            foreach (var name in ListMemoryBanks())
            {
                if (IsLegacyBankLayout(GetMemoryBankDir(name)))
                    result.Add(name);
            }
            return result;
        }

        /// <summary>
        /// Folds a legacy bank into the three-file layout.
        ///
        /// Deliberately LOSSLESS AND DUMB: it concatenates each legacy folder's
        /// articles under the file they belong to, and does not try to distil 136
        /// term articles into a tidy table. Distilling needs judgement about which
        /// decisions still hold, and a machine that guesses wrong here produces
        /// exactly the confident-but-unreviewable content this redesign exists to
        /// end. The user prunes afterwards, with everything still in front of them.
        ///
        /// Nothing is deleted: the legacy folders are moved into
        /// <c>reference/_legacy/</c>, so a bad conversion can be inspected and
        /// redone by hand.
        /// </summary>
        public static bool TryConvertLegacyBank(string bankDir, out string error, out int articlesFolded)
        {
            error = null;
            articlesFolded = 0;

            try
            {
                if (!IsLegacyBankLayout(bankDir))
                {
                    error = "This bank is not on the legacy layout (or has already been converted).";
                    return false;
                }

                var bankName = new DirectoryInfo(bankDir).Name;

                // legacy folder -> target file. 05_INDICES and 06_TEMPLATES are
                // dropped: indices were generated FROM the articles, and templates
                // were prompts for the automation that no longer exists. Both were
                // already never read into a prompt.
                var map = new[]
                {
                    new { Folder = "01_CLIENTS",     Target = MemoryBankReader.BriefFile,       Heading = "Client notes" },
                    new { Folder = "02_TERMINOLOGY", Target = MemoryBankReader.TerminologyFile, Heading = "Terminology" },
                    new { Folder = "04_STYLE",       Target = MemoryBankReader.StyleFile,       Heading = "Style" },
                    new { Folder = "03_DOMAINS",     Target = MemoryBankReader.StyleFile,       Heading = "Domain knowledge" },
                };

                var buffers = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);

                foreach (var m in map)
                {
                    var dir = Path.Combine(bankDir, m.Folder);
                    if (!Directory.Exists(dir)) continue;

                    StringBuilder sb;
                    if (!buffers.TryGetValue(m.Target, out sb))
                    {
                        sb = new StringBuilder();
                        sb.AppendLine("# " + Path.GetFileNameWithoutExtension(m.Target) + " - " + bankName);
                        sb.AppendLine();
                        sb.AppendLine("> Converted from the old folder layout. Everything is preserved");
                        sb.AppendLine("> below, unedited. Prune it and, for terminology, rewrite it as a");
                        sb.AppendLine("> table - a table is what makes a wrong row findable.");
                        sb.AppendLine("> The originals are in `reference/_legacy/`.");
                        sb.AppendLine();
                        buffers[m.Target] = sb;
                    }

                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                    sb.AppendLine("## " + m.Heading + " (from " + m.Folder + ")");
                    sb.AppendLine();

                    foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories)
                                                  .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (MemoryBankReader.IsIgnoredSidecar(file)) continue;
                        var rel = file.Substring(bankDir.Length).TrimStart('\\', '/');
                        if (rel.IndexOf("_archive", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        string body;
                        try { body = File.ReadAllText(file); } catch { continue; }
                        if (string.IsNullOrWhiteSpace(body)) continue;

                        var title = Path.GetFileNameWithoutExtension(file);
                        sb.AppendLine("### " + title);
                        sb.AppendLine();
                        sb.AppendLine(FoldArticleBody(StripFrontmatterBlock(body), title));
                        sb.AppendLine();
                        articlesFolded++;
                    }
                }

                foreach (var kv in buffers)
                {
                    var path = Path.Combine(bankDir, kv.Key);
                    if (File.Exists(path)) continue;
                    File.WriteAllText(path, kv.Value.ToString(), new UTF8Encoding(false));
                }

                // Anything the map did not cover still needs a home, and the raw
                // inbox is source material by definition.
                var legacyRoot = Path.Combine(bankDir, MemoryBankReader.ReferenceFolder, "_legacy");
                Directory.CreateDirectory(legacyRoot);
                foreach (var folder in SkeletonFolders)
                {
                    var src = Path.Combine(bankDir, folder);
                    if (!Directory.Exists(src)) continue;
                    var dst = Path.Combine(legacyRoot, folder);
                    try { if (!Directory.Exists(dst)) Directory.Move(src, dst); } catch { }
                }

                // A bank with no brief at all still reads as "not a bank".
                var briefPath = Path.Combine(bankDir, MemoryBankReader.BriefFile);
                if (!File.Exists(briefPath))
                {
                    File.WriteAllText(briefPath,
                        "# " + bankName + "\r\n\r\n" +
                        "Converted from the old folder layout; the previous bank had no\r\n" +
                        "client profile to fold in. Describe the client here.\r\n",
                        new UTF8Encoding(false));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Could not convert the bank at\n  " + bankDir + "\n\n" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Prepares one article's body to sit UNDER an <c>###</c> heading.
        ///
        /// Three things have to happen or the merged file is worse than the
        /// folders it replaced:
        ///
        /// 1. Headings are demoted. An article written as a standalone note uses
        ///    <c>##</c> for its own sections; dropped verbatim under an <c>###</c>
        ///    term heading, those sections outrank the term that contains them, so
        ///    every "Preferred translation" reads as a sibling of the whole
        ///    terminology section rather than as part of one term. Folding and
        ///    outlining then show nonsense - and being scannable is the entire
        ///    reason for merging.
        /// 2. A leading title that just repeats the filename is dropped, since
        ///    the <c>###</c> heading already says it.
        /// 3. <c>[[wikilinks]]</c> are flattened. They pointed at sibling FILES
        ///    that no longer exist once everything is in one document; leaving
        ///    them is a promise of a link that goes nowhere.
        /// </summary>
        private static string FoldArticleBody(string body, string title)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";

            var lines = body.Replace("\r\n", "\n").Split('\n').ToList();

            // Drop a leading H1 that merely repeats the article title.
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var t = lines[i].TrimStart();
                if (t.StartsWith("#", StringComparison.Ordinal))
                {
                    var heading = t.TrimStart('#').Trim();
                    if (string.Equals(heading, title, StringComparison.OrdinalIgnoreCase))
                        lines.RemoveAt(i);
                }
                break;
            }

            // Shift by whatever puts the article's TOP heading at level 4, i.e.
            // one below the ### it is filed under. A fixed offset cannot do this:
            // articles that lead with ## and articles that lead with # would land
            // at different depths, and shifting ## by three leaves a hole at
            // level 4 that makes an outline view show a gap where the term's
            // first section should be.
            int topLevel = int.MaxValue;
            bool scanFence = false;
            foreach (var raw in lines)
            {
                var t = raw.TrimStart();
                if (t.StartsWith("```", StringComparison.Ordinal)) { scanFence = !scanFence; continue; }
                if (scanFence || !t.StartsWith("#", StringComparison.Ordinal)) continue;
                int n = 0;
                while (n < t.Length && t[n] == '#') n++;
                if (n < t.Length && t[n] == ' ' && n < topLevel) topLevel = n;
            }
            int shift = topLevel == int.MaxValue ? 0 : Math.Max(0, 4 - topLevel);

            var sb = new StringBuilder();
            bool inFence = false;
            foreach (var raw in lines)
            {
                var line = raw;

                // Never rewrite inside a code fence.
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    inFence = !inFence;

                if (!inFence)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        int hashes = 0;
                        while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
                        if (hashes < trimmed.Length && trimmed[hashes] == ' ')
                        {
                            var demoted = Math.Min(6, hashes + shift);
                            line = new string('#', demoted) + trimmed.Substring(hashes);
                        }
                    }

                    line = WikiLinkPattern.Replace(line, m => m.Groups[1].Value);
                }

                sb.AppendLine(line);
            }

            return sb.ToString().Trim();
        }

        private static readonly System.Text.RegularExpressions.Regex WikiLinkPattern =
            new System.Text.RegularExpressions.Regex(@"\[\[([^\]|]+)(?:\|[^\]]*)?\]\]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Removes a leading YAML frontmatter block, including the malformed
        /// "fenced frontmatter" variant (a ```yaml / ```markdown fence wrapping
        /// the ---...--- block) that an earlier generation of the automation
        /// produced in about 15% of articles.
        /// </summary>
        private static string StripFrontmatterBlock(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var s = text.TrimStart('﻿', ' ', '\r', '\n', '\t');

            if (s.StartsWith("```", StringComparison.Ordinal))
            {
                var endFence = s.IndexOf("```", 3, StringComparison.Ordinal);
                if (endFence > 0)
                    s = s.Substring(endFence + 3).TrimStart('\r', '\n');
            }

            if (s.StartsWith("---", StringComparison.Ordinal))
            {
                var idx = s.IndexOf("\n---", StringComparison.Ordinal);
                if (idx > 0)
                {
                    var after = s.IndexOf('\n', idx + 1);
                    if (after > 0) s = s.Substring(after + 1);
                }
            }

            return s;
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
        /// exist on disk are left untouched – user edits to templates are
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
                    // Skip this file – individual failures must not block the rest.
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

        /// <summary>Folder where cleared chat sessions are archived (one JSON file per clear).</summary>
        public static string ChatArchiveDir => Path.Combine(TradosSettingsDir, "chat_archive");

        /// <summary>Returns a timestamped archive path for the current moment.</summary>
        public static string ChatArchiveFilePath(DateTime timestamp) =>
            Path.Combine(ChatArchiveDir, $"chat_{timestamp:yyyy-MM-dd_HH-mm-ss}.json");

        /// <summary>Folder containing per-project settings overlays.</summary>
        public static string ProjectsDir => Path.Combine(TradosDir, "projects");

        /// <summary>
        /// Folder where in-progress batch translation backups are written as TMX files.
        /// One .tmx file is created per batch translate run. If Trados crashes mid-run,
        /// the last-written TMX can be imported into a TM to recover the translations.
        /// </summary>
        public static string BatchBackupsDir => Path.Combine(TradosDir, "batch_backups");

        /// <summary>
        /// Returns a timestamped TMX backup path for a batch translate run.
        /// The project name is embedded in the filename for easy identification.
        /// </summary>
        public static string BatchBackupFilePath(DateTime timestamp, string projectName = null)
        {
            var safe = string.IsNullOrEmpty(projectName)
                ? ""
                : "_" + string.Concat(projectName.Split(Path.GetInvalidFileNameChars()));
            // Trim to keep the filename reasonable
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            return Path.Combine(BatchBackupsDir,
                $"batch_{timestamp:yyyy-MM-dd_HH-mm-ss}{safe}.tmx");
        }

        /// <summary>Folder holding the persistent token-usage ledger (JSONL files).</summary>
        public static string UsageDir => Path.Combine(TradosDir, "usage");

        /// <summary>
        /// Monthly-rotated usage ledger path, e.g. usage-2026-06.jsonl. One JSON
        /// object per AI call is appended. Plain JSONL so it opens in a spreadsheet
        /// or is parsed with a script. Pass a UTC timestamp.
        /// </summary>
        public static string UsageLogFilePath(DateTime timestampUtc) =>
            Path.Combine(UsageDir, $"usage-{timestampUtc:yyyy-MM}.jsonl");

        // ── Configuration ────────────────────────────────────────────

        /// <summary>
        /// Persists <paramref name="path"/> as "user_data_path" in the shared config.json
        /// and resets the cached root so subsequent accesses use the new value.
        /// </summary>
        public static void SetRoot(string path)
        {
            _root = path;
            SupervertalerPaths.Set(path);
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
                    // Non-fatal – legacy files remain in place as a fallback
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
                // Nothing to migrate – probably a fresh install.  Write the flag
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
                // Non-fatal – old files remain usable at the old paths
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>
        /// Removes the legacy plugin-only settings directory at
        /// <c>%LocalAppData%\Supervertaler.Trados\</c> after the one-time
        /// migration has completed (signalled by the <c>.migrated</c> flag
        /// inside the new Trados data directory).
        ///
        /// Earlier versions also tried to remove <c>%LocalAppData%\Supervertaler\</c>
        /// on every startup on the assumption that it was a stale Workbench
        /// artifact. That deletion was ungated – any user (or future-us) who
        /// happened to have data there would lose it on every Trados start.
        /// Removed in v4.19.55; the directory is now left untouched.
        /// </summary>
        private static void CleanupLegacyFolders()
        {
            var flagFile = Path.Combine(TradosDir, ".migrated");

            if (File.Exists(flagFile) && Directory.Exists(LegacyDir))
            {
                try { Directory.Delete(LegacyDir, true); } catch { }
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
                    // No existing entry – write minimal JSON
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
