namespace Supervertaler.Trados.Models
{
    /// <summary>
    /// How a MultiTerm termbase was loaded.
    /// </summary>
    public enum MultiTermLoadMode
    {
        DirectAccess,
        TerminologyProviderApi,
        Failed
    }

    /// <summary>
    /// Metadata about a MultiTerm .sdltb termbase detected from the Trados project.
    /// Uses negative SyntheticId to avoid collision with Supervertaler term IDs.
    /// </summary>
    public class MultiTermTermbaseInfo
    {
        public long SyntheticId { get; set; }
        public string FilePath { get; set; }
        public string Name { get; set; }
        public string SourceIndexName { get; set; }
        public string TargetIndexName { get; set; }
        public int TermCount { get; set; }
        public bool IsEnabled { get; set; } = true;
        public MultiTermLoadMode LoadMode { get; set; }
    }

    /// <summary>
    /// Configuration for a MultiTerm termbase discovered from the active Trados project.
    /// </summary>
    public class MultiTermTermbaseConfig
    {
        public string FilePath { get; set; }
        public string TermbaseName { get; set; }
        public string SourceIndexName { get; set; }
        public string TargetIndexName { get; set; }
        public long SyntheticId { get; set; }

        /// <summary>
        /// Raw settings XML from the project's TermbaseConfiguration.
        /// Used to extract the correct provider URI for the API fallback.
        /// </summary>
        public string SettingsXml { get; set; }
    }
}
