using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Host-agnostic controller for SuperSearch. Owns the single
    /// <see cref="SuperSearchControl"/> instance and all search / replace /
    /// navigate logic, so the UI can be hosted either by the standalone
    /// <c>SuperSearchViewPart</c> or as a tab inside the Supervertaler
    /// Assistant panel. Exposed as a process-wide singleton via
    /// <see cref="Shared"/> so both hosts share one control (and therefore
    /// one set of results, which survives a tab switch).
    /// </summary>
    public class SuperSearchController
    {
        private static SuperSearchController _shared;

        /// <summary>
        /// The shared controller instance. First access creates the control
        /// and wires it to the EditorController. Both hosts call this; whichever
        /// runs first does the wiring, the other just re-parents the control.
        /// </summary>
        public static SuperSearchController Shared =>
            _shared ?? (_shared = new SuperSearchController());

        private readonly SuperSearchControl _control;

        /// <summary>The SuperSearch UI control. Re-parent this into whichever host is active.</summary>
        public SuperSearchControl Control => _control;

        private EditorController _editorController;

        /// <summary>The single embedded web-results window, created on first use
        /// and reused for the rest of the session.</summary>
        private Controls.WebSearchBrowserForm _webForm;
        private IStudioDocument _activeDocument;

        // Project root of the last completed discovery, so a document change
        // within the same project doesn't trigger a redundant re-scan.
        private string _lastProjectRoot;

        // Search cancellation
        private CancellationTokenSource _searchCts;

        // Last search results (for replace operations)
        private List<SearchResult> _lastResults;

        private SuperSearchController()
        {
            _control = new SuperSearchControl();

            // Wire UI events
            _control.SearchRequested += OnSearchRequested;
            _control.StopRequested += OnStopRequested;
            _control.NavigateRequested += OnNavigateRequested;
            _control.ReplaceRequested += OnReplaceRequested;
            _control.ReplaceAllRequested += OnReplaceAllRequested;
            _control.HelpRequested += (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.SuperSearch);
            _control.ModeChanged += OnModeChanged;
            _control.WebResourcesChanged += OnWebResourcesChanged;
            _control.WebSearchRequested += OnWebSearchRequested;

            // Restore the persisted search-source mode (Project files / Files + TMs / TMs only).
            var settings = SettingsService.Current;
            _control.SetSourceMode(ParseSourceMode(settings.SuperSearchMode));

            // GetWebResources() reconciles the stored list against the built-ins
            // of this build, so a resource whose URL we fixed since the user last
            // saved is repaired here rather than staying broken forever.
            _control.SetWebResources(settings.GetWebResources());
            _control.WebResultsInBrowser = settings.WebResultsInBrowser;

            _editorController = StudioEditor.Controller;
            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;
                _activeDocument = _editorController.ActiveDocument;
                ApplyDocumentLanguages();

                // Kick off project discovery so the panel is populated when it
                // first opens. RefreshProjectFiles offloads all file I/O to a
                // background thread, so this never blocks plugin start-up.
                RefreshProjectFiles();
            }
        }

        // ─── Search-source mode ──────────────────────────────────

        private static SuperSearchSourceMode ParseSourceMode(string s)
        {
            switch (s)
            {
                case "Everything": return SuperSearchSourceMode.Everything;
                case "Tms": return SuperSearchSourceMode.Tms;
                case "Termbases": return SuperSearchSourceMode.Termbases;
                // Legacy names, written before the scopes were reorganised into
                // Everything / Project files / TMs / Termbases. Accepted so an
                // existing settings file doesn't silently reset the user's
                // choice to Project files.
                case "FilesAndTms": return SuperSearchSourceMode.Everything;
                case "TmsOnly": return SuperSearchSourceMode.Tms;
                default: return SuperSearchSourceMode.ProjectFiles;
            }
        }

        private void OnModeChanged(object sender, EventArgs e)
        {
            // Persist the mode so it survives across sessions.
            //
            // The fresh load-modify-save this replaces carried the comment
            // "so a setting changed elsewhere in the meantime isn't clobbered" —
            // a hand-rolled defence against the five-copies defect, and nearly
            // right: it narrowed the window without closing it, since another
            // writer could still save between that load and that save. Update()
            // holds one lock across both.
            try
            {
                SettingsService.Update(s =>
                    s.SuperSearchMode = _control.SelectedSourceMode.ToString());
            }
            catch { /* persistence failure must not break the UI */ }
        }

        // ─── Web resources ───────────────────────────────────────

        private void OnWebResourcesChanged(object sender, EventArgs e)
        {
            try
            {
                SettingsService.Update(s =>
                {
                    s.SetWebResources(_control.GetWebResources());
                    s.WebResultsMode = _control.WebResultsInBrowser ? "Browser" : "Embedded";
                });
            }
            catch { /* persistence failure must not break the UI */ }
        }

        /// <summary>
        /// Resolves the project's language pair and opens the enabled resources.
        /// Only "Browser" mode exists today; embedded WebView2 tabs will branch
        /// here on <see cref="TermLensSettings.WebResultsInBrowser"/>.
        /// </summary>
        private void OnWebSearchRequested(object sender, WebSearchRequestEventArgs e)
        {
            try
            {
                var (sourceLocale, targetLocale) = DocumentLanguages();

                // A term picked from the target side is looked up in the target
                // language, so the pair flips: a Dutch word in an EN→NL project
                // must be searched nl→en, or IATE, Linguee and Reverso all return
                // nothing and the feature looks broken.
                if (e.FromTarget)
                {
                    var swap = sourceLocale;
                    sourceLocale = targetLocale;
                    targetLocale = swap;
                }

                // Resources that need no language codes still work without a
                // project open, so an unknown pair is a warning, not a blocker.
                if (string.IsNullOrEmpty(sourceLocale) || string.IsNullOrEmpty(targetLocale))
                {
                    DiagnosticLog.Log("WebSearch",
                        $"Language pair unresolved (src='{sourceLocale}', tgt='{targetLocale}') — "
                        + "language-specific resources may produce odd URLs");
                }

                var targets = WebSearchUrlBuilder.BuildAll(
                    e.Resources, e.Query, sourceLocale, targetLocale);

                if (targets.Count == 0)
                {
                    _control.SetStatus("No web resources produced a usable URL.");
                    return;
                }

                // Embedded mode is opt-in and degrades silently: a missing
                // WebView2 runtime, or an environment we cannot create, drops
                // through to the browser rather than failing the search.
                if (!_control.WebResultsInBrowser && WebView2Support.IsAvailable)
                {
                    if (TryShowEmbedded(e.Query, targets)) return;
                    DiagnosticLog.Log("WebSearch",
                        "Embedded mode unavailable — falling back to the browser");
                }

                var single = WebSearchLauncher.OpenAll(targets);
                _control.SetStatus(single
                    ? $"Opened {targets.Count} web resource(s) for “{e.Query}” in a new browser window."
                    : $"Opened {targets.Count} web resource(s) for “{e.Query}”.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("WebSearch", $"Web search failed: {ex}");
                _control.SetStatus("Web search failed – see the diagnostic log.");
            }
        }

        /// <summary>
        /// Opens the embedded browser window. Returns false if it could not be
        /// shown at all, so the caller can fall back to the user's browser.
        ///
        /// <para>The window is shown non-modally and owned by nothing, so it
        /// behaves like a second Studio window: the user can maximise it, park it
        /// on another monitor, and keep translating underneath.</para>
        /// </summary>
        private bool TryShowEmbedded(string query, List<WebSearchTarget> targets)
        {
            try
            {
                // One window for the session, reused. Closing it only hides it,
                // so the WebView2 environment and any signed-in sessions stay
                // warm — and searches refresh tabs in place instead of piling up
                // windows, which is the whole reason embedded mode exists.
                // Sample the host window BEFORE our own form exists. AppInitializer
                // tries first, but it runs while Studio is still starting up and
                // the main window may not be there yet — and once our form does
                // exist, MainWindowHandle can return ours instead, which is how a
                // window ends up owning itself and therefore owning nothing.
                ForegroundWindow.CaptureHostMainWindow();

                if (_webForm == null || _webForm.IsDisposed)
                    _webForm = new WebSearchBrowserForm();

                var form = _webForm;
                form.Show();

                // Awaited via a continuation rather than blocked on: WebView2's
                // initialisation needs the message pump we would otherwise hold,
                // so blocking here would deadlock.
                form.ShowResultsAsync(query, targets).ContinueWith(t =>
                {
                    if (t.IsFaulted || !t.Result)
                    {
                        DiagnosticLog.Log("WebSearch",
                            "Embedded window could not load; opening the browser instead");
                        try { form.Hide(); } catch { }
                        WebSearchLauncher.OpenAll(targets);
                        _control.SetStatus(
                            $"Embedded view unavailable – opened {targets.Count} resource(s) in your browser.");
                        return;
                    }

                    // Raise it only once the tabs exist. WebView2 spins up a
                    // separate browser process that takes the foreground while it
                    // initialises; raising before that means it lands behind
                    // Trados on a warm start, when init is fast enough to win.
                    form.BringToFrontHard();
                    _control.SetStatus(
                        $"Opened {targets.Count} web resource(s) for “{query}” in the embedded browser.");
                }, TaskScheduler.FromCurrentSynchronizationContext());

                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("WebSearch",
                    $"Embedded window failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ─── Document Events ─────────────────────────────────────

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            _activeDocument = _editorController?.ActiveDocument;
            ApplyDocumentLanguages();
            RefreshProjectFiles();
        }

        /// <summary>
        /// The active document's source and target language as culture names
        /// ("nl-NL", "en-GB"), or nulls when no document is open. UI thread only.
        /// </summary>
        private (string source, string target) DocumentLanguages()
        {
            try
            {
                var activeFile = _activeDocument?.ActiveFile;
                return (activeFile?.SourceFile?.Language?.CultureInfo?.Name,
                        activeFile?.Language?.CultureInfo?.Name);
            }
            catch { return (null, null); /* ActiveFile can throw while the document is changing */ }
        }

        /// <summary>
        /// Points the panel's labels at the active document's languages, and
        /// returns them. When they change, the panel drops the results it shows:
        /// every row was turned to face the document it was searched from, so
        /// under the new document's headers it would be wrong (Dutch under
        /// "English", after switching from an NL→EN document to an EN→NL one),
        /// and the Live filter would test the new boxes against the wrong
        /// columns. UI thread only.
        /// </summary>
        private (string source, string target) ApplyDocumentLanguages()
        {
            var langs = DocumentLanguages();
            if (_control.SetLanguages(langs.source, langs.target))
                _lastResults = null;   // Replace All must never act on rows no longer shown
            return langs;
        }

        /// <summary>
        /// Refreshes the project's file and TM lists shown in the panel.
        /// Resolves the active file path here (Trados document API — UI thread),
        /// then offloads the actual scan to a background thread. File I/O
        /// (enumerating SDLXLIFF files, parsing the .sdlproj for TMs, probing TM
        /// paths) must never run on the Trados UI thread — on a project whose
        /// folder is slow to reach it would freeze, or even hang, the whole
        /// application (notably during start-up).
        /// </summary>
        private void RefreshProjectFiles(bool force = false)
        {
            var filePath = GetActiveFilePath();
            if (string.IsNullOrEmpty(filePath)) return;

            Task.Run(() => DiscoverProject(filePath, force));
        }

        /// <summary>
        /// Background-thread project discovery: locate the project root,
        /// enumerate its SDLXLIFF files, find its translation memories, and
        /// publish the results to the control. Never call this on the UI thread.
        /// </summary>
        private void DiscoverProject(string filePath, bool force)
        {
            try
            {
                var projectRoot = FindProjectRoot(filePath);
                if (projectRoot == null) return;

                // Skip a redundant re-scan when the project root is unchanged,
                // unless the caller forces it.
                if (!force && string.Equals(projectRoot, _lastProjectRoot, StringComparison.OrdinalIgnoreCase))
                    return;
                _lastProjectRoot = projectRoot;

                var files = XliffSearcher.FindProjectXliffFiles(filePath);
                var tms = TmSearcher.FindProjectTms(filePath);

                // Termbases are discovered up front too, so the TBs picker is
                // populated before the first search rather than only after one.
                // DetectTermbases needs the Trados document, hence the UI-thread
                // hop; the reading itself stays on this background thread.
                var tbConfigs = SafeInvokeGet(() =>
                {
                    try
                    {
                        var doc = StudioEditor.Controller?.ActiveDocument;
                        return MultiTermProjectDetector.DetectTermbases(doc);
                    }
                    catch { return null; }
                });
                List<string> termbaseLabels;
                try
                {
                    termbaseLabels = TermbaseSearcher.Discover(tbConfigs)
                        .Select(TermbaseSearcher.Label).ToList();
                }
                catch { termbaseLabels = new List<string>(); }

                SafeInvoke(() =>
                {
                    _control.SetProjectFiles(files);
                    _control.SetProjectTms(tms);
                    _control.SetProjectTermbases(termbaseLabels);
                    var tmNote = tms.Count > 0 ? $", {tms.Count} TM(s)" : "";
                    var tbNote = termbaseLabels.Count > 0 ? $", {termbaseLabels.Count} termbase(s)" : "";
                    _control.SetStatus(
                        $"Project: {Path.GetFileName(projectRoot)} – {files.Count} file(s){tmNote}{tbNote}");
                });
            }
            catch { /* discovery failure must not crash the plugin */ }
        }

        /// <summary>
        /// Walks up from a file to the directory containing the project's
        /// <c>.sdlproj</c>. Pure file-system access — safe on a background thread.
        /// </summary>
        private static string FindProjectRoot(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    if (Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly).Length > 0)
                        return dir;
                }
                catch { /* permission denied — keep walking up */ }

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            return null;
        }

        // ─── Search ──────────────────────────────────────────────

        private async void OnSearchRequested(object sender, SearchRequestEventArgs e)
        {
            // Cancel any in-progress search
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            var mode = e.SourceMode;
            bool needFiles = mode == SuperSearchSourceMode.ProjectFiles
                          || mode == SuperSearchSourceMode.Everything;
            bool needTms = mode == SuperSearchSourceMode.Tms
                        || mode == SuperSearchSourceMode.Everything;
            bool needTermbases = mode == SuperSearchSourceMode.Termbases
                              || mode == SuperSearchSourceMode.Everything;

            // The document's language pair decides how every TM and termbase is
            // turned before matching, so the source box always means "the
            // language you translate from" - and it is what the column headers
            // name. Re-applied here as well as on document change, so a pair
            // that could not be read then is picked up now, and the headers
            // always describe the rows this search will produce.
            var languages = SafeInvokeGet(() => ApplyDocumentLanguages());

            // Termbase discovery needs the Trados document (project settings),
            // so resolve the project's MultiTerm/.ttb configuration on the UI
            // thread; the reading itself happens on the background thread with
            // everything else.
            List<Models.MultiTermTermbaseConfig> projectTermbaseConfigs = null;
            if (needTermbases)
            {
                projectTermbaseConfigs = SafeInvokeGet(() =>
                {
                    try
                    {
                        var doc = StudioEditor.Controller?.ActiveDocument;
                        return MultiTermProjectDetector.DetectTermbases(doc);
                    }
                    catch { return null; }
                });
            }

            // Resolve the active file path on the UI thread (Trados document
            // API); everything else — project discovery AND the search itself —
            // runs on a background thread so the Trados UI never blocks on file
            // I/O. Re-discovering here means a file/TM added mid-session is
            // picked up without reopening the project.
            var activeFilePath = GetActiveFilePath();

            SafeInvoke(() =>
            {
                _control.SetSearching(true);
                _control.SetStatus("Scanning project...");
            });

            var sw = Stopwatch.StartNew();
            int searchedFiles = 0, searchedTms = 0, searchedTermbases = 0;
            string emptyMessage = null;

            try
            {
                var results = await Task.Run(() =>
                {
                    var projectFiles = string.IsNullOrEmpty(activeFilePath)
                        ? new List<string>()
                        : XliffSearcher.FindProjectXliffFiles(activeFilePath);
                    var projectTms = string.IsNullOrEmpty(activeFilePath)
                        ? new List<string>()
                        : TmSearcher.FindProjectTms(activeFilePath);

                    ct.ThrowIfCancellationRequested();

                    // Publish the discovered lists to the control and snapshot
                    // the user's Files/TMs selection in one synchronous UI
                    // round-trip, so the search uses a consistent filtered set.
                    var selected = SafeInvokeGet(() =>
                    {
                        _control.SetProjectFiles(projectFiles);
                        _control.SetProjectTms(projectTms);
                        return Tuple.Create(
                            needFiles ? _control.GetSelectedFiles() : new List<string>(),
                            needTms ? _control.GetSelectedTms() : new List<string>());
                    });
                    var files = selected?.Item1 ?? new List<string>();
                    var tms = selected?.Item2 ?? new List<string>();
                    searchedFiles = files.Count;
                    searchedTms = tms.Count;

                    var termbases = needTermbases
                        ? TermbaseSearcher.Discover(projectTermbaseConfigs)
                        : new List<TermbaseSearcher.TermbaseSource>();
                    if (termbases.Count > 0)
                    {
                        // Publish the discovered termbases to the TBs picker and
                        // snapshot the user's selection, exactly as the Files and
                        // TMs pickers do above.
                        var labels = termbases.Select(TermbaseSearcher.Label).ToList();
                        var keep = SafeInvokeGet(() =>
                        {
                            _control.SetProjectTermbases(labels);
                            return new HashSet<string>(
                                _control.GetSelectedTermbases(), StringComparer.OrdinalIgnoreCase);
                        });
                        if (keep != null)
                            termbases = termbases
                                .Where(t => keep.Contains(TermbaseSearcher.Label(t)))
                                .ToList();
                    }
                    searchedTermbases = termbases.Count;

                    if (files.Count == 0 && tms.Count == 0 && termbases.Count == 0)
                    {
                        emptyMessage = mode == SuperSearchSourceMode.Tms
                            ? "No translation memories found for this project."
                            : mode == SuperSearchSourceMode.Termbases
                                ? "No termbases found. Check your Supervertaler termbase settings, "
                                  + "or attach a termbase in Trados Project Settings."
                                : mode == SuperSearchSourceMode.ProjectFiles
                                    ? "No project files found. Open a file in the editor first."
                                    : "Nothing to search: no project files, translation memories or "
                                      + "termbases found. Open a file in the editor first.";
                        return null;
                    }

                    SafeInvoke(() => _control.SetStatus("Searching..."));

                    bool hasSource = !string.IsNullOrEmpty(e.SourceQuery);
                    bool hasTarget = !string.IsNullOrEmpty(e.TargetQuery);

                    // Run a single query against the selected files + TMs with a
                    // given scope (source-only or target-only).
                    Func<string, SearchScope, string, List<SearchResult>> runOne = (q, scope, label) =>
                    {
                        var m = new List<SearchResult>();
                        if (files.Count > 0)
                            m.AddRange(XliffSearcher.Search(
                                files, q, scope, e.CaseSensitive, e.UseRegex, e.WholeWord,
                                (done, total) => SafeInvoke(() =>
                                    _control.SetStatus($"Searching files ({label})... ({done}/{total})")),
                                ct));
                        if (tms.Count > 0)
                            m.AddRange(TmSearcher.Search(
                                tms, q, scope, e.CaseSensitive, e.UseRegex, e.WholeWord,
                                languages.source, languages.target,
                                (done, total) => SafeInvoke(() =>
                                    _control.SetStatus($"Searching TMs ({label})... ({done}/{total})")),
                                ct));
                        if (termbases.Count > 0)
                            m.AddRange(TermbaseSearcher.Search(
                                termbases, q, scope, e.CaseSensitive, e.UseRegex, e.WholeWord,
                                languages.source, languages.target,
                                (done, total) => SafeInvoke(() =>
                                    _control.SetStatus($"Searching termbases ({label})... ({done}/{total})")),
                                ct));
                        return m;
                    };

                    List<SearchResult> merged;
                    if (hasSource && hasTarget)
                    {
                        // Both boxes filled: a segment must match the source term
                        // in its source AND the target term in its target.
                        var srcHits = runOne(e.SourceQuery, SearchScope.SourceOnly, "source");
                        var tgtKeys = new HashSet<string>(
                            runOne(e.TargetQuery, SearchScope.TargetOnly, "target").Select(ResultKey));
                        merged = srcHits.Where(r => tgtKeys.Contains(ResultKey(r))).ToList();
                    }
                    else if (hasSource)
                    {
                        merged = runOne(e.SourceQuery, SearchScope.SourceOnly, "source");
                    }
                    else
                    {
                        merged = runOne(e.TargetQuery, SearchScope.TargetOnly, "target");
                    }

                    return merged;
                }, ct);

                sw.Stop();

                if (results == null)
                {
                    SafeInvoke(() =>
                    {
                        _control.SetStatus(emptyMessage ?? "Nothing to search.");
                        _control.SetSearching(false);
                    });
                    return;
                }

                // The rows face the document the search started from. If the
                // user switched to a document in another language pair while it
                // ran, the headers have moved on and these rows would sit under
                // the wrong ones - so they are dropped, not shown.
                bool stale = SafeInvokeGet(() =>
                    !string.Equals(_control.Languages.SourceLocale, languages.source, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_control.Languages.TargetLocale, languages.target, StringComparison.OrdinalIgnoreCase));
                if (stale)
                {
                    SafeInvoke(() =>
                    {
                        _control.SetStatus("The open document's languages changed during the search – search again.");
                        _control.SetSearching(false);
                    });
                    return;
                }

                _lastResults = results;

                SafeInvoke(() =>
                {
                    _control.SetResults(results);
                    _control.SetStatus(DescribeResults(results, searchedFiles, searchedTms, searchedTermbases, sw.ElapsedMilliseconds));
                    _control.SetSearching(false);
                });
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() =>
                {
                    _control.SetStatus("Search cancelled.");
                    _control.SetSearching(false);
                });
            }
            catch (RegexMatchTimeoutException)
            {
                SafeInvoke(() =>
                {
                    _control.SetStatus("Search stopped. " + UserRegex.TooSlow);
                    _control.SetSearching(false);
                });
            }
            catch (Exception ex)
            {
                SafeInvoke(() =>
                {
                    _control.SetStatus($"Search error: {ex.Message}");
                    _control.SetSearching(false);
                });
            }
        }

        /// <summary>
        /// Identity used to intersect the source-term and target-term result
        /// sets when both boxes are filled. XLIFF segments are distinguished by
        /// file + unit + segment; TM entries (no stable segment id) fall back to
        /// their source/target text.
        /// </summary>
        private static string ResultKey(SearchResult r)
        {
            return string.Join("",
                r.FilePath ?? "", r.ParagraphUnitId ?? "", r.SegmentId ?? "",
                r.SourceText ?? "", r.TargetText ?? "");
        }

        private static string DescribeResults(List<SearchResult> results, int fileCount,
            int tmCount, int termbaseCount, long ms)
        {
            int fileHits = results.Count(r => r.Kind == ResultKind.XliffSegment);
            int tmHits = results.Count(r => r.Kind == ResultKind.TmEntry);
            int tbHits = results.Count(r => r.Kind == ResultKind.TermbaseEntry);

            // Report only the sources actually searched, so the line stays
            // readable in the single-source scopes.
            var parts = new List<string>();
            if (fileCount > 0) parts.Add($"{fileHits} in {fileCount} file(s)");
            if (tmCount > 0) parts.Add($"{tmHits} in {tmCount} TM(s)");
            if (termbaseCount > 0) parts.Add($"{tbHits} in {termbaseCount} termbase(s)");

            if (parts.Count == 0)
                return $"{results.Count} result(s) – {ms} ms";
            if (parts.Count == 1)
                return $"{parts[0]} – {ms} ms";
            return $"{results.Count} result(s) – " + string.Join(", ", parts) + $" – {ms} ms";
        }

        private void OnStopRequested(object sender, EventArgs e)
        {
            _searchCts?.Cancel();
        }

        // ─── Navigate ────────────────────────────────────────────

        private void OnNavigateRequested(object sender, NavigateToSegmentEventArgs e)
        {
            // Must run on the UI thread – same pattern as AiAssistantViewPart.OnNavigateToSegment
            SafeInvoke(() =>
            {
                if (_activeDocument == null || _editorController == null)
                {
                    _control.SetStatus("No active document.");
                    return;
                }

                var result = _control.GetSelectedResult();
                if (result == null) return;

                // TM concordance hits and termbase entries aren't in any
                // document — nothing to navigate to.
                if (result.Kind == ResultKind.TmEntry
                    || result.Kind == ResultKind.TermbaseEntry)
                {
                    _control.SetStatus(result.Kind == ResultKind.TermbaseEntry
                        ? "This is a termbase entry – use the preview pane below to copy the term."
                        : "This is a translation-memory hit – use the preview pane below to copy the text.");
                    return;
                }

                var activeFilePath = GetActiveFilePath();
                var isSameFile = activeFilePath != null &&
                    string.Equals(activeFilePath, result.FilePath, StringComparison.OrdinalIgnoreCase);

                if (!isSameFile)
                {
                    _control.SetStatus(
                        $"Open \"{result.FileName}\" in the editor first, then double-click to navigate.");
                    return;
                }

                try
                {
                    _activeDocument.SetActiveSegmentPair(
                        result.ParagraphUnitId, result.SegmentId, true);

                    // Give focus back to the editor so the navigation is visible
                    try { _editorController.Activate(); }
                    catch { /* Activate may not be available */ }

                    _control.SetStatus(
                        $"Navigated to segment #{result.SegmentNumber} in {result.FileName}");
                }
                catch (Exception ex)
                {
                    _control.SetStatus($"Navigation failed: {ex.Message}");
                }
            });
        }

        private string GetActiveFilePath()
        {
            try
            {
                return _activeDocument?.ActiveFile?.LocalFilePath;
            }
            catch
            {
                return null;
            }
        }

        // ─── Replace ─────────────────────────────────────────────

        private void OnReplaceRequested(object sender, ReplaceRequestEventArgs e)
        {
            if (e.SelectedResult == null) return;

            // Replace only applies to SDLXLIFF segments — not to TM
            // concordance hits, and not to termbase entries (edit those in the
            // termbase editor, where the change is reviewable).
            if (e.SelectedResult.Kind == ResultKind.TmEntry
                || e.SelectedResult.Kind == ResultKind.TermbaseEntry)
            {
                SafeInvoke(() => _control.SetStatus(
                    e.SelectedResult.Kind == ResultKind.TermbaseEntry
                        ? "Replace doesn't apply to termbase entries – select a project-file row."
                        : "Replace doesn't apply to translation-memory results – select a project-file row."));
                return;
            }

            if (_activeDocument == null) return;

            var result = e.SelectedResult;

            // The segment must be in the active file
            var activeFilePath = GetActiveFilePath();
            if (activeFilePath == null ||
                !string.Equals(activeFilePath, result.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                SafeInvoke(() => _control.SetStatus(
                    "Navigate to the segment first (double-click the row). Replace works on the active file."));
                return;
            }

            try
            {
                // Navigate to segment
                _activeDocument.SetActiveSegmentPair(result.ParagraphUnitId, result.SegmentId, true);

                // Get the active segment pair and use ProcessSegmentPair to modify it
                var activePair = _activeDocument.ActiveSegmentPair;
                if (activePair == null)
                {
                    SafeInvoke(() => _control.SetStatus("Could not access the active segment."));
                    return;
                }

                var outcome = ReplaceInActiveSegmentPair(
                    activePair, e.SearchText, e.ReplaceText,
                    e.CaseSensitive, e.UseRegex, e.WholeWord, out var newTarget, out var wholeSegment);

                switch (outcome)
                {
                    case ActiveReplaceOutcome.NoMatch:
                        SafeInvoke(() => _control.SetStatus("No match found in target text."));
                        return;
                    case ActiveReplaceOutcome.SpansInlineTags:
                        SafeInvoke(() => _control.SetStatus(
                            "Match spans inline tags or a tracked change – skipped to preserve them. Edit the segment manually."));
                        return;
                    case ActiveReplaceOutcome.Error:
                        SafeInvoke(() => _control.SetStatus("Replace failed – the segment couldn't be modified."));
                        return;
                    case ActiveReplaceOutcome.Declined:
                        SafeInvoke(() => _control.SetStatus(
                            "Not replaced – the segment holds tracked changes that Trados Studio protects while Track Changes is off. Accept or reject them, or switch Track Changes on."));
                        return;
                }

                result.TargetText = newTarget;
                SafeInvoke(() =>
                {
                    _control.SetResults(_lastResults);
                    _control.SetStatus(wholeSegment == null
                        ? "Replaced in 1 segment."
                        : "Replaced in 1 segment – " + wholeSegment);
                });
            }
            catch (RegexMatchTimeoutException)
            {
                SafeInvoke(() => _control.SetStatus("Not replaced. " + UserRegex.TooSlow));
            }
            catch (Exception ex)
            {
                SafeInvoke(() => _control.SetStatus($"Replace error: {ex.Message}"));
            }
        }

        private void OnReplaceAllRequested(object sender, ReplaceRequestEventArgs e)
        {
            if (_lastResults == null || _lastResults.Count == 0) return;

            // Count target matches. TM concordance hits aren't in any document,
            // so Replace All only ever touches SDLXLIFF segment results. This
            // runs the pattern over every target before anything is changed, so
            // a pattern that runs out of time (UserRegex) usually stops here,
            // with nothing replaced.
            List<SearchResult> targetMatches;
            try
            {
                targetMatches = _lastResults.Where(r =>
                    r.Kind == ResultKind.XliffSegment &&
                    IsTargetMatch(r.TargetText, e.SearchText, e.CaseSensitive, e.UseRegex, e.WholeWord)).ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                SafeInvoke(() => _control.SetStatus("Nothing replaced. " + UserRegex.TooSlow));
                MessageBox.Show("Nothing was replaced.\n\n" + UserRegex.TooSlow,
                    "SuperSearch – Replace All", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (targetMatches.Count == 0)
            {
                SafeInvoke(() => _control.SetStatus("No matches found in target text."));
                return;
            }

            // Group by file
            var fileGroups = targetMatches.GroupBy(r => r.FilePath).ToList();

            var activeFilePath = GetActiveFilePath();
            var diskGroups = fileGroups.Where(g =>
                activeFilePath == null ||
                !string.Equals(g.Key, activeFilePath, StringComparison.OrdinalIgnoreCase)).ToList();

            // Files open in the editor are never edited on disk: Studio holds
            // its own copy of each. Null when they cannot be told - then no file
            // is edited on disk at all.
            var openInEditor = FilesOpenInEditor();
            var closedCount = openInEditor == null ? 0
                : diskGroups.Count(g => !XliffDiskReplace.IsOpen(g.Key, openInEditor));
            var activeCount = fileGroups.Count - diskGroups.Count;   // the active file, if it has matches
            var leftCount = diskGroups.Count - closedCount;          // files this run leaves alone

            // Nothing this run may change: say so, rather than ask to replace.
            if (activeCount == 0 && closedCount == 0)
            {
                MessageBox.Show(openInEditor == null
                        ? "Replace All could not tell which files are open in the editor, so it changes no file on disk. " +
                          "Go to the file and run Replace All there."
                        : $"All {targetMatches.Count} occurrence(s) are in files open in the editor, other than the one you are in, " +
                          "so Replace All would change nothing. Go to that file and run Replace All there.",
                    "SuperSearch – Replace All", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var msg = $"Replace {targetMatches.Count} occurrence(s) in {fileGroups.Count} file(s)?";
            if (activeCount > 0)
                msg += "\n\nChanges in the active file go through the Trados API and can be undone with Ctrl+Z.";
            if (closedCount > 0)
                msg += $"\n\n{closedCount} file(s) not open in the editor will be changed directly on disk. " +
                       "Ctrl+Z cannot undo that, but a copy of each file as it was is kept first, in\n" +
                       UserDataPath.ReplaceBackupsDir;
            if (leftCount > 0)
                msg += openInEditor == null
                    ? $"\n\n{leftCount} file(s) are left unchanged: Replace All could not tell which files are open in the editor."
                    : $"\n\n{leftCount} file(s) open in the editor, other than the one you are in, are left unchanged.";

            var dialogResult = MessageBox.Show(msg, "SuperSearch – Replace All",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dialogResult != DialogResult.OK) return;

            // Second confirmation for disk modifications Ctrl+Z cannot undo
            if (closedCount > 0)
            {
                var confirm = MessageBox.Show(
                    "Are you sure? Changes to files on disk cannot be undone with Ctrl+Z.\n\n" +
                    "To undo them later, copy a file back from the backup folder while it is closed in Trados Studio.",
                    "SuperSearch – Final Confirmation",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation,
                    MessageBoxDefaultButton.Button2); // default to "No"
                if (confirm != DialogResult.Yes) return;
            }

            int replacedCount = 0;
            int errorCount = 0;
            int skippedTagSpan = 0;
            int protectedCount = 0;   // tracked changes Studio protects (SegmentWriteCheck)
            int wholeSegmentCount = 0;   // Track Changes on, but not recorded word by word
            var wholeSegmentWhy = new List<string>();
            // The pattern ran out of time (UserRegex) on a segment: the run stops
            // there. Every later segment would only be tried and given up on.
            bool tooSlow = false;

            foreach (var group in fileGroups)
            {
                var isActiveFile = activeFilePath != null &&
                    string.Equals(group.Key, activeFilePath, StringComparison.OrdinalIgnoreCase);

                if (isActiveFile && _activeDocument != null)
                {
                    // Replace via Trados API for the active file
                    foreach (var result in group)
                    {
                        try
                        {
                            _activeDocument.SetActiveSegmentPair(result.ParagraphUnitId, result.SegmentId, true);
                            var pair = _activeDocument.ActiveSegmentPair;
                            if (pair == null) { errorCount++; continue; }

                            var outcome = ReplaceInActiveSegmentPair(
                                pair, e.SearchText, e.ReplaceText,
                                e.CaseSensitive, e.UseRegex, e.WholeWord, out var newTarget, out var wholeSegment);

                            if (outcome == ActiveReplaceOutcome.Replaced)
                            {
                                result.TargetText = newTarget;
                                replacedCount++;
                                if (wholeSegment != null)
                                {
                                    wholeSegmentCount++;
                                    if (!wholeSegmentWhy.Contains(wholeSegment)) wholeSegmentWhy.Add(wholeSegment);
                                }
                            }
                            else if (outcome == ActiveReplaceOutcome.SpansInlineTags)
                            {
                                skippedTagSpan++;
                            }
                            else if (outcome == ActiveReplaceOutcome.Error)
                            {
                                errorCount++;
                            }
                            else if (outcome == ActiveReplaceOutcome.Declined)
                            {
                                protectedCount++;
                            }
                        }
                        catch (RegexMatchTimeoutException) { tooSlow = true; break; }
                        catch { errorCount++; }
                    }
                }
            }
            int activeReplaced = replacedCount;

            // Every other file is changed on disk - unless it is open in the
            // editor - with a backup taken first (XliffDiskReplace). Not after
            // the pattern ran out of time in the active file.
            var disk = diskGroups.Count == 0 || tooSlow ? null
                : XliffDiskReplace.Run(diskGroups, openInEditor,
                    s => PerformReplace(s, e.SearchText, e.ReplaceText, e.CaseSensitive, e.UseRegex, e.WholeWord),
                    UserDataPath.ReplaceBackupsDir, DateTime.Now);
            bool tooSlowOnDisk = disk?.TooSlow == true;
            tooSlow |= tooSlowOnDisk;
            var diskFiles = disk?.Files ?? new List<XliffDiskReplace.FileReport>();
            replacedCount += diskFiles.Sum(f => f.Replaced);
            skippedTagSpan += diskFiles.Sum(f => f.SpansMarkup);
            var changedSinceSearch = diskFiles.Sum(f => f.ChangedSinceSearch);
            var filesWritten = diskFiles.Count(f => f.Replaced > 0);
            var filesNotWritten = diskFiles.Count(f => f.NotWritten != null);

            SafeInvoke(() =>
            {
                _control.SetResults(_lastResults);
                var statusMsg = tooSlow
                    ? $"Replace All stopped: the regular expression took too long – simplify it. Replaced {replacedCount} segment(s) before that"
                    : $"Replaced {replacedCount} segment(s)";
                if (skippedTagSpan > 0) statusMsg += $", skipped {skippedTagSpan} (match spans inline tags or a tracked change)";
                if (protectedCount > 0) statusMsg += $", not replaced in {protectedCount} (tracked changes Studio protects while Track Changes is off: accept or reject them first)";
                if (wholeSegmentCount > 0)
                    statusMsg += $", {wholeSegmentCount} not word by word: "
                        + string.Join("; ", wholeSegmentWhy.Select(w => w.TrimEnd('.')));
                if (changedSinceSearch > 0) statusMsg += $", {changedSinceSearch} not replaced because the file changed since the search";
                if (filesNotWritten > 0) statusMsg += $", {filesNotWritten} file(s) left unchanged";
                if (errorCount > 0) statusMsg += $" ({errorCount} error(s))";
                if (filesWritten > 0)
                    statusMsg += $". {filesWritten} file(s) changed on disk – reopen to see the changes.";
                _control.SetStatus(statusMsg);
            });

            if (disk != null && (filesWritten > 0 || filesNotWritten > 0 || changedSinceSearch > 0))
                ShowDiskSummary(disk);
            else if (tooSlow && !tooSlowOnDisk)
                MessageBox.Show("Replace All stopped.\n\n" + UserRegex.TooSlow + "\n\n"
                        + (activeReplaced > 0
                            ? $"{activeReplaced} segment(s) in the open file had already been replaced; Ctrl+Z undoes them. "
                            : "Nothing was replaced. ")
                        + "No file on disk was changed.",
                    "SuperSearch – Replace All", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// What Replace All did to the files on disk, file by file, and where the
        /// backups are. Shown once the run is over, because the status line is
        /// cut off in a narrow pane and a refused file must not go unnoticed.
        /// </summary>
        private static void ShowDiskSummary(XliffDiskReplace.RunReport disk)
        {
            const int maxListed = 15;
            string Label(XliffDiskReplace.FileReport f) =>
                Path.Combine(Path.GetFileName(Path.GetDirectoryName(f.FilePath)) ?? "", Path.GetFileName(f.FilePath));

            string List(IEnumerable<string> lines)
            {
                var all = lines.ToList();
                var shown = all.Take(maxListed).Select(l => "  " + l);
                var more = all.Count > maxListed ? new[] { $"  …and {all.Count - maxListed} more" } : new string[0];
                return string.Join("\n", shown.Concat(more));
            }

            var sb = new System.Text.StringBuilder();
            if (disk.TooSlow)
                sb.Append("Replace All stopped.\n\n").Append(UserRegex.TooSlow).Append("\n\n");
            var written = disk.Files.Where(f => f.Replaced > 0).ToList();
            var notWritten = disk.Files.Where(f => f.NotWritten != null).ToList();
            var changed = disk.Files.Sum(f => f.ChangedSinceSearch);

            if (written.Count > 0)
                sb.Append("Changed on disk:\n")
                  .Append(List(written.Select(f => $"{Label(f)} – {f.Replaced} segment(s)"))).Append("\n\n");
            if (notWritten.Count > 0)
                sb.Append("Left unchanged:\n")
                  .Append(List(notWritten.Select(f => $"{Label(f)} – {f.NotWritten}"))).Append("\n\n");
            if (changed > 0)
                sb.Append($"{changed} segment(s) no longer read as the search found them and were left alone. " +
                          "Search again to include them.\n\n");
            if (notWritten.Any(f => f.NotWritten == "open in the editor"))
                sb.Append("To change a file that is open in the editor, go to it and run Replace All there.\n\n");

            if (disk.BackupDir == null)
            {
                MessageBox.Show(sb.ToString().TrimEnd(), "SuperSearch – Replace All",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            sb.Append("The files as they were before are in:\n").Append(disk.BackupDir).Append("\n\n")
              .Append("Open that folder now?");
            if (MessageBox.Show(sb.ToString(), "SuperSearch – Replace All",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                try { Process.Start("explorer.exe", "\"" + disk.BackupDir + "\""); } catch { }
            }
        }

        /// <summary>
        /// The full paths of the files in every document open in the editor -
        /// all files of a merged document, and every tab, not only the active
        /// one. Null if Studio would not say, and then nothing is edited on disk.
        /// </summary>
        private HashSet<string> FilesOpenInEditor()
        {
            try
            {
                var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var doc in _editorController.GetDocuments())
                    foreach (var file in doc.Files)
                        if (!string.IsNullOrEmpty(file?.LocalFilePath))
                            open.Add(Path.GetFullPath(file.LocalFilePath));
                return open;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Replace inside one SDLXLIFF segment's text, as it reads with its
        /// tracked changes accepted - the on-disk counterpart of
        /// <see cref="TargetTextReplace"/>: text inside a tracked deletion is
        /// neither matched nor changed, and the replace is made inside each text
        /// node, so inline tags stay. When that does not give the same text as
        /// replacing in the whole text, the match runs across a tag or a tracked
        /// change: <see cref="TargetTextReplace.Outcome.SpansMarkup"/>, and the
        /// node is left exactly as it was. (Until 201 the nodes were edited
        /// first and checked after, so a skipped segment kept a partial replace,
        /// saved with the file whenever another segment in it was replaced.)
        /// Only plain text nodes are edited; a match in whitespace between tags
        /// counts as spanning.
        /// </summary>
        internal static TargetTextReplace.Outcome ReplaceInSegmentNode(XmlNode node,
            Func<string, string> perform, out string after)
        {
            var texts = XliffSearcher.AcceptedTextNodes(node);
            var before = string.Concat(texts.Select(t => t.Value));
            after = perform(before);
            if (after == before) return TargetTextReplace.Outcome.NoMatch;

            var nodeByNode = texts.Select(t => t is XmlText ? perform(t.Value) : t.Value).ToList();
            if (texts.Count == 0 || string.Concat(nodeByNode) != after)
                return TargetTextReplace.Outcome.SpansMarkup;

            for (int i = 0; i < texts.Count; i++)
                if (!string.Equals(texts[i].Value, nodeByNode[i], StringComparison.Ordinal))
                    texts[i].Value = nodeByNode[i];
            return TargetTextReplace.Outcome.Replaceable;
        }

        // ─── Text Helpers ────────────────────────────────────────

        /// <summary>
        /// Outcome of an in-editor replace. <see cref="Replaced"/> means the
        /// segment was actually modified; <see cref="SpansInlineTags"/> means
        /// the search string straddled a tag boundary so we refused to apply
        /// a destructive flatten-and-rewrite (and the caller should report
        /// "skipped, would lose formatting" rather than counting a success).
        /// </summary>
        // Declined: Studio did not apply the edit (SegmentWriteCheck) - the
        // segment holds tracked changes it protects while Track Changes is off.
        private enum ActiveReplaceOutcome { Replaced, NoMatch, SpansInlineTags, Error, Declined }

        /// <summary>
        /// Replaces text in the active segment pair while preserving inline
        /// tags. Pre-v4.19.56 the replace path read <c>pair.Target.ToString()</c>,
        /// did a string replace, then cleared the target and re-added a single
        /// cloned <see cref="IText"/> – which destroyed every tag pair,
        /// placeholder, and formatting span the segment originally contained.
        ///
        /// This helper instead walks the existing target's <see cref="IText"/>
        /// children - those the segment reads, deleted text left out
        /// (<see cref="TargetTextReplace"/>) - and replaces each one's text
        /// in-place, so structure is preserved. If the search string straddles
        /// a tag, comment or tracked-change boundary (no single IText contains
        /// the full match), the per-IText replace produces a flat result that
        /// doesn't match what a flat replace would produce – in that case we
        /// refuse to apply rather than try to be clever, and return
        /// <see cref="ActiveReplaceOutcome.SpansInlineTags"/> so the caller can
        /// surface a clear "match spans tags – skipped" message. A pattern that
        /// runs out of time (<see cref="UserRegex"/>) throws
        /// <see cref="RegexMatchTimeoutException"/>, with the segment unchanged.
        /// </summary>
        /// <param name="wholeSegment">With Track Changes on, why a replace was
        /// recorded as one whole-segment change rather than word by word (the
        /// segment already holds tracked changes, say); null otherwise.</param>
        private ActiveReplaceOutcome ReplaceInActiveSegmentPair(
            ISegmentPair pair, string searchText, string replaceText,
            bool caseSensitive, bool useRegex, bool wholeWord, out string newFlatTarget,
            out string wholeSegment)
        {
            newFlatTarget = null;
            wholeSegment = null;
            if (pair == null || _activeDocument == null) return ActiveReplaceOutcome.Error;

            // Pre-flight on the text as it reads with its tracked changes
            // accepted (deleted text is neither matched nor changed): replace in
            // each text node and see if that gives the same as replacing in the
            // whole text. If not, the match straddles a tag, a comment or a
            // tracked change. This used to start from pair.Target.ToString(),
            // which renders tag markup and deleted text too, so it could not
            // agree with the per-node replace in a segment with any tag.
            Func<string, string> perform = s =>
                PerformReplace(s, searchText, replaceText, caseSensitive, useRegex, wholeWord);
            var plan = TargetTextReplace.Plan(pair.Target, perform, out _, out var expected);
            if (plan == TargetTextReplace.Outcome.NoMatch) return ActiveReplaceOutcome.NoMatch;
            if (plan == TargetTextReplace.Outcome.SpansMarkup) return ActiveReplaceOutcome.SpansInlineTags;

            // Safe to apply. With Track Changes on, recorded word by word, as
            // update_segments records a rewrite (#138); until 201 Studio tracked
            // the in-place edit, which it records as the whole segment replaced.
            // A pattern that runs out of time here is caught inside the edit, not
            // thrown through Studio: Apply has changed nothing, the edit is
            // cancelled, and the timeout goes to the caller once Studio is done.
            RegexMatchTimeoutException tooSlow = null;
            TrackedWrite tracked;
            try
            {
                tracked = SegmentWriteCheck.ProcessChecked(_activeDocument, pair, "Supervertaler",
                    (sp, cancel) =>
                    {
                        try { TargetTextReplace.Apply(sp.Target, perform); }
                        catch (RegexMatchTimeoutException ex) { tooSlow = ex; cancel.Cancel = true; }
                    }, wordLevel: true);
            }
            catch (SegmentWriteDeclinedException)
            {
                return ActiveReplaceOutcome.Declined;
            }
            catch
            {
                return ActiveReplaceOutcome.Error;
            }
            if (tooSlow != null) throw tooSlow;

            newFlatTarget = expected;
            wholeSegment = tracked?.Note;
            return ActiveReplaceOutcome.Replaced;
        }

        /// <summary>
        /// Finds the first IText node in a segment (used as a template for cloning).
        /// Same pattern as SegmentTagHandler.FindFirstText.
        /// </summary>
        private static IText FindFirstText(ISegment segment)
        {
            if (segment == null) return null;
            foreach (var item in segment)
            {
                if (item is IText text)
                    return text;
            }
            return null;
        }

        /// <summary>The replace itself. A bad pattern leaves the text as it was;
        /// a match that runs out of time (<see cref="UserRegex"/>) throws, and
        /// is never taken for "no match".</summary>
        private static string PerformReplace(string text, string search, string replace,
            bool caseSensitive, bool useRegex, bool wholeWord)
        {
            if (useRegex)
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { return Regex.Replace(text, search, replace, options, UserRegex.MatchTimeout); }
                catch (RegexMatchTimeoutException) { throw; }
                catch { return text; }
            }
            if (wholeWord)
            {
                // Whole-word literal replace: \b boundaries. Escape $ in the
                // replacement so it stays literal (Regex.Replace treats $ specially).
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                try
                {
                    return Regex.Replace(text, @"\b" + Regex.Escape(search) + @"\b",
                        (replace ?? "").Replace("$", "$$"), options, UserRegex.MatchTimeout);
                }
                catch (RegexMatchTimeoutException) { throw; }
                catch { return text; }
            }
            return ReplaceString(text, search, replace, caseSensitive);
        }

        private static string ReplaceString(string text, string search, string replace, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(search))
                return text;

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var result = new System.Text.StringBuilder();
            int pos = 0;

            while (pos < text.Length)
            {
                int idx = text.IndexOf(search, pos, comparison);
                if (idx < 0)
                {
                    result.Append(text, pos, text.Length - pos);
                    break;
                }
                result.Append(text, pos, idx - pos);
                result.Append(replace);
                pos = idx + search.Length;
            }

            return result.ToString();
        }

        private static bool IsTargetMatch(string targetText, string search,
            bool caseSensitive, bool useRegex, bool wholeWord)
        {
            if (string.IsNullOrEmpty(targetText)) return false;

            if (useRegex)
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { return Regex.IsMatch(targetText, search, options, UserRegex.MatchTimeout); }
                catch (RegexMatchTimeoutException) { throw; }
                catch { return false; }
            }

            if (wholeWord)
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { return Regex.IsMatch(targetText, @"\b" + Regex.Escape(search) + @"\b", options, UserRegex.MatchTimeout); }
                catch (RegexMatchTimeoutException) { throw; }
                catch { return false; }
            }

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return targetText.IndexOf(search, comparison) >= 0;
        }

        // ─── Helpers ─────────────────────────────────────────────

        private void SafeInvoke(Action action)
        {
            try
            {
                if (_control.InvokeRequired)
                    _control.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }

        /// <summary>
        /// Synchronous variant of <see cref="SafeInvoke"/> that marshals a
        /// value-returning delegate to the UI thread and waits for the result.
        /// Used from the background search task to publish the discovered
        /// file/TM lists and read back the user's selection in one round-trip.
        /// </summary>
        private T SafeInvokeGet<T>(Func<T> func)
        {
            try
            {
                if (_control.InvokeRequired)
                    return (T)_control.Invoke(func);
                return func();
            }
            catch
            {
                return default(T);
            }
        }
    }
}
