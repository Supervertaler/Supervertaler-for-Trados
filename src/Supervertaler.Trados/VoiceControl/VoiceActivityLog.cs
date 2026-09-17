using System;
using System.Collections.Generic;
using System.Linq;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// What SuperVoice has heard and done this session, kept so it can be SEEN
    /// (issue #129).
    ///
    /// <para>Until now every message the voice system produced flashed in the
    /// TermLens status label for a few seconds and was gone: which reading a
    /// selection took, why a word could not be heard, how many candidates there
    /// were. The information that matters most is exactly the information that
    /// disappeared fastest, and it lived in a panel belonging to another feature.
    /// This is the same stream, kept.</para>
    ///
    /// <para>Static and independent of any pane. The manager writes here whether or
    /// not the SuperVoice pane exists, and the pane subscribes when it opens - so
    /// closing the pane costs nothing and opening it shows what already happened.
    /// The old status strip keeps receiving everything too; nothing regresses for a
    /// translator who never opens the pane.</para>
    /// </summary>
    internal static class VoiceActivityLog
    {
        /// <summary>
        /// How many utterances are kept. A session can produce hundreds and the
        /// pane shows the recent ones; this is a display buffer, not a log. The
        /// diagnostic log remains the complete record.
        /// </summary>
        private const int Capacity = 60;

        internal enum Outcome
        {
            /// <summary>Heard, not yet resolved.</summary>
            Pending,
            /// <summary>A command ran, or a selection was made.</summary>
            Done,
            /// <summary>Understood and deliberately declined - an ambiguous or buried word.</summary>
            Refused,
            /// <summary>Nothing matched, or nothing was heard.</summary>
            Missed,
            /// <summary>Heard while dictating, so ignored on purpose.</summary>
            Suppressed
        }

        internal sealed class Entry
        {
            public DateTime Time;
            /// <summary>The raw utterance, as the recogniser returned it.</summary>
            public string Heard;
            /// <summary>What it came to - the command, the words selected, or the reason not.</summary>
            public string Result;
            public Outcome Kind;
        }

        private static readonly object _lock = new object();
        private static readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Raised whenever an entry is added or completed. On the thread
        /// that wrote it, which is usually the audio thread - marshal before touching UI.</summary>
        public static event Action Changed;

        /// <summary>Most recent first, so a pane can render it top-down without reversing.</summary>
        public static List<Entry> Snapshot()
        {
            lock (_lock) return Enumerable.Reverse(_entries).ToList();
        }

        /// <summary>An utterance arrived. Opens an entry for whatever it turns into.</summary>
        public static void Heard(string utterance)
        {
            if (string.IsNullOrWhiteSpace(utterance)) return;
            lock (_lock)
            {
                _entries.Add(new Entry
                {
                    Time = DateTime.Now,
                    Heard = utterance.Trim(),
                    Kind = Outcome.Pending
                });
                while (_entries.Count > Capacity) _entries.RemoveAt(0);
            }
            Raise();
        }

        /// <summary>
        /// What the utterance came to. Attaches to the open entry rather than adding
        /// a row, so one utterance stays one line: heard, then what happened to it.
        ///
        /// <para>An outcome with no entry to attach to - a command fired by a
        /// keystroke, say - opens its own, so nothing is silently dropped.</para>
        /// </summary>
        public static void Resolved(string result, Outcome kind)
        {
            if (string.IsNullOrWhiteSpace(result)) return;
            lock (_lock)
            {
                var open = _entries.LastOrDefault();
                if (open == null || open.Kind != Outcome.Pending)
                {
                    _entries.Add(new Entry { Time = DateTime.Now, Heard = null, Result = result.Trim(), Kind = kind });
                    while (_entries.Count > Capacity) _entries.RemoveAt(0);
                }
                else
                {
                    open.Result = result.Trim();
                    open.Kind = kind;
                }
            }
            Raise();
        }

        /// <summary>Whether the latest utterance is still waiting for its outcome.</summary>
        public static bool HasPending()
        {
            lock (_lock)
            {
                var open = _entries.LastOrDefault();
                return open != null && open.Kind == Outcome.Pending;
            }
        }

        public static void Clear()
        {
            lock (_lock) _entries.Clear();
            Raise();
        }

        private static void Raise()
        {
            // A subscriber that throws must not take down recognition: this runs on
            // the audio thread, and an exception there stops the listener.
            try { Changed?.Invoke(); } catch { }
        }
    }
}
