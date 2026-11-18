using System;
using System.Collections.Generic;
using System.Windows.Forms;
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

        /// <summary>One of the menu's built-in actions.</summary>
        internal sealed class BuiltIn
        {
            /// <summary>What the caller runs it by.</summary>
            public string Id;
            /// <summary>The menu text; the letter after '&amp;' picks it from the keyboard.</summary>
            public string Text;
            /// <summary>Its own shortcut in the Editor, as Supervertaler ships it.</summary>
            public string Shortcut;
            public string ToolTip;
        }

        /// <summary>
        /// The fixed top section of the menu (Michael, 18 Nov 2026): the three
        /// things Supervertaler does to the segment you are in. With the menu
        /// open, T, F or P runs one - so Alt+Q, F is FuzzyFixer, a key sequence
        /// no other program takes. Fixed, in this order, and not configurable:
        /// nobody has asked to vary it. The same actions stay in Studio's own
        /// context menu and keep their own shortcuts.
        ///
        /// <para>The shortcuts shown are the ones Supervertaler ships. Studio lets
        /// a user change them (File > Options > Keyboard Shortcuts), and the menu
        /// does not know when they have: it shows the shipped key, as it does for
        /// the numbered prompts.</para>
        /// </summary>
        internal static readonly BuiltIn[] BuiltIns =
        {
            new BuiltIn { Id = "translate", Text = "&Translate active segment", Shortcut = "Alt+T",
                ToolTip = "Translate the segment you are in with the AI" },
            new BuiltIn { Id = "fuzzyfixer", Text = "&FuzzyFixer", Shortcut = "Ctrl+Alt+F",
                ToolTip = "Have the AI repair the best fuzzy match for the segment you are in" },
            new BuiltIn { Id = "proofread", Text = "&Proofread active segment", Shortcut = "Ctrl+Alt+P",
                ToolTip = "Have the AI proofread the segment you are in" },
        };

        /// <summary>
        /// Adds <see cref="BuiltIns"/> to a menu. <paramref name="run"/> gets the
        /// id of the one chosen; it is called from the item's click, on the UI
        /// thread, after the menu has closed. A prompt whose name holds an '&amp;'
        /// could claim one of the three letters: WinForms then only moves the
        /// highlight between the two and Enter runs the highlighted one, so
        /// nothing runs by surprise.
        /// </summary>
        internal static void AddBuiltIns(ToolStripItemCollection items, Action<string> run)
        {
            if (items == null) return;
            foreach (var b in BuiltIns)
            {
                var id = b.Id;
                var item = new ToolStripMenuItem(b.Text)
                {
                    ShortcutKeyDisplayString = b.Shortcut,
                    ToolTipText = b.ToolTip,
                    Tag = id,
                };
                item.Click += (s, e) => run?.Invoke(id);
                items.Add(item);
            }
        }

        private static bool IsForTrados(PromptTemplate p) =>
            p != null && string.Equals((p.App ?? "").Trim(), "trados", StringComparison.OrdinalIgnoreCase);

        private static string LabelOf(PromptTemplate p) => (p.MenuLabel ?? "").Trim();
    }
}
