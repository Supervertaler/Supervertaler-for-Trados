using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Settings tab for GroupShare server credentials (issue #35). Studio does
    /// not expose its own credential store to plugin code, so the user enters the
    /// GroupShare login once here; the password is DPAPI-encrypted at rest.
    ///
    /// The UI edits a single server (the common case), but the underlying model
    /// (<see cref="TermLensSettings.GroupShareServers"/>) is a list so multi-server
    /// support can be added later without a data migration. Used by SuperSearch
    /// today; intended to also back GroupShare-aware batch / AutoPrompt / termbase
    /// features later, which is why it lives in Settings rather than in SuperSearch.
    /// </summary>
    public class GroupShareSettingsPanel : UserControl
    {
        private readonly TextBox _txtUrl;
        private readonly ComboBox _cboAuthMode;
        private readonly TextBox _txtUser;
        private readonly TextBox _txtPassword;

        // Combo index 0 = GroupShare/SDL auth, 1 = Windows/AD auth.
        private const int AuthIdxGroupShare = 0;
        private const int AuthIdxWindows = 1;

        public GroupShareSettingsPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            AutoScroll = true;
            Font = new Font("Segoe UI", UiScale.FontSize(9f));
            Padding = new Padding(UiScale.Pixels(16), UiScale.Pixels(12), UiScale.Pixels(16), UiScale.Pixels(12));

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pixels(110)));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label Header(string text, bool first = false) => new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", UiScale.FontSize(9f), FontStyle.Bold),
                Margin = new Padding(0, UiScale.Pixels(first ? 0 : 14), 0, UiScale.Pixels(6))
            };
            Label FieldLabel(string text) => new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, UiScale.Pixels(8), UiScale.Pixels(8), UiScale.Pixels(7))
            };
            Label Hint(string text) => new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(0, UiScale.Pixels(2), 0, UiScale.Pixels(8))
            };
            TextBox Box(bool password = false) => new TextBox
            {
                Width = UiScale.Pixels(340),
                UseSystemPasswordChar = password,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4))
            };

            _txtUrl = Box();
            _txtUser = Box();
            _txtPassword = Box(password: true);

            _cboAuthMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = UiScale.Pixels(340),
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, UiScale.Pixels(4), 0, UiScale.Pixels(4))
            };
            _cboAuthMode.Items.Add("GroupShare Authentication");
            _cboAuthMode.Items.Add("Windows Authentication");
            _cboAuthMode.SelectedIndex = AuthIdxGroupShare;

            int row = 0;
            void AddSpan(Control c) { layout.Controls.Add(c, 0, row); layout.SetColumnSpan(c, 2); row++; }
            void AddField(string label, Control c) { layout.Controls.Add(FieldLabel(label), 0, row); layout.Controls.Add(c, 1, row); row++; }

            AddSpan(Header("GroupShare server", first: true));
            AddSpan(Hint("Enter your GroupShare login once. SuperSearch uses it to search server-based\r\n"
                       + "translation memories. The password is encrypted (Windows DPAPI) and stored\r\n"
                       + "only on this machine. Leave all fields blank to remove the stored login."));
            AddField("Server URL:", _txtUrl);
            layout.Controls.Add(Hint("e.g. https://groupshare.example.com/"), 1, row); row++;
            AddField("Login provider:", _cboAuthMode);
            AddField("Username:", _txtUser);
            AddField("Password:", _txtPassword);
            layout.Controls.Add(Hint("For Windows (AD) authentication, choose Windows Authentication and enter\r\n"
                       + "your AD username and password."), 1, row); row++;

            Controls.Add(layout);
        }

        /// <summary>Loads the first stored GroupShare server into the fields.</summary>
        public void PopulateFromSettings(TermLensSettings settings)
        {
            var gs = settings?.GroupShareServers?.FirstOrDefault();
            _txtUrl.Text = gs?.BaseUrl ?? "";
            _cboAuthMode.SelectedIndex =
                string.Equals(gs?.AuthMode, "Windows", StringComparison.OrdinalIgnoreCase)
                    ? AuthIdxWindows : AuthIdxGroupShare;
            _txtUser.Text = gs?.Username ?? "";
            _txtPassword.Text = gs != null ? DpapiSecret.Unprotect(gs.PasswordProtected) : "";
        }

        /// <summary>Writes the fields back into settings (password DPAPI-encrypted).</summary>
        public void ApplyToSettings(TermLensSettings settings)
        {
            if (settings == null) return;
            if (settings.GroupShareServers == null)
                settings.GroupShareServers = new List<GroupShareServerCredential>();

            var url = (_txtUrl.Text ?? "").Trim();
            var user = (_txtUser.Text ?? "").Trim();
            var pass = _txtPassword.Text ?? "";

            // All blank -> remove any stored server, keeping settings tidy.
            if (url.Length == 0 && user.Length == 0 && pass.Length == 0)
            {
                settings.GroupShareServers.Clear();
                return;
            }

            // Normalise the URL to a single trailing slash so host-matching is stable.
            if (url.Length > 0 && !url.EndsWith("/")) url += "/";

            var gs = settings.GroupShareServers.FirstOrDefault();
            if (gs == null)
            {
                gs = new GroupShareServerCredential();
                settings.GroupShareServers.Add(gs);
            }
            gs.BaseUrl = url;
            gs.AuthMode = _cboAuthMode.SelectedIndex == AuthIdxWindows ? "Windows" : "GroupShare";
            gs.Username = user;
            gs.PasswordProtected = DpapiSecret.Protect(pass);
        }
    }
}
