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
        /// Base URL for the unified Supervertaler help site.
        /// Hosted on Cloudflare Pages from the <c>Supervertaler-Help</c>
        /// repo (Astro Starlight build). Trados Plugin pages live under
        /// <c>/trados/</c>; Workbench pages live under <c>/workbench/</c>.
        /// Migrated off GitBook in v4.19.x – the old
        /// <c>supervertaler.gitbook.io/help</c> URL kept working during
        /// the transition and is now eligible for sunset.
        /// </summary>
        private const string DocsBaseUrl = "https://docs.supervertaler.com";

        /// <summary>
        /// Help topic identifiers. Each value is appended to <see cref="DocsBaseUrl"/>
        /// to form a full URL.
        /// <para>
        /// The site uses <b>folder-based URLs</b>: every page's URL mirrors
        /// its on-disk path under <c>trados/</c> in the help repo (with
        /// <c>README.md</c> files mapping to the folder root). Trailing
        /// slashes are canonical; Astro will 301 from the slashless form,
        /// so omitting one still works but adds a hop.
        /// </para>
        /// <para>
        /// Authoritative URL map: <c>src/generated/sidebar.js</c> in the
        /// <c>Supervertaler-Help</c> repo. Regenerate that file with
        /// <c>python _migrate/generate_sidebar.py</c> after editing
        /// SUMMARY.md and the entries here line up 1:1 with the
        /// <c>link</c> fields there.
        /// </para>
        /// </summary>
        public static class Topics
        {
            public const string Overview            = "trados/";
            public const string Installation        = "trados/installation/";
            public const string GettingStarted      = "trados/getting-started/";
            public const string Licensing           = "trados/licensing/";
            public const string AiCostGuide         = "trados/ai-cost-guide/";

            public const string TermLensPanel       = "trados/termlens/";
            public const string AddTermDialog       = "trados/termlens/adding-terms/";
            public const string TermLensPopup       = "trados/termlens/termlens-popup/";
            // Renamed v4.20.25 to match the brand: page slug term-picker/ → termpicker/.
            // Old slug 301-redirects to the new one via public/_redirects on the help site.
            // Field name TermPickerDialog kept unchanged so all C# call sites stay stable.
            public const string TermPickerDialog    = "trados/termlens/termpicker/";
            public const string MultiTermSupport    = "trados/multiterm-support/";

            public const string AiAssistantChat     = "trados/ai-assistant/";
            public const string StudioTools         = "trados/ai-assistant/studio-tools/";

            // Memory banks (formerly "SuperMemory") – nested under the Supervertaler
            // Assistant section in SUMMARY.md. C# identifier names kept as
            // SuperMemory* for backwards-compat with existing call sites; rename
            // when the Trados UI strings are updated to match the new memory bank
            // terminology.
            public const string SuperMemory         = "trados/ai-assistant/super-memory/";
            public const string SuperMemoryQuickAdd = "trados/ai-assistant/super-memory/quick-add/";
            public const string SuperMemoryInbox    = "trados/ai-assistant/super-memory/process-inbox/";
            public const string SuperMemoryHealth   = "trados/ai-assistant/super-memory/health-check/";
            public const string SuperMemoryDistill  = "trados/ai-assistant/super-memory/distill/";
            public const string SuperMemoryObsidian = "trados/ai-assistant/super-memory/obsidian-setup/";

            public const string SuperSearch         = "trados/supersearch/";
            public const string QuickLauncher       = "trados/quicklauncher/";

            public const string BatchOperations     = "trados/batch-operations/";
            public const string BatchTranslate      = "trados/batch-translate/";
            public const string AiProofreader       = "trados/ai-proofreader/";
            public const string Reports             = "trados/reports/";
            public const string ClipboardMode       = "trados/clipboard-mode/";
            public const string GeneratePrompt      = "trados/generate-prompt/";
            public const string ImportExport       = "trados/import-export/";

            public const string TermbaseEditor      = "trados/termbase-management/";

            public const string SettingsTermLens    = "trados/settings/termlens/";
            public const string SettingsAi          = "trados/settings/ai-settings/";
            public const string PromptLogging       = "trados/settings/ai-settings/#prompt-logging";
            public const string SettingsPrompts     = "trados/settings/prompts/";
            public const string SettingsBackup      = "trados/settings/backup/";
            public const string SettingsUsageStats  = "trados/settings/usage-statistics/";
            public const string SettingsGeneral     = "trados/settings/usage-statistics/";
            public const string ProjectSettings     = "trados/settings/project-settings/";

            public const string KeyboardShortcuts   = "trados/keyboard-shortcuts/";
            public const string Troubleshooting     = "trados/troubleshooting/";
        }

        /// <summary>
        /// Opens the help page for the given topic identifier.
        /// Falls back to the Trados landing page if topic is null/empty.
        /// </summary>
        /// <remarks>
        /// Only the leading slash is trimmed from the topic; trailing
        /// slashes (the canonical form on the Astro site) are preserved
        /// so the page is served directly instead of via a 301 redirect.
        /// </remarks>
        public static void OpenHelp(string topic = null)
        {
            string url = string.IsNullOrEmpty(topic)
                ? DocsBaseUrl + "/" + Topics.Overview
                : DocsBaseUrl + "/" + topic.TrimStart('/');

            OpenUrl(url);
        }

        /// <summary>
        /// Opens the docs site root (the product chooser landing page).
        /// </summary>
        public static void OpenDocsHome()
        {
            OpenUrl(DocsBaseUrl);
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No default browser configured – silently ignore
            }
        }
    }
}
