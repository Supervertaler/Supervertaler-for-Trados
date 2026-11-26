using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Supervertaler.Trados.Models;

namespace Supervertaler.Trados.Core
{
    /// <summary>One termbase under the single name and id that every MCP
    /// termbase tool reports and accepts.</summary>
    internal sealed class TermbaseRef
    {
        /// <summary>Supervertaler termbases: the database row id (positive).
        /// Trados project termbases: the stable synthetic id derived from the
        /// file path (negative; see <see cref="MultiTermProjectDetector.MakeStableSyntheticId"/>).</summary>
        public long Id;
        /// <summary>Supervertaler: the termbase's name. Trados: the name the
        /// project's termbase settings give it - the name lookup_term and
        /// check_terminology see on its entries.</summary>
        public string Name;
        /// <summary>"supervertaler", "trados-ttb" or "multiterm".</summary>
        public string Kind;
        /// <summary>Trados termbases only.</summary>
        public string FilePath;
        /// <summary>Supervertaler: language codes. Trados: the project's index names.</summary>
        public string SourceLang;
        public string TargetLang;
        /// <summary>This job's termbase: a Trados project termbase, or the
        /// Supervertaler termbase ticked Project. Named in a not-found error.</summary>
        public bool IsProject;

        public bool IsTrados => Kind != TermbaseCatalog.KindSupervertaler;

        /// <summary>"Name (id N)" - how errors name a termbase.</summary>
        public override string ToString() => $"{Name} (id {Id})";
    }

    /// <summary>
    /// The termbases the tools can see - Supervertaler's and the open project's
    /// Trados termbases - and the one rule for turning what a caller typed into
    /// one of them. Before this, list_resources named a Trados termbase by its
    /// file name while lookup_term and check_terminology used the project's own
    /// name for it plus a " [Trados project termbase]" suffix, so a name copied
    /// from one tool matched nothing in another and check_terminology's filter
    /// silently checked nothing.
    /// </summary>
    internal static class TermbaseCatalog
    {
        public const string KindSupervertaler = "supervertaler";
        public const string KindTtb = "trados-ttb";
        public const string KindMultiTerm = "multiterm";

        /// <summary>The suffix lookup_term used to add to Trados termbase names
        /// (up to 18.20.209). Still accepted, so a name an assistant copied from
        /// an older reply keeps working.</summary>
        public const string LegacyTradosSuffix = " [Trados project termbase]";

        public static string KindOfFile(string path)
            => (path ?? "").EndsWith(".ttb", StringComparison.OrdinalIgnoreCase) ? KindTtb : KindMultiTerm;

        /// <summary>Every termbase: Supervertaler's (from the database at
        /// <paramref name="dbPath"/>, if any) then the project's Trados ones (from
        /// <paramref name="projectTermbases"/>, which TermLens detects for the open
        /// document). Never throws; a database that cannot be read just adds none.</summary>
        public static List<TermbaseRef> Build(string dbPath, IEnumerable<MultiTermTermbaseConfig> projectTermbases,
            long projectTermbaseId = -1)
        {
            var all = new List<TermbaseRef>();
            if (!string.IsNullOrEmpty(dbPath))
            {
                try
                {
                    using (var reader = new TermbaseReader(dbPath))
                    {
                        if (reader.Open())
                            foreach (var tb in reader.GetTermbases() ?? new List<TermbaseInfo>())
                                all.Add(new TermbaseRef
                                {
                                    Id = tb.Id, Name = tb.Name, Kind = KindSupervertaler,
                                    SourceLang = tb.SourceLang, TargetLang = tb.TargetLang,
                                    IsProject = tb.Id == projectTermbaseId
                                });
                    }
                }
                catch { /* no Supervertaler termbases then */ }
            }
            foreach (var c in projectTermbases ?? Enumerable.Empty<MultiTermTermbaseConfig>())
            {
                if (c == null) continue;
                all.Add(new TermbaseRef
                {
                    Id = c.SyntheticId, Name = c.TermbaseName, Kind = KindOfFile(c.FilePath),
                    FilePath = c.FilePath, SourceLang = c.SourceIndexName, TargetLang = c.TargetIndexName,
                    IsProject = true
                });
            }
            return all;
        }

