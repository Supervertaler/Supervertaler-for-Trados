using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>A page of a match's older translations: asked for when a match with some is selected, or "Show more".</summary>
        sealed class OlderRequest
        {
            public VaultMatch Match;
            public EverTmPaneResult Result;
            public int Page;
        }

        sealed class OlderAnswer
        {
            public VaultMatch Match;            // which match and page this is: the control drops it if that match is no longer selected
            public int Page;
            public List<VaultMatch> Items = new List<VaultMatch>();
            public int Total;
            public string Error;
        }

        EditorController _editor;
        IStudioDocument _doc;
        EverTmLookupWorker<Request, EverTmPaneResult> _worker;
        /// <summary>Older translations, on a worker of their own (newest request wins, like the lookups), so a page never
        /// waits behind a segment's lookup or holds one up.</summary>
        EverTmLookupWorker<OlderRequest, OlderAnswer> _olderWorker;
        /// <summary>
        /// The project's Studio TMs (Options > "Also show matches from the project's Studio TMs"), on a worker of their
        /// own, newest segment wins, so EverTM's matches never wait for them (StudioTmMatches). The session holds the
        /// open TMs for the current document and is used only on that worker; it is replaced when the switch goes off.
        /// The last answer is kept for its segment, so looking the same segment up again (the second look, a removal)
        /// doesn't search the Studio TMs again.
        /// </summary>
        EverTmLookupWorker<StudioTmMatches.Request, StudioTmMatches.Answer> _studioWorker;
        volatile StudioTmMatches.Session _studioSession = new StudioTmMatches.Session();   // read on the Studio TM worker
        StudioTmMatches.Answer _studioAnswer;
        StudioTmMatches.Request _studioAsked;   // the search on its way, if any
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
        /// <summary>The last removal, for Undo (until the next removal or until the pane closes); and the library whose
        /// RemovalsApplied this pane listens to.</summary>
        VaultRemovalKey _lastRemoval;
        VaultTranslationEdit _lastEdit;     // the last edit, for Undo; only one of the two is set
        VaultLibrary _removalLibrary;
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
            _control.OlderWanted += (m, r, page) => _olderWorker?.Submit(new OlderRequest { Match = m, Result = r, Page = page });
            _control.RemoveRequested += m => Remove(m);
            _control.EditRequested += m => Edit(m);
            _control.UndoRequested += (s, e) => Undo();
            _control.VisibleChanged += (s, e) => { if (_control.Visible && _owed) LookUpActive(); };
            _control.Disposed += (s, e) => Shutdown();
            TranslationResultsGuard.Watch(this, _control);

            _again = new System.Windows.Forms.Timer { Interval = 4000 };
            _again.Tick += (s, e) => { _again.Stop(); LookUpActive(again: true); };

            _worker = new EverTmLookupWorker<Request, EverTmPaneResult>(RunLookup, Deliver,
                m => { try { DiagnosticLog.WriteAlways("EverTM", m); } catch { } });
            _olderWorker = new EverTmLookupWorker<OlderRequest, OlderAnswer>(RunOlder, DeliverOlder,
                m => { try { DiagnosticLog.WriteAlways("EverTM", m); } catch { } });
            _studioWorker = new EverTmLookupWorker<StudioTmMatches.Request, StudioTmMatches.Answer>(
                r => StudioTmMatches.Search(_studioSession, r), DeliverStudio,
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
            try { _olderWorker?.Stop(); } catch { }
            try { _studioWorker?.Stop(); } catch { }
            _studioSession = new StudioTmMatches.Session();   // the open TMs released
            try { if (_removalLibrary != null) _removalLibrary.RemovalsApplied -= OnRemovalsApplied; } catch { }
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
            _olderWorker?.Invalidate();
            ForgetStudio();
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
                if (!c.Visible) { _owed = true; _worker.Invalidate(); ForgetStudio(); return; }
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
                AskStudio(shown, request, fileKey, key, editor);
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

        // ---- Studio TMs ----

        /// <summary>
        /// With the switch on: marks the result as waiting for the Studio TMs, and asks for their search unless this
        /// segment's answer (same minimum and maximum) is already here or on its way. With it off: nothing, and the open
        /// TMs are let go. UI thread.
        /// </summary>
        void AskStudio(EverTmPaneResult shown, Request request, string fileKey, string segmentKey, EverTmEditorSegment editor)
        {
            if (!Settings.SettingsService.Current.EverTmPaneStudioTms || fileKey == null)
            {
                if (_studioAnswer != null || _studioAsked != null || _studioSession.Document != null)
                {
                    ForgetStudio();
                    _studioSession = new StudioTmMatches.Session();
                }
                return;
            }
            var l = request.Lookup;
            int min = Math.Max(EverTmMatchesLookup.MinScore, Math.Min(100, l.MinScore));
            int max = Math.Max(1, Math.Min(EverTmMatchesLookup.MaxMatches, l.MaxMatches));
            shown.StudioKey = fileKey + "|" + segmentKey;
            bool Same(string key, int mn, int mx) => key == shown.StudioKey && mn == min && mx == max;
            if (_studioAnswer != null && Same(_studioAnswer.Key, _studioAnswer.MinScore, _studioAnswer.MaxMatches)) return;
            _studioAnswer = null;
            shown.StudioPending = true;
            if (_studioAsked != null && Same(_studioAsked.Key, _studioAsked.MinScore, _studioAsked.MaxMatches)) return;
            _studioAsked = new StudioTmMatches.Request
            {
                Key = shown.StudioKey, DocumentFile = fileKey, Segment = editor.Segment, Document = editor.Pieces,
                SourceLang = l.SourceLang, TargetLang = l.TargetLang, SourceCulture = l.SourceCulture, TargetCulture = l.TargetCulture,
                MinScore = min, MaxMatches = max,
            };
            _studioWorker.Submit(_studioAsked);
        }

        void ForgetStudio()
        {
            _studioWorker?.Invalidate();
            _studioAnswer = null;
            _studioAsked = null;
        }

        /// <summary>From the Studio TM worker: kept for its segment, and merged into the result shown if it is that segment's.</summary>
        void DeliverStudio(EverTmLookupWorker<StudioTmMatches.Request, StudioTmMatches.Answer>.Result<StudioTmMatches.Answer> result)
        {
            var c = _control;
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            c.BeginInvoke((Action)(() =>
            {
                if (c.IsDisposed || !_studioWorker.IsLatest(result.Id)) return;
                var asked = _studioAsked;
                var a = result.Value;
                if (a == null)
                {
                    if (asked == null) return;
                    a = new StudioTmMatches.Answer { Key = asked.Key, MinScore = asked.MinScore, MaxMatches = asked.MaxMatches };
                    a.LeftOut.Add("the search failed: " + result.Error);
                }
                _studioAnswer = a;
                _studioAsked = null;
                var r = c.LastResult;
                if (r == null || r.StudioKey != a.Key) return;
                r.MergeStudio(a, a.MaxMatches);
                c.UpdateResult(r);
            }));
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
                if (r.StudioKey != null && _studioAnswer != null && _studioAnswer.Key == r.StudioKey) r.MergeStudio(_studioAnswer, _studioAnswer.MaxMatches);
                c.ShowResult(r);
            }));
        }

        /// <summary>
        /// On the worker: one page of a match's older translations (<see cref="VaultLibrary.Translations"/>, 50 at a
        /// time), each made ready to insert against this segment's source (its tags placed, a number put right). A page
        /// reads 50 units: about 18 ms, also in a group of thousands.
        /// </summary>
        static OlderAnswer RunOlder(OlderRequest request)
        {
            var answer = new OlderAnswer { Match = request.Match, Page = request.Page };
            var r = request.Result;
            if (request.Match == null || r == null || string.IsNullOrEmpty(r.TargetCulture)) return answer;
            var document = r.Editor?.Pieces;
            if (document == null) return answer;   // never so: the segment was copied before its lookup
            try
            {
                var library = VaultLibrary.For(VaultLibrary.DefaultRootInUse);
                var page = library.Translations(request.Match, r.SourceLang, VaultMarkup.BaseLanguage(r.TargetCulture), request.Page, r.TargetCulture);
                answer.Total = page.Total;
                foreach (var c in page.Items)
                {
                    var m = VaultLookup.Offer(document, request.Match, c);
                    if (m != null) answer.Items.Add(m);
                }
            }
            catch (Exception ex)
            {
                answer.Error = ex.Message;   // shown in the pane; the worker logs nothing for a caught failure, so log it here
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: reading older translations failed: " + ex); } catch { }
            }
            return answer;
        }

        void DeliverOlder(EverTmLookupWorker<OlderRequest, OlderAnswer>.Result<OlderAnswer> result)
        {
            var c = _control;
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            var a = result.Value;
            if (a == null) return;   // RunOlder catches its own failures, so there is always an answer
            c.BeginInvoke((Action)(() =>
            {
                if (c.IsDisposed || !_olderWorker.IsLatest(result.Id)) return;
                c.ShowOlder(a.Match, a.Page, a.Items, a.Total, a.Error);
            }));
        }

        /// <summary>Whether a second look found what is already shown (same units, same scores, same problem).</summary>
        static bool SameMatches(EverTmPaneResult shown, EverTmPaneResult fresh)
        {
            if (shown == null || fresh == null || shown.SegmentId != fresh.SegmentId || shown.ParagraphId != fresh.ParagraphId) return false;
            if (shown.Problem != fresh.Problem || shown.Error != fresh.Error) return false;
            if (shown.StudioKey != fresh.StudioKey || shown.StudioPending != fresh.StudioPending) return false;
            var a = shown.EverTmMatches ?? shown.Matches ?? new List<VaultMatch>();
            var b = fresh.Matches ?? new List<VaultMatch>();
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Score != b[i].Score || a[i].Candidate?.Id != b[i].Candidate?.Id || a[i].Candidate?.TargetRaw != b[i].Candidate?.TargetRaw) return false;
            return true;
        }

        // ---- removing a wrong translation ----

        /// <summary>
        /// "Remove from EverTM" (right-click, or Delete): every stored copy of this source with this translation, in
        /// every formatting and variant, both directions (VaultLibrary.Remove). Only EverTM: never a Studio TM, a file or
        /// the target. No "are you sure?": Undo instead (Michael's design, 9 Nov 2026). It is hidden here at once and
        /// applied in the background; once applied, the segment is looked up again, so the next translation (if any)
        /// takes its place. UI thread; Remove itself never waits or throws.
        /// </summary>
        void Remove(VaultMatch m)
        {
            var c = _control;
            var r = c?.LastResult;
            if (m?.Candidate == null || r == null || string.IsNullOrEmpty(r.TargetCulture)) return;
            if (NotOurs(m, r)) return;
            try
            {
                var library = VaultLibrary.For(VaultLibrary.DefaultRootInUse);
                ListenForRemovals(library);
                var key = library.Remove(m.Candidate, r.SourceLang, VaultMarkup.BaseLanguage(r.TargetCulture), Environment.UserName);
                if (key == null) { c.ShowNote("Nothing to remove"); return; }
                _lastRemoval = key;
                _lastEdit = null;
                c.ShowRemoved("Removed from EverTM only (your Studio TMs are unchanged): \u201C" + Short(EverTmEditorSegment.PlainTarget(m)) + "\u201D", undo: true);
                LookUpActive();
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: remove failed: " + ex); } catch { }
                c.ShowNote("Not removed: " + ex.Message);
            }
        }

        void Undo()
        {
            var edit = _lastEdit;
            if (edit != null)
            {
                _lastEdit = null;
                try
                {
                    var lib = VaultLibrary.For(VaultLibrary.DefaultRootInUse);
                    ListenForRemovals(lib);
                    lib.UndoEdit(edit, Environment.UserName);
                    _control.ShowRemoved("Edit undone: the old translation comes back in a moment", undo: false);
                }
                catch (Exception ex)
                {
                    try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: undo failed: " + ex); } catch { }
                    _control.ShowNote("Not undone: " + ex.Message);
                }
                return;
            }
            var key = _lastRemoval;
            if (key == null) return;
            _lastRemoval = null;
            try
            {
                var library = VaultLibrary.For(VaultLibrary.DefaultRootInUse);
                ListenForRemovals(library);
                library.Restore(key, Environment.UserName);
                _control.ShowRemoved("Removal undone: it will be offered again in a moment", undo: false);
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: undo failed: " + ex); } catch { }
                _control.ShowNote("Not undone: " + ex.Message);
            }
        }

        /// <summary>
        /// "Edit in EverTM..." (right-click, or F2): the stored source and target, with their tags as markers, and the
        /// fields, in a window (<see cref="EverTmEditDialog"/>). On Save, VaultLibrary.Edit: every copy of this
        /// translation becomes the edited one (the old text marked removed, so an import doesn't bring it back), or only
        /// the fields change. Only EverTM; never a Studio TM, a file or the target. Undo in the status line.
        /// </summary>
        void Edit(VaultMatch m)
        {
            var c = _control;
            var r = c?.LastResult;
            if (m?.Candidate == null || r == null || string.IsNullOrEmpty(r.TargetCulture)) return;
            if (NotOurs(m, r)) return;
            try
            {
                VaultTranslationEdit edit;
                using (var dialog = new EverTmEditDialog(m.Candidate, r.SourceCulture ?? r.SourceLang, r.TargetCulture))
                {
                    if (dialog.ShowDialog(c.FindForm()) != System.Windows.Forms.DialogResult.OK) return;
                    edit = new VaultTranslationEdit
                    {
                        SourceLang = r.SourceLang, TargetLang = VaultMarkup.BaseLanguage(r.TargetCulture),
                        ShownSource = m.Candidate.SourceRaw, ShownTarget = m.Candidate.TargetRaw,
                        NewSource = dialog.NewSource, NewTarget = dialog.NewTarget, Fields = dialog.NewFields,
                        OldFields = m.Candidate.Fields?.ToList() ?? new List<(string, string)>(),
                        OldChanged = m.Candidate.Changed, OldChangedBy = m.Candidate.ChangedBy,
                    };
                }
                var library = VaultLibrary.For(VaultLibrary.DefaultRootInUse);
                ListenForRemovals(library);
                var queued = library.Edit(edit, Environment.UserName);
                if (queued == null) { c.ShowNote("Nothing changed"); return; }
                _lastEdit = queued;
                _lastRemoval = null;
                c.ShowRemoved(queued.TextChanged
                    ? "Edited in EverTM only (your Studio TMs are unchanged): \u201C" + Short(VaultMarkup.Plain(queued.NewTarget)) + "\u201D"
                    : "Fields edited in EverTM only", undo: true);
                LookUpActive();
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.WriteAlways("EverTM", "EverTM Matches: edit failed: " + ex); } catch { }
                c.ShowNote("Not edited: " + ex.Message);
            }
        }

        /// <summary>A Studio TM's match: not EverTM's to change. Says so.</summary>
        bool NotOurs(VaultMatch m, EverTmPaneResult r)
        {
            var tm = r.StudioOf(m);
            if (tm == null) return false;
            _control.ShowNote("This match is from the Studio TM " + tm.Name + ": edit or delete it in Studio's Translation Results");
            return true;
        }

        void ListenForRemovals(VaultLibrary library)
        {
            if (ReferenceEquals(library, _removalLibrary)) return;
            if (_removalLibrary != null) _removalLibrary.RemovalsApplied -= OnRemovalsApplied;
            _removalLibrary = library;
            library.RemovalsApplied += OnRemovalsApplied;
        }

        /// <summary>On EverTM's learning thread: a removal or restore is in the store and the index; look again.</summary>
        void OnRemovalsApplied()
        {
            var c = _control;
            if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
            try { c.BeginInvoke((Action)(() => { if (!c.IsDisposed) LookUpActive(); })); } catch { }
        }

        static string Short(string text)
        {
            text = text ?? "";
            return text.Length <= 60 ? text : text.Substring(0, 57).TrimEnd() + "\u2026";
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

                var tm = r.StudioOf(m);   // a Studio TM's match: labelled with that TM's name
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
                }, label: (d, p) => SegmentOrigin.MarkTmMatch(d, p, m.Score, tm?.Name ?? SuperVaultProvider.DisplayName));

                string what = "Match " + m.Score + "%" + (tm != null ? " from " + tm.Name : "");
                if (!written) c.ShowNote("Nothing inserted: this segment's source has no text to copy the formatting from");
                else if (plain && r.Editor != null && r.Editor.HasTags) c.ShowNote(what + " inserted without formatting");
                else c.ShowNote(what + " inserted");
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
