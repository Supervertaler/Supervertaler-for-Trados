using System;
using System.Collections.Generic;
using System.IO;
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

        // ─── Per-project termbase settings ──────────────────────────

        /// <summary>
        /// Path to the Supervertaler SQLite database for this project.
        /// </summary>
        [DataMember(Name = "termbasePath")]
        public string TermbasePath { get; set; } = "";

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
        /// for this project. Empty string means no project-specific override
        /// (falls back to the global SelectedPromptPath).
        /// </summary>
        [DataMember(Name = "activePromptPath")]
        public string ActivePromptPath { get; set; } = "";

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

                var json = File.ReadAllText(path, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ProjectSettings));
                    var ps = (ProjectSettings)serializer.ReadObject(stream);

                    // Null-safety for lists
                    if (ps.WriteTermbaseIds == null) ps.WriteTermbaseIds = new List<long>();
                    if (ps.DisabledTermbaseIds == null) ps.DisabledTermbaseIds = new List<long>();
                    if (ps.DisabledMultiTermIds == null) ps.DisabledMultiTermIds = new List<long>();
                    if (ps.DisabledAiTermbaseIds == null) ps.DisabledAiTermbaseIds = new List<long>();
                    if (ps.EnabledAiMultiTermIds == null) ps.EnabledAiMultiTermIds = new List<long>();

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
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Saves project-specific settings for the given .sdlproj path.
        /// Uses the project name from the settings object to create a human-readable filename.
        /// Cleans up old files with a different name for the same hash (e.g. after a project rename).
        /// </summary>
        public static void Save(string projectFilePath, ProjectSettings ps)
        {
            try
            {
                var key = GetProjectKey(projectFilePath);
                if (key == null) return;

                Directory.CreateDirectory(ProjectsDir);

                // Determine the target path using the project name
                var targetPath = GetProjectSettingsPath(projectFilePath, ps.ProjectName);
                if (targetPath == null) return;

                // Clean up old files for the same hash if the name has changed
                var existingFiles = Directory.GetFiles(ProjectsDir, key + "*.json");
                foreach (var oldFile in existingFiles)
                {
                    if (!string.Equals(oldFile, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(oldFile); } catch { }
                    }
                }

                // Serialise to JSON
                using (var stream = new MemoryStream())
                {
                    var serializerSettings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    var serializer = new DataContractJsonSerializer(typeof(ProjectSettings), serializerSettings);
                    serializer.WriteObject(stream, ps);

                    var json = Encoding.UTF8.GetString(stream.ToArray());

                    // Pretty-print the JSON for human readability
                    json = IndentJson(json);

                    File.WriteAllText(targetPath, json, Encoding.UTF8);
                }
            }
            catch
            {
                // Silently ignore save failures
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
