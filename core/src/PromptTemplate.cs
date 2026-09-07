using System.Collections.Generic;

namespace Supervertaler.Core.Models
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
        /// Category (from YAML 'category:' field or folder name on disk).
        /// Common values: "Translate", "Proofread", "QuickLauncher", "QuickLauncher/Default".
        /// Legacy values "quickmenu_prompts" / "domain:" are normalised on load.
        /// </summary>
        public string Category { get; set; } = "";

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
        public bool IsDefault { get; set; }

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
        /// The language pair the prompt was written for, as memoQ or Trados
        /// language codes, or empty when it does not say.
        ///
        /// A prompt is not direction-neutral: it names the source and target
        /// languages in its role, locks terminology one way round, and carries
        /// register rules for one target. Selecting a Dutch-to-English prompt in
        /// an English-to-Dutch project produced a perfectly confident translation
        /// against instructions written for the opposite job, with nothing
        /// anywhere saying so. Exported glossaries already declare their pair;
        /// this is the same declaration for the prompt that produced them.
        /// </summary>
        /// <summary>
        /// What wrote this prompt, when it was not a person - currently only
        /// "autoprompt". Empty for a hand-written prompt.
        ///
        /// <para>Recorded because the runtime needs to tell the two apart. A
        /// drafted prompt already carries a locked-terms table chosen for this
        /// document, so sending the project glossary alongside it supplies the
        /// same job's terminology twice, from two sources that were not written
        /// to agree - and on the terms where they disagree the model is left to
        /// pick. A hand-written prompt makes no such claim and still wants the
        /// glossary.</para>
        ///
        /// <para>A string rather than a bool so that "drafted by what" stays
        /// answerable: the answer will not always be AutoPrompt.</para>
        /// </summary>
        public string DraftedBy { get; set; } = "";

        /// <summary>True when something other than a person wrote this prompt.</summary>
        public bool IsDrafted => !string.IsNullOrWhiteSpace(DraftedBy);

        public string SourceLang { get; set; } = "";

        public string TargetLang { get; set; } = "";

        /// <summary>
        /// Sort order within a folder (from YAML 'sort_order:' field).
        /// Lower values appear first. Default 100 for unset (sorts after explicit values).
        /// </summary>
        public int SortOrder { get; set; } = 100;

        /// <summary>
        /// Frontmatter lines the parser did not recognise, kept verbatim and in
        /// order so that saving a prompt cannot silently delete them.
        ///
        /// The library is edited by more than one thing — two plugins, a text
        /// editor, whatever comes next — and the writer only knows how to emit
        /// the fields this class models. Without this, every save rewrote the
        /// frontmatter block from those fields alone and dropped the rest:
        /// re-ordering two prompts in the manager panel was enough to strip
        /// <c>read_only</c>, <c>quicklauncher_grid</c>, <c>tags</c> and
        /// <c>favorite</c> off the pair that moved, with nothing shown to the
        /// user and no way to get them back.
        ///
        /// Deliberately raw strings rather than a parsed map: the point is to
        /// hand back exactly what was there, including a key this version has
        /// never heard of and a future version might.
        /// </summary>
        public List<string> UnrecognizedFrontmatter { get; set; } = new List<string>();

        /// <summary>
        /// True when the file carried an explicit QuickLauncher key, rather than
        /// the membership being inferred from a "QuickLauncher/…" category.
        ///
        /// Exists so the writer can put back a line the file already had without
        /// adding one to files that never had it. Not frontmatter itself, and
        /// never written on its own.
        /// </summary>
        public bool QuickLauncherFlagWasExplicit { get; set; }

        /// <summary>
        /// When true, the prompt is hidden from the QuickLauncher right-click menu
        /// but still visible in the Prompt Manager tree (shown with a "(hidden)" suffix).
        /// From YAML 'hidden:' field.
        /// </summary>
        public bool HiddenFromMenu { get; set; }

        /// <summary>
        /// Destinations the prompt can be dispatched to from the QuickLauncher menu.
        /// Currently supported values: "assistant" (send to the in-Trados AI Assistant
        /// or to Supervertaler Workbench's Chat, per the user's global
        /// QuickLauncherTarget setting) and "clipboard" (copy the expanded prompt to
        /// the system clipboard for the user to paste into an external chat such as
        /// claude.ai).
        ///
        /// When the list contains a single value (the default — just "assistant"),
        /// the menu shows a flat item: clicking fires that single mode. When two or
        /// more modes are configured, the menu shows a cascading submenu so the
        /// user can pick the destination at runtime.
        ///
        /// Parsed from YAML 'quicklauncher_modes:' field, which accepts either an
        /// inline list (`[assistant, clipboard]`) or a comma-separated string
        /// (`assistant, clipboard`).
        /// </summary>
        public List<string> QuickLauncherModes { get; set; } = new List<string> { "assistant" };

        /// <summary>
        /// Which entry in <see cref="QuickLauncherModes"/> should be presented as
        /// the default when the menu shows a submenu (rendered first, gets the
        /// natural first-item Enter activation). Falls back to the first item in
        /// the list when unset or unrecognised. From YAML 'default_mode:' field.
        /// </summary>
        public string DefaultMode { get; set; } = "assistant";

        /// <summary>
        /// True when the prompt has two or more <see cref="QuickLauncherModes"/>
        /// configured, in which case the menu builder renders a cascading submenu
        /// instead of a flat item.
        /// </summary>
        public bool HasMultipleQuickLauncherModes =>
            QuickLauncherModes != null && QuickLauncherModes.Count >= 2;

        /// <summary>The label to display in the QuickLauncher menu (QuickLauncherLabel if set, else Name).</summary>
        public string MenuLabel => string.IsNullOrWhiteSpace(QuickLauncherLabel) ? Name : QuickLauncherLabel;

        /// <summary>
        /// True when this template is a local text transform (type: transform)
        /// rather than an AI prompt. Transforms apply find/replace rules directly
        /// to the target segment without calling an AI provider.
        /// </summary>
        public bool IsTransform => "transform".Equals(Type, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Find/replace pairs for text transforms (type: transform).
        /// Each entry has a Find string and a Replace string.
        /// Parsed from YAML frontmatter 'replacements:' block.
        /// </summary>
        public List<TextReplacement> Replacements { get; set; } = new List<TextReplacement>();

        public override string ToString() => Name;
    }

    /// <summary>
    /// A single find/replace rule used by text transform prompts.
    /// </summary>
    public class TextReplacement
    {
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
    }
}
