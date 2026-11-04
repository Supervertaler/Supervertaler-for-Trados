using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Availability probing and environment construction for the embedded
    /// WebView2 browser used by SuperSearch's "Embedded" web-results mode.
    ///
    /// <para>Two things have to be right or WebView2 fails in ways that are hard
    /// to read from a stack trace, and both are handled here:</para>
    ///
    /// <list type="number">
    /// <item><b>The Evergreen Runtime must be installed.</b> It ships with
    /// Windows 11 and reaches Windows 10 through Edge, so it is present almost
    /// everywhere — but "almost" is not "always", and a missing runtime must
    /// degrade to browser mode rather than throw.</item>
    /// <item><b>The user-data folder must be writable.</b> WebView2 defaults it
    /// to the *process* directory, which for a plugin is the Trados install
    /// folder under Program Files. That is not writable, and it is the single
    /// most common way WebView2 fails inside a Studio plugin.</item>
    /// </list>
    /// </summary>
    public static class WebView2Support
    {
        private const string LogCategory = "WebView2";

        private static bool _probed;
        private static string _version;

        /// <summary>
        /// The installed Evergreen Runtime version, or null when WebView2 is not
        /// usable on this machine. Probed once and cached — the answer cannot
        /// change without restarting Studio.
        /// </summary>
        public static string RuntimeVersion
        {
            get
            {
                if (_probed) return _version;
                _probed = true;
                try
                {
                    _version = ProbeVersion();
                    DiagnosticLog.Log(LogCategory,
                        string.IsNullOrEmpty(_version)
                            ? "No WebView2 runtime found"
                            : $"WebView2 runtime {_version}");
                }
                catch (Exception ex)
                {
                    // Catches two quite different failures with one net:
                    //   - the Evergreen Runtime is not installed (the API throws);
                    //   - Microsoft.Web.WebView2.Core could not be loaded at all,
                    //     which surfaces as a FileNotFoundException/TypeLoadException
                    //     when the JIT prepares ProbeVersion. We take the assemblies
                    //     from the Trados install folder, so that would mean a future
                    //     Studio stopped shipping them — unlikely, but it must
                    //     degrade to browser mode rather than take the pane down.
                    _version = null;
                    DiagnosticLog.Log(LogCategory,
                        $"WebView2 unavailable ({ex.GetType().Name}: {ex.Message})");
                }
                return _version;
            }
        }

        /// <summary>
        /// The only place a WebView2 type is touched during probing. Kept in its
        /// own non-inlined method so that if the assembly is missing, the load
        /// failure is raised on entry to <i>this</i> method — inside the caller's
        /// try block — instead of when the caller itself is JIT-compiled, where
        /// no catch could reach it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ProbeVersion()
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }

        /// <summary>True when embedded browsing can be used at all.</summary>
        public static bool IsAvailable => !string.IsNullOrEmpty(RuntimeVersion);

        /// <summary>
        /// Where WebView2 keeps its profile: cookies, sign-ins, cache, local storage.
        ///
        /// <para>Deliberately <b>not</b> under <see cref="Settings.UserDataPath"/>.
        /// That folder is user-chosen and shared with Workbench, so it is
        /// routinely on OneDrive or Google Drive — and a Chromium profile is a
        /// high-churn cache that must never be synced. This one is pinned to
        /// LocalApplicationData, which is always machine-local.</para>
        ///
        /// <para>And deliberately <b>not</b> in <c>%LocalAppData%\Supervertaler.Trados</c>:
        /// that is the pre-unification data folder, which
        /// <c>UserDataPath.CleanupLegacyFolders</c> deletes at every Studio start
        /// once it has been migrated. The profile lived there until 205, so every
        /// sign-in (ProZ, Juremy) and cookie choice was gone after a restart. The
        /// update checker's cache fell into the same trap and moved to the same
        /// folder (<see cref="UpdateChecker.CacheFilePath"/>);
        /// <c>%LocalAppData%\Supervertaler</c> is left alone. The old profile goes
        /// with the rest of the legacy folder at the next start.</para>
        ///
        /// <para>One folder for every Studio on the machine. WebView2 allows one
        /// browser process per profile; a second Studio opened alongside joins the
        /// first one's (tested with 2024 x86 and 2026 x64, runtime 156: same
        /// process, cookies shared live) and keeps working when the first closes
        /// or crashes. A host that cannot reach the running browser - one on
        /// another Windows desktop - fails per tab with 0x800700AA instead.</para>
        ///
        /// <para>It is also why signing in to ProZ or Juremy inside the embedded
        /// browser is a separate session from the one in the user's real browser:
        /// different profile, different cookie jar.</para>
        /// </summary>
        public static string UserDataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Supervertaler", "trados-webview2");

        /// <summary>
        /// Creates the shared WebView2 environment, or returns null when the
        /// runtime is missing or the profile folder cannot be created. Callers
        /// treat null as "fall back to opening results in the browser".
        /// </summary>
        public static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            if (!IsAvailable) return null;

            try
            {
                var folder = UserDataFolder;
                Directory.CreateDirectory(folder);

                // WebView2 does not report an unwritable profile folder as such.
                // Tested (runtime 156): with an existing profile every tab fails
                // with "resource in use" (0x800700AA); with a new, empty one the
                // window sits on "Loading…" for about a minute, fails, and leaves
                // a browser process behind. Writing a file first turns both into
                // the browser fallback, via the catch below. DeleteOnClose: the
                // file goes even if Studio dies before the using block ends.
                using (new FileStream(Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

                return await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,   // use the installed Evergreen runtime
                    userDataFolder: folder,
                    options: null);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(LogCategory,
                    $"Could not create WebView2 environment: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
