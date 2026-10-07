using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            settings = settings ?? SettingsService.Current;

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
            // Builds from before 2026-07-02 use the OLD single sequence (4.x,
            // covering both Studio generations). Nothing in the catalogue carries
            // major 4 any more, so matching on the running build's own major left
            // every 4.x install permanently "up to date" — never offered an
            // update, and, now that the plugin is App-Store-only, with no other
            // way to hear about one. For those builds, target the generation of
            // the Studio that is actually running.
            int targetMajor = currentMajor;
            if (currentMajor < FirstStudioAlignedMajor)
            {
                targetMajor = DetectRunningStudioMajor() ?? FirstStudioAlignedMajor;
                DiagnosticLog.Log("UpdateChecker",
                    $"Legacy build {currentVersion}: offering {targetMajor}.x updates.");
            }

            string bestTag = null;
            string bestDownloadUrl = null;
            foreach (var cv in entry.Versions)
            {
                var tag = NormaliseVersion(cv.Version);
                if (string.IsNullOrEmpty(tag)) continue;
                ParseVersion(tag, out int major, out _, out _, out _);
                if (major != targetMajor) continue;
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

            // "Not now" silences every update prompt for a week, whatever the
            // version. Checked after the version comparison so the snooze only
            // suppresses a prompt that would otherwise have been shown.
            if (settings.IsUpdateSnoozed())
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
        /// <summary>
        /// First plugin major that encodes its target Studio generation
        /// (18 = Studio 2024, 19 = Studio 2026). Anything below this is from the
        /// old single 4.x sequence, which covered both generations.
        /// </summary>
        private const int FirstStudioAlignedMajor = 18;

        /// <summary>
        /// Major version of the Trados Studio actually running (18 or 19), read
        /// from the host executable, or null if it can't be determined.
        ///
        /// Needed only for legacy 4.x plugin builds: their own version says
        /// nothing about which Studio they target, because the old numbering was
        /// a single sequence shared by both.
        /// </summary>
        private static int? DetectRunningStudioMajor()
        {
            try
            {
                var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
                if (v != null && v.Major >= FirstStudioAlignedMajor)
                    return v.Major;
            }
            catch { /* fall through to the caller's default */ }
            return null;
        }

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

        /// <summary>Where the running copy is installed.</summary>
        internal sealed class Install
        {
            public string StudioKey;    // "18" (Studio 2024) or "19" (Studio 2026)
            public string PackagesDir;  // ...\Trados Studio\<key>\Plugins\Packages
            public string PackagePath;  // the .sdlplugin this copy was unpacked from
            public string UnpackedDir;  // ...\Plugins\Unpacked\<package name>, where this DLL runs
            public bool AllUsers;       // under ProgramData: "This computer for all users"
        }

        /// <summary>
        /// Where the running copy came from. Studio unpacks
        /// &lt;Plugins&gt;\Packages\&lt;name&gt;.sdlplugin into &lt;Plugins&gt;\Unpacked\&lt;name&gt;\,
        /// so the folder this DLL runs from names the Studio version, the install
        /// scope and the package's real file name. All three used to be guessed
        /// ("18" and "Supervertaler for Trados.sdlplugin" hard-coded), which sent a
        /// Studio 2026 update into Studio 2024's folder - on a computer with both
        /// Studios, overwriting the 2024 copy with a build 2024 cannot load. Null
        /// when not running from an Unpacked folder (a test harness, a copied DLL).
        /// </summary>
        internal static Install RunningInstall() =>
            InstallFor(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

        internal static Install InstallFor(string asmDir)
        {
            try
            {
                var unpackedRoot = Path.GetDirectoryName(asmDir);
                if (unpackedRoot == null ||
                    !string.Equals(Path.GetFileName(unpackedRoot), "Unpacked", StringComparison.OrdinalIgnoreCase))
                    return null;
                var pluginsRoot = Path.GetDirectoryName(unpackedRoot);
                var studioDir = pluginsRoot == null ? null : Path.GetDirectoryName(pluginsRoot);
                if (studioDir == null) return null;
                var packagesDir = Path.Combine(pluginsRoot, "Packages");
                var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\') + "\\";
                return new Install
                {
                    StudioKey = Path.GetFileName(studioDir),
                    PackagesDir = packagesDir,
                    PackagePath = Path.Combine(packagesDir, Path.GetFileName(asmDir) + ".sdlplugin"),
                    UnpackedDir = asmDir,
                    AllUsers = asmDir.StartsWith(common, StringComparison.OrdinalIgnoreCase)
                };
            }
            catch { return null; }
        }

        /// <summary>The Packages folders of the three install scopes (Roaming,
        /// Local, ProgramData) for one Studio version folder ("18", "19").</summary>
        internal static string[] PackagesDirs(string studioKey) => new[]
        {
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
        }.Select(f => Path.Combine(Environment.GetFolderPath(f), "Trados", "Trados Studio", studioKey, "Plugins", "Packages"))
         .ToArray();

        /// <summary>
        /// The Studio version folder this build belongs to when the install cannot
        /// be read from where it runs: its own major (18 = Studio 2024, 19 = Studio
        /// 2026), or the running Studio's for a legacy 4.x build.
        /// </summary>
        internal static string BuildStudioKey()
        {
            ParseVersion(GetCurrentVersion() ?? "", out int major, out _, out _, out _);
            if (major < FirstStudioAlignedMajor) major = DetectRunningStudioMajor() ?? FirstStudioAlignedMajor;
            return major.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether this Windows user may replace <paramref name="path"/>: opens it
        /// for writing without changing it, or, if it does not exist yet, makes and
        /// drops a probe file beside it. Only a refusal counts as no - a file that is
        /// merely in use is a different problem, reported when it happens. On a
        /// "for all users" install it is the normal answer for anyone but the
        /// administrator.
        /// </summary>
        internal static bool CanReplace(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                    return true;
                }
                var probe = Path.Combine(Path.GetDirectoryName(path), ".sv-write-test-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (IOException) { return true; }
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
