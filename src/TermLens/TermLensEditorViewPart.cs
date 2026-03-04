using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TermLens.Controls;
using TermLens.Models;
using TermLens.Settings;

namespace TermLens
{
    /// <summary>
    /// Trados Studio editor ViewPart that docks the TermLens panel below the editor.
    /// Listens to segment changes and updates the terminology display accordingly.
    /// </summary>
    [ViewPart(
        Id = "TermLensEditorViewPart",
        Name = "TermLens",
        Description = "Inline terminology display \u2014 shows source text with translations underneath matched terms",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Top, Pinned = true)]
    public class TermLensEditorViewPart : AbstractViewPartController
    {
        private static readonly Lazy<TermLensControl> _control =
            new Lazy<TermLensControl>(() => new TermLensControl());

        // Single instance — Trados creates exactly one ViewPart of each type.
        // Used by AddTermAction to trigger a reload after inserting a term.
        private static TermLensEditorViewPart _currentInstance;

        private EditorController _editorController;
        private IStudioDocument _activeDocument;
        private TermLensSettings _settings;

        // --- Alt+digit chord state machine ---
        private static int? _pendingDigit;
        private static Timer _chordTimer;

        protected override IUIControl GetContentControl()
        {
            return _control.Value;
        }

        protected override void Initialize()
        {
            _currentInstance = this;

            // Load persisted settings
            _settings = TermLensSettings.Load();

            _editorController = SdlTradosStudio.Application.GetController<EditorController>();

            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;

                // If a document is already open, wire up to it immediately
                if (_editorController.ActiveDocument != null)
                {
                    _activeDocument = _editorController.ActiveDocument;
                    _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                }
            }

            // Wire up term insertion — when user clicks a translation in the panel
            _control.Value.TermInsertRequested += OnTermInsertRequested;

            // Wire up the gear/settings button
            _control.Value.SettingsRequested += OnSettingsRequested;

            // Load termbase: prefer saved setting, fall back to auto-detect
            LoadTermbase();

            // Display the current segment immediately (even without a termbase, show all words)
            UpdateFromActiveSegment();
        }

