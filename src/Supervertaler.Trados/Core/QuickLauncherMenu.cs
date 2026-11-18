using System;
using System.Collections.Generic;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// What the QuickLauncher menu (Alt+Q) lists, where that is a rule and not
    /// a menu item. No state and no library of its own, so it can be checked
    /// without Studio.
    /// </summary>
    internal static class QuickLauncherMenu
    {
        /// <summary>
        /// One folder's prompts as the menu lists them: where a prompt tagged for
        /// Trados (<c>app: "trados"</c>, file "Define [Trados].md") has a twin of
        /// the same name that is not (another Supervertaler product sharing the
        /// library writes "Define.md"), only the tagged one is listed. The tag is
        /// not shown, so the menu read "Define" twice. Names are compared as the
        /// menu shows them (<see cref="PromptTemplate.MenuLabel"/>), ignoring case.
        /// An untagged prompt with a name of its own stays, and so do two untagged
        /// prompts of one name: without a tagged one there is nothing to choose by.
        /// Order is kept. No file is touched: the twin stays in the library, in
        /// the Prompt Manager, and on a number key it was given or falls on
        /// (slots go by file path and by position among all QuickLauncher
        /// prompts, see <see cref="QuickLauncherSlotRunner"/>).
        ///
        /// <para>Scale: one pass to collect the tagged names and one to filter,
        /// per folder, each time the menu opens.</para>
        /// </summary>
        public static List<PromptTemplate> WithoutTwins(IList<PromptTemplate> prompts)
        {
            var result = new List<PromptTemplate>();
            if (prompts == null) return result;

            var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in prompts)
                if (IsForTrados(p)) tagged.Add(LabelOf(p));

            foreach (var p in prompts)
                if (p != null && (IsForTrados(p) || !tagged.Contains(LabelOf(p))))
                    result.Add(p);
            return result;
        }

        private static bool IsForTrados(PromptTemplate p) =>
            p != null && string.Equals((p.App ?? "").Trim(), "trados", StringComparison.OrdinalIgnoreCase);

        private static string LabelOf(PromptTemplate p) => (p.MenuLabel ?? "").Trim();
    }
}
