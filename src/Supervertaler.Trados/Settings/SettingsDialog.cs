using System;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// The one way to open Supervertaler Settings.
    ///
    /// <para><b>Why this exists.</b> Three places built the dialog themselves and
    /// each handed it a different settings object — the AI Assistant passed its
    /// copy, TermLens passed its copy, and the About box passed a freshly loaded
    /// one. So "open Settings" meant three different things depending on which
    /// icon you clicked, and whichever one you used last reverted the others.
    /// Stage 2 made the object shared; this makes the *opening* shared, which is
    /// what a user means by "it should just open the settings".</para>
    ///
    /// <para>It also owns what has to happen afterwards. Both panels have to
    /// refresh, and previously each opener refreshed itself and then told the
    /// other by hand — a wiring that only existed in two of the three call sites,
    /// so opening Settings from the About box refreshed nothing at all.</para>
    ///
    /// <para><b>Why not on <see cref="SettingsService"/>.</b> That type is read
    /// from MCP bridge threads and deliberately knows nothing about WinForms.
    /// Putting a modal dialog on it would make the settings owner depend on a UI
    /// that most of its callers do not have.</para>
    /// </summary>
    public static class SettingsDialog
    {
        private const string LogCategory = "Settings";

        /// <summary>
        /// Opens the settings dialog on the shared settings instance and
        /// refreshes both panels afterwards.
        /// </summary>
        /// <param name="owner">Window to parent to; may be null.</param>
        /// <param name="promptLibrary">The caller's prompt library, so prompt
        /// edits are visible to it immediately. Null gets a fresh one.</param>
        /// <param name="defaultTab">Tab to open on — the only thing that should
        /// differ between one gear icon and another.</param>
        /// <returns>True if the user committed (OK, or an import, which rewrites
        /// the file whatever the user does next).</returns>
        public static bool Show(IWin32Window owner, PromptLibrary promptLibrary = null, int defaultTab = 0)
        {
            bool openExample;
            bool committed = ShowDialog(owner, promptLibrary, defaultTab, out openExample);
            // After the dialog has gone: the example asks its own questions.
            if (openExample) Supervertaler.Trados.Controls.ExampleProjectRunner.Start(owner);
            return committed;
        }

        private static bool ShowDialog(IWin32Window owner, PromptLibrary promptLibrary, int defaultTab, out bool openExample)
        {
            using (var form = new TermLensSettingsForm(promptLibrary, defaultTab))
            {
                // Live-sync of the active prompt rides on the static
                // PromptManagerPanel.ActivePromptChangedGlobal hook rather than
                // anything here, so it fires wherever the dialog was opened from.
                var result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();

                if (form.SettingsImported)
                {
                    // Import replaces the file wholesale, so the shared instance
                    // has to be re-read rather than saved.
                    SettingsService.Reload();
                }

                var committed = result == DialogResult.OK || form.SettingsImported;
                openExample = result == DialogResult.OK && form.OpenExampleProjectAfterClose;

                // Prompt deletions hit disk immediately, even if the user then
                // presses Cancel, so the library is refreshed either way.
                promptLibrary?.Refresh();

                // Both panels, always, regardless of which one opened the dialog
                // — that is the entire point of routing through here. Each is a
                // no-op when its panel has never been opened.
                //
                // Not hung off SettingsService.Changed: that fires on every save,
                // including the A+/A- font buttons, and TermLens's refresh forces
                // a full termbase reload that can take ~2 minutes on a cold
                // Studio 2026 cache. A refresh this expensive has to be asked
                // for explicitly, not triggered by any write that happens past.
                if (committed)
                {
                    SafeRefresh("TermLens", TermLensEditorViewPart.RefreshAfterSettingsChanged);
                    SafeRefresh("AI Assistant", AiAssistantViewPart.RefreshAfterSettingsChanged);
                }

                return committed;
            }
        }

        /// <summary>
        /// One panel failing to refresh must not stop the other, and must not
        /// throw out of a dialog the user has already closed — at that point the
        /// settings are saved and an exception would only look like the save
        /// failed.
        /// </summary>
        private static void SafeRefresh(string what, Action refresh)
        {
            try { refresh(); }
            catch (Exception ex)
            {
                DiagnosticLog.Log(LogCategory,
                    "Could not refresh " + what + " after a settings change: " + ex.Message);
            }
        }
    }
}
