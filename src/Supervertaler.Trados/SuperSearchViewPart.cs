using System.Drawing;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Dockable ViewPart for SuperSearch — cross-file search, find &amp; replace,
    /// and click-to-navigate across all SDLXLIFF files in a Trados project.
    ///
    /// The UI and all search/replace/navigate logic live in
    /// <see cref="SuperSearchController"/> so the same control can alternatively
    /// be hosted as a tab inside the Supervertaler Assistant panel (the
    /// <c>SuperSearchInAssistantTab</c> setting). When that mode is active this
    /// ViewPart shows a placeholder pointing the user to the Assistant tab —
    /// Trados always registers the ViewPart from plugin.xml, so it can't be
    /// hidden outright.
    /// </summary>
    [ViewPart(
        Id = "SuperSearchViewPart",
        Name = "SuperSearch",
        Description = "Cross-file search and replace for Trados projects",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Bottom, Pinned = false)]
    public class SuperSearchViewPart : AbstractViewPartController
    {
        /// <summary>
        /// Provides access to the SuperSearch control for the context-menu action.
        /// Always returns the shared control regardless of which host owns it.
        /// </summary>
        public static SuperSearchControl GetControl()
        {
            return SuperSearchController.Shared.Control;
        }

        /// <summary>
        /// True when SuperSearch should be hosted as a tab in the Supervertaler
        /// Assistant panel rather than in this standalone ViewPart. Gated on an
        /// Assistant licence: an unlicensed Assistant panel is fully covered by
        /// the upgrade overlay, so without a licence we keep this standalone
        /// ViewPart working as the fallback host.
        /// </summary>
        public static bool IsHostedInAssistantTab()
        {
            return SettingsService.Current.SuperSearchInAssistantTab
                && LicenseManager.Instance.HasAssistantAccess;
        }

        protected override IUIControl GetContentControl()
        {
            if (IsHostedInAssistantTab())
                return new SuperSearchPlaceholderControl();

            return SuperSearchController.Shared.Control;
        }

        protected override void Initialize()
        {
            // In standalone mode, eagerly create the shared controller so it
            // wires up the EditorController (project-file scanning, document
            // change tracking) before the panel is first shown. In tab mode
            // AiAssistantViewPart owns that wiring instead.
            if (!IsHostedInAssistantTab())
            {
                var _ = SuperSearchController.Shared;
            }
        }

        /// <summary>
        /// Shown in the standalone SuperSearch ViewPart when SuperSearch is
        /// hosted as a tab in the Supervertaler Assistant panel instead.
        /// </summary>
        private sealed class SuperSearchPlaceholderControl : UserControl, IUIControl
        {
            public SuperSearchPlaceholderControl()
            {
                BackColor = Color.White;
                var lbl = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9f),
                    ForeColor = Color.FromArgb(110, 110, 110),
                    Text = "SuperSearch is docked as a tab in the Supervertaler Assistant panel.\n\n" +
                           "To use it as a separate panel again, turn off\n" +
                           "\"Show SuperSearch as a tab in the Supervertaler Assistant panel\"\n" +
                           "in Settings, then restart Trados Studio."
                };
                Controls.Add(lbl);
            }
        }
    }
}
