using System;
using System.Drawing;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.VoiceControl;

namespace Supervertaler.Trados.Controls
{
    /// <summary>
    /// The SuperVoice pane (issue #129): the microphone, what state it is in, and
    /// what it has heard - in a panel of its own rather than a status label inside
    /// TermLens.
    ///
    /// <para><b>Why a pane.</b> Every indicator lived in the TermLens header, so a
    /// translator who does not use TermLens saw none of it, and every message the
    /// voice system produced flashed for two to five seconds and was gone. Once
    /// selection arrived, those messages started carrying the things worth reading -
    /// which of four readings was taken, why a word could not be heard, what the
    /// recogniser actually returned. That is a list, not a flash.</para>
    ///
    /// <para><b>Deliberately small.</b> It holds the microphone, the state, and the
    /// recent utterances. The numbered-word view that #128 needs is NOT here: a pane
    /// big enough for a long segment is one nobody has room for, so numbering is a
    /// transient popup. This pane can live in a narrow strip beside the editor
    /// permanently, which is the whole point of it.</para>
    ///
    /// <para>The TermLens header button and the floating strip keep working exactly
    /// as before. This is in addition to them, not instead.</para>
    /// </summary>
    internal sealed class SuperVoiceControl : UserControl, IUIControl
    {
        private readonly Button _btnMic;
        private readonly Label _lblState;
        private readonly ListView _list;
        private readonly Button _btnSettings;
        private readonly Button _btnHelp;

        private static readonly Color Green = Color.FromArgb(60, 160, 80);
        private static readonly Color Amber = Color.FromArgb(220, 150, 40);
        private static readonly Color Grey = Color.FromArgb(150, 150, 150);
        private static readonly Color Red = Color.FromArgb(200, 70, 60);

        public SuperVoiceControl()
        {
            BackColor = SystemColors.Window;
            Padding = new Padding(UiScale.Pixels(6));

            _btnMic = new Button
            {
                Text = "🎤",
                Font = new Font(Font.FontFamily, 13f),
                FlatStyle = FlatStyle.Flat,
                Width = UiScale.Pixels(38),
                Height = UiScale.Pixels(30),
                ForeColor = Grey,
                Dock = DockStyle.Left
            };
            _btnMic.FlatAppearance.BorderSize = 0;
            _btnMic.Click += (s, e) => VoiceControlManager.Instance.Toggle();
            new ToolTip().SetToolTip(_btnMic, "Start or stop SuperVoice (Ctrl+Alt+D)");

            _lblState = new Label
            {
                Text = "Off",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Grey,
                Padding = new Padding(UiScale.Pixels(4), 0, 0, 0)
            };

            _btnSettings = MakeIconButton("⚙", "SuperVoice settings", (s, e) => ShowSettings());
            _btnHelp = MakeIconButton("?", "Help", (s, e) => HelpSystem.OpenHelp(HelpSystem.Topics.VoiceCommands));

            var header = new Panel { Dock = DockStyle.Top, Height = UiScale.Pixels(34) };
            var headerRight = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false
            };
            headerRight.Controls.Add(_btnSettings);
            headerRight.Controls.Add(_btnHelp);
            header.Controls.Add(_lblState);
            header.Controls.Add(headerRight);
            header.Controls.Add(_btnMic);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window
            };
            _list.Columns.Add("Time", UiScale.Pixels(56));
            _list.Columns.Add("Heard", UiScale.Pixels(150));
            _list.Columns.Add("What happened", UiScale.Pixels(260));
            // The last column takes the slack, so a narrow pane still shows the
            // outcome - which is the column worth reading.
            _list.Resize += (s, e) => FitColumns();

            Controls.Add(_list);
            Controls.Add(header);

            VoiceActivityLog.Changed += OnActivityChanged;
            DictationMode.Changed += OnDictationChanged;
            Refresh_();
            SyncState();
        }

        private Button MakeIconButton(string glyph, string tip, EventHandler onClick)
        {
            var b = new Button
            {
                Text = glyph,
                FlatStyle = FlatStyle.Flat,
                Width = UiScale.Pixels(28),
                Height = UiScale.Pixels(28),
                Margin = new Padding(UiScale.Pixels(2), UiScale.Pixels(3), 0, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += onClick;
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        private void FitColumns()
        {
            if (_list.Columns.Count < 3) return;
            var slack = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width
                        - SystemInformation.VerticalScrollBarWidth - UiScale.Pixels(4);
            if (slack > UiScale.Pixels(80)) _list.Columns[2].Width = slack;
        }

        private static void ShowSettings()
        {
            using (var dlg = new VoiceSettingsDialog())
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    VoiceControlManager.Instance.ReloadCommands();
            }
        }

        /// <summary>
        /// Called from the manager whenever the microphone's state changes, so the
        /// pane agrees with the TermLens button rather than tracking its own idea.
        /// </summary>
        public void SetState(int state, string text)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<int, string>(SetState), state, text); } catch { }
                return;
            }
            switch (state)
            {
                case 2: _btnMic.ForeColor = Green; break;
                case 1: _btnMic.ForeColor = Amber; break;
                default: _btnMic.ForeColor = Grey; break;
            }
            _lblState.ForeColor = state == 2 ? Green : state == 1 ? Amber : Grey;
            if (!string.IsNullOrWhiteSpace(text)) _lblState.Text = text;
            else if (state == 0) _lblState.Text = "Off";
        }

        private void SyncState()
        {
            var running = VoiceControlManager.Instance.IsRunning;
            SetState(running ? 2 : 0, running ? "Listening…" : "Off");
        }

        private void OnDictationChanged(bool dictating)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<bool>(OnDictationChanged), dictating); } catch { }
                return;
            }
            // While dictating, every command but the way out is ignored on purpose.
            // Saying so here is the difference between "gated" and "broken".
            if (dictating)
            {
                _lblState.Text = "Dictating — say \"stop now\" to take over";
                _lblState.ForeColor = Amber;
                _btnMic.ForeColor = Amber;
            }
            else SyncState();
        }

        private void OnActivityChanged()
        {
            // Raised on the audio thread.
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(Refresh_)); } catch { }
                return;
            }
            Refresh_();
        }

        private void Refresh_()
        {
            if (IsDisposed || _list.IsDisposed) return;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var e in VoiceActivityLog.Snapshot())
                {
                    var item = new ListViewItem(e.Time.ToString("HH:mm:ss"));
                    item.SubItems.Add(e.Heard ?? "");
                    item.SubItems.Add(e.Result ?? (e.Kind == VoiceActivityLog.Outcome.Pending ? "…" : ""));
                    switch (e.Kind)
                    {
                        case VoiceActivityLog.Outcome.Done: item.ForeColor = Green; break;
                        case VoiceActivityLog.Outcome.Refused: item.ForeColor = Amber; break;
                        case VoiceActivityLog.Outcome.Missed: item.ForeColor = Red; break;
                        case VoiceActivityLog.Outcome.Suppressed: item.ForeColor = Grey; break;
                        default: item.ForeColor = SystemColors.GrayText; break;
                    }
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
                FitColumns();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Static events: a pane that is closed and reopened would otherwise
                // accumulate subscriptions and refresh a disposed list.
                VoiceActivityLog.Changed -= OnActivityChanged;
                DictationMode.Changed -= OnDictationChanged;
            }
            base.Dispose(disposing);
        }
    }
}
