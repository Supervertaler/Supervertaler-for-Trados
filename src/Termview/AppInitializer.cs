using System;
using System.IO;
using System.Reflection;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;

namespace Termview
{
    /// <summary>
    /// Registers an AssemblyResolve handler so that third-party DLLs
    /// (System.Data.SQLite) can be loaded from the plugin's own directory.
    /// This runs before any ViewPart is instantiated.
    /// </summary>
    [ApplicationInitializer]
    public class AppInitializer : IApplicationInitializer
    {
        public void Execute()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            // Only handle assemblies we ship
            var name = new AssemblyName(args.Name);
            if (!name.Name.StartsWith("System.Data.SQLite", StringComparison.OrdinalIgnoreCase))
                return null;

            // Look in the same directory as this plugin assembly
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir == null)
                return null;

            var dllPath = Path.Combine(pluginDir, name.Name + ".dll");
            if (File.Exists(dllPath))
                return Assembly.LoadFrom(dllPath);

            return null;
        }
    }
}
