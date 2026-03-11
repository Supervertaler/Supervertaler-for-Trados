using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Supervertaler.Trados.Licensing;

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

            // Initialize licensing — loads cached state, triggers background validation
            LicenseManager.Instance.InitializeAsync();
        }

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
