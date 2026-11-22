using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// "Keep Translation Results behind Supervertaler's panes" (EverTM Matches > Options; off by default). Michael, 22
    /// Nov 2026: Translation Results kept covering TermLens on every segment, so EverTM Matches could not replace it.
    ///
    /// <para><b>What Studio does</b> (read from Studio 2024's editor): each time it looks the active segment up, it
    /// publishes "HighlightTmView", and the editor shows Translation Results: <c>Show()</c> and make it the selected tab
    /// of its group, which reopens it when closed. It holds back only when that would hide the Messages, Comments or
    /// TQA pane. There is no setting.</para>
    ///
    /// <para><b>What this does</b>: when a watched Supervertaler pane (TermLens, EverTM Matches, TermPicker) is hidden
    /// within a few seconds of a segment change, with no mouse button down, and Translation Results is now the pane in
    /// view, it selects the Supervertaler pane's tab again (<see cref="AbstractViewPartController.Show"/>: the same
    /// <c>Show()</c> + selected tab Studio used, without activating the pane, so the keyboard stays in the editor). At
    /// most three times per segment, so it can never fight Studio in a loop. Posted to the next UI message, which runs
    /// before Windows repaints, so the swap is normally not seen.</para>
    ///
    /// <para><b>What it leaves alone</b>: a tab the user clicks (a mouse button is down); a pane Translation Results did
    /// not cover; Translation Results in another dock group (then nothing is hidden). Whether Translation Results is in
    /// view is read by reflection (Studio's docking view is not public); if that fails, it does nothing.</para>
    ///
    /// <para><b>State</b>: the watched panes, the time of the last segment change and a counter, all on the UI thread.</para>
    /// </summary>
    internal static class TranslationResultsGuard
    {
        const string TranslationResultsId = "EditorView.TranslationMemoryViewPart";
        const long WindowMs = 4000;        // Studio's lookup, and so its "show", follows the segment change
        const int MaxPerSegment = 3;

        static readonly List<(AbstractViewPartController Part, Control Content)> Watched = new List<(AbstractViewPartController, Control)>();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static long _segmentChangedAt = -WindowMs;
        static int _restoresThisSegment;
        static bool _wired, _logged;
        static IStudioDocument _doc;

        public static bool On
        {
            get { try { return SettingsService.Current.KeepTranslationResultsBack; } catch { return false; } }
        }

        /// <summary>Watches a Supervertaler pane. UI thread; once per pane.</summary>
        public static void Watch(AbstractViewPartController part, Control content)
        {
            if (part == null || content == null) return;
            foreach (var w in Watched) if (ReferenceEquals(w.Part, part)) return;
            Watched.Add((part, content));
            content.VisibleChanged += (s, e) => OnVisibleChanged(part, content);
            content.Disposed += (s, e) => Watched.RemoveAll(w => ReferenceEquals(w.Part, part));
            WireEditor();
        }

        static void WireEditor()
        {
            if (_wired) return;
            var editor = StudioEditor.Controller;
            if (editor == null) return;
            _wired = true;
            editor.ActiveDocumentChanged += (s, e) => Attach(editor.ActiveDocument);
            Attach(editor.ActiveDocument);
        }

        static void Attach(IStudioDocument doc)
        {
            if (_doc != null) try { _doc.ActiveSegmentChanged -= OnSegmentChanged; } catch { }
            _doc = doc;
            if (_doc != null) _doc.ActiveSegmentChanged += OnSegmentChanged;
            OnSegmentChanged(null, EventArgs.Empty);   // a document opened: Studio looks its first segment up too
        }

        static void OnSegmentChanged(object sender, EventArgs e)
        {
            _segmentChangedAt = Clock.ElapsedMilliseconds;
            _restoresThisSegment = 0;
        }

        static void OnVisibleChanged(AbstractViewPartController part, Control content)
        {
            try
            {
                if (content.Visible || !On) return;
                if (Clock.ElapsedMilliseconds - _segmentChangedAt > WindowMs) return;   // not Studio's lookup
                if (Control.MouseButtons != MouseButtons.None) return;                 // the user clicked a tab
                if (_restoresThisSegment >= MaxPerSegment || content.IsDisposed || !content.IsHandleCreated) return;
                // After Studio has finished switching the tab: then Translation Results reads as in view.
                content.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (content.IsDisposed || part.IsInView || TranslationResultsInView(part) != true) return;
                        _restoresThisSegment++;
                        part.Show();
                        if (!_logged) { _logged = true; DiagnosticLog.Log("Panes", "Translation Results came forward over a Supervertaler pane; brought the pane back (logged once per session)"); }
                    }
                    catch (Exception ex) { DiagnosticLog.Log("Panes", "could not bring the pane back: " + ex.Message); }
                }));
            }
            catch { }
        }

        static PropertyInfo _studioWindow;
        static FieldInfo _viewPartField;

        /// <summary>Whether Studio's Translation Results is in view; null when that can't be read (then nothing is done).</summary>
        static bool? TranslationResultsInView(AbstractViewPartController part)
        {
            try
            {
                // The pane's own view (2022, 2024 and 2026 all keep an internal IViewPart ViewPart, whose View is the
                // Editor view); failing that, Studio's active view (2024 and 2026's private StudioWindow).
                if (_viewPartField == null)
                    _viewPartField = typeof(AbstractViewPartController).GetField("ViewPart", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var view = Get(_viewPartField?.GetValue(part), "View");
                if (view == null)
                {
                    if (_studioWindow == null)
                        _studioWindow = typeof(AbstractViewPartController).GetProperty("StudioWindow", BindingFlags.Instance | BindingFlags.NonPublic);
                    view = Get(_studioWindow?.GetValue(part), "ActiveView");
                }
                var parts = Get(view, "ViewParts");
                if (parts == null) return null;
                object tr = null;
                foreach (var p in parts.GetType().GetProperties())
                {
                    var ix = p.GetIndexParameters();
                    if (ix.Length == 1 && ix[0].ParameterType == typeof(string)) { tr = p.GetValue(parts, new object[] { TranslationResultsId }); break; }
                }
                return Get(tr, "IsInView") as bool?;
            }
            catch { return null; }
        }

        /// <summary>A property by name, also when the object only has it through an interface it implements explicitly.</summary>
        static object Get(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p == null)
                foreach (var i in o.GetType().GetInterfaces())
                    if ((p = i.GetProperty(name)) != null) break;
            return p?.GetValue(o);
        }
    }
}
