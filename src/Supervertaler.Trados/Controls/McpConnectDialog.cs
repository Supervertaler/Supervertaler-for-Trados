using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// "Connect AI assistant" dialog – guides the user through connecting an
    /// MCP-capable AI app (Claude Desktop, ChatGPT desktop, …) to the live
    /// Trados session via the Supervertaler MCP Server.
    ///
    /// Deliberately does NOT edit other apps' config files: the supported
    /// path is the .mcpb extension (installed via Claude Desktop's
    /// Desktop), with a copy-paste JSON snippet for other MCP clients.
    /// </summary>
    public class McpConnectDialog : Form
    {
        private const string DownloadUrl =
            "https://github.com/Supervertaler/Supervertaler-for-Trados/releases/latest";
        private const string DocsUrl =
            "https://docs.supervertaler.com/trados/";

        public McpConnectDialog()
        {
            Text = "Connect AI assistant – Supervertaler MCP Server";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(UiScale.Pixels(560), UiScale.Pixels(430));

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(UiScale.Pixels(14)),
                AutoScroll = true
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var intro = new Label
            {
                Text = "The Supervertaler MCP Server lets AI assistants talk directly to your live " +
                       "Trados Studio session: ask about the open project, browse segments, search " +
                       "your TMs and termbases, and insert translations – all from a chat window.\r\n\r\n" +
                       "Everything stays on this computer: the connection is local-only and " +
                       "token-protected.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };
            root.Controls.Add(intro);

            // ── Status ────────────────────────────────────────────────────
            root.Controls.Add(SectionHeader("Status"));

            var claudeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            bool claudeInstalled = Directory.Exists(claudeDir);
            bool extensionInstalled = false;
            try
            {
                var extRoot = Path.Combine(claudeDir, "Claude Extensions");
                extensionInstalled = Directory.Exists(extRoot) &&
                    Directory.GetDirectories(extRoot, "*supervertaler-mcp-server*").Any();
            }
            catch { /* status is best-effort */ }

            // A hand-written mcpServers entry in Claude Desktop's config file is
            // an equally valid connection (typical for developers/power users).
            // Detect it so the dialog doesn't claim "not connected", and so we
            // can warn when BOTH paths are active (= duplicate tools in Claude).
            bool manualConfigEntry = false;
            try
            {
                var cfgPath = Path.Combine(claudeDir, "claude_desktop_config.json");
                manualConfigEntry = File.Exists(cfgPath) &&
                    File.ReadAllText(cfgPath).IndexOf(
                        "SupervertalerMcpServer", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { /* status is best-effort */ }

            bool bridgeUp = false;
            try { bridgeUp = File.Exists(UserDataPath.SupervertalerBridgeFile); } catch { }

            root.Controls.Add(StatusLine(bridgeUp,
                bridgeUp
                    ? "Supervertaler bridge is running in this Trados session."
                    : "Supervertaler bridge not running yet – it starts when you open a document " +
                      "in the editor."));
            root.Controls.Add(StatusLine(claudeInstalled,
                claudeInstalled ? "Claude Desktop detected on this computer."
                                : "Claude Desktop not detected (claude.ai/download)."));

            if (extensionInstalled && manualConfigEntry)
            {
                var warn = StatusLine(false,
                    "Connected twice: the extension is installed AND Claude Desktop's config file has a " +
                    "manual Supervertaler entry. Claude will show every tool twice – remove one of the two " +
                    "(usually the manual entry in claude_desktop_config.json).");
                warn.ForeColor = Color.FromArgb(190, 110, 0);
                root.Controls.Add(warn);
            }
            else if (extensionInstalled)
            {
                root.Controls.Add(StatusLine(true,
                    "Supervertaler MCP Server extension is installed in Claude Desktop."));
            }
            else if (manualConfigEntry)
            {
                root.Controls.Add(StatusLine(true,
                    "Connected via a manual entry in Claude Desktop's config file (no extension needed – " +
                    "don't also install the extension, or every tool will appear twice)."));
            }
            else
            {
                root.Controls.Add(StatusLine(false,
                    "Supervertaler MCP Server extension not installed yet."));
            }

            // Version handshake: only shown once an AI app has actually connected
            // this session (LastSeenExeVersion > 0). Outdated = the exe predates a
            // feature this plugin needs; the AI also relays the same nudge in chat.
            if (Core.SupervertalerBridge.LastSeenExeVersion > 0)
            {
                if (Core.SupervertalerBridge.ExeOutdated)
                {
                    var old = StatusLine(false,
                        "Your MCP extension is outdated for this plugin version – download the latest " +
                        "below and reinstall it in your AI app.");
                    old.ForeColor = Color.FromArgb(190, 110, 0);
                    root.Controls.Add(old);
                }
                else
                {
                    root.Controls.Add(StatusLine(true,
                        "An AI app has connected this session – extension version is up to date."));
                }
            }

            // ── Claude Desktop (recommended) ─────────────────────────────
            root.Controls.Add(SectionHeader("Claude Desktop (recommended)"));

            var stepHint = new Label
            {
                Text = "1.  Download the extension file (Supervertaler-MCP-Server.mcpb).\r\n" +
                       "2.  In Claude Desktop: Settings → Extensions → Advanced settings → Install extension…\r\n" +
                       "3.  Restart Claude Desktop and ask: \"What's the status of my Trados project?\"",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(stepHint);

            var btnDownload = new Button
            {
                Text = "Download extension (.mcpb)…",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };
            btnDownload.Click += (s, e) => OpenUrl(DownloadUrl);
            root.Controls.Add(btnDownload);

            // ── Other MCP-capable AI apps ─────────────────────────────────
            root.Controls.Add(SectionHeader("Other AI apps (ChatGPT desktop, Claude Code, …)"));

            var manualHint = new Label
            {
                Text = "Point the app's MCP configuration at SupervertalerMcpServer.exe. The button " +
                       "below copies a ready-made JSON snippet – paste it into the app's MCP config " +
                       "and adjust the path to where you saved the exe. See the documentation for " +
                       "per-app instructions.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(manualHint);

            var manualHost = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0)
            };
            var btnCopy = new Button { Text = "Copy config snippet", AutoSize = true };
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(
"{\r\n" +
"  \"mcpServers\": {\r\n" +
"    \"Supervertaler MCP Server\": {\r\n" +
"      \"command\": \"C:\\\\path\\\\to\\\\SupervertalerMcpServer.exe\"\r\n" +
"    }\r\n" +
"  }\r\n" +
"}");
                    btnCopy.Text = "Copied!";
                }
                catch { /* clipboard can be locked by another app */ }
            };
            var btnDocs = new Button { Text = "Open documentation", AutoSize = true };
            btnDocs.Click += (s, e) => OpenUrl(DocsUrl);
            manualHost.Controls.Add(btnCopy);
            manualHost.Controls.Add(btnDocs);
            root.Controls.Add(manualHost);

            // ── Close ─────────────────────────────────────────────────────
            var btnClose = new Button
            {
                Text = "Close",
                AutoSize = true,
                DialogResult = DialogResult.OK,
                Margin = new Padding(0, UiScale.Pixels(12), 0, 0)
            };
            root.Controls.Add(btnClose);
            AcceptButton = btnClose;
            CancelButton = btnClose;

            Controls.Add(root);
        }

        private static Label SectionHeader(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Margin = new Padding(0, UiScale.Pixels(8), 0, UiScale.Pixels(4))
            };
        }

        private static Label StatusLine(bool ok, string text)
        {
            return new Label
            {
                Text = (ok ? "✓  " : "•  ") + text,
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                ForeColor = ok ? Color.FromArgb(0, 130, 0) : Color.FromArgb(120, 120, 120),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(2))
            };
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open browser: " + ex.Message + "\r\n\r\n" + url,
                    "Supervertaler", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
