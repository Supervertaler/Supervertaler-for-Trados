using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
    /// TQA pane. There is no setting (Michael asked RWS for one on the Ideas portal, 22 Nov 2026).</para>
    ///
    /// <para><b>What this does</b>: a watched Supervertaler pane (TermLens, EverTM Matches, TermPicker) that is in view
    /// is "wanted". Five times a second, a wanted pane that Translation Results has covered gets its tab selected again
    /// (<see cref="AbstractViewPartController.Show"/>: the same <c>Show()</c> + selected tab Studio used, without
    /// activating the pane, so the keyboard stays in the editor). It stays wanted until the user clicks at its tabs, or
    /// until something other than Translation Results has been in front of it for a while (the user slid an auto-hide
    /// pane away, or left the editor).</para>
    ///
    /// <para><b>Why it works this way</b> (Michael's tests, 22 Nov 2026). The first version waited to be told a pane
    /// was covered: Studio's docking never told. The second looked only during the 1.5 s after a segment change, and a
    /// pane had to have been in view in the 2.5 s before: Studio brought Translation Results forward a second time after
    /// those looks, and the pane stayed covered ("stuck"). Studio's interface is busy while it looks a segment up, and
    /// no plugin code runs until it is done, so Translation Results always shows for as long as that lookup takes
    /// (about one to two seconds); only the sticking could be fixed.</para>
    ///
    /// <para><b>Choices it leaves alone</b>: a mouse click at the pane's tabs (seen by a message filter, so a click is
    /// noticed even when the check runs after the button is up), Translation Results in another dock group (then nothing
    /// is covered). Not noticed: picking Translation Results from Studio's View ribbon; it is brought back behind.
    /// Whether Translation Results is in view is read by reflection (Studio's docking view is not public); if that
    /// fails, nothing is done.</para>
    ///
    /// <para><b>Fighting Studio</b>: at most <see cref="MaxPerSegment"/> restores per segment, so a Studio that keeps
    /// showing Translation Results can never be fought in a loop; the next segment starts again.</para>
    ///
    /// <para><b>State</b>: the watched panes, for each wanted one when and where it was last seen in view, the last mouse
    /// press, one UI timer and a counter, all on the UI thread. A tick reads one property per watched pane (three panes),
    /// plus one reflection read while a pane is covered: nothing measurable.</para>
    /// </summary>
    internal static class TranslationResultsGuard
    {
        const string TranslationResultsId = "EditorView.TranslationMemoryViewPart";
        const int MaxPerSegment = 6;
        const int TickMs = 200;
        /// <summary>Checks in a row with neither the pane nor Translation Results in view before the pane stops being
        /// wanted. Counted in checks, not time: while Studio is busy no check runs, however long it takes.</summary>
        const int GoneChecks = 5;
        const int MaxTraced = 12;         // the first decisions of each session go to the diagnostic log, then it is quiet

        sealed class Seen
        {
            public long At;               // Clock ms when last seen in view
            public Rectangle Area;        // its screen area then
            public int Gone;              // checks in a row with something else (not Translation Results) in front
        }

        static readonly List<(AbstractViewPartController Part, Control Content)> Watched = new List<(AbstractViewPartController, Control)>();
        /// <summary>The wanted panes.</summary>
        static readonly Dictionary<AbstractViewPartController, Seen> Wanted = new Dictionary<AbstractViewPartController, Seen>();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static int _restoresThisSegment, _traced;
        static bool _wired;
        static IStudioDocument _doc;
        static Timer _tick;
        static long _segmentAt;
        static long _downAt = -1;
        static Point _downWhere;

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
            content.Disposed += (s, e) => { Watched.RemoveAll(w => ReferenceEquals(w.Part, part)); Wanted.Remove(part); };
            WireEditor();
        }

        static void WireEditor()
        {
            if (_wired) return;
            var editor = StudioEditor.Controller;
            if (editor == null) return;
            _wired = true;
            Application.AddMessageFilter(new MouseDowns());
            _tick = new Timer { Interval = TickMs };
            _tick.Tick += (s, e) => { try { Check(); } catch { } };
            _tick.Start();
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
            _restoresThisSegment = 0;
            _segmentAt = Clock.ElapsedMilliseconds;
            try { Check(); } catch { }
        }

        /// <summary>
        /// Notes the panes in view (wanted), and brings back each wanted pane Translation Results has covered, unless
        /// the user clicked at its tabs since it was last in view.
        /// </summary>
        static void Check()
        {
            if (!On) { Wanted.Clear(); return; }
            long now = Clock.ElapsedMilliseconds;
            bool? tr = null;
            foreach (var w in Watched.ToArray())
            {
                var part = w.Part;
                if (w.Content.IsDisposed) continue;
                bool inView;
                try { inView = part.IsInView; } catch { continue; }
                if (inView)
                {
                    if (!Wanted.TryGetValue(part, out var s)) Wanted[part] = s = new Seen();
                    s.At = now; s.Gone = 0;
                    try { s.Area = w.Content.RectangleToScreen(w.Content.ClientRectangle); } catch { }
                    continue;
                }
                if (!Wanted.TryGetValue(part, out var seen)) continue;
                if (ClickedAtItsTabs(seen, w.Content))
                {
                    Wanted.Remove(part);
                    Trace(Name(part) + ": left behind (a mouse click at its tabs)");
                    continue;
                }
                if (tr == null) tr = TranslationResultsInView(part);
                if (tr != true)
                {
                    // Something else is in front (or it can't be read): leave it, and stop wanting the pane after a while.
                    if (++seen.Gone >= GoneChecks) { Wanted.Remove(part); Trace(Name(part) + ": not in view, and Translation Results isn't either: left alone"); }
                    continue;
                }
                seen.Gone = 0;
                if (_restoresThisSegment >= MaxPerSegment) continue;
                if (++_restoresThisSegment == MaxPerSegment) Trace(Name(part) + ": brought back " + MaxPerSegment + " times on this segment; no more until the next one");
                part.Show();
                bool back = false;
                try { back = part.IsInView; } catch { }
                Trace(Name(part) + ": covered by Translation Results; brought back " + (now - _segmentAt) + " ms after the segment change (in view now: " + back + ")");
                if (back) seen.At = now;
            }
        }

        /// <summary>
        /// A mouse press since the pane was last in view, at its area or a strip above and below it, where a docked
        /// group draws its tabs: the user chose a tab, Translation Results perhaps. A press anywhere else, such as on a
        /// segment in the editor, is not a choice of pane.
        /// </summary>
        static bool ClickedAtItsTabs(Seen seen, Control content)
        {
            if (_downAt < seen.At || seen.Area.IsEmpty) return false;
            var area = seen.Area;
            int strip = 50;
            try { strip = (int)(50 * content.DeviceDpi / 96.0); } catch { }
            area.Inflate(0, strip);
            return area.Contains(_downWhere);
        }

        /// <summary>Notes each mouse press in Studio (never handles one). A check may run well after the button is up,
        /// when Studio has been busy, so the press is remembered rather than read from the mouse at check time.</summary>
        sealed class MouseDowns : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0201: case 0x0204: case 0x0207:   // WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN
                    case 0x00A1: case 0x00A4: case 0x00A7:   // the same on a window's frame
                        _downAt = Clock.ElapsedMilliseconds;
                        _downWhere = Control.MousePosition;
                        break;
                }
                return false;
            }
        }

        static string Name(AbstractViewPartController part) => part.GetType().Name.Replace("ViewPart", "");

        static void Trace(string message)
        {
            if (_traced >= MaxTraced) return;
            _traced++;
            try { DiagnosticLog.WriteAlways("Panes", message + (_traced == MaxTraced ? " (no more of these this session)" : "")); } catch { }
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
