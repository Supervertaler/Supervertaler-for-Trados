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
        /// Base URL for the Trados plugin section of the GitBook documentation.
        /// </summary>
        private const string DocsBaseUrl = "https://supervertaler.gitbook.io/help/supervertaler-for-trados";

        /// <summary>
        /// Generic docs home (not the Trados section).
        /// </summary>
        private const string DocsHomeUrl = "https://supervertaler.gitbook.io/help";

        /// <summary>
        /// Help topic identifiers. Each maps to a specific documentation page path
        /// relative to the Trados plugin docs section.
        /// </summary>
        public static class Topics
        {
            public const string Overview           = "overview";
            public const string Installation       = "installation";
            public const string GettingStarted     = "getting-started";
            public const string TermLensPanel      = "termlens";
            public const string AddTermDialog      = "termlens/adding-terms";
            public const string TermPickerDialog   = "termlens/term-picker";
            public const string AiAssistantChat    = "ai-assistant";
            public const string BatchTranslate     = "batch-translate";
            public const string TermbaseEditor     = "termbase-management";
            public const string SettingsTermLens   = "settings/termlens";
            public const string SettingsAi         = "settings/ai-settings";
            public const string SettingsPrompts    = "settings/prompts";
            public const string MultiTermSupport   = "multiterm-support";
            public const string KeyboardShortcuts  = "keyboard-shortcuts";
            public const string Troubleshooting    = "troubleshooting";
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
