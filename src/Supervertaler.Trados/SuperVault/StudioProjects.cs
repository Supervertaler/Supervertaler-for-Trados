using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// A document's Studio project name and customer, for the units EverTM learns from it. Studio doesn't pass them with a
    /// confirmed segment, so they are read from the .sdlproj in the document's folder or above it (a project's documents
    /// sit in a language folder under the project folder). Called on EverTM's learning thread
    /// (<see cref="Supervertaler.Core.SuperVault.VaultLibrary.ProjectOf"/>), never on Studio's UI thread.
    ///
    /// Cached per folder for the life of the process: one read per project, however many segments are confirmed. The
    /// .sdlproj is streamed and reading stops as soon as both values are found, so a large project file costs little.
    /// </summary>
    internal static class StudioProjects
    {
        const int MaxCached = 500;   // folders; a day's work touches a handful
        static readonly Dictionary<string, (string, string)> Cache = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        public static (string Project, string Client) Describe(string documentPath)
        {
            var dir = Path.GetDirectoryName(documentPath);
            if (string.IsNullOrEmpty(dir)) return (null, null);
            lock (Cache) if (Cache.TryGetValue(dir, out var known)) return known;
            var found = Read(dir);
            lock (Cache)
            {
                if (Cache.Count >= MaxCached) Cache.Clear();
                Cache[dir] = found;
            }
            return found;
        }

        static (string, string) Read(string dir)
        {
            for (int up = 0; up < 3 && !string.IsNullOrEmpty(dir); up++)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { files = new string[0]; }
                if (files.Length > 0) return ReadProject(files[0]);
                dir = Path.GetDirectoryName(dir);
            }
            return (null, null);
        }

        /// <summary>GeneralProjectInfo/@Name and Customer/@Name.</summary>
        internal static (string, string) ReadProject(string sdlproj)
        {
            string name = null, customer = null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
            using (var r = XmlReader.Create(sdlproj, settings))
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element) continue;
                    if (name == null && r.LocalName == "GeneralProjectInfo") name = r.GetAttribute("Name");
                    else if (customer == null && r.LocalName == "Customer") customer = r.GetAttribute("Name");
                    if (name != null && customer != null) break;
                }
            return (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), string.IsNullOrWhiteSpace(customer) ? null : customer.Trim());
        }
    }
}