        private void LoadTermbase(bool forceReload = false)
        {
            var disabled = _settings.DisabledTermbaseIds != null && _settings.DisabledTermbaseIds.Count > 0
                ? new HashSet<long>(_settings.DisabledTermbaseIds)
                : null;

            // Push project glossary IDs to the control for pink/blue coloring
            _control.Value.SetProjectTermbaseIds(_settings.ProjectTermbaseIds);

            // 1. Use the saved termbase path if set and the file exists
            if (!string.IsNullOrEmpty(_settings.TermbasePath) && File.Exists(_settings.TermbasePath))
            {
                _control.Value.LoadTermbase(_settings.TermbasePath, disabled, forceReload);
                return;
            }

            // 2. Fallback: auto-detect Supervertaler's default locations
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Supervertaler_Data", "resources", "supervertaler.db"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler", "resources", "supervertaler.db"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _control.Value.LoadTermbase(path, disabled, forceReload);
                    return;
                }
            }
        }

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            SafeInvoke(() =>
            {
                using (var form = new TermLensSettingsForm(_settings))
                {
                    // Find a parent window handle for proper dialog parenting
                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? form.ShowDialog(parent)
                        : form.ShowDialog();

                    if (result == System.Windows.Forms.DialogResult.OK)
                    {
                        // Settings already saved inside the form's OK handler.
                        // Force reload — the user may have toggled glossaries.
                        LoadTermbase(forceReload: true);
                        UpdateFromActiveSegment();
                    }
                }
            });
        }

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            if (_activeDocument != null)
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;

            _activeDocument = _editorController?.ActiveDocument;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;
                UpdateFromActiveSegment();
            }
            else
            {
                SafeInvoke(() => _control.Value.Clear());
            }
        }

        private void OnActiveSegmentChanged(object sender, EventArgs e)
        {
            UpdateFromActiveSegment();
        }

        private void UpdateFromActiveSegment()
        {
            if (_activeDocument?.ActiveSegmentPair == null)
            {
                SafeInvoke(() => _control.Value.Clear());
                return;
            }

            try
            {
                var sourceSegment = _activeDocument.ActiveSegmentPair.Source;
                var sourceText = sourceSegment?.ToString() ?? "";
                SafeInvoke(() => _control.Value.UpdateSegment(sourceText));
            }
            catch (Exception)
            {
                // Silently handle — segment may not be available during transitions
            }
        }

        private void SafeInvoke(Action action)
        {
            var ctrl = _control.Value;
            if (ctrl.InvokeRequired)
                ctrl.BeginInvoke(action);
            else
                action();
        }

        private void OnTermInsertRequested(object sender, TermInsertEventArgs e)
        {
            if (_activeDocument == null || string.IsNullOrEmpty(e.TargetTerm))
                return;

            try
            {
                _activeDocument.Selection.Target.Replace(e.TargetTerm, "TermLens");
            }
            catch (Exception)
            {
                // Silently handle — editor may not allow insertion at this moment
            }
        }

        /// <summary>
        /// Called by AddTermAction after a term is inserted.
        /// Reloads settings and the term index so the new term appears immediately.
        /// </summary>
        public static void NotifyTermAdded()
        {
            var instance = _currentInstance;
            if (instance == null) return;

            // Re-read settings in case WriteTermbaseId or disabled list changed
            instance._settings = TermLensSettings.Load();
            instance.LoadTermbase(forceReload: true);
            instance.UpdateFromActiveSegment();
        }

        // ─── Alt+digit term insertion ────────────────────────────────

        /// <summary>
        /// Called by TermInsertDigitNAction when Alt+digit is pressed.
        /// Implements a two-digit chord state machine with 400ms timeout.
        /// </summary>
        public static void HandleDigitPress(int digit)
        {
            var instance = _currentInstance;
            if (instance == null) return;

            // If there's already a pending first digit, combine into a two-digit number
            if (_pendingDigit.HasValue)
            {
                StopChordTimer();
                int number = _pendingDigit.Value * 10 + digit;
                _pendingDigit = null;
                instance.InsertTermByIndex(number);
                return;
            }

            // Check how many matched terms are in the current segment
            int matchCount = _control.Value.MatchCount;

            if (matchCount <= 9)
            {
                // ≤9 terms: insert immediately, no chord wait needed
                int number = digit == 0 ? 10 : digit;
                instance.InsertTermByIndex(number);
            }
            else
            {
                // 10+ terms: start chord timer, wait for possible second digit
                _pendingDigit = digit;
                StartChordTimer();
            }
        }

        private static void StartChordTimer()
        {
            StopChordTimer();
            _chordTimer = new Timer { Interval = 400 };
            _chordTimer.Tick += OnChordTimerTick;
            _chordTimer.Start();
        }

        private static void StopChordTimer()
        {
            if (_chordTimer != null)
            {
                _chordTimer.Stop();
                _chordTimer.Tick -= OnChordTimerTick;
                _chordTimer.Dispose();
                _chordTimer = null;
            }
        }

        private static void OnChordTimerTick(object sender, EventArgs e)
        {
            StopChordTimer();

            var instance = _currentInstance;
            if (instance == null || !_pendingDigit.HasValue) return;

            int digit = _pendingDigit.Value;
            _pendingDigit = null;

            // Single digit: 0 means term 10, otherwise 1-9
            int number = digit == 0 ? 10 : digit;
            instance.InsertTermByIndex(number);
        }

        private void InsertTermByIndex(int oneBasedIndex)
        {
            if (_activeDocument == null) return;

            var entry = _control.Value.GetTermByIndex(oneBasedIndex);
            if (entry == null) return;

            try
            {
                _activeDocument.Selection.Target.Replace(entry.TargetTerm, "TermLens");
            }
            catch (Exception)
            {
                // Silently handle — editor may not allow insertion at this moment
            }
        }

        // ─── Term Picker dialog ─────────────────────────────────────

        /// <summary>
        /// Called by TermPickerAction (Ctrl+Shift+G).
        /// Opens a dialog showing all matched terms for the current segment.
        /// </summary>
        public static void HandleTermPicker()
        {
            var instance = _currentInstance;
            if (instance == null || instance._activeDocument == null) return;

            var matches = _control.Value.GetCurrentMatches();
            if (matches.Count == 0) return;

            instance.SafeInvoke(() =>
            {
                using (var dlg = new TermPickerDialog(matches))
                {
                    var parent = _control.Value.FindForm();
                    var result = parent != null
                        ? dlg.ShowDialog(parent)
                        : dlg.ShowDialog();

                    if (result == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedTargetTerm))
                    {
                        try
                        {
                            instance._activeDocument.Selection.Target.Replace(
                                dlg.SelectedTargetTerm, "TermLens");
                        }
                        catch (Exception)
                        {
                            // Silently handle
                        }
                    }
                }
            });
        }

        // ─────────────────────────────────────────────────────────────

        public override void Dispose()
        {
            if (_currentInstance == this)
                _currentInstance = null;

            StopChordTimer();

            if (_activeDocument != null)
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;

            if (_editorController != null)
                _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged;

            base.Dispose();
        }
    }
}
