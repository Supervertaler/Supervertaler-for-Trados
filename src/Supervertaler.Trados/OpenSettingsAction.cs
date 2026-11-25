using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.DefaultLocations;
using Sdl.Desktop.IntegrationApi.Extensions;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// "Supervertaler" group on Studio's Add-Ins tab. The Add-Ins tab is there in
    /// every view (Welcome, Projects, Files, Editor …), so this is the way into
    /// Settings when no document is open: every other way in (the gear icons on
    /// the Supervertaler panes) belongs to the Editor view.
    /// </summary>
    [RibbonGroup("Supervertaler_RibbonGroup", Name = "Supervertaler")]
    [RibbonGroupLayout(typeof(StudioDefaultRibbonTabs.AddinsRibbonTabLocation))]
    public class SupervertalerRibbonGroup : AbstractRibbonGroup
    {
    }

    /// <summary>
    /// Opens Supervertaler Settings from anywhere in Studio: the button in the
    /// Supervertaler group on the Add-Ins tab, or Ctrl+Alt+Shift+S.
    ///
    /// <para>A global action (no controller), so the shortcut works in every
    /// view, not only in the Editor. Ctrl+Alt+S was the first choice but is
    /// Studio's own Merge Segments in the Editor (checked in the Studio 2026
    /// DLLs, 25 Nov 2026); Ctrl+Alt+Shift+S is used by Studio only in the
    /// Alignment editor (Save alignment as). Users can change it in File >
    /// Options > Keyboard Shortcuts.</para>
    /// </summary>
    [Action("Supervertaler_OpenSettings",
        Name = "Supervertaler Settings",
        Description = "Open Supervertaler's settings (AI, TermLens, prompts, licence)",
        Icon = "SupervertalerIcon")]
    [ActionLayout(typeof(SupervertalerRibbonGroup), 10, DisplayType.Large, "", false)]
    [Shortcut(Keys.Control | Keys.Alt | Keys.Shift | Keys.S)]
    public class OpenSettingsAction : AbstractAction
    {
        // The dialog is modal, so Studio cannot normally send the shortcut while
        // it is open; this only guards against a second press queued before the
        // first dialog appeared. UI thread only, reset in finally.
        private static bool _open;

        protected override void Execute()
        {
            if (_open) return;
            _open = true;
            try
            {
                SettingsDialog.Show(null);
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("Settings", "Opening Settings from the Add-Ins tab failed: " + ex); } catch { }
                MessageBox.Show("Supervertaler Settings could not be opened: " + ex.Message,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _open = false;
            }
        }
    }
}
