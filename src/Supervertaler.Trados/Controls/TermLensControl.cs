using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Main TermLens panel control. Renders the source segment as a flowing
    /// word-by-word display with terminology translations underneath matched terms.
    /// Port of Supervertaler's TermLensWidget.
    /// </summary>
    public class TermLensControl : UserControl, IUIControl
    {
        private readonly FlowLayoutPanel _flowPanel;
        private readonly Label _statusLabel;
        private readonly Panel _headerPanel;
        private readonly Label _headerLabel;

        private TermMatcher _matcher;
        private TermbaseReader _reader;
        private string _currentDbPath;
        private long _projectTermbaseId = -1;
        private string _multiTermStatusSuffix = "";

        /// <summary>
        /// Number of matched terms in the current segment display.
        /// Used by the Alt+digit state machine to decide between immediate and chord modes.
        /// </summary>
        public int MatchCount { get; private set; }

        /// <summary>
        /// Fired when the user clicks a translation to insert it into the target segment.
        /// </summary>
        public event EventHandler<TermInsertEventArgs> TermInsertRequested;

        /// <summary>
        /// Fired when the user right-clicks a term and selects "Edit Term...".
        /// </summary>
        public event EventHandler<TermEditEventArgs> TermEditRequested;

        /// <summary>
        /// Fired when the user right-clicks a term and selects "Delete Term".
        /// </summary>
        public event EventHandler<TermEditEventArgs> TermDeleteRequested;

        /// <summary>
        /// Fired when the user right-clicks a term and toggles non-translatable status.
        /// </summary>
        public event EventHandler<TermEditEventArgs> TermNonTranslatableToggled;

        /// <summary>
        /// Fired when the user changes font size via the A+/A- buttons.
        /// The ViewPart should persist the new size and refresh the segment display.
        /// </summary>
        public event EventHandler FontSizeChanged;

        public TermLensControl()
        {
            SuspendLayout();

            BackColor = Color.White;

            // Header bar
            _headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(245, 245, 245),
                Padding = new Padding(6, 2, 56, 2)  // 56px right padding for gear/help buttons that float on top
            };

            _headerLabel = new Label
            {
                Text = "TermLens",
                Dock = DockStyle.Left,
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _headerPanel.Controls.Add(_headerLabel);

            // Font size increase button (A+)
            var btnFontUp = new Button
            {
                Text = "A+",
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnFontUp.FlatAppearance.BorderSize = 0;
            btnFontUp.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            btnFontUp.Click += OnFontIncrease;
            _headerPanel.Controls.Add(btnFontUp);

            // Font size decrease button (A−)
            var btnFontDown = new Button
            {
                Text = "A\u2212", // A followed by minus sign (−)
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 7f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnFontDown.FlatAppearance.BorderSize = 0;
            btnFontDown.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            btnFontDown.Click += OnFontDecrease;
            _headerPanel.Controls.Add(btnFontDown);

            // Status label (right of header, left of font buttons)
            _statusLabel = new Label
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(120, 120, 120),
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 4, 0)
            };
            _headerPanel.Controls.Add(_statusLabel);

            // Main flow panel for term blocks
            _flowPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                Padding = new Padding(4),
                BackColor = Color.White,
                FlowDirection = FlowDirection.LeftToRight
            };

            Controls.Add(_flowPanel);
            Controls.Add(_headerPanel);

            _matcher = new TermMatcher();

            ResumeLayout(false);
        }

        /// <summary>
        /// Sets the project termbase ID (shown in pink).
        /// Call this whenever settings change. Pass -1 for no project termbase.
        /// </summary>
        public void SetProjectTermbaseId(long id)
        {
            _projectTermbaseId = id;
        }

        /// <summary>
        /// Loads a Supervertaler termbase database.
        /// </summary>
        /// <param name="dbPath">Path to the .db file.</param>
        /// <param name="disabledTermbaseIds">Termbase IDs to exclude (null = load all).</param>
        /// <param name="forceReload">Force reload even if the path hasn't changed.</param>
        public bool LoadTermbase(string dbPath, HashSet<long> disabledTermbaseIds = null,
            bool forceReload = false, bool globalCaseSensitive = false, string projectSourceLang = null)
        {
            if (!forceReload && dbPath == _currentDbPath && _reader != null)
                return true;

            _reader?.Dispose();
            _reader = new TermbaseReader(dbPath);

            if (!_reader.Open())
            {
                _statusLabel.Text = "Failed to open termbase";
                return false;
            }

            // Build in-memory index for fast matching
            var index = _reader.LoadAllTerms(disabledTermbaseIds, globalCaseSensitive, projectSourceLang);
            _matcher.LoadIndex(index);

            var termbases = _reader.GetTermbases();
            int enabledCount = 0;
            int totalTerms = 0;
            foreach (var tb in termbases)
            {
                if (disabledTermbaseIds == null || !disabledTermbaseIds.Contains(tb.Id))
                {
                    enabledCount++;
                    totalTerms += tb.TermCount;
                }
            }
            _statusLabel.Text = enabledCount == termbases.Count
                ? $"{termbases.Count} termbases, {totalTerms} terms"
                : $"{enabledCount}/{termbases.Count} termbases, {totalTerms} terms";
            _currentDbPath = dbPath;

            return true;
        }

        /// <summary>
        /// Adds a single term entry to the in-memory index without reloading the database.
        /// Call this after InsertTerm() for an incremental update.
        /// </summary>
        public void AddTermToIndex(TermEntry entry)
        {
            _matcher.AddEntry(entry);
        }

        /// <summary>
        /// Removes a term from the in-memory index by its ID.
        /// Call this after DeleteTerm() for an incremental update.
        /// </summary>
        public void RemoveTermFromIndex(long termId)
        {
            _matcher.RemoveEntry(termId);
        }

        /// <summary>
        /// Returns all loaded termbase term entries from the in-memory index.
        /// Used for termbase injection into AI translation prompts.
        /// </summary>
        public List<TermEntry> GetAllLoadedTerms()
        {
            return _matcher.GetAllEntries();
        }

        /// <summary>
        /// Merges MultiTerm terms into the existing TermMatcher index.
        /// Call after LoadTermbase() to add MultiTerm entries alongside Supervertaler terms.
        /// </summary>
        public void MergeMultiTermEntries(Dictionary<string, List<TermEntry>> multiTermIndex,
            List<MultiTermTermbaseInfo> infos)
        {
            _matcher.MergeIndex(multiTermIndex);

            // Build and store MultiTerm status suffix so it persists across UpdateSegment() calls
            if (infos != null && infos.Count > 0)
            {
                int mtCount = 0;
                int mtTerms = 0;
                foreach (var info in infos)
                {
                    if (info.LoadMode != MultiTermLoadMode.Failed)
                    {
                        mtCount++;
                        mtTerms += info.TermCount;
                    }
                }
                if (mtCount > 0)
                {
                    _multiTermStatusSuffix = $" + {mtCount} MultiTerm, {mtTerms:N0} terms";
                }
            }
        }

        /// <summary>
        /// Removes all MultiTerm entries from the TermMatcher index.
        /// Called before reloading MultiTerm termbases (e.g. on project switch).
        /// </summary>
        public void ClearMultiTermEntries()
        {
            _matcher.RemoveMultiTermEntries();
            _multiTermStatusSuffix = "";
        }

        /// <summary>
        /// Updates the display with a new source segment.
        /// Call this when the active segment changes in Trados Studio.
        /// </summary>
        public void UpdateSegment(string sourceText)
        {
            _flowPanel.SuspendLayout();
            _flowPanel.Controls.Clear();

            if (string.IsNullOrWhiteSpace(sourceText))
            {
                _statusLabel.Text = "";
                _flowPanel.ResumeLayout(true);
                return;
            }

            var tokens = _matcher.Tokenize(sourceText);

            int matchCount = 0;
            int wordCount = 0;
            int shortcutIndex = 0;

            foreach (var token in tokens)
            {
                if (token.IsLineBreak)
                {
                    // Force a line break in the flow layout
                    _flowPanel.SetFlowBreak(
                        _flowPanel.Controls.Count > 0
                            ? _flowPanel.Controls[_flowPanel.Controls.Count - 1]
                            : null,
                        true);
                    continue;
                }

                wordCount++;

                if (token.HasMatch)
                {
                    // A term is "project" if any of its entries come from the project termbase
                    bool isProject = false;
                    if (_projectTermbaseId >= 0)
                    {
                        foreach (var m in token.Matches)
                        {
                            if (m.TermbaseId == _projectTermbaseId)
                            {
                                isProject = true;
                                break;
                            }
                        }
                    }

                    // Check if any entry is non-translatable
                    bool isNonTranslatable = false;
                    foreach (var m in token.Matches)
                    {
                        if (m.IsNonTranslatable)
                        {
                            isNonTranslatable = true;
                            break;
                        }
                    }

                    // Check if all entries are from MultiTerm (no Supervertaler entries)
                    bool isMultiTerm = true;
                    foreach (var m in token.Matches)
                    {
                        if (!m.IsMultiTerm)
                        {
                            isMultiTerm = false;
                            break;
                        }
                    }

                    // Sort entries: non-MultiTerm first (Supervertaler takes priority),
                    // then project termbase entries, then by ranking ASC
                    var sortedEntries = token.Matches;
                    if (sortedEntries.Count > 1)
                    {
                        sortedEntries = new List<TermEntry>(sortedEntries);
                        sortedEntries.Sort((a, b) =>
                        {
                            // Non-MultiTerm entries first
                            if (a.IsMultiTerm != b.IsMultiTerm) return a.IsMultiTerm ? 1 : -1;
                            // Project termbase entries first
                            if (_projectTermbaseId >= 0)
                            {
                                bool aProj = a.TermbaseId == _projectTermbaseId;
                                bool bProj = b.TermbaseId == _projectTermbaseId;
                                if (aProj != bProj) return aProj ? -1 : 1;
                            }
                            return a.Ranking.CompareTo(b.Ranking);
                        });
                    }

                    var block = new TermBlock(token.Text, sortedEntries, shortcutIndex, isProject, isNonTranslatable, isMultiTerm, token.AbbreviationMatchIds)
                    {
                        Font = Font,
                        Margin = new Padding(2, 1, 2, 1)
                    };

                    block.TermInsertRequested += (s, args) => TermInsertRequested?.Invoke(s, args);
                    block.TermEditRequested += (s, args) => TermEditRequested?.Invoke(s, args);
                    block.TermDeleteRequested += (s, args) => TermDeleteRequested?.Invoke(s, args);
                    block.TermNonTranslatableToggled += (s, args) => TermNonTranslatableToggled?.Invoke(s, args);
                    _flowPanel.Controls.Add(block);

                    matchCount++;
                    shortcutIndex++;
                }
                else
                {
                    var label = new WordLabel(token.Text)
                    {
                        Font = Font,
                    };
                    _flowPanel.Controls.Add(label);
                }
            }

            MatchCount = matchCount;

            _statusLabel.Text = (matchCount > 0
                ? $"\u2713 Found {matchCount} terms in {wordCount} words"
                : $"{wordCount} words, no matches") + _multiTermStatusSuffix;

            _flowPanel.ResumeLayout(true);
        }

        /// <summary>
        /// Returns the TermEntry and abbreviation-match flag for the given 1-based shortcut index.
        /// When matchedViaAbbreviation is true, the caller should insert TargetAbbreviation
        /// instead of TargetTerm.
        /// </summary>
        public (TermEntry entry, bool matchedViaAbbreviation) GetTermByIndex(int oneBasedIndex)
        {
            foreach (Control ctrl in _flowPanel.Controls)
            {
                var block = ctrl as TermBlock;
                if (block != null && (block.ShortcutIndex + 1) == oneBasedIndex)
                    return (block.PrimaryEntry, block.IsAbbreviationMatch);
            }
            return (null, false);
        }

        /// <summary>
        /// Returns all current matched term blocks for the term picker dialog.
        /// Each item contains: 1-based index, source text, all entries (with synonyms).
        /// Duplicate source terms (same term matched at multiple positions) are merged
        /// into a single entry so each term appears only once in the picker.
        /// </summary>
        public List<TermPickerMatch> GetCurrentMatches()
        {
            var results = new List<TermPickerMatch>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (Control ctrl in _flowPanel.Controls)
            {
                var block = ctrl as TermBlock;
                if (block != null && block.PrimaryEntry != null)
                {
                    var key = block.PrimaryEntry.SourceTerm ?? "";

                    if (seen.TryGetValue(key, out int existingIdx))
                    {
                        // Merge entries into the existing match (skip duplicate)
                        var existing = results[existingIdx];
                        foreach (var entry in block.Entries)
                        {
                            if (!existing.AllEntries.Contains(entry))
                                existing.AllEntries.Add(entry);
                        }
                        // Promote to project termbase if either occurrence is
                        if (block.IsProjectTermbase)
                            existing.IsProjectTermbase = true;
                    }
                    else
                    {
                        seen[key] = results.Count;
                        results.Add(new TermPickerMatch
                        {
                            Index = results.Count + 1, // renumber sequentially
                            SourceText = block.PrimaryEntry.SourceTerm,
                            PrimaryEntry = block.PrimaryEntry,
                            AllEntries = new List<TermEntry>(block.Entries),
                            IsProjectTermbase = block.IsProjectTermbase
                        });
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Clears the display.
        /// </summary>
        public void Clear()
        {
            _flowPanel.Controls.Clear();
            _statusLabel.Text = "";
            MatchCount = 0;
        }

        /// <summary>
        /// Sets the panel font size (in points). Call this on startup with the
        /// persisted value, or when the user changes it via Settings.
        /// </summary>
        public void SetFontSize(float sizeInPoints)
        {
            sizeInPoints = Math.Max(7f, Math.Min(16f, sizeInPoints));
            Font = new Font("Segoe UI", sizeInPoints, FontStyle.Regular);
        }

        private void OnFontIncrease(object sender, EventArgs e)
        {
            var newSize = Math.Min(Font.Size + 0.5f, 16f);
            Font = new Font(Font.FontFamily, newSize, Font.Style);
            FontSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnFontDecrease(object sender, EventArgs e)
        {
            var newSize = Math.Max(Font.Size - 0.5f, 7f);
            Font = new Font(Font.FontFamily, newSize, Font.Style);
            FontSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─── License gating ────────────────────────────────────────

        private Panel _licenseOverlay;

        /// <summary>
        /// Shows a license-required overlay that covers the TermLens panel.
        /// </summary>
        public void ShowLicenseRequired()
        {
            if (_licenseOverlay != null) return;

            _licenseOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            var lbl = new Label
            {
                Text = "Your trial has expired.\nEnter a license key in Settings \u2192 License\nto continue using Supervertaler for Trados.",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(100, 100, 100)
            };

            _licenseOverlay.Controls.Add(lbl);
            Controls.Add(_licenseOverlay);
            _licenseOverlay.BringToFront();
        }

        /// <summary>
        /// Hides the license-required overlay after activation.
        /// </summary>
        public void HideLicenseRequired()
        {
            if (_licenseOverlay == null) return;
            Controls.Remove(_licenseOverlay);
            _licenseOverlay.Dispose();
            _licenseOverlay = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
