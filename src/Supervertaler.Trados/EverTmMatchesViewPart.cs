using System;
using System.Collections.Generic;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Licensing;
using Supervertaler.Trados.SuperVault;

namespace Supervertaler.Trados
{
    /// <summary>
    /// EverTM Matches: EverTM's matches for the active segment in a pane of their own, with everything EverTM knows
    /// about each (docs/design/evertm-pane.md; Michael's decisions of 9 and 10 Nov 2026). Docked at the bottom and
    /// not pinned, like SuperSearch, so it takes no room until opened; Studio remembers where it is dragged.
    ///
    /// <para><b>Threading.</b> On a segment change, Studio's UI thread only copies the source (through Studio's own TM
    /// segment converter, <see cref="EverTmEditorSegment"/>) and hands it to one background worker, newest request
    /// first (<see cref="EverTmLookupWorker{TRequest,TResult}"/>). The result is drawn only if it is still the newest
    /// when it is back on the UI thread. While the pane is hidden nothing is looked up; showing it looks up the current
    /// segment.</para>
    ///
    /// <para><b>What it never does.</b> It never learns (confirming the segment does that, through EverTM's provider,
    /// as before) and never changes Studio's TM provider, Analyze or Pre-translate. Inserting writes the target only,
    /// through the plugin's checked write.</para>
    ///
    /// <para><b>State.</b> The current document, the worker, and whether a lookup is owed because the pane was hidden.
    /// Nothing is cached between segments; per document, only the one "is EverTM learning here" note.</para>
    /// </summary>
    [ViewPart(
        Id = "EverTmMatchesViewPart",
        Name = "EverTM Matches",
        Description = "EverTM's matches for the active segment, with everything EverTM knows about each",
        Icon = "EverTmIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Bottom, Pinned = false)]
    public class EverTmMatchesViewPart : AbstractViewPartController
    {
        static EverTmMatchesViewPart _instance;
        static EverTmMatchesControl _control;

        sealed class Request
        {
            public EverTmMatchesLookup.Request Lookup;
            public EverTmPaneResult Shown;      // the UI-thread half, filled in before the lookup
            public string ProjectFile;          // set once per document: the worker reads whether EverTM learns here
            public bool Again;                  // the second look at the same segment (see _again)
        }

        EditorController _editor;
        IStudioDocument _doc;
        EverTmLookupWorker<Request, EverTmPaneResult> _worker;
        bool _owed;              // a segment changed while the pane was hidden
        string _segmentKey;      // the segment last looked up (paragraph and segment id), for the second look
        /// <summary>
        /// One more lookup of the same segment, 4 s after arriving on it (1 s of margin). A segment you have just confirmed reaches
        /// EverTM's index 1 to 3 s later (it is gathered for 0.5 s, stored, and the index is reopened every 2 s), and
        /// until then EverTM offers it only as an exact match. So the next segment, very often a near-repeat in a
        /// patent, was looked up too early to get it as a fuzzy match (Michael's test, 22 Nov 2026, segments 342-343).
        /// Nothing changes on screen when the second look finds the same.
        /// </summary>
        System.Windows.Forms.Timer _again;
        string _noteFor;         // the document whose "EverTM learns here" note has been asked for
        string _projectNote;     // that note

        protected override IUIControl GetContentControl()
        {
            EnsureControl();
            return _control;
        }

        static void EnsureControl()
        {
            if (_control != null && !_control.IsDisposed) return;
            _control = new EverTmMatchesControl();
        }

        protected override void Initialize()
        {
            _instance = this;
            EnsureControl();
            _control.InsertRequested += (m, r) => Insert(m, r, fromPane: true);
            _control.OptionsChanged += (s, e) => LookUpActive();
            _control.VisibleChanged += (s, e) => { if (_control.Visible && _owed) LookUpActive(); };
            _control.Disposed += (s, e) => Shutdown();
            TranslationResultsGuard.Watch(this, _control);

            _again = new System.Windows.Forms.Timer { Interval = 4000 };
            _again.Tick += (s, e) => { _again.Stop(); LookUpActive(again: true); };

            _worker = new EverTmLookupWorker<Request, EverTmPaneResult>(RunLookup, Deliver,
                m => { try { DiagnosticLog.WriteAlways("EverTM", m); } catch { } });

            LicenseManager.Instance.LicenseStateChanged += OnLicenceChanged;
            ApplyLicence();

            _editor = StudioEditor.Controller;
            if (_editor != null)
            {
                _editor.ActiveDocumentChanged += OnActiveDocumentChanged;
                Attach(_editor.ActiveDocument);
            }
            LookUpActive();
        }

        void Shutdown()
        {
            try { _worker?.Stop(); } catch { }
            try { _again?.Stop(); _again?.Dispose(); } catch { }
            try { LicenseManager.Instance.LicenseStateChanged -= OnLicenceChanged; } catch { }
            try { if (_editor != null) _editor.ActiveDocumentChanged -= OnActiveDocumentChanged; } catch { }
            Attach(null);
        }

        // ---- licence ----

        static bool Licensed => LicenseManager.Instance.HasTier1Access;

        void OnLicenceChanged(object sender, EventArgs e)
        {
            var c = _control;
            if (c == null || c.IsDisposed) return;
            try
            {
                if (c.InvokeRequired) c.BeginInvoke((Action)(() => { ApplyLicence(); LookUpActive(); }));
                else { ApplyLicence(); LookUpActive(); }
            }
            catch { }
        }

        void ApplyLicence()
        {
            if (Licensed) _control.HideLicenseRequired();
            else { _worker?.Invalidate(); _control.ShowLicenseRequired(); }
        }

        // ---- the document and its segments ----

        void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            Attach(_editor?.ActiveDocument);
            LookUpActive();
        }

        void Attach(IStudioDocument doc)
        {
            if (_doc != null) { try { _doc.ActiveSegmentChanged -= OnActiveSegmentChanged; } catch { } }
            _doc = doc;
            _segmentKey = null;
            try { _again?.Stop(); } catch { }
            _worker?.Invalidate();
            if (_doc != null) _doc.ActiveSegmentChanged += OnActiveSegmentChanged;
        }

        void OnActiveSegmentChanged(object sender, EventArgs e) => LookUpActive();

        /// <summary>
        /// Copies the active segment and asks for its lookup. UI thread; costs the copy only. Never throws into Studio.
        /// </summary>
        void LookUpActive(bool again = false)
        {
            try
            {
                var c = _control;
                if (c == null || c.IsDisposed || !Licensed) return;
                if (!c.Visible) { _owed = true; _worker.Invalidate(); return; }
                _owed = false;

                var doc = _doc;
                if (doc == null) { _worker.Invalidate(); c.ShowMessage("Open a document to see EverTM's matches."); return; }
                var pair = doc.ActiveSegmentPair;
                if (pair?.Source == null) { _worker.Invalidate(); c.ShowMessage("Click a segment to see EverTM's matches."); return; }

                string key = doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id + "/" + pair.Properties?.Id.Id;
                if (again && key != _segmentKey) return;   // moved on meanwhile: that segment has its own lookup
                var file = doc.ActiveFile;
                var sourceCulture = file?.SourceFile?.Language?.CultureInfo?.Name;
                var targetCulture = file?.Language?.CultureInfo?.Name;
                if (string.IsNullOrEmpty(sourceCulture) || string.IsNullOrEmpty(targetCulture)) { c.ShowMessage("This document's languages can't be read."); return; }

                var editor = EverTmEditorSegment.From(pair.Source, new Sdl.Core.Globalization.CultureCode(sourceCulture));
                var shown = new EverTmPaneResult
                {
                    Editor = editor,
                    TagMapAtLookup = SegmentTagHandler.Serialize(pair.Source).TagMap,
                    SegmentId = pair.Properties?.Id.Id,
                    ParagraphId = doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id,
                    SourceLang = VaultMarkup.BaseLanguage(sourceCulture),
                    SourceCulture = sourceCulture,
                    TargetCulture = targetCulture,
                };
                var request = new Request
                {
                    Shown = shown,
                    Lookup = new EverTmMatchesLookup.Request
                    {
                        Document = editor.Pieces,
                        SourceLang = shown.SourceLang,
                        TargetLang = VaultMarkup.BaseLanguage(targetCulture),
                        SourceCulture = sourceCulture,
                        TargetCulture = targetCulture,
                        MinScore = Settings.SettingsService.Current.EverTmPaneMinScore,
                        MaxMatches = Settings.SettingsService.Current.EverTmPaneMaxMatches,
                    },
                };
                string fileKey = file?.LocalFilePath;
                if (!again && fileKey != null && fileKey != _noteFor) { _noteFor = fileKey; _projectNote = null; request.ProjectFile = fileKey; }

                request.Again = again;
                if (!again)
                {
                    _segmentKey = key;
                    c.MarkStale();
                    _again.Stop();
                    _again.Start();
                }
                _worker.Submit(request);
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: reading the segment failed: " + ex); } catch { }
                try { _control?.ShowMessage("EverTM Matches couldn't read this segment: " + ex.Message); } catch { }
            }
        }

        /// <summary>On the worker: the lookup, and once per document whether EverTM learns in it. No Studio objects.</summary>
        static EverTmPaneResult RunLookup(Request request)
        {
            if (VaultLibrary.Log == null)
                VaultLibrary.Log = message => DiagnosticLog.WriteAlways("EverTM", message);
            var library = VaultLibrary.For(VaultLibrary.DefaultRootInUse);
            var answer = EverTmMatchesLookup.Run(library, request.Lookup);
            var r = request.Shown;
            r.Matches = answer.Matches;
            r.Units = answer.Units;
            r.Problem = answer.Problem;
            r.Building = answer.Building;
            if (request.ProjectFile != null)
            {
                var learns = TmSearcher.EverTmLearns(request.ProjectFile, request.Lookup.SourceCulture, request.Lookup.TargetCulture);
                if (learns == false) r.ProjectNote = "EverTM is not in this project's TMs with Update ticked: segments you confirm won't be learned";
            }
            return r;
        }

        /// <summary>From the worker: draws the result on the UI thread if it is still the newest.</summary>
        void Deliver(EverTmLookupWorker<Request, EverTmPaneResult>.Result<EverTmPaneResult> result)
        {
            var c = _control;
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            c.BeginInvoke((Action)(() =>
            {
                if (c.IsDisposed || !_worker.IsLatest(result.Id)) return;
                var r = result.Value ?? new EverTmPaneResult();
                if (result.Error != null) r.Error = result.Error;
                r.Milliseconds = result.Milliseconds;
                if (r.ProjectNote != null) _projectNote = r.ProjectNote;
                else r.ProjectNote = _projectNote;
                if (result.Value != null && SameMatches(c.Result, r)) return;   // the second look found nothing new
                c.ShowResult(r);
            }));
        }

        /// <summary>Whether a second look found what is already shown (same units, same scores, same problem).</summary>
        static bool SameMatches(EverTmPaneResult shown, EverTmPaneResult fresh)
        {
            if (shown == null || fresh == null || shown.SegmentId != fresh.SegmentId || shown.ParagraphId != fresh.ParagraphId) return false;
            if (shown.Problem != fresh.Problem || shown.Error != fresh.Error) return false;
            var a = shown.Matches ?? new List<VaultMatch>();
            var b = fresh.Matches ?? new List<VaultMatch>();
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Score != b[i].Score || a[i].Candidate?.Id != b[i].Candidate?.Id || a[i].Candidate?.TargetRaw != b[i].Candidate?.TargetRaw) return false;
            return true;
        }

        // ---- inserting ----

        /// <summary>Alt+Shift+1–9: inserts match <paramref name="n"/> of those shown. UI thread.</summary>
        public static void InsertNumber(int n)
        {
            var vp = _instance;
            var c = _control;
            if (vp == null || c == null || c.IsDisposed) return;
            var m = c.MatchNumber(n);
            var r = c.Result;
            if (m == null || r == null) { c.ShowNote(r == null ? "EverTM Matches is still looking this segment up" : "There is no match " + n); return; }
            vp.Insert(m, r, fromPane: false);
        }

        /// <summary>
        /// Writes the match into the active segment's target: the match's text with the document's own tags, as
        /// Translation Results places them; plain text, said so, when the tags can't be placed. Never confirms.
        /// Labelled as a TM match ("TM, 87%, EverTM") as far as Studio allows. UI thread.
        /// </summary>
        void Insert(VaultMatch m, EverTmPaneResult r, bool fromPane)
        {
            var c = _control;
            var doc = _doc;
            try
            {
                var pair = doc?.ActiveSegmentPair;
                if (pair == null || m == null || r == null) return;
                // The matches must be this segment's: after a segment change they are greyed until the new ones arrive.
                if (pair.Properties?.Id.Id != r.SegmentId || doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id != r.ParagraphId)
                { c.ShowNote("These matches are for another segment"); return; }
                if (pair.Properties?.IsLocked == true) { c.ShowNote("This segment is locked: nothing inserted"); return; }

                string markup = r.Editor?.TargetMarkup(m, r.TagMapAtLookup);
                bool plain = markup == null || (m.Tags == VaultTagOutcome.Unmapped && r.Editor != null && r.Editor.HasTags);
                if (plain) markup = null;
                string text = EverTmEditorSegment.PlainTarget(m);
                bool written = false;

                SegmentWriteCheck.ProcessChecked(doc, pair, "EverTM match", (sp, cancel) =>
                {
                    var live = SegmentTagHandler.Serialize(sp.Source);
                    if (markup != null && live.TagMap.Count == r.TagMapAtLookup.Count)
                    {
                        written = SegmentTagHandler.ReconstructTarget(sp.Target, sp.Source, markup, live.TagMap);
                        if (written) return;
                    }
                    plain = true;
                    var template = SegmentTagHandler.FindFirstText(sp.Source);
                    if (template == null) { cancel.Cancel = true; return; }
                    var markers = SegmentTagHandler.CaptureCommentMarkers(sp.Target);
                    sp.Target.Clear();
                    var destination = SegmentTagHandler.OpenCommentMarkers(sp.Target, markers);
                    SegmentTagHandler.AppendText(destination, text, sp.Source, live.TagMap, template);
                    written = true;
                }, label: (d, p) => SegmentOrigin.MarkTmMatch(d, p, m.Score, SuperVaultProvider.DisplayName));

                if (!written) c.ShowNote("Nothing inserted: this segment's source has no text to copy the formatting from");
                else if (plain && r.Editor != null && r.Editor.HasTags) c.ShowNote("Match " + m.Score + "% inserted without formatting");
                else c.ShowNote("Match " + m.Score + "% inserted");
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: insert failed: " + ex); } catch { }
                c?.ShowNote("Not inserted: " + ex.Message);
            }
            finally
            {
                // Typing goes on in the target, as after inserting from Translation Results.
                if (fromPane)
                    try { c?.BeginInvoke((Action)(() => { try { StudioEditor.Controller?.Activate(); } catch { } })); } catch { }
            }
        }
    }
}
