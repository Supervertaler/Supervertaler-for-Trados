using System;
using System.Linq;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Termview.Controls;

namespace Termview
{
    /// <summary>
    /// Trados Studio editor ViewPart that docks the Termview panel below the editor.
    /// Listens to segment changes and updates the terminology display accordingly.
    /// </summary>
    [ViewPart(
        Id = "TermviewEditorViewPart",
        Name = "Termview",
        Description = "Inline terminology display — shows source text with translations underneath matched terms",
        Icon = "TermviewIcon"
    )]
    [ViewPartLayout(typeof(EditorController), Dock = DockType.Bottom)]
    public class TermviewEditorViewPart : AbstractViewPartController
    {
        private static readonly Lazy<TermviewControl> _control =
            new Lazy<TermviewControl>(() => new TermviewControl());

        private EditorController _editorController;
        private IStudioDocument _activeDocument;

        protected override IUIControl GetContentControl()
        {
            return _control.Value;
        }

        protected override void Initialize()
        {
            _editorController = SdlTradosStudio.Application.GetController<EditorController>();

            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged += OnActiveDocumentChanged;
            }

            // Wire up term insertion — when user clicks a translation in the panel
            _control.Value.TermInsertRequested += OnTermInsertRequested;

            // Try to load the default Supervertaler termbase
            TryLoadDefaultTermbase();
        }

        private void OnActiveDocumentChanged(object sender, DocumentEventArgs e)
        {
            // Unsubscribe from previous document
            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;
            }

            _activeDocument = _editorController?.ActiveDocument;

            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged += OnActiveSegmentChanged;

                // Update immediately with current segment
                UpdateFromActiveSegment();
            }
            else
            {
                _control.Value.Clear();
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
                _control.Value.Clear();
                return;
            }

            try
            {
                // Get the source segment text
                var sourceSegment = _activeDocument.ActiveSegmentPair.Source;
                var sourceText = sourceSegment?.ToString() ?? "";

                _control.Value.UpdateSegment(sourceText);
            }
            catch (Exception)
            {
                // Silently handle — segment may not be available during transitions
            }
        }

        private void OnTermInsertRequested(object sender, TermInsertEventArgs e)
        {
            if (_activeDocument == null || string.IsNullOrEmpty(e.TargetTerm))
                return;

            try
            {
                // Insert the translation at the current cursor position in the target segment
                _activeDocument.Selection.Target.Replace(e.TargetTerm, "Termview");
            }
            catch (Exception)
            {
                // Silently handle — editor may be in a state that doesn't allow insertion
            }
        }

        private void TryLoadDefaultTermbase()
        {
            // Look for the Supervertaler database in standard locations
            var candidates = new[]
            {
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Supervertaler_Data", "resources", "supervertaler.db"),
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Supervertaler", "resources", "supervertaler.db"),
            };

            foreach (var path in candidates)
            {
                if (System.IO.File.Exists(path))
                {
                    _control.Value.LoadTermbase(path);
                    return;
                }
            }
        }

        public override void Dispose()
        {
            if (_activeDocument != null)
            {
                _activeDocument.ActiveSegmentChanged -= OnActiveSegmentChanged;
            }

            if (_editorController != null)
            {
                _editorController.ActiveDocumentChanged -= OnActiveDocumentChanged;
            }

            base.Dispose();
        }
    }
}
