using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TermLens.Settings
{
    /// <summary>
    /// Persisted settings for the TermLens plugin.
    /// Stored at %LocalAppData%\TermLens\settings.json.
    /// </summary>
    [DataContract]
    public class TermLensSettings
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermLens");

        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

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
        /// ID of the termbase that receives new terms via the Add Term action.
        /// -1 means no write termbase is configured.
        /// </summary>
        [DataMember(Name = "writeTermbaseId")]
        public long WriteTermbaseId { get; set; } = -1;

        /// <summary>
        /// IDs of termbases the user has marked as "Project" glossaries.
        /// Project glossaries are shown in pink; all others in blue.
        /// </summary>
        [DataMember(Name = "projectTermbaseIds")]
        public List<long> ProjectTermbaseIds { get; set; } = new List<long>();

        /// <summary>
        /// Loads settings from disk. Returns default settings if the file doesn't exist or can't be read.
        /// </summary>
        public static TermLensSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new TermLensSettings();

                var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(TermLensSettings));
                    return (TermLensSettings)serializer.ReadObject(stream);
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
