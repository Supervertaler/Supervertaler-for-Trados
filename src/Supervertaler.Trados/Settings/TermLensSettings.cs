using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Supervertaler.Trados.Core;

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
        /// Opt-in diagnostic logging. When true, the plugin writes a detailed debug
        /// trace to <see cref="Core.DiagnosticLog.LogFilePath"/> for troubleshooting.
        /// Off by default; mirrored into <see cref="Core.DiagnosticLog.Enabled"/> on load.
        /// </summary>
        [DataMember(Name = "diagnosticLogging")]
        public bool DiagnosticLogging { get; set; } = false;

        /// <summary>
        /// IDs of termbases the user has disabled. Empty means all termbases are active.
        /// Stored as disabled-list so newly added termbases are active by default.
        /// </summary>
        [DataMember(Name = "disabledTermbaseIds")]
        public List<long> DisabledTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// DEPRECATED – kept for backward-compatible migration from settings that
        /// stored a single write target.  New code should use <see cref="WriteTermbaseIds"/>.
        /// </summary>
        [DataMember(Name = "writeTermbaseId")]
        public long WriteTermbaseId { get; set; } = -1;

        /// <summary>
        /// IDs of termbases that receive new terms via the Add Term / Quick-Add Term actions.
        /// Multiple termbases can be marked as Write targets – a new term is inserted into all of them.
        /// Empty list means no write termbases are configured.
        /// </summary>
        [DataMember(Name = "writeTermbaseIds")]
        public List<long> WriteTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// Names of termbases for which the user has explicitly confirmed
        /// Write/Project assignment despite the termbase's declared language
        /// pair not matching the active project (i.e.
        /// <see cref="Core.LanguageUtils.TermbaseDirection.Unrelated"/>).
        ///
        /// Keyed by termbase name rather than ID because the underlying
        /// SQLite schema declares <c>name TEXT NOT NULL UNIQUE</c>, so names
        /// are stable across database rebuilds in a way IDs aren't. With
        /// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> ID reuse within one
        /// database is impossible, but a user who wipes and recreates
        /// <c>supervertaler.db</c> could end up with stale ID-based
        /// confirmations applying to different termbases. Names sidestep
        /// that – the only re-ask trigger is a deliberate rename.
        ///
        /// The confirm dialog in Settings → Termbases adds the name here on
        /// "Yes, add anyway"; unticking the box removes it so a re-tick
        /// re-asks. Empty / missing means no overrides have been confirmed.
        /// </summary>
        [DataMember(Name = "confirmedNonMatchingWriteTermbaseNames")]
        public List<string> ConfirmedNonMatchingWriteTermbaseNames { get; set; } = new List<string>();

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

        // ─── TermPicker layout persistence ────────────────────────────
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

        // ─── SuperSearch docking ──────────────────────────────────────
        /// <summary>
        /// When true, SuperSearch is hosted as a tab inside the Supervertaler
        /// Assistant panel instead of its own dockable ViewPart. Requires a
        /// Trados restart to take effect (the control can only have one host).
        /// Default: false (standalone ViewPart).
        /// </summary>
        [DataMember(Name = "superSearchInAssistantTab")]
        public bool SuperSearchInAssistantTab { get; set; } = false;

        /// <summary>
        /// SuperSearch search source: "ProjectFiles" (SDLXLIFF files only),
        /// "FilesAndTms" (files + project translation memories), or "TmsOnly"
        /// (concordance — project TMs only). Persisted until the user changes
        /// it. Default: "ProjectFiles". Stored as a string for forward
        /// compatibility with future modes.
        /// </summary>
        [DataMember(Name = "superSearchMode")]
        public string SuperSearchMode { get; set; } = "ProjectFiles";

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

        // ─── Agglutinative / no-space language matching ──────────────
        /// <summary>
        /// Suffix-tolerant term matching for languages where grammatical
        /// particles attach directly to a noun with no intervening space
        /// (Korean, Japanese). When active, a term still matches when the
        /// segment token carries a trailing particle (값 ↦ 값으로,
        /// 제2 전압 값 ↦ 제2 전압 값으로), and the Add Term actions stop
        /// auto-expanding the selection to whitespace so the bare noun can be
        /// saved (장치, not 장치의).
        ///
        /// Three-state, stored as a string for forward compatibility:
        ///   "auto" (default) — on when the project source language is Korean
        ///                      or Japanese;
        ///   "on"             — always on (e.g. Chinese, or an agglutinative
        ///                      language not auto-detected);
        ///   "off"            — always off (strict whole-token matching).
        /// </summary>
        [DataMember(Name = "suffixTolerantMatching")]
        public string SuffixTolerantMatching { get; set; } = "auto";

        /// <summary>
        /// Resolves whether suffix-tolerant matching is active for a given
        /// source language, honouring the three-state <see cref="SuffixTolerantMatching"/>.
        /// </summary>
        public bool ResolveSuffixTolerant(string sourceLanguage)
        {
            switch ((SuffixTolerantMatching ?? "auto").Trim().ToLowerInvariant())
            {
                case "on": return true;
                case "off": return false;
                default: return IsAgglutinativeNoSpaceLanguage(sourceLanguage);
            }
        }

        /// <summary>
        /// True for Korean / Japanese, where particles attach to nouns without a
        /// space. Accepts ISO codes (ko, ja, ko-KR, ja-JP) and English display
        /// names ("Korean", "Japanese (Japan)").
        /// </summary>
        public static bool IsAgglutinativeNoSpaceLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return false;
            var l = lang.Trim().ToLowerInvariant();
            return l.StartsWith("ko") || l.StartsWith("ja")
                || l.Contains("korean") || l.Contains("japanese");
        }

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
        /// Not tied to any account, machine, or identity – purely random.
        /// </summary>
        [DataMember(Name = "usageStatisticsId")]
        public string UsageStatisticsId { get; set; } = "";

        /// <summary>
        /// Whether the user has already been asked about usage statistics.
        /// Once true, the opt-in dialog is not shown again (the user can
        /// still change the setting in Settings at any time).
        ///
        /// Legacy v1 flag - kept for backwards compatibility with old settings
        /// files but no longer checked. The opt-in dialog now uses
        /// UsageStatisticsAskedV2 so that users who saw the old "Yes, share?"
        /// framing get a second chance under the new "default-on, switch off
        /// here if you'd rather not" framing.
        /// </summary>
        [DataMember(Name = "usageStatisticsAsked")]
        public bool UsageStatisticsAsked { get; set; } = false;

        /// <summary>
        /// Whether the user has been asked about usage statistics under the
        /// rewritten dialog (v2: informational, default-on, opt-out). Defaults
        /// to false so every existing user sees the new dialog once after
        /// updating, regardless of what they answered to the old one.
        /// </summary>
        [DataMember(Name = "usageStatisticsAskedV2")]
        public bool UsageStatisticsAskedV2 { get; set; } = false;

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
                    if (s.ConfirmedNonMatchingWriteTermbaseNames == null)
                        s.ConfirmedNonMatchingWriteTermbaseNames = new List<string>();

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
                    if (s.AiSettings.EnabledAiMultiTermIds == null)
                        s.AiSettings.EnabledAiMultiTermIds = new List<long>();

                    // Ensure prompt settings have safe defaults
                    if (s.AiSettings.SelectedPromptPath == null)
                        s.AiSettings.SelectedPromptPath = "";
                    // CustomSystemPrompt is intentionally nullable (null = use default)

                    // Ensure the active memory bank is populated. The OnDeserializing
                    // hook pre-seeds this, but belt-and-braces for callers who build
                    // AiSettings instances outside of the serializer.
                    if (string.IsNullOrWhiteSpace(s.AiSettings.ActiveMemoryBankName))
                        s.AiSettings.ActiveMemoryBankName = UserDataPath.DefaultMemoryBankName;

                    // Migrate: retired OpenAI models → GPT-5.4 Mini
                    var openAiModel = s.AiSettings.OpenAiModel;
                    if (openAiModel == "gpt-4.1" || openAiModel == "gpt-4.1-mini" ||
                        openAiModel == "o4-mini" || openAiModel == "gpt-4o" ||
                        openAiModel == "gpt-4o-mini")
                    {
                        s.AiSettings.OpenAiModel = "gpt-5.4-mini";
                    }

                    // Migrate: UI scale factor missing or invalid from older settings
                    if (s.UiScaleFactor <= 0f || s.UiScaleFactor > 3f)
                        s.UiScaleFactor = 1.0f;

                    // Migrate: chat font size missing from older settings (deserializes as 0)
                    if (s.ChatFontSize <= 0f)
                        s.ChatFontSize = 9f;

                    // Ensure update checker field is never null
                    if (s.SkippedUpdateVersion == null)
                        s.SkippedUpdateVersion = "";

                    // Suffix-tolerant matching missing from older settings → "auto"
                    if (string.IsNullOrWhiteSpace(s.SuffixTolerantMatching))
                        s.SuffixTolerantMatching = "auto";

                    // Ensure usage statistics ID is never null
                    if (s.UsageStatisticsId == null)
                        s.UsageStatisticsId = "";

                    // Keep the global diagnostic-logging switch in sync with the
                    // persisted preference on every load.
                    DiagnosticLog.Enabled = s.DiagnosticLogging;

                    return s;
                }
            }
            catch (Exception ex)
            {
                // Log the failure to a sidecar file so a future regression
                // surfaces immediately instead of silently wiping the user's
                // saved settings (the exact failure mode of the v4.19.52 bug).
                try
                {
                    var dir = Path.GetDirectoryName(SettingsFilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                        var logPath = Path.Combine(dir, "settings-load-errors.log");
                        File.AppendAllText(logPath,
                            $"[{DateTime.Now:O}] {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n");
                    }
                }
                catch { /* logging must never throw out of Load */ }
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

            if (AiSettings != null && ps.EnabledAiMultiTermIds != null)
                AiSettings.EnabledAiMultiTermIds = ps.EnabledAiMultiTermIds;

            // Per-project active prompt (overrides global SelectedPromptPath)
            if (AiSettings != null && !string.IsNullOrEmpty(ps.ActivePromptPath))
                AiSettings.SelectedPromptPath = ps.ActivePromptPath;
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
                EnabledAiMultiTermIds = AiSettings?.EnabledAiMultiTermIds != null
                    ? new List<long>(AiSettings.EnabledAiMultiTermIds) : new List<long>(),
                // Always true when extracted – the settings are considered initialised once
                // the plugin has loaded and applied them at least once.
                AiTermbaseIdsInitialized = true,
                ActivePromptPath = AiSettings?.SelectedPromptPath ?? "",
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

        /// <summary>
        /// Round-trips a default <see cref="TermLensSettings"/> through the same
        /// DataContractJsonSerializer pipeline that <see cref="Load"/> and
        /// <see cref="Save"/> use. Returns <c>null</c> on success or an error
        /// description on failure.
        ///
        /// Guards against the silent-data-loss class of bug introduced in
        /// v4.19.52 (commit 71af680), where adding a second
        /// <c>[OnDeserializing]</c> method to <see cref="AiSettings"/> caused
        /// <see cref="System.Runtime.Serialization.InvalidDataContractException"/>
        /// on every deserialize attempt – which <c>Load()</c>'s catch-all then
        /// swallowed, returning fresh defaults and making every saved setting
        /// vanish from the user's perspective. Plugin startup calls this and
        /// logs the result, so any future contract violation surfaces
        /// immediately in the plugin log instead of after users notice their
        /// settings have disappeared.
        /// </summary>
        public static string RunStartupSelfTest()
        {
            try
            {
                var sample = new TermLensSettings();
                var s = new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };
                var serializer = new DataContractJsonSerializer(typeof(TermLensSettings), s);
                using (var ms = new MemoryStream())
                {
                    serializer.WriteObject(ms, sample);
                    ms.Position = 0;
                    var roundTripped = serializer.ReadObject(ms);
                    if (roundTripped == null)
                        return "ReadObject returned null";
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().FullName + ": " + ex.Message;
            }
        }
    }
}
