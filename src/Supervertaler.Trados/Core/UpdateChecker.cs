using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Polls the RWS AppStore catalogue for a newer published version of plugin 432.
    /// The AppStore is the single source of truth – the plugin never installs
    /// unsigned builds from GitHub. GitHub releases remain as documentation only.
    /// </summary>
    public sealed class UpdateChecker
    {
        private static readonly HttpClient _http = new HttpClient();

        private const string AppStoreCatalogueUrl = "https://api-appstore.rws.com/app-store-api/v1/plugins";
        private const string ApiVersionHeaderName = "Apiversion";
        private const string ApiVersionHeaderValue = "2.0.0";
        private const int PluginId = 432;
        private const string ReleaseNotesUrlFormat =
            "https://github.com/Supervertaler/Supervertaler-for-Trados/releases/tag/v{0}";
        private const int CacheTtlHours = 24;

        static UpdateChecker()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-Trados-UpdateCheck/2.0");
        }

        /// <summary>
        /// Checks the RWS AppStore for a newer published version. Returns
        /// (newVersion, releaseNotesUrl, pluginDownloadUrl) if an update is
        /// available, or null if the user is up to date or the check fails.
        /// A 24-hour local cache avoids hammering the API across sessions.
        /// </summary>
        public static async Task<(string version, string url, string pluginUrl)?> CheckForUpdateAsync(
            TermLensSettings settings = null)
        {
            settings = settings ?? TermLensSettings.Load();

            // Cache first – skip the network entirely if the cached entry is fresh
            var entry = LoadCachedEntry();
            if (entry == null)
            {
                entry = await FetchEntryFromApiAsync().ConfigureAwait(false);
                if (entry != null) SaveCachedEntry(entry);
            }
            if (entry?.Versions == null || entry.Versions.Length == 0) return null;

            var currentVersion = GetCurrentVersion();
            if (string.IsNullOrEmpty(currentVersion)) return null;
            ParseVersion(currentVersion, out int currentMajor, out _, out _, out _);

            // Only offer updates that target the SAME Studio generation. The
            // version major encodes the target Studio (18.x = Studio 2024,
            // 19.x = Studio 2026), and the AppStore lists every generation's
            // build side by side. Without this filter a Studio 2024 user (18.x)
            // gets offered the 19.x build that only runs on Studio 2026.
            //
            // API returns 4-part versions (e.g. "18.20.86.0"); NormaliseVersion
            // trims to 3-part so the compare against SkippedUpdateVersion and the
            // assembly InformationalVersion (both 3-part) agree.
            string bestTag = null;
            string bestDownloadUrl = null;
            foreach (var cv in entry.Versions)
            {
                var tag = NormaliseVersion(cv.Version);
                if (string.IsNullOrEmpty(tag)) continue;
                ParseVersion(tag, out int major, out _, out _, out _);
                if (major != currentMajor) continue;
                if (bestTag == null || CompareVersions(tag, bestTag) > 0)
                {
                    bestTag = tag;
                    bestDownloadUrl = cv.DownloadUrl;
                }
            }
            if (bestTag == null) return null;

            if (CompareVersions(bestTag, currentVersion) <= 0) return null;

            if (string.Equals(settings.SkippedUpdateVersion, bestTag, StringComparison.OrdinalIgnoreCase))
                return null;

            var releaseUrl = string.Format(ReleaseNotesUrlFormat, bestTag);
            var pluginUrl = ToHttps(bestDownloadUrl);

            return (bestTag, releaseUrl, pluginUrl);
        }

        /// <summary>
        /// Gets the current InformationalVersion from the assembly.
        /// </summary>
        internal static string GetCurrentVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var attrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (attrs is AssemblyInformationalVersionAttribute[] infoAttrs && infoAttrs.Length > 0)
                return infoAttrs[0].InformationalVersion;

            var v = asm.GetName().Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }

        /// <summary>
        /// Compares two semantic version strings (e.g. "4.1.0-beta" vs "4.0.2-beta").
        /// Returns positive if a &gt; b, negative if a &lt; b, zero if equal.
        /// Pre-release (beta) sorts lower than release: 4.1.0-beta &lt; 4.1.0.
        /// </summary>
        internal static int CompareVersions(string a, string b)
        {
            ParseVersion(a, out int aMajor, out int aMinor, out int aPatch, out string aPre);
            ParseVersion(b, out int bMajor, out int bMinor, out int bPatch, out string bPre);

            var c = aMajor.CompareTo(bMajor);
            if (c != 0) return c;

            c = aMinor.CompareTo(bMinor);
            if (c != 0) return c;

            c = aPatch.CompareTo(bPatch);
            if (c != 0) return c;

            bool aHasPre = !string.IsNullOrEmpty(aPre);
            bool bHasPre = !string.IsNullOrEmpty(bPre);

            if (!aHasPre && !bHasPre) return 0;
            if (!aHasPre && bHasPre) return 1;
            if (aHasPre && !bHasPre) return -1;

            return string.Compare(aPre, bPre, StringComparison.OrdinalIgnoreCase);
        }

        private static void ParseVersion(string version, out int major, out int minor, out int patch, out string preRelease)
        {
            major = 0;
            minor = 0;
            patch = 0;
            preRelease = "";

            if (string.IsNullOrEmpty(version)) return;

            version = version.TrimStart('v');

            var hyphen = version.IndexOf('-');
            string numPart;
            if (hyphen >= 0)
            {
                numPart = version.Substring(0, hyphen);
                preRelease = version.Substring(hyphen + 1);
            }
            else
            {
                numPart = version;
            }

            var parts = numPart.Split('.');
            if (parts.Length >= 1) int.TryParse(parts[0], out major);
            if (parts.Length >= 2) int.TryParse(parts[1], out minor);
            if (parts.Length >= 3) int.TryParse(parts[2], out patch);
        }

        /// <summary>
        /// Downloads a file from a URL to a local path. Used by the one-click
        /// update flow to pull the AppStore-signed .sdlplugin.
        /// </summary>
        internal static async Task DownloadFileAsync(string url, string destinationPath)
        {
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await httpStream.CopyToAsync(fileStream).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Returns the Packages directory (…\Plugins\Packages) for the install
        /// scope where Supervertaler currently lives. This lets updates write
        /// back to whichever scope the user originally chose during Trados
        /// Plugin Installer (Roaming, LocalAppData, or ProgramData) instead of
        /// hard-coding Roaming and creating orphan duplicates. Falls back to
        /// Roaming if no existing install is found.
        /// </summary>
        internal static string FindCurrentInstallScopePackagesDir()
        {
            foreach (var dir in AllPackagesRoots())
            {
                var pkg = Path.Combine(dir, PluginFileName);
                if (File.Exists(pkg))
                    return dir;
            }
            return AllPackagesRoots()[0]; // Roaming fallback
        }

        /// <summary>
        /// Companion to <see cref="FindCurrentInstallScopePackagesDir"/> –
        /// returns the sibling Unpacked directory for the same install scope.
        /// </summary>
        internal static string FindCurrentInstallScopeUnpackedDir()
        {
            var pkgDir = FindCurrentInstallScopePackagesDir();
            var pluginsRoot = Path.GetDirectoryName(pkgDir);
            return Path.Combine(pluginsRoot, "Unpacked");
        }

        private const string PluginFileName = "Supervertaler for Trados.sdlplugin";

        private static string[] AllPackagesRoots()
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Trados", "Trados Studio", "18", "Plugins", "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Trados", "Trados Studio", "18", "Plugins", "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Trados", "Trados Studio", "18", "Plugins", "Packages"),
            };
        }

        // --- Cache -----------------------------------------------------------

        private static string CacheFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Supervertaler.Trados", "appstore_cache.json");

        private static CachedEntry LoadCachedEntry()
        {
            try
            {
                if (!File.Exists(CacheFilePath)) return null;
                var json = File.ReadAllText(CacheFilePath, Encoding.UTF8);
                var entry = DeserializeCache(json);
                // Reject empty/old-schema caches (pre-multi-version) so they get
                // refetched rather than silently suppressing the update check.
                if (entry?.Versions == null || entry.Versions.Length == 0) return null;
                if ((DateTime.UtcNow - entry.FetchedAtUtc).TotalHours >= CacheTtlHours) return null;
                return entry;
            }
            catch { return null; }
        }

        private static void SaveCachedEntry(CachedEntry entry)
        {
            try
            {
                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(CachedEntry));
                    serializer.WriteObject(stream, entry);
                    File.WriteAllBytes(CacheFilePath, stream.ToArray());
                }
            }
            catch
            {
                // Non-fatal – a stale or missing cache just means the next check
                // hits the API again.
            }
        }

        private static CachedEntry DeserializeCache(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(CachedEntry));
                    return (CachedEntry)serializer.ReadObject(stream);
                }
            }
            catch { return null; }
        }

        // --- API fetch -------------------------------------------------------

        private static async Task<CachedEntry> FetchEntryFromApiAsync()
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, AppStoreCatalogueUrl))
                {
                    req.Headers.TryAddWithoutValidation(ApiVersionHeaderName, ApiVersionHeaderValue);
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var catalogue = ParseCatalogue(json);
                        if (catalogue?.Value == null) return null;

                        foreach (var plugin in catalogue.Value)
                        {
                            if (plugin.Id != PluginId) continue;
                            if (plugin.Versions == null || plugin.Versions.Length == 0) return null;

                            // Keep ALL published versions – the AppStore lists a
                            // build per Studio generation (18.x, 19.x, …), and the
                            // caller picks the one matching the installed major.
                            var versions = new List<CachedVersion>();
                            foreach (var v in plugin.Versions)
                            {
                                if (string.IsNullOrEmpty(v.VersionNumber)) continue;
                                versions.Add(new CachedVersion
                                {
                                    Version = v.VersionNumber,
                                    DownloadUrl = v.DownloadUrl
                                });
                            }
                            if (versions.Count == 0) return null;

                            return new CachedEntry
                            {
                                FetchedAtUtc = DateTime.UtcNow,
                                Versions = versions.ToArray()
                            };
                        }
                    }
                }
            }
            catch
            {
                // Network / parse errors → treat as "no update info available"
            }
            return null;
        }

        private static AppStoreCatalogue ParseCatalogue(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppStoreCatalogue));
                    return (AppStoreCatalogue)serializer.ReadObject(stream);
                }
            }
            catch { return null; }
        }

        // --- Normalisation ---------------------------------------------------

        private static string NormaliseVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            return version.EndsWith(".0") ? version.Substring(0, version.Length - 2) : version;
        }

        private static string ToHttps(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "https://" + url.Substring(7);
            return url;
        }

        // --- Data contracts --------------------------------------------------

        [DataContract]
        private class AppStoreCatalogue
        {
            [DataMember(Name = "value")]
            public AppStorePlugin[] Value { get; set; }
        }

        [DataContract]
        private class AppStorePlugin
        {
            [DataMember(Name = "id")]
            public int Id { get; set; }

            [DataMember(Name = "versions")]
            public AppStoreVersion[] Versions { get; set; }
        }

        [DataContract]
        private class AppStoreVersion
        {
            [DataMember(Name = "versionNumber")]
            public string VersionNumber { get; set; }

            [DataMember(Name = "downloadUrl")]
            public string DownloadUrl { get; set; }
        }

        [DataContract]
        private class CachedEntry
        {
            [DataMember(Name = "fetchedAtUtc")]
            public DateTime FetchedAtUtc { get; set; }

            [DataMember(Name = "versions")]
            public CachedVersion[] Versions { get; set; }
        }

        [DataContract]
        private class CachedVersion
        {
            [DataMember(Name = "version")]
            public string Version { get; set; }

            [DataMember(Name = "downloadUrl")]
            public string DownloadUrl { get; set; }
        }
    }
}
