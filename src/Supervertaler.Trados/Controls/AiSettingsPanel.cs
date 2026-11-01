using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Models;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// WinForms UserControl for AI provider configuration.
    /// Embedded in the Settings dialog as the "AI Settings" tab.
    ///
    /// Layout is built from nested <see cref="TableLayoutPanel"/>s with AutoSize
    /// rows/columns and <see cref="AutoScaleMode"/> = None, so every label, field
    /// and button sizes to its (UiScale-scaled) content and reflows automatically.
    /// This is DPI/scale-proof: nothing clips or overlaps at 100/125/150/175% or
    /// any custom Windows display scale. UiScale owns all scaling; WinForms' own
    /// autoscaling is disabled so it can't double-scale on top.
    /// </summary>
    public class AiSettingsPanel : UserControl
    {
        // Provider + Model
        private ComboBox _cmbProvider;
        private ComboBox _cmbModel;
        private Button _btnFetchModels;   // #106: asks the provider for its current list
        private CheckBox _chkShowAllModels; // #106: advanced mode - the whole fetched list
        // The fetched lists as of this dialog session - loaded from settings, updated
        // by the button, written back by ApplyToSettings. Held here so a fetch with
        // an unsaved key does not touch the saved settings until OK.
        private AiSettings _fetchedSource = new AiSettings();
        // Optional free-text model ID — overrides _cmbModel when filled. Lets
        // users pick a model that isn't in the curated dropdown (e.g. a new
        // release, a preview model, or an OpenRouter router like openrouter/free).
        private TextBox _txtCustomModelId;

        // API Key
        private TextBox _txtApiKey;
        private Label _lblKeyHint;   // #108: "this does not look like an OpenAI key"
        // Under each key box: why it is empty when the stored key cannot be read on
        // this computer (design note §7, M1). Under the provider box, otherwise, the
        // start-up notice about a damaged locked key file. See UpdateKeyNotes.
        private Label _lblKeyNote, _lblCustomKeyNote;
        private Label _keyNoteSpacer, _customKeyNoteSpacer;
        // Provider → M1, for the providers whose shared key cannot be read here.
        private readonly Dictionary<string, string> _unreadableKeyNotes = new Dictionary<string, string>();
        // The endpoint profiles' names as loaded: a name gone at OK was renamed or
        // removed, and its key leaves the locked file (SaveLockedProfileKeys).
        private List<string> _profileNamesLoaded = new List<string>();

        private Button _btnShowKey;
        private Button _btnTestConnection;
        private Label _lblStatus;

        // Machine-translation engines (SuperBench contenders; docs/design/mt-engines.md
        // §8): one key box each, in the shared key file under the engine's id - the
        // ids Sidekick uses - with M1 under a box whose stored key cannot be read here.
        private readonly Dictionary<string, TextBox> _mtKeyBoxes = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, Label> _mtKeyNotes = new Dictionary<string, Label>();
        private readonly Dictionary<string, Label> _mtKeyNoteSpacers = new Dictionary<string, Label>();
        private readonly Dictionary<string, string> _mtUnreadableNotes = new Dictionary<string, string>();
        private TextBox _txtMicrosoftRegion;
        private Label _lblMtStatus;

        // Provider-specific rows (toggled in OnProviderChanged). Hiding both
        // cells of a TableLayoutPanel row collapses it, so the rest reflows.
        private readonly List<Control> _ollamaRows = new List<Control>();
        private readonly List<Control> _customRows = new List<Control>();
        private readonly List<Control> _localServerRows = new List<Control>(); // Ollama or Custom

        // Ollama section
        private TextBox _txtOllamaEndpoint;
        private NumericUpDown _nudOllamaTimeout;

        private Label _lblOllamaTimeoutHint;

        // Custom OpenAI section
        private ComboBox _cmbCustomProfile;
        private Button _btnAddProfile;
        private Button _btnRemoveProfile;
        private Button _btnRenameProfile;
        private TextBox _txtCustomEndpoint;
        private TextBox _txtCustomModel;
        private TextBox _txtCustomApiKey;
        private Button _btnShowCustomKey;

        // AI Context section – shared (all AI features)
        private Label _lblAiContextHeader;
        private CheckBox _chkIncludeDocumentContext;
        private Label _lblMaxSegments;
        private NumericUpDown _nudMaxSegments;
        private Label _lblPromptContext;
        private NumericUpDown _nudPromptContext;
        private CheckBox _chkIncludeTermMetadata;
        private CheckBox _chkIncludeSuperMemory;
        private CheckBox _chkIncludeSuperMemoryAutoPrompt;
        private CheckBox _chkLogPrompts;
        private CheckBox _chkPersistUsageLog;
        private NumericUpDown _nudMonthlyBudget;
        private Label _lblBatchSize;
        private NumericUpDown _nudBatchSize;
        private CheckBox _chkTcAsComments;
        private CheckBox _chkProofreadCommentLabel;
        private CheckBox _chkKeepRepetitions;
        private Label _lblAiTermbases;

        // AI Context section – Chat & QuickLauncher only
        private Label _lblChatContextHeader;
        private Label _lblChatContextNote;
        private CheckBox _chkIncludeTmMatches;
        private Label _lblTmFuzzyMinScore;            // #116
        private NumericUpDown _nudTmFuzzyMinScore;    // #116
        private CheckBox _chkDemoMode;
        private Label _lblSurroundingSegments;
        private NumericUpDown _nudSurroundingSegments;

        private Label _lblInfo;

        private bool _keyVisible;
        private bool _customKeyVisible;

        // Track API keys per-provider so switching providers preserves each key
        private readonly Dictionary<string, string> _providerApiKeys = new Dictionary<string, string>();
        private string _lastProviderKey;

        public AiSettingsPanel()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            // UiScale owns all DPI/scale-based sizing; AutoScaleMode.None stops
            // WinForms from double-scaling on top of it. The whole panel is laid
            // out with TableLayoutPanels (AutoSize rows/columns), so labels,
            // fields and buttons size to their scaled content and never clip or
            // overlap at any Windows display scale. (Replaces the old absolute-
            // positioned, AutoScaleMode.Dpi layout that clipped at high DPI.)
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            AutoScroll = true;
            Font = new Font("Segoe UI", UiScale.FontSize(9f));
            Padding = new Padding(UiScale.Pixels(16), UiScale.Pixels(12), UiScale.Pixels(16), UiScale.Pixels(12));

            var labelColor = Color.FromArgb(80, 80, 80);
            var headerColor = Color.FromArgb(50, 50, 50);

            // ── small factories / layout helpers (local functions) ──────────
            Font HeaderFont() => new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold);
            Font HintFont() => new Font("Segoe UI", UiScale.FontSize(7.5f), FontStyle.Italic);

            TableLayoutPanel NewGrid()
            {
                var t = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                    Margin = Padding.Empty
                };
                t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                return t;
            }

            Label FieldLabel(string text, int indentSteps = 0) => new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = labelColor,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(UiScale.Pixels(indentSteps * 20), UiScale.Pixels(7), UiScale.Pixels(8), UiScale.Pixels(7))
            };

            Label Header(string text, bool first = false) => new Label
            {
                Text = text,
                AutoSize = true,
                Font = HeaderFont(),
                ForeColor = headerColor,
                Margin = new Padding(0, UiScale.Pixels(first ? 0 : 14), 0, UiScale.Pixels(6))
            };

            CheckBox Check(string text, int indentSteps = 0) => new CheckBox
            {
                Text = text,
                AutoSize = true,
                // AutoSize checkboxes can size a touch short at high DPI, letting
                // adjacent rows look like they touch; MinimumSize + a little extra
                // vertical margin guarantees a clean gap at any scale.
                MinimumSize = new Size(0, UiScale.Pixels(20)),
                ForeColor = labelColor,
                Margin = new Padding(UiScale.Pixels(indentSteps * 20), UiScale.Pixels(4), 0, UiScale.Pixels(5))
            };

            TextBox FillBox(bool password = false) => new TextBox
            {
                Dock = DockStyle.Fill,
                UseSystemPasswordChar = password,
                Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3))
            };

            ComboBox FillCombo() => new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3))
            };

            NumericUpDown SmallNud(int min, int max, int value, int increment) => new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                Increment = increment,
                Width = UiScale.Pixels(95), // fits 4-digit values + spinner at high DPI
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3))
            };

            Button TextButton(string text) // AutoSize: never clips at any scale/locale
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlatStyle = FlatStyle.System,
                    Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3)),
                    Padding = new Padding(UiScale.Pixels(8), UiScale.Pixels(2), UiScale.Pixels(8), UiScale.Pixels(2))
                };
                return b;
            }

            Button GlyphButton(string glyph, string tip, bool bold = false)
            {
                var b = new Button
                {
                    Text = glyph,
                    Width = UiScale.Pixels(36),
                    Height = UiScale.Pixels(32),
                    FlatStyle = FlatStyle.Flat,
                    // The pencil glyph (✎) renders wider/taller than + and −, so it
                    // gets a slightly smaller font to stay inside the button.
                    Font = new Font("Segoe UI", UiScale.FontSize(bold ? 11f : 10f), bold ? FontStyle.Bold : FontStyle.Regular),
                    ForeColor = Color.FromArgb(70, 70, 70),
                    Anchor = AnchorStyles.Left,
                    Padding = Padding.Empty,
                    Margin = new Padding(UiScale.Pixels(3), UiScale.Pixels(3), 0, UiScale.Pixels(3))
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
                new ToolTip().SetToolTip(b, tip);
                return b;
            }

            // A textbox that fills the row with a trailing AutoSize "Show/Hide" button.
            TableLayoutPanel KeyRow(TextBox box, Button showBtn)
            {
                var host = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    RowCount = 1,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty
                };
                host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                box.Margin = new Padding(0, UiScale.Pixels(3), UiScale.Pixels(6), UiScale.Pixels(3));
                host.Controls.Add(box, 0, 0);
                host.Controls.Add(showBtn, 1, 0);
                return host;
            }

            void Span(TableLayoutPanel t, ref int r, Control c)
            {
                t.Controls.Add(c, 0, r);
                t.SetColumnSpan(c, 2);
                r++;
            }

            void Pair(TableLayoutPanel t, ref int r, Control labelCtrl, Control field)
            {
                t.Controls.Add(labelCtrl, 0, r);
                t.Controls.Add(field, 1, r);
                r++;
            }

            void Row(TableLayoutPanel t, ref int r, string labelText, Control field, int indentSteps = 0)
            {
                Pair(t, ref r, FieldLabel(labelText, indentSteps), field);
            }

            // Empty placeholder for the label column when a control should align
            // under the field column with no label.
            Label NoLabel() => new Label { AutoSize = true, Margin = Padding.Empty };

            // A note under a key box. Unlike the one-line hint, it wraps: "can't be
            // read" needs the whole sentence, what happened and what to do. Hidden,
            // with its placeholder, until there is something to say (UpdateKeyNotes),
            // so an ordinary dialog keeps its layout.
            void KeyNote(TableLayoutPanel t, ref int r, out Label spacer, out Label note)
            {
                spacer = NoLabel();
                spacer.Visible = false;
                note = new Label
                {
                    AutoSize = true, Visible = false, Anchor = AnchorStyles.Left,
                    ForeColor = Color.FromArgb(180, 90, 0),
                    Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, UiScale.Pixels(6)),
                };
                Pair(t, ref r, spacer, note);
            }

            // ── root grid ───────────────────────────────────────────────────
            var root = NewGrid();
            root.Dock = DockStyle.Top;
            int row = 0;

            // ===== AI Provider =====
            Span(root, ref row, Header("AI Provider", first: true));

            _cmbProvider = FillCombo();
            foreach (var key in LlmModels.AllProviderKeys)
                _cmbProvider.Items.Add(new ProviderItem(key));
            _cmbProvider.SelectedIndexChanged += OnProviderChanged;
            Row(root, ref row, "Provider:", _cmbProvider);

            _cmbModel = FillCombo();
            // #106: the dropdown and a Models... button side by side. The button asks
            // the provider for its own list, so a model released after this build
            // can be picked here rather than typed into the Model ID box below.
            _btnFetchModels = TextButton("Fetch list");   // no ellipsis: it updates the dropdown in place, no window
            _btnFetchModels.Margin = new Padding(UiScale.Pixels(6), UiScale.Pixels(3), 0, UiScale.Pixels(3));
            var ttFetch = new ToolTip();
            ttFetch.SetToolTip(_btnFetchModels,
                "Ask the provider for its current model list, using the API key above " +
                "(it need not be saved yet). New models are added to the dropdown and " +
                "remembered.");
            _btnFetchModels.Click += OnFetchModelsClick;
            _chkShowAllModels = new CheckBox
            {
                Text = "Show all models",
                AutoSize = true,
                Margin = new Padding(UiScale.Pixels(8), UiScale.Pixels(6), 0, UiScale.Pixels(3)),
            };
            var ttAll = new ToolTip();
            ttAll.SetToolTip(_chkShowAllModels,
                "Off: the short list - the few models worth recommending, with a verdict each. " +
                "On: everything the provider's own list returned as well.");
            _chkShowAllModels.CheckedChanged += (s, e) =>
            {
                _fetchedSource.ShowAllModels = _chkShowAllModels.Checked;
                PopulateModels(GetSelectedProviderKey(), (_cmbModel.SelectedItem as ModelItem)?.Id);
            };
            var modelHost = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = new Padding(0)
            };
            modelHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            modelHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            modelHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            modelHost.Controls.Add(_cmbModel, 0, 0);
            modelHost.Controls.Add(_btnFetchModels, 1, 0);
            modelHost.Controls.Add(_chkShowAllModels, 2, 0);
            Row(root, ref row, "Model:", modelHost);

            var lnkViewModels = new LinkLabel
            {
                Text = "View all supported models...",
                AutoSize = true,
                LinkColor = Color.FromArgb(0, 102, 153),
                Margin = new Padding(0, UiScale.Pixels(2), 0, UiScale.Pixels(4))
            };
            lnkViewModels.LinkClicked += (s, e) =>
            {
                using (var dlg = new SupportedModelsDialog())
                    dlg.ShowDialog(this);
            };
            Pair(root, ref row, NoLabel(), lnkViewModels);

            _txtCustomModelId = FillBox();
            var ttCustomModel = new ToolTip();
            ttCustomModel.SetToolTip(_txtCustomModelId,
                "Enter an exact model ID to use a model that isn't in the dropdown " +
                "(e.g. a new release, a preview model, or an OpenRouter router such " +
                "as \"openrouter/free\"). Leave blank to use the model selected above.");
            Row(root, ref row, "Model ID:", _txtCustomModelId);

            var lblCustomModelHint = new Label
            {
                Text = "(optional – overrides the dropdown above)",
                AutoSize = true,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = HintFont(),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(4))
            };
            Pair(root, ref row, NoLabel(), lblCustomModelHint);

            _txtApiKey = FillBox(password: true);
            _btnShowKey = TextButton("Show");
            _btnShowKey.Click += OnShowKeyClick;
            Row(root, ref row, "API Key:", KeyRow(_txtApiKey, _btnShowKey));
            // #108: a key pasted into the wrong provider's box is caught here, in words,
            // before the provider refuses it with a 401 that names nothing.
            // One fixed line with an ellipsis: a status line that reflows a laid-out
            // dialog is worse than one that truncates (memoQ's finding).
            _lblKeyHint = new Label
            {
                AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = UiScale.Pixels(18),
                Visible = false, ForeColor = Color.FromArgb(180, 90, 0),
                Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, UiScale.Pixels(4)),
            };
            Pair(root, ref row, NoLabel(), _lblKeyHint);
            _txtApiKey.TextChanged += (s, e) => UpdateKeyHint();
            KeyNote(root, ref row, out _keyNoteSpacer, out _lblKeyNote);
            _txtApiKey.TextChanged += (s, e) => UpdateKeyNotes();
            // The note wraps at the width of the key's row, known only once laid out.
            _txtApiKey.Parent.SizeChanged += (s, e) => UpdateKeyNotes();

            _btnTestConnection = TextButton("Test Connection");
            _btnTestConnection.Click += OnTestConnectionClick;
            _lblStatus = new Label
            {
                Text = "",
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(UiScale.Pixels(8), UiScale.Pixels(8), 0, 0)
            };
            var testHost = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            testHost.Controls.Add(_btnTestConnection);
            testHost.Controls.Add(_lblStatus);
            Pair(root, ref row, NoLabel(), testHost);

            // ===== Ollama section (rows shown only when Ollama is selected) =====
            // Added directly to the root grid (not a nested AutoSize panel) so the
            // fields inherit the root's definite width — a nested panel left the
            // trailing button overflowing the right edge at high DPI. Hiding both
            // cells of a row collapses it (handled in OnProviderChanged).
            var lblOllamaEndpoint = FieldLabel("Endpoint:");
            _txtOllamaEndpoint = FillBox();
            _txtOllamaEndpoint.Text = "http://localhost:11434";
            Pair(root, ref row, lblOllamaEndpoint, _txtOllamaEndpoint);
            _ollamaRows.Add(lblOllamaEndpoint);
            _ollamaRows.Add(_txtOllamaEndpoint);

            // ===== Custom OpenAI section (rows shown only when Custom is selected) =====
            var sepCustom = new Label
            {
                Height = Math.Max(1, UiScale.Pixels(1)),
                BorderStyle = BorderStyle.Fixed3D,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, UiScale.Pixels(10), 0, UiScale.Pixels(6))
            };
            Span(root, ref row, sepCustom);
            _customRows.Add(sepCustom);

            var lblCustomHeader = Header("Custom OpenAI-Compatible Endpoint");
            Span(root, ref row, lblCustomHeader);
            _customRows.Add(lblCustomHeader);

            _cmbCustomProfile = FillCombo();
            _cmbCustomProfile.SelectedIndexChanged += OnCustomProfileChanged;
            _btnAddProfile = GlyphButton("+", "Add a new endpoint", bold: true);
            _btnAddProfile.Click += OnAddProfileClick;
            _btnRemoveProfile = GlyphButton("−", "Remove this endpoint", bold: true);
            _btnRemoveProfile.Click += OnRemoveProfileClick;
            _btnRenameProfile = GlyphButton("✎", "Rename this endpoint");
            _btnRenameProfile.Click += OnRenameProfileClick;
            var profileHost = new TableLayoutPanel
            {
                ColumnCount = 4,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            profileHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            profileHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            profileHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            profileHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            profileHost.Controls.Add(_cmbCustomProfile, 0, 0);
            profileHost.Controls.Add(_btnAddProfile, 1, 0);
            profileHost.Controls.Add(_btnRemoveProfile, 2, 0);
            profileHost.Controls.Add(_btnRenameProfile, 3, 0);
            var lblProfile = FieldLabel("Profile:");
            Pair(root, ref row, lblProfile, profileHost);
            _customRows.Add(lblProfile);
            _customRows.Add(profileHost);

            var lblCustomEndpoint = FieldLabel("Endpoint:");
            _txtCustomEndpoint = FillBox();
            Pair(root, ref row, lblCustomEndpoint, _txtCustomEndpoint);
            _customRows.Add(lblCustomEndpoint);
            _customRows.Add(_txtCustomEndpoint);

            var lblCustomModel = FieldLabel("Model:");
            _txtCustomModel = FillBox();
            Pair(root, ref row, lblCustomModel, _txtCustomModel);
            _customRows.Add(lblCustomModel);
            _customRows.Add(_txtCustomModel);

            var lblCustomApiKey = FieldLabel("API Key:");
            _txtCustomApiKey = FillBox(password: true);
            _btnShowCustomKey = TextButton("Show");
            _btnShowCustomKey.Click += OnShowCustomKeyClick;
            var customKeyHost = KeyRow(_txtCustomApiKey, _btnShowCustomKey);
            Pair(root, ref row, lblCustomApiKey, customKeyHost);
            _customRows.Add(lblCustomApiKey);
            _customRows.Add(customKeyHost);
            // Not one of _customRows: shown only when this profile's key cannot be read.
            KeyNote(root, ref row, out _customKeyNoteSpacer, out _lblCustomKeyNote);
            _txtCustomApiKey.TextChanged += (s, e) => UpdateKeyNotes();
            customKeyHost.SizeChanged += (s, e) => UpdateKeyNotes();

            // ===== Time limit for a server on this computer (Ollama and Custom) =====
            // Below both sections so it sits last in either. The setting keeps its
            // Ollama name: it is in users' settings.json under that name.
            var lblOllamaTimeout = FieldLabel("Timeout (min):");
            _nudOllamaTimeout = SmallNud(0, 120, 0, 1);
            _lblOllamaTimeoutHint = new Label
            {
                Text = "0 = auto (" + LlmClient.LocalServerDefaultTimeoutMinutes + " min)",
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = HintFont(),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(UiScale.Pixels(8), UiScale.Pixels(6), 0, 0)
            };
            var ollamaTimeoutHost = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty
            };
            ollamaTimeoutHost.Controls.Add(_nudOllamaTimeout);
            ollamaTimeoutHost.Controls.Add(_lblOllamaTimeoutHint);
            Pair(root, ref row, lblOllamaTimeout, ollamaTimeoutHost);
            _localServerRows.Add(lblOllamaTimeout);
            _localServerRows.Add(ollamaTimeoutHost);

            // Hide provider-specific rows until OnProviderChanged shows the right set.
            foreach (var c in _ollamaRows) c.Visible = false;
            foreach (var c in _customRows) c.Visible = false;
            foreach (var c in _localServerRows) c.Visible = false;

            // ===== Machine translation (SuperBench contenders) =====
            Span(root, ref row, Header("Machine translation (SuperBench)"));
            Span(root, ref row, new Label
            {
                Text = SuperBenchRunner.OfferedEngineNames("and") + " can be SuperBench contenders. "
                     + "The keys are shared with Sidekick: a key entered here is entered there too.",
                AutoSize = true, ForeColor = Color.FromArgb(110, 110, 110),
                MaximumSize = new Size(UiScale.Pixels(560), 0), Margin = new Padding(0, 0, 0, UiScale.Pixels(6)),
            });
            foreach (var engine in SuperBenchRunner.OfferedEngines)
            {
                var box = FillBox(password: true);
                var show = TextButton("Show");
                show.Click += (s, e) => box.UseSystemPasswordChar = !box.UseSystemPasswordChar;
                var test = TextButton("Test");
                test.Margin = new Padding(UiScale.Pixels(6), UiScale.Pixels(3), 0, UiScale.Pixels(3));
                var engineId = engine;
                test.Click += async (s, e) => await OnTestEngineClick(engineId, test);
                var host = KeyRow(box, show);
                host.ColumnCount = 3;
                host.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                host.Controls.Add(test, 2, 0);
                Row(root, ref row, Supervertaler.Core.MachineTranslation.DisplayName(engine) + " key:", host);
                KeyNote(root, ref row, out var spacer, out var note);
                _mtKeyBoxes[engine] = box;
                _mtKeyNotes[engine] = note;
                _mtKeyNoteSpacers[engine] = spacer;
                box.TextChanged += (s, e) => UpdateMtKeyNotes();
                host.SizeChanged += (s, e) => UpdateMtKeyNotes();
            }
            // Only while Microsoft is offered; null otherwise, and the saved region is left alone.
            if (SuperBenchRunner.OfferedEngines.Contains(Supervertaler.Core.MachineTranslation.Microsoft))
            {
                _txtMicrosoftRegion = new TextBox { Width = UiScale.Pixels(160), Anchor = AnchorStyles.Left, Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3)) };
                var regionHint = new Label
                {
                    Text = "empty for a Global resource, else e.g. westeurope",
                    AutoSize = true, ForeColor = Color.Gray, Font = HintFont(), Anchor = AnchorStyles.Left,
                    Margin = new Padding(UiScale.Pixels(8), UiScale.Pixels(6), 0, 0),
                };
                var regionHost = new FlowLayoutPanel
                {
                    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false, Margin = Padding.Empty,
                };
                regionHost.Controls.Add(_txtMicrosoftRegion);
                regionHost.Controls.Add(regionHint);
                Row(root, ref row, "Microsoft region:", regionHost);
            }
            _lblMtStatus = new Label
            {
                AutoSize = true, ForeColor = Color.FromArgb(100, 100, 100), Anchor = AnchorStyles.Left,
                MaximumSize = new Size(UiScale.Pixels(560), 0), Margin = new Padding(0, UiScale.Pixels(2), 0, UiScale.Pixels(4)),
            };
            Pair(root, ref row, NoLabel(), _lblMtStatus);

            // ===== AI context (Batch operations, Chat and QuickLauncher) =====
            _lblAiContextHeader = Header("Context layers (Batch operations, Chat and QuickLauncher)");
            Span(root, ref row, _lblAiContextHeader);

            _chkIncludeDocumentContext = Check("Include full document content in AI context");
            _chkIncludeDocumentContext.Checked = true;
            var docTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            docTip.SetToolTip(_chkIncludeDocumentContext,
                "Sends all source segments to the AI so it can determine the document type\r\n" +
                "(legal, medical, technical, etc.) and provide context-appropriate assistance.\r\n" +
                "Uses more tokens but greatly improves response quality.\r\n" +
                "Applies to Chat, QuickLauncher, and Batch Operations.");
            _chkIncludeDocumentContext.CheckedChanged += (s, ev) =>
            {
                _nudMaxSegments.Enabled = _chkIncludeDocumentContext.Checked;
                _lblMaxSegments.Enabled = _chkIncludeDocumentContext.Checked;
            };
            Span(root, ref row, _chkIncludeDocumentContext);

            _lblMaxSegments = FieldLabel("Max segments:", indentSteps: 1);
            _nudMaxSegments = SmallNud(100, 2000, 500, 100);
            var maxSegTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            maxSegTip.SetToolTip(_nudMaxSegments,
                "Maximum number of source segments to include in the AI prompt.\r\n" +
                "Documents larger than this will be truncated (first 80% + last 20%).");
            Pair(root, ref row, _lblMaxSegments, _nudMaxSegments);

            _chkIncludeTermMetadata = Check("Include term definitions and domains");
            _chkIncludeTermMetadata.Checked = true;
            var metaTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            metaTip.SetToolTip(_chkIncludeTermMetadata,
                "When enabled, term definitions, domains, and notes are included\r\n" +
                "alongside matched terminology in the AI prompt.\r\n" +
                "Applies to Chat, QuickLauncher, and Batch Operations.");
            Span(root, ref row, _chkIncludeTermMetadata);

            _chkIncludeSuperMemory = Check("Include SuperMemory knowledge base in AI context");
            _chkIncludeSuperMemory.Checked = true;
            var smTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            smTip.SetToolTip(_chkIncludeSuperMemory,
                "When enabled, relevant SuperMemory articles (client profiles, domain\r\n" +
                "knowledge, style guides, terminology decisions) are automatically\r\n" +
                "loaded and included in AI prompts for translations, Proofread and\r\n" +
                "chat. Proofread checks the translations against them.");
            _chkIncludeSuperMemory.CheckedChanged += (s, ev) =>
            {
                _chkIncludeSuperMemoryAutoPrompt.Enabled = _chkIncludeSuperMemory.Checked;
            };
            Span(root, ref row, _chkIncludeSuperMemory);

            _chkIncludeSuperMemoryAutoPrompt = Check("Use knowledge base when generating prompts (AutoPrompt)", indentSteps: 1);
            _chkIncludeSuperMemoryAutoPrompt.Checked = true;
            var apTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            apTip.SetToolTip(_chkIncludeSuperMemoryAutoPrompt,
                "When enabled, AutoPrompt includes your SuperMemory articles\r\n" +
                "(client conventions, terminology reasoning, style guides) in\r\n" +
                "the meta-prompt so that generated prompts reflect your\r\n" +
                "established knowledge base from the start.");
            Span(root, ref row, _chkIncludeSuperMemoryAutoPrompt);

            _chkLogPrompts = Check("Log prompts and responses (Reports tab and a daily log file)");
            _chkLogPrompts.Checked = false;
            var logTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            logTip.SetToolTip(_chkLogPrompts,
                "When enabled, every AI call is logged to the Reports tab with the\r\n" +
                "full prompt, response, token counts and cost - and appended, as one\r\n" +
                "JSON line per call, to a daily file under your\r\n" +
                "Supervertaler\\trados\\logs\\prompts folder, so you can see exactly what\r\n" +
                "was sent after the fact. Image bytes are never written; attachments\r\n" +
                "are recorded by type and size. Files older than 7 days are removed.");
            Span(root, ref row, _chkLogPrompts);

            _chkPersistUsageLog = Check("Keep a persistent token-usage log (for cost monitoring)");
            _chkPersistUsageLog.Checked = true;
            var usageTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300 };
            usageTip.SetToolTip(_chkPersistUsageLog,
                "When enabled, every AI call's token usage and cost is appended to a\r\n" +
                "monthly log file under your Supervertaler\\trados\\usage folder.\r\n" +
                "It records metadata only (model, tokens, cost, project, file) – never\r\n" +
                "the prompt or response text. Open it in Excel, or use the report below.");
            Span(root, ref row, _chkPersistUsageLog);

            var btnUsageReport = TextButton("Usage && Costs report…");
            btnUsageReport.Click += (s, ev) =>
            {
                try { using (var f = new UsageReportForm()) f.ShowDialog(FindForm()); }
                catch { /* report is best-effort */ }
            };
            Span(root, ref row, btnUsageReport);

            var lblMonthlyBudget = FieldLabel("Monthly budget (USD, 0 = none):");
            _nudMonthlyBudget = SmallNud(0, 100000, 0, 1);
            _nudMonthlyBudget.DecimalPlaces = 2;   // allow cents (e.g. 0.18, 25.50)
            _nudMonthlyBudget.Width = UiScale.Pixels(110);
            var budgetTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            budgetTip.SetToolTip(_nudMonthlyBudget,
                "A soft monthly spend limit. When this month's logged AI cost reaches\r\n" +
                "this amount, starting a batch translation shows a warning you can\r\n" +
                "dismiss. Advisory only – it never blocks. 0 disables the budget.");
            Pair(root, ref row, lblMonthlyBudget, _nudMonthlyBudget);

            // #116: the floor for the TM search that Batch Translate runs. The
            // on/off switch stays on the Batch Operations tab, where the
            // per-job judgement about a memory is made; this is the number
            // behind it and belongs with the other context knobs.
            _lblTmFuzzyMinScore = FieldLabel("Lowest TM match to send (%):");
            _nudTmFuzzyMinScore = SmallNud(50, 100, 70, 5);
            var fuzzyTip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300 };
            fuzzyTip.SetToolTip(_nudTmFuzzyMinScore,
                "Batch Translate and Clipboard Mode search the project's translation\r\n" +
                "memories and send each segment's closest match to the AI as a reference,\r\n" +
                "which it checks before using. This is the lowest match percentage worth\r\n" +
                "sending.\r\n\r\n" +
                "A match is always sent together with the source it was made for, so\r\n" +
                "the AI can see which words differ rather than assuming the wording\r\n" +
                "fits. That is what makes a fuzzy safe to send at all.\r\n\r\n" +
                "70% is where Studio's own fuzzy band starts. Below that a “match”\r\n" +
                "agrees with the segment largely by accident and competes with your\r\n" +
                "termbase for the AI's attention. Set it to 100 for exact matches only.\r\n\r\n" +
                "Turned on and off with “Send TM matches to the AI” on the Batch\r\n" +
                "Operations tab.");
            Pair(root, ref row, _lblTmFuzzyMinScore, _nudTmFuzzyMinScore);

            _lblBatchSize = FieldLabel("Batch size:");
            _nudBatchSize = SmallNud(5, 100, 20, 1);
            var batchTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            batchTip.SetToolTip(_nudBatchSize,
                "Number of segments sent to the AI provider per API call during\r\n" +
                "Batch Translate and Batch Proofread. Lower values are safer;\r\n" +
                "higher values reduce cost and improve cross-segment consistency.");
            Pair(root, ref row, _lblBatchSize, _nudBatchSize);

            _chkKeepRepetitions = Check("Keep repetitions consistent in Batch Translate");
            var repetitionsTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
            repetitionsTip.SetToolTip(_chkKeepRepetitions,
                "The AI translates every segment separately, so the same sentence can come back\r\n" +
                "worded differently each time it occurs.\r\n\r\n" +
                "Ticked (the default): every later occurrence of a sentence gets the translation of its\r\n" +
                "first, across all files in the run. A segment the run does not overwrite – a TM match,\r\n" +
                "a locked segment – keeps its translation, and that translation is the one its empty\r\n" +
                "repetitions get. If two such segments already disagree, nothing is changed and the\r\n" +
                "log lists them. Nothing sent to the AI changes.\r\n\r\n" +
                "A repetition is the same text exactly – same case, spaces (a non-breaking space is not\r\n" +
                "an ordinary one) and tags; only tag numbering may differ.");
            Span(root, ref row, _chkKeepRepetitions);

            _chkTcAsComments = Check("Add the AI's [[TC: …]] notes as Trados comments");
            var tcTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
            tcTip.SetToolTip(_chkTcAsComments,
                "When the AI needs to flag something in a segment – a defect in the source, a real\r\n" +
                "ambiguity, an error it corrected in a 100% TM match, a deliberate departure from the\r\n" +
                "termbase – it adds one short note at the end of that segment's translation, written\r\n" +
                "as your comment to the client:\r\n" +
                "[[TC: …]].\r\n\r\n" +
                "Unticked (the default): the note stays at the end of the target, where you see it while\r\n" +
                "you review. Remove it before you confirm the segment, or turn it into a comment yourself.\r\n\r\n" +
                "Ticked: the note is taken out of the target and added to the segment as a Trados\r\n" +
                "comment instead.\r\n\r\n" +
                "Applies to Batch Translate, Translate active segment (Alt+T) and Paste from Clipboard.");
            Span(root, ref row, _chkTcAsComments);

            _chkProofreadCommentLabel = Check("Start Proofread comments with “AI proofreader (model):”");
            var labelTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 300 };
            labelTip.SetToolTip(_chkProofreadCommentLabel,
                "With “Also add issues as Trados comments” ticked on Batch Operations, each issue the\r\n" +
                "proofreader finds is added to its segment as a Trados comment.\r\n\r\n" +
                "Ticked (the default): the comment starts with “AI proofreader” and the model that\r\n" +
                "found the issue, for example “AI proofreader (Claude Opus 5.5):”, so whoever gets the\r\n" +
                "file can tell it from a colleague’s comment.\r\n\r\n" +
                "Unticked: the comment holds only the issue and the suggestion.");
            Span(root, ref row, _chkProofreadCommentLabel);

            // Termbase AI inclusion is now chosen on the Termbases tab (the "AI"
            // column in the termbase grid), so this is just a pointer to that.
            _lblAiTermbases = new Label
            {
                Text = "Termbases included in AI prompts are chosen on the Termbases tab – " +
                       "tick the “AI” column for each termbase the AI should see.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                ForeColor = labelColor,
                Margin = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4))
            };
            Span(root, ref row, _lblAiTermbases);

            // ===== AI context (Chat and QuickLauncher only) =====
            _lblChatContextHeader = Header("Context layers (Chat and QuickLauncher)");
            Span(root, ref row, _lblChatContextHeader);

            _lblChatContextNote = new Label
            {
                Text = "Most of these apply to Chat and QuickLauncher only – see each tooltip for exceptions.",
                AutoSize = true,
                ForeColor = Color.FromArgb(130, 130, 130),
                Font = HintFont(),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(4))
            };
            Span(root, ref row, _lblChatContextNote);

            _chkIncludeTmMatches = Check("Include TM matches in AI context (Chat, QuickLauncher, AutoPrompt)");
            _chkIncludeTmMatches.Checked = true;
            var tmTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300 };
            tmTip.SetToolTip(_chkIncludeTmMatches,
                "When enabled, the AI gets translation reference pairs in two ways:\r\n" +
                "\r\n" +
                "  • Chat / QuickLauncher: live TM lookups for the active segment – fuzzy\r\n" +
                "    and exact matches from your project TMs are included as references.\r\n" +
                "\r\n" +
                "  • AutoPrompt (Batch Operations): up to 50 already-translated, human-\r\n" +
                "    confirmed segment pairs are sampled evenly from the active document\r\n" +
                "    and included as in-project translation examples. Includes 100% / exact\r\n" +
                "    matches that have been applied and confirmed, fuzzy-and-edited\r\n" +
                "    segments, and segments translated from scratch – anything with a\r\n" +
                "    Translated / ApprovedTranslation / ApprovedSignOff confirmation level.\r\n" +
                "\r\n" +
                "Batch Translate has its own TM setting – “Send TM matches to the AI”\r\n" +
                "on the Batch Operations tab – and is not affected by this checkbox.\r\n" +
                "Batch Proofread works segment-by-segment without TM reference pairs.");
            Span(root, ref row, _chkIncludeTmMatches);

            _chkDemoMode = Check("Incognito mode – anonymise project names, paths, and personal data in AI responses");
            _chkDemoMode.Checked = false;
            var demoTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            demoTip.SetToolTip(_chkDemoMode,
                "When enabled, the AI replaces all project names, file paths, TM names,\r\n" +
                "and other identifying data with anonymised placeholders in its responses.\r\n" +
                "Useful for screen sharing, recording demos, posting screenshots in forums,\r\n" +
                "or any situation where you need to keep client data confidential.\r\n" +
                "Toggle on before sharing, toggle off when done.");
            Span(root, ref row, _chkDemoMode);

            _lblSurroundingSegments = FieldLabel("Surrounding segments:", indentSteps: 1);
            _nudSurroundingSegments = SmallNud(1, 20, 5, 1);
            var surroundingTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            surroundingTip.SetToolTip(_nudSurroundingSegments,
                "Number of segments before and after the active segment to include\r\n" +
                "in {{SURROUNDING_SEGMENTS}} QuickLauncher prompts and the AI Assistant\r\n" +
                "chat context. Default: 5 (five segments on each side).\r\n" +
                "Only applies to Chat and QuickLauncher – not to Batch Operations.");
            Pair(root, ref row, _lblSurroundingSegments, _nudSurroundingSegments);

            // ===== External AI assistants (MCP) =====
            var lblMcpHeader = Header("External AI assistants (MCP)");
            Span(root, ref row, lblMcpHeader);

            var lblMcpHint = new Label
            {
                Text = "Connect Claude Desktop, ChatGPT or other MCP-capable AI apps directly to " +
                       "this Trados session – ask about the open project, search your TMs and " +
                       "termbases, and more, from the AI app's own chat window.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                ForeColor = labelColor,
                Font = HintFont(),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(4))
            };
            Span(root, ref row, lblMcpHint);

            var btnMcpConnect = new Button
            {
                Text = "Connect AI assistant…",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, UiScale.Pixels(4))
            };
            btnMcpConnect.Click += (s, e) =>
            {
                using (var dlg = new McpConnectDialog())
                    dlg.ShowDialog(FindForm());
            };
            Span(root, ref row, btnMcpConnect);

            _lblPromptContext = FieldLabel("Prompt context – source segments:", indentSteps: 0);
            _nudPromptContext = SmallNud(0, 50000, 0, 100);
            var promptCtxTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300 };
            promptCtxTip.SetToolTip(_nudPromptContext,
                "How many source segments the AI's get_prompt_context tool includes when it helps you\r\n" +
                "craft a translation prompt for the open project.\r\n" +
                "0 = the whole document (best for large-context models like Claude, and for high-value\r\n" +
                "projects where you want the AI to see everything). A positive number caps it.");
            Pair(root, ref row, _lblPromptContext, _nudPromptContext);

            // ===== Info =====
            _lblInfo = new Label
            {
                Text = "API keys are stored locally and never sent anywhere except to the selected provider.",
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = HintFont(),
                Margin = new Padding(0, UiScale.Pixels(12), 0, 0)
            };
            Span(root, ref row, _lblInfo);

            Controls.Add(root);
        }

        /// <summary>
        /// Sets the DropDownWidth of a ComboBox to fit the widest item text,
        /// so long model descriptions are fully visible in the dropdown list.
        /// </summary>
        private static void AutoSizeDropDown(ComboBox cmb)
        {
            var maxWidth = cmb.Width;
            using (var g = cmb.CreateGraphics())
            {
                foreach (var item in cmb.Items)
                {
                    var w = (int)g.MeasureString(item.ToString(), cmb.Font).Width + 20;
                    if (w > maxWidth) maxWidth = w;
                }
            }
            cmb.DropDownWidth = maxWidth;
        }

        // ─── Settings Population ─────────────────────────────────────

        public void PopulateFromSettings(AiSettings settings)
        {
            // #106: the cached provider lists, so the dropdown shows them from the start.
            _fetchedSource = new AiSettings
            {
                FetchedModels = new List<FetchedModelEntry>(settings?.FetchedModels ?? new List<FetchedModelEntry>()),
                ShowAllModels = settings?.ShowAllModels ?? false
            };
            if (_chkShowAllModels != null) _chkShowAllModels.Checked = _fetchedSource.ShowAllModels;
            if (settings == null) return;

            // Load ALL provider API keys into the dictionary so switching preserves them
            var keys = settings.ApiKeys ?? new AiApiKeys();
            // #108: the shared keys win; the plugin's own key is the fallback where
            // they have none. A shared key this Windows account cannot unlock is not
            // replaced by that older copy: its box stays empty, and the note under it
            // says why and what to do (design note §7, M1). That note replaces the
            // one-off "could not be read" warning, which looked only at the plugin's
            // own copies and so said nothing on a second PC even when it mattered.
            _unreadableKeyNotes.Clear();
            string Shared(string provider, string own)
            {
                var key = Supervertaler.Core.ApiKeyStore.Get(provider);
                if (key != null) return key;
                var note = Supervertaler.Core.ApiKeyStore.UnreadableKeyNote(provider);
                if (note == null) return own ?? "";
                _unreadableKeyNotes[provider] = note;
                return "";
            }
            _providerApiKeys[LlmModels.ProviderOpenAi] = Shared(LlmModels.ProviderOpenAi, keys.OpenAi);
            _providerApiKeys[LlmModels.ProviderClaude] = Shared(LlmModels.ProviderClaude, keys.Claude);
            _providerApiKeys[LlmModels.ProviderGemini] = Shared(LlmModels.ProviderGemini, keys.Gemini);
            _providerApiKeys[LlmModels.ProviderGrok] = Shared(LlmModels.ProviderGrok, keys.Grok);
            _providerApiKeys[LlmModels.ProviderMistral] = Shared(LlmModels.ProviderMistral, keys.Mistral);
            _providerApiKeys[LlmModels.ProviderDeepSeek] = Shared(LlmModels.ProviderDeepSeek, keys.DeepSeek);
            _providerApiKeys[LlmModels.ProviderOpenRouter] = Shared(LlmModels.ProviderOpenRouter, keys.OpenRouter);
            _providerApiKeys[LlmModels.ProviderCustomOpenAi] = keys.CustomOpenAi ?? "";
            _providerApiKeys[LlmModels.ProviderOllama] = ""; // Ollama doesn't use API keys

            // Select provider (this triggers OnProviderChanged which loads the right key)
            _lastProviderKey = null; // reset so first switch doesn't save empty string
            for (int i = 0; i < _cmbProvider.Items.Count; i++)
            {
                if (((ProviderItem)_cmbProvider.Items[i]).Key == settings.SelectedProvider)
                {
                    _cmbProvider.SelectedIndex = i;
                    break;
                }
            }

            // Set model selections (after provider triggers model list population)
            SetSelectedModel(settings);

            // Ollama
            _txtOllamaEndpoint.Text = settings.OllamaEndpoint ?? "http://localhost:11434";
            _nudOllamaTimeout.Value = Math.Max(_nudOllamaTimeout.Minimum,
                Math.Min(_nudOllamaTimeout.Maximum, settings.OllamaTimeoutMinutes));

            // Custom OpenAI profiles
            PopulateCustomProfiles(settings);
            UpdateKeyNotes();

            // Machine-translation keys: the shared file only - Trados never kept its
            // own. One this account cannot unlock leaves its box empty, with M1 under it.
            _mtUnreadableNotes.Clear();
            foreach (var engine in _mtKeyBoxes.Keys)
            {
                var key = Supervertaler.Core.ApiKeyStore.Get(engine);
                if (key == null)
                {
                    var note = Supervertaler.Core.ApiKeyStore.UnreadableKeyNote(engine);
                    if (note != null) _mtUnreadableNotes[engine] = note;
                }
                _mtKeyBoxes[engine].Text = key ?? "";
            }
            if (_txtMicrosoftRegion != null) _txtMicrosoftRegion.Text = settings.MicrosoftTranslatorRegion ?? "";
            _lblMtStatus.Text = "";
            UpdateMtKeyNotes();

            // AI Context
            _chkIncludeTmMatches.Checked = settings.IncludeTmMatches;
            _nudTmFuzzyMinScore.Value = Math.Min(100, Math.Max(50, settings.TmFuzzyMinScore));
            _chkDemoMode.Checked = settings.DemoMode;
            _chkIncludeDocumentContext.Checked = settings.IncludeDocumentContext;
            _nudMaxSegments.Value = Math.Max(_nudMaxSegments.Minimum,
                Math.Min(_nudMaxSegments.Maximum, settings.DocumentContextMaxSegments));
            _nudMaxSegments.Enabled = settings.IncludeDocumentContext;
            _lblMaxSegments.Enabled = settings.IncludeDocumentContext;
            _nudPromptContext.Value = Math.Max(_nudPromptContext.Minimum,
                Math.Min(_nudPromptContext.Maximum, settings.PromptContextMaxSegments));
            _nudSurroundingSegments.Value = Math.Max(_nudSurroundingSegments.Minimum,
                Math.Min(_nudSurroundingSegments.Maximum, settings.QuickLauncherSurroundingSegments));
            _chkIncludeTermMetadata.Checked = settings.IncludeTermMetadata;
            _chkIncludeSuperMemory.Checked = settings.IncludeSuperMemoryContext;
            _chkIncludeSuperMemoryAutoPrompt.Checked = settings.IncludeSuperMemoryInAutoPrompt;
            _chkIncludeSuperMemoryAutoPrompt.Enabled = settings.IncludeSuperMemoryContext;
            _chkLogPrompts.Checked = settings.LogPromptsToReports;
            _chkPersistUsageLog.Checked = settings.IsUsageLogEnabled;
            _nudMonthlyBudget.Value = Math.Max(_nudMonthlyBudget.Minimum,
                Math.Min(_nudMonthlyBudget.Maximum, (decimal)settings.MonthlyBudgetUsd));
            _nudBatchSize.Value = Math.Max(_nudBatchSize.Minimum,
                Math.Min(_nudBatchSize.Maximum, settings.BatchSize > 0 ? settings.BatchSize : 20));
            _chkTcAsComments.Checked = settings.TcMarkersAsComments;
            _chkProofreadCommentLabel.Checked = settings.ProofreadCommentLabel;
            _chkKeepRepetitions.Checked = settings.KeepRepetitionsConsistent;
        }


        public void ApplyToSettings(AiSettings settings)
        {
            if (settings == null) return;

            // #106: whatever was fetched during this dialog session is kept.
            settings.FetchedModels = new List<FetchedModelEntry>(_fetchedSource.FetchedModels ?? new List<FetchedModelEntry>());
            settings.ShowAllModels = _fetchedSource.ShowAllModels;

            var provider = GetSelectedProviderKey();
            settings.SelectedProvider = provider;

            // Model — a custom model ID (if entered) overrides the curated dropdown.
            var selectedModel = _cmbModel.SelectedItem as ModelItem;
            var customModelId = _txtCustomModelId.Text.Trim();
            var modelId = customModelId.Length > 0 ? customModelId : selectedModel?.Id;
            switch (provider)
            {
                case LlmModels.ProviderOpenAi:
                    settings.OpenAiModel = modelId ?? LlmModels.DefaultOpenAiModelId;
                    break;
                case LlmModels.ProviderClaude:
                    settings.ClaudeModel = modelId ?? "claude-sonnet-5-5";
                    break;
                case LlmModels.ProviderGemini:
                    settings.GeminiModel = modelId ?? "gemini-3.1-flash-lite";
                    break;
                case LlmModels.ProviderGrok:
                    settings.GrokModel = modelId ?? "grok-4.3";
                    break;
                case LlmModels.ProviderMistral:
                    settings.MistralModel = modelId ?? "mistral-large-latest";
                    break;
                case LlmModels.ProviderDeepSeek:
                    settings.DeepSeekModel = modelId ?? "deepseek-v4-pro";
                    break;
                case LlmModels.ProviderOpenRouter:
                    settings.OpenRouterModel = modelId ?? "anthropic/claude-sonnet-5.5";
                    break;
                case LlmModels.ProviderOllama:
                    settings.OllamaModel = modelId ?? "translategemma:12b";
                    break;
            }

            // Save the current provider's key into the dictionary first
            _providerApiKeys[provider] = _txtApiKey.Text.Trim();

            // Write ALL provider keys from the dictionary to settings
            if (settings.ApiKeys == null) settings.ApiKeys = new AiApiKeys();
            string val;
            settings.ApiKeys.OpenAi = _providerApiKeys.TryGetValue(LlmModels.ProviderOpenAi, out val) ? val : "";
            settings.ApiKeys.Claude = _providerApiKeys.TryGetValue(LlmModels.ProviderClaude, out val) ? val : "";
            settings.ApiKeys.Gemini = _providerApiKeys.TryGetValue(LlmModels.ProviderGemini, out val) ? val : "";
            settings.ApiKeys.Grok = _providerApiKeys.TryGetValue(LlmModels.ProviderGrok, out val) ? val : "";
            settings.ApiKeys.Mistral = _providerApiKeys.TryGetValue(LlmModels.ProviderMistral, out val) ? val : "";
            settings.ApiKeys.DeepSeek = _providerApiKeys.TryGetValue(LlmModels.ProviderDeepSeek, out val) ? val : "";
            settings.ApiKeys.OpenRouter = _providerApiKeys.TryGetValue(LlmModels.ProviderOpenRouter, out val) ? val : "";
            settings.ApiKeys.CustomOpenAi = _providerApiKeys.TryGetValue(LlmModels.ProviderCustomOpenAi, out val) ? val : "";

            // #108: the shared key file is the source of truth - write what changed.
            foreach (var keyProvider in new[] { LlmModels.ProviderOpenAi, LlmModels.ProviderClaude, LlmModels.ProviderGemini,
                                             LlmModels.ProviderGrok, LlmModels.ProviderMistral, LlmModels.ProviderDeepSeek,
                                             LlmModels.ProviderOpenRouter })
            {
                var typed = _providerApiKeys.TryGetValue(keyProvider, out val) ? (val ?? "").Trim() : "";
                var stored = Supervertaler.Core.ApiKeyStore.Get(keyProvider) ?? "";
                if (!string.Equals(typed, stored, StringComparison.Ordinal))
                    Supervertaler.Core.ApiKeyStore.Set(keyProvider, typed);
            }

            // The engines' keys the same way. An empty box over a key this account
            // cannot read is not a change (Get gives null), so OK keeps that key.
            foreach (var kv in _mtKeyBoxes)
            {
                var typed = kv.Value.Text.Trim();
                var stored = Supervertaler.Core.ApiKeyStore.Get(kv.Key) ?? "";
                if (!string.Equals(typed, stored, StringComparison.Ordinal))
                    Supervertaler.Core.ApiKeyStore.Set(kv.Key, typed);
            }
            if (_txtMicrosoftRegion != null) settings.MicrosoftTranslatorRegion = _txtMicrosoftRegion.Text.Trim();

            // Ollama endpoint + timeout
            settings.OllamaEndpoint = _txtOllamaEndpoint.Text.Trim();
            settings.OllamaTimeoutMinutes = (int)_nudOllamaTimeout.Value;

            // Custom OpenAI profiles – save current profile values first
            SaveCurrentCustomProfile(settings);
            settings.SelectedCustomProfileName = (_cmbCustomProfile.SelectedItem as CustomProfileItem)?.Name ?? "";

            // AI Context
            settings.IncludeTmMatches = _chkIncludeTmMatches.Checked;
            settings.TmFuzzyMinScore = (int)_nudTmFuzzyMinScore.Value;
            settings.DemoMode = _chkDemoMode.Checked;
            settings.IncludeDocumentContext = _chkIncludeDocumentContext.Checked;
            settings.DocumentContextMaxSegments = (int)_nudMaxSegments.Value;
            settings.PromptContextMaxSegments = (int)_nudPromptContext.Value;
            settings.QuickLauncherSurroundingSegments = (int)_nudSurroundingSegments.Value;
            settings.IncludeTermMetadata = _chkIncludeTermMetadata.Checked;
            settings.IncludeSuperMemoryContext = _chkIncludeSuperMemory.Checked;
            settings.IncludeSuperMemoryInAutoPrompt = _chkIncludeSuperMemoryAutoPrompt.Checked;
            settings.LogPromptsToReports = _chkLogPrompts.Checked;
            settings.PersistUsageLog = _chkPersistUsageLog.Checked;
            settings.MonthlyBudgetUsd = (double)_nudMonthlyBudget.Value;
            settings.BatchSize = (int)_nudBatchSize.Value;
            settings.TcMarkersAsComments = _chkTcAsComments.Checked;
            settings.ProofreadCommentLabel = _chkProofreadCommentLabel.Checked;
            settings.KeepRepetitionsConsistent = _chkKeepRepetitions.Checked;
            // NOTE: DisabledAiTermbaseIds / AiTermbaseIdsInitialized are now owned by the
            // Termbases tab (the "AI" column in the termbase grid), not this panel.
        }

        // ─── Event Handlers ──────────────────────────────────────────

        private void OnProviderChanged(object sender, EventArgs e)
        {
            var providerKey = GetSelectedProviderKey();

            // Save the outgoing provider's API key before switching
            if (_lastProviderKey != null)
                _providerApiKeys[_lastProviderKey] = _txtApiKey.Text.Trim();

            // Populate model list: curated plus whatever a fetch added (#106)
            PopulateModels(providerKey, null);
            if (_btnFetchModels != null)
                _btnFetchModels.Enabled = Supervertaler.Core.LlmModelCatalog.CanFetch(providerKey);

            // Restore the incoming provider's API key
            string savedKey;
            _txtApiKey.Text = _providerApiKeys.TryGetValue(providerKey, out savedKey) ? savedKey : "";
            UpdateKeyHint();

            // Show/hide provider-specific rows. Hiding both cells of a row
            // collapses it, so the root grid reflows automatically — no manual
            // repositioning to do.
            bool isOllama = providerKey == LlmModels.ProviderOllama;
            bool isCustom = providerKey == LlmModels.ProviderCustomOpenAi;
            foreach (var c in _ollamaRows) c.Visible = isOllama;
            foreach (var c in _customRows) c.Visible = isCustom;
            foreach (var c in _localServerRows) c.Visible = isOllama || isCustom;

            // API key field: hide for Ollama, show for others
            _txtApiKey.Enabled = providerKey != LlmModels.ProviderOllama;
            _btnShowKey.Enabled = providerKey != LlmModels.ProviderOllama;

            // Model field: hide for Custom OpenAI (uses profile's model)
            _cmbModel.Enabled = providerKey != LlmModels.ProviderCustomOpenAi;

            // Custom model ID field: same enable rule. Cleared on every provider
            // switch — the value is provider-specific. During settings load,
            // SetSelectedModel runs after this and re-fills it if the saved model
            // isn't in the curated dropdown.
            _txtCustomModelId.Enabled = providerKey != LlmModels.ProviderCustomOpenAi;
            _txtCustomModelId.Text = "";

            // Clear status
            _lblStatus.Text = "";

            _lastProviderKey = providerKey;
            UpdateKeyNotes();
        }

        /// <summary>
        /// Fills the model dropdown for a provider from the merged catalogue, keeping
        /// <paramref name="keepId"/> selected if it is present, else the provider's
        /// default - not simply the first entry, which for OpenAI is the dearest.
        /// </summary>
        private void PopulateModels(string providerKey, string keepId)
        {
            _cmbModel.Items.Clear();
            foreach (var m in ModelCatalog.ModelsFor(providerKey, _fetchedSource))
                _cmbModel.Items.Add(new ModelItem(m));

            // Auto-size the dropdown to fit the longest model description
            AutoSizeDropDown(_cmbModel);

            if (_cmbModel.Items.Count == 0) return;
            int IndexOf(string id)
            {
                if (string.IsNullOrEmpty(id)) return -1;
                for (int i = 0; i < _cmbModel.Items.Count; i++)
                    if (string.Equals(((ModelItem)_cmbModel.Items[i]).Id, id, StringComparison.OrdinalIgnoreCase))
                        return i;
                return -1;
            }
            var index = IndexOf(keepId);
            if (index < 0) index = IndexOf(LlmModels.DefaultModelId(providerKey));
            _cmbModel.SelectedIndex = index < 0 ? 0 : index;
        }

        /// <summary>#106: ask the provider for its model list and add what is new.</summary>
        private async void OnFetchModelsClick(object sender, EventArgs e)
        {
            var provider = GetSelectedProviderKey();
            var keep = (_cmbModel.SelectedItem as ModelItem)?.Id;
            _btnFetchModels.Enabled = false;
            _lblStatus.Text = "Fetching the model list\u2026";
            _lblStatus.ForeColor = Color.FromArgb(100, 100, 100);
            try
            {
                var apiKey = GetEffectiveApiKey();
                var keyProblem = KeyBoxProblem(provider, apiKey, test: false);
                if (keyProblem != null)
                {
                    _lblStatus.Text = "✗ " + keyProblem;
                    _lblStatus.ForeColor = Color.FromArgb(180, 60, 60);
                    return;
                }
                string baseUrl = null;
                if (provider == LlmModels.ProviderOllama) baseUrl = _txtOllamaEndpoint.Text.Trim();
                else if (provider == LlmModels.ProviderCustomOpenAi) baseUrl = _txtCustomEndpoint.Text.Trim();

                var models = await Supervertaler.Core.LlmModelCatalog.FetchAsync(provider, apiKey, baseUrl, CancellationToken.None);

                var curated = new HashSet<string>(
                    (LlmModels.GetModelsForProvider(provider) ?? new LlmModelInfo[0]).Select(m => m.Id),
                    StringComparer.OrdinalIgnoreCase);
                int added = models.Count(m => !curated.Contains(m.Id));

                ModelCatalog.RecordFetched(_fetchedSource, provider, models);
                // Fetching means "show me": switch to the full list.
                if (added > 0 && !_chkShowAllModels.Checked) _chkShowAllModels.Checked = true;   // repopulates
                else PopulateModels(provider, keep);

                _lblStatus.Text = $"\u2713 {LlmModels.GetProviderDisplayName(provider)}: {models.Count} models" +
                                  (added > 0 ? $", {added} beyond the short list - shown with Show all models ticked" : ", none beyond the short list");
                _lblStatus.ForeColor = Color.FromArgb(30, 130, 60);
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"\u2717 Model list: {ex.Message}";
                _lblStatus.ForeColor = Color.FromArgb(180, 60, 60);
            }
            finally
            {
                _btnFetchModels.Enabled = Supervertaler.Core.LlmModelCatalog.CanFetch(provider);
            }
        }

        private async void OnTestConnectionClick(object sender, EventArgs e)
        {
            _btnTestConnection.Enabled = false;
            _lblStatus.Text = "Testing...";
            _lblStatus.ForeColor = Color.FromArgb(100, 100, 100);

            try
            {
                var provider = GetSelectedProviderKey();
                var model = GetEffectiveModel();
                var apiKey = GetEffectiveApiKey();
                var keyProblem = KeyBoxProblem(provider, apiKey, test: true);
                if (keyProblem != null)
                {
                    _lblStatus.Text = "✗ " + keyProblem;
                    _lblStatus.ForeColor = Color.FromArgb(180, 60, 60);
                    return;
                }
                string baseUrl = null;

                if (provider == LlmModels.ProviderOllama)
                    baseUrl = _txtOllamaEndpoint.Text.Trim();
                else if (provider == LlmModels.ProviderCustomOpenAi)
                    baseUrl = _txtCustomEndpoint.Text.Trim();

                using (var client = new LlmClient(provider, model, apiKey, baseUrl))
                {
                    var error = await client.TestConnectionAsync(CancellationToken.None);
                    if (error == null)
                    {
                        _lblStatus.Text = "✓ Connected";
                        _lblStatus.ForeColor = Color.FromArgb(30, 130, 60);
                    }
                    else
                    {
                        _lblStatus.Text = $"✗ {error}";
                        _lblStatus.ForeColor = Color.FromArgb(180, 60, 60);
                    }
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"✗ {ex.Message}";
                _lblStatus.ForeColor = Color.FromArgb(180, 60, 60);
            }
            finally
            {
                _btnTestConnection.Enabled = true;
            }
        }

        /// <summary>
        /// Test beside an engine's key box: what is in the box, never a blank in
        /// place of a key that cannot be read (the same rule as Test Connection).
        /// DeepL is asked for its usage, which is free; Google and Microsoft
        /// translate one word, four characters.
        /// </summary>
        private async Task OnTestEngineClick(string engine, Button button)
        {
            var name = Supervertaler.Core.MachineTranslation.DisplayName(engine);
            var key = _mtKeyBoxes[engine].Text.Trim();
            if (key.Length == 0)
            {
                _lblMtStatus.Text = "✗ " + name + ": " + (_mtUnreadableNotes.ContainsKey(engine)
                    ? "the stored key can't be read on this computer – paste it in again first."
                    : "enter an API key first.");
                _lblMtStatus.ForeColor = Color.FromArgb(180, 60, 60);
                return;
            }

            button.Enabled = false;
            _lblMtStatus.Text = "Testing " + name + "…";
            _lblMtStatus.ForeColor = Color.FromArgb(100, 100, 100);
            try
            {
                string accepted = null;
                var failure = await Supervertaler.Core.MachineTranslation.CheckKeyAsync(engine, key,
                    _txtMicrosoftRegion?.Text.Trim(), TradosApiKeys.SettingsPlace, CancellationToken.None, line => accepted = line);
                if (IsDisposed) return;
                _lblMtStatus.Text = failure == null ? "✓ " + (accepted ?? name + " accepted the key.") : "✗ " + failure.Message;
                _lblMtStatus.ForeColor = failure == null ? Color.FromArgb(30, 130, 60) : Color.FromArgb(180, 60, 60);
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _lblMtStatus.Text = "✗ " + name + ": " + ex.Message;
                _lblMtStatus.ForeColor = Color.FromArgb(180, 60, 60);
            }
            finally
            {
                if (!button.IsDisposed) button.Enabled = true;
            }
        }

        /// <summary>M1 under an engine's key box while it is empty because its stored key cannot be read here.</summary>
        private void UpdateMtKeyNotes()
        {
            foreach (var kv in _mtKeyBoxes)
            {
                string note = null;
                if (kv.Value.Text.Trim().Length == 0) _mtUnreadableNotes.TryGetValue(kv.Key, out note);
                ShowKeyNote(_mtKeyNotes[kv.Key], _mtKeyNoteSpacers[kv.Key], kv.Value.Parent, note);
            }
        }

        /// <summary>
        /// The notes under the key boxes. Under a box that is empty because its
        /// stored key cannot be read on this computer, that sentence (design note
        /// §7, M1) - until something is typed, which is the remedy. Under the
        /// provider box when there is no such key, the start-up notice about a
        /// damaged locked key file, if there was one this session.
        /// </summary>
        private void UpdateKeyNotes()
        {
            if (_lblKeyNote == null || _lblCustomKeyNote == null) return;
            var provider = GetSelectedProviderKey();

            string note = null;
            if (_txtApiKey.Text.Trim().Length == 0) _unreadableKeyNotes.TryGetValue(provider, out note);
            ShowKeyNote(_lblKeyNote, _keyNoteSpacer, _txtApiKey.Parent, note ?? TradosApiKeys.StartupNotice);

            string customNote = null;
            if (provider == LlmModels.ProviderCustomOpenAi && _txtCustomApiKey.Text.Trim().Length == 0)
                customNote = (_cmbCustomProfile.SelectedItem as CustomProfileItem)?.UnreadableNote;
            ShowKeyNote(_lblCustomKeyNote, _customKeyNoteSpacer, _txtCustomApiKey.Parent, customNote);
        }

        /// <summary>
        /// Why Test Connection or Fetch list must not call the provider with what is
        /// in the key box, or null. A box left empty because the stored key cannot
        /// be read here is never sent in its place (design note §2) - the same rule
        /// as memoQ's dialogs. Nor, for a test, is an empty box for a cloud
        /// provider, which can only fail. Fetch list may go without a key: some
        /// providers publish their list openly, and so may an endpoint on this computer.
        /// </summary>
        private string KeyBoxProblem(string provider, string apiKey, bool test)
        {
            if (!string.IsNullOrEmpty(apiKey) || provider == LlmModels.ProviderOllama) return null;
            var unreadable = provider == LlmModels.ProviderCustomOpenAi
                ? (_cmbCustomProfile.SelectedItem as CustomProfileItem)?.UnreadableNote != null
                : _unreadableKeyNotes.ContainsKey(provider);
            if (unreadable) return "The stored key can't be read on this computer – paste it in again first.";
            return test && provider != LlmModels.ProviderCustomOpenAi ? "Enter an API key first." : null;
        }

        private static void ShowKeyNote(Label note, Label spacer, Control row, string text)
        {
            var show = !string.IsNullOrEmpty(text);
            if (show)
            {
                // Wraps at the width of the key's row, as tall as the text needs.
                var width = Math.Max(UiScale.Pixels(240), row?.Width ?? 0);
                if (note.MaximumSize.Width != width) note.MaximumSize = new Size(width, 0);
                if (note.Text != text) note.Text = text;
            }
            if (note.Visible != show) note.Visible = show;
            if (spacer.Visible != show) spacer.Visible = show;
        }

        /// <summary>#108: says when the key in the box plainly belongs to another service.</summary>
        private void UpdateKeyHint()
        {
            if (_lblKeyHint == null || _txtApiKey == null) return;
            string hint = null;
            try { hint = Supervertaler.Core.ApiKeyStore.CheckShape(GetSelectedProviderKey(), _txtApiKey.Text); } catch { }
            _lblKeyHint.Text = hint ?? "";
            _lblKeyHint.Visible = hint != null;
        }

        private void OnShowKeyClick(object sender, EventArgs e)
        {
            _keyVisible = !_keyVisible;
            _txtApiKey.UseSystemPasswordChar = !_keyVisible;
            _btnShowKey.Text = _keyVisible ? "Hide" : "Show";
        }

        private void OnShowCustomKeyClick(object sender, EventArgs e)
        {
            _customKeyVisible = !_customKeyVisible;
            _txtCustomApiKey.UseSystemPasswordChar = !_customKeyVisible;
            _btnShowCustomKey.Text = _customKeyVisible ? "Hide" : "Show";
        }

        private void OnAddProfileClick(object sender, EventArgs e)
        {
            var name = "New Endpoint";
            int counter = 1;
            while (ProfileExists(name))
                name = $"New Endpoint {++counter}";

            _cmbCustomProfile.Items.Add(new CustomProfileItem(name));
            _cmbCustomProfile.SelectedIndex = _cmbCustomProfile.Items.Count - 1;
            _txtCustomEndpoint.Text = "";
            _txtCustomModel.Text = "";
            _txtCustomApiKey.Text = "";
        }

        private void OnRemoveProfileClick(object sender, EventArgs e)
        {
            if (_cmbCustomProfile.SelectedIndex < 0 || _cmbCustomProfile.Items.Count == 0)
                return;

            var idx = _cmbCustomProfile.SelectedIndex;
            _cmbCustomProfile.Items.RemoveAt(idx);

            if (_cmbCustomProfile.Items.Count > 0)
                _cmbCustomProfile.SelectedIndex = Math.Min(idx, _cmbCustomProfile.Items.Count - 1);
            else
            {
                _txtCustomEndpoint.Text = "";
                _txtCustomModel.Text = "";
                _txtCustomApiKey.Text = "";
            }
        }

        private void OnRenameProfileClick(object sender, EventArgs e)
        {
            if (!(_cmbCustomProfile.SelectedItem is CustomProfileItem current))
                return;

            var idx = _cmbCustomProfile.SelectedIndex;
            var newName = PromptForProfileRename(FindForm(), current.Name);
            if (newName == null || newName == current.Name)
                return;

            current.Name = newName;

            // ComboBox caches the display string per item; re-inserting forces a redraw
            // without firing SelectedIndexChanged (which would save-then-reload fields).
            _cmbCustomProfile.SelectedIndexChanged -= OnCustomProfileChanged;
            try
            {
                _cmbCustomProfile.Items[idx] = current;
                _cmbCustomProfile.SelectedIndex = idx;
                _lastCustomProfileIndex = idx;
            }
            finally
            {
                _cmbCustomProfile.SelectedIndexChanged += OnCustomProfileChanged;
            }
        }

        private string PromptForProfileRename(IWin32Window parent, string currentName)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Rename endpoint";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(380, 130);
                dlg.ShowInTaskbar = false;

                var lbl = new Label
                {
                    Text = "New name for this endpoint:",
                    Location = new Point(12, 12),
                    Size = new Size(356, 18),
                    AutoSize = false
                };

                var txt = new TextBox
                {
                    Location = new Point(12, 38),
                    Size = new Size(356, 22),
                    Text = currentName ?? ""
                };
                txt.SelectAll();

                var lblError = new Label
                {
                    Location = new Point(12, 66),
                    Size = new Size(356, 18),
                    ForeColor = Color.FromArgb(180, 60, 60),
                    Text = ""
                };

                var btnOk = new Button
                {
                    Text = "Rename",
                    DialogResult = DialogResult.OK,
                    Location = new Point(192, 90),
                    Size = new Size(85, 28)
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(283, 90),
                    Size = new Size(85, 28)
                };

                void Revalidate()
                {
                    var candidate = txt.Text.Trim();
                    if (string.IsNullOrEmpty(candidate))
                    {
                        btnOk.Enabled = false;
                        lblError.Text = "";
                    }
                    else if (candidate == (currentName ?? ""))
                    {
                        btnOk.Enabled = false;
                        lblError.Text = "";
                    }
                    else if (NameTakenByOther(candidate, currentName))
                    {
                        btnOk.Enabled = false;
                        lblError.Text = "Another endpoint already uses that name.";
                    }
                    else
                    {
                        btnOk.Enabled = true;
                        lblError.Text = "";
                    }
                }

                txt.TextChanged += (s, e) => Revalidate();
                Revalidate();

                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;
                dlg.Controls.AddRange(new Control[] { lbl, txt, lblError, btnOk, btnCancel });

                if (dlg.ShowDialog(parent) != DialogResult.OK)
                    return null;

                return txt.Text.Trim();
            }
        }

        private bool NameTakenByOther(string candidate, string currentName)
        {
            foreach (CustomProfileItem item in _cmbCustomProfile.Items)
            {
                if (item.Name == currentName) continue;
                if (string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private int _lastCustomProfileIndex = -1;

        private void OnCustomProfileChanged(object sender, EventArgs e)
        {
            // Save the previous profile's fields before switching
            if (_lastCustomProfileIndex >= 0 && _lastCustomProfileIndex < _cmbCustomProfile.Items.Count)
            {
                var prev = (CustomProfileItem)_cmbCustomProfile.Items[_lastCustomProfileIndex];
                prev.Endpoint = _txtCustomEndpoint.Text.Trim();
                prev.Model = _txtCustomModel.Text.Trim();
                prev.ApiKey = _txtCustomApiKey.Text.Trim();
            }

            // Load the newly selected profile
            if (_cmbCustomProfile.SelectedItem is CustomProfileItem item)
            {
                _txtCustomEndpoint.Text = item.Endpoint ?? "";
                _txtCustomModel.Text = item.Model ?? "";
                _txtCustomApiKey.Text = item.ApiKey ?? "";
            }

            _lastCustomProfileIndex = _cmbCustomProfile.SelectedIndex;
            UpdateKeyNotes();
        }

        // ─── Helpers ─────────────────────────────────────────────────

        private string GetSelectedProviderKey()
        {
            return (_cmbProvider.SelectedItem as ProviderItem)?.Key ?? LlmModels.ProviderOpenAi;
        }

        private string GetEffectiveModel()
        {
            var provider = GetSelectedProviderKey();
            if (provider == LlmModels.ProviderCustomOpenAi)
                return _txtCustomModel.Text.Trim();
            // A custom model ID, if entered, overrides the curated dropdown.
            var customId = _txtCustomModelId.Text.Trim();
            if (customId.Length > 0)
                return customId;
            var modelItem = _cmbModel.SelectedItem as ModelItem;
            if (modelItem != null) return modelItem.Id;
            return LlmModels.DefaultOpenAiModelId;
        }

        private string GetEffectiveApiKey()
        {
            var provider = GetSelectedProviderKey();
            if (provider == LlmModels.ProviderCustomOpenAi)
                return _txtCustomApiKey.Text.Trim();
            if (provider == LlmModels.ProviderOllama)
                return "";
            return _txtApiKey.Text.Trim();
        }

        private void SetSelectedModel(AiSettings settings)
        {
            string targetId;
            switch (settings.SelectedProvider)
            {
                case LlmModels.ProviderOpenAi: targetId = settings.OpenAiModel; break;
                case LlmModels.ProviderClaude: targetId = settings.ClaudeModel; break;
                case LlmModels.ProviderGemini: targetId = settings.GeminiModel; break;
                case LlmModels.ProviderGrok: targetId = settings.GrokModel; break;
                case LlmModels.ProviderMistral: targetId = settings.MistralModel; break;
                case LlmModels.ProviderDeepSeek: targetId = settings.DeepSeekModel; break;
                case LlmModels.ProviderOpenRouter: targetId = settings.OpenRouterModel; break;
                case LlmModels.ProviderOllama: targetId = settings.OllamaModel; break;
                default: return;
            }

            _txtCustomModelId.Text = "";

            for (int i = 0; i < _cmbModel.Items.Count; i++)
            {
                if (((ModelItem)_cmbModel.Items[i]).Id == targetId)
                {
                    _cmbModel.SelectedIndex = i;
                    return;
                }
            }

            // The saved model isn't in the curated dropdown — show it in the
            // custom model ID field instead (works for any provider).
            if (!string.IsNullOrEmpty(targetId))
                _txtCustomModelId.Text = targetId;
        }

        private void PopulateCustomProfiles(AiSettings settings)
        {
            _cmbCustomProfile.Items.Clear();
            _profileNamesLoaded = new List<string>();
            if (settings.CustomOpenAiProfiles != null)
            {
                foreach (var p in settings.CustomOpenAiProfiles)
                {
                    var item = new CustomProfileItem(p.Name)
                    {
                        Endpoint = p.Endpoint,
                        Model = p.Model,
                        ApiKey = p.ApiKey
                    };
                    // Step 1: the profile's own key decides; the locked file's copy
                    // is used only where settings.json has none (TradosApiKeys).
                    if (string.IsNullOrWhiteSpace(p.ApiKey))
                    {
                        var id = TradosApiKeys.ProfileId(p.Name);
                        var locked = Supervertaler.Core.ApiKeyStore.Get(id);
                        if (locked != null) item.ApiKey = item.LockedKey = locked;
                        else item.UnreadableNote = Supervertaler.Core.ApiKeyStore.UnreadableKeyNote(id);
                    }
                    _cmbCustomProfile.Items.Add(item);
                    _profileNamesLoaded.Add(p.Name);
                }
            }

            // Select the active profile
            for (int i = 0; i < _cmbCustomProfile.Items.Count; i++)
            {
                if (((CustomProfileItem)_cmbCustomProfile.Items[i]).Name == settings.SelectedCustomProfileName)
                {
                    _cmbCustomProfile.SelectedIndex = i;
                    return;
                }
            }
            if (_cmbCustomProfile.Items.Count > 0)
                _cmbCustomProfile.SelectedIndex = 0;
        }

        private void SaveCurrentCustomProfile(AiSettings settings)
        {
            // Save current custom profile fields to the item
            if (_cmbCustomProfile.SelectedItem is CustomProfileItem current)
            {
                current.Endpoint = _txtCustomEndpoint.Text.Trim();
                current.Model = _txtCustomModel.Text.Trim();
                current.ApiKey = _txtCustomApiKey.Text.Trim();
            }

            // Rebuild the profiles list from combo items
            settings.CustomOpenAiProfiles = new System.Collections.Generic.List<CustomOpenAiProfile>();
            foreach (CustomProfileItem item in _cmbCustomProfile.Items)
            {
                settings.CustomOpenAiProfiles.Add(new CustomOpenAiProfile
                {
                    Name = item.Name,
                    Endpoint = item.Endpoint,
                    Model = item.Model,
                    // A key shown from the locked file, unchanged, stays out of
                    // settings.json: an OK that changed nothing must not put a key
                    // back into the data folder.
                    ApiKey = item.LockedKey != null && item.ApiKey == item.LockedKey ? "" : item.ApiKey
                });
            }
        }

        /// <summary>
        /// After settings.json has been saved: each endpoint profile's key into the
        /// locked file, and the entries of profiles renamed or removed out of it
        /// (<see cref="TradosApiKeys.SaveProfileKeys"/>). Called by the settings
        /// form after its save, so settings.json is written first, as for every key.
        /// </summary>
        public void SaveLockedProfileKeys()
        {
            var now = new List<(string Name, string Key)>();
            foreach (CustomProfileItem item in _cmbCustomProfile.Items)
                now.Add((item.Name, item.ApiKey));
            TradosApiKeys.SaveProfileKeys(now, _profileNamesLoaded);
            _profileNamesLoaded = now.Select(p => p.Name).ToList();
        }

        private bool ProfileExists(string name)
        {
            foreach (CustomProfileItem item in _cmbCustomProfile.Items)
            {
                if (item.Name == name) return true;
            }
            return false;
        }

        // ─── ComboBox Item Types ─────────────────────────────────────

        private class ProviderItem
        {
            public string Key { get; }
            public ProviderItem(string key) { Key = key; }
            public override string ToString() => LlmModels.GetProviderDisplayName(Key);
        }

        private class ModelItem
        {
            public string Id { get; }
            private readonly string _display;
            public ModelItem(LlmModelInfo info)
            {
                Id = info.Id;
                _display = string.IsNullOrWhiteSpace(info.Description)
                    ? info.DisplayName
                    : $"{info.DisplayName}  –  {info.Description}";
            }
            public override string ToString() => _display;
        }

        private class CustomProfileItem
        {
            public string Name { get; set; }
            public string Endpoint { get; set; } = "";
            public string Model { get; set; } = "";
            public string ApiKey { get; set; } = "";
            /// <summary>The key as read from the locked file, when settings.json had none; else null.</summary>
            public string LockedKey { get; set; }
            /// <summary>M1, when the locked file holds this profile's key but it cannot be read here.</summary>
            public string UnreadableNote { get; set; }
            public CustomProfileItem(string name) { Name = name; }
            public override string ToString() => Name;
        }
    }
}
