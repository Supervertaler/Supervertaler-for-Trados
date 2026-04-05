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
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Dockable ViewPart for Supervertaler SuperSearch.
    /// Provides cross-file search, find &amp; replace, and click-to-navigate
    /// across all SDLXLIFF files in a Trados project.
    /// </summary>
    [ViewPart(
        Id = "SuperSearchViewPart",
        Name = "Supervertaler SuperSearch",
        Description = "Cross-file search and replace for Trados projects",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Bottom, Pinned = false)]
    public class SuperSearchViewPart : AbstractViewPartController
    {
        private static readonly Lazy<SuperSearchControl> _control =
            new Lazy<SuperSearchControl>(() => new SuperSearchControl());

        private EditorController _editorController;
        private IStudioDocument _activeDocument;

        // Cached project SDLXLIFF file list (refreshed on document change)
        private List<string> _projectFiles;
        private string _lastProjectRoot;

        // Search cancellation
        private CancellationTokenSource _searchCts;

        // Last search results (for replace operations)
        private List<SearchResult> _lastResults;

        /// <summary>
        /// Provides access to the SuperSearch control for the context menu action.
        /// </summary>
        public static SuperSearchControl GetControl()
        {
            return _control.IsValueCreated ? _control.Value : null;
        }

        protected override IUIControl GetContentControl()
        {
            return _control.Value;
        }

        protected override void Initialize()
        {
            // Wire UI events
            _control.Value.SearchRequested += OnSearchRequested;
            _control.Value.StopRequested += OnStopRequested;
            _control.Value.NavigateRequested += OnNavigateRequested;
            _control.Value.ReplaceRequested += OnReplaceRequested;
            _control.Value.ReplaceAllRequested += OnReplaceAllRequested;
            _control.Value.HelpRequested += (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.SuperSearch);

            _editorController = SdlTradosStudio.Application.GetController<EditorController>();
            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    RefreshProjectFiles();
                }
            }
        }

        // ─── Document Events ─────────────────────────────────────

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            _activeDocument = _editorController?.ActiveDocument;
            RefreshProjectFiles();
        }

        private void RefreshProjectFiles()
        {
            if (_activeDocument == null) return;

            try
            {
                var activeFile = _activeDocument.ActiveFile;
                if (activeFile == null) return;

                var filePath = activeFile.LocalFilePath;
                if (string.IsNullOrEmpty(filePath)) return;

                // Find project root
                var dir = Path.GetDirectoryName(filePath);
                string projectRoot = null;
                while (!string.IsNullOrEmpty(dir))
                {
                    try
                    {
                        if (Directory.GetFiles(dir, "*.sdlproj", SearchOption.TopDirectoryOnly).Length > 0)
                        {
                            projectRoot = dir;
                            break;
                        }
                    }
                    catch { }

                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir) break;
                    dir = parent;
                }

                // Only re-scan if project root changed
                if (projectRoot != null && projectRoot != _lastProjectRoot)
                {
                    _lastProjectRoot = projectRoot;
                    _projectFiles = XliffSearcher.FindProjectXliffFiles(filePath);
                    SafeInvoke(() =>
                    {
                        _control.Value.SetProjectFiles(_projectFiles);
                        _control.Value.SetStatus(
                            $"Project: {Path.GetFileName(projectRoot)} \u2014 {_projectFiles.Count} file(s)");
                    });
                }
            }
            catch
            {
                _projectFiles = null;
            }
        }

        // ─── Search ──────────────────────────────────────────────

        private async void OnSearchRequested(object sender, SearchRequestEventArgs e)
        {
            // Cancel any in-progress search
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            if (_projectFiles == null || _projectFiles.Count == 0)
            {
                RefreshProjectFiles();
                if (_projectFiles == null || _projectFiles.Count == 0)
                {
                    SafeInvoke(() =>
                    {
                        _control.Value.SetStatus("No project files found. Open a file in the editor first.");
                        _control.Value.SetSearching(false);
                    });
                    return;
                }
            }

            // Use the file selection from the control (respects user's file filter)
            var files = _control.Value.GetSelectedFiles();
            if (files.Count == 0)
            {
                SafeInvoke(() =>
                {
                    _control.Value.SetStatus("No files selected. Click 'Files' to select files to search.");
                    _control.Value.SetSearching(false);
                });
                return;
            }

            SafeInvoke(() =>
            {
                _control.Value.SetSearching(true);
                _control.Value.SetStatus("Searching...");
            });

            var sw = Stopwatch.StartNew();

            try
            {
                var results = await Task.Run(() =>
                    XliffSearcher.Search(
                        files, e.Query, e.Scope,
                        e.CaseSensitive, e.UseRegex,
                        (done, total) => SafeInvoke(() =>
                            _control.Value.SetStatus($"Searching... ({done}/{total} files)")),
                        ct),
                    ct);

                sw.Stop();
                _lastResults = results;

                SafeInvoke(() =>
                {
                    _control.Value.SetResults(results);
                    _control.Value.SetStatus(
                        $"{results.Count} result(s) in {files.Count} file(s) \u2014 {sw.ElapsedMilliseconds} ms");
                    _control.Value.SetSearching(false);
                });
            }
            catch (OperationCanceledException)
            {
                SafeInvoke(() =>
                {
                    _control.Value.SetStatus("Search cancelled.");
                    _control.Value.SetSearching(false);
                });
            }
            catch (Exception ex)
            {
                SafeInvoke(() =>
                {
                    _control.Value.SetStatus($"Search error: {ex.Message}");
                    _control.Value.SetSearching(false);
                });
            }
        }

        private void OnStopRequested(object sender, EventArgs e)
        {
            _searchCts?.Cancel();
        }

        // ─── Navigate ────────────────────────────────────────────

        private void OnNavigateRequested(object sender, NavigateToSegmentEventArgs e)
        {
            // Must run on the UI thread — same pattern as AiAssistantViewPart.OnNavigateToSegment
            SafeInvoke(() =>
            {
                if (_activeDocument == null || _editorController == null)
                {
                    _control.Value.SetStatus("No active document.");
                    return;
                }

                var result = _control.Value.GetSelectedResult();
                if (result == null) return;

                var activeFilePath = GetActiveFilePath();
                var isSameFile = activeFilePath != null &&
                    string.Equals(activeFilePath, result.FilePath, StringComparison.OrdinalIgnoreCase);

                if (!isSameFile)
                {
                    _control.Value.SetStatus(
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

                    _control.Value.SetStatus(
                        $"Navigated to segment #{result.SegmentNumber} in {result.FileName}");
                }
                catch (Exception ex)
                {
                    _control.Value.SetStatus($"Navigation failed: {ex.Message}");
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
            if (_activeDocument == null) return;

            var result = e.SelectedResult;

            // The segment must be in the active file
            var activeFilePath = GetActiveFilePath();
            if (activeFilePath == null ||
                !string.Equals(activeFilePath, result.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                SafeInvoke(() => _control.Value.SetStatus(
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
                    SafeInvoke(() => _control.Value.SetStatus("Could not access the active segment."));
                    return;
                }

                var currentTarget = activePair.Target?.ToString() ?? "";
                var newTarget = PerformReplace(currentTarget, e.SearchText, e.ReplaceText,
                    e.CaseSensitive, e.UseRegex);

                if (newTarget == currentTarget)
                {
                    SafeInvoke(() => _control.Value.SetStatus("No match found in target text."));
                    return;
                }

                // Apply via ProcessSegmentPair delegate
                var capturedNewTarget = newTarget;
                _activeDocument.ProcessSegmentPair(activePair, "Supervertaler",
                    (sp, cancel) =>
                    {
                        var textTpl = FindFirstText(sp.Source);
                        if (textTpl != null)
                        {
                            sp.Target.Clear();
                            var textClone = (IText)textTpl.Clone();
                            textClone.Properties.Text = capturedNewTarget;
                            sp.Target.Add(textClone);
                        }
                    });

                result.TargetText = newTarget;
                SafeInvoke(() =>
                {
                    _control.Value.SetResults(_lastResults);
                    _control.Value.SetStatus("Replaced in 1 segment.");
                });
            }
            catch (Exception ex)
            {
                SafeInvoke(() => _control.Value.SetStatus($"Replace error: {ex.Message}"));
            }
        }

        private void OnReplaceAllRequested(object sender, ReplaceRequestEventArgs e)
        {
            if (_lastResults == null || _lastResults.Count == 0) return;

            // Count target matches
            var targetMatches = _lastResults.Where(r =>
                IsTargetMatch(r.TargetText, e.SearchText, e.CaseSensitive, e.UseRegex)).ToList();

            if (targetMatches.Count == 0)
            {
                SafeInvoke(() => _control.Value.SetStatus("No matches found in target text."));
                return;
            }

            // Group by file
            var fileGroups = targetMatches.GroupBy(r => r.FilePath).ToList();

            var activeFilePath = GetActiveFilePath();
            var hasNonActiveFiles = fileGroups.Any(g =>
                activeFilePath == null ||
                !string.Equals(g.Key, activeFilePath, StringComparison.OrdinalIgnoreCase));

            var msg = $"Replace {targetMatches.Count} occurrence(s) in {fileGroups.Count} file(s)?\n\n";

            if (hasNonActiveFiles)
            {
                msg += "WARNING: This will modify SDLXLIFF files directly on disk for files " +
                       "not currently open in the editor. These changes CANNOT be undone.\n\n" +
                       "Changes in the active file go through the Trados API and can be undone.\n\n" +
                       "Save your project before proceeding.";
            }
            else
            {
                msg += "All changes go through the Trados API and can be undone with Ctrl+Z.";
            }

            var dialogResult = MessageBox.Show(msg, "SuperSearch \u2014 Replace All",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dialogResult != DialogResult.OK) return;

            // Second confirmation for irreversible disk modifications
            if (hasNonActiveFiles)
            {
                var confirm = MessageBox.Show(
                    "Are you sure? Changes to files on disk cannot be undone.\n\n" +
                    "Make sure you have saved your project or have a backup.",
                    "SuperSearch \u2014 Final Confirmation",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation,
                    MessageBoxDefaultButton.Button2); // default to "No"
                if (confirm != DialogResult.Yes) return;
            }

            int replacedCount = 0;
            int errorCount = 0;

            foreach (var group in fileGroups)
            {
                var filePath = group.Key;
                var isActiveFile = activeFilePath != null &&
                    string.Equals(filePath, activeFilePath, StringComparison.OrdinalIgnoreCase);

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

                            var currentTarget = pair.Target?.ToString() ?? "";
                            var newTarget = PerformReplace(currentTarget, e.SearchText, e.ReplaceText,
                                e.CaseSensitive, e.UseRegex);

                            if (newTarget != currentTarget)
                            {
                                var capturedNewTarget = newTarget;
                                _activeDocument.ProcessSegmentPair(pair, "Supervertaler",
                                    (sp, cancel) =>
                                    {
                                        var textTpl = FindFirstText(sp.Source);
                                        if (textTpl != null)
                                        {
                                            sp.Target.Clear();
                                            var textClone = (IText)textTpl.Clone();
                                            textClone.Properties.Text = capturedNewTarget;
                                            sp.Target.Add(textClone);
                                        }
                                    });
                                result.TargetText = newTarget;
                                replacedCount++;
                            }
                        }
                        catch { errorCount++; }
                    }
                }
                else
                {
                    // Replace directly in the SDLXLIFF file on disk
                    try
                    {
                        int count = ReplaceInXliffFile(filePath, group.ToList(),
                            e.SearchText, e.ReplaceText, e.CaseSensitive, e.UseRegex);
                        replacedCount += count;
                    }
                    catch { errorCount += group.Count(); }
                }
            }

            SafeInvoke(() =>
            {
                _control.Value.SetResults(_lastResults);
                var statusMsg = $"Replaced {replacedCount} segment(s)";
                if (errorCount > 0) statusMsg += $" ({errorCount} error(s))";
                if (fileGroups.Any(g => !string.Equals(g.Key, activeFilePath, StringComparison.OrdinalIgnoreCase)))
                    statusMsg += ". Non-active files were modified on disk \u2014 reopen to see changes.";
                _control.Value.SetStatus(statusMsg);
            });
        }

        /// <summary>
        /// Replaces target text directly in an SDLXLIFF file on disk.
        /// Used for files not currently open in the editor.
        /// </summary>
        private int ReplaceInXliffFile(string filePath, List<SearchResult> results,
            string searchText, string replaceText, bool caseSensitive, bool useRegex)
        {
            var doc = new XmlDocument();
            doc.PreserveWhitespace = true;
            doc.Load(filePath);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            var root = doc.DocumentElement;
            var xliffNs = root?.NamespaceURI ?? "";
            if (!string.IsNullOrEmpty(xliffNs))
                nsMgr.AddNamespace("x", xliffNs);

            var prefix = string.IsNullOrEmpty(xliffNs) ? "" : "x:";
            int count = 0;

            foreach (var result in results)
            {
                var unit = doc.SelectSingleNode(
                    $"//{prefix}trans-unit[@id='{result.ParagraphUnitId}']", nsMgr);
                if (unit == null) continue;

                var targetNode = unit.SelectSingleNode($"{prefix}target", nsMgr);
                if (targetNode == null) continue;

                // Find the specific segment marker
                XmlNode segNode = null;
                if (!string.IsNullOrEmpty(result.SegmentId))
                {
                    segNode = targetNode.SelectSingleNode(
                        $".//{prefix}mrk[@mtype='seg'][@mid='{result.SegmentId}']", nsMgr);
                }

                var node = segNode ?? targetNode;
                var currentText = node.InnerText;
                var newText = PerformReplace(currentText, searchText, replaceText, caseSensitive, useRegex);

                if (newText != currentText)
                {
                    if (node.ChildNodes.Count == 1 && node.FirstChild is XmlText)
                    {
                        node.FirstChild.Value = newText;
                    }
                    else
                    {
                        ReplaceTextInNodes(node, searchText, replaceText, caseSensitive, useRegex);
                    }

                    result.TargetText = newText;
                    count++;
                }
            }

            if (count > 0)
                doc.Save(filePath);

            return count;
        }

        private void ReplaceTextInNodes(XmlNode parent, string searchText, string replaceText,
            bool caseSensitive, bool useRegex)
        {
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child is XmlText textNode)
                {
                    var newVal = PerformReplace(textNode.Value, searchText, replaceText, caseSensitive, useRegex);
                    if (newVal != textNode.Value)
                        textNode.Value = newVal;
                }
                else if (child.HasChildNodes)
                {
                    ReplaceTextInNodes(child, searchText, replaceText, caseSensitive, useRegex);
                }
            }
        }

        // ─── Text Helpers ────────────────────────────────────────

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

        private static string PerformReplace(string text, string search, string replace,
            bool caseSensitive, bool useRegex)
        {
            if (useRegex)
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { return Regex.Replace(text, search, replace, options); }
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

        private static bool IsTargetMatch(string targetText, string search, bool caseSensitive, bool useRegex)
        {
            if (string.IsNullOrEmpty(targetText)) return false;

            if (useRegex)
            {
                var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { return Regex.IsMatch(targetText, search, options); }
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
                if (_control.IsValueCreated && _control.Value.InvokeRequired)
                    _control.Value.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }
    }
}
