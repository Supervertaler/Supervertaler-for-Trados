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
    /// <para>Docked RIGHT by default, where it lands beside Translation Results and
    /// usually as a tab of it. That suits how the pane is actually read: the
    /// microphone's state has to be visible all the time and already is, in the
    /// TermLens header - what this pane holds is the history, which is looked at
    /// occasionally rather than watched. A tab costs no screen space until it is
    /// wanted. Whoever prefers it always visible drags it once and Studio
    /// remembers.</para>
    /// </summary>
    [ViewPart(
        Id = "SuperVoiceViewPart",
        Name = "SuperVoice",
        Description = "Voice control for Trados Studio - commands, selection and dictation hand-off",
        Icon = "SuperVoiceIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Right, Pinned = false)]
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
