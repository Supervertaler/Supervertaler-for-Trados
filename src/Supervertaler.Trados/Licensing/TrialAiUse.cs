using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.Trados.Licensing
{
    /// <summary>
    /// Whether a trial installation has used the AI, for the trial check-in
    /// (19 Nov 2026): 41% of trials never check in again after their first day,
    /// and the dashboard could not tell whether they got stuck before their
    /// first AI request (no key) or after it. Read from the usage ledger the
    /// plugin already keeps (UsageStore, one JSON line per AI request, metadata
    /// only), so nothing new is stored. Only a count and a day leave the
    /// computer: no project, file, model or text.
    ///
    /// <para><b>Scale.</b> Reads the monthly files oldest first and stops after
    /// <see cref="Cap"/> successful requests or <see cref="MaxLines"/> lines,
    /// whichever comes first: a check-in never reads more than that, however
    /// long the ledger. A trial installation has a few dozen lines.</para>
    /// </summary>
    internal static class TrialAiUse
    {
        /// <summary>Counted up to here; more is reported as this.</summary>
        internal const int Cap = 100;
        internal const int MaxLines = 5000;

        /// <summary>Unknown: no successful request in the ledger, and the ledger is switched off.</summary>
        internal const int Unknown = -1;

        private static readonly Regex OkLine = new Regex("\"ok\"\\s*:\\s*true", RegexOptions.Compiled);
        private static readonly Regex Ts = new Regex("\"ts\"\\s*:\\s*\"(\\d{4}-\\d{2}-\\d{2})", RegexOptions.Compiled);

        /// <summary>
        /// Successful AI requests in the ledger in <paramref name="usageDir"/>,
        /// up to <see cref="Cap"/>, and the UTC day of the first (yyyy-MM-dd,
        /// null when none). <see cref="Unknown"/> when there is none and
        /// <paramref name="ledgerOn"/> is false: a user who switched the
        /// ledger off may have made requests it did not record. Never throws:
        /// an unreadable folder or line counts as nothing found.
        /// </summary>
        internal static int Count(string usageDir, bool ledgerOn, out string firstDay)
        {
            firstDay = null;
            int ok = 0, lines = 0;
            try
            {
                if (!string.IsNullOrEmpty(usageDir) && Directory.Exists(usageDir))
                {
                    // usage-yyyy-MM.jsonl: the name sorts by month.
                    var files = Directory.GetFiles(usageDir, "usage-*.jsonl")
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        foreach (var line in ReadLines(file))
                        {
                            if (++lines > MaxLines || ok >= Cap) break;
                            if (!OkLine.IsMatch(line)) continue;
                            ok++;
                            if (firstDay == null)
                            {
                                var m = Ts.Match(line);
                                if (m.Success) firstDay = m.Groups[1].Value;
                            }
                        }
                        if (lines > MaxLines || ok >= Cap) break;
                    }
                }
            }
            catch { /* nothing found */ }
            if (ok == 0 && !ledgerOn) return Unknown;
            return Math.Min(ok, Cap);
        }

        private static System.Collections.Generic.IEnumerable<string> ReadLines(string file)
        {
            // Shared read: the ledger may be appended to while this reads.
            FileStream fs;
            try { fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch { yield break; }
            using (fs)
            using (var reader = new StreamReader(fs))
            {
                string line;
                while (true)
                {
                    try { line = reader.ReadLine(); }
                    catch { yield break; }
                    if (line == null) yield break;
                    yield return line;
                }
            }
        }
    }
}
