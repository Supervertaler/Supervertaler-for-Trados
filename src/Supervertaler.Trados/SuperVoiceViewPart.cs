using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Dockable ViewPart for SuperVoice (issue #129) - the microphone, its state,
    /// and what it has heard.
    ///
    /// <para>The voice indicators used to live only in the TermLens header, which
    /// meant a translator who does not keep TermLens open saw none of them, and
    /// every message flashed for a few seconds in a panel belonging to another
    /// feature. Studio lets a pane be docked anywhere and remembers where, so a
    /// narrow strip beside the editor costs nothing and is always readable.</para>
    ///
    /// <para>Docked bottom and unpinned by default: it is a companion to the editor,
    /// not something to give the right-hand column to. Whoever wants it elsewhere
    /// drags it once and Studio remembers.</para>
    /// </summary>
    [ViewPart(
        Id = "SuperVoiceViewPart",
        Name = "SuperVoice",
        Description = "Voice control for Trados Studio - commands, selection and dictation hand-off",
        Icon = "SuperVoiceIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Bottom, Pinned = false)]
    public class SuperVoiceViewPart : AbstractViewPartController
    {
        private static SuperVoiceControl _control;

        /// <summary>
        /// The pane's control, or null when the pane has never been shown. The voice
        /// manager asks for it to mirror state; a null answer means nobody is
        /// looking, which is the ordinary case and not a failure.
        /// </summary>
        internal static SuperVoiceControl TryGetControl()
        {
            var c = _control;
            return c != null && !c.IsDisposed ? c : null;
        }

        protected override IUIControl GetContentControl()
        {
            if (_control == null || _control.IsDisposed)
                _control = new SuperVoiceControl();
            return _control;
        }

        protected override void Initialize()
        {
            // Nothing to wire: the control subscribes to the activity log itself,
            // and the log is written whether or not this pane exists.
        }
    }
}
