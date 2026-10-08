using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Per-project settings overlay. Stored in a separate JSON file per Trados project
    /// at {SharedRoot}/trados/projects/{key} - {name}.json (resolved via UserDataPath).
    /// Only contains settings that vary between projects (termbase path, enabled/disabled
    /// termbases, write targets, etc.). Global settings (API keys, UI prefs) stay in
    /// the main settings.json.
    /// </summary>
    [DataContract]
    public class ProjectSettings
    {
        private static string ProjectsDir => UserDataPath.ProjectsDir;

        // ─── Human-readable metadata ────────────────────────────────

        /// <summary>
        /// Full path to the .sdlproj file. Stored for human readability only –
        /// the file is looked up by hash key, not by this path.
        /// </summary>
        [DataMember(Name = "projectPath")]
        public string ProjectPath { get; set; } = "";

        /// <summary>
        /// Trados project name. Stored for human readability only.
        /// </summary>
        [DataMember(Name = "projectName")]
        public string ProjectName { get; set; } = "";

        /// <summary>
        /// Studio's own id for the project (its GUID), which renaming the project
        /// does not change. The file is keyed by the .sdlproj PATH, and renaming a
        /// project in Studio renames the .sdlproj, so without this a renamed
        /// project arrived as a stranger: every termbase off, no drawings folder,
        /// no prompt. <see cref="AdoptRenamed"/> finds the settings by this id.
        /// Empty in files written before 18/19.20.198 until the project is next
        /// opened.
        /// </summary>
        [DataMember(Name = "projectId")]
        public string ProjectId { get; set; } = "";

        /// <summary>
        /// Optional client / end-customer name for this project. Attributed to each
        /// token-usage record so the ledger can produce per-client cost reports.
        /// Free text; blank if unset.
        /// </summary>
        [DataMember(Name = "client")]
        public string Client { get; set; } = "";

        // ─── Per-project termbase settings ──────────────────────────

        /// <summary>
        /// Path to the Supervertaler SQLite database for this project.
        /// </summary>
        [DataMember(Name = "termbasePath")]
        public string TermbasePath { get; set; } = "";

        /// <summary>
        /// The SuperMemory bank this project uses. Empty means none has been
        /// chosen for it.
        ///
        /// <para>Per project because a bank feeds EVERY prompt: carrying the
        /// previous job's bank into a new client's project silently supplies the
        /// wrong terminology and style, and nothing on screen says so. Empty is
        /// deliberately not "inherit whatever was last active" - no bank is
        /// better than another client's bank.</para>
        /// </summary>
        [DataMember(Name = "memoryBankName")]
        public string MemoryBankName { get; set; } = "";

        /// <summary>
        /// IDs of termbases marked as write targets for this project.
        /// </summary>
        [DataMember(Name = "writeTermbaseIds")]
        public List<long> WriteTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// ID of the termbase marked as "Project" (pink highlighting) for this project.
        /// -1 means not set.
        /// </summary>
        [DataMember(Name = "projectTermbaseId")]
        public long ProjectTermbaseId { get; set; } = -1;

        /// <summary>
        /// IDs of Supervertaler termbases the user has disabled for this project.
        /// </summary>
        [DataMember(Name = "disabledTermbaseIds")]
        public List<long> DisabledTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// Synthetic IDs of MultiTerm termbases disabled for this project.
        /// </summary>
        [DataMember(Name = "disabledMultiTermIds")]
        public List<long> DisabledMultiTermIds { get; set; } = new List<long>();

        /// <summary>
        /// IDs of termbases excluded from AI context for this project.
        /// </summary>
        [DataMember(Name = "disabledAiTermbaseIds")]
        public List<long> DisabledAiTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// IDs of MultiTerm (.sdltb) termbases EXPLICITLY enabled for AI context for this
        /// project. MultiTerm termbases are opt-in (default off); see AiSettings.EnabledAiMultiTermIds.
        /// </summary>
        [DataMember(Name = "enabledAiMultiTermIds")]
        public List<long> EnabledAiMultiTermIds { get; set; } = new List<long>();

        /// <summary>
        /// True once the AI termbase selection has been explicitly initialised for this project.
        /// False (or absent in older project files) triggers a one-time migration that disables
        /// all termbases from AI context, matching the opt-in default for new projects.
        /// </summary>
        [DataMember(Name = "aiTermbaseIdsInitialized")]
        public bool AiTermbaseIdsInitialized { get; set; } = false;

        // ─── Per-project prompt settings ────────────────────────────

        /// <summary>
        /// Relative path (from prompt_library/) of the active translation prompt
        /// for this project. Empty string means the default prompt. It is applied
        /// to the global SelectedPromptPath at every project switch (#135), so the
        /// global value is always the open project's, never a previous one's.
        /// </summary>
        [DataMember(Name = "activePromptPath")]
        public string ActivePromptPath { get; set; } = "";

        /// <summary>The proofreading prompt for this project; empty means the default.</summary>
        [DataMember(Name = "activeProofreadPromptPath")]
        public string ActiveProofreadPromptPath { get; set; } = "";

        public string GetBatchPrompt(bool proofread) =>
            (proofread ? ActiveProofreadPromptPath : ActivePromptPath) ?? "";

        // ─── Reference drawings ─────────────────────────────────────

        /// <summary>
        /// Folder holding this project's reference drawings, as chosen by the
        /// user. Empty means this project has none, and the feature stays quiet.
        ///
        /// <para><b>Never inferred.</b> Studio projects live wherever the user
        /// saved them, under whatever folder names they prefer, so there is no
        /// layout to deduce. <c>ReferenceImages.Suggest</c> may propose a nearby
        /// folder in the settings UI, but only an explicit choice is stored here —
        /// a guessed folder can belong to a sibling job, and drawings from the
        /// wrong matter are worse than none, since the output still reads
        /// plausibly.</para>
        ///
        /// <para>Per project rather than global because drawings belong to a job,
        /// and per project rather than per bank because a bank can outlive the
        /// Studio project that produced it. The distilled <c>figures.md</c>
        /// written FROM these images is bank-scoped; the images themselves are
        /// not.</para>
        /// </summary>
        [DataMember(Name = "referenceImagesFolder")]
        public string ReferenceImagesFolder { get; set; } = "";

        // ─── Static helpers ─────────────────────────────────────────

        /// <summary>
        /// Computes a stable, filesystem-safe key from the .sdlproj path.
        /// Uses SHA256 truncated to 12 hex characters.
        /// </summary>
        public static string GetProjectKey(string projectFilePath)
        {
            if (string.IsNullOrEmpty(projectFilePath))
                return null;

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(projectFilePath.Trim().ToLowerInvariant()));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Sanitises a project name for use in a filename by removing characters
        /// that are illegal in Windows file paths.
        /// </summary>
        private static string SanitiseProjectName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (Array.IndexOf(invalid, c) < 0)
                    sb.Append(c);
            }
            // Trim trailing dots/spaces (Windows restriction)
            return sb.ToString().TrimEnd('.', ' ');
        }

        /// <summary>
        /// Returns the full path to the project settings file for the given project.
        /// Uses the format: {hash} - {projectName}.json for human readability.
        /// Falls back to finding by hash prefix if the exact file doesn't exist
        /// (handles migration from old hash-only filenames and project renames).
        /// </summary>
        private static string GetProjectSettingsPath(string projectFilePath, string projectName = null)
        {
            var key = GetProjectKey(projectFilePath);
            if (key == null) return null;

            // If we have a project name, build the readable filename
            if (!string.IsNullOrEmpty(projectName))
            {
                var safeName = SanitiseProjectName(projectName);
                if (!string.IsNullOrEmpty(safeName))
                {
                    var readablePath = Path.Combine(ProjectsDir, key + " - " + safeName + ".json");
                    if (File.Exists(readablePath))
                        return readablePath;
                }
            }

            // Search for any file starting with the hash key (handles old names + renames)
            if (Directory.Exists(ProjectsDir))
            {
                var matches = Directory.GetFiles(ProjectsDir, key + "*.json");
                if (matches.Length > 0)
                    return matches[0];
            }

            // No existing file found – return the readable path for new saves
            if (!string.IsNullOrEmpty(projectName))
            {
                var safeName = SanitiseProjectName(projectName);
                if (!string.IsNullOrEmpty(safeName))
                    return Path.Combine(ProjectsDir, key + " - " + safeName + ".json");
            }

            // Absolute fallback – hash only
            return Path.Combine(ProjectsDir, key + ".json");
        }

        /// <summary>
        /// Point every project that records <paramref name="oldName"/> at
        /// <paramref name="newName"/>, after that memory bank has been renamed.
        /// </summary>
        /// <remarks>
        /// A project records its bank by NAME, and the reader treats a missing
        /// bank as an empty one - so without this, renaming a bank leaves every
        /// project that used it contributing nothing to any prompt, with nothing
        /// on screen saying so. Same reasoning as carrying the rename across
        /// AiSettings.ActiveMemoryBankName, applied to the other place a bank
        /// name is stored.
        /// </remarks>
        public static void RenameMemoryBankEverywhere(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return;
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                if (!Directory.Exists(ProjectsDir)) return;

                foreach (var file in Directory.GetFiles(ProjectsDir, "*.json"))
                {
                    try
                    {
                        ProjectSettings ps;
                        var json = Supervertaler.Core.AtomicFile.ReadAllText(file);
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        {
                            var serializer = new DataContractJsonSerializer(typeof(ProjectSettings));
                            ps = (ProjectSettings)serializer.ReadObject(stream);
                        }

                        if (ps == null || string.IsNullOrEmpty(ps.ProjectPath)) continue;
                        if (!string.Equals(ps.MemoryBankName, oldName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        ps.MemoryBankName = newName;
                        Save(ps.ProjectPath, ps);
                    }
                    catch { /* one unreadable file must not stop the rest */ }
                }
            }
            catch { }
        }

        /// <summary>
        /// Checks whether project-specific settings exist for the given project.
        /// </summary>
        public static bool HasProjectSettings(string projectFilePath)
        {
            var path = GetProjectSettingsPath(projectFilePath);
            return path != null && File.Exists(path);
        }

        /// <summary>
        /// Loads project-specific settings for the given .sdlproj path.
        /// Returns null if no project settings file exists.
        /// </summary>
        public static ProjectSettings Load(string projectFilePath)
        {
            try
            {
                var path = GetProjectSettingsPath(projectFilePath);
                if (path == null || !File.Exists(path))
                    return null;

                var ps = ReadFile(path);
                if (ps == null) return null;

                // Migrate old hash-only filenames to readable format
                if (!string.IsNullOrEmpty(ps.ProjectName))
                {
                    var key = GetProjectKey(projectFilePath);
                    var safeName = SanitiseProjectName(ps.ProjectName);
                    if (key != null && !string.IsNullOrEmpty(safeName))
                    {
                        var readablePath = Path.Combine(ProjectsDir, key + " - " + safeName + ".json");
                        if (!string.Equals(path, readablePath, StringComparison.OrdinalIgnoreCase)
                            && !File.Exists(readablePath))
                        {
                            try { File.Move(path, readablePath); } catch { }
                        }
                    }
                }

                return ps;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// One settings file, or null when it cannot be read. A file locked for a
        /// moment - a backup or sync tool reading it - is tried again briefly; one
        /// that does not parse is not.
        /// </summary>
        private static ProjectSettings ReadFile(string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return Parse(Supervertaler.Core.AtomicFile.ReadAllText(path));
                }
                catch (IOException) when (attempt < 3 && File.Exists(path))
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static ProjectSettings Parse(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var ps = (ProjectSettings)new DataContractJsonSerializer(typeof(ProjectSettings)).ReadObject(stream);
                    if (ps == null) return null;

                    // The serializer runs no initialisers: a member absent from the
                    // file (every file older than the member) comes back null.
                    if (ps.WriteTermbaseIds == null) ps.WriteTermbaseIds = new List<long>();
                    if (ps.DisabledTermbaseIds == null) ps.DisabledTermbaseIds = new List<long>();
                    if (ps.DisabledMultiTermIds == null) ps.DisabledMultiTermIds = new List<long>();
                    if (ps.DisabledAiTermbaseIds == null) ps.DisabledAiTermbaseIds = new List<long>();
                    if (ps.EnabledAiMultiTermIds == null) ps.EnabledAiMultiTermIds = new List<long>();
                    if (ps.ProjectId == null) ps.ProjectId = "";
                    return ps;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Moves the settings of Studio project <paramref name="projectId"/> from
        /// the path it had to <paramref name="projectFilePath"/>, when Studio has
        /// renamed or moved it, and returns them. Null when there is nothing to
        /// move.
        ///
        /// <para>Only settings whose own .sdlproj is GONE are taken, from a drive
        /// that is there to look at: a project copied rather than renamed has the
        /// same id and its original still exists, and the original keeps its
        /// settings - the copy starts from the new-project defaults, as before. A
        /// file on a disconnected drive may belong to a project that is simply
        /// offline. Several candidates (renamed twice, the first move
        /// interrupted): the most recently written.</para>
        ///
        /// <para>The old file is deleted only once the new one reads back, so an
        /// interrupted move leaves both rather than neither. Scale: reads every
        /// file in the projects folder, but only for a path that has no settings
        /// yet - once per new or renamed project - and parses only files that
        /// contain the id.</para>
        /// </summary>
        public static ProjectSettings AdoptRenamed(string projectId, string projectFilePath, string projectName,
            [System.Runtime.CompilerServices.CallerMemberName] string via = "")
        {
            if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(projectFilePath)) return null;
            if (!Directory.Exists(ProjectsDir)) return null;

            ProjectSettings found = null;
            string foundFile = null;
            var foundTime = DateTime.MinValue;
            foreach (var file in Directory.GetFiles(ProjectsDir, "*.json"))
            {
                string text;
                try { text = Supervertaler.Core.AtomicFile.ReadAllText(file); }
                catch { continue; }
                if (text.IndexOf(projectId, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var ps = Parse(text);
                if (ps == null || !string.Equals(ps.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(ps.ProjectPath)
                    || string.Equals(ps.ProjectPath.Trim(), projectFilePath.Trim(), StringComparison.OrdinalIgnoreCase)
                    || File.Exists(ps.ProjectPath)
                    || !VolumeAvailable(ps.ProjectPath))
                    continue;

                var written = File.GetLastWriteTimeUtc(file);
                if (found != null && written <= foundTime) continue;
                found = ps;
                foundFile = file;
                foundTime = written;
            }
            if (found == null) return null;

            var previousPath = found.ProjectPath;
            found.ProjectPath = projectFilePath;
            if (!string.IsNullOrEmpty(projectName)) found.ProjectName = projectName;
            Save(projectFilePath, found, via);

            var moved = Load(projectFilePath);
            if (moved == null) return found;   // the write failed: use them this session, keep the old file
            try { File.Delete(foundFile); } catch { }

            try
            {
                Supervertaler.Trados.Core.DiagnosticLog.Log("Overlay", "settings of " + (found.ProjectName ?? "") + " carried over from "
                    + previousPath + ": the same Studio project, renamed or moved (" + via + ")");
            }
            catch { }
            return moved;
        }

        private static bool VolumeAvailable(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) && Directory.Exists(root);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Saves project-specific settings for the given .sdlproj path.
        /// Uses the project name from the settings object to create a human-readable filename.
        /// Cleans up old files with a different name for the same hash (e.g. after a project rename).
        ///
        /// <para>Never destroys a settings file it cannot read. Every file this
        /// write replaces either parses - and the new one supersedes it - or is
        /// kept beside it as <c>.unreadable-&lt;time&gt;</c>, which nothing reads: a
        /// damaged or locked file may still be the project's real settings, and
        /// defaults written over one is how a project loses its termbases. The
        /// write itself goes to a temporary file that is then swapped in, and the
        /// same project's file under an older name is deleted only after that, so
        /// a crash or a full disk leaves the previous file whole.</para>
        /// </summary>
        public static void Save(string projectFilePath, ProjectSettings ps,
            [System.Runtime.CompilerServices.CallerMemberName] string via = "")
        {
            try
            {
                var key = GetProjectKey(projectFilePath);
                if (key == null || ps == null) return;

                // #135: every overlay write is logged with its caller. The recorded
                // bank drifted between projects on 2026-09-18 and no writer owned up.
                try
                {
                    Supervertaler.Trados.Core.DiagnosticLog.Log("Overlay", "write " + key + " (" + (ps?.ProjectName ?? "") + ") via " + via
                        + ": bank='" + (ps?.MemoryBankName ?? "") + "' prompt='" + (ps?.ActivePromptPath ?? "")
                        + "' proofread='" + (ps?.ActiveProofreadPromptPath ?? "") + "'");
                }
                catch { }

                Directory.CreateDirectory(ProjectsDir);

                // Determine the target path using the project name
                var targetPath = GetProjectSettingsPath(projectFilePath, ps.ProjectName);
                if (targetPath == null) return;

                string json;
                using (var stream = new MemoryStream())
                {
                    var serializerSettings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    new DataContractJsonSerializer(typeof(ProjectSettings), serializerSettings).WriteObject(stream, ps);

                    // Pretty-print the JSON for human readability
                    json = IndentJson(Encoding.UTF8.GetString(stream.ToArray()));
                }

                var replaced = Directory.GetFiles(ProjectsDir, key + "*.json");
                foreach (var file in replaced)
                {
                    if (ReadFile(file) != null) continue;
                    // Throws when it cannot be moved either: the save is abandoned
                    // and the file left exactly as it was.
                    File.Move(file, file + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    try { Supervertaler.Trados.Core.DiagnosticLog.Log("Overlay", "kept an unreadable settings file aside: " + Path.GetFileName(file)); }
                    catch { }
                }

                // Swapped in by a rename that works while someone else - the other
                // Studio, a scanner, a sync tool - is reading the file, waiting
                // out a brief lock. File.Replace refused outright whenever the
                // file was open anywhere: 6 of 21 saves on 7 October 2026.
                // UTF-8 with a BOM, as File.WriteAllText wrote them before.
                var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
                if (Supervertaler.Core.AtomicFile.Write(targetPath, bytes, replaceExisting: true,
                        m => Supervertaler.Trados.Core.DiagnosticLog.Log("Overlay", m))
                    != Supervertaler.Core.AtomicFile.Outcome.Written)
                {
                    Supervertaler.Trados.Core.DiagnosticLog.Log("Overlay", "write FAILED for " + (ps.ProjectName ?? projectFilePath) + " via " + via + "; the previous settings are unchanged.");
                    return;
                }

                // Only now that the new file is in place: this project's file under an older name.
                foreach (var file in replaced)
                {
                    if (!string.Equals(file, targetPath, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Supervertaler.Trados.Core.DiagnosticLog.Log("Overlay", "write FAILED for " + (ps?.ProjectName ?? projectFilePath) + " via " + via + ": " + ex.Message); }
                catch { }
            }
        }

        /// <summary>
        /// Naïve JSON indenter – works for the simple flat structure of project settings
        /// without requiring an external JSON library.
        /// </summary>
        private static string IndentJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            bool escaped = false;

            foreach (char c in json)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    sb.Append(c);
                    continue;
                }

                if (inString)
                {
                    sb.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        sb.AppendLine();
                        indent--;
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        if (!char.IsWhiteSpace(c))
                            sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
