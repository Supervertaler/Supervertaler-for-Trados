using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// "Open example project", from a button: the language dialog, the question
    /// when an example for those languages exists already, the background job
    /// with a small progress window, and the closing message. On the UI thread.
    /// Also the entry point the welcome chat will use.
    /// </summary>
    internal static class ExampleProjectRunner
    {
        private const string Caption = "Open example project";

        internal static void Start(IWin32Window owner)
        {
            ExampleData data;
            try { data = ExampleProject.Load(); }
            catch (Exception ex) { Say(owner, ex.Message, MessageBoxIcon.Warning); return; }

            var projectsFolder = AiAssistantViewPart.StudioProjectsFolder();
            if (projectsFolder == null)
            {
                Say(owner, "Studio's Projects folder could not be found, so the example project cannot be made.", MessageBoxIcon.Warning);
                return;
            }

            // 1. Which languages.
            string curSource = null, curTarget = null;
            try
            {
                var info = SdlTradosStudio.Application.GetController<ProjectsController>().CurrentProject?.GetProjectInfo();
                curSource = info?.SourceLanguage?.CultureInfo?.Name;
                curTarget = info?.TargetLanguages?.FirstOrDefault()?.CultureInfo?.Name;
            }
            catch { }
            var guess = ExampleProject.Guess(curSource, curTarget, System.Globalization.CultureInfo.CurrentUICulture.Name);
            string englishGuess = new[] { curSource, curTarget }.FirstOrDefault(c => c != null && c.StartsWith("en", StringComparison.OrdinalIgnoreCase));

            ExampleDirection d;
            using (var dlg = new ExampleProjectDialog(data, guess.language, guess.intoEnglish, englishGuess))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                d = dlg.Direction;
            }

            // 2. Where, and whether one exists already.
            var folder = ExampleProject.PlanFolder(projectsFolder, d, AiAssistantViewPart.RegisteredNames());
            if (folder.Error != null) { Say(owner, folder.Error, MessageBoxIcon.Warning); return; }
            var action = AiAssistantViewPart.ExampleAction.Create;
            if (folder.ExistingProjectFile != null)
            {
                var choice = AskExisting(owner, ExampleProject.EnglishName(d.IntoEnglish ? d.Source : d.Target), folder.ExistingName);
                if (choice == null) return;
                action = choice.Value;
            }

            // 3. The job, with a progress window.
            var jobId = AiAssistantViewPart.StartExampleProjectJob(data, d, folder, projectsFolder, action, out var busy);
            if (jobId == null) { Say(owner, busy, MessageBoxIcon.Information); return; }
            BridgeTaskStatusResponse status;
            using (var progress = new ProgressWindow(jobId))
            {
                progress.ShowDialog(owner);
                status = progress.Final ?? AiAssistantViewPart.ExampleJobStatus(jobId);
            }

            // 4. What happened.
            if (status == null || status.Status != "done")
            {
                Say(owner, status?.Error ?? "The example project could not be made.", MessageBoxIcon.Warning);
                return;
            }
            var lines = new List<string>
            {
                // The job says whether Studio actually opened it.
                status.Note ?? "The example project is ready.",
                "",
                "Things to try (right-click in the Editor for Supervertaler's commands):",
                "•  Translate active segment (Alt+T), or Batch Translate the whole document",
                "•  FuzzyFixer (Ctrl+Alt+F) on a segment with a fuzzy match, such as \"Press + to raise the temperature …\"",
                "•  Proofread, and QuickLauncher (Alt+Q)",
                "•  The project's memory bank (SuperMemory) gives the AI a short client brief and style rules: change them and translate again",
            };
            if (status.Messages != null && status.Messages.Count > 0)
            {
                lines.Add("");
                lines.AddRange(status.Messages);
            }
            var keyNote = NoKeyNote();
            if (keyNote != null) { lines.Add(""); lines.Add(keyNote); }
            lines.Add("");
            lines.Add("The guide lists which sentences have translation memory matches, and more to try. " +
                      "It opens in your browser, so you can keep it beside Studio.");
            ShowDone(owner, string.Join(Environment.NewLine, lines));
        }

        /// <summary>
        /// The closing message, with "Open the guide": the Help page "Try the
        /// example project". A message box is gone once it is closed, and the
        /// panels are wherever each user's layout puts them (or closed), so the
        /// lasting copy of the tips is the Help page, in its own browser window.
        /// </summary>
        private static void ShowDone(IWin32Window owner, string text)
        {
            using (var f = new Form
            {
                Text = Caption, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = new Font("Segoe UI", 9f), Padding = new Padding(UiScale.Pixels(12))
            })
            {
                var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(UiScale.Pixels(480), 0) };
                var guide = new Button { Text = "Open the guide", AutoSize = true };
                var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
                guide.Click += (s, e) =>
                {
                    try { HelpSystem.OpenHelp(HelpSystem.Topics.ExampleProject); } catch { }
                    f.Close();
                };
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, UiScale.Pixels(12), 0, 0) };
                buttons.Controls.AddRange(new Control[] { guide, ok });
                var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                stack.Controls.Add(label);
                stack.Controls.Add(buttons);
                f.Controls.Add(stack);
                f.AcceptButton = guide;
                f.CancelButton = ok;
                f.ShowDialog(owner);
            }
        }

        /// <summary>A line about the API key when the chosen AI provider has none, else null.</summary>
        private static string NoKeyNote()
        {
            try
            {
                var ai = SettingsService.Current?.AiSettings;
                if (ai == null) return null;
                var key = TradosApiKeys.For(ai.SelectedProvider, ai, out var problem);
                if (!string.IsNullOrEmpty(key) || problem == null) return null;
                return "To use the AI, add an API key first: " + TradosApiKeys.SettingsPlace + ".";
            }
            catch { return null; }
        }

        private static AiAssistantViewPart.ExampleAction? AskExisting(IWin32Window owner, string language, string name)
        {
            using (var f = new Form
            {
                Text = Caption, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = new Font("Segoe UI", 9f), Padding = new Padding(UiScale.Pixels(12))
            })
            {
                AiAssistantViewPart.ExampleAction? result = null;
                var text = new Label
                {
                    Text = "You already have the example project for " + language + " (\"" + name + "\").\n\n" +
                           "Open it as it is, with your changes, or start afresh? Starting afresh deletes it, " +
                           "with its termbase and memory bank, and makes a new one.",
                    AutoSize = true, MaximumSize = new Size(UiScale.Pixels(420), 0)
                };
                var open = new Button { Text = "Open it", AutoSize = true };
                var fresh = new Button { Text = "Start afresh", AutoSize = true };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                open.Click += (s, e) => { result = AiAssistantViewPart.ExampleAction.OpenExisting; f.Close(); };
                fresh.Click += (s, e) => { result = AiAssistantViewPart.ExampleAction.StartAfresh; f.Close(); };
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, UiScale.Pixels(12), 0, 0) };
                buttons.Controls.AddRange(new Control[] { open, fresh, cancel });
                var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
                stack.Controls.Add(text);
                stack.Controls.Add(buttons);
                f.Controls.Add(stack);
                f.AcceptButton = open;
                f.CancelButton = cancel;
                f.ShowDialog(owner);
                return result;
            }
        }

        private static void Say(IWin32Window owner, string text, MessageBoxIcon icon) =>
            MessageBox.Show(owner, text, Caption, MessageBoxButtons.OK, icon);

        /// <summary>
        /// Shows the job's progress and closes itself when the job ends. No
        /// Cancel: the job takes seconds, and stopping Studio's project creation
        /// half-way would leave more to clean up than letting it finish.
        /// </summary>
        private sealed class ProgressWindow : Form
        {
            private readonly string _jobId;
            private readonly Label _label;
            private readonly Timer _timer;
            public BridgeTaskStatusResponse Final { get; private set; }

            public ProgressWindow(string jobId)
            {
                _jobId = jobId;
                Text = Caption;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                ControlBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                Font = new Font("Segoe UI", 9f);
                ClientSize = new Size(UiScale.Pixels(420), UiScale.Pixels(90));
                _label = new Label { Dock = DockStyle.Top, Height = UiScale.Pixels(40), Text = "Making the example project: starting" };
                var bar = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, Height = UiScale.Pixels(18) };
                Padding = new Padding(UiScale.Pixels(12));
                Controls.Add(bar);
                Controls.Add(_label);
                _timer = new Timer { Interval = 300 };
                _timer.Tick += (s, e) => Poll();
                Shown += (s, e) => _timer.Start();
                FormClosed += (s, e) => _timer.Stop();
            }

            private void Poll()
            {
                var st = AiAssistantViewPart.ExampleJobStatus(_jobId);
                if (st == null || !st.Found || !st.Running)
                {
                    _timer.Stop();
                    Final = st;
                    Close();
                    return;
                }
                _label.Text = "Making the example project: " + (st.Progress ?? "working");
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _timer.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
