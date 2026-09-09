using System;
using System.Collections.Generic;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Controls;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// Dockable TermPicker pane – the list view of the current segment's
    /// terminology, for users who prefer a flat sortable list as their
    /// permanent terminology display rather than TermLens's in-context chips.
    ///
    /// TermLens and TermPicker are sibling representations of the same data, and
    /// each is now available in both placements:
    ///
    ///                     docked pane        popup at cursor
    ///   TermLens          TermLens panel     Ctrl tap
    ///   TermPicker        THIS pane          Alt+P
    ///
    /// Alt+P prefers this pane when it is visible – it moves the keyboard into
    /// the list rather than covering it with a modal popup (see TryFocusPane).
    /// With the pane closed, Alt+P opens the popup exactly as before.
    ///
    /// Pinned so that opening it from the View tab gives a permanently visible
    /// pane. Unpinned it arrives auto-hidden – it slides in and straight back
    /// out again, which reads as a glitch. Trados remembers wherever the user
    /// drags it afterwards (Michael's layout: Translation Results top-right,
    /// this pane below it).
    ///
    /// Refresh is driven by TermLensEditorViewPart (which already recomputes
    /// matches on every segment change) calling <see cref="RefreshIfOpen"/>, so
    /// the pane is always exactly in step with the TermLens panel instead of
    /// duplicating the editor event wiring.
    /// </summary>
    [ViewPart(
        Id = "TermPickerViewPart",
        Name = "TermPicker",
        Description = "Matched terms for the current segment as a list",
        Icon = "TermLensIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Right, Pinned = true)]
    public class TermPickerViewPart : AbstractViewPartController
    {
        private static TermPickerControl _control;
        private static TermPickerViewPart _instance;

        protected override IUIControl GetContentControl()
        {
            EnsureControl();
            return _control;
        }

        protected override void Initialize()
        {
            _instance = this;
            EnsureControl();
            EnsureTermLensLoaded();
            // Populate immediately: the pane is usually shown while a document
            // is already open, so waiting for the next segment change would
            // leave it blank and looking broken.
            RefreshIfOpen();
        }

        private static void EnsureControl()
        {
            if (_control != null && !_control.IsDisposed) return;

            _control = new TermPickerControl();
            _control.ShowHint = true;
            _control.EscapeReturnsFocusToEditor = true;

            // Insert through the same path the popup and the TermLens chips use,
            // so casing adaptation and the "TermLens" origin label are identical.
            _control.InsertRequested += (s, e) =>
                TermLensEditorViewPart.InsertTargetText(e.TargetTerm);

            try
            {
                var settings = SettingsService.Current;
                _control.ApplyColumnWidths(settings.TermPickerPaneColumnWidths);
            }
            catch { /* widths are cosmetic */ }
        }

        /// <summary>
        /// Forces the TermLens ViewPart to initialise. Trados only runs a
        /// ViewPart's Initialize() when its pane is first SHOWN, so a user who
        /// keeps TermPicker visible while TermLens is collapsed to a tab had a
        /// TermLens that was never tracking the document - and this pane, which
        /// sources its matches from it, stayed empty until they clicked the
        /// TermLens tab. GetController returns an uninitialised controller;
        /// EnsureInitialized is idempotent (the same trick AppInitializer uses
        /// to start the bridge without the Assistant pane being opened).
        /// </summary>
        private static void EnsureTermLensLoaded()
        {
            try
            {
                var vp = SdlTradosStudio.Application.GetController<TermLensEditorViewPart>();
                vp?.EnsureInitialized();
            }
            catch { /* the pane still fills on the next segment change */ }
        }

        /// <summary>
        /// Reloads the pane from the current segment's matches. Called by
        /// TermLensEditorViewPart whenever the segment display is rebuilt.
        /// No-op when the pane has never been opened, so users who don't use it
        /// pay nothing.
        /// </summary>
        public static void RefreshIfOpen()
        {
            var ctrl = _control;
            if (ctrl == null || ctrl.IsDisposed || !ctrl.IsHandleCreated) return;

            try
            {
                var matches = TermLensEditorViewPart.GetCurrentSegmentMatches()
                              ?? new List<TermPickerMatch>();
                if (ctrl.InvokeRequired)
                    ctrl.BeginInvoke((Action)(() => ctrl.LoadMatches(matches)));
                else
                    ctrl.LoadMatches(matches);
            }
            catch
            {
                // Document may be mid-transition – the next segment change retries
            }
        }

        /// <summary>
        /// Focuses the docked pane if it is open and visible, returning true.
        /// TermPickerAction calls this first: with the pane in view, Alt+P moves
        /// the keyboard into it (arrows / Right-Left / digits / Enter / E all
        /// work there) instead of covering it with a modal popup. When the pane
        /// isn't in the layout, this returns false and the popup opens as before.
        /// </summary>
        public static bool TryFocusPane()
        {
            var ctrl = _control;
            if (ctrl == null || ctrl.IsDisposed || !ctrl.IsHandleCreated || !ctrl.Visible)
                return false;
            try
            {
                _instance?.Activate();   // un-collapse / bring the pane forward
                ctrl.FocusList();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Escape support for the pane: when the pane owns the keyboard, hand
        /// focus back to the Studio editor (an auto-hidden pane retracts as a
        /// side effect). Called by the global Escape hook; returns false when
        /// the pane is not focused so Escape passes through to Studio.
        /// </summary>
        public static bool TryEscapeDismiss()
        {
            var ctrl = _control;
            if (ctrl == null || ctrl.IsDisposed || !ctrl.IsHandleCreated
                || !ctrl.Visible || !ctrl.ContainsFocus)
                return false;
            try
            {
                SdlTradosStudio.Application.GetController<EditorController>()?.Activate();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Persists the pane's column widths (called on shutdown).</summary>
        public static void SaveState()
        {
            var ctrl = _control;
            if (ctrl == null || ctrl.IsDisposed) return;
            try
            {
                SettingsService.Update(s => s.TermPickerPaneColumnWidths = ctrl.GetColumnWidths());
            }
            catch { }
        }
    }
}
