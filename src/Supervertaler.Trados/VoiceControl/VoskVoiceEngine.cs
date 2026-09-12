using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// Engine abstraction so alternative recognisers (SAPI, cloud) could be
    /// slotted in later without touching the manager/executor.
    /// </summary>
    internal interface IVoiceEngine : IDisposable
    {
        /// <summary>Raised on a background thread with each recognised phrase.</summary>
        event Action<string> Recognized;
        void Start(List<string> grammarPhrases);
        void UpdateGrammar(List<string> grammarPhrases);
        void Stop();

        /// <summary>
        /// Re-recognises the utterance just reported, against a different grammar.
        /// Returns null when there is no audio to re-run or the engine cannot.
        /// See <see cref="VoskVoiceEngine.RecognizeLastUtterance"/> for why.
        /// </summary>
        string RecognizeLastUtterance(List<string> grammarPhrases);

        /// <summary>
        /// #127: the same, against a model for another language - the source side,
        /// whose words the command model cannot pronounce. <paramref name="modelDir"/>
        /// is loaded once and kept for the session. Null when it cannot be loaded.
        /// </summary>
        string RecognizeLastUtteranceWith(string modelDir, List<string> grammarPhrases);
    }

    /// <summary>
    /// Vosk in grammar mode – the recogniser is constrained to the command
    /// phrases (plus "[unk]" for everything else), which is what makes
    /// commands fast (~30 ms) and near-perfect: it literally cannot
    /// mis-hear a command as anything but another command or [unk].
    /// Same approach as Workbench's ContinuousVoiceListener.
    /// </summary>
    internal sealed class VoskVoiceEngine : IVoiceEngine
    {
        private IntPtr _model;
        private IntPtr _recognizer;
        private WaveInCapture _capture;
        private readonly object _lock = new object();

        public event Action<string> Recognized;

        public void Start(List<string> grammarPhrases)
        {
            if (!VoskNative.Preload(VoiceRuntimeInstaller.LibVoskPath))
                throw new InvalidOperationException("libvosk.dll could not be loaded.");

            VoskNative.vosk_set_log_level(-1); // silence libvosk's stderr chatter

            _model = VoskNative.vosk_model_new(VoskNative.Utf8(VoiceRuntimeInstaller.ModelDir));
            if (_model == IntPtr.Zero)
                throw new InvalidOperationException("The voice model could not be loaded (it may be corrupt – delete the trados/voice/models folder to re-download).");

            _recognizer = VoskNative.vosk_recognizer_new_grm(_model, 16000f, GrammarJson(grammarPhrases));
            if (_recognizer == IntPtr.Zero)
                throw new InvalidOperationException("The voice recogniser could not be created.");

            _capture = new WaveInCapture();
            _capture.DataAvailable += OnAudio;
            _capture.Start();
        }

        /// <summary>Rebuilds the grammar live after the command set changes.</summary>
        public void UpdateGrammar(List<string> grammarPhrases)
        {
            lock (_lock)
            {
                if (_model == IntPtr.Zero) return;
                var fresh = VoskNative.vosk_recognizer_new_grm(_model, 16000f, GrammarJson(grammarPhrases));
                if (fresh == IntPtr.Zero) return;
                var old = _recognizer;
                _recognizer = fresh;
                if (old != IntPtr.Zero) VoskNative.vosk_recognizer_free(old);
            }
        }

        private static byte[] GrammarJson(List<string> phrases)
        {
            // Hand-rolled JSON array – phrases are plain lowercase words, but
            // escape quotes/backslashes defensively.
            var sb = new StringBuilder("[");
            foreach (var p in phrases.Concat(new[] { "[unk]" }))
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"').Append(p.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            sb.Append(']');
            return VoskNative.Utf8(sb.ToString());
        }

        /// <summary>
        /// The audio of the utterance in progress, and of the one just finished.
        ///
        /// <para>Kept so that an utterance can be recognised a SECOND time against a
        /// different grammar - see <see cref="RecognizeLastUtterance"/>. Recognition
        /// has already happened by the time anyone can see what was said, so the only
        /// way to reconsider it is to still have the audio.</para>
        ///
        /// <para>Bounded: 16 kHz mono 16-bit is 32 KB per second, so the cap is about
        /// 320 KB. Speech that runs past it drops its OLDEST audio rather than its
        /// newest, because an over-long utterance is nearly always silence or noise
        /// with the real words at the end. Without a cap, leaving the microphone on
        /// in a quiet room would grow this until the utterance finally ended.</para>
        /// </summary>
        private const int MaxUtteranceBytes = 10 * 16000 * 2;
        private readonly List<byte[]> _utterance = new List<byte[]>();
        private int _utteranceBytes;
        private byte[] _lastUtterance;

        private void OnAudio(byte[] data, int length)
        {
            string resultJson = null;
            lock (_lock)
            {
                if (_recognizer == IntPtr.Zero) return;

                var chunk = new byte[length];
                Array.Copy(data, chunk, length);
                _utterance.Add(chunk);
                _utteranceBytes += length;
                while (_utteranceBytes > MaxUtteranceBytes && _utterance.Count > 1)
                {
                    _utteranceBytes -= _utterance[0].Length;
                    _utterance.RemoveAt(0);
                }

                // Returns 1 when an utterance ended (silence after speech)
                if (VoskNative.vosk_recognizer_accept_waveform(_recognizer, data, length) == 1)
                {
                    resultJson = VoskNative.ReadUtf8(VoskNative.vosk_recognizer_result(_recognizer));

                    // Snapshot before clearing: the re-run happens while this result
                    // is being handled, and the next utterance starts collecting
                    // immediately.
                    _lastUtterance = Flatten(_utterance, _utteranceBytes);
                    _utterance.Clear();
                    _utteranceBytes = 0;

                    DumpUtteranceIfAsked(_lastUtterance);
                }
            }
            if (resultJson == null) return;

            var text = ExtractText(resultJson);
            if (string.IsNullOrWhiteSpace(text)) return;
            // Strip [unk] tokens; if nothing else remains, it wasn't a command
            text = text.Replace("[unk]", " ").Trim();
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            if (text.Length == 0) return;

            try { Recognized?.Invoke(text); } catch { }
        }

        /// <summary>
        /// Writes each finished utterance to disk as a WAV, but only when the
        /// SUPERVERTALER_VOICE_DUMP environment variable is set. For measuring other
        /// recognisers against the translator's real voice and microphone - the
        /// question of whether a different engine can hear the words Vosk cannot is
        /// not answerable with synthesised audio.
        ///
        /// <para>Off by default and gated by an env var rather than a setting, so it
        /// cannot be left on by accident: recorded speech is not something to keep
        /// without meaning to.</para>
        /// </summary>
        private static readonly string DumpDir = Environment.GetEnvironmentVariable("SUPERVERTALER_VOICE_DUMP");

        private static void DumpUtteranceIfAsked(byte[] pcm16k)
        {
            if (string.IsNullOrWhiteSpace(DumpDir) || pcm16k == null || pcm16k.Length == 0) return;
            try
            {
                System.IO.Directory.CreateDirectory(DumpDir);
                var path = System.IO.Path.Combine(DumpDir,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".wav");
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create))
                using (var w = new System.IO.BinaryWriter(fs))
                {
                    // Minimal RIFF/WAVE header: 16 kHz, mono, 16-bit PCM.
                    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    w.Write(36 + pcm16k.Length);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                    w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                    w.Write(16); w.Write((short)1); w.Write((short)1);
                    w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    w.Write(pcm16k.Length);
                    w.Write(pcm16k);
                }
            }
            catch { /* a failed dump must never touch recognition */ }
        }

        private static byte[] Flatten(List<byte[]> chunks, int total)
        {
            var all = new byte[total];
            var at = 0;
            foreach (var c in chunks)
            {
                Array.Copy(c, 0, all, at, c.Length);
                at += c.Length;
            }
            return all;
        }

        /// <summary>
        /// Recognises the utterance just reported again, against a different grammar.
        ///
        /// <para><b>Why this exists.</b> The grammar is closed and the recogniser
        /// cannot return nothing, so every command phrase competes for every sound in
        /// every segment. Our own "term eight" put "eight" into the vocabulary, the
        /// article "a" sounds exactly like it, and "select a further" could not be
        /// said at all. Patching the known collisions one at a time cannot scale: any
        /// command a user adds can create a new one.</para>
        ///
        /// <para>Switching grammar when "select" is heard is impossible - by then the
        /// utterance has already been recognised. So it is recognised a second time
        /// instead, against a grammar holding only the segment's own words. Words that
        /// are not in that list cannot be returned at all, which removes the whole
        /// class of collisions rather than the three we happen to know about.</para>
        ///
        /// <para>A fresh recogniser is cheap: <c>vosk_recognizer_new_grm</c> takes the
        /// model as a parameter, and the model - the expensive part - is already
        /// loaded and shared.</para>
        ///
        /// <para><b>What it costs</b> (.dev/second-pass-scaling.ps1, 2026-09-12):
        /// building the recogniser is 0-2 ms and does NOT scale with the number of
        /// segment words - 10 words and 50 words measure the same. The whole cost is
        /// decoding, and that scales only with how long the translator spoke: about
        /// 40-55 ms per second of silence, and roughly double that for speech, which
        /// matches the 97-390 ms seen live. So a long SEGMENT is free and only a long
        /// UTTERANCE is not - and a "select ..." phrase is a few words by nature.</para>
        /// </summary>
        public string RecognizeLastUtterance(List<string> grammarPhrases)
        {
            IntPtr model;
            lock (_lock) { model = _model; }
            return RecognizeWith(model, grammarPhrases);
        }

        /// <summary>
        /// #127: a second model, for the source language, loaded on first use and kept
        /// for the session.
        ///
        /// <para>Loading is the expensive part - a model is tens of megabytes of FST -
        /// so it is cached by directory. Two models is the realistic ceiling: one
        /// project has one source language.</para>
        /// </summary>
        private readonly Dictionary<string, IntPtr> _extraModels =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);

        public string RecognizeLastUtteranceWith(string modelDir, List<string> grammarPhrases)
        {
            if (string.IsNullOrWhiteSpace(modelDir)) return null;

            IntPtr model;
            lock (_lock)
            {
                if (!_extraModels.TryGetValue(modelDir, out model))
                {
                    model = VoskNative.vosk_model_new(VoskNative.Utf8(modelDir));
                    // Cached even when it failed, so a broken model is not retried on
                    // every utterance - each attempt would stall the audio thread.
                    _extraModels[modelDir] = model;
                    Core.DiagnosticLog.WriteAlways("Voice",
                        "source model " + (model == IntPtr.Zero ? "FAILED to load" : "loaded") + ": " + modelDir);
                }
            }
            if (model == IntPtr.Zero) return null;
            return RecognizeWith(model, grammarPhrases);
        }

        private string RecognizeWith(IntPtr model, List<string> grammarPhrases)
        {
            byte[] audio;
            lock (_lock) { audio = _lastUtterance; }
            if (audio == null || audio.Length == 0 || model == IntPtr.Zero) return null;
            if (grammarPhrases == null || grammarPhrases.Count == 0) return null;

            var rec = IntPtr.Zero;
            try
            {
                rec = VoskNative.vosk_recognizer_new_grm(model, 16000f, GrammarJson(grammarPhrases));
                if (rec == IntPtr.Zero) return null;

                // Fed in one block; the endpointing that splits live audio into
                // utterances does not matter here, because the clip IS one utterance
                // and the final result is taken regardless.
                VoskNative.vosk_recognizer_accept_waveform(rec, audio, audio.Length);
                var json = VoskNative.ReadUtf8(VoskNative.vosk_recognizer_final_result(rec));

                var text = ExtractText(json).Replace("[unk]", " ").Trim();
                while (text.Contains("  ")) text = text.Replace("  ", " ");
                return text.Length == 0 ? null : text;
            }
            catch { return null; }
            finally
            {
                if (rec != IntPtr.Zero) VoskNative.vosk_recognizer_free(rec);
            }
        }

        /// <summary>Pulls "text" out of Vosk's {"text" : "..."} result JSON.</summary>
        internal static string ExtractText(string json)
        {
            var idx = json.IndexOf("\"text\"", StringComparison.Ordinal);
            if (idx < 0) return "";
            var colon = json.IndexOf(':', idx);
            var q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            var q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        public void Stop()
        {
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            lock (_lock)
            {
                if (_recognizer != IntPtr.Zero) { VoskNative.vosk_recognizer_free(_recognizer); _recognizer = IntPtr.Zero; }
                if (_model != IntPtr.Zero) { VoskNative.vosk_model_free(_model); _model = IntPtr.Zero; }
                // #127: source-language models too - tens of megabytes each, and a
                // failed load is stored as Zero, which must not be freed.
                foreach (var m in _extraModels.Values)
                    if (m != IntPtr.Zero) VoskNative.vosk_model_free(m);
                _extraModels.Clear();
                // Recorded speech must not outlive the session that captured it.
                _utterance.Clear();
                _utteranceBytes = 0;
                _lastUtterance = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
