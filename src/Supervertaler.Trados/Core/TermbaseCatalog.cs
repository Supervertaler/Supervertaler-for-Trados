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
        public static List<TermbaseRef> Build(string dbPath, IEnumerable<MultiTermTermbaseConfig> projectTermbases)
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
                                    SourceLang = tb.SourceLang, TargetLang = tb.TargetLang
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
                    FilePath = c.FilePath, SourceLang = c.SourceIndexName, TargetLang = c.TargetIndexName
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

            error = $"no termbase called '{w}'. " + Available(all);
            return null;
        }

        /// <summary>"Available: A (id 1), B (id -42)", or why there are none.</summary>
        public static string Available(IReadOnlyList<TermbaseRef> all)
            => all == null || all.Count == 0
                ? "No termbases are available (none in Supervertaler, and no Trados project termbase is loaded - " +
                  "those appear once a document is open in the Editor)."
                : "Available: " + string.Join(", ", all) + ".";

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
