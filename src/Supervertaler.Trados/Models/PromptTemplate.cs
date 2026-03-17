namespace Supervertaler.Trados.Models
{
    /// <summary>
    /// Represents a prompt template loaded from the shared prompt library.
    /// Stored as .svprompt files (Markdown + YAML frontmatter).
    /// </summary>
    public class PromptTemplate
    {
        /// <summary>Display name (from YAML 'name:' field or filename).</summary>
        public string Name { get; set; } = "";

        /// <summary>One-line description (from YAML 'description:' field).</summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Domain or category (from YAML 'category:' / 'domain:' field or folder name).
        /// Common values: "Translate", "Proofread", "QuickLauncher".
        /// Legacy value "quickmenu_prompts" is normalised to "QuickLauncher" on load.
        /// </summary>
        public string Domain { get; set; } = "";

        /// <summary>The actual prompt text (everything after the YAML frontmatter).</summary>
        public string Content { get; set; } = "";

        /// <summary>Full filesystem path to the .svprompt file.</summary>
        public string FilePath { get; set; } = "";

        /// <summary>
        /// Relative path from the prompts root directory.
        /// Used as the stable identifier for settings persistence.
        /// </summary>
        public string RelativePath { get; set; } = "";

        /// <summary>True if this prompt was shipped with the plugin (can be restored if deleted).</summary>
        public bool IsBuiltIn { get; set; }

        /// <summary>True if this prompt is read-only (e.g. from a shared folder).</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// True when the prompt should appear in the QuickLauncher right-click menu.
        /// Set by YAML 'sv_quicklauncher: true' (or legacy 'sv_quickmenu: true') or category 'QuickLauncher' / 'quickmenu_prompts'.
        /// </summary>
        public bool IsQuickLauncher { get; set; }

        /// <summary>
        /// Optional short label shown in the QuickLauncher menu (from YAML 'quickmenu_label:').
        /// Falls back to Name if empty.
        /// </summary>
        public string QuickLauncherLabel { get; set; } = "";

        /// <summary>The label to display in the QuickLauncher menu (QuickLauncherLabel if set, else Name).</summary>
        public string MenuLabel => string.IsNullOrWhiteSpace(QuickLauncherLabel) ? Name : QuickLauncherLabel;

        public override string ToString() => Name;
    }
}
