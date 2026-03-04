using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using TermLens.Core;
using TermLens.Models;

namespace TermLens.Controls
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
        private HashSet<long> _projectTermbaseIds = new HashSet<long>();

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
        /// Fired when the user clicks the gear/settings button in the header.
        /// </summary>
        public event EventHandler SettingsRequested;

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
                Padding = new Padding(6, 2, 2, 2)
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

            // Gear button (right side of header)
            var btnSettings = new Button
            {
                Text = "\u2699\uFE0E",  // gear character + text presentation selector
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Symbol", 11f),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                TabStop = false,
                UseCompatibleTextRendering = true,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            btnSettings.Click += (s, e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
            _headerPanel.Controls.Add(btnSettings);

            // Status label (right of header, left of gear button)
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
        /// Sets the project termbase IDs (glossaries shown in pink).
        /// Call this whenever settings change.
        /// </summary>
        public void SetProjectTermbaseIds(IEnumerable<long> ids)
        {
            _projectTermbaseIds = ids != null ? new HashSet<long>(ids) : new HashSet<long>();
        }

        /// <summary>
        /// Loads a Supervertaler termbase database.
        /// </summary>
        /// <param name="dbPath">Path to the .db file.</param>
        /// <param name="disabledTermbaseIds">Termbase IDs to exclude (null = load all).</param>
        /// <param name="forceReload">Force reload even if the path hasn't changed.</param>
        public bool LoadTermbase(string dbPath, HashSet<long> disabledTermbaseIds = null, bool forceReload = false)
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
            var index = _reader.LoadAllTerms(disabledTermbaseIds);
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
                    // A term is "project" if any of its entries come from a project glossary
                    bool isProject = false;
                    foreach (var m in token.Matches)
                    {
                        if (_projectTermbaseIds.Contains(m.TermbaseId))
                        {
                            isProject = true;
                            break;
                        }
                    }

                    var block = new TermBlock(token.Text, token.Matches, shortcutIndex, isProject)
                    {
                        Font = Font,
                        Margin = new Padding(2, 1, 2, 1)
                    };

                    block.TermInsertRequested += (s, args) => TermInsertRequested?.Invoke(s, args);
                    _flowPanel.Controls.Add(block);

                    matchCount++;
                    shortcutIndex++;
                }
                else
                {
                    var label = new WordLabel(token.Text)
                    {
                        Font = Font,
                        Margin = new Padding(2, 4, 2, 4)
                    };
                    _flowPanel.Controls.Add(label);
                }
            }

            MatchCount = matchCount;

            _statusLabel.Text = matchCount > 0
                ? $"\u2713 Found {matchCount} terms in {wordCount} words"
                : $"{wordCount} words, no matches";

            _flowPanel.ResumeLayout(true);
        }

        /// <summary>
        /// Returns the TermEntry for the given 1-based shortcut index, or null if not found.
        /// Used by Alt+digit insertion.
        /// </summary>
        public TermEntry GetTermByIndex(int oneBasedIndex)
        {
            foreach (Control ctrl in _flowPanel.Controls)
            {
                var block = ctrl as TermBlock;
                if (block != null && (block.ShortcutIndex + 1) == oneBasedIndex)
                    return block.PrimaryEntry;
            }
            return null;
        }

        /// <summary>
        /// Returns all current matched term blocks for the term picker dialog.
        /// Each item contains: 1-based index, source text, all entries (with synonyms).
        /// </summary>
        public List<TermPickerMatch> GetCurrentMatches()
        {
            var results = new List<TermPickerMatch>();
            foreach (Control ctrl in _flowPanel.Controls)
            {
                var block = ctrl as TermBlock;
                if (block != null && block.PrimaryEntry != null)
                {
                    results.Add(new TermPickerMatch
                    {
                        Index = block.ShortcutIndex + 1,
                        SourceText = block.PrimaryEntry.SourceTerm,
                        PrimaryEntry = block.PrimaryEntry,
                        AllEntries = new List<TermEntry>(block.Entries),
                        IsProjectGlossary = block.IsProjectGlossary
                    });
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
