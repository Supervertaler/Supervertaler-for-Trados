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
            public const string TermPickerDialog   = "features/termlens/term-picker";
            public const string AiAssistantChat    = "features/ai-assistant";
            public const string BatchTranslate     = "features/batch-translate";
            public const string MultiTermSupport   = "features/multiterm-support";
            public const string TermbaseEditor     = "terminology/termbase-management";
            public const string SettingsTermLens   = "settings/termlens";
            public const string SettingsAi         = "settings/ai-settings";
            public const string SettingsPrompts    = "settings/prompts";
            public const string SettingsBackup     = "settings/backup";
            public const string Licensing          = "getting-started/licensing";
            public const string KeyboardShortcuts  = "reference/keyboard-shortcuts";
            public const string Troubleshooting    = "reference/troubleshooting";
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
