using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
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

        // API Key
        private TextBox _txtApiKey;
        private Button _btnShowKey;
        private Button _btnTestConnection;
        private Label _lblStatus;

        // Ollama section
        private Panel _pnlOllama;
        private TextBox _txtOllamaEndpoint;

        // Custom OpenAI section
        private Panel _pnlCustom;
        private ComboBox _cmbCustomProfile;
        private Button _btnAddProfile;
        private Button _btnRemoveProfile;
        private TextBox _txtCustomEndpoint;
        private TextBox _txtCustomModel;
        private TextBox _txtCustomApiKey;
        private Button _btnShowCustomKey;

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
            SuspendLayout();
            BackColor = Color.White;
            AutoScroll = true;

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
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
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
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(lblModel);
            Controls.Add(_cmbModel);
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
                Width = 50,
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
                Width = 120,
                Height = 26,
                Location = new Point(120, y),
                FlatStyle = FlatStyle.System
            };
            _btnTestConnection.Click += OnTestConnectionClick;

            _lblStatus = new Label
            {
                Text = "",
                Location = new Point(250, y + 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100),
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(_btnTestConnection);
            Controls.Add(_lblStatus);
            y += 40;

            // === Ollama section ===
            _pnlOllama = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(400, 50),
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
                Width = 260,
                Text = "http://localhost:11434",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _pnlOllama.Controls.Add(lblOllamaEndpoint);
            _pnlOllama.Controls.Add(_txtOllamaEndpoint);
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
                lblProfile, _cmbCustomProfile, _btnAddProfile, _btnRemoveProfile,
                lblCustomEndpoint, _txtCustomEndpoint,
                lblCustomModel, _txtCustomModel,
                lblCustomApiKey, _txtCustomApiKey, _btnShowCustomKey
            });
            Controls.Add(_pnlCustom);

            // === Info label ===
            var lblInfo = new Label
            {
                Text = "API keys are stored locally and never sent anywhere except to the selected provider.",
                Location = new Point(16, y + 160),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 150, 150),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Italic)
            };
            Controls.Add(lblInfo);

            ResumeLayout(false);

            // Layout adjustments that depend on parent width
            Resize += (s, e) => LayoutApiKeyRow();
            LayoutApiKeyRow();
        }

        private void LayoutApiKeyRow()
        {
            if (_txtApiKey == null || _btnShowKey == null) return;
            _btnShowKey.Location = new Point(Width - 16 - _btnShowKey.Width, _txtApiKey.Top);
            _txtApiKey.Width = _btnShowKey.Left - _txtApiKey.Left - 6;
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

            // Custom OpenAI profiles
            PopulateCustomProfiles(settings);
        }

        public void ApplyToSettings(AiSettings settings)
        {
            if (settings == null) return;

            var provider = GetSelectedProviderKey();
            settings.SelectedProvider = provider;

            // Model
            var selectedModel = _cmbModel.SelectedItem as ModelItem;
            switch (provider)
            {
                case LlmModels.ProviderOpenAi:
                    settings.OpenAiModel = selectedModel?.Id ?? "gpt-4o";
                    break;
                case LlmModels.ProviderClaude:
                    settings.ClaudeModel = selectedModel?.Id ?? "claude-sonnet-4-6";
                    break;
                case LlmModels.ProviderGemini:
                    settings.GeminiModel = selectedModel?.Id ?? "gemini-2.5-flash";
                    break;
                case LlmModels.ProviderOllama:
                    settings.OllamaModel = selectedModel?.Id ?? "translategemma:12b";
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
            settings.ApiKeys.CustomOpenAi = _providerApiKeys.TryGetValue(LlmModels.ProviderCustomOpenAi, out val) ? val : "";

            // Ollama endpoint
            settings.OllamaEndpoint = _txtOllamaEndpoint.Text.Trim();

            // Custom OpenAI profiles — save current profile values first
            SaveCurrentCustomProfile(settings);
            settings.SelectedCustomProfileName = (_cmbCustomProfile.SelectedItem as CustomProfileItem)?.Name ?? "";
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
            return (_cmbModel.SelectedItem as ModelItem)?.Id ?? "gpt-4o";
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
                case LlmModels.ProviderOllama: targetId = settings.OllamaModel; break;
                default: return;
            }

            for (int i = 0; i < _cmbModel.Items.Count; i++)
            {
                if (((ModelItem)_cmbModel.Items[i]).Id == targetId)
                {
                    _cmbModel.SelectedIndex = i;
                    return;
                }
            }
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
                _display = $"{info.DisplayName}  —  {info.Description}";
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
