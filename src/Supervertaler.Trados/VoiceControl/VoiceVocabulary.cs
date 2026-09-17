using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Supervertaler.Trados.VoiceControl
{
    /// <summary>
    /// The words a Vosk model can hear at all (issue #126).
    ///
    /// <para><b>Why this exists.</b> The recogniser runs in grammar mode, and a
    /// grammar word the model's lexicon does not contain is dropped without a word
    /// to anyone - libvosk logs "Ignoring word missing in vocabulary" to a stderr we
    /// silence. So "select adsorbent" became "select add", and the translator was
    /// told there was no "add" in the target: a true statement about a word never
    /// spoken, hiding the actual cause. Measured on a real patent (2026-09-17): the
    /// small English model does not know 24% of the distinct target words, and 34%
    /// of those of seven letters or more - the technical vocabulary. The Dutch model,
    /// 39% and 47% of the source. This is the ordinary case, not an edge.</para>
    ///
    /// <para><b>Where the words come from.</b> The small models ship no words.txt,
    /// but <c>graph/Gr.fst</c> carries its output symbol table in the OpenFst binary
    /// format: a magic number, a name, two counts, then (word, id) pairs. 152,217
    /// words for the English model, 100,006 for the Dutch, read once per model in
    /// well under a second and kept as a hash set. Checked against a Python parse of
    /// the same files before this was written.</para>
    ///
    /// <para><b>What it is for.</b> Telling the truth when a selection fails:
    /// "adsorbent" cannot be heard, rather than no "add" in the target. And, when
    /// #128 exists, the same moment is where selecting by number gets offered - the
    /// deterministic route to exactly these words.</para>
    /// </summary>
    internal static class VoiceVocabulary
    {
        private const int SymbolTableMagic = 2125658996;   // OpenFst kSymbolTableMagicNumber

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, HashSet<string>> _byModel =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The model's vocabulary, loaded on first use and cached. Null when the
        /// model directory or its Gr.fst is missing, or the table cannot be read -
        /// in which case callers say nothing about hearability rather than guess.
        /// </summary>
        public static HashSet<string> Get(string modelDir)
        {
            if (string.IsNullOrWhiteSpace(modelDir)) return null;
            lock (_lock)
            {
                HashSet<string> cached;
                if (_byModel.TryGetValue(modelDir, out cached)) return cached;
            }
            HashSet<string> words = null;
            try
            {
                var fst = Path.Combine(modelDir, "graph", "Gr.fst");
                if (File.Exists(fst)) words = ParseSymbolTable(File.ReadAllBytes(fst));
                Core.DiagnosticLog.Log("Voice", "vocabulary for " + Path.GetFileName(modelDir) + ": "
                    + (words == null ? "not readable" : words.Count + " words"));
            }
            catch (Exception ex)
            {
                try { Core.DiagnosticLog.Log("Voice", "vocabulary read failed: " + ex.Message); } catch { }
            }
            lock (_lock) { _byModel[modelDir] = words; }   // a failure is cached too: no retry storm
            return words;
        }

        /// <summary>Loads in the background so the first failure message has it.</summary>
        public static void Preload(string modelDir)
        {
            if (string.IsNullOrWhiteSpace(modelDir)) return;
            lock (_lock) { if (_byModel.ContainsKey(modelDir)) return; }
            System.Threading.Tasks.Task.Run(() => Get(modelDir));
        }

        /// <summary>
        /// The words of a segment the model cannot hear, in segment order, distinct.
        /// Empty when the vocabulary is unknown - absence of evidence is not a claim.
        /// </summary>
        public static List<string> Unhearable(string modelDir, IEnumerable<string> segmentWords)
        {
            var result = new List<string>();
            var vocab = Get(modelDir);
            if (vocab == null || segmentWords == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in segmentWords)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                var key = w.Trim().ToLowerInvariant();
                if (key.Length < 2 || !key.Any(char.IsLetter)) continue;
                if (vocab.Contains(key) || !seen.Add(key)) continue;
                result.Add(w.Trim());
            }
            return result;
        }

        /// <summary>
        /// Why a spoken phrase found nothing, when the answer is that the model could
        /// never have heard the word. Null when hearability is not the explanation
        /// (or is unknown), so the caller keeps its ordinary message.
        ///
        /// <para>Two shapes. When some unhearable word BEGINS with a heard word -
        /// "add" for "adsorbent", the recogniser having matched the sound to the
        /// nearest word it knew - that word is named: it is almost certainly the one
        /// meant. Otherwise the count is given, with a few examples, so the
        /// translator knows the segment has words the model cannot take.</para>
        /// </summary>
        public static string ExplainMiss(string modelDir, string spoken, IEnumerable<string> segmentWords,
                                         string modelLabel)
        {
            var unheard = Unhearable(modelDir, segmentWords);
            if (unheard.Count == 0) return null;

            var heardWords = (spoken ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant()).Where(w => w.Length >= 3).ToList();
            // Not a prefix test: "adsorbent" is a-d-S and the recogniser heard
            // a-d-D, the nearest SOUND it knew. Two shared leading letters out of
            // three is the signal, so: a common prefix of at least two letters and
            // at least half the heard word, against the (small) unhearable set.
            var named = unheard.Where(u => heardWords.Any(h =>
                u.Length > h.Length && CommonPrefix(u, h) >= Math.Max(2, (h.Length + 1) / 2))).ToList();

            if (named.Count > 0)
            {
                return Quote(named) + (named.Count == 1 ? " cannot" : " cannot")
                     + " be heard - not in " + modelLabel + "'s vocabulary";
            }
            var sample = string.Join(", ", unheard.Take(3));
            return "no \"" + spoken + "\" here - " + unheard.Count + (unheard.Count == 1 ? " word" : " words")
                 + " in this segment cannot be heard by " + modelLabel + " (" + sample
                 + (unheard.Count > 3 ? ", …" : "") + ")";
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        private static string Quote(List<string> words)
        {
            if (words.Count == 1) return "\"" + words[0] + "\"";
            if (words.Count == 2) return "\"" + words[0] + "\" and \"" + words[1] + "\"";
            return string.Join(", ", words.Take(words.Count - 1).Select(w => "\"" + w + "\""))
                 + " and \"" + words[words.Count - 1] + "\"";
        }

        /// <summary>
        /// Finds the largest OpenFst symbol table in an FST file and returns its
        /// words. Layout after the magic: int32 name length, name, int64 available
        /// key, int64 size, then size × (int32 length, utf-8 word, int64 key). The
        /// header is scanned for the magic rather than parsed, because the FST
        /// header's own layout varies by arc type and the table's does not.
        /// </summary>
        internal static HashSet<string> ParseSymbolTable(byte[] b)
        {
            HashSet<string> best = null;
            var magic = BitConverter.GetBytes(SymbolTableMagic);
            for (int i = IndexOf(b, magic, 0); i >= 0; i = IndexOf(b, magic, i + 4))
            {
                try
                {
                    int p = i + 4;
                    int nameLen = BitConverter.ToInt32(b, p); p += 4;
                    if (nameLen < 0 || nameLen > 1024) continue;
                    p += nameLen;
                    p += 8;                                          // available_key
                    long size = BitConverter.ToInt64(b, p); p += 8;
                    if (size <= 0 || size > 5_000_000) continue;
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    for (long k = 0; k < size; k++)
                    {
                        int len = BitConverter.ToInt32(b, p); p += 4;
                        if (len < 0 || len > 200 || p + len + 8 > b.Length) { set = null; break; }
                        set.Add(Encoding.UTF8.GetString(b, p, len)); p += len + 8;
                    }
                    if (set != null && (best == null || set.Count > best.Count)) best = set;
                }
                catch { /* not a table after all; keep scanning */ }
            }
            return best;
        }

        private static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = Math.Max(0, from); i <= hay.Length - needle.Length; i++)
            {
                if (hay[i] != needle[0]) continue;
                int k = 1;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }
    }
}
