using System;
using System.Collections.Generic;
using System.Linq;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Wraps the Trados Studio SDK's <see cref="TranslationProviderServer"/> so
    /// that <see cref="TmSearcher"/> can concordance-search server-based
    /// (GroupShare) TMs with the exact same downstream code it uses for
    /// file-based <c>.sdltm</c> (issue #35).
    ///
    /// Credentials ("Option A" — reuse what the user already gave Studio): the
    /// plugin's <c>SupervertalerTmProviderFactory</c> hands us Studio's
    /// <see cref="ITranslationProviderCredentialStore"/> via
    /// <see cref="CaptureCredentialStore"/> whenever Studio instantiates a
    /// provider, and we look the GroupShare server credential up from there. No
    /// separate credential entry, no stored password of our own. If no
    /// credential is found the search simply skips server TMs (never throws).
    ///
    /// Lines marked "// VERIFY" use SDK signatures that must be confirmed against
    /// the Sdl.LanguagePlatform.TranslationMemoryApi assembly on first build
    /// (do it against the Studio 2024 / Studio18 build, which is activated). The
    /// enumeration methods (GetTranslationMemory / GetTranslationMemories /
    /// LanguageDirections) are taken from RWS's published docs; the server
    /// constructor and the credential-key scheme are the parts to double-check.
    /// </summary>
    public static class ServerTmClient
    {
        /// <summary>Parsed form of a <c>sdltm.http://host/?orgPath=/Org/Sub&amp;tmName=Foo</c> URI.</summary>
        public sealed class ServerTmRef
        {
            public Uri BaseUri;      // e.g. https://groupsharedev.sdlproducts.com/
            public string OrgPath;   // e.g. /Supervertaler
            public string TmName;    // e.g. en-US to nl-BE
            public string OriginalUri;
        }

        /// <summary>Credentials for one GroupShare host.</summary>
        public sealed class ServerTmCredentials
        {
            public bool UseWindowsCredentials;
            public string UserName;
            public string Password;
        }

        /// <summary>
        /// Optional override. When set, this takes precedence over the captured
        /// Studio credential store (used by tests, or a future settings-based
        /// fallback). Return null to fall through to the captured store.
        /// </summary>
        public static Func<Uri, ServerTmCredentials> CredentialResolver { get; set; }

        // Studio's translation-provider credential store, captured opportunistically
        // by SupervertalerTmProviderFactory. May be null if Studio has not yet
        // instantiated any provider this session.
        private static ITranslationProviderCredentialStore _credentialStore;

        // One authenticated server object per host, reused across the TMs touched
        // in a single SuperSearch run so we authenticate once.
        private static readonly Dictionary<string, TranslationProviderServer> _serverCache =
            new Dictionary<string, TranslationProviderServer>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Called by the provider factory to hand us Studio's credential store.</summary>
        public static void CaptureCredentialStore(ITranslationProviderCredentialStore store)
        {
            if (store != null)
            {
                _credentialStore = store;
                DiagnosticLog.Log("ServerTM", "Credential store captured from provider factory.");
            }
        }

        /// <summary>True for <c>sdltm.http://</c> / <c>sdltm.https://</c> (server) URIs.</summary>
        public static bool IsServerTmUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            return uri.StartsWith("sdltm.http://", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("sdltm.https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Parses <c>sdltm.http://host/?orgPath=/Org&amp;tmName=Foo</c> into a <see cref="ServerTmRef"/>.</summary>
        public static bool TryParseServerTmUri(string uri, out ServerTmRef refOut)
        {
            refOut = null;
            if (!IsServerTmUri(uri)) return false;

            try
            {
                // Strip "sdltm." so the remainder is a normal http(s) URI.
                var httpForm = uri.Substring("sdltm.".Length);
                var u = new Uri(httpForm);

                string orgPath = null, tmName = null;
                foreach (var pair in u.Query.TrimStart('?').Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    if (eq < 0) continue;
                    var key = Uri.UnescapeDataString(pair.Substring(0, eq));
                    var val = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    if (string.Equals(key, "orgPath", StringComparison.OrdinalIgnoreCase)) orgPath = val;
                    else if (string.Equals(key, "tmName", StringComparison.OrdinalIgnoreCase)) tmName = val;
                }

                if (string.IsNullOrEmpty(tmName)) return false;

                refOut = new ServerTmRef
                {
                    BaseUri = new Uri(u.GetLeftPart(UriPartial.Authority) + "/"),
                    OrgPath = string.IsNullOrEmpty(orgPath) ? "/" : orgPath,
                    TmName = tmName,
                    OriginalUri = uri
                };
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Authenticates (or reuses) a server connection for the ref's host,
        /// resolves the named TM, and yields its language directions ready for
        /// <c>SearchText</c>. Never throws — yields nothing on any failure so a
        /// mixed file+server search degrades gracefully.
        /// </summary>
        public static IEnumerable<ITranslationMemoryLanguageDirection> OpenLanguageDirections(ServerTmRef sref)
        {
            if (sref == null) yield break;
            DiagnosticLog.Log("ServerTM", "OpenLanguageDirections: host=" + sref.BaseUri.Host
                + ", orgPath=" + sref.OrgPath + ", tmName=" + sref.TmName);

            TranslationProviderServer server;
            try { server = GetOrCreateServer(sref.BaseUri); }
            catch (Exception ex) { DiagnosticLog.Log("ServerTM", "GetOrCreateServer threw: " + ex.Message); yield break; }
            if (server == null) { DiagnosticLog.Log("ServerTM", "No server connection (no credentials found) - skipping this TM."); yield break; }

            ServerBasedTranslationMemory tm = null;
            var serverPath = CombineServerPath(sref.OrgPath, sref.TmName);
            try
            {
                tm = server.GetTranslationMemory(serverPath, TranslationMemoryProperties.All); // VERIFY path shape
            }
            catch (Exception ex) { DiagnosticLog.Log("ServerTM", "GetTranslationMemory('" + serverPath + "') threw: " + ex.Message); tm = null; }
            if (tm != null) DiagnosticLog.Log("ServerTM", "Resolved TM by path: " + serverPath);

            if (tm == null)
            {
                // Fallback: enumerate and match by name.
                try
                {
                    var all = server.GetTranslationMemories();
                    DiagnosticLog.Log("ServerTM", "Enumerated " + all.Count() + " server TM(s); matching name '" + sref.TmName + "'.");
                    tm = all.FirstOrDefault(t =>
                        string.Equals(t.Name, sref.TmName, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex) { DiagnosticLog.Log("ServerTM", "GetTranslationMemories() threw: " + ex.Message); tm = null; }
            }
            if (tm == null) { DiagnosticLog.Log("ServerTM", "TM not resolved on server - skipping."); yield break; }

            IEnumerable<ITranslationMemoryLanguageDirection> lds;
            try
            {
                var list = tm.LanguageDirections.Cast<ITranslationMemoryLanguageDirection>().ToList(); // VERIFY element type
                DiagnosticLog.Log("ServerTM", "TM '" + sref.TmName + "' has " + list.Count + " language direction(s).");
                lds = list;
            }
            catch (Exception ex) { DiagnosticLog.Log("ServerTM", "LanguageDirections threw: " + ex.Message); yield break; }

            foreach (var ld in lds)
                yield return ld;
        }

        /// <summary>Clears the per-run server/auth cache. Call at the end of a SuperSearch run.</summary>
        public static void ResetCache() => _serverCache.Clear();

        private static TranslationProviderServer GetOrCreateServer(Uri baseUri)
        {
            var key = baseUri.Host;
            if (_serverCache.TryGetValue(key, out var cached)) return cached;

            var creds = ResolveCredentials(baseUri);
            if (creds == null) return null; // no credentials known -> caller skips this TM

            // VERIFY constructor: documented/common form is
            //   new TranslationProviderServer(Uri uri, bool useWindowsCredentials, string userName, string password)
            var server = new TranslationProviderServer(
                baseUri,
                creds.UseWindowsCredentials,
                creds.UserName ?? string.Empty,
                creds.Password ?? string.Empty);

            _serverCache[key] = server;
            return server;
        }

        private static ServerTmCredentials ResolveCredentials(Uri baseUri)
        {
            // 1. Explicit override wins (tests / future fallback).
            var overridden = CredentialResolver?.Invoke(baseUri);
            if (overridden != null) return overridden;

            // 2. Settings-based GroupShare credentials (primary, reliable). The
            //    user enters the server login once; the password is DPAPI-encrypted.
            try
            {
                var settings = TermLensSettings.Load();
                var match = settings?.GroupShareServers?
                    .FirstOrDefault(g => HostMatches(g.BaseUrl, baseUri));
                if (match != null)
                {
                    // "Windows" => AD / Windows authentication (useWindowsCredentials
                    // = true). Anything else (incl. null from an older settings file)
                    // => GroupShare/SDL authentication.
                    bool useWin = string.Equals(match.AuthMode, "Windows", StringComparison.OrdinalIgnoreCase);

                    // GroupShare auth needs a username; Windows auth may also rely on
                    // the current Windows identity (blank username), so allow that.
                    if (useWin || !string.IsNullOrEmpty(match.Username))
                    {
                        var pw = DpapiSecret.Unprotect(match.PasswordProtected);
                        DiagnosticLog.Log("ServerTM", "Using settings-based credential for host "
                            + baseUri.Host + " (user='" + match.Username + "', auth="
                            + (useWin ? "Windows" : "GroupShare") + ").");
                        return new ServerTmCredentials
                        {
                            UseWindowsCredentials = useWin,
                            UserName = match.Username ?? string.Empty,
                            Password = pw
                        };
                    }
                }
            }
            catch (Exception ex) { DiagnosticLog.Log("ServerTM", "Settings credential lookup failed: " + ex.Message); }

            // 3. Studio's credential store (opportunistic fallback; only populated
            //    when a Supervertaler bridged TM is also in the project).
            var store = _credentialStore;
            if (store == null)
            {
                DiagnosticLog.Log("ServerTM", "No credential store captured yet - cannot resolve GroupShare credentials.");
                return null;
            }

            // GroupShare credentials are registered under the project-server
            // prefix (ps.http://host) per RWS docs; try that first, then the TM
            // scheme and the plain base as fallbacks. VERIFY the exact key.
            foreach (var candidate in CredentialKeyCandidates(baseUri))
            {
                TranslationProviderCredential cred;
                try { cred = store.GetCredential(candidate); } // VERIFY member
                catch (Exception ex) { DiagnosticLog.Log("ServerTM", "GetCredential('" + candidate + "') threw: " + ex.Message); cred = null; }
                if (cred == null || string.IsNullOrEmpty(cred.Credential))
                {
                    DiagnosticLog.Log("ServerTM", "No credential under key: " + candidate);
                    continue;
                }

                // Stored credential is typically "username:password". VERIFY format.
                var raw = cred.Credential;
                var sep = raw.IndexOf(':');
                var user = sep > 0 ? raw.Substring(0, sep) : string.Empty;
                // Never log the password — only the key that matched and the username.
                DiagnosticLog.Log("ServerTM", "Found credential under key: " + candidate
                    + " (user='" + user + "', hasColon=" + (sep > 0) + ").");

                if (sep > 0)
                {
                    return new ServerTmCredentials
                    {
                        UseWindowsCredentials = false,
                        UserName = raw.Substring(0, sep),
                        Password = raw.Substring(sep + 1)
                    };
                }

                // No colon — treat the whole string as a token/password.
                return new ServerTmCredentials
                {
                    UseWindowsCredentials = false,
                    UserName = string.Empty,
                    Password = raw
                };
            }

            DiagnosticLog.Log("ServerTM", "No GroupShare credential found under any candidate key.");
            return null;
        }

        private static bool HostMatches(string baseUrl, Uri baseUri)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;
            try { return string.Equals(new Uri(baseUrl).Host, baseUri.Host, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static IEnumerable<Uri> CredentialKeyCandidates(Uri baseUri)
        {
            var host = baseUri.GetLeftPart(UriPartial.Authority); // http(s)://host[:port]
            var candidates = new List<string>
            {
                "ps." + host + "/",       // project-server prefix (GroupShare)
                "sdltm." + host + "/",    // TM scheme
                host + "/"                // plain base
            };
            foreach (var c in candidates)
            {
                Uri u = null;
                try { u = new Uri(c); } catch { }
                if (u != null) yield return u;
            }
        }

        private static string CombineServerPath(string orgPath, string tmName)
        {
            var op = string.IsNullOrEmpty(orgPath) ? "/" : orgPath;
            if (!op.StartsWith("/")) op = "/" + op;
            if (!op.EndsWith("/")) op += "/";
            return op + tmName; // e.g. "/Supervertaler/en-US to nl-BE"  // VERIFY expected shape
        }
    }
}
