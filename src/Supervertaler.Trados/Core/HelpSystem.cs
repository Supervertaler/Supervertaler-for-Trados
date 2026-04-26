using System.Diagnostics;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Centralized context-sensitive help system.
    /// Maps UI elements to GitBook documentation pages and opens them in the browser.
    /// </summary>
    public static class HelpSystem
    {
        /// <summary>
        /// Base URL for the Trados plugin GitBook documentation space.
        /// </summary>
        private const string DocsBaseUrl = "https://supervertaler.gitbook.io/trados";

        /// <summary>
        /// Desktop app docs home (separate GitBook space).
        /// </summary>
        private const string DocsHomeUrl = "https://supervertaler.gitbook.io/supervertaler";

        /// <summary>
        /// Help topic identifiers. Each maps to a specific documentation page path
        /// relative to the Trados plugin docs section.
        /// </summary>
        public static class Topics
        {
            // GitBook sections (## headers in SUMMARY.md) become URL path prefixes.
            public const string Overview           = "";  // root of the space (README.md)
            public const string Installation       = "getting-started/installation";
            public const string GettingStarted     = "getting-started/getting-started";
            public const string TermLensPanel      = "features/termlens";
            public const string AddTermDialog      = "features/termlens/adding-terms";
            public const string TermLensPopup      = "features/termlens/termlens-popup";
            public const string TermPickerDialog   = "features/termlens/term-picker";
            public const string AiAssistantChat    = "features/ai-assistant";
            public const string StudioTools        = "features/ai-assistant/studio-tools";
            public const string QuickLauncher      = "features/quicklauncher";
            public const string BatchOperations    = "features/batch-operations";
            public const string BatchTranslate     = "features/batch-operations/batch-translate";
            public const string AiProofreader      = "features/batch-operations/ai-proofreader";
            public const string AiProofreaderReports = "features/batch-operations/ai-proofreader#reports-tab";
            public const string PromptLogging       = "settings/ai-settings#prompt-logging";
            public const string AiCostGuide         = "reference/ai-cost-guide";
            public const string ClipboardMode       = "features/batch-operations/clipboard-mode";
            public const string GeneratePrompt      = "features/batch-operations/generate-prompt";
            public const string MultiTermSupport   = "features/multiterm-support";
            public const string TermbaseEditor     = "terminology/termbase-management";
            public const string SettingsGeneral    = "settings/usage-statistics";
            public const string SettingsTermLens   = "settings/termlens";
            public const string SettingsAi         = "settings/ai-settings";
            public const string SettingsPrompts    = "settings/prompts";
            public const string SettingsBackup     = "settings/backup";
            public const string SettingsUsageStats = "settings/usage-statistics";
            public const string Licensing          = "getting-started/licensing";
            public const string ProjectSettings    = "settings/project-settings";
            public const string KeyboardShortcuts  = "reference/keyboard-shortcuts";
            public const string Troubleshooting    = "reference/troubleshooting";
            // Memory banks (formerly "SuperMemory") — now nested under the Supervertaler
            // Assistant section in the GitBook SUMMARY. C# identifier names kept as
            // SuperMemory* for backwards-compat with existing call sites; rename when the
            // Trados UI strings are updated to match the new memory bank terminology.
            public const string SuperMemory        = "features/ai-assistant/super-memory";
            public const string SuperMemoryQuickAdd = "features/ai-assistant/super-memory/quick-add";
            public const string SuperMemoryInbox    = "features/ai-assistant/super-memory/process-inbox";
            public const string SuperMemoryHealth   = "features/ai-assistant/super-memory/health-check";
            public const string SuperMemoryDistill  = "features/ai-assistant/super-memory/distill";
            public const string SuperMemoryObsidian = "features/ai-assistant/super-memory/obsidian-setup";
            public const string SuperSearch         = "features/supersearch";
        }

        /// <summary>
        /// Opens the help page for the given topic identifier.
        /// Falls back to the section root if topic is null/empty.
        /// </summary>
        public static void OpenHelp(string topic = null)
        {
            string url = string.IsNullOrEmpty(topic)
                ? DocsBaseUrl
                : DocsBaseUrl + "/" + topic.TrimStart('/');

            OpenUrl(url);
        }

        /// <summary>
        /// Opens the generic docs home (not the Trados section).
        /// </summary>
        public static void OpenDocsHome()
        {
            OpenUrl(DocsHomeUrl);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No default browser configured — silently ignore
            }
        }
    }
}
