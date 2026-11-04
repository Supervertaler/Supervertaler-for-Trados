using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Runs <see cref="UnsegmentedTextDetector"/> on the files of the document
    /// open in the editor, in the background, whenever the active document
    /// changes - and keeps the results so the panel, get_active_project and
    /// check_unsegmented_text can all answer without reading the file again.
    ///
    /// <para><b>Never delays opening a document.</b> The event handler only reads
    /// file names and starts a task; the check itself runs on the thread pool. A
    /// newer document change cancels a check still running for an older one.</para>
    ///
    /// <para><b>State, and why each piece is needed:</b> the results per file
    /// (so switching between open documents does not re-read a 50 MB file each
    /// time; pruned to the files still open in the editor at every document
    /// change, so it cannot outgrow what Studio itself holds), the active
    /// document's file list (read by bridge threads, which must not touch
    /// Studio's API), and the running check's cancellation source. A result is
    /// reused only while the file's size and write time are unchanged, so a
    /// save or a re-prepare is always checked afresh.</para>
    ///
    /// <para>A file that cannot be read to the end (locked, half written,
    /// malformed) gives no warning and no dialog, one log line, and is not
    /// remembered: the next document change tries it again.</para>
    /// </summary>
    internal static class UnsegmentedTextMonitor
    {
        private const string LogCategory = "Unsegmented";

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, UnsegmentedFileResult> _results =
            new Dictionary<string, UnsegmentedFileResult>(StringComparer.OrdinalIgnoreCase);
        private static string[] _activeFiles = new string[0];   // replaced whole, never mutated
        private static CancellationTokenSource _running;
        private static EditorController _editor;
        private static SynchronizationContext _ui;

        /// <summary>
        /// Raised on the UI thread when the active document's results may have
        /// changed: a different document became active, or a check finished.
        /// </summary>
        public static event Action Changed;

        /// <summary>
        /// Call on the UI thread with Studio's editor controller. Only the first call
        /// does anything: the controller lives as long as Studio, and so does this
        /// subscription. There is deliberately no Detach - the panel's view part can
        /// be disposed after its replacement has initialised, and detaching then
        /// would switch the check off for the rest of the session.
        /// </summary>
        public static void Attach(EditorController editor)
        {
            if (editor == null || _editor != null) return;
            _editor = editor;
            _ui = SynchronizationContext.Current;
            editor.ActiveDocumentChanged += OnActiveDocumentChanged;
            OnActiveDocumentChanged(editor, null);   // a document may be open already
        }

        /// <summary>
        /// The task that owns a source disposes it when it ends, which can be just
        /// before a newer document change cancels it: nothing is left to cancel then.
        /// </summary>
        private static void Cancel(CancellationTokenSource cts)
        {
            try { cts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private static void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            string[] files;
            HashSet<string> open;
            try
            {
                files = FilesOf(_editor?.ActiveDocument);
                open = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
                foreach (var doc in _editor?.GetDocuments() ?? Enumerable.Empty<IStudioDocument>())
                    foreach (var f in FilesOf(doc)) open.Add(f);
            }
            catch (Exception ex)
            {
                // Between documents Studio can throw from these; nothing to check.
                DiagnosticLog.Log(LogCategory, "Could not list the open files: " + ex.Message);
                files = new string[0];
                open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            lock (_gate)
            {
                _activeFiles = files;
                foreach (var gone in _results.Keys.Where(k => !open.Contains(k)).ToList())
                    _results.Remove(gone);
            }
            RaiseChanged();   // the previous document's warning goes at once
            CheckInBackground(files);
        }

        private static string[] FilesOf(IStudioDocument doc)
        {
            if (doc == null) return new string[0];
            var list = new List<string>();
            foreach (var f in doc.Files)
            {
                var p = f?.LocalFilePath;
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase))
                    list.Add(Path.GetFullPath(p));
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void CheckInBackground(string[] files)
        {
            if (files.Length == 0)
            {
                Cancel(Interlocked.Exchange(ref _running, null));
                return;
            }
            var mine = new CancellationTokenSource();
            Cancel(Interlocked.Exchange(ref _running, mine));

            var token = mine.Token;
            // No token passed to Task.Run itself: the body must always run, because
            // its finally is what disposes the source.
            Task.Run(() =>
            {
                try
                {
                    bool any = false;
                    foreach (var path in files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Refresh(path, token) != null) any = true;
                    }
                    if (any) RaiseChanged();
                }
                catch (OperationCanceledException) { /* a newer document took over */ }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteAlways(LogCategory, "Background check failed: " + ex.Message);
                }
                finally
                {
                    // Only the owner disposes; a replaced source was cancelled by
                    // its successor and is disposed here, by the task that used it.
                    Interlocked.CompareExchange(ref _running, null, mine);
                    mine.Dispose();
                }
            });
        }

        /// <summary>
        /// Checks <paramref name="path"/> unless the kept result is still current.
        /// Returns the new result when it ran a check (failures included), null
        /// when the kept one was reused or the file is gone.
        /// </summary>
        private static UnsegmentedFileResult Refresh(string path, CancellationToken token)
        {
            long length;
            DateTime written;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    lock (_gate) _results.Remove(path);
                    return null;
                }
                length = info.Length;
                written = info.LastWriteTimeUtc;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(LogCategory, $"Could not read {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }

            lock (_gate)
            {
                UnsegmentedFileResult kept;
                if (_results.TryGetValue(path, out kept) && kept.IsCurrent(length, written))
                    return null;
            }

            var started = DateTime.UtcNow;
            var result = UnsegmentedTextDetector.CheckFile(path, token);
            var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            lock (_gate)
            {
                if (result.Ok) _results[path] = result;
                else _results.Remove(path);
            }

            if (!result.Ok)
                DiagnosticLog.WriteAlways(LogCategory,
                    $"Could not check {Path.GetFileName(path)} for text outside segments ({result.Error}); no warning shown.");
            else
                DiagnosticLog.Log(LogCategory,
                    $"{Path.GetFileName(path)}: {result.Segments} segments, {result.HitCount} stretch(es) outside segments, " +
                    $"{result.ParagraphCount} paragraph(s) without segments, {result.Length / 1024} KB in {ms} ms.");
            return result;
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler == null) return;
            var ui = _ui;
            if (ui != null)
            {
                if (SynchronizationContext.Current == ui) SafeRaise(handler);
                else ui.Post(_ => SafeRaise(handler), null);
            }
            else if (UiThread.InvokeRequired)
                UiThread.Invoke(() => SafeRaise(handler));
            else
                SafeRaise(handler);
        }

        private static void SafeRaise(Action handler)
        {
            try { handler(); }
            catch (Exception ex) { DiagnosticLog.WriteAlways(LogCategory, "Warning display failed: " + ex.Message); }
        }

        /// <summary>
        /// What is known now about the active document, without reading anything:
        /// the kept result of each of its files. <paramref name="pending"/> counts
        /// files with no result yet (check still running, or the file unreadable).
        /// </summary>
        public static List<UnsegmentedFileResult> Current(out int pending)
        {
            var list = new List<UnsegmentedFileResult>();
            pending = 0;
            lock (_gate)
            {
                foreach (var path in _activeFiles)
                {
                    UnsegmentedFileResult r;
                    if (_results.TryGetValue(path, out r)) list.Add(r);
                    else pending++;
                }
            }
            return list;
        }

        /// <summary>
        /// Re-checks every file of the active document that changed since it was
        /// last checked, on the calling thread (never the UI thread: a large file
        /// takes a second or so), and returns all of their results, failures
        /// included. For check_unsegmented_text.
        /// </summary>
        public static List<UnsegmentedFileResult> CheckActiveDocumentNow(out bool attached)
        {
            string[] files;
            lock (_gate) files = _activeFiles;
            attached = _editor != null;

            var list = new List<UnsegmentedFileResult>();
            bool any = false;
            foreach (var path in files)
            {
                var fresh = Refresh(path, CancellationToken.None);
                if (fresh != null)
                {
                    any = true;
                    list.Add(fresh);
                    continue;
                }
                UnsegmentedFileResult kept;
                lock (_gate) _results.TryGetValue(path, out kept);
                list.Add(kept ?? new UnsegmentedFileResult
                {
                    FilePath = path, Ok = false, Error = "the file could not be found or read"
                });
            }
            if (any) RaiseChanged();
            return list;
        }

        /// <summary>
        /// Lines for get_active_project's warnings: up to <paramref name="maxListed"/>
        /// hits named one by one, then one line for the rest. Empty when there is
        /// nothing to say (including while the check is still running).
        /// </summary>
        public static List<string> BridgeWarnings(int maxListed = 5)
        {
            int pending;
            var results = Current(out pending);
            var hits = results.SelectMany(r => r.Hits).ToList();
            int total = results.Sum(r => r.HitCount);
            var lines = new List<string>();
            if (total == 0) return lines;

            foreach (var h in hits.Take(maxListed))
                lines.Add(DescribeForAi(h));
            if (total > maxListed)
                lines.Add($"...and {total - maxListed} more stretch(es) of source text outside any segment. " +
                          "Call check_unsegmented_text for the full list.");
            return lines;
        }

        /// <summary>One hit, as the AI is told it.</summary>
        public static string DescribeForAi(UnsegmentedHit h)
        {
            var what = h.ParagraphNotSegmented
                ? "A paragraph was never segmented"
                : "Source text sits outside any segment";
            return $"{what} in {DisplayName(h.FilePath)}, {h.Location}: \"{Shorten(h.Text, 120)}\". " +
                   "It is not in the editor, QA and the word count skip it, and it will export untranslated. " +
                   "Tell the user; it has to be fixed outside the editor (in the source file, or in the exported file).";
        }

        /// <summary>The file's name as Studio shows it: without ".sdlxliff".</summary>
        public static string DisplayName(string path)
        {
            var name = Path.GetFileName(path ?? "");
            return name.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - ".sdlxliff".Length)
                : name;
        }

        public static string Shorten(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";
        }

        /// <summary>
        /// Puts the editor on the hit's nearest segment (the one before it, else
        /// the one after). Returns null on success, otherwise why not. UI thread.
        /// </summary>
        public static string GoTo(UnsegmentedHit hit)
        {
            var doc = _editor?.ActiveDocument;
            if (doc == null) return "No document is open in the editor.";
            if (hit?.JumpSegmentId == null) return "This file has no segments to go to.";
            try
            {
                doc.SetActiveSegmentPair(hit.JumpParagraphUnitId, hit.JumpSegmentId, true);
                return null;
            }
            catch (Exception ex)
            {
                return "Could not go to segment " + hit.JumpSegmentId + ": " + ex.Message;
            }
        }
    }
}
