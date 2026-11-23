using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>What <see cref="FindLookupTms(string,string,string)"/> found: the memories to search for TM matches.</summary>
        public sealed class LookupTms
        {
            /// <summary>File paths and server URIs to search, as <see cref="FindProjectTms"/> gives them.</summary>
            public List<string> Tms = new List<string>();
            /// <summary>EverTM is in the list, enabled and with Lookup ticked: search it too, through its own provider.</summary>
            public bool EverTm;
            /// <summary>How many memories the list for this language pair holds (EverTM included, machine translation not).</summary>
            public int Total;
            /// <summary>How many of them are searched.</summary>
            public int Searched;
            /// <summary>Each memory's penalty in the project's list (the Penalty column), by the entry in <see cref="Tms"/>;
            /// missing means none. A memory listed twice takes the lower.</summary>
            public Dictionary<string, int> Penalties = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The memories a TM-match lookup may search, for a document in the given language pair: those in
        /// Project Settings &gt; Language Pairs &gt; Translation Memory and Automated Translation that are
        /// Enabled AND have Lookup ticked (<c>PerformNormalSearch</c>) - what Studio itself would look up in.
        /// EverTM (<c>evertm:///</c>, or <c>supervault:///</c> from before the rename) follows the same rule and
        /// is reported apart, because it is searched through its own provider and not opened as a file.
        ///
        /// <para>Unlike <see cref="FindProjectTms"/>, which lists every memory the project file names and every
        /// <c>.sdltm</c> in its <c>Tm</c> folder, for the features where the translator picks what to search.
        /// Until 208 the lookup used that list too, so a memory that was switched off was searched all the same.</para>
        ///
        /// <para>The list is the language pair's own when the pair has one ("Use different translation
        /// providers for this language pair", <c>OverrideParent="true"</c>), and All Language Pairs' otherwise. A
        /// pair that does not override still carries a list in the project file - a copy from when it was last
        /// edited - and that copy is not read.</para>
        /// Never throws; empty when the project file can't be found or read.
        /// </summary>
        /// <param name="sourceCulture">The document's source language ("nl-NL"), or null when unknown.</param>
        /// <param name="targetCulture">The document's target language ("en-GB"), or null when unknown.</param>
        public static LookupTms FindLookupTms(string anyProjectFilePath, string sourceCulture, string targetCulture)
        {
            try
            {
                var projPath = FindProjectFile(anyProjectFilePath);
                if (projPath == null) return new LookupTms();
                return FindLookupTms(XDocument.Load(projPath), Path.GetDirectoryName(projPath), sourceCulture, targetCulture);
            }
            catch { return new LookupTms(); }
        }

        /// <summary>The same, of a project file already read. <paramref name="projDir"/> is where relative TM paths start.</summary>
        public static LookupTms FindLookupTms(XDocument projDoc, string projDir, string sourceCulture, string targetCulture)
        {
            var result = new LookupTms();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);      // every memory in the list
            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // those that are on
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);     // the files and URIs in Tms
            var cascade = EffectiveCascade(projDoc, sourceCulture, targetCulture);
            if (cascade == null) return result;

            foreach (var entry in cascade.Elements().Where(e => e.Name.LocalName == "CascadeEntryItem"))
            {
                var main = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "MainTranslationProviderItem");
                var mainUri = main?.Attribute("Uri")?.Value;
                if (string.IsNullOrEmpty(mainUri)) continue;

                bool everTm = IsEverTmUri(mainUri);
                // The entry's memories: its main one and, when the project has one for it, its project TM.
                var files = new List<string>();
                if (!everTm)
                {
                    foreach (var item in entry.Elements().Where(e => e.Name.LocalName == "MainTranslationProviderItem"
                                                                  || e.Name.LocalName == "ProjectTranslationProviderItem"))
                    {
                        if (item != main && IsFalse(item.Attribute("Enabled"))) continue;
                        var tm = LookupTarget(item.Attribute("Uri")?.Value, projDir);
                        if (tm != null) files.Add(tm);
                    }
                    // Machine translation, a termbase provider, or a memory whose file is gone: not a TM to count.
                    if (files.Count == 0) continue;
                }

                // One memory listed twice counts once, and is searched if either line has it on.
                bool on = !IsFalse(main.Attribute("Enabled")) && !IsFalse(entry.Attribute("PerformNormalSearch"));
                var key = everTm ? "evertm" : files[0];
                if (seen.Add(key)) result.Total++;
                if (!on) continue;
                if (searched.Add(key)) result.Searched++;

                if (everTm) result.EverTm = true;
                else
                {
                    int.TryParse(entry.Attribute("Penalty")?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int penalty);
                    penalty = Math.Max(0, Math.Min(100, penalty));
                    foreach (var tm in files)
                    {
                        if (added.Add(tm)) result.Tms.Add(tm);
                        if (!result.Penalties.TryGetValue(tm, out int had) || penalty < had) result.Penalties[tm] = penalty;
                    }
                }
            }

            result.Tms = result.Tms
                .OrderBy(p => ServerTmClient.IsServerTmUri(p) ? p : Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
            return result;
        }

        /// <summary>
        /// Whether segments confirmed in this language pair are learned by EverTM: it is in the pair's TM list, enabled,
        /// with Update ticked (<c>PerformUpdate</c>). Read from the saved project file, so a change not yet saved in
        /// Project Settings shows only once Studio has saved it. Null when the project file can't be found or read.
        /// For EverTM Matches' status line; reads the file, so not on Studio's UI thread.
        /// </summary>
        public static bool? EverTmLearns(string anyProjectFilePath, string sourceCulture, string targetCulture)
        {
            try
            {
                var projPath = FindProjectFile(anyProjectFilePath);
                if (projPath == null) return null;
                var cascade = EffectiveCascade(XDocument.Load(projPath), sourceCulture, targetCulture);
                if (cascade == null) return false;
                foreach (var entry in cascade.Elements().Where(e => e.Name.LocalName == "CascadeEntryItem"))
                {
                    var main = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "MainTranslationProviderItem");
                    if (IsEverTmUri(main?.Attribute("Uri")?.Value) && !IsFalse(main.Attribute("Enabled")) && !IsFalse(entry.Attribute("PerformUpdate")))
                        return true;
                }
                return false;
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether Studio looks EverTM up for this language pair: it is in the pair's TM list, Enabled, with Lookup ticked
        /// (<c>PerformNormalSearch</c>). EverTM Matches follows the same ticks (Michael, 23 Nov 2026: "It should follow
        /// the ticks, like Studio does"). Read from the saved project file. Null when the project file can't be found or
        /// read, so a document outside a project, or a file that can't be read, never hides EverTM.
        /// </summary>
        public static bool? EverTmLooksUp(string anyProjectFilePath, string sourceCulture, string targetCulture)
        {
            try
            {
                var projPath = FindProjectFile(anyProjectFilePath);
                if (projPath == null) return null;
                return FindLookupTms(XDocument.Load(projPath), Path.GetDirectoryName(projPath), sourceCulture, targetCulture).EverTm;
            }
            catch { return null; }
        }

        /// <summary>
        /// What the Batch Translate log says when a run left memories out, or null when it searched them all.
        /// </summary>
        public static string SkippedTmsSentence(int searched, int total)
        {
            int skipped = total - searched;
            if (total <= 0 || skipped <= 0) return null;
            return "TM matches: " + searched + " of " + total + " project TM" + (total == 1 ? "" : "s") + " searched; "
                + skipped + (skipped == 1 ? " is switched off or has" : " are switched off or have")
                + " Lookup unticked in Project Settings.";
        }

        /// <summary>
        /// The TM list that applies to a document in this language pair: the pair's own when it overrides, the
        /// project's (All Language Pairs) otherwise. Null when the project file has none.
        /// </summary>
        static XElement EffectiveCascade(XDocument projDoc, string sourceCulture, string targetCulture)
        {
            var directions = projDoc.Descendants().Where(e => e.Name.LocalName == "LanguageDirection").ToList();
            var pair = directions.FirstOrDefault(d =>
                !string.IsNullOrEmpty(sourceCulture) && !string.IsNullOrEmpty(targetCulture)
                && string.Equals(d.Attribute("SourceLanguageCode")?.Value, sourceCulture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Attribute("TargetLanguageCode")?.Value, targetCulture, StringComparison.OrdinalIgnoreCase));
            // A project with one language pair: the document is in it, whatever the two name it.
            if (pair == null && directions.Count == 1) pair = directions[0];

            var own = pair?.Descendants().FirstOrDefault(e => e.Name.LocalName == "CascadeItem");
            if (own != null && string.Equals(own.Attribute("OverrideParent")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                return own;

            return projDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CascadeItem"
                && !e.Ancestors().Any(a => a.Name.LocalName == "LanguageDirection"));
        }

        static bool IsFalse(XAttribute a)
        {
            return string.Equals(a?.Value, "false", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsEverTmUri(string value)
        {
            Uri uri;
            return Uri.TryCreate(value ?? "", UriKind.Absolute, out uri)
                && Supervertaler.Trados.SuperVault.SuperVaultProvider.IsOurs(uri);
        }

        /// <summary>A TM line's memory as the search takes it: a server URI, or the full path of an existing .sdltm. Null for anything else.</summary>
        static string LookupTarget(string uri, string projDir)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            if (ServerTmClient.IsServerTmUri(uri)) return uri;
            if (uri.IndexOf(".sdltm", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var path = ResolveTmUri(uri, projDir);
            return path != null && File.Exists(path) ? path : null;
        }

        /// <summary>The project file (.sdlproj) of a document in it: in its folder or above. Null when there is none.</summary>
        public static string ProjectFileOf(string anyProjectFilePath) => FindProjectFile(anyProjectFilePath);

        static string FindProjectFile(string anyProjectFilePath)
        {
            if (string.IsNullOrEmpty(anyProjectFilePath)) return null;
            var dir = Path.GetDirectoryName(anyProjectFilePath);
            while (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    var found = Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly);
                    if (found.Length > 0) return found[0];
                }
                catch { /* permission denied */ }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
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
        /// <param name="documentSource">The open document's source language as a
        /// culture name ("nl-NL"), and <paramref name="documentTarget"/> its
        /// target. Each memory is turned to face the document before it is
        /// searched - see <see cref="SearchLanguageDirection"/>. Null when no
        /// document is open, in which case every memory is searched as stored.</param>
        public static List<SearchResult> Search(
            List<string> tmFiles,
            string query,
            SearchScope scope,
            bool caseSensitive,
            bool useRegex,
            bool wholeWord,
            string documentSource,
            string documentTarget,
            Action<int, int> progress,
            CancellationToken ct)
        {
            var results = new List<SearchResult>();
            if (string.IsNullOrEmpty(query) || tmFiles == null || tmFiles.Count == 0)
                return results;

            var pair = Tuple.Create(documentSource, documentTarget);
            int total = tmFiles.Count;

            // This run's server sign-ins, one per host; gone with the run.
            var connections = new ServerTmClient.Connections();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i, total);

                var entry = tmFiles[i];

                // Route each entry to the file or server branch.
                if (ServerTmClient.IsServerTmUri(entry))
                {
                    try { SearchServerTm(entry, query, scope, caseSensitive, useRegex, wholeWord, pair, results, connections, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (RegexMatchTimeoutException) { throw; }   // the pattern, not the TM (UserRegex)
                    catch { /* skip a server TM that can't be opened / authenticated */ }
                }
                else
                {
                    try { SearchFileTm(entry, query, scope, caseSensitive, useRegex, wholeWord, pair, results, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (RegexMatchTimeoutException) { throw; }   // the pattern, not the TM (UserRegex)
                    catch { /* skip a TM that can't be opened (locked, corrupt) */ }
                }
            }

            progress?.Invoke(total, total);
            return results;
        }

        // File-based branch (unchanged behaviour, extracted for symmetry).
        private static void SearchFileTm(
            string tmPath, string query, SearchScope scope,
            bool caseSensitive, bool useRegex, bool wholeWord,
            Tuple<string, string> documentPair, List<SearchResult> results, CancellationToken ct)
        {
            var tmName = Path.GetFileNameWithoutExtension(tmPath);
            var tm = new FileBasedTranslationMemory(tmPath);
            var ld = tm.LanguageDirection;
            if (ld == null) return;

            SearchLanguageDirection(ld, tmName, tmPath, "TM", query, scope,
                caseSensitive, useRegex, wholeWord, documentPair, results, ct);
        }

        // Server-based (GroupShare) branch.
        private static void SearchServerTm(
            string tmUri, string query, SearchScope scope,
            bool caseSensitive, bool useRegex, bool wholeWord,
            Tuple<string, string> documentPair, List<SearchResult> results,
            ServerTmClient.Connections connections, CancellationToken ct)
        {
            if (!ServerTmClient.TryParseServerTmUri(tmUri, out var sref)) return;

            foreach (var ld in ServerTmClient.OpenLanguageDirections(sref, connections))
            {
                ct.ThrowIfCancellationRequested();
                if (ld == null) continue;
                SearchLanguageDirection(ld, sref.TmName, tmUri, "GroupShare", query, scope,
                    caseSensitive, useRegex, wholeWord, documentPair, results, ct);
            }
        }

        private static SearchScope Flip(SearchScope scope)
        {
            switch (scope)
            {
                case SearchScope.SourceOnly: return SearchScope.TargetOnly;
                case SearchScope.TargetOnly: return SearchScope.SourceOnly;
                default: return scope;
            }
        }

        // The shared concordance + post-filter + result-shaping loop, used
        // verbatim by both the file and server branches. This is the code that
        // previously lived inline inside Search().
        //
        // Every memory is turned to face the open document first. A memory
        // stored the other way round (EN→NL, in an NL→EN project) used to be
        // searched and shown as stored: the box for the language you translate
        // from ran a concordance over the memory's English side, and any hit
        // put English in the left-hand column. Now the query goes to whichever
        // side holds that language and the row comes back the document's way
        // round. A memory in some other pair altogether (NL→DE in an NL→EN
        // project, which a multi-language project attaches alongside) is
        // searched as stored, and its rows say which languages they hold.
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
            Tuple<string, string> documentPair,
            List<SearchResult> results,
            CancellationToken ct)
        {
            string tmSource = null, tmTarget = null;
            try
            {
                tmSource = ld.SourceLanguage?.Name;
                tmTarget = ld.TargetLanguage?.Name;
            }
            catch { /* unreadable direction: searched as stored, claimed as nothing */ }

            bool reversed = LanguageUtils.IsReversedPair(
                documentPair.Item1, documentPair.Item2, tmSource, tmTarget);
            var shownSource = reversed ? tmTarget : tmSource;
            var shownTarget = reversed ? tmSource : tmTarget;
            bool inPair = LanguageUtils.IsInDocumentPair(
                documentPair.Item1, documentPair.Item2, shownSource, shownTarget);

            // The scope is in the document's terms; on a reversed memory its
            // "source" is the memory's target side.
            var tmScope = reversed ? Flip(scope) : scope;
            var modes = new List<SearchMode>();
            if (tmScope == SearchScope.SourceOnly || tmScope == SearchScope.SourceAndTarget)
                modes.Add(SearchMode.ConcordanceSearch);
            if (tmScope == SearchScope.TargetOnly || tmScope == SearchScope.SourceAndTarget)
                modes.Add(SearchMode.TargetConcordanceSearch);

            // Keyed per memory, so two memories cannot keep un-suppressing
            // each other on every search.
            if (reversed || !inPair)
                DiagnosticLog.LogIfChanged("SuperSearch TM " + tmName,
                    $"TM '{tmName}' is {tmSource}→{tmTarget}, the document "
                    + $"{documentPair.Item1}→{documentPair.Item2}: "
                    + (reversed ? "searched and shown the other way round" : "")
                    + (reversed && !inPair ? "; " : "")
                    + (inPair ? "" : "rows labelled with their own languages"));

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
                    // exact case / regex / whole-word options. Both sides are
                    // still the memory's own here, so the memory's scope applies.
                    bool matches = false;
                    if (tmScope == SearchScope.SourceOnly || tmScope == SearchScope.SourceAndTarget)
                        matches = XliffSearcher.QueryMatches(sourceText, query, caseSensitive, useRegex, wholeWord);
                    if (!matches && (tmScope == SearchScope.TargetOnly || tmScope == SearchScope.SourceAndTarget))
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
                        SourceText = reversed ? targetText : sourceText,
                        TargetText = reversed ? sourceText : targetText,
                        MatchScore = r.ScoringResult?.Match ?? 0,
                        Status = status,
                        OffPairSource = inPair ? null : shownSource,
                        OffPairTarget = inPair ? null : shownTarget,
                    });
                }
            }

            if (logSrv) DiagnosticLog.Log("ServerTM",
                "TM '" + tmName + "' search summary: raw=" + rawTotal
                + ", nullTU=" + nullTuTotal + ", added=" + addedTotal + ".");
        }
    }
}