        /// <summary>
        /// The termbase <paramref name="wanted"/> names, or null with
        /// <paramref name="error"/> saying why. Tried in order: the id; the name;
        /// for a Trados termbase its file name (with or without extension) or full
        /// path; the name with the old lookup_term suffix removed. Case is ignored.
        /// A name two termbases share is refused with both ids rather than guessed.
        /// </summary>
        public static TermbaseRef Resolve(IReadOnlyList<TermbaseRef> all, string wanted, out string error)
        {
            error = null;
            all = all ?? new List<TermbaseRef>();
            var w = (wanted ?? "").Trim();
            if (w.Length == 0)
            {
                error = "name the termbase, by name or id as list_resources gives it. " + Available(all);
                return null;
            }

            if (long.TryParse(w, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                var byId = all.FirstOrDefault(t => t.Id == id);
                if (byId != null) return byId;
            }

            var candidates = new List<Func<TermbaseRef, bool>>
            {
                t => Same(t.Name, w),
                t => t.IsTrados && Same(SafeFileName(t.FilePath, false), w),
                t => t.IsTrados && Same(SafeFileName(t.FilePath, true), w),
                t => t.IsTrados && Same(t.FilePath, w),
            };
            if (w.EndsWith(LegacyTradosSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var bare = w.Substring(0, w.Length - LegacyTradosSuffix.Length).Trim();
                candidates.Add(t => t.IsTrados && Same(t.Name, bare));
            }

            foreach (var test in candidates)
            {
                var hits = all.Where(test).ToList();
                if (hits.Count == 1) return hits[0];
                if (hits.Count > 1)
                {
                    error = $"more than one termbase is called '{w}': " + string.Join(", ", hits) +
                            ". Pass the id instead.";
                    return null;
                }
            }

            error = $"no termbase called '{w}'. " + Available(all, w);
            return null;
        }

        /// <summary>A short list of all termbases: every one.</summary>
        public const int ListAllUpTo = 10;
        public const int MaxSuggestions = 5;

        /// <summary>
        /// What a not-found error names. Up to <see cref="ListAllUpTo"/> termbases:
        /// all of them. More (a real setup has well over a hundred): only the
        /// names closest to <paramref name="wanted"/>, this job's termbases, and a
        /// pointer to list_resources - a reply listing 120 termbases buries the
        /// answer and costs the assistant's context for nothing.
        /// </summary>
        public static string Available(IReadOnlyList<TermbaseRef> all, string wanted = null)
        {
            if (all == null || all.Count == 0)
                return "No termbases are available (none in Supervertaler, and no Trados project termbase is loaded - " +
                       "those appear once a document is open in the Editor).";
            if (all.Count <= ListAllUpTo)
                return "Available: " + string.Join(", ", all) + ".";

            var parts = new List<string>();
            var closest = Closest(all, wanted, MaxSuggestions);
            if (closest.Count > 0) parts.Add("Closest: " + string.Join(", ", closest) + ".");
            var project = all.Where(t => t.IsProject && !closest.Contains(t)).ToList();
            if (project.Count > 0) parts.Add("This job's: " + string.Join(", ", project) + ".");
            parts.Add($"list_resources lists all {all.Count}.");
            return string.Join(" ", parts);
        }

        /// <summary>
        /// The termbases whose names are most like <paramref name="wanted"/>,
        /// best first: one name inside the other, words in common, or few
        /// edits apart (a typo, a missing date). Names that are nothing alike
        /// are left out rather than padded in.
        /// </summary>
        internal static List<TermbaseRef> Closest(IReadOnlyList<TermbaseRef> all, string wanted, int max)
        {
            var w = (wanted ?? "").Trim().ToLowerInvariant();
            if (w.Length == 0 || all == null) return new List<TermbaseRef>();
            return all
                .Select(t => new { t, score = Similarity(w, (t.Name ?? "").Trim().ToLowerInvariant()) })
                .Where(x => x.score >= 0.4)
                .OrderByDescending(x => x.score).ThenBy(x => x.t.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(x => x.t)
                .ToList();
        }

        internal static double Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            double best = 0;
            if (a.Contains(b) || b.Contains(a))
                best = 0.6 + 0.4 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);

            var wa = Words(a); var wb = Words(b);
            if (wa.Count > 0 && wb.Count > 0)
                best = Math.Max(best, (double)wa.Intersect(wb).Count() / Math.Min(wa.Count, wb.Count) * 0.9);

            best = Math.Max(best, 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length));
            return best;
        }

        private static HashSet<string> Words(string s)
            => new HashSet<string>(s.Split(new[] { ' ', '-', '_', '.', ',', '(', ')', '[', ']', '/' },
                    StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3 && !x.All(char.IsDigit)));

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }

        private static bool Same(string a, string b)
            => !string.IsNullOrEmpty(a) && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);

        private static string SafeFileName(string path, bool withExtension)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? null
                    : withExtension ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            }
            catch { return null; }
        }
    }
}
