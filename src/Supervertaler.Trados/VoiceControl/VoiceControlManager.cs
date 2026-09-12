using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Supervertaler.Trados.Controls;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// The glue: owns the engine, executor, command set and status display,
    /// and exposes the single Toggle() the action/header button call. First
    /// activation downloads the voice runtime (libvosk + small English
    /// model) with progress shown; every later activation is instant.
    ///
    /// Status display strategy: when the TermLens panel exists, its header
    /// 🎤 button IS the indicator (integrated, covers nothing – heard
    /// commands flash in the panel's status label). The floating strip is
    /// only a fallback for sessions where TermLens isn't open, and is
    /// draggable with its position remembered.
    /// </summary>
    internal sealed class VoiceControlManager
    {
        private static VoiceControlManager _instance;
        public static VoiceControlManager Instance =>
            _instance ?? (_instance = new VoiceControlManager());

        private IVoiceEngine _engine;
        private VoiceStatusWindow _statusWindow;   // fallback display only
        private TermLensControl _hostControl;      // preferred display + marshal target
        private VoiceCommandExecutor _executor;
        private List<VoiceCommand> _commands;
        private volatile bool _running;
        private volatile bool _starting;

        public bool IsRunning => _running;

        public void Toggle()
        {
            if (_running || _starting) Stop();
            else Start();
        }

        /// <summary>Starts listening (downloads the runtime first if needed).</summary>
        public void Start()
        {
            if (_running || _starting) return;
            _starting = true;

            _commands = VoiceCommandSet.Load();
            _executor = new VoiceCommandExecutor();
            _executor.LoadCommands(_commands);
            _executor.CommandExecuted += (phrase, desc) => FlashCommand(phrase);
            // #125: a command heard and deliberately ignored is not the same as one
            // not heard. Say which, or dictation mode looks like a broken recogniser.
            _executor.CommandSuppressed += phrase => FlashCommand("(dictating) " + phrase);
            DictationMode.Changed += on => SetStatus(on ? "Dictating - say the word again to stop" : "Listening…",
                                                     state: 2);

            // Preferred: the TermLens header hosts the indicator. Fallback:
            // the floating strip (draggable, position remembered).
            _hostControl = TermLensEditorViewPart.TryGetVoiceHost();
            if (_hostControl == null)
            {
                _statusWindow = new VoiceStatusWindow();
                _statusWindow.StopRequested += (s, e) => Stop();
                _statusWindow.AdvancedRequested += (s, e) => ShowAdvancedDialog();
                _statusWindow.Show();
            }
            SetStatus(VoiceRuntimeInstaller.IsInstalled ? "Starting…" : "Setting up (one-time)…", state: 1);

            // Runtime install + model load are seconds-slow – background thread.
            var worker = new Thread(() =>
            {
                try
                {
                    VoiceRuntimeInstaller.EnsureInstalled(msg => SetStatus(msg, state: 1));

                    var engine = new VoskVoiceEngine();
                    engine.Recognized += OnRecognized;
                    engine.Start(VoiceCommandSet.GrammarPhrases(_commands)
                        .Concat(_segmentWords).Distinct().ToList());
                    _engine = engine;

                    _running = true;
                    SetStatus("Listening…", state: 2);

                    // #125: seed the grammar with the segment the translator is
                    // already in - otherwise its words are absent until they move.
                    try
                    {
                        var marshalCtl = MarshalControl();
                        if (marshalCtl != null && !marshalCtl.IsDisposed)
                            marshalCtl.BeginInvoke((Action)TermLensEditorViewPart.PushVoiceSegmentWordsNow);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    var marshal = MarshalControl();
                    if (marshal != null && !marshal.IsDisposed)
                    {
                        marshal.BeginInvoke((Action)(() =>
                        {
                            Stop();
                            MessageBox.Show(
                                "Voice commands could not start:\n\n" + ex.Message,
                                "Supervertaler – Voice commands",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }));
                    }
                }
                finally
                {
                    _starting = false;
                }
            })
            { IsBackground = true, Name = "Supervertaler.VoiceStart" };
            worker.Start();
        }

        public void Stop()
        {
            _running = false;
            // #125: never leave the dictation gate up. It suppresses every command
            // but the way out, and the way out is a voice command - so a gate that
            // survived a stop would make the next session look completely deaf.
            DictationMode.Reset();
            // Likewise the remembered selection: a stale offset would make the first
            // "select the" of the next session land on the second occurrence.
            try { TermLensEditorViewPart.VoiceForgetLastSelection(); } catch { }
            try { _engine?.Dispose(); } catch { }
            _engine = null;

            var host = _hostControl;
            _hostControl = null;
            if (host != null && !host.IsDisposed)
                host.SetVoiceState(0);

            var window = _statusWindow;
            _statusWindow = null;
            if (window != null && !window.IsDisposed)
            {
                if (window.InvokeRequired) window.BeginInvoke((Action)(() => window.Close()));
                else window.Close();
            }
        }

        private Control MarshalControl()
        {
            var host = _hostControl;
            if (host != null && !host.IsDisposed) return host;
            var window = _statusWindow;
            if (window != null && !window.IsDisposed) return window;
            return null;
        }

        private void SetStatus(string text, int state)
        {
            var host = _hostControl;
            if (host != null && !host.IsDisposed)
            {
                host.SetVoiceState(state, text);
                return;
            }
            var window = _statusWindow;
            if (window != null && !window.IsDisposed)
                window.SetStatus(text, listening: state == 2);
        }

        /// <summary>
        /// #125: says something on the voice strip. For telling the translator what
        /// a command did when the document itself does not show it - "3 matches, say
        /// more words" after an ambiguous selection, which otherwise just looks like
        /// the wrong word being picked.
        /// </summary>
        public void Announce(string text)
        {
            // Longer than a command echo. This is the only sign anything happened.
            try { FlashCommand(text, 5000); } catch { }
        }

        private void FlashCommand(string phrase, int milliseconds = 2000)
        {
            var host = _hostControl;
            if (host != null && !host.IsDisposed)
            {
                host.FlashVoiceCommand(phrase, milliseconds);
                return;
            }
            var window = _statusWindow;
            if (window != null && !window.IsDisposed)
                window.FlashCommand(phrase);
        }

        private void OnRecognized(string text)
        {
            // #125: every utterance the recogniser returns, matched or not. An
            // attempt that fires no command leaves no other trace, and "nothing
            // happened" covers two very different faults: the words were not heard,
            // or they were heard and no command wanted them. Without this the two
            // are indistinguishable from the outside.
            try
            {
                Core.DiagnosticLog.WriteAlways("VoiceHeard",
                    string.IsNullOrWhiteSpace(text) ? "(nothing)" : "\"" + text + "\"");
            }
            catch { }

            text = ReconsiderSelection(text);

            // Engine thread → UI thread
            var marshal = MarshalControl();
            if (marshal == null) return;
            try
            {
                marshal.BeginInvoke((Action)(() => _executor?.Execute(text)));
            }
            catch { /* host torn down mid-recognition */ }
        }

        /// <summary>
        /// #125: hears a "select …" utterance again, against the segment's words
        /// alone.
        ///
        /// <para><b>The problem this solves.</b> The grammar is closed and the
        /// recogniser cannot return nothing, so every command phrase competes for
        /// every sound. Our own "term eight" and "match eight" put "eight" into the
        /// vocabulary of every segment; the article "a" is the same sound; and
        /// "select a further" could not be said at all. Resolving known homophones
        /// treats three symptoms, and any command a user adds can create a new
        /// one.</para>
        ///
        /// <para>The grammar cannot be switched when "select" is heard - by then the
        /// utterance is already recognised. So the audio is kept and recognised a
        /// second time against a grammar of the segment's own words plus the slot
        /// prefixes. A command word that is not in the segment cannot come back at
        /// all, which removes the whole class rather than the known members of
        /// it.</para>
        ///
        /// <para>The first reading is kept if the second returns nothing, or returns
        /// nothing after the prefix: a second opinion is only worth having when it
        /// has something to say.</para>
        /// </summary>
        private string ReconsiderSelection(string text)
        {
            try
            {
                var engine = _engine;
                if (engine == null || string.IsNullOrWhiteSpace(text)) return text;

                var prefixes = _commands.Where(c => c.Enabled)
                                        .SelectMany(c => c.SlotPrefixes())
                                        .Distinct()
                                        .ToList();
                if (prefixes.Count == 0) return text;
                if (!prefixes.Any(p => ContainsWord(text, p))) return text;

                List<string> words;
                lock (_segmentWordLock) { words = new List<string>(_segmentWords); }
                if (words.Count == 0) return text;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var second = engine.RecognizeLastUtterance(words.Concat(prefixes).Distinct().ToList());
                sw.Stop();
                if (string.IsNullOrWhiteSpace(second))
                {
                    Core.DiagnosticLog.WriteAlways("VoiceHeard",
                        "second pass returned nothing in " + sw.ElapsedMilliseconds + " ms - keeping \"" + text + "\"");
                    return text;
                }

                // Only accept a second reading that still names the command and still
                // has words after it. Anything else is a worse answer than the first.
                if (!prefixes.Any(p => ContainsWord(second, p))) return text;

                Core.DiagnosticLog.WriteAlways("VoiceHeard",
                    "second pass (" + sw.ElapsedMilliseconds + " ms, " + words.Count + " segment words): \""
                    + text + "\" -> \"" + second + "\""
                    + (string.Equals(second, text, StringComparison.OrdinalIgnoreCase) ? " (unchanged)" : ""));
                return second;
            }
            catch { return text; }
        }

        private static bool ContainsWord(string utterance, string word)
        {
            return (" " + utterance + " ").IndexOf(" " + word + " ",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Reloads commands (after the Advanced dialog saves) into the live engine.</summary>
        public void ReloadCommands()
        {
            _commands = VoiceCommandSet.Load();
            _executor?.LoadCommands(_commands);
            RefreshGrammar();
        }

        /// <summary>
        /// #125: the words of the segment the translator is in, added to the
        /// recogniser's vocabulary so a slot command can name them.
        ///
        /// <para>Vosk runs in grammar mode: it can only ever hear what is in this
        /// list. "select sealing ring" is therefore not a matter of recognising open
        /// speech and matching it afterwards - the words have to be in the grammar
        /// before they can be heard at all. That is the whole reason this is fast and
        /// accurate, and the reason it has to be rebuilt as the translator moves.</para>
        /// </summary>
        private List<string> _segmentWords = new List<string>();

        /// <summary>
        /// Guards <see cref="_segmentWords"/>. It is written on the UI thread as the
        /// translator moves between segments, and read on the AUDIO thread by
        /// <see cref="ReconsiderSelection"/> - a list being replaced under an
        /// enumeration. The list is only ever swapped whole, never mutated, so the
        /// lock is held just long enough to take a copy.
        /// </summary>
        private readonly object _segmentWordLock = new object();

        /// <summary>
        /// Replaces the per-segment vocabulary and rebuilds the live grammar. Cheap
        /// to call on every segment change: it no-ops when the words are unchanged,
        /// which they are for the many segments that share wording in a patent.
        /// </summary>
        public void SetSegmentWords(IEnumerable<string> words)
        {
            var fresh = (words ?? Enumerable.Empty<string>())
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => w.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            lock (_segmentWordLock)
            {
                if (fresh.Count == _segmentWords.Count
                    && !fresh.Except(_segmentWords).Any()) return;
                _segmentWords = fresh;
            }
            RefreshGrammar();
        }

        /// <summary>Command phrases plus the current segment's words.</summary>
        private void RefreshGrammar()
        {
            if (_engine == null || _commands == null) return;
            var phrases = VoiceCommandSet.GrammarPhrases(_commands)
                .Concat(_segmentWords)
                .Distinct()
                .ToList();
            try { _engine.UpdateGrammar(phrases); }
            catch (Exception ex)
            {
                try { Core.DiagnosticLog.Log("Voice", "Grammar refresh failed: " + ex.Message); } catch { }
            }
        }

        /// <summary>Opens the Advanced command editor (gear / header right-click).</summary>
        public void ShowAdvancedDialog()
        {
            using (var dlg = new VoiceSettingsDialog())
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    ReloadCommands();
            }
        }
    }
}
