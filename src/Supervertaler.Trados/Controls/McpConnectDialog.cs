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
    /// Claude Desktop installs itself from a .mcpb bundle, so for that app this
    /// dialog only hands over the file. ChatGPT has no equivalent, so there the
    /// plugin does edit the app's config — see <see cref="Core.ChatGptMcpSetup"/>,
    /// which backs the file up and touches only its own block. Any other client
    /// gets a copy-paste snippet.
    /// </summary>
    public class McpConnectDialog : Form
    {
        private const string DownloadUrl =
            "https://github.com/Supervertaler/Supervertaler-for-Trados/releases/latest";
        private const string DocsUrl =
            "https://docs.supervertaler.com/trados/";

        public McpConnectDialog()
        {
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;
            Text = "Connect AI assistant – Supervertaler MCP Server";
            // Sizable rather than FixedDialog: this dialog has gained a section
            // per supported AI app, and a fixed height means each new one pushes
            // the buttons below the fold. It still scrolls, but scrolling to
            // reach a button you did not know was there is a poor default.
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;

            var preferred = new Size(UiScale.Pixels(560), UiScale.Pixels(660));
            MinimumSize = new Size(UiScale.Pixels(460), UiScale.Pixels(360));
            // Never taller than the screen it opens on. UiScale multiplies the
            // height, so on a small laptop at 150% the preferred size would
            // otherwise run off the bottom and hide the very buttons this
            // change exists to reveal.
            try
            {
                var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
                preferred.Height = Math.Min(preferred.Height, (int)(workingArea.Height * 0.9));
                preferred.Width = Math.Min(preferred.Width, (int)(workingArea.Width * 0.9));
            }
            catch { /* fall back to the unclamped preferred size */ }
            ClientSize = preferred;

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

            // Ask this session's listener, not the shared handshake file: with two
            // Studios open that file belongs to whichever started last, so the older
            // one reported the other's bridge as "this Trados session".
            bool bridgeUp = false;
            try { bridgeUp = AiAssistantViewPart.IsBridgeRunning; } catch { }

            root.Controls.Add(StatusLine(bridgeUp,
                bridgeUp
                    ? "Supervertaler bridge is running in this Trados session."
                    : "Supervertaler bridge not running yet – it starts on its own shortly after Trados " +
                      "Studio does; no document or panel needs to be open. If it stays like this, check " +
                      "that the AI Assistant is enabled in Settings, or restart Studio."));

            // A second Studio decides whether the AI will accept an edit at all, and
            // there is no way to tell from inside this one. Say so here rather than
            // letting the refusal be the user's first news of it.
            try
            {
                var others = Core.SupervertalerBridge.ListOtherLiveInstances();
                if (others.Count > 0)
                {
                    var note = StatusLine(false,
                        "Another Trados Studio is running with Supervertaler: "
                        + string.Join(", ", others.Select(o => o.Describe())) + ". "
                        + "AI apps can read from either, but will refuse to change segments until you say "
                        + "which one to use – ask the assistant to work with a named project or Studio "
                        + "version, or close the other Studio.");
                    note.ForeColor = Color.FromArgb(190, 110, 0);
                    root.Controls.Add(note);
                }
            }
            catch { /* status is best-effort */ }

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
                        "An AI app connected this session – server version is up to date. (Which app is not reported back to the plugin.)"));
                }
            }

            // Everything from here down is per-app. Without the sub-headings the
            // list reads as one verdict, so a user with ChatGPT set up and no
            // Claude sees four Claude-specific lines and no mention of the app
            // they actually use.
            root.Controls.Add(SubHeader("Claude Desktop"));

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

            // ── ChatGPT desktop status ────────────────────────────────────
            root.Controls.Add(SubHeader("ChatGPT desktop"));

            bool chatGptInstalled = Core.ChatGptMcpSetup.IsChatGptInstalled();
            bool chatGptConfigured = Core.ChatGptMcpSetup.IsConfigured();

            root.Controls.Add(StatusLine(chatGptInstalled,
                chatGptInstalled ? "ChatGPT desktop detected on this computer."
                                 : "ChatGPT desktop not detected."));
            root.Controls.Add(StatusLine(chatGptConfigured,
                chatGptConfigured
                    ? "Supervertaler MCP Server is registered in ChatGPT's configuration."
                    : "Not registered with ChatGPT yet – use the button below."));


            // ── Claude Desktop (recommended) ─────────────────────────────
            root.Controls.Add(SectionHeader("Claude Desktop (recommended)"));

            var stepHint = new Label
            {
                Text = extensionInstalled
                    ? "Replacing the installed extension (Claude Desktop cannot delete the server while it is running):\r\n" +
                      "1.  Download the new extension file (Supervertaler-MCP-Server.mcpb).\r\n" +
                      "2.  Quit Claude Desktop completely — closing the window is not enough, it keeps\r\n" +
                      "     running in the notification area. Then start it again.\r\n" +
                      "3.  Settings → Extensions → Advanced settings → Install extension…\r\n" +
                      "4.  Restart Claude Desktop and ask: \"What's the status of my Trados project?\""
                    : "1.  Download the extension file (Supervertaler-MCP-Server.mcpb).\r\n" +
                      "2.  In Claude Desktop: Settings → Extensions → Advanced settings → Install extension…\r\n" +
                      "3.  Restart Claude Desktop and ask: \"What's the status of my Trados project?\"",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(stepHint);

            if (extensionInstalled)
            {
                // Skipping the quit step fails with a raw "EPERM: operation not
                // permitted, unlink …SupervertalerMcpServer.exe", which reads like a
                // broken download rather than a running process holding the file.
                var lockNote = new Label
                {
                    Text = "If you skip step 2 the install fails with an \"EPERM … unlink\" error. The "
                         + "uninstall is then queued: quit Claude Desktop, start it again, and install once more.",
                    AutoSize = true,
                    ForeColor = Color.FromArgb(110, 110, 110),
                    MaximumSize = new Size(UiScale.Pixels(520), 0),
                    Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
                };
                root.Controls.Add(lockNote);
            }

            var btnDownload = new Button
            {
                Text = "Download extension (.mcpb)…",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };
            btnDownload.Click += (s, e) => OpenUrl(DownloadUrl);
            root.Controls.Add(btnDownload);

            // ── ChatGPT desktop ───────────────────────────────────────────
            root.Controls.Add(SectionHeader("ChatGPT desktop"));

            var chatGptHint = new Label
            {
                Text = "ChatGPT has no drag-and-drop installer, so this does the work for you: it " +
                       "downloads the server, keeps it in your Supervertaler data folder, and " +
                       "registers it in ChatGPT's configuration. Afterwards, quit ChatGPT from the " +
                       "notification area (closing the window is not enough) and start it again.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(chatGptHint);

            var chatGptStatus = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(110, 110, 110),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(chatGptStatus);

            var btnChatGpt = new Button
            {
                AutoSize = true,
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };

            // Label and status are set together: a button reading "Set up
            // ChatGPT desktop" directly under "Already set up" reads as an
            // instruction the user has somehow failed to follow.
            Action refreshChatGptState = () =>
            {
                var configured = Core.ChatGptMcpSetup.IsConfigured();
                chatGptStatus.Text = configured
                    ? "Already set up. Re-running refreshes the server and its path."
                    : "Not set up yet.";
                btnChatGpt.Text = configured
                    ? "Re-run setup"
                    : "Set up ChatGPT desktop";
            };
            refreshChatGptState();
            btnChatGpt.Click += async (s, e) =>
            {
                var original = btnChatGpt.Text;
                btnChatGpt.Enabled = false;
                try
                {
                    var result = await Core.ChatGptMcpSetup.RunAsync(
                        msg => { try { btnChatGpt.Text = msg; } catch { } });

                    refreshChatGptState();
                    original = btnChatGpt.Text;   // may have flipped to "Re-run setup"

                    var body = result.Message;
                    if (result.Success && result.BackupPath != null)
                        body += "\r\n\r\nYour previous configuration was backed up to:\r\n"
                              + result.BackupPath;

                    MessageBox.Show(this, body, "Supervertaler", MessageBoxButtons.OK,
                        result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
                finally
                {
                    btnChatGpt.Text = original;
                    btnChatGpt.Enabled = true;
                }
            };
            root.Controls.Add(btnChatGpt);

            // ── Google Antigravity / Gemini CLI ───────────────────────────
            root.Controls.Add(SectionHeader("Google Antigravity"));

            var agHint = new Label
            {
                Text = "Antigravity connects like any other MCP app, but it also has a shell and a "
                     + "file browser – and left to itself it answers Trados questions by digging "
                     + "through your project folder instead of asking Studio, which is slow and gives "
                     + "stale answers. This installs a short instruction file that tells it to use the "
                     + "Supervertaler tools and read the document. The Gemini CLI reads the same file.",
                AutoSize = true,
                MaximumSize = new Size(UiScale.Pixels(520), 0),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(agHint);

            var agStatus = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(110, 110, 110),
                Margin = new Padding(0, 0, 0, UiScale.Pixels(6))
            };
            root.Controls.Add(agStatus);

            var btnAntigravity = new Button
            {
                AutoSize = true,
                Margin = new Padding(0, 0, 0, UiScale.Pixels(10))
            };

            // Same reasoning as the ChatGPT button above: the label and the status
            // line are written together, so the button never reads as an
            // instruction the user has somehow failed to follow.
            Action refreshAgState = () =>
            {
                if (!Core.AntigravitySkillSetup.IsInstalled())
                {
                    agStatus.Text = Core.AntigravitySkillSetup.IsAntigravityInstalled()
                        ? "Not installed yet."
                        : "Not installed yet. Antigravity does not appear to be on this PC – "
                          + "installing the file now is fine, it will be found later.";
                    btnAntigravity.Text = "Install Antigravity skill";
                }
                else if (Core.AntigravitySkillSetup.IsOutdated())
                {
                    agStatus.Text = "Installed, but it differs from this plugin version – either older, "
                                  + "or edited by you. Your copy is backed up before it is replaced.";
                    btnAntigravity.Text = "Update Antigravity skill";
                }
                else
                {
                    agStatus.Text = "Installed and up to date.";
                    btnAntigravity.Text = "Reinstall Antigravity skill";
                }
            };
            refreshAgState();
            btnAntigravity.Click += (s, e) =>
            {
                btnAntigravity.Enabled = false;
                try
                {
                    var result = Core.AntigravitySkillSetup.Install();
                    refreshAgState();

                    var body = result.Message;
                    if (result.Success && result.BackupPath != null)
                        body += "\r\n\r\nYour previous version was backed up to:\r\n" + result.BackupPath;

                    MessageBox.Show(this, body, "Supervertaler", MessageBoxButtons.OK,
                        result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
                finally { btnAntigravity.Enabled = true; }
            };
            root.Controls.Add(btnAntigravity);

            // ── Other MCP-capable AI apps ─────────────────────────────────
            root.Controls.Add(SectionHeader("Other AI apps (Claude Code, …)"));

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

        /// <summary>A per-app heading inside the Status block. Lighter than
        /// <see cref="SectionHeader"/> so the groups read as subordinate to it
        /// rather than as new sections of the dialog.</summary>
        private static Label SubHeader(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                ForeColor = Color.FromArgb(90, 90, 90),
                Margin = new Padding(UiScale.Pixels(2), UiScale.Pixels(6), 0, UiScale.Pixels(2))
            };
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
