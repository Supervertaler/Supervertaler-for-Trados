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
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler.Trados");

        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

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
        /// IDs of termbases that receive new terms via the Add Term / Quick Add Term actions.
        /// Multiple termbases can be marked as Write targets — a new term is inserted into all of them.
        /// Empty list means no write termbases are configured.
        /// </summary>
        [DataMember(Name = "writeTermbaseIds")]
        public List<long> WriteTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// ID of the termbase the user has marked as the "Project" glossary.
        /// The project glossary is shown in pink; all others in blue.
        /// -1 means no project glossary is configured.
        /// </summary>
        [DataMember(Name = "projectTermbaseId")]
        public long ProjectTermbaseId { get; set; } = -1;

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

        // ─── Glossary Editor dialog layout persistence ──────────────
        [DataMember(Name = "glossaryEditorWidth")]
        public int GlossaryEditorWidth { get; set; }

        [DataMember(Name = "glossaryEditorHeight")]
        public int GlossaryEditorHeight { get; set; }

        // ─── Panel font size ─────────────────────────────────────────
        /// <summary>
        /// Font size (in points) for the TermLens panel. Default: 9pt.
        /// Adjustable via the A+/A- buttons in the panel header or the Settings dialog.
        /// </summary>
        [DataMember(Name = "panelFontSize")]
        public float PanelFontSize { get; set; } = 9f;

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
                // Auto-migrate from old TermLens settings location
                if (!File.Exists(SettingsFile) && File.Exists(OldSettingsFile))
                {
                    Directory.CreateDirectory(SettingsDir);
                    File.Copy(OldSettingsFile, SettingsFile);
                }

                if (!File.Exists(SettingsFile))
                    return new TermLensSettings();

                var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(TermLensSettings));
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

                    // Ensure AI settings are never null (backward compat with older settings files)
                    if (s.AiSettings == null)
                        s.AiSettings = new AiSettings();
                    if (s.AiSettings.ApiKeys == null)
                        s.AiSettings.ApiKeys = new AiApiKeys();
                    if (s.AiSettings.CustomOpenAiProfiles == null)
                        s.AiSettings.CustomOpenAiProfiles = new List<CustomOpenAiProfile>();

                    return s;
                }
            }
            catch
            {
                return new TermLensSettings();
            }
        }

        /// <summary>
        /// Saves settings to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);

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
                    File.WriteAllText(SettingsFile, json, Encoding.UTF8);
                }
            }
            catch
            {
                // Silently ignore save failures
            }
        }
    }
}
