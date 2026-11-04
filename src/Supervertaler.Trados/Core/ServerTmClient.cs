using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core;
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

        /// <summary>
        /// The server connections of ONE run - a SuperSearch, a batch's TM lookup, a
        /// TM write, a TM comparison - so the run signs in once per host however
        /// many of that host's TMs it opens. The run creates it, passes it down and
        /// lets it go when it ends; nothing needs closing
        /// (<see cref="TranslationProviderServer"/> holds nothing disposable).
        ///
        /// <para>Every run is sequential, so this is never shared between threads.
        /// Until 18.20.198 it was one static Dictionary for all of them: SuperSearch,
        /// Batch Translate's TM lookup and the MCP tools each run on a thread of their
        /// own, so two could write it at once - which can corrupt a Dictionary - and
        /// whichever finished first cleared the others' connections mid-run.</para>
        /// </summary>
        public sealed class Connections
        {
            internal readonly Dictionary<string, TranslationProviderServer> Servers =
                new Dictionary<string, TranslationProviderServer>(StringComparer.OrdinalIgnoreCase);
        }

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
        /// Authenticates (or reuses, within <paramref name="connections"/>) a server
        /// connection for the ref's host, resolves the named TM, and yields its
        /// language directions ready for <c>SearchText</c>. Never throws — yields
        /// nothing on any failure so a mixed file+server search degrades gracefully.
        /// Null <paramref name="connections"/> means a run of one: a fresh sign-in.
        /// </summary>
        public static IEnumerable<ITranslationMemoryLanguageDirection> OpenLanguageDirections(
            ServerTmRef sref, Connections connections)
        {
            if (sref == null) yield break;
            DiagnosticLog.Log("ServerTM", "OpenLanguageDirections: server=" + DisplayOrigin(sref.BaseUri)
                + ", orgPath=" + sref.OrgPath + ", tmName=" + sref.TmName);

            TranslationProviderServer server;
            try { server = GetOrCreateServer(sref.BaseUri, connections ?? new Connections()); }
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

        private static TranslationProviderServer GetOrCreateServer(Uri baseUri, Connections connections)
        {
            // One connection per server address (scheme, host and port), never
            // per host name: https://server and http://server:8080 are different
            // servers as far as a login is concerned.
            var key = OriginOf(baseUri.AbsoluteUri);
            if (key == null) return null;
            if (connections.Servers.TryGetValue(key, out var cached)) return cached;

            var creds = ResolveCredentials(baseUri);
            if (creds == null) return null; // no credentials known -> caller skips this TM

            // VERIFY constructor: documented/common form is
            //   new TranslationProviderServer(Uri uri, bool useWindowsCredentials, string userName, string password)
            var server = new TranslationProviderServer(
                baseUri,
                creds.UseWindowsCredentials,
                creds.UserName ?? string.Empty,
                creds.Password ?? string.Empty);

            connections.Servers[key] = server;
            return server;
        }

        private static ServerTmCredentials ResolveCredentials(Uri baseUri)
        {
            // 1. Explicit override wins (tests / future fallback).
            var overridden = CredentialResolver?.Invoke(baseUri);
            if (overridden != null) return overridden;

            // 2. Settings-based GroupShare credentials (primary, reliable). The
            //    user enters the server login once; the password is DPAPI-encrypted.
            //    A stored login is used ONLY for the exact server address it was
            //    saved for - same scheme, host and port (SameOrigin). A login for
            //    https://server never goes to http://server or https://server:8080,
            //    for GroupShare and Windows authentication alike.
            string refusedFor = null;
            try
            {
                var settings = SettingsService.Current;
                var servers = settings?.GroupShareServers;
                var match = servers?.FirstOrDefault(g => g != null && SameOrigin(g.BaseUrl, baseUri));
                if (match == null)
                {
                    // Same host name, different address: say so (below) rather than
                    // leave the user wondering why the TM never connects.
                    refusedFor = servers?
                        .Where(g => g != null && string.Equals(HostOf(g.BaseUrl), baseUri.Host, StringComparison.OrdinalIgnoreCase))
                        .Select(g => g.BaseUrl)
                        .FirstOrDefault();
                    if (refusedFor != null)
                        DiagnosticLog.WriteAlways("ServerTM", "Stored GroupShare login for " + refusedFor
                            + " NOT used for " + DisplayOrigin(baseUri)
                            + ": a login is only used for the exact server address it was saved for (scheme, host and port).");
                }
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
                        DiagnosticLog.Log("ServerTM", "Using settings-based credential for "
                            + DisplayOrigin(baseUri) + " (user='" + match.Username + "', auth="
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

            var fromStudio = ResolveFromStudioStore(baseUri);
            if (fromStudio == null && refusedFor != null)
                ReportRefusedLogin(refusedFor, baseUri);
            return fromStudio;
        }

        private static ServerTmCredentials ResolveFromStudioStore(Uri baseUri)
        {
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

        /// <summary>
        /// The normalised origin of a server address - <c>scheme://host:port</c>,
        /// lower case, the port always written out (443 for https, 80 for http
        /// when not given) - or null when it is not an http(s) address. Accepts
        /// Studio's prefixed forms (<c>sdltm.https://…</c>, <c>ps.https://…</c>)
        /// and a bare host name, which is taken as https.
        /// </summary>
        internal static string OriginOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var s = url.Trim();
            foreach (var prefix in new[] { "sdltm.", "ps." })
            {
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(prefix.Length); break; }
            }
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "https://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
            if (string.IsNullOrEmpty(u.Host)) return null;
            return u.Scheme + "://" + u.IdnHost.ToLowerInvariant() + ":" + u.Port;
        }

        /// <summary>
        /// True only when the stored server address and the TM's server are the
        /// same origin: scheme, host (any case) and effective port. An unreadable
        /// address on either side never matches.
        /// </summary>
        internal static bool SameOrigin(string storedBaseUrl, Uri tmBaseUri)
        {
            if (tmBaseUri == null) return false;
            var a = OriginOf(storedBaseUrl);
            var b = OriginOf(tmBaseUri.AbsoluteUri);
            return a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string HostOf(string url)
        {
            var o = OriginOf(url);
            if (o == null) return null;
            try { return new Uri(o).Host; } catch { return null; }
        }

        private static string DisplayOrigin(Uri u)
        {
            try { return u.GetLeftPart(UriPartial.Authority); } catch { return u?.Host ?? ""; }
        }

        private static int _refusalShown;

        /// <summary>
        /// A stored login that was not used because the TM is on another address
        /// of the same host. The caller has logged it; the user is told the first
        /// time in a session, since every search would otherwise repeat it.
        /// </summary>
        private static void ReportRefusedLogin(string storedBaseUrl, Uri tmBaseUri)
        {
            if (Interlocked.Exchange(ref _refusalShown, 1) == 1) return;
            var text =
                "Supervertaler did not connect to a GroupShare translation memory at\n"
                + DisplayOrigin(tmBaseUri) + "\n\n"
                + "Your stored GroupShare login is for\n" + storedBaseUrl + "\n\n"
                + "A login is only used for the exact server address it was saved for – "
                + "the same http or https, server name and port – so it was not sent to this one.\n\n"
                + "If this really is your GroupShare server, enter this address on the GroupShare tab "
                + "of Supervertaler’s settings. This is shown once; any further cases go to the diagnostic log.";
            try
            {
                StartupNotices.Enqueue(null, "groupshare-login-not-used", owner =>
                    MessageBox.Show(owner, text, "Supervertaler – GroupShare",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
            catch (Exception ex) { DiagnosticLog.Log("ServerTM", "Could not show the GroupShare notice: " + ex.Message); }
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
