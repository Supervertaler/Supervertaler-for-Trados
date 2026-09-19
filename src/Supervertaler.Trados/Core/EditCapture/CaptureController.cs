using System;
using System.Collections.Generic;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core.EditCapture
{
    /// <summary>
    /// EditLens phase 1: watch the editor and record what was observed. No pane,
    /// no warnings, no model, no network, no learning. Just accumulate the asset.
    ///
    /// <para><b>Why a plugin at all:</b> the bilingual file keeps the final target
    /// and its origin, but nothing anywhere keeps what was in the target box
    /// <em>before</em> the translator changed it. Checked across 321 files of the
    /// real archive: <c>previous-origin</c> appears zero times. That proposal
    /// exists only in memory between population and confirmation, so every day
    /// without this running destroys it permanently.</para>
    ///
    /// <para><b>Two subscriptions, deliberately.</b> <c>ContentChanged</c> for
    /// population and <c>ActiveSegmentChanged</c> for departure.
    /// <c>SegmentsConfirmationLevelChanged</c> is NOT used: Studio can raise it
    /// after the cursor has already moved on — Qualitivity's own source carries a
    /// comment saying exactly that, and works around it by amending the record it
    /// already wrote. Capturing on departure means the wrong record is never
    /// written, so the ordering stops mattering and the handler stops being
    /// needed. The close sweep supplies the authoritative final state.</para>
    ///
    /// <para><b>The sweep runs on close, not on save.</b> It walks every
    /// segment, and on the first live run a single save wrote 930 rows for a
    /// document of that size - almost all of them identical to the last pass.
    /// At a few saves an hour that is most of the database and none of the
    /// value. Departures already cover everything touched mid-session, so
    /// close is enough, and the cost of a crash is the tail the append-only
    /// design already accepts.</para>
    ///
    /// <para>Nothing here may throw into Studio's UI thread: every handler is
    /// wrapped, and failures are logged and swallowed. A capture failure must be
    /// invisible to the translator.</para>
    /// </summary>
    internal sealed class CaptureController : IDisposable
    {
        private readonly CaptureStore _store;
        private EditorController _editor;
        private IStudioDocument _doc;

        // The segment we are sitting on. Kept as a reference AND as ids: the
        // reference is O(1) and right nearly always, the ids are the fallback
        // when it has gone stale across a filter change or a reload. Looking a
        // segment up by id means walking every pair in the document, which is
        // not something to do on every cursor move.
        private ISegmentPair _current;

        private string _project, _srcLang, _tgtLang;
        private bool _started;

        public CaptureController(CaptureStore store) { _store = store; }

        public void Start(EditorController editor)
        {
            if (_started || editor == null || _store == null) return;
            try
            {
                _editor = editor;
                _editor.ActiveDocumentChanged += OnDocumentChanged;
                _editor.Closing += OnClosing;
                Attach(_editor.ActiveDocument);
                _started = true;
                DiagnosticLog.Log("EditCapture", "capture started");
            }
            catch (Exception ex) { Swallow("start", ex); }
        }

        // ─── Document lifecycle ─────────────────────────────────────

        private void Attach(IStudioDocument doc)
        {
            try
            {
                Detach();
                _doc = doc;
                if (_doc == null) return;

                _doc.ActiveSegmentContentIsReady += OnContentReady;
                _doc.ActiveSegmentChanged += OnActiveSegmentChanged;

                _project = SafeProjectName(_doc);
                _srcLang = SafeLang(_doc, true);
                _tgtLang = SafeLang(_doc, false);
                Remember(_doc.ActiveSegmentPair);
            }
            catch (Exception ex) { Swallow("attach", ex); }
        }

        private void Detach()
        {
            // Unsubscribe even when the previous operation threw: a handler left
            // attached to a closed document is the leak this guards.
            try
            {
                if (_doc != null)
                {
                    _doc.ActiveSegmentContentIsReady -= OnContentReady;
                    _doc.ActiveSegmentChanged -= OnActiveSegmentChanged;
                }
            }
            catch { }
            finally { _doc = null; _current = null; }
        }

        private void OnDocumentChanged(object sender, DocumentEventArgs e)
        {
            try
            {
                // The document we are leaving gets a final sweep; nothing else
                // will speak for it once it is gone.
                SweepDocument(_doc);
                Attach(_editor?.ActiveDocument);
            }
            catch (Exception ex) { Swallow("document changed", ex); }
        }

        private void OnClosing(object sender, CancelDocumentEventArgs e)
        {
            try { SweepDocument(e?.Document ?? _doc); }
            catch (Exception ex) { Swallow("closing", ex); }
        }

        // ─── Capture points ─────────────────────────────────────────

        /// <summary>
        /// The active segment's content has settled — this is the proposal,
        /// whatever put it there: MT, TM, AI, a pre-translate pass or a paste.
        ///
        /// <para><b>ContentChanged is deliberately not used.</b> It names the
        /// segments that changed, which looked ideal, but there is no reliable
        /// way to get from one of those ISegments back to its pair, and the pair
        /// is what carries identity. Measured, in this order: matching by segment
        /// number found a different paragraph unit (numbers repeat, and a merged
        /// document renumbers); ParentParagraphUnit is null on those segments;
        /// and object identity does not match either, because Studio does not
        /// hand out the same instance the pair holds. Three attempts, three live
        /// runs, no populated events. This event carries no payload at all and
        /// therefore needs none: it is about the active segment by definition, so
        /// the pair is simply ActiveSegmentPair.</para>
        ///
        /// <para>The cost is a known gap: a segment changed somewhere else in the
        /// document — auto-propagate — gets no proposal. Its final state is still
        /// recorded by the sweep at close, so the record says "no proposal
        /// captured" rather than inventing one.</para>
        /// </summary>
        private void OnContentReady(object sender, EventArgs e)
        {
            try { Emit(CaptureEvent.Populated, _doc?.ActiveSegmentPair); }
            catch (Exception ex) { Swallow("content ready", ex); }
        }

        /// <summary>
        /// The cursor moved. The segment we just left is unambiguous, so its
        /// final state is read here rather than at a confirmation event that may
        /// arrive after the move.
        /// </summary>
        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            try
            {
                EmitLeft();
                Remember(_doc?.ActiveSegmentPair);
            }
            catch (Exception ex) { Swallow("active segment changed", ex); }
        }

        private void EmitLeft()
        {
            if (_current == null) return;
            try
            {
                // A stale reference throws on read; the sweep at save or close
                // covers anything missed here, so there is no id lookup to fall
                // back to and no shadow copy of the document to maintain.
                Emit(CaptureEvent.Left, _current);
            }
            catch (Exception ex) { Swallow("left", ex); }
        }

        /// <summary>
        /// Read the whole document. Later than any <c>left</c> event, so where the
        /// two disagree this one is right. Also the only thing that captures the
        /// last segment of a session, which is never departed from, and comments
        /// added after a segment was confirmed — the API raises no event at all
        /// for a comment, so this is the substitute for polling.
        /// </summary>
        private void SweepDocument(IStudioDocument doc)
        {
            if (doc == null) return;
            try
            {
                foreach (var pair in doc.SegmentPairs)
                    Emit(CaptureEvent.Sweep, pair, doc);
            }
            catch (Exception ex) { Swallow("sweep", ex); }
        }

        // ─── Reading a pair ─────────────────────────────────────────

        private void Remember(ISegmentPair pair)
        {
            _current = pair;
        }

        /// <summary>
        /// Identity and text come from the PAIR, on every path.
        ///
        /// <para>Two earlier attempts failed here and both are worth recording.
        /// Looking a pair up by segment <em>number</em> matched a different
        /// paragraph unit, because those numbers repeat and a merged "Multiple
        /// Files" document renumbers them. Taking identity from the ISegment
        /// instead looked cleaner but <c>ParentParagraphUnit</c> is null on the
        /// segments Studio hands to ContentChanged, so every row was rejected by
        /// the NOT NULL columns and an entire session was lost. The pair is the
        /// only graph that reliably answers both questions, so the content event
        /// is mapped onto it by object identity — never by number.</para>
        /// </summary>
        private void Emit(string kind, ISegmentPair pair, IStudioDocument doc = null)
        {
            try
            {
                doc = doc ?? _doc;
                if (pair == null || doc == null) return;

                var puId = "";
                try { puId = doc.GetParentParagraphUnit(pair)?.Properties?.ParagraphUnitId.Id ?? ""; }
                catch { }
                var segId = "";
                try { segId = pair.Properties?.Id.Id ?? ""; } catch { }

                // Either id missing makes the row unpairable and the NOT NULL
                // columns reject it anyway. Count it as a gap and move on.
                if (puId.Length == 0 || segId.Length == 0) return;

                string originType = null;
                int? match = null;
                try
                {
                    var origin = pair.Properties?.TranslationOrigin;
                    if (origin != null)
                    {
                        originType = string.IsNullOrEmpty(origin.OriginType) ? null : origin.OriginType;
                        match = origin.MatchPercent;
                    }
                }
                catch { }

                _store.Enqueue(new CaptureEvent
                {
                    Event = kind,
                    FileId = SafeFileId(doc),
                    UnitId = puId,
                    SegId = segId,
                    Source = SafeText(pair.Source),
                    Target = SafeText(pair.Target),
                    Origin = originType,
                    MatchPercent = match,
                    ConfLevel = SafeConfLevel(pair),
                    Project = _project,
                    SrcLang = _srcLang,
                    TgtLang = _tgtLang
                });
            }
            catch (Exception ex) { Swallow("emit", ex); }
        }

        private static string SafeConfLevel(ISegmentPair pair)
        {
            try
            {
                return (pair.Properties?.ConfirmationLevel
                        ?? Sdl.Core.Globalization.ConfirmationLevel.Unspecified).ToString();
            }
            catch { return null; }
        }

        private static string SafeText(ISegment seg)
        {
            try { return seg == null ? null : SegmentTagHandler.GetFinalText(seg); }
            catch { return null; }
        }

        private static string SafeFileId(IStudioDocument doc)
        {
            try
            {
                var f = doc?.ActiveFile ?? System.Linq.Enumerable.FirstOrDefault(doc?.Files);
                return f?.Id.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static string SafeProjectName(IStudioDocument doc)
        {
            try { return (doc?.Project as Sdl.ProjectAutomation.FileBased.FileBasedProject)?.GetProjectInfo()?.Name; }
            catch { return null; }
        }

        private static string SafeLang(IStudioDocument doc, bool source)
        {
            try
            {
                var f = doc?.ActiveFile;
                return source
                    ? f?.SourceFile?.Language?.DisplayName
                    : f?.Language?.DisplayName;
            }
            catch { return null; }
        }

        private static void Swallow(string where, Exception ex)
        {
            try { DiagnosticLog.Log("EditCapture", where + ": " + ex.GetType().Name + " " + ex.Message); }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                if (_editor != null)
                {
                    _editor.ActiveDocumentChanged -= OnDocumentChanged;
                    _editor.Closing -= OnClosing;
                }
            }
            catch { }
            finally
            {
                Detach();
                _editor = null;
                _started = false;
            }
        }
    }
}
