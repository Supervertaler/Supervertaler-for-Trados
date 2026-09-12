using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Hands dictation over to an external tool and takes it back (issue #125).
    ///
    /// <para><b>What this is for.</b> Selection is done by the plugin, because it
    /// needs the segment's own words and a closed vocabulary. Replacement text is
    /// not: it is free prose, often in a second language, and a grammar-constrained
    /// recogniser cannot hear words it was not given. The translator already runs a
    /// dictation tool that does that well, so the plugin's job is to start it, stay
    /// out of the way, and stop it - on command, without the keyboard.</para>
    ///
    /// <para><b>The part an external macro tool cannot do.</b> While the translator
    /// is dictating, every word they say is still reaching our recogniser. Its
    /// grammar is closed, so only our own command phrases can be heard - but a
    /// replacement containing "confirm" or "next segment" would fire one, mid
    /// sentence, into the document. Because it was OUR command that started the
    /// dictation, we know we are in it, and can ignore everything except the way
    /// out. A macro tool driving the same hotkey has no idea what state the
    /// dictation tool is in.</para>
    ///
    /// <para>"Stop listening" is exempt from that gate as well. If the recogniser
    /// ever mishears the way out, the gate would otherwise leave voice with no
    /// spoken exit at all - the keyboard toggle still works, but a mode you can
    /// only leave by reaching for the keyboard is a poor one in a feature whose
    /// point is not having to.</para>
    ///
    /// <para><b>The trigger is per-installation.</b> Dictation tools differ, and one
    /// tool offers several. It is therefore carried in the command itself -
    /// <c>dictate_toggle:ctrl+win+space</c> - editable in the same grid as every
    /// other command, with no new settings surface. That chord is the default
    /// hands-free shortcut of the tool this was built against, so nothing has to be
    /// configured on either side; <c>dictate_toggle:middleclick</c> is accepted too,
    /// for a tool that listens for one.</para>
    ///
    /// <para><b>Not handled, deliberately.</b> The stop word is still being listened
    /// to by the dictation tool when it is spoken, so it lands in the text. Deleting
    /// it automatically means racing that tool's own paste, and a mistimed backspace
    /// eats the translation rather than the stop word. Left alone until there is
    /// evidence about how often it actually bites.</para>
    /// </summary>
    internal static class DictationMode
    {
        private const string LogCategory = "Dictation";

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        /// <summary>
        /// True while the translator is dictating into the external tool. Read by the
        /// executor to suppress every command but the way out.
        /// </summary>
        public static bool Active { get; private set; }

        /// <summary>Raised when the mode changes, so the status strip can say so.</summary>
        public static event Action<bool> Changed;

        /// <summary>
        /// Flips dictation on or off and sends the trigger the external tool is
        /// listening for. <paramref name="trigger"/> is the part of the action id
        /// after the colon: a chord like "ctrl+alt+w", or "middleclick".
        /// </summary>
        /// <summary>
        /// #125: the fixed string the dictation tool is configured to write in place
        /// of the spoken stop phrase, or null when it writes the words themselves.
        ///
        /// <para>The tool's own dictionary will not map a phrase to nothing, but it
        /// will map it to a marker - and that is better than nothing would have been.
        /// Spoken, the stop phrase arrives formatted: sentence-cased, with a full
        /// stop, as ". Dictate". A marker collapses every such variant into one fixed
        /// string, so removing it is a search for a known literal rather than a guess
        /// about punctuation.</para>
        /// </summary>
        public static string StopMarker { get; private set; }

        public static void Toggle(string trigger, string stopMarker = null)
        {
            if (!string.IsNullOrWhiteSpace(stopMarker)) StopMarker = stopMarker.Trim();
            var going = !Active;

            // Flip BEFORE sending, so the recogniser is already gated when the tool
            // starts listening - the translator may begin speaking immediately.
            Active = going;
            try { Changed?.Invoke(going); } catch { }

            var sent = SendTrigger(trigger);
            DiagnosticLog.WriteAlways(LogCategory,
                (going ? "ON" : "OFF") + " via \"" + (trigger ?? "(none)") + "\""
                + (sent ? "" : " - TRIGGER NOT SENT, the dictation tool was not told"));

            // Coming out of dictation, the marker has not been pasted yet - the tool
            // transcribes and pastes after it stops. So the removal waits for the
            // text to arrive rather than guessing a delay: a fixed sleep would
            // sometimes fire before the paste and do nothing, and sometimes after a
            // slow paste and still do nothing.
            if (!going && !string.IsNullOrWhiteSpace(StopMarker))
                TermLensEditorViewPart.VoiceAwaitAndRemoveMarker(StopMarker);
        }

        /// <summary>
        /// Forces the mode off without sending anything. For when listening stops
        /// altogether: leaving the gate up would silently swallow every command on
        /// the next session.
        /// </summary>
        public static void Reset()
        {
            if (!Active) return;
            Active = false;
            try { Changed?.Invoke(false); } catch { }
            DiagnosticLog.Log(LogCategory, "reset (voice stopped while dictating)");
        }

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const byte VK_LWIN = 0x5B, VK_SPACE = 0x20;

        /// <summary>
        /// Presses a chord like "ctrl+win+space" - the dictation tool's own default.
        ///
        /// <para>Not SendKeys, which the rest of the voice executor uses: SendKeys
        /// has no way to express the Windows key, and the default trigger needs it.
        /// keybd_event takes virtual key codes, so it can.</para>
        /// </summary>
        private static bool SendChord(string chord)
        {
            var parts = chord.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            var down = new System.Collections.Generic.List<byte>();
            byte key = 0;

            foreach (var raw in parts)
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": down.Add(VK_CONTROL); break;
                    case "alt": down.Add(VK_MENU); break;
                    case "shift": down.Add(VK_SHIFT); break;
                    case "win": case "windows": case "meta": down.Add(VK_LWIN); break;
                    case "space": key = VK_SPACE; break;
                    default:
                        var token = raw.Trim();
                        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
                            key = (byte)char.ToUpperInvariant(token[0]);
                        break;
                }
            }
            if (key == 0) return false;

            foreach (var m in down) keybd_event(m, 0, 0, IntPtr.Zero);
            keybd_event(key, 0, 0, IntPtr.Zero);
            keybd_event(key, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            // Modifiers released in reverse, so nothing is left latched if the
            // dictation tool grabs focus between the presses.
            for (int i = down.Count - 1; i >= 0; i--)
                keybd_event(down[i], 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            return true;
        }

        private static bool SendTrigger(string trigger)
        {
            var t = (trigger ?? "").Trim();
            if (t.Length == 0) return false;

            try
            {
                if (string.Equals(t, "middleclick", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t, "middle click", StringComparison.OrdinalIgnoreCase))
                {
                    // Wherever the pointer happens to be. Harmless in the Trados grid -
                    // measured - but it IS positional: over an embedded browser, such
                    // as the SuperSearch pane, a middle click means something else.
                    mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, IntPtr.Zero);
                    mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, IntPtr.Zero);
                    return true;
                }

                return SendChord(t);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(LogCategory, "sending \"" + t + "\" failed: " + ex.Message);
                return false;
            }
        }
    }
}
