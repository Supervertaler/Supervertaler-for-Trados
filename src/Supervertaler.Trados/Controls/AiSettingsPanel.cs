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

        // Ollama section
        private Panel _pnlOllama;
        private TextBox _txtOllamaEndpoint;
        private NumericUpDown _nudOllamaTimeout;
        private Label _lblOllamaTimeoutHint;

        // Custom OpenAI section
        private Panel _pnlCustom;
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

        // Y position right after the Test Connection row (before provider-specific panels)
        private int _providerSectionY;

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
            // Let WinForms scale this dialog by system DPI so it doesn't squish
            // at >100% Windows display scaling. Cheap fallback; for surfaces
            // with their own UiScale-driven layout, set AutoScaleMode = None
            // instead and let UiScale own scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            SuspendLayout();
            BackColor = Color.White;
            AutoScroll = true;

            // Set a reasonable initial size so anchor margins are computed correctly
            // (the panel will be resized to fill the TabPage via Dock = Fill)
            Size = new Size(530, 800);

            var y = 16;
            var labelColor = Color.FromArgb(80, 80, 80);
            var labelFont = Font;
            var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);

            // === Section header ===
            var lblHeader = new Label
            {
                Text = "AI Provider",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, y),
                AutoSize = true
            };
            Controls.Add(lblHeader);
            y += 28;

            // === Provider ===
            var lblProvider = new Label
            {
                Text = "Provider:",
                Location = new Point(16, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _cmbProvider = new ComboBox
            {
                Location = new Point(120, y),
                Width = 260,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (var key in LlmModels.AllProviderKeys)
                _cmbProvider.Items.Add(new ProviderItem(key));
            _cmbProvider.SelectedIndexChanged += OnProviderChanged;
            Controls.Add(lblProvider);
            Controls.Add(_cmbProvider);
            y += 32;

            // === Model ===
            var lblModel = new Label
            {
                Text = "Model:",
                Location = new Point(16, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _cmbModel = new ComboBox
            {
                Location = new Point(120, y),
                Width = 260,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            Controls.Add(lblModel);
            Controls.Add(_cmbModel);
            y += 32;

            // === View supported models link ===
            var lnkViewModels = new LinkLabel
            {
                Text = "View all supported models...",
                Location = new Point(120, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                LinkColor = Color.FromArgb(0, 102, 153)
            };
            lnkViewModels.LinkClicked += (s, e) =>
            {
                using (var dlg = new SupportedModelsDialog())
                    dlg.ShowDialog(this);
            };
            Controls.Add(lnkViewModels);
            y += 24;

            // === Custom model ID (optional — overrides the dropdown) ===
            var lblCustomModelId = new Label
            {
                // Must be short enough to clear the x=16..120 label column with
                // real margin — at the panel font + Windows display scaling,
                // "Custom model:" (~89px) still ran into the textbox. "Model ID:"
                // (~58px) keeps ~45px+ clearance at any scale. The hint label and
                // tooltip carry the "custom / overrides the dropdown" meaning.
                Text = "Model ID:",
                Location = new Point(16, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtCustomModelId = new TextBox
            {
                Location = new Point(120, y),
                Width = 260
            };
            var lblCustomModelHint = new Label
            {
                Text = "(optional – overrides the dropdown above)",
                Location = new Point(388, y + 3),
                AutoSize = true,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 8f, FontStyle.Italic)
            };
            var ttCustomModel = new ToolTip();
            ttCustomModel.SetToolTip(_txtCustomModelId,
                "Enter an exact model ID to use a model that isn't in the dropdown " +
                "(e.g. a new release, a preview model, or an OpenRouter router such " +
                "as \"openrouter/free\"). Leave blank to use the model selected above.");
            Controls.Add(lblCustomModelId);
            Controls.Add(_txtCustomModelId);
            Controls.Add(lblCustomModelHint);
            y += 32;

            // === API Key ===
            var lblApiKey = new Label
            {
                Text = "API Key:",
                Location = new Point(16, y + 3),
                AutoSize = true,
                ForeColor = labelColor
            };
            _btnShowKey = new Button
            {
                Text = "Show",
                // 80px design-time width (was 50). After AutoScaleMode.Dpi
                // scales the control at >100% Windows display scaling, 50
                // wasn't wide enough for the "Show" text – it was clipping
                // to "Sho". 80 leaves comfortable padding at any DPI.
                Width = 80,
                Height = 23,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnShowKey.FlatAppearance.BorderSize = 0;
            _btnShowKey.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnShowKey.Click += OnShowKeyClick;

            _txtApiKey = new TextBox
            {
                Location = new Point(120, y),
                UseSystemPasswordChar = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblApiKey);
            Controls.Add(_txtApiKey);
            Controls.Add(_btnShowKey);
            y += 32;

            // === Test Connection + Status ===
            _btnTestConnection = new Button
            {
                Text = "Test Connection",
                // 160px design-time width (was 120). After AutoScaleMode.Dpi
                // scales the control at >100% Windows display scaling, 120
                // wasn't wide enough for "Test Connection" – the text wrapped
                // to two lines. 160 keeps it on one line at any DPI.
                Width = 160,
                Height = 26,
                Location = new Point(120, y),
                FlatStyle = FlatStyle.System
            };
            _btnTestConnection.Click += OnTestConnectionClick;

            _lblStatus = new Label
            {
                Text = "",
                // Sit past the Test Connection button's right edge — at x=250 the
                // label started underneath the 160px-wide button (which is in
                // front in z-order), clipping the start of "Connected".
                Location = new Point(_btnTestConnection.Right + 8, y + 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(_btnTestConnection);
            Controls.Add(_lblStatus);
            y += 40;

            // === Ollama section ===
            // Controls placed directly on the UserControl (not in a child Panel)
            // to avoid clipping issues with right-anchored controls.
            _pnlOllama = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(400, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };

            var lblOllamaEndpoint = new Label
            {
                Text = "Endpoint:",
                Location = new Point(16, 8),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtOllamaEndpoint = new TextBox
            {
                Location = new Point(120, 5),
                Size = new Size(_pnlOllama.Width - 120 - 20, 20),
                Text = "http://localhost:11434",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblOllamaTimeout = new Label
            {
                Text = "Timeout (min):",
                Location = new Point(16, 38),
                AutoSize = true,
                ForeColor = labelColor
            };
            _nudOllamaTimeout = new NumericUpDown
            {
                Location = new Point(135, 37),
                Width = 75,
                Minimum = 0,
                Maximum = 120,
                Value = 0,
                Margin = new Padding(0, 0, 5, 0)
            };
            _lblOllamaTimeoutHint = new Label
            {
                Text = "0 = auto (3\u201310 min, based on model size)",
                Location = new Point(217, 40),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8f)
            };

            _pnlOllama.Controls.Add(lblOllamaEndpoint);
            _pnlOllama.Controls.Add(_txtOllamaEndpoint);
            _pnlOllama.Controls.Add(lblOllamaTimeout);
            _pnlOllama.Controls.Add(_nudOllamaTimeout);
            _pnlOllama.Controls.Add(_lblOllamaTimeoutHint);
            Controls.Add(_pnlOllama);

            // === Custom OpenAI section ===
            _pnlCustom = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(400, 150),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Visible = false
            };

            var sepCustom = new Label
            {
                Location = new Point(16, 0),
                Height = 1,
                BorderStyle = BorderStyle.Fixed3D,
                Width = 360,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblCustomHeader = new Label
            {
                Text = "Custom OpenAI-Compatible Endpoint",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, 8),
                AutoSize = true
            };

            var lblProfile = new Label
            {
                Text = "Profile:",
                Location = new Point(16, 38),
                AutoSize = true,
                ForeColor = labelColor
            };
            _cmbCustomProfile = new ComboBox
            {
                Location = new Point(120, 35),
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _cmbCustomProfile.SelectedIndexChanged += OnCustomProfileChanged;

            _btnAddProfile = new Button
            {
                Text = "+",
                Width = 26, Height = 26,
                Location = new Point(306, 33),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnAddProfile.FlatAppearance.BorderSize = 0;
            _btnAddProfile.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnAddProfile.Click += OnAddProfileClick;

            _btnRemoveProfile = new Button
            {
                Text = "\u2212",
                Width = 26, Height = 26,
                Location = new Point(334, 33),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnRemoveProfile.FlatAppearance.BorderSize = 0;
            _btnRemoveProfile.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnRemoveProfile.Click += OnRemoveProfileClick;

            _btnRenameProfile = new Button
            {
                Text = "\u270E",
                Width = 26, Height = 26,
                Location = new Point(362, 33),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnRenameProfile.FlatAppearance.BorderSize = 0;
            _btnRenameProfile.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnRenameProfile.Click += OnRenameProfileClick;
            new ToolTip().SetToolTip(_btnRenameProfile, "Rename this endpoint");

            var lblCustomEndpoint = new Label
            {
                Text = "Endpoint:",
                Location = new Point(16, 68),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtCustomEndpoint = new TextBox
            {
                Location = new Point(120, 65),
                Width = 260,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblCustomModel = new Label
            {
                Text = "Model:",
                Location = new Point(16, 98),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtCustomModel = new TextBox
            {
                Location = new Point(120, 95),
                Width = 260,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblCustomApiKey = new Label
            {
                Text = "API Key:",
                Location = new Point(16, 128),
                AutoSize = true,
                ForeColor = labelColor
            };
            _txtCustomApiKey = new TextBox
            {
                Location = new Point(120, 125),
                Width = 210,
                UseSystemPasswordChar = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _btnShowCustomKey = new Button
            {
                Text = "Show",
                Width = 50,
                Height = 23,
                Location = new Point(336, 125),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(80, 80, 80),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _btnShowCustomKey.FlatAppearance.BorderSize = 0;
            _btnShowCustomKey.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 220, 220);
            _btnShowCustomKey.Click += OnShowCustomKeyClick;

            _pnlCustom.Controls.AddRange(new Control[]
            {
                sepCustom, lblCustomHeader,
                lblProfile, _cmbCustomProfile, _btnAddProfile, _btnRemoveProfile, _btnRenameProfile,
                lblCustomEndpoint, _txtCustomEndpoint,
                lblCustomModel, _txtCustomModel,
                lblCustomApiKey, _txtCustomApiKey, _btnShowCustomKey
            });
            Controls.Add(_pnlCustom);

            // Store base Y for dynamic repositioning
            _providerSectionY = y;

            // === AI Context section – all AI features ===
            _lblAiContextHeader = new Label
            {
                Text = "AI context (Batch operations, Chat and QuickLauncher)",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true
            };
            Controls.Add(_lblAiContextHeader);

            _chkIncludeDocumentContext = new CheckBox
            {
                Text = "Include full document content in AI context",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = true
            };
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
            Controls.Add(_chkIncludeDocumentContext);

            _lblMaxSegments = new Label
            {
                Text = "Max segments:",
                Location = new Point(36, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor
            };
            Controls.Add(_lblMaxSegments);

            _nudMaxSegments = new NumericUpDown
            {
                Location = new Point(160, 0), // positioned dynamically
                Width = 80,
                Minimum = 100,
                Maximum = 2000,
                Value = 500,
                Increment = 100
            };
            var maxSegTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            maxSegTip.SetToolTip(_nudMaxSegments,
                "Maximum number of source segments to include in the AI prompt.\r\n" +
                "Documents larger than this will be truncated (first 80% + last 20%).");
            Controls.Add(_nudMaxSegments);

            _chkIncludeTermMetadata = new CheckBox
            {
                Text = "Include term definitions and domains",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = true
            };
            var metaTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            metaTip.SetToolTip(_chkIncludeTermMetadata,
                "When enabled, term definitions, domains, and notes are included\r\n" +
                "alongside matched terminology in the AI prompt.\r\n" +
                "Applies to Chat, QuickLauncher, and Batch Operations.");
            Controls.Add(_chkIncludeTermMetadata);

            _chkIncludeSuperMemory = new CheckBox
            {
                Text = "Include SuperMemory knowledge base in AI context",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = true
            };
            var smTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            smTip.SetToolTip(_chkIncludeSuperMemory,
                "When enabled, relevant SuperMemory articles (client profiles, domain\r\n" +
                "knowledge, style guides, terminology decisions) are automatically\r\n" +
                "loaded and included in AI prompts for translations and chat.");
            _chkIncludeSuperMemory.CheckedChanged += (s, ev) =>
            {
                _chkIncludeSuperMemoryAutoPrompt.Enabled = _chkIncludeSuperMemory.Checked;
            };
            Controls.Add(_chkIncludeSuperMemory);

            _chkIncludeSuperMemoryAutoPrompt = new CheckBox
            {
                Text = "Use knowledge base when generating prompts (AutoPrompt)",
                Location = new Point(36, 0), // indented, positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = true
            };
            var apTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            apTip.SetToolTip(_chkIncludeSuperMemoryAutoPrompt,
                "When enabled, AutoPrompt includes your SuperMemory articles\r\n" +
                "(client conventions, terminology reasoning, style guides) in\r\n" +
                "the meta-prompt so that generated prompts reflect your\r\n" +
                "established knowledge base from the start.");
            Controls.Add(_chkIncludeSuperMemoryAutoPrompt);

            _chkLogPrompts = new CheckBox
            {
                Text = "Log prompts and responses to Reports tab",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = false
            };
            var logTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            logTip.SetToolTip(_chkLogPrompts,
                "When enabled, every AI API call is logged to the Reports tab with\r\n" +
                "the full prompt, response, estimated token counts, and cost.\r\n" +
                "Useful for monitoring costs and debugging prompt behaviour.");
            Controls.Add(_chkLogPrompts);

            _lblBatchSize = new Label
            {
                Text = "Batch size:",
                Location = new Point(16, 0),
                AutoSize = true,
                ForeColor = labelColor
            };
            Controls.Add(_lblBatchSize);

            _nudBatchSize = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 100,
                Value = 20,
                // 80px gives the digits enough room after AutoScaleMode.Dpi
                // scales the control at >100% Windows display scaling – at
                // 60px the system spinner buttons take up most of the width.
                Width = 80,
                Location = new Point(160, 0)
            };
            var batchTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            batchTip.SetToolTip(_nudBatchSize,
                "Number of segments sent to the AI provider per API call during\r\n" +
                "Batch Translate and Batch Proofread. Lower values are safer;\r\n" +
                "higher values reduce cost and improve cross-segment consistency.");
            Controls.Add(_nudBatchSize);

            // Termbase AI inclusion is now chosen on the Termbases tab (the "AI"
            // column in the termbase grid), so the user controls term recognition
            // and AI inclusion in one place. This is just a pointer to that.
            _lblAiTermbases = new Label
            {
                Text = "Termbases included in AI prompts are chosen on the Termbases tab – " +
                       "tick the “AI” column for each termbase the AI should see.",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor
            };
            Controls.Add(_lblAiTermbases);

            // === AI Context section – Chat & QuickLauncher only ===
            _lblChatContextHeader = new Label
            {
                Text = "AI context (Chat and QuickLauncher)",
                Font = headerFont,
                ForeColor = Color.FromArgb(50, 50, 50),
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true
            };
            Controls.Add(_lblChatContextHeader);

            _lblChatContextNote = new Label
            {
                Text = "Most of these apply to Chat and QuickLauncher only \u2013 see each tooltip for exceptions.",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = Color.FromArgb(130, 130, 130),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic)
            };
            Controls.Add(_lblChatContextNote);

            _chkIncludeTmMatches = new CheckBox
            {
                Text = "Include TM matches in AI context (Chat, QuickLauncher, AutoPrompt)",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = true
            };
            var tmTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300 };
            tmTip.SetToolTip(_chkIncludeTmMatches,
                "When enabled, the AI gets translation reference pairs in two ways:\r\n" +
                "\r\n" +
                "  \u2022 Chat / QuickLauncher: live TM lookups for the active segment \u2013 fuzzy\r\n" +
                "    and exact matches from your project TMs are included as references.\r\n" +
                "\r\n" +
                "  \u2022 AutoPrompt (Batch Operations): up to 50 already-translated, human-\r\n" +
                "    confirmed segment pairs are sampled evenly from the active document\r\n" +
                "    and included as in-project translation examples. Includes 100% / exact\r\n" +
                "    matches that have been applied and confirmed, fuzzy-and-edited\r\n" +
                "    segments, and segments translated from scratch \u2013 anything with a\r\n" +
                "    Translated / ApprovedTranslation / ApprovedSignOff confirmation level.\r\n" +
                "\r\n" +
                "Other Batch Operations (Translate, Proofread) are unaffected by this\r\n" +
                "checkbox \u2013 they always work segment-by-segment without TM reference\r\n" +
                "pairs.");
            Controls.Add(_chkIncludeTmMatches);

            _chkDemoMode = new CheckBox
            {
                Text = "Incognito mode \u2014 anonymise project names, paths, and personal data in AI responses",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor,
                Checked = false
            };
            var demoTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            demoTip.SetToolTip(_chkDemoMode,
                "When enabled, the AI replaces all project names, file paths, TM names,\r\n" +
                "and other identifying data with anonymised placeholders in its responses.\r\n" +
                "Useful for screen sharing, recording demos, posting screenshots in forums,\r\n" +
                "or any situation where you need to keep client data confidential.\r\n" +
                "Toggle on before sharing, toggle off when done.");
            Controls.Add(_chkDemoMode);

            _lblSurroundingSegments = new Label
            {
                Text = "Surrounding segments:",
                Location = new Point(36, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor
            };
            Controls.Add(_lblSurroundingSegments);

            _nudSurroundingSegments = new NumericUpDown
            {
                Location = new Point(210, 0), // positioned dynamically – label is wider than "Max segments:"
                // 80px gives the digits enough room after AutoScaleMode.Dpi
                // scales the control at >100% Windows display scaling – at
                // 60px the system spinner buttons take up most of the width.
                Width = 80,
                Minimum = 1,
                Maximum = 20,
                Value = 5,
                Increment = 1
            };
            var surroundingTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            surroundingTip.SetToolTip(_nudSurroundingSegments,
                "Number of segments before and after the active segment to include\r\n" +
                "in {{SURROUNDING_SEGMENTS}} QuickLauncher prompts and the AI Assistant\r\n" +
                "chat context. Default: 5 (five segments on each side).\r\n" +
                "Only applies to Chat and QuickLauncher \u2013 not to Batch Operations.");
            Controls.Add(_nudSurroundingSegments);

            _lblQuickLauncherTarget = new Label
            {
                Text = "QuickLauncher prompts go to:",
                Location = new Point(36, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = labelColor
            };
            Controls.Add(_lblQuickLauncherTarget);

            _cmbQuickLauncherTarget = new ComboBox
            {
                Location = new Point(210, 0), // positioned dynamically
                Width = 220,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbQuickLauncherTarget.Items.Add("In-Trados AI Assistant");
            // Display string was "Workbench Sidekick" through Trados v4.19.x \u2013
            // renamed to "Workbench Chat" after Supervertaler Workbench v1.10.4
            // retired the Sidekick floating window and the Chat surface was
            // promoted into Workbench itself. The persisted setting value
            // (settings.QuickLauncherTarget == "WorkbenchSidekick") is kept
            // unchanged on disk so existing users' saved preferences still
            // resolve \u2013 it's an internal identifier, never user-visible.
            _cmbQuickLauncherTarget.Items.Add("Workbench Chat");
            _cmbQuickLauncherTarget.SelectedIndex = 0;
            var qlTargetTip = new ToolTip { AutoPopDelay = 10000, InitialDelay = 300 };
            qlTargetTip.SetToolTip(_cmbQuickLauncherTarget,
                "Where Ctrl+Q QuickLauncher prompts are run.\r\n" +
                "\r\n" +
                "  In-Trados AI Assistant \u2013 default. Prompt + response stay in the\r\n" +
                "  Trados Assistant chat panel.\r\n" +
                "\r\n" +
                "  Workbench Chat \u2013 posts the prompt to Supervertaler Workbench's\r\n" +
                "  AI tab \u2192 Chat sub-tab over a localhost bridge. Useful when you\r\n" +
                "  prefer a larger chat window for the response, or want both\r\n" +
                "  products' chat history in one place. Falls back to the in-Trados\r\n" +
                "  Assistant if Workbench isn't running.");
            Controls.Add(_cmbQuickLauncherTarget);

            // === Info label ===
            _lblInfo = new Label
            {
                Text = "API keys are stored locally and never sent anywhere except to the selected provider.",
                Location = new Point(16, 0), // positioned dynamically
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic)
            };
            Controls.Add(_lblInfo);

            ResumeLayout(false);

            // Initial layout of dynamic controls
            RepositionAiContextSection();

            // Layout adjustments that depend on parent width
            Resize += (s, e) =>
            {
                LayoutApiKeyRow();
                LayoutTermbasesList();
                LayoutProviderModelRows();
            };
            LayoutApiKeyRow();
        }

        /// <summary>
        /// Repositions the AI Context section below the currently visible provider panel,
        /// eliminating empty space when Ollama/Custom panels are hidden.
        /// </summary>
        private void RepositionAiContextSection()
        {
            var y = _providerSectionY;

            // Add height of the visible provider panel
            if (_pnlOllama.Visible)
                y += _pnlOllama.Height + 8;
            else if (_pnlCustom.Visible)
                y += _pnlCustom.Height + 8;
            else
                y += 8; // small gap after Test Connection when no provider panel

            // ── Section 1: AI Context (all AI features) ──
            _lblAiContextHeader.Location = new Point(16, y);
            y += 26;

            _chkIncludeDocumentContext.Location = new Point(16, y);
            y += 24;

            _lblMaxSegments.Location = new Point(36, y + 3);
            _nudMaxSegments.Location = new Point(160, y);
            y += 30;

            _chkIncludeTermMetadata.Location = new Point(16, y);
            y += 24;

            _chkIncludeSuperMemory.Location = new Point(16, y);
            y += 24;

            _chkIncludeSuperMemoryAutoPrompt.Location = new Point(36, y);
            y += 28;

            _chkLogPrompts.Location = new Point(16, y);
            y += 28;

            _lblBatchSize.Location = new Point(16, y + 3);
            _nudBatchSize.Location = new Point(160, y);
            y += 30;

            _lblAiTermbases.Location = new Point(16, y);
            y += 28;

            // ── Section 2: AI Context (Chat & QuickLauncher only) ──
            _lblChatContextHeader.Location = new Point(16, y);
            y += 22;

            _lblChatContextNote.Location = new Point(16, y);
            y += 20;

            _chkIncludeTmMatches.Location = new Point(16, y);
            y += 24;

            _chkDemoMode.Location = new Point(16, y);
            y += 24;

            _lblSurroundingSegments.Location = new Point(36, y + 3);
            // Position the NUD dynamically against the label's actual right
            // edge: at 150% Windows scaling the AutoSize label "Surrounding
            // segments:" is wider than the fixed x=210 column the other rows
            // use, so a hard-coded x would put the NUD on top of the label.
            _nudSurroundingSegments.Location = new Point(_lblSurroundingSegments.Right + 8, y);
            y += 34;

            // The label "QuickLauncher prompts go to:" is wider than the
            // shared x=210 column the other rows use, so the combo is
            // anchored to the label's right edge instead of a fixed x to
            // avoid the label clipping into the combo's leading text.
            _lblQuickLauncherTarget.Location = new Point(36, y + 3);
            _cmbQuickLauncherTarget.Location = new Point(_lblQuickLauncherTarget.Right + 8, y);
            y += 34;

            _lblInfo.Location = new Point(16, y);
        }

        private void LayoutApiKeyRow()
        {
            if (_txtApiKey == null || _btnShowKey == null) return;
            // Use ClientSize.Width, NOT Width: when AutoScroll's vertical
            // scrollbar appears (which happens because the AI Context section
            // is taller than the panel), Width still reports the full panel
            // size but ClientSize.Width is reduced by the scrollbar width.
            // Using Width here makes the Show button slide under the scrollbar.
            _btnShowKey.Location = new Point(ClientSize.Width - 16 - _btnShowKey.Width, _txtApiKey.Top);
            _txtApiKey.Width = _btnShowKey.Left - _txtApiKey.Left - 6;
        }

        private void LayoutTermbasesList()
        {
            // Termbase AI inclusion is now chosen on the Termbases tab (the "AI" column);
            // the in-panel checklist has been retired, so there is nothing to lay out here.
        }

        private void LayoutProviderModelRows()
        {
            if (_cmbProvider == null || _cmbModel == null) return;
            var rightMargin = 16;
            // Use ClientSize.Width (not Width) so the dropdowns don't extend
            // under the AutoScroll vertical scrollbar when the AI Context
            // section overflows the panel.
            var w = Math.Max(120, ClientSize.Width - _cmbProvider.Left - rightMargin);
            _cmbProvider.Width = w;
            _cmbModel.Width = w;
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

            // Show/hide provider-specific sections
            _pnlOllama.Visible = providerKey == LlmModels.ProviderOllama;
            _pnlCustom.Visible = providerKey == LlmModels.ProviderCustomOpenAi;

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

            // Reposition AI Context section based on visible provider panel
            RepositionAiContextSection();

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
                        _lblStatus.Text = "\u2713 Connected";
                        _lblStatus.ForeColor = Color.FromArgb(30, 130, 60);
                    }
                    else
                    {
                        _lblStatus.Text = $"\u2717 {error}";
                        _lblStatus.ForeColor = Color.FromArgb(180, 60, 60);
                    }
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = $"\u2717 {ex.Message}";
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
