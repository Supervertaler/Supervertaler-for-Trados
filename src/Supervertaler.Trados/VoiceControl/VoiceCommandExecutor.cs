using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Matches recognised phrases to commands and executes them.
    ///
    /// Internal actions call plugin code directly (TermLens insertion, popup,
    /// picker, navigation). Keystroke actions synthesise the chord via
    /// SendKeys – Studio dispatches its own shortcuts AND this plugin's
    /// registered shortcuts identically, so "alt+up" triggers our quick-add
    /// action just like pressing it.
    ///
    /// Safety: keystrokes only fire when Trados Studio is the foreground
    /// window – a stray "confirm" while reading email must do nothing.
    /// Internal TermLens actions are equally foreground-gated because they
    /// act on the active document.
    /// </summary>
    internal sealed class VoiceCommandExecutor
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>
        /// UTC time of the last synthetic keystroke this executor sent.
        /// CtrlTapFilter consults this to ignore the Ctrl press/release pairs
        /// that SendKeys synthesises for Ctrl-modified chords ("zoom in" →
        /// Ctrl+Alt+PgUp) – without it, the tap detector sees a phantom
        /// Ctrl tap and pops the TermLens popup after every such command.
        /// </summary>
        internal static DateTime LastSyntheticKeystrokeUtc { get; private set; } = DateTime.MinValue;

        private readonly Dictionary<string, VoiceCommand> _byPhrase =
            new Dictionary<string, VoiceCommand>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// #125: internal actions that take an argument - the words the translator
        /// spoke after a slot command's prefix. "select sealing ring" reaches
        /// _slotHandlers["select_phrase"]("sealing ring").
        /// </summary>
        private readonly Dictionary<string, Action<string>> _slotHandlers =
            new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Slot commands by their spoken prefix, longest prefix first.</summary>
        private readonly List<KeyValuePair<string, VoiceCommand>> _bySlotPrefix =
            new List<KeyValuePair<string, VoiceCommand>>();

        private readonly Dictionary<string, Action> _internalHandlers =
            new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised after a command executes (phrase, description) – for status display.</summary>
        public event Action<string, string> CommandExecuted;

        /// <summary>#125: a command was heard but ignored because dictation is on.</summary>
        public event Action<string> CommandSuppressed;

        public VoiceCommandExecutor()
        {
            // Internal action registry – direct plugin calls
            for (int i = 1; i <= 9; i++)
            {
                int n = i; // capture
                _internalHandlers["insert_term_" + n] = () => TermLensEditorViewPart.HandleDigitPress(n);
            }
            _internalHandlers["term_picker"] = TermLensEditorViewPart.HandleTermPicker;
            _internalHandlers["termlens_popup"] = TermLensEditorViewPart.HandleTermLensPopup;
            _internalHandlers["navigate_next"] = () => TermLensEditorViewPart.VoiceNavigateSegment(true);
            _internalHandlers["navigate_previous"] = () => TermLensEditorViewPart.VoiceNavigateSegment(false);
            _internalHandlers["stop_listening"] = () => VoiceControlManager.Instance.Stop();

            // #125, step 2 of the voice-selection work: recognition only. Reports
            // what Vosk heard after the prefix and what the segment actually
            // contains, and selects nothing. Isolating recognition from matching
            // means a failure here is unambiguous - either the words were heard or
            // they were not, with no matcher in between to blame.
            _slotHandlers["select_phrase"] = TermLensEditorViewPart.VoiceSelectPhrase;
            _slotHandlers["select_source_phrase"] = TermLensEditorViewPart.VoiceSelectSourcePhrase;
            _internalHandlers["delete_selection"] = TermLensEditorViewPart.VoiceDeleteSelection;
        }

        /// <summary>
        /// #125: matches "select sealing ring" against a command whose spoken form is
        /// "select {phrase}", returning the command and putting "sealing ring" in
        /// <paramref name="argument"/>. Null when nothing matches.
        ///
        /// <para>Longest prefix wins, so a command "select again" is not swallowed by
        /// "select" with "again" as its argument. An utterance that is only the
        /// prefix, with nothing after it, does NOT match - "select" on its own names
        /// no phrase, and firing on it would select whatever the matcher liked
        /// least-badly.</para>
        /// </summary>
        private VoiceCommand MatchSlot(string spoken, out string argument)
        {
            argument = null;
            if (string.IsNullOrWhiteSpace(spoken)) return null;

            foreach (var entry in _bySlotPrefix)
            {
                var prefix = entry.Key;

                // The prefix need not open the utterance. The recogniser prepends a
                // stray word often enough to matter - "to select the" was heard twice
                // in one session - and an exact command already tolerates that
                // through the containment fallback below. Anything before the prefix
                // is discarded; everything after it is the argument.
                var at = IndexOfPhrase(spoken, prefix);
                if (at < 0) continue;

                var after = at + prefix.Length;
                if (after >= spoken.Length || spoken[after] != ' ') continue;

                var tail = spoken.Substring(after + 1).Trim();
                if (tail.Length == 0) continue;

                argument = tail;
                return entry.Value;
            }
            return null;
        }

        /// <summary>
        /// Where a phrase starts in an utterance, on word boundaries, or -1. Bounded
        /// so that a prefix "select" is not found inside a longer word.
        /// </summary>
        private static int IndexOfPhrase(string utterance, string phrase)
        {
            if (string.IsNullOrEmpty(phrase)) return -1;
            for (int i = 0; ; )
            {
                var at = utterance.IndexOf(phrase, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0) return -1;
                var startOk = at == 0 || utterance[at - 1] == ' ';
                var end = at + phrase.Length;
                var endOk = end >= utterance.Length || utterance[end] == ' ';
                if (startOk && endOk) return at;
                i = at + 1;
            }
        }

        /// <summary>Rebuilds the phrase lookup from the (enabled) command list.</summary>
        public void LoadCommands(List<VoiceCommand> commands)
        {
            _byPhrase.Clear();
            _bySlotPrefix.Clear();
            foreach (var cmd in commands)
            {
                if (!cmd.Enabled) continue;
                foreach (var phrase in cmd.AllPhrases())
                    if (!_byPhrase.ContainsKey(phrase))
                        _byPhrase[phrase] = cmd;
                foreach (var prefix in cmd.SlotPrefixes())
                    _bySlotPrefix.Add(new KeyValuePair<string, VoiceCommand>(prefix, cmd));
            }
            // Longest prefix first, so "select again" is not swallowed by "select".
            _bySlotPrefix.Sort((x, y) => y.Key.Length.CompareTo(x.Key.Length));
        }

        /// <summary>
        /// Executes the command for a recognised phrase. Must be called on the
        /// UI thread. Grammar mode means the text is (almost) always an exact
        /// phrase; a containment fallback covers joined utterances like
        /// "confirm confirm" or leading noise words.
        /// </summary>
        public void Execute(string recognizedText)
        {
            var spoken = (recognizedText ?? "").Trim();
            string slotArgument = null;

            // Three ways to match, in decreasing strictness. Exact first, so an
            // ordinary command can never be read as the argument of a slot one.
            // Then slot: "select sealing ring" -> prefix "select", argument
            // "sealing ring". Containment last, because it is the loosest and would
            // happily find "select" inside a dictated sentence.
            VoiceCommand cmd;
            if (!_byPhrase.TryGetValue(spoken, out cmd))
                cmd = MatchSlot(spoken, out slotArgument);

            if (cmd == null)
            {
                // Fallback: longest known phrase contained in the utterance
                string best = null;
                foreach (var phrase in _byPhrase.Keys)
                {
                    if ((" " + spoken + " ").IndexOf(" " + phrase + " ", StringComparison.OrdinalIgnoreCase) >= 0
                        && (best == null || phrase.Length > best.Length))
                        best = phrase;
                }
                if (best == null) return;
                cmd = _byPhrase[best];
            }

            var action = cmd.Action ?? "";

            // #125: an action id may carry its own argument after a colon -
            // "dictate_toggle:ctrl+alt+w". Dictation triggers differ per tool and
            // per installation, so the trigger lives in the command the translator
            // can edit rather than in a settings screen of its own.
            string actionArg = null;
            var colon = action.IndexOf(':');
            if (colon > 0)
            {
                actionArg = action.Substring(colon + 1).Trim();
                action = action.Substring(0, colon).Trim();
            }

            var isDictateToggle = string.Equals(action, "dictate_toggle", StringComparison.OrdinalIgnoreCase);

            // While dictating, every word reaches this recogniser as well as the
            // dictation tool. The grammar is closed, so only our own phrases can be
            // heard - but a replacement containing "confirm" would fire one into the
            // document mid sentence. Nothing runs except the way out.
            // "stop listening" must always work; everything else only when
            // Studio is the active window.
            var isStop = string.Equals(action, "stop_listening", StringComparison.OrdinalIgnoreCase);

            if (DictationMode.Active && !isDictateToggle && !isStop)
            {
                CommandSuppressed?.Invoke(cmd.Phrase);
                return;
            }
            if (!isStop && !IsStudioForeground()) return;

            // Echoed BEFORE the handler runs, not after, so that a handler which has
            // something of its own to say gets the last word. A refusal ("'the' is
            // inside another word") or an ambiguity warning is written by the
            // handler; echoing afterwards overwrote it within the same tick, and the
            // translator saw only the command name - which is why every such message
            // built for #125 was invisible in testing.
            //
            // Slot commands echo what was HEARD, not their phrase: "select {phrase}"
            // is a template, and a placeholder tells the translator nothing about
            // whether the words they said arrived intact.
            CommandExecuted?.Invoke(cmd.HasSlot() ? spoken : cmd.Phrase, cmd.Description);

            try
            {
                // #127: dictation types over the selection, so it must not start on a
                // source one - the same reason "delete that" refuses. Only blocked
                // when turning dictation ON; the way out must always work, or the
                // gate below would trap the translator.
                if (isDictateToggle && !DictationMode.Active
                    && TermLensEditorViewPart.VoiceLastSelectionWasSource)
                {
                    VoiceControlManager.Instance?.Announce(
                        "that is source text - select in the target first");
                    return;
                }

                if (isDictateToggle)
                {
                    // "dictate_toggle:<trigger>:<marker>" - the marker is optional and
                    // is whatever the dictation tool writes for the spoken stop phrase.
                    string trigger = actionArg, marker = null;
                    var second = (actionArg ?? "").IndexOf(':');
                    if (second > 0)
                    {
                        marker = actionArg.Substring(second + 1).Trim();
                        trigger = actionArg.Substring(0, second).Trim();
                    }
                    DictationMode.Toggle(trigger, marker);
                }
                else if (string.Equals(cmd.ActionType, "internal", StringComparison.OrdinalIgnoreCase))
                {
                    Action<string> slotHandler;
                    if (slotArgument != null
                        && _slotHandlers.TryGetValue(action, out slotHandler))
                    {
                        slotHandler(slotArgument);
                    }
                    else
                    {
                        Action handler;
                        if (_internalHandlers.TryGetValue(action, out handler))
                            handler();
                    }
                }
                else if (string.Equals(cmd.ActionType, "keystroke", StringComparison.OrdinalIgnoreCase))
                {
                    var keys = ChordToSendKeys(cmd.Action);   // chords keep their colons? no - chords have none
                    if (keys != null)
                    {
                        // Bracket the send so CtrlTapFilter can ignore the
                        // synthetic Ctrl press/release pair (message delivery
                        // can trail SendWait, hence the trailing stamp too).
                        LastSyntheticKeystrokeUtc = DateTime.UtcNow;
                        SendKeys.SendWait(keys);
                        LastSyntheticKeystrokeUtc = DateTime.UtcNow;
                    }
                }
            }
            catch
            {
                // A failing command must never take down the listener
            }
        }

        private static bool IsStudioForeground()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        /// <summary>
        /// Converts a Workbench-style chord ("ctrl+enter", "alt+up", "f3")
        /// into SendKeys syntax ("^{ENTER}", "%{UP}", "{F3}").
        /// Returns null when the chord can't be parsed.
        /// </summary>
        internal static string ChordToSendKeys(string chord)
        {
            if (string.IsNullOrWhiteSpace(chord)) return null;

            var mods = new StringBuilder();
            string key = null;
            foreach (var raw in chord.ToLowerInvariant().Split('+'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                switch (part)
                {
                    case "ctrl": case "control": mods.Append('^'); break;
                    case "alt": mods.Append('%'); break;
                    case "shift": mods.Append('+'); break;
                    default: key = part; break;
                }
            }
            if (key == null) return null;

            string keyToken;
            switch (key)
            {
                case "enter": case "return": keyToken = "{ENTER}"; break;
                case "up": keyToken = "{UP}"; break;
                case "down": keyToken = "{DOWN}"; break;
                case "left": keyToken = "{LEFT}"; break;
                case "right": keyToken = "{RIGHT}"; break;
                case "delete": case "del": keyToken = "{DEL}"; break;
                case "insert": case "ins": keyToken = "{INS}"; break;
                case "home": keyToken = "{HOME}"; break;
                case "end": keyToken = "{END}"; break;
                case "pgup": case "pageup": keyToken = "{PGUP}"; break;
                case "pgdn": case "pagedown": keyToken = "{PGDN}"; break;
                case "tab": keyToken = "{TAB}"; break;
                case "escape": case "esc": keyToken = "{ESC}"; break;
                case "space": keyToken = " "; break;
                case "backspace": keyToken = "{BACKSPACE}"; break;
                default:
                    if (key.Length >= 2 && key[0] == 'f' && int.TryParse(key.Substring(1), out var fn) && fn >= 1 && fn <= 24)
                        keyToken = "{F" + fn + "}";
                    else if (key.Length == 1)
                    {
                        // Escape SendKeys specials for single characters
                        var c = key[0];
                        keyToken = "+^%~(){}[]".IndexOf(c) >= 0 ? "{" + c + "}" : key;
                    }
                    else
                        return null;
                    break;
            }
            return mods + keyToken;
        }
    }
}
