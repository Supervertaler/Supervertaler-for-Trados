namespace Supervertaler.Trados.Models
{
    /// <summary>
    /// Represents a prompt template loaded from the shared prompt library.
    /// Stored as .md files (Markdown + YAML frontmatter). Legacy .svprompt files also supported.
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

        /// <summary>Full filesystem path to the prompt file (.md or legacy .svprompt).</summary>
        public string FilePath { get; set; } = "";

        /// <summary>
        /// Document type (from YAML 'type:' field). Default "prompt".
        /// Used to identify Supervertaler document types in plain .md files.
        /// </summary>
        public string Type { get; set; } = "prompt";

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
        /// Set by YAML 'category: QuickLauncher', or by placing the file in a folder named 'QuickLauncher'.
        /// </summary>
        public bool IsQuickLauncher { get; set; }

        /// <summary>
        /// Optional short label shown in the QuickLauncher menu (from YAML 'quicklauncher_label:').
        /// Falls back to Name if empty.
        /// </summary>
        public string QuickLauncherLabel { get; set; } = "";

        /// <summary>
        /// Target application for this prompt (from YAML 'app:' field).
        /// "workbench" = Supervertaler Workbench only, "trados" = Trados plugin only,
        /// "both" = shared between both (default).
        /// </summary>
        public string App { get; set; } = "both";

        /// <summary>
        /// Sort order within a folder (from YAML 'sort_order:' field).
        /// Lower values appear first. Default 100 for unset (sorts after explicit values).
        /// </summary>
        public int SortOrder { get; set; } = 100;

        /// <summary>The label to display in the QuickLauncher menu (QuickLauncherLabel if set, else Name).</summary>
        public string MenuLabel => string.IsNullOrWhiteSpace(QuickLauncherLabel) ? Name : QuickLauncherLabel;

        public override string ToString() => Name;
    }
}
