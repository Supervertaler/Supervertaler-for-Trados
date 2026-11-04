using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sdl.Core.PluginFramework;
using Sdl.Core.PluginFramework.Integration;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
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
    /// <para><b>By reflection, and why.</b> The two Studio parts this needs,
    /// <c>StudioPluginManager</c> and <c>IViewPart</c>, live in
    /// Sdl.Desktop.Platform, which is not on Studio's list of assemblies a plugin
    /// may reference (pluginconfig.xml). A plugin DLL that references it is
    /// refused when Studio starts ("Assembly reference is not public") and Studio
    /// runs without the plugin - which is what the first version of this fix did.
    /// So they are looked up by name in the copy Studio has loaded, and this file
    /// must never name a type from that assembly directly. build.sh checks every
    /// package against Studio's list (tools/StudioReferenceCheck) so that such a
    /// reference cannot be installed again.</para>
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
        private const string PlatformAssemblyName = "Sdl.Desktop.Platform";

        // Studio's singleton once found, so the reflection runs once per session
        // rather than on every MCP request and editor action. It lives as long as
        // Studio does, so it cannot go stale.
        private static EditorController _controller;
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
        /// this plugin calls on it needs the view. Should Studio's container not be
        /// reachable, this falls back to GetController - working, with the old gap -
        /// and says so once in the log.
        /// </summary>
        public static EditorController Controller
        {
            get
            {
#if STUDIO17
                return SdlTradosStudio.Application?.GetController<EditorController>();
#else
                var found = _controller;
                if (found != null) return found;

                string why;
                try
                {
                    var resolve = FindResolve();
                    if (resolve == null)
                    {
                        why = "StudioPluginManager.Resolve<T>() not found";
                    }
                    else
                    {
                        found = resolve.MakeGenericMethod(typeof(EditorController)).Invoke(null, null) as EditorController;
                        if (found != null)
                        {
                            _controller = found;
                            return found;
                        }
                        why = "the container returned null";
                    }
                }
                catch (Exception ex)
                {
                    var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                    why = inner.GetType().Name + ": " + inner.Message;
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

                var create = FindCreateViewPart();
                if (create == null)
                {
                    DiagnosticLog.WriteAlways("StudioEditor", typeof(T).Name +
                        ": cannot be created (IViewPart or ExtensionsFactory.Create<T>(IExtension) not found).");
                    return null;
                }
                var parameters = create.GetParameters();
                var args = new object[parameters.Length];
                args[0] = extension;
                for (int i = 1; i < parameters.Length; i++)
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                create.Invoke(null, args);
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                try { DiagnosticLog.WriteAlways("StudioEditor", typeof(T).Name + ": could not be created: " + inner.GetType().Name + ": " + inner.Message); }
                catch { }
                return null;
            }

            pane = app.GetController<T>();
            if (pane == null)
                DiagnosticLog.WriteAlways("StudioEditor", typeof(T).Name + ": created, but GetController does not find it.");
            return pane;
#endif
        }

#if !STUDIO17
        // The two lookups below only find members: no logging, nothing invoked, so
        // tools/studio_editor_test.ps1 can run them against Studio's own DLLs and
        // catch a renamed member, which would otherwise only show as the log line
        // and the old behaviour.

        /// <summary><c>StudioPluginManager.Resolve&lt;T&gt;()</c> (open generic), or null.</summary>
        internal static MethodInfo FindResolve() =>
            PlatformAssembly()
                ?.GetType(PlatformAssemblyName + ".PluginManagement.StudioPluginManager", false)
                ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Resolve" && m.IsGenericMethodDefinition
                                     && m.GetGenericArguments().Length == 1
                                     && m.GetParameters().Length == 0);

        /// <summary><c>ExtensionsFactory.Create&lt;Sdl.Desktop.Platform.IViewPart&gt;(IExtension, ...)</c>,
        /// ready to invoke, or null.</summary>
        internal static MethodInfo FindCreateViewPart()
        {
            var viewPartType = PlatformAssembly()?.GetType(PlatformAssemblyName + ".IViewPart", false);
            var create = typeof(ExtensionsFactory).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition
                                     && m.GetGenericArguments().Length == 1
                                     && m.GetParameters().Length >= 1
                                     && m.GetParameters()[0].ParameterType == typeof(IExtension));
            return viewPartType == null || create == null ? null : create.MakeGenericMethod(viewPartType);
        }

        /// <summary>The Sdl.Desktop.Platform Studio has loaded (it is loaded long
        /// before any plugin code runs), or null.</summary>
        private static Assembly PlatformAssembly() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, PlatformAssemblyName, StringComparison.Ordinal));
#endif
    }
}
