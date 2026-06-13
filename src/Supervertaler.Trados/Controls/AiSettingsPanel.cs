using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
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
        // Optional free-text model ID — overrides _cmbModel when filled. Lets
        // users pick a model that isn't in the curated dropdown (e.g. a new
        // release, a preview model, or an OpenRouter router like openrouter/free).
        private TextBox _txtCustomModelId;

        // API Key
        private TextBox _txtApiKey;
        private Button _btnShowKey;
        private Button _btnTestConnection;
        private Label _lblStatus;

        // Provider-specific rows (toggled in OnProviderChanged). Hiding both
        // cells of a TableLayoutPanel row collapses it, so the rest reflows.
        private readonly List<Control> _ollamaRows = new List<Control>();
        private readonly List<Control> _customRows = new List<Control>();

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
        private CheckBox _chkIncludeTermMetadata;
        private CheckBox _chkIncludeSuperMemory;
        private CheckBox _chkIncludeSuperMemoryAutoPrompt;
        private CheckBox _chkLogPrompts;
        private Label _lblBatchSize;
        private NumericUpDown _nudBatchSize;
        private Label _lblAiTermbases;

        // AI Context section – Chat & QuickLauncher only
        private Label _lblChatContextHeader;
        private Label _lblChatContextNote;
        private CheckBox _chkIncludeTmMatches;
        private CheckBox _chkDemoMode;
        private Label _lblSurroundingSegments;
        private NumericUpDown _nudSurroundingSegments;
        private Label _lblQuickLauncherTarget;
        private ComboBox _cmbQuickLauncherTarget;

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
                    Width = UiScale.Pixels(32),
                    Height = UiScale.Pixels(30),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", UiScale.FontSize(11f), bold ? FontStyle.Bold : FontStyle.Regular),
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
            Row(root, ref row, "Model:", _cmbModel);

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

            var lblOllamaTimeout = FieldLabel("Timeout (min):");
            _nudOllamaTimeout = SmallNud(0, 120, 0, 1);
            _lblOllamaTimeoutHint = new Label
            {
                Text = "0 = auto (3–10 min, based on model size)",
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
            _ollamaRows.Add(lblOllamaTimeout);
            _ollamaRows.Add(ollamaTimeoutHost);

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

            // Hide provider-specific rows until OnProviderChanged shows the right set.
            foreach (var c in _ollamaRows) c.Visible = false;
            foreach (var c in _customRows) c.Visible = false;

            // ===== AI context (Batch operations, Chat and QuickLauncher) =====
            _lblAiContextHeader = Header("AI context (Batch operations, Chat and QuickLauncher)");
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
                "loaded and included in AI prompts for translations and chat.");
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

            _chkLogPrompts = Check("Log prompts and responses to Reports tab");
            _chkLogPrompts.Checked = false;
            var logTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            logTip.SetToolTip(_chkLogPrompts,
                "When enabled, every AI API call is logged to the Reports tab with\r\n" +
                "the full prompt, response, estimated token counts, and cost.\r\n" +
                "Useful for monitoring costs and debugging prompt behaviour.");
            Span(root, ref row, _chkLogPrompts);

            _lblBatchSize = FieldLabel("Batch size:");
            _nudBatchSize = SmallNud(5, 100, 20, 1);
            var batchTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            batchTip.SetToolTip(_nudBatchSize,
                "Number of segments sent to the AI provider per API call during\r\n" +
                "Batch Translate and Batch Proofread. Lower values are safer;\r\n" +
                "higher values reduce cost and improve cross-segment consistency.");
            Pair(root, ref row, _lblBatchSize, _nudBatchSize);

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
            _lblChatContextHeader = Header("AI context (Chat and QuickLauncher)");
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
                "Other Batch Operations (Translate, Proofread) are unaffected by this\r\n" +
                "checkbox – they always work segment-by-segment without TM reference\r\n" +
                "pairs.");
            Span(root, ref row, _chkIncludeTmMatches);

            _chkDemoMode = Check("Incognito mode — anonymise project names, paths, and personal data in AI responses");
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

            _lblQuickLauncherTarget = FieldLabel("QuickLauncher prompts go to:", indentSteps: 1);
            _cmbQuickLauncherTarget = new ComboBox
            {
                Width = UiScale.Pixels(220),
                Anchor = AnchorStyles.Left,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, UiScale.Pixels(3), 0, UiScale.Pixels(3))
            };
            _cmbQuickLauncherTarget.Items.Add("In-Trados AI Assistant");
            // Display string was "Workbench Sidekick" through Trados v4.19.x –
            // renamed to "Workbench Chat" after Supervertaler Workbench v1.10.4
            // retired the Sidekick floating window and the Chat surface was
            // promoted into Workbench itself. The persisted setting value
            // (settings.QuickLauncherTarget == "WorkbenchSidekick") is kept
            // unchanged on disk so existing users' saved preferences still
            // resolve – it's an internal identifier, never user-visible.
            _cmbQuickLauncherTarget.Items.Add("Workbench Chat");
            _cmbQuickLauncherTarget.SelectedIndex = 0;
            var qlTargetTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            qlTargetTip.SetToolTip(_cmbQuickLauncherTarget,
                "Where Ctrl+Q QuickLauncher prompts are run.\r\n" +
                "\r\n" +
                "  In-Trados AI Assistant – default. Prompt + response stay in the\r\n" +
                "  Trados Assistant chat panel.\r\n" +
                "\r\n" +
                "  Workbench Chat – posts the prompt to Supervertaler Workbench's\r\n" +
                "  AI tab → Chat sub-tab over a localhost bridge. Useful when you\r\n" +
                "  prefer a larger chat window for the response, or want both\r\n" +
                "  products' chat history in one place. Falls back to the in-Trados\r\n" +
                "  Assistant if Workbench isn't running.");
            Pair(root, ref row, _lblQuickLauncherTarget, _cmbQuickLauncherTarget);

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
            if (settings == null) return;

            // Load ALL provider API keys into the dictionary so switching preserves them
            var keys = settings.ApiKeys ?? new AiApiKeys();
            _providerApiKeys[LlmModels.ProviderOpenAi] = keys.OpenAi ?? "";
            _providerApiKeys[LlmModels.ProviderClaude] = keys.Claude ?? "";
            _providerApiKeys[LlmModels.ProviderGemini] = keys.Gemini ?? "";
            _providerApiKeys[LlmModels.ProviderGrok] = keys.Grok ?? "";
            _providerApiKeys[LlmModels.ProviderMistral] = keys.Mistral ?? "";
            _providerApiKeys[LlmModels.ProviderDeepSeek] = keys.DeepSeek ?? "";
            _providerApiKeys[LlmModels.ProviderOpenRouter] = keys.OpenRouter ?? "";
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

            // AI Context
            _chkIncludeTmMatches.Checked = settings.IncludeTmMatches;
            _chkDemoMode.Checked = settings.DemoMode;
            _chkIncludeDocumentContext.Checked = settings.IncludeDocumentContext;
            _nudMaxSegments.Value = Math.Max(_nudMaxSegments.Minimum,
                Math.Min(_nudMaxSegments.Maximum, settings.DocumentContextMaxSegments));
            _nudMaxSegments.Enabled = settings.IncludeDocumentContext;
            _lblMaxSegments.Enabled = settings.IncludeDocumentContext;
            _nudSurroundingSegments.Value = Math.Max(_nudSurroundingSegments.Minimum,
                Math.Min(_nudSurroundingSegments.Maximum, settings.QuickLauncherSurroundingSegments));
            _cmbQuickLauncherTarget.SelectedIndex =
                string.Equals(settings.QuickLauncherTarget, "WorkbenchSidekick", StringComparison.OrdinalIgnoreCase)
                    ? 1 : 0;
            _chkIncludeTermMetadata.Checked = settings.IncludeTermMetadata;
            _chkIncludeSuperMemory.Checked = settings.IncludeSuperMemoryContext;
            _chkIncludeSuperMemoryAutoPrompt.Checked = settings.IncludeSuperMemoryInAutoPrompt;
            _chkIncludeSuperMemoryAutoPrompt.Enabled = settings.IncludeSuperMemoryContext;
            _chkLogPrompts.Checked = settings.LogPromptsToReports;
            _nudBatchSize.Value = Math.Max(_nudBatchSize.Minimum,
                Math.Min(_nudBatchSize.Maximum, settings.BatchSize > 0 ? settings.BatchSize : 20));
        }


        public void ApplyToSettings(AiSettings settings)
        {
            if (settings == null) return;

            var provider = GetSelectedProviderKey();
            settings.SelectedProvider = provider;

            // Model — a custom model ID (if entered) overrides the curated dropdown.
            var selectedModel = _cmbModel.SelectedItem as ModelItem;
            var customModelId = _txtCustomModelId.Text.Trim();
            var modelId = customModelId.Length > 0 ? customModelId : selectedModel?.Id;
            switch (provider)
            {
                case LlmModels.ProviderOpenAi:
                    settings.OpenAiModel = modelId ?? "gpt-5.4-mini";
                    break;
                case LlmModels.ProviderClaude:
                    settings.ClaudeModel = modelId ?? "claude-sonnet-4-6";
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
                    settings.OpenRouterModel = modelId ?? "anthropic/claude-sonnet-4.6";
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

            // Ollama endpoint + timeout
            settings.OllamaEndpoint = _txtOllamaEndpoint.Text.Trim();
            settings.OllamaTimeoutMinutes = (int)_nudOllamaTimeout.Value;

            // Custom OpenAI profiles – save current profile values first
            SaveCurrentCustomProfile(settings);
            settings.SelectedCustomProfileName = (_cmbCustomProfile.SelectedItem as CustomProfileItem)?.Name ?? "";

            // AI Context
            settings.IncludeTmMatches = _chkIncludeTmMatches.Checked;
            settings.DemoMode = _chkDemoMode.Checked;
            settings.IncludeDocumentContext = _chkIncludeDocumentContext.Checked;
            settings.DocumentContextMaxSegments = (int)_nudMaxSegments.Value;
            settings.QuickLauncherSurroundingSegments = (int)_nudSurroundingSegments.Value;
            settings.QuickLauncherTarget = _cmbQuickLauncherTarget.SelectedIndex == 1
                ? "WorkbenchSidekick" : "TradosAssistant";
            settings.IncludeTermMetadata = _chkIncludeTermMetadata.Checked;
            settings.IncludeSuperMemoryContext = _chkIncludeSuperMemory.Checked;
            settings.IncludeSuperMemoryInAutoPrompt = _chkIncludeSuperMemoryAutoPrompt.Checked;
            settings.LogPromptsToReports = _chkLogPrompts.Checked;
            settings.BatchSize = (int)_nudBatchSize.Value;
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

            // Populate model list
            _cmbModel.Items.Clear();
            var models = LlmModels.GetModelsForProvider(providerKey);
            foreach (var m in models)
                _cmbModel.Items.Add(new ModelItem(m));

            // Auto-size the dropdown to fit the longest model description
            AutoSizeDropDown(_cmbModel);

            if (_cmbModel.Items.Count > 0)
                _cmbModel.SelectedIndex = 0;

            // Restore the incoming provider's API key
            string savedKey;
            _txtApiKey.Text = _providerApiKeys.TryGetValue(providerKey, out savedKey) ? savedKey : "";

            // Show/hide provider-specific rows. Hiding both cells of a row
            // collapses it, so the root grid reflows automatically — no manual
            // repositioning to do.
            bool isOllama = providerKey == LlmModels.ProviderOllama;
            bool isCustom = providerKey == LlmModels.ProviderCustomOpenAi;
            foreach (var c in _ollamaRows) c.Visible = isOllama;
            foreach (var c in _customRows) c.Visible = isCustom;

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
            return "gpt-5.4-mini";
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
            if (settings.CustomOpenAiProfiles != null)
            {
                foreach (var p in settings.CustomOpenAiProfiles)
                {
                    _cmbCustomProfile.Items.Add(new CustomProfileItem(p.Name)
                    {
                        Endpoint = p.Endpoint,
                        Model = p.Model,
                        ApiKey = p.ApiKey
                    });
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
                    ApiKey = item.ApiKey
                });
            }
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
                _display = $"{info.DisplayName}  –  {info.Description}";
            }
            public override string ToString() => _display;
        }

        private class CustomProfileItem
        {
            public string Name { get; set; }
            public string Endpoint { get; set; } = "";
            public string Model { get; set; } = "";
            public string ApiKey { get; set; } = "";
            public CustomProfileItem(string name) { Name = name; }
            public override string ToString() => Name;
        }
    }
}
