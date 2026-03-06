using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;

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

            PreloadNativeSQLite();
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        /// <summary>
        /// Loads e_sqlite3.dll from our plugin's runtimes/ folder by absolute path,
        /// pinning it in the Windows module table before SQLitePCLRaw initialises.
        /// Unlike System.Data.SQLite's "SQLite.Interop.dll" this uses standard
        /// SQLite C entry points — no version-hash matching issues.
        /// </summary>
        private static void PreloadNativeSQLite()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir == null) return;

            var rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            var path = Path.Combine(pluginDir, "runtimes", rid, "native", "e_sqlite3.dll");

            if (File.Exists(path))
                LoadLibrary(path);
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
