using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Supervertaler.Trados.Controls;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The pop-up that tells the user that the document they opened has source
    /// text outside every segment. It is shown to everyone, including those who
    /// never show the TermLens panel and so never see its yellow line.
    ///
    /// <para><b>When it shows</b> (<see cref="ShouldShow"/>, which
    /// tools\unsegmented_text_test.ps1 checks): the background check of the
    /// document open in the editor has finished, that document is still the one
    /// open, and it has at least one stretch between or beside segments.
    /// Paragraphs with no segment at all, and files that could not be read,
    /// never count. It shows once per document per Studio session, and again
    /// only when the document's stretches change. Switching back to it, saving
    /// it, or the check running again with the same results shows nothing.</para>
    ///
    /// <para><b>How it shows:</b> a small window owned by Studio's main window,
    /// so it stays above Studio. It does not take the focus, so typing in the
    /// editor carries on. While another dialog is open (Studio's main window is
    /// disabled), it waits until that dialog closes. A pop-up still on screen
    /// when the user moves to another document is closed. If they had not
    /// touched it, it counts as not seen and comes back with its document.</para>
    ///
    /// <para><b>State</b>, all in memory and all on the UI thread: what each
    /// document was last told (only documents that had stretches get an entry,
    /// so a session holds a handful; nothing is saved), the pop-up on screen,
    /// and the timer that waits out another dialog. The timer is made once and
    /// lives as long as Studio, like the monitor.</para>
    /// </summary>
    internal static class UnsegmentedTextAlert
    {
        private const string LogCategory = "Unsegmented";

        private static readonly Dictionary<string, string> _told =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static UnsegmentedTextAlertForm _open;
        private static Timer _waitForDialog;

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        // ─── the decision ───────────────────────────────────────────

        /// <summary>A document, by its files, in any order and any case.</summary>
        internal static string DocumentKey(IEnumerable<string> files) =>
            string.Join("|", (files ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => f.ToUpperInvariant())
                .Distinct()
                .OrderBy(f => f, StringComparer.Ordinal));

        /// <summary>
        /// What the user would be told about these results: each stretch by
        /// place and text, not the file's size or save time, so saving a file
        /// without fixing it is no change. Empty when there is no stretch.
        /// Paragraphs with no segment and unreadable files add nothing. At most
        /// 200 stretches are kept per file, so the key stays a few KB.
        /// </summary>
        internal static string ResultsKey(IEnumerable<UnsegmentedFileResult> results)
        {
            var sb = new StringBuilder();
            var withHits = (results ?? Enumerable.Empty<UnsegmentedFileResult>())
                .Where(r => r != null && r.Ok && r.HitCount > 0)
                .OrderBy(r => (r.FilePath ?? "").ToUpperInvariant(), StringComparer.Ordinal);
            foreach (var r in withHits)
            {
                sb.Append((r.FilePath ?? "").ToUpperInvariant()).Append('#').Append(r.HitCount);
                foreach (var h in r.Hits)
                    sb.Append('|').Append(h.ParagraphUnitId)
                      .Append(':').Append(h.SegmentBefore)
                      .Append(':').Append(h.SegmentAfter)
                      .Append(':').Append(StringComparer.Ordinal.GetHashCode(h.Text ?? ""));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Whether to show the pop-up now for the document made of
        /// <paramref name="checkedFiles"/>. <paramref name="openFiles"/> are the
        /// files of the document open in the editor at this moment (null when
        /// Studio could not say). Says yes once per document and set of
        /// stretches, and remembers that it did. UI thread, or a test.
        /// </summary>
        internal static bool ShouldShow(string[] checkedFiles, string[] openFiles,
            IEnumerable<UnsegmentedFileResult> results, out string documentKey, out string resultsKey)
        {
            documentKey = "";
            resultsKey = "";
            if (checkedFiles == null || checkedFiles.Length == 0 || openFiles == null) return false;

            // Closed, or another document came forward, while the check ran.
            var mine = new HashSet<string>(checkedFiles, StringComparer.OrdinalIgnoreCase);
            if (!mine.SetEquals(openFiles)) return false;

            resultsKey = ResultsKey((results ?? Enumerable.Empty<UnsegmentedFileResult>())
                .Where(r => r != null && mine.Contains(r.FilePath ?? "")));
            if (resultsKey.Length == 0) return false;

            documentKey = DocumentKey(mine);
            string before;
            if (_told.TryGetValue(documentKey, out before) && before == resultsKey) return false;
            _told[documentKey] = resultsKey;
            return true;
        }

        /// <summary>
        /// A pop-up closed before the user did anything with it was not seen: its
        /// document may show it again. Does nothing once newer results were told.
        /// </summary>
        internal static void Unsee(string documentKey, string resultsKey)
        {
            string before;
            if (documentKey != null && _told.TryGetValue(documentKey, out before) && before == resultsKey)
                _told.Remove(documentKey);
        }

        // ─── showing it ─────────────────────────────────────────────

        /// <summary>
        /// The background check of <paramref name="checkedFiles"/> has finished,
        /// whether or not it had to read anything. UI thread. Never throws.
        /// </summary>
        public static void CheckFinished(string[] checkedFiles)
        {
            try
            {
                if (!UnsegmentedTextMonitor.IsAttached || checkedFiles == null || checkedFiles.Length == 0) return;
                if (AnotherDialogIsOpen())
                {
                    WaitForDialog();
                    return;
                }

                var results = UnsegmentedTextMonitor.ResultsFor(checkedFiles);
                string documentKey, resultsKey;
                if (!ShouldShow(checkedFiles, UnsegmentedTextMonitor.ActiveDocumentFilesNow(), results,
                        out documentKey, out resultsKey))
                    return;
                Show(documentKey, resultsKey, results.Where(r => r.Ok && r.HitCount > 0).ToList());
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(LogCategory, "Could not show the pop-up: " + ex.Message);
            }
        }

        /// <summary>
        /// The document open in the editor changed. A pop-up about another
        /// document is closed. UI thread. Never throws.
        /// </summary>
        public static void DocumentChanged(string[] files)
        {
            try
            {
                var form = _open;
                if (form == null || form.IsDisposed) return;
                if (string.Equals(form.DocumentKey, DocumentKey(files), StringComparison.OrdinalIgnoreCase)) return;
                Close(form);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(LogCategory, "Could not close the pop-up: " + ex.Message);
            }
        }

        private static void Show(string documentKey, string resultsKey, List<UnsegmentedFileResult> withHits)
        {
            Close(_open);   // one at a time; a newer one for the same document replaces it

            var form = new UnsegmentedTextAlertForm(documentKey, resultsKey, withHits);
            try
            {
                form.FormClosed += (s, e) => { if (_open == form) _open = null; };
                form.ShowOver(StudioMainWindow());
                _open = form;
                DiagnosticLog.Log(LogCategory,
                    $"Pop-up shown: {withHits.Sum(r => r.HitCount)} stretch(es) in {withHits.Count} file(s).");
            }
            catch
            {
                Unsee(documentKey, resultsKey);   // not shown, so not seen
                form.Dispose();
                throw;
            }
        }

        private static void Close(UnsegmentedTextAlertForm form)
        {
            if (form == null || form.IsDisposed) return;
            if (!form.Seen) Unsee(form.DocumentKey, form.ResultsKey);
            if (_open == form) _open = null;
            form.Close();
        }

        /// <summary>
        /// True while a modal dialog is open in Studio, ours or Studio's: a
        /// modal disables the window that owns it.
        /// </summary>
        private static bool AnotherDialogIsOpen()
        {
            var main = StudioMainWindow();
            return main != IntPtr.Zero && !IsWindowEnabled(main);
        }

        /// <summary>
        /// Studio's main window, or zero. The handle is taken at start-up; one
        /// that is no longer a window would read as disabled for ever, and would
        /// own nothing.
        /// </summary>
        private static IntPtr StudioMainWindow()
        {
            var main = ForegroundWindow.HostMainWindow();
            return main != IntPtr.Zero && IsWindow(main) ? main : IntPtr.Zero;
        }

        /// <summary>
        /// Looks again once a second until the dialog has closed, then decides
        /// afresh for whichever document is open by then.
        /// </summary>
        private static void WaitForDialog()
        {
            if (_waitForDialog == null)
            {
                _waitForDialog = new Timer { Interval = 1000 };
                _waitForDialog.Tick += (s, e) =>
                {
                    if (AnotherDialogIsOpen()) return;
                    _waitForDialog.Stop();
                    CheckFinished(UnsegmentedTextMonitor.ActiveFiles);
                };
            }
            if (_waitForDialog.Enabled) return;
            _waitForDialog.Start();
            DiagnosticLog.Log(LogCategory, "Another dialog is open; the pop-up waits until it closes.");
        }
    }
}
