using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// Persisted settings for the Supervertaler for Trados plugin.
    /// Stored at %LocalAppData%\Supervertaler.Trados\settings.json.
    /// </summary>
    [DataContract]
    public class TermLensSettings
    {
        /// <summary>
        /// Full path to the settings JSON file on disk.
        /// Resolved through UserDataPath so it moves with the shared data folder.
        /// </summary>
        public static string SettingsFilePath => UserDataPath.SettingsFilePath;

        // Old settings path for auto-migration from TermLens
        private static readonly string OldSettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermLens", "settings.json");

        [DataMember(Name = "termbasePath")]
        public string TermbasePath { get; set; } = "";

        [DataMember(Name = "autoLoadOnStartup")]
        public bool AutoLoadOnStartup { get; set; } = true;

        /// <summary>
        /// IDs of termbases the user has disabled. Empty means all termbases are active.
        /// Stored as disabled-list so newly added termbases are active by default.
        /// </summary>
        [DataMember(Name = "disabledTermbaseIds")]
        public List<long> DisabledTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// DEPRECATED — kept for backward-compatible migration from settings that
        /// stored a single write target.  New code should use <see cref="WriteTermbaseIds"/>.
        /// </summary>
        [DataMember(Name = "writeTermbaseId")]
        public long WriteTermbaseId { get; set; } = -1;

        /// <summary>
        /// IDs of termbases that receive new terms via the Add Term / Quick-Add Term actions.
        /// Multiple termbases can be marked as Write targets — a new term is inserted into all of them.
        /// Empty list means no write termbases are configured.
        /// </summary>
        [DataMember(Name = "writeTermbaseIds")]
        public List<long> WriteTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// ID of the termbase the user has marked as the "Project" termbase.
        /// The project termbase is shown in pink; all others in blue.
        /// -1 means no project termbase is configured.
        /// </summary>
        [DataMember(Name = "projectTermbaseId")]
        public long ProjectTermbaseId { get; set; } = -1;

        /// <summary>
        /// Synthetic IDs of MultiTerm termbases the user has disabled (negative numbers).
        /// Empty means all detected MultiTerm termbases are active.
        /// </summary>
        [DataMember(Name = "disabledMultiTermIds")]
        public List<long> DisabledMultiTermIds { get; set; } = new List<long>();

        // ─── Term Picker dialog layout persistence ────────────────────
        [DataMember(Name = "termPickerWidth")]
        public int TermPickerWidth { get; set; }

        [DataMember(Name = "termPickerHeight")]
        public int TermPickerHeight { get; set; }

        [DataMember(Name = "termPickerColumnWidths")]
        public List<int> TermPickerColumnWidths { get; set; } = new List<int>();

        // ─── Settings form layout persistence ─────────────────────────
        [DataMember(Name = "settingsFormWidth")]
        public int SettingsFormWidth { get; set; }

        [DataMember(Name = "settingsFormHeight")]
        public int SettingsFormHeight { get; set; }

        // ─── Termbase Editor dialog layout persistence ──────────────
        [DataMember(Name = "termbaseEditorWidth")]
        public int TermbaseEditorWidth { get; set; }

        [DataMember(Name = "termbaseEditorHeight")]
        public int TermbaseEditorHeight { get; set; }

        // ─── Panel font size ─────────────────────────────────────────
        /// <summary>
        /// Font size (in points) for the TermLens panel. Default: 9pt.
        /// Adjustable via the A+/A- buttons in the panel header or the Settings dialog.
        /// </summary>
        [DataMember(Name = "panelFontSize")]
        public float PanelFontSize { get; set; } = 9f;

        /// <summary>
        /// Font size (in points) for the AI Assistant chat bubbles. Default: 9pt.
        /// Adjustable via the A+/A- buttons in the chat header.
        /// </summary>
        [DataMember(Name = "chatFontSize")]
        public float ChatFontSize { get; set; } = 9f;

        // ─── UI scale factor ──────────────────────────────────────────
        /// <summary>
        /// Global UI scale factor for all Supervertaler controls. Default: 1.0 (100%).
        /// Applied on top of Windows DPI scaling. Requires Trados restart to take full effect.
        /// </summary>
        [DataMember(Name = "uiScaleFactor")]
        public float UiScaleFactor { get; set; } = 1.0f;

        // ─── Term shortcut style ────────────────────────────────────────
        /// <summary>
        /// How Alt+digit shortcuts work for terms beyond 9.
        /// "sequential" = type Alt+4,5 for term 45 (timer-based, clean badges).
        /// "repeated"   = type Alt+5,5 for term 14 (no timer ambiguity, repeated-digit badges).
        /// Default: sequential.
        /// </summary>
        [DataMember(Name = "termShortcutStyle")]
        public string TermShortcutStyle { get; set; } = "sequential";

        /// <summary>
        /// Delay in milliseconds for the sequential chord timer.
        /// After the first digit, the system waits this long for a second digit
        /// before inserting the single-digit term. Default: 1100ms.
        /// Only applies when TermShortcutStyle is "sequential".
        /// </summary>
        [DataMember(Name = "chordDelayMs")]
        public int ChordDelayMs { get; set; } = 1100;

        // ─── Case sensitivity ────────────────────────────────────────
        /// <summary>
        /// Global default for case-sensitive term matching.
        /// When true, terms only match if the source text has the same case as the indexed term.
        /// Individual termbases can override this via their own case_sensitive setting.
        /// Default: false (case-insensitive, matching current behaviour).
        /// </summary>
        [DataMember(Name = "caseSensitiveMatching")]
        public bool CaseSensitiveMatching { get; set; } = false;

        // ─── Update checker ──────────────────────────────────────────
        /// <summary>
        /// Version string the user chose to skip (e.g. "4.2.0-beta").
        /// The update dialog will not show again for this version.
        /// </summary>
        [DataMember(Name = "skippedUpdateVersion")]
        public string SkippedUpdateVersion { get; set; } = "";

        // ─── Usage statistics ──────────────────────────────────────
        /// <summary>
        /// Whether the user has opted in to anonymous usage statistics.
        /// Default: false (strictly opt-in). Can be changed at any time in Settings.
        /// </summary>
        [DataMember(Name = "usageStatisticsEnabled")]
        public bool UsageStatisticsEnabled { get; set; } = false;

        /// <summary>
        /// Random anonymous UUID generated on first opt-in.
        /// Not tied to any account, machine, or identity — purely random.
        /// </summary>
        [DataMember(Name = "usageStatisticsId")]
        public string UsageStatisticsId { get; set; } = "";

        /// <summary>
        /// Whether the user has already been asked about usage statistics.
        /// Once true, the opt-in dialog is not shown again (the user can
        /// still change the setting in Settings at any time).
        /// </summary>
        [DataMember(Name = "usageStatisticsAsked")]
        public bool UsageStatisticsAsked { get; set; } = false;

        // ─── AI settings ────────────────────────────────────────────
        /// <summary>
        /// AI provider configuration (API keys, provider selection, model selection).
        /// </summary>
        [DataMember(Name = "aiSettings")]
        public AiSettings AiSettings { get; set; } = new AiSettings();

        /// <summary>
        /// Loads settings from disk. Returns default settings if the file doesn't exist or can't be read.
        /// </summary>
        public static TermLensSettings Load()
        {
            try
            {
                var settingsFile = SettingsFilePath;
                var settingsDir  = Path.GetDirectoryName(settingsFile);

                // Auto-migrate from old TermLens settings location
                if (!File.Exists(settingsFile) && File.Exists(OldSettingsFile))
                {
                    Directory.CreateDirectory(settingsDir);
                    File.Copy(OldSettingsFile, settingsFile);
                }

                if (!File.Exists(settingsFile))
                    return new TermLensSettings();

                var json = File.ReadAllText(settingsFile, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var loadSettings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    var serializer = new DataContractJsonSerializer(typeof(TermLensSettings), loadSettings);
                    var s = (TermLensSettings)serializer.ReadObject(stream);

                    // Migrate: old single WriteTermbaseId → new WriteTermbaseIds list
                    if ((s.WriteTermbaseIds == null || s.WriteTermbaseIds.Count == 0)
                        && s.WriteTermbaseId >= 0)
                    {
                        s.WriteTermbaseIds = new List<long> { s.WriteTermbaseId };
                        s.WriteTermbaseId = -1;
                    }

                    // Ensure list is never null
                    if (s.WriteTermbaseIds == null)
                        s.WriteTermbaseIds = new List<long>();

                    // Migrate: chord delay missing from older settings (deserializes as 0)
                    if (s.ChordDelayMs <= 0)
                        s.ChordDelayMs = 1100;

                    // Ensure AI settings are never null (backward compat with older settings files)
                    if (s.AiSettings == null)
                        s.AiSettings = new AiSettings();
                    if (s.AiSettings.ApiKeys == null)
                        s.AiSettings.ApiKeys = new AiApiKeys();
                    if (s.AiSettings.CustomOpenAiProfiles == null)
                        s.AiSettings.CustomOpenAiProfiles = new List<CustomOpenAiProfile>();
                    if (s.AiSettings.DisabledAiTermbaseIds == null)
                        s.AiSettings.DisabledAiTermbaseIds = new List<long>();

                    // Ensure prompt settings have safe defaults
                    if (s.AiSettings.SelectedPromptPath == null)
                        s.AiSettings.SelectedPromptPath = "";
                    // CustomSystemPrompt is intentionally nullable (null = use default)

                    // Migrate: UI scale factor missing or invalid from older settings
                    if (s.UiScaleFactor <= 0f || s.UiScaleFactor > 3f)
                        s.UiScaleFactor = 1.0f;

                    // Migrate: chat font size missing from older settings (deserializes as 0)
                    if (s.ChatFontSize <= 0f)
                        s.ChatFontSize = 9f;

                    // Ensure update checker field is never null
                    if (s.SkippedUpdateVersion == null)
                        s.SkippedUpdateVersion = "";

                    // Ensure usage statistics ID is never null
                    if (s.UsageStatisticsId == null)
                        s.UsageStatisticsId = "";

                    return s;
                }
            }
            catch
            {
                return new TermLensSettings();
            }
        }

        // ─── Per-project overlay ─────────────────────────────────────

        /// <summary>
        /// Applies a project-specific settings overlay onto this global settings instance.
        /// Only copies the per-project fields (termbase path, enabled/disabled IDs, etc.).
        /// </summary>
        public void ApplyProjectOverlay(ProjectSettings ps)
        {
            if (ps == null) return;

            TermbasePath = ps.TermbasePath ?? "";
            WriteTermbaseIds = ps.WriteTermbaseIds ?? new List<long>();
            ProjectTermbaseId = ps.ProjectTermbaseId;
            DisabledTermbaseIds = ps.DisabledTermbaseIds ?? new List<long>();
            DisabledMultiTermIds = ps.DisabledMultiTermIds ?? new List<long>();

            if (AiSettings != null && ps.DisabledAiTermbaseIds != null)
                AiSettings.DisabledAiTermbaseIds = ps.DisabledAiTermbaseIds;
        }

        /// <summary>
        /// Extracts the per-project fields from this settings instance into a
        /// ProjectSettings object suitable for saving.
        /// </summary>
        public ProjectSettings ExtractProjectSettings(string projectPath = null, string projectName = null)
        {
            return new ProjectSettings
            {
                ProjectPath = projectPath ?? "",
                ProjectName = projectName ?? "",
                TermbasePath = TermbasePath ?? "",
                WriteTermbaseIds = WriteTermbaseIds != null
                    ? new List<long>(WriteTermbaseIds) : new List<long>(),
                ProjectTermbaseId = ProjectTermbaseId,
                DisabledTermbaseIds = DisabledTermbaseIds != null
                    ? new List<long>(DisabledTermbaseIds) : new List<long>(),
                DisabledMultiTermIds = DisabledMultiTermIds != null
                    ? new List<long>(DisabledMultiTermIds) : new List<long>(),
                DisabledAiTermbaseIds = AiSettings?.DisabledAiTermbaseIds != null
                    ? new List<long>(AiSettings.DisabledAiTermbaseIds) : new List<long>(),
            };
        }

        /// <summary>
        /// Saves settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                var settingsFile = SettingsFilePath;
                var settingsDir  = Path.GetDirectoryName(settingsFile);
                Directory.CreateDirectory(settingsDir);

                using (var stream = new MemoryStream())
                {
                    var settings = new DataContractJsonSerializerSettings
                    {
                        UseSimpleDictionaryFormat = true
                    };
                    var serializer = new DataContractJsonSerializer(typeof(TermLensSettings), settings);
                    serializer.WriteObject(stream, this);

                    // Pretty-print by re-parsing (DataContractJsonSerializer writes compact JSON)
                    var json = Encoding.UTF8.GetString(stream.ToArray());
                    File.WriteAllText(settingsFile, json, Encoding.UTF8);
                }
            }
            catch
            {
                // Silently ignore save failures
            }
        }
    }
}
