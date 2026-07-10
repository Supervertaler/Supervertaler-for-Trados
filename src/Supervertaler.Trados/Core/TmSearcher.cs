using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Searches a Trados project's attached translation memories the way
    /// Studio's built-in Concordance does, but folds the hits into SuperSearch's
    /// result list so file results and TM results can be shown together.
    ///
    /// The TM API does a fuzzy concordance pass; hits are then post-filtered
    /// with <see cref="XliffSearcher.QueryMatches"/> so the Aa / .* / Word
    /// options apply to TM results exactly as they do to file results.
    ///
    /// Both file-based <c>.sdltm</c> and server-based (GroupShare) TMs are
    /// searched: file TMs open via <see cref="FileBasedTranslationMemory"/>,
    /// server TMs via <see cref="ServerTmClient"/> (which wraps
    /// <c>TranslationProviderServer</c>). The concordance/post-filter/result
    /// shaping is identical for both — see <see cref="SearchLanguageDirection"/>.
    /// </summary>
    public static class TmSearcher
    {
        /// <summary>
        /// Finds the translation memories attached to the project that contains
        /// <paramref name="anyProjectFilePath"/>: the TM provider URIs declared
        /// in the project's <c>.sdlproj</c> (both file-based <c>.sdltm</c> and
        /// server-based <c>sdltm.http(s)://</c> GroupShare TMs), plus any
        /// <c>.sdltm</c> in the project's <c>Tm</c> subfolder. Server TMs are
        /// returned as their raw <c>sdltm.http…</c> URI; file TMs as absolute
        /// paths. <see cref="Search"/> routes each entry accordingly.
        /// </summary>
        public static List<string> FindProjectTms(string anyProjectFilePath)
        {
            var tms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(anyProjectFilePath)) return new List<string>();

            // Walk up to the project root (directory containing a .sdlproj).
            var dir = Path.GetDirectoryName(anyProjectFilePath);
            string projPath = null;
            while (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    var found = Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly);
                    if (found.Length > 0) { projPath = found[0]; break; }
                }
                catch { /* permission denied */ }

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            if (projPath == null) return new List<string>();

            var projDir = Path.GetDirectoryName(projPath);

            // 1. TM URIs declared in the .sdlproj
            try
            {
                var projDoc = XDocument.Load(projPath);
                var allUris = projDoc.Descendants()
                    .Where(e => e.Name.LocalName == "MainTranslationProviderItem"
                             || e.Name.LocalName == "ProjectTranslationProviderItem")
                    .Select(e => e.Attribute("Uri")?.Value)
                    .Where(u => !string.IsNullOrEmpty(u));

                foreach (var uri in allUris)
                {
                    // Server-based (GroupShare) TM — keep the raw URI for the server branch.
                    if (ServerTmClient.IsServerTmUri(uri))
                    {
                        tms.Add(uri);
                        continue;
                    }

                    // File-based .sdltm (contains the literal ".sdltm").
                    if (uri.IndexOf(".sdltm", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var path = ResolveTmUri(uri, projDir);
                        if (path != null && File.Exists(path)) tms.Add(path);
                    }
                }
            }
            catch { /* unreadable .sdlproj */ }

            // 2. Any .sdltm in the project's Tm subfolder
            try
            {
                var tmSubDir = Path.Combine(projDir, "Tm");
                if (Directory.Exists(tmSubDir))
                {
                    foreach (var f in Directory.GetFiles(tmSubDir, "*.sdltm", SearchOption.AllDirectories))
                    {
                        try { tms.Add(Path.GetFullPath(f)); } catch { tms.Add(f); }
                    }
                }
            }
            catch { }

            // File TMs sort by filename; server URIs sort by their whole string.
            return tms
                .OrderBy(p => ServerTmClient.IsServerTmUri(p) ? p : Path.GetFileName(p),
                         StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Friendly display name for a TM entry returned by <see cref="FindProjectTms"/>:
        /// the file name (no extension) for a file-based <c>.sdltm</c>, or the
        /// GroupShare TM name with a "(GroupShare)" suffix for a server
        /// <c>sdltm.http(s)://</c> URI. Safe on server URIs, which would otherwise
        /// throw in <see cref="Path.GetFileNameWithoutExtension"/> (invalid path chars).
        /// </summary>
        public static string DisplayName(string tmEntry)
        {
            if (string.IsNullOrEmpty(tmEntry)) return "";
            if (ServerTmClient.IsServerTmUri(tmEntry))
            {
                if (ServerTmClient.TryParseServerTmUri(tmEntry, out var sref))
                    return sref.TmName + " (GroupShare)";
                return "GroupShare TM";
            }
            try { return Path.GetFileNameWithoutExtension(tmEntry); }
            catch { return tmEntry; }
        }

        private static string ResolveTmUri(string uri, string projectDir)
        {
            if (string.IsNullOrEmpty(uri)) return null;

            var path = uri;
            if (path.StartsWith("sdltm.file:///")) path = path.Substring("sdltm.file:///".Length);
            else if (path.StartsWith("file:///")) path = path.Substring("file:///".Length);

            try { path = Uri.UnescapeDataString(path); } catch { }

            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(projectDir))
                path = Path.Combine(projectDir, path);

            try { return Path.GetFullPath(path); } catch { return path; }
        }

        /// <summary>
        /// Runs a concordance search across the given TMs (file-based and/or
        /// server-based) and returns matching entries as
        /// <see cref="SearchResult"/>s tagged <see cref="ResultKind.TmEntry"/>.
        /// Source-only / target-only / both is honoured via the TM API's source
        /// and target concordance modes; the fuzzy hits are then post-filtered
        /// against the exact query options.
        /// </summary>
        public static List<SearchResult> Search(
            List<string> tmFiles,
            string query,
            SearchScope scope,
            bool caseSensitive,
            bool useRegex,
            bool wholeWord,
            Action<int, int> progress,
            CancellationToken ct)
        {
            var results = new List<SearchResult>();
            if (string.IsNullOrEmpty(query) || tmFiles == null || tmFiles.Count == 0)
                return results;

            var modes = new List<SearchMode>();
            if (scope == SearchScope.SourceOnly || scope == SearchScope.SourceAndTarget)
                modes.Add(SearchMode.ConcordanceSearch);
            if (scope == SearchScope.TargetOnly || scope == SearchScope.SourceAndTarget)
                modes.Add(SearchMode.TargetConcordanceSearch);

            int total = tmFiles.Count;
            try
            {
                for (int i = 0; i < total; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(i, total);

                    var entry = tmFiles[i];

                    // Route each entry to the file or server branch.
                    if (ServerTmClient.IsServerTmUri(entry))
                    {
                        try { SearchServerTm(entry, query, scope, caseSensitive, useRegex, wholeWord, modes, results, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { /* skip a server TM that can't be opened / authenticated */ }
                    }
                    else
                    {
                        try { SearchFileTm(entry, query, scope, caseSensitive, useRegex, wholeWord, modes, results, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch { /* skip a TM that can't be opened (locked, corrupt) */ }
                    }
                }
            }
            finally
            {
                // Drop the per-run authenticated-server cache.
                ServerTmClient.ResetCache();
            }

            progress?.Invoke(total, total);
            return results;
        }

        // File-based branch (unchanged behaviour, extracted for symmetry).
        private static void SearchFileTm(
            string tmPath, string query, SearchScope scope,
            bool caseSensitive, bool useRegex, bool wholeWord,
            List<SearchMode> modes, List<SearchResult> results, CancellationToken ct)
        {
            var tmName = Path.GetFileNameWithoutExtension(tmPath);
            var tm = new FileBasedTranslationMemory(tmPath);
            var ld = tm.LanguageDirection;
            if (ld == null) return;

            SearchLanguageDirection(ld, tmName, tmPath, "TM", query, scope,
                caseSensitive, useRegex, wholeWord, modes, results, ct);
        }

        // Server-based (GroupShare) branch.
        private static void SearchServerTm(
            string tmUri, string query, SearchScope scope,
            bool caseSensitive, bool useRegex, bool wholeWord,
            List<SearchMode> modes, List<SearchResult> results, CancellationToken ct)
        {
            if (!ServerTmClient.TryParseServerTmUri(tmUri, out var sref)) return;

            foreach (var ld in ServerTmClient.OpenLanguageDirections(sref))
            {
                ct.ThrowIfCancellationRequested();
                if (ld == null) continue;
                SearchLanguageDirection(ld, sref.TmName, tmUri, "GroupShare", query, scope,
                    caseSensitive, useRegex, wholeWord, modes, results, ct);
            }
        }

        // The shared concordance + post-filter + result-shaping loop, used
        // verbatim by both the file and server branches. This is the code that
        // previously lived inline inside Search().
        private static void SearchLanguageDirection(
            ITranslationMemoryLanguageDirection ld,
            string tmName,
            string sourceLabel,
            string status,
            string query,
            SearchScope scope,
            bool caseSensitive,
            bool useRegex,
            bool wholeWord,
            List<SearchMode> modes,
            List<SearchResult> results,
            CancellationToken ct)
        {
            // A TU can come back on both the source- and target-side passes;
            // dedupe on the source/target text pair.
            var seen = new HashSet<string>();

            // Server-TM diagnostics: SearchText behaviour on a GroupShare LD is
            // the thing we're verifying, so trace each stage when this is a server TM.
            bool logSrv = status == "GroupShare";
            int rawTotal = 0, nullTuTotal = 0, addedTotal = 0;

            foreach (var mode in modes)
            {
                ct.ThrowIfCancellationRequested();

                SearchResults sr = null;
                if (logSrv)
                {
                    // The GroupShare TM Server rejects concordance requests whose
                    // MaxResults exceeds its server-side cap with 400 Bad Request
                    // (Studio's own concordance asks for far fewer). Observed on
                    // GroupShare 2020 SR1: MaxResults=100 -> 400, MaxResults=50 -> OK.
                    // So 50 is the primary; 30 is a fallback for any stricter server.
                    var attempts = new List<SearchSettings>
                    {
                        new SearchSettings { Mode = mode, MaxResults = 50, MinScore = 30 },
                        new SearchSettings { Mode = mode, MaxResults = 30, MinScore = 30 },
                    };
                    foreach (var attempt in attempts)
                    {
                        try
                        {
                            sr = ld.SearchText(attempt, query);
                            DiagnosticLog.Log("ServerTM", "SearchText(mode=" + mode
                                + ", MaxResults=" + attempt.MaxResults + ") OK: "
                                + (sr?.Results?.Count ?? 0) + " raw hit(s).");
                            break;
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Log("ServerTM", "SearchText(mode=" + mode
                                + ", MaxResults=" + attempt.MaxResults + ") THREW: "
                                + ex.GetType().Name + ": " + ex.Message);
                            sr = null;
                        }
                    }
                }
                else
                {
                    var settings = new SearchSettings { Mode = mode, MaxResults = 500, MinScore = 30 };
                    try { sr = ld.SearchText(settings, query); }
                    catch { continue; }
                }
                if (sr?.Results == null) continue;

                foreach (var r in sr.Results)
                {
                    ct.ThrowIfCancellationRequested();
                    rawTotal++;

                    var tu = r.MemoryTranslationUnit;
                    if (tu == null) { nullTuTotal++; continue; }

                    var sourceText = tu.SourceSegment?.ToPlain() ?? "";
                    var targetText = tu.TargetSegment?.ToPlain() ?? "";

                    // Post-filter the fuzzy concordance hit against the user's
                    // exact case / regex / whole-word options.
                    bool matches = false;
                    if (scope == SearchScope.SourceOnly || scope == SearchScope.SourceAndTarget)
                        matches = XliffSearcher.QueryMatches(sourceText, query, caseSensitive, useRegex, wholeWord);
                    if (!matches && (scope == SearchScope.TargetOnly || scope == SearchScope.SourceAndTarget))
                        matches = XliffSearcher.QueryMatches(targetText, query, caseSensitive, useRegex, wholeWord);
                    if (!matches) continue;

                    if (!seen.Add(sourceText + "" + targetText)) continue;
                    addedTotal++;

                    results.Add(new SearchResult
                    {
                        Kind = ResultKind.TmEntry,
                        FilePath = sourceLabel,   // file path or server URI (dedupe/label key)
                        FileName = tmName,
                        ParagraphUnitId = null,
                        SegmentId = null,
                        SegmentNumber = 0,
                        SourceText = sourceText,
                        TargetText = targetText,
                        MatchScore = r.ScoringResult?.Match ?? 0,
                        Status = status
                    });
                }
            }

            if (logSrv) DiagnosticLog.Log("ServerTM",
                "TM '" + tmName + "' search summary: raw=" + rawTotal
                + ", nullTU=" + nullTuTotal + ", added=" + addedTotal + ".");
        }
    }
}
