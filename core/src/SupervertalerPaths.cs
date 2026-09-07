using System;
using System.IO;
using System.Text;

namespace Supervertaler.Core
{
    /// <summary>
    /// The one folder every Supervertaler product shares.
    ///
    /// Extracted from the Trados plugin's <c>UserDataPath</c>, which is 1,400
    /// lines of paths specific to that plugin — settings, termbases, memory banks,
    /// runtime handshake files — and does not belong here. Only the shared root
    /// does: it is resolved from a pointer file that Supervertaler Workbench also
    /// writes, so a user who moved their data folder once has moved it for
    /// everything, and a <c>pricing.json</c> dropped there re-prices every product
    /// at once.
    ///
    /// The Trados plugin's <c>UserDataPath.Root</c> delegates here rather than
    /// keeping its own copy, so the two can never disagree about where a user's
    /// data lives.
    /// </summary>
    public static class SupervertalerPaths
    {
        private static string _root;
        private static readonly object _lock = new object();

        /// <summary>
        /// Pointer file, shared with Supervertaler Workbench:
        /// <c>%APPDATA%\Supervertaler\config.json</c>.
        /// </summary>
        private static string ConfigFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Supervertaler", "config.json");

        /// <summary>Where the data folder lives when nothing has pointed elsewhere.</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Supervertaler");

        /// <summary>
        /// Root of the shared Supervertaler user-data folder: whatever
        /// <c>config.json</c> points at, or <see cref="DefaultRoot"/>.
        ///
        /// Cached after the first read. A user who relocates their data folder
        /// mid-session is not a case worth re-statting the disk for on every
        /// lookup; <see cref="Reset"/> exists for the code that does the moving.
        /// </summary>
        public static string Root
        {
            get
            {
                lock (_lock)
                {
                    if (_root == null) _root = Resolve();
                    return _root;
                }
            }
        }

        /// <summary>
        /// Prompt library: <c>.md</c> files with YAML frontmatter, one per prompt,
        /// under the shared root.
        ///
        /// Shared deliberately and from the start — Workbench and the Trados
        /// plugin already read the same folder, and the memoQ plugin now makes
        /// three. A plain folder of Markdown is also why a prompt picker can be
        /// written in something other than C# without agreeing a format first.
        /// </summary>
        public static string PromptLibraryDir => Path.Combine(Root, "prompt_library");

        /// <summary>Shared resources folder (the Supervertaler database lives here).</summary>
        public static string ResourcesDir => Path.Combine(Root, "resources");

        /// <summary>Forgets the cached root. For code that has just relocated the folder.</summary>
        public static void Reset()
        {
            lock (_lock) _root = null;
        }

        /// <summary>Overrides the root, for a caller that has just chosen or moved it.</summary>
        public static void Set(string path)
        {
            lock (_lock) _root = path;
        }

        private static string Resolve()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile, Encoding.UTF8);
                    var path = ExtractJsonString(json, "user_data_path");
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch
            {
                // An unreadable or malformed pointer falls back to the default
                // rather than failing: losing a custom location costs the user a
                // re-pick, whereas throwing here would take down whatever asked.
            }

            return DefaultRoot;
        }

        /// <summary>
        /// Pulls one string value out of a flat JSON file.
        ///
        /// Hand-rolled rather than pulled from a serializer: this runs before
        /// anything else is initialised, in two different plugin sandboxes, and
        /// the file has exactly one key worth reading. Carried across from the
        /// Trados implementation unchanged so the two cannot diverge on a
        /// malformed file.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            var searchKey = "\"" + key + "\"";
            var idx = json.IndexOf(searchKey, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var valStart = json.IndexOf('"', idx + searchKey.Length + 1);
            if (valStart < 0) return null;

            var valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return null;

            return json.Substring(valStart + 1, valEnd - valStart - 1).Replace("\\\\", "\\");
        }
    }
}
