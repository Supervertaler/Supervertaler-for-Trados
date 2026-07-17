using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Runs before any ViewPart is instantiated.
    ///
    ///  1. Pre-loads e_sqlite3.dll (the native SQLite library used by
    ///     SQLitePCLRaw / Microsoft.Data.Sqlite) by full path so that
    ///     Windows finds it before any other copy on the DLL search path.
    ///
    ///  2. Registers an AssemblyResolve handler so all managed DLLs we ship
    ///     (Microsoft.Data.Sqlite, SQLitePCLRaw, System.Memory, etc.) are
    ///     resolved from our plugin directory.  Trados Studio ships older
    ///     versions of several System.* polyfill DLLs; our handler ensures
    ///     Microsoft.Data.Sqlite gets the versions it was compiled against.
    /// </summary>
    [ApplicationInitializer]
    public class AppInitializer : IApplicationInitializer
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string fileName);

        public void Execute()
        {
            // Install global crash handlers FIRST, so any fatal/unhandled exception
            // from here on is captured to the diagnostic log even when verbose
            // diagnostic logging is switched off. A silent, no-dialog crash with an
            // empty log is otherwise impossible to diagnose.
            InstallCrashHandlers();
            try
            {
                Core.DiagnosticLog.WriteAlways("Startup",
                    "Supervertaler for Trados v" + (UpdateChecker.GetCurrentVersion() ?? "?") + " starting.");
            }
            catch { }

            // Check for stale Unpacked folder (user installed a newer .sdlplugin
            // but Trados didn't re-extract).  If detected, rename the old folder
            // and prompt for restart.
            if (HandlePendingUpdate())
                return;

            // Enable TLS 1.2+ for HTTPS API calls (OpenAI, Anthropic, Google)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // Record token usage for EVERY AI call, independent of the Assistant
            // pane. The pane's own handler only updates the Reports-tab UI now, so
            // usage logging works even if the pane is never opened this session.
            try { UsageLogger.EnsureSubscribed(); } catch { }

            // Start the Supervertaler bridge (MCP / Workbench) independent of the
            // Assistant pane. The bridge lives in AiAssistantViewPart, which Trados
            // instantiates lazily only when its pane is first activated – so a user
            // who works only in TermLens (or never opens the pane) would have no
            // bridge, and the MCP connection would silently fail. Here we force the
            // pane's controller to load once a document is open in the editor, which
            // runs its Initialize() and starts the bridge regardless of layout.
            try { EnsureBridgeViewPartLoads(); } catch { }

            // Order matters:
            // 1. AssemblyResolve first - so managed SQLitePCLRaw DLLs can be found
            // 2. PreloadNativeSQLite - pins e_sqlite3.dll in the Windows module table
            // 3. Explicit Batteries init - ensures the provider is set up before
            //    any SqliteConnection is created (its static constructor does the
            //    same, but by that point the native DLL search may have already failed
            //    on non-standard environments like Windows on ARM / Parallels)
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            PreloadNativeSQLite();

            // Call SQLitePCL.Batteries_V2.Init() via reflection (no compile-time
            // dependency on the transitive package).
            //
            // The TYPE LIVES IN THE `SQLitePCL` NAMESPACE, not `SQLitePCLRaw`.
            // The package name and the namespace differ — easy to get wrong.
            // Init() loads e_sqlite3, calls Setup on the dynamic_cdecl provider,
            // and calls raw.SetProvider in one shot — everything Microsoft.Data.Sqlite
            // needs before its first Open(). Under Studio 2024 (x86) this Init()
            // call silently no-op'd because of the wrong namespace, but some other
            // Trados-loaded assembly transitively triggered SQLite init. Studio 2026
            // (x64) doesn't have that lucky side-effect, so the typo surfaced.
            // SQLite provider registration via reflection. CRITICAL: load
            // SQLitePCLRaw.batteries_v2 by ABSOLUTE PATH from the plugin folder, not
            // by short name. Assembly.Load("SQLitePCLRaw.batteries_v2") returns
            // whatever Trados already has loaded (Studio 2026 has its own SQLitePCL
            // 2.1.2 loaded by startup), but Microsoft.Data.Sqlite is bound via our
            // AssemblyResolve handler to OUR plugin-folder SQLitePCLRaw 2.1.6 copy.
            // Calling Batteries_V2.Init() on Trados's instance sets the provider on
            // Trados's `raw` static state — invisible to our Microsoft.Data.Sqlite,
            // which then errors with "You need to call SQLitePCL.raw.SetProvider()".
            // LoadFrom() with the explicit plugin path forces our instance, so Init()
            // runs against the SAME core SQLitePCLRaw that Microsoft.Data.Sqlite uses.
            //
            // The namespace is "SQLitePCL.Batteries_V2" — NOT "SQLitePCLRaw.Batteries_V2".
            // Easy trap: the NuGet package is SQLitePCLRaw.* but the classes live in
            // the SQLitePCL namespace.
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var batteriesPath = Path.Combine(pluginDir, "SQLitePCLRaw.batteries_v2.dll");
                if (File.Exists(batteriesPath))
                {
                    // Pre-load our SQLitePCLRaw.core so Init's typeref to `raw` resolves
                    // to the same instance Microsoft.Data.Sqlite ends up using.
                    var corePath = Path.Combine(pluginDir, "SQLitePCLRaw.core.dll");
                    if (File.Exists(corePath))
                        Assembly.LoadFrom(corePath);

                    var asm = Assembly.LoadFrom(batteriesPath);
                    var type = asm?.GetType("SQLitePCL.Batteries_V2");
                    var init = type?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                    init?.Invoke(null, null);
                }
            }
            catch
            {
                // SqliteConnection.Open() will surface a descriptive error if the
                // provider truly isn't registered.
            }

            // First-run setup: show folder-selection dialog when no config.json exists yet
            if (UserDataPath.NeedsFirstRunSetup)
            {
                using (var dlg = new SetupDialog())
                    dlg.ShowDialog();
                // If the user cancelled, SetRoot was never called; Root falls back to
                // ~/Supervertaler/ which is the correct default anyway.
            }

            // One-time migration from %LocalAppData%\Supervertaler.Trados\ -> new location
            UserDataPath.MigrateIfNeeded();

            // Multi-memory-bank migration: if a legacy single-bank folder exists and
            // the new memory-banks/ root does not, ask the user to name their existing
            // bank so it can be moved into the new layout. Silently skipped on fresh
            // installs or installations that already use the multi-bank layout (e.g.
            // because the Python Supervertaler Assistant migrated first).
            MigrateLegacyMemoryBankIfNeeded();

            // Initialize licensing - loads cached state, triggers background validation
            LicenseManager.Instance.InitializeAsync();
        }

        // ── Global crash handlers ────────────────────────────────────

        private static bool _crashHandlersInstalled;
        private static bool _bridgeLoaderArmed;

        /// <summary>
        /// Ensures AiAssistantViewPart (which hosts the Supervertaler bridge) is
        /// instantiated once a document is open, independent of whether the user
        /// ever activates the Assistant pane.
        ///
        /// The EditorController isn't available yet at ApplicationInitializer time
        /// (it's created when the Editor view first loads), so we wait on
        /// Application.Idle until it appears, then force the pane's controller via
        /// GetController on each document-open. Both GetController and the pane's
        /// own Initialize/StartSupervertalerBridge are idempotent, so repeated
        /// calls are cheap no-ops after the first. All best-effort: any failure
        /// leaves the pre-existing behaviour (bridge starts when the pane opens).
        /// </summary>
        private static void EnsureBridgeViewPartLoads()
        {
            if (_bridgeLoaderArmed) return;
            _bridgeLoaderArmed = true;

            EventHandler idle = null;
            idle = (s, e) =>
            {
                EditorController editor;
                try { editor = SdlTradosStudio.Application.GetController<EditorController>(); }
                catch { editor = null; }
                if (editor == null) return; // editor view not up yet – wait for next idle

                Application.Idle -= idle; // controller ready – stop polling

                void ForcePane()
                {
                    try { SdlTradosStudio.Application.GetController<AiAssistantViewPart>(); }
                    catch { }
                }

                try { editor.ActiveDocumentChanged += (s2, e2) => ForcePane(); }
                catch { }

                // A document may already be open by the time the controller appears.
                try { if (editor.ActiveDocument != null) ForcePane(); }
                catch { }
            };

            try { Application.Idle += idle; }
            catch { }
        }

        /// <summary>
        /// Subscribe to the process-wide unhandled-exception events and write any
        /// terminating exception to the diagnostic log, regardless of the verbose
        /// "Enable diagnostic logging" toggle. Idempotent. Managed exceptions are
        /// captured here; a native AccessViolation / StackOverflow can still bypass
        /// these (the host exe.config governs corrupted-state policy), in which case
        /// Windows Event Viewer remains the source of truth — but "log still empty
        /// even with handlers installed" is itself a useful diagnostic signal.
        /// </summary>
        private static void InstallCrashHandlers()
        {
            if (_crashHandlersInstalled) return;
            _crashHandlersInstalled = true;
            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    try { Core.DiagnosticLog.WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception); } catch { }
                    e.SetObserved();
                };

                // NOTE: we deliberately do NOT subscribe to Application.ThreadException.
                // Handling it makes WinForms SWALLOW the host's own UI-thread exceptions
                // and keep pumping the message loop — which turned Trados's fatal paint
                // failures (under memory pressure) into an unkillable hang instead of a
                // clean exit. Terminating cases are still captured by
                // AppDomain.UnhandledException above, without altering host behaviour.
            }
            catch { /* never let crash-handler setup break startup */ }
        }

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Core.DiagnosticLog.WriteCrash(
                    "AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")",
                    e.ExceptionObject);
            }
            catch { }
        }

        // ── Multi-memory-bank migration ─────────────────────────────

        /// <summary>
        /// If <see cref="UserDataPath.NeedsLegacyBankMigration"/> reports true,
        /// show the <see cref="LegacyMemoryBankMigrationDialog"/> and persist the
        /// chosen bank name to <c>AiSettings.ActiveMemoryBankName</c> on success.
        /// Wrapped in a try/catch so a misbehaving dialog never blocks plugin startup.
        /// </summary>
        private static void MigrateLegacyMemoryBankIfNeeded()
        {
            try
            {
                if (!UserDataPath.NeedsLegacyBankMigration)
                    return;

                using (var dlg = new LegacyMemoryBankMigrationDialog())
                {
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return; // User hit "Skip for now" - legacy folder stays put.

                    var chosen = dlg.ChosenBankName;
                    if (string.IsNullOrWhiteSpace(chosen))
                        return;

                    // Persist the chosen bank as the active one so every AI feature
                    // that opens later in this session finds the right vault.
                    var settings = TermLensSettings.Load();
                    if (settings.AiSettings == null)
                        settings.AiSettings = new AiSettings();
                    settings.AiSettings.ActiveMemoryBankName = chosen;
                    settings.Save();
                }
            }
            catch
            {
                // Non-fatal - the user can still rename the folder manually and set
                // the active bank from the settings dialog on a future session.
            }
        }

        // ── Stale-plugin detection ───────────────────────────────────

        private const string PluginFolderName = "Supervertaler for Trados";
        private const string PluginFileName   = "Supervertaler for Trados.sdlplugin";

        /// <summary>
        /// Detects if a newer .sdlplugin package has been installed but Trados
        /// is still running the old Unpacked copy.  If so, renames the stale
        /// Unpacked folder (so Trados re-extracts on next start) and prompts
        /// the user to restart.
        /// Returns true if a restart is needed (caller should skip init).
        /// </summary>
        private static bool HandlePendingUpdate()
        {
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (asmDir == null) return false;

                var unpackedRoot = Path.GetDirectoryName(asmDir);   // ...\Plugins\Unpacked\
                if (unpackedRoot == null) return false;

                // 1. Clean up .old folder from a previous update cycle
                var oldDir = Path.Combine(unpackedRoot, PluginFolderName + ".old");
                if (Directory.Exists(oldDir))
                {
                    try { Directory.Delete(oldDir, true); } catch { }
                }

                // 2. Get our running version
                var currentVersion = UpdateChecker.GetCurrentVersion();
                if (string.IsNullOrEmpty(currentVersion)) return false;

                // 3. Find the newest .sdlplugin across all plugin locations
                var newestPackage = FindNewestPackage();
                if (newestPackage == null) return false;

                // 4. Read version from the .sdlplugin (ZIP/OPC) manifest
                var packageVersion = ReadPackageVersion(newestPackage);
                if (string.IsNullOrEmpty(packageVersion)) return false;

                // Normalize: manifest has 4-part (4.12.3.0), strip trailing ".0"
                if (packageVersion.EndsWith(".0"))
                    packageVersion = packageVersion.Substring(0, packageVersion.Length - 2);

                // 5. Compare - if package is not newer, we're up to date
                if (UpdateChecker.CompareVersions(packageVersion, currentVersion) <= 0)
                    return false;

                // 6. Package is newer - rename Unpacked folder and prompt restart
                try
                {
                    Directory.Move(asmDir, oldDir);
                }
                catch
                {
                    // Rename failed (rare) - still show the restart message
                }

                MessageBox.Show(
                    "Supervertaler for Trados has been updated to v" + packageVersion + ".\n\n" +
                    "Please close and restart Trados Studio to load the new version.",
                    "Supervertaler - Update Installed",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                return true;
            }
            catch
            {
                return false;  // Any failure -> continue normally
            }
        }

        /// <summary>
        /// Searches all three Trados plugin Packages folders for our .sdlplugin
        /// and returns the path to the one with the newest version.
        /// </summary>
        private static string FindNewestPackage()
        {
            var locations = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Trados", "Trados Studio", "18", "Plugins", "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Trados", "Trados Studio", "18", "Plugins", "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Trados", "Trados Studio", "18", "Plugins", "Packages"),
            };

            string bestPath = null;
            string bestVersion = null;

            foreach (var dir in locations)
            {
                var pkg = Path.Combine(dir, PluginFileName);
                if (!File.Exists(pkg)) continue;

                var ver = ReadPackageVersion(pkg);
                if (string.IsNullOrEmpty(ver)) continue;

                var normalizedVer = ver.EndsWith(".0") ? ver.Substring(0, ver.Length - 2) : ver;
                if (bestVersion == null || UpdateChecker.CompareVersions(normalizedVer, bestVersion) > 0)
                {
                    bestVersion = normalizedVer;
                    bestPath = pkg;
                }
            }

            return bestPath;
        }

        /// <summary>
        /// Opens the .sdlplugin (OPC/ZIP) and reads the &lt;Version&gt; element
        /// from pluginpackage.manifest.xml inside it.
        /// </summary>
        private static string ReadPackageVersion(string sdlpluginPath)
        {
            try
            {
                using (var stream = File.OpenRead(sdlpluginPath))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry("pluginpackage.manifest.xml");
                    if (entry == null) return null;

                    using (var reader = new StreamReader(entry.Open()))
                    {
                        var xml = reader.ReadToEnd();
                        var match = Regex.Match(xml, @"<Version>([^<]+)</Version>");
                        return match.Success ? match.Groups[1].Value : null;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // ── Native SQLite preloading ────────────────────────────────

        /// <summary>
        /// Loads e_sqlite3.dll from our plugin's runtimes/ folder by absolute path,
        /// pinning it in the Windows module table before SQLitePCLRaw initialises.
        /// Unlike System.Data.SQLite's "SQLite.Interop.dll" this uses standard
        /// SQLite C entry points - no version-hash matching issues.
        ///
        /// Detects the process architecture using PROCESSOR_ARCHITECTURE env var
        /// to handle ARM64 (Windows on ARM / Parallels on Apple Silicon) in addition
        /// to x86 and x64.
        /// </summary>
        private static void PreloadNativeSQLite()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir == null) return;

            // Determine runtime identifier from actual process architecture.
            // PROCESSOR_ARCHITECTURE reflects the process, not the machine:
            //   x86 process (even on ARM64 machine) -> "x86"
            //   x64 process -> "AMD64"
            //   ARM64 native process -> "ARM64"
            var rid = GetProcessRid();

            // Try the detected architecture first, then fall back to alternatives.
            // This handles edge cases like running in Parallels on Apple Silicon
            // where the process might be ARM64 but we only have x64/x86 binaries,
            // or vice versa.
            var candidates = new[] { rid, "win-arm64", "win-x64", "win-x86", "win-arm" };

            foreach (var candidate in candidates)
            {
                var path = Path.Combine(pluginDir, "runtimes", candidate, "native", "e_sqlite3.dll");
                if (File.Exists(path))
                {
                    var handle = LoadLibrary(path);
                    if (handle != IntPtr.Zero)
                    {
                        // CRITICAL: Also copy to root plugin dir so SQLitePCLRaw's
                        // own NativeLibrary.TryLoad() finds it.  On Windows on ARM,
                        // SQLitePCLRaw detects the MACHINE architecture (ARM64) rather
                        // than the PROCESS architecture (x86), so its runtimes/ search
                        // picks the wrong binary.  Its fallback is {pluginDir}/e_sqlite3.dll.
                        try
                        {
                            var rootCopy = Path.Combine(pluginDir, "e_sqlite3.dll");
                            if (!File.Exists(rootCopy))
                                File.Copy(path, rootCopy);
                        }
                        catch
                        {
                            // Non-fatal - our LoadLibrary already pinned it in the
                            // module table.  If Batteries_V2.Init() also fails, the
                            // user gets a descriptive error on first database access.
                        }

                        return; // Successfully loaded
                    }
                }
            }
        }

        /// <summary>
        /// Maps PROCESSOR_ARCHITECTURE to a .NET runtime identifier (RID).
        /// </summary>
        private static string GetProcessRid()
        {
            var arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "")
                .ToUpperInvariant();

            switch (arch)
            {
                case "ARM64": return "win-arm64";
                case "ARM":   return "win-arm";
                case "AMD64": return "win-x64";
                default:      return "win-x86";
            }
        }

        /// <summary>
        /// Assemblies we ship alongside TermLens.dll.  When the CLR cannot resolve
        /// one of these from Trados Studio's probing paths (or finds a version that
        /// is too old), this handler loads our copy from the plugin directory.
        /// </summary>
        private static readonly string[] ManagedAssemblies = new[]
        {
            "Microsoft.Data.Sqlite",
            "SQLitePCLRaw.core",
            "SQLitePCLRaw.batteries_v2",
            "SQLitePCLRaw.provider.dynamic_cdecl",
            "System.Memory",
            "System.Buffers",
            "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe",
        };

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;

            bool isOurs = false;
            foreach (var asm in ManagedAssemblies)
            {
                if (string.Equals(name, asm, StringComparison.OrdinalIgnoreCase))
                {
                    isOurs = true;
                    break;
                }
            }
            if (!isOurs) return null;

            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir == null) return null;

            var dllPath = Path.Combine(pluginDir, name + ".dll");
            if (File.Exists(dllPath))
                return Assembly.LoadFrom(dllPath);

            return null;
        }
    }
}
