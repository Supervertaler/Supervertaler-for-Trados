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
    /// <para><b>What this does</b>: while the switch is on, it notes once a second which watched Supervertaler panes
    /// (TermLens, EverTM Matches, TermPicker) are in view. After each segment change it looks a few times over 1.5 s:
    /// a pane that was in view just before, isn't now, with Translation Results now in view, gets its tab selected
    /// again (<see cref="AbstractViewPartController.Show"/>: the same <c>Show()</c> + selected tab Studio used, without
    /// activating the pane, so the keyboard stays in the editor). At most three times per segment, so it can never
    /// fight Studio in a loop. It doesn't wait to be told a pane was covered: Studio's docking didn't tell (first
    /// version, Michael's test, 22 Nov 2026).</para>
    ///
    /// <para><b>What it leaves alone</b>: a pane the user clicked away from at its tabs, and a pane not in view lately
    /// (the user picked Translation Results before moving on); Translation Results in another dock group (then nothing
    /// is covered). Whether Translation Results is in
    /// view is read by reflection (Studio's docking view is not public); if that fails, it does nothing.</para>
    ///
    /// <para><b>State</b>: the watched panes, when each was last in view, two UI timers and a counter, all on the UI thread.
    /// The once-a-second note reads one property per pane: nothing measurable.</para>
    /// </summary>
    internal static class TranslationResultsGuard
    {
        const string TranslationResultsId = "EditorView.TranslationMemoryViewPart";
        const int MaxPerSegment = 3;

        static readonly List<(AbstractViewPartController Part, Control Content)> Watched = new List<(AbstractViewPartController, Control)>();
        /// <summary>When each watched pane was last seen in view (Clock ms); missing: not lately.</summary>
        static readonly Dictionary<AbstractViewPartController, long> InViewAt = new Dictionary<AbstractViewPartController, long>();
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static int _restoresThisSegment, _traced;
        static bool _wired;
        static IStudioDocument _doc;
        /// <summary>Notes once a second which watched panes are in view, while the switch is on: a pane covered by
        /// Translation Results is brought back only if it was in view just before. Studio's docking does not reliably
        /// tell a pane it was covered (Michael's test, 22 Nov 2026: nothing happened), so this is read, not heard.</summary>
        static Timer _notice;
        /// <summary>The looks after a segment change: Studio brings Translation Results forward when its lookup starts,
        /// which may be before or a little after plugins hear of the segment change.</summary>
        static Timer _after;
        static readonly int[] AfterMs = { 0, 150, 600, 1500 };
        static int _afterStep;
        const long RecentMs = 2500;       // "in view just before"
        const int MaxTraced = 12;         // the first decisions of each session go to the diagnostic log, then it is quiet

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
            content.VisibleChanged += (s, e) =>
            {
                // A tab the user clicks away from: not "in view just before" any more.
                if (!content.Visible && ClickOnItsTabs(content)) InViewAt.Remove(part);
            };
            content.Disposed += (s, e) => { Watched.RemoveAll(w => ReferenceEquals(w.Part, part)); InViewAt.Remove(part); };
            WireEditor();
        }

        static void WireEditor()
        {
            if (_wired) return;
            var editor = StudioEditor.Controller;
            if (editor == null) return;
            _wired = true;
            _notice = new Timer { Interval = 1000 };
            _notice.Tick += (s, e) => { if (On) Note(); };
            _notice.Start();
            _after = new Timer();
            _after.Tick += (s, e) => AfterTick();
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
            if (!On) return;
            _restoresThisSegment = 0;
            _afterStep = 0;
            _after.Stop();
            AfterTick();
        }

        static void AfterTick()
        {
            _after.Stop();
            try { Check(); } catch { }
            _afterStep++;
            if (_afterStep < AfterMs.Length)
            {
                _after.Interval = Math.Max(1, AfterMs[_afterStep] - AfterMs[_afterStep - 1]);
                _after.Start();
            }
        }

        /// <summary>Notes the panes in view now.</summary>
        static void Note()
        {
            long now = Clock.ElapsedMilliseconds;
            foreach (var w in Watched.ToArray())
                try { if (!w.Content.IsDisposed && w.Part.IsInView) InViewAt[w.Part] = now; } catch { }
        }

        /// <summary>
        /// Brings back each watched pane that was in view just before and isn't now, when Translation Results is the
        /// one in view and the user didn't click at the pane's tabs. At most <see cref="MaxPerSegment"/> per segment.
        /// </summary>
        static void Check()
        {
            if (!On) return;
            long now = Clock.ElapsedMilliseconds;
            bool? tr = null;
            foreach (var w in Watched.ToArray())
            {
                var part = w.Part;
                if (w.Content.IsDisposed) continue;
                if (part.IsInView) { InViewAt[part] = now; continue; }
                if (!InViewAt.TryGetValue(part, out var at) || now - at > RecentMs) continue;   // not in view lately: leave it
                if (tr == null) tr = TranslationResultsInView(part) ?? false;
                if (tr != true) { Trace(Name(part) + " not in view, but Translation Results isn't either (or can't be read): left alone"); continue; }
                if (ClickOnItsTabs(w.Content)) { InViewAt.Remove(part); Trace(Name(part) + ": left alone (a mouse click at the pane's tabs)"); continue; }
                if (_restoresThisSegment >= MaxPerSegment) { Trace(Name(part) + ": left alone (already brought back " + MaxPerSegment + " times on this segment)"); continue; }
                _restoresThisSegment++;
                part.Show();
                Trace(Name(part) + ": covered by Translation Results after a segment change; brought back (in view now: " + part.IsInView + ")");
                if (part.IsInView) InViewAt[part] = now;
            }
        }

        /// <summary>
        /// A mouse button down near the pane's own tabs (the pane's area and a strip above and below it, where a docked
        /// group draws its tabs): the user is choosing a tab, Translation Results perhaps. A click anywhere else, such
        /// as on a segment in the editor, is not a choice of pane.
        /// </summary>
        static bool ClickOnItsTabs(Control content)
        {
            if (Control.MouseButtons == MouseButtons.None) return false;
            var area = content.RectangleToScreen(content.ClientRectangle);
            int strip = (int)(50 * content.DeviceDpi / 96.0);
            area.Inflate(0, strip);
            return area.Contains(Control.MousePosition);
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
