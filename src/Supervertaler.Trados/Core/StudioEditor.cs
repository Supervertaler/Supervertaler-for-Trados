using System;
using System.Linq;
using System.Threading;
using Sdl.Core.PluginFramework;
using Sdl.Core.PluginFramework.Integration;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.Platform.PluginManagement;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Studio's <see cref="EditorController"/> and this plugin's own Editor panes,
    /// got without making Studio set up its Editor view before it shows it. Use
    /// <see cref="Controller"/> everywhere instead of
    /// <c>SdlTradosStudio.Application.GetController&lt;EditorController&gt;()</c>,
    /// and <see cref="Pane{T}"/> for a pane that may be needed before a document
    /// has been opened.
    ///
    /// <para><b>Why.</b> GetController&lt;EditorController&gt; does more than return
    /// the controller. The first time it is called it makes Studio set up the
    /// Editor view: every Editor pane (Messages, Translation Results and the rest,
    /// ours too) is created and wired to the view, and the view's tab control is
    /// created as well - but that control is only finished
    /// (<c>TabbedViewControl.Init</c>) when Studio first SHOWS the view. Studio
    /// itself sets the view up and shows it in one go, after a document has
    /// loaded, so on its own there is no gap. Called early, there is one: if the
    /// first document opened afterwards reports file-parsing messages while it
    /// loads ("SDL XLIFF validation error at line N"), the Messages pane changes
    /// its title, the view passes that on to the unfinished control, and Studio
    /// fails with a NullReferenceException in
    /// <c>TabbedViewControl.UpdateViewPartProperties</c> (its <c>_view</c> is
    /// still null). The document does not open, and only a restart helps. From
    /// 20.112 the bridge loader made that call about 1.5 s after Studio started,
    /// so every session whose first document had such messages failed this way.
    /// Opening any other document first hid it: that shows the view.</para>
    ///
    /// <para>Checked by decompiling Studio 2024, Studio 2026 and the 2026 beta,
    /// which all do this the same way.</para>
    ///
    /// <para><b>Not in Studio 2022.</b> There the controller is created as a
    /// plugin extension, not in Studio's container, and it subscribes to the
    /// editor's document events only when its view is set up, so GetController
    /// stays the only way to get a working one; both members do exactly what the
    /// code did before. Whether Studio 2022 has the same gap has not been
    /// checked.</para>
    /// </summary>
    internal static class StudioEditor
    {
#if !STUDIO17
        private static int _controllerFallbackLogged;
#endif

        /// <summary>
        /// The editor controller, or null outside Studio.
        ///
        /// Studio registers it as a singleton in its container, and its documents
        /// and events come from Studio's editor service, not from the view, so
        /// resolving it from the container gives the very object GetController
        /// returns (GetController resolves it the same way and only then sets up
        /// the view), with nothing set up. Its own Initialize is empty, and nothing
        /// this plugin calls on it needs the view. Should a later Studio stop
        /// registering it like that, this falls back to GetController - working,
        /// with the old gap - and says so once in the log.
        /// </summary>
        public static EditorController Controller
        {
            get
            {
#if STUDIO17
                return SdlTradosStudio.Application?.GetController<EditorController>();
#else
                string why;
                try
                {
                    var controller = StudioPluginManager.Resolve<EditorController>();
                    if (controller != null) return controller;
                    why = "the container returned null";
                }
                catch (Exception ex)
                {
                    why = ex.GetType().Name + ": " + ex.Message;
                }

                if (Interlocked.Exchange(ref _controllerFallbackLogged, 1) == 0)
                {
                    try
                    {
                        DiagnosticLog.WriteAlways("StudioEditor",
                            "EditorController not resolvable without the Editor view (" + why +
                            "); using GetController, which sets the view up early.");
                    }
                    catch { }
                }
                return SdlTradosStudio.Application?.GetController<EditorController>();
#endif
            }
        }

        /// <summary>
        /// One of this plugin's Editor panes, or null if Studio cannot give it yet.
        /// UI thread only.
        ///
        /// Studio creates a plugin's pane objects only when it sets up the view they
        /// belong to, and all of ours belong to the Editor view, so until then
        /// GetController for one of them returns null. The bridge loader used to get
        /// round that by asking for the EditorController first, which set the view
        /// up - the gap described on this class. Instead, this asks Studio's plugin
        /// framework for the pane exactly as Studio does for each plugin pane when
        /// it sets the view up (<c>ExtensionsFactory.Create&lt;IViewPart&gt;</c> on
        /// the pane's extension, in <c>ViewPartRegistryOperator</c>). The framework
        /// keeps what it creates, so GetController finds the pane straight away,
        /// and when Studio sets the view up later it gets this same object.
        /// Nothing else happens to the pane until then: the caller still runs its
        /// Initialize itself.
        ///
        /// If that fails, this returns null and logs why; the bridge loader then
        /// tries again when a document opens, by which time Studio has set up the
        /// view itself.
        /// </summary>
        public static T Pane<T>() where T : AbstractViewPartController
        {
            var app = SdlTradosStudio.Application;
            if (app == null) return null;

            var pane = app.GetController<T>();
#if STUDIO17
            return pane;
#else
            if (pane != null) return pane;   // already created: the view is set up, or we created it

            try
            {
                var extension = PluginManager.DefaultPluginRegistry
                    ?.GetExtensionPoint<ViewPartAttribute>()
                    ?.Extensions
                    .FirstOrDefault(e => e.ExtensionType == typeof(T));
                if (extension == null)
                {
                    DiagnosticLog.WriteAlways("StudioEditor", typeof(T).Name + ": no ViewPart extension found.");
                    return null;
                }
                ExtensionsFactory.Create<Sdl.Desktop.Platform.IViewPart>(extension);
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("StudioEditor", typeof(T).Name + ": could not be created: " + ex.GetType().Name + ": " + ex.Message); }
                catch { }
                return null;
            }

            pane = app.GetController<T>();
            if (pane == null)
                DiagnosticLog.WriteAlways("StudioEditor", typeof(T).Name + ": created, but GetController does not find it.");
            return pane;
#endif
        }
    }
}
