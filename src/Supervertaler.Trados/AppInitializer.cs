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
            // Check for stale Unpacked folder (user installed a newer .sdlplugin
            // but Trados didn't re-extract).  If detected, rename the old folder
            // and prompt for restart.
            if (HandlePendingUpdate())
                return;

            // Enable TLS 1.2+ for HTTPS API calls (OpenAI, Anthropic, Google)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // Order matters:
            // 1. AssemblyResolve first — so managed SQLitePCLRaw DLLs can be found
            // 2. PreloadNativeSQLite — pins e_sqlite3.dll in the Windows module table
            // 3. Explicit Batteries init — ensures the provider is set up before
            //    any SqliteConnection is created (its static constructor does the
            //    same, but by that point the native DLL search may have already failed
            //    on non-standard environments like Windows on ARM / Parallels)
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            PreloadNativeSQLite();

            // Use reflection to call SQLitePCLRaw.Batteries_V2.Init() without
            // a compile-time dependency on the transitive package.
            try
            {
                var asm = Assembly.Load("SQLitePCLRaw.batteries_v2");
                var type = asm?.GetType("SQLitePCLRaw.Batteries_V2");
                var init = type?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                init?.Invoke(null, null);
            }
            catch
            {
                // Swallow — SqliteConnection's static ctor will retry.
                // If it also fails, the user gets a descriptive error
                // when they first try to use a database.
            }

            // First-run setup: show folder-selection dialog when no config.json exists yet
            if (UserDataPath.NeedsFirstRunSetup)
            {
                using (var dlg = new SetupDialog())
                    dlg.ShowDialog();
                // If the user cancelled, SetRoot was never called; Root falls back to
                // ~/Supervertaler/ which is the correct default anyway.
            }

            // One-time migration from %LocalAppData%\Supervertaler.Trados\ → new location
            UserDataPath.MigrateIfNeeded();

            // Multi-memory-bank migration: if a legacy single-bank folder exists and
            // the new memory-banks/ root does not, ask the user to name their existing
            // bank so it can be moved into the new layout. Silently skipped on fresh
            // installs or installations that already use the multi-bank layout (e.g.
            // because the Python Supervertaler Assistant migrated first).
            MigrateLegacyMemoryBankIfNeeded();

            // Initialize licensing — loads cached state, triggers background validation
            LicenseManager.Instance.InitializeAsync();
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
                        return; // User hit "Skip for now" — legacy folder stays put.

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
                // Non-fatal — the user can still rename the folder manually and set
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

                var unpackedRoot = Path.GetDirectoryName(asmDir);   // …\Plugins\Unpacked\
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

                // 5. Compare — if package is not newer, we're up to date
                if (UpdateChecker.CompareVersions(packageVersion, currentVersion) <= 0)
                    return false;

                // 6. Package is newer — rename Unpacked folder and prompt restart
                try
                {
                    Directory.Move(asmDir, oldDir);
                }
                catch
                {
                    // Rename failed (rare) — still show the restart message
                }

                MessageBox.Show(
                    "Supervertaler for Trados has been updated to v" + packageVersion + ".\n\n" +
                    "Please close and restart Trados Studio to load the new version.",
                    "Supervertaler — Update Installed",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                return true;
            }
            catch
            {
                return false;  // Any failure → continue normally
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
        /// SQLite C entry points — no version-hash matching issues.
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
            //   x86 process (even on ARM64 machine) → "x86"
            //   x64 process → "AMD64"
            //   ARM64 native process → "ARM64"
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
                            // Non-fatal — our LoadLibrary already pinned it in the
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
