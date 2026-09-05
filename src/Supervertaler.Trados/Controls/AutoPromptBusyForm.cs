using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// Tiny modal busy window that runs an async LLM call and closes itself when it
    /// completes. Written for the AutoPrompt context classification and reused by
    /// "Add term with abbreviation (AI)" - so the caller names the window and says
    /// what is being waited for; the defaults are AutoPrompt's. Because it is modal and
    /// awaits on the UI thread, the window stays responsive (the marquee animates)
    /// while the "Reading the document…" step runs, and the user cannot re-trigger
    /// AutoPrompt underneath it. Mirrors the Workbench background-worker + busy
    /// dialog around _classify_document_context.
    /// </summary>
    internal class AutoPromptBusyForm : Form
    {
        private readonly Func<Task<string>> _work;

        /// <summary>The classifier's raw reply, or null on error / no result.</summary>
        public string Result { get; private set; }

        /// <summary>The exception if the call failed, otherwise null.</summary>
        public Exception Error { get; private set; }

        public AutoPromptBusyForm(Func<Task<string>> work, string title = null, string message = null)
        {
            _work = work;
            Icon = Supervertaler.Trados.Core.IconHelper.AppIcon;

            Text = string.IsNullOrEmpty(title) ? "AutoPrompt" : title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;      // no X – closes itself when the call returns
            ShowInTaskbar = false;
            ClientSize = new Size(360, 92);

            var label = new Label
            {
                Text = string.IsNullOrEmpty(message) ? "Reading the document to detect its context…" : message,
                Location = new Point(16, 18),
                Size = new Size(328, 20),
                AutoSize = false,
            };
            var bar = new ProgressBar
            {
                Location = new Point(16, 46),
                Size = new Size(328, 18),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
            };

            Controls.Add(label);
            Controls.Add(bar);

            // Run the async call once the window is on screen; close on completion.
            Shown += async (s, e) =>
            {
                try { Result = await _work(); }
                catch (Exception ex) { Error = ex; }
                try { Close(); } catch { }
            };
        }
    }
}
