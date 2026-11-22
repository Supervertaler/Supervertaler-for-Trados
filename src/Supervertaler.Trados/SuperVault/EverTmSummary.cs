using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.SuperVault
{
    /// <summary>
    /// The faint line under each EverTM match: Client · Project · date (docs/design/evertm-pane.md, section 1, Michael's
    /// decision of 10 Nov 2026). No Studio types, so the tests check every rule without Studio.
    ///
    /// <list type="bullet">
    /// <item>Missing fields are left out, with their separator; never "(none)".</item>
    /// <item>No Client and no Project: the Source takes their place. The line is never empty: the date is always there
    /// ("undated" without one).</item>
    /// <item>Several values: the first, then "+N".</item>
    /// <item>Too narrow: Client and Project are cut with "…"; the date and the variant are never cut. Below about 15
    /// characters for the cut items, only the date (and variant) is left. The line never wraps.</item>
    /// <item>A variant that differs from the project's is added at the end ("· en-US", or "· nl-NL > en-US" when both
    /// sides differ). A bare language ("en") or a matching variant adds nothing.</item>
    /// </list>
    ///
    /// Cost: a few string operations per row drawn, from the fields already in the match. No lookup, no store read.
    /// </summary>
    public static class EverTmSummary
    {
        const string Sep = " · ";
        const string Ellipsis = "…";
        /// <summary>The cut items need at least this many characters' room, or they are left out.</summary>
        public const int MinCutChars = 15;

        /// <summary>The parts of the line, before fitting: the items that may be cut, and the tail that may not.</summary>
        public sealed class Parts
        {
            public List<string> Cuttable = new List<string>();   // Client, Project (or Source)
            public string Tail;                                  // "4 Nov 2026" or "undated", then the variant
        }

        public static Parts Make(VaultCandidate c, string sourceLang, string projectSource, string projectTarget)
        {
            var p = new Parts();
            string client = FirstPlus(c?.Fields, VaultFields.Client), project = FirstPlus(c?.Fields, VaultFields.Project);
            if (client != null) p.Cuttable.Add(client);
            if (project != null) p.Cuttable.Add(project);
            if (p.Cuttable.Count == 0)
            {
                var source = FirstPlus(c?.Fields, VaultFields.Source);
                if (source != null) p.Cuttable.Add(source);
            }
            p.Tail = Date(c);
            var variant = VariantNote(c?.Variant, sourceLang, projectSource, projectTarget);
            if (variant != null) p.Tail += Sep + variant;
            return p;
        }

        /// <summary>The line, fitted to <paramref name="width"/> as measured by <paramref name="measure"/> (pixels in the
        /// pane; characters in the tests).</summary>
        public static string Fit(Parts p, int width, Func<string, int> measure)
        {
            string full = string.Join(Sep, p.Cuttable.Concat(new[] { p.Tail }));
            if (measure(full) <= width || p.Cuttable.Count == 0) return full;

            // Room left for the cut items once the tail and its separator are placed.
            int room = width - measure(Sep + p.Tail);
            if (room < measure(new string('x', MinCutChars))) return p.Tail;

            // Cut each item from the end until it all fits; the last item gives way first.
            var items = p.Cuttable.ToList();
            string Join() => string.Join(Sep, items);
            for (int i = items.Count - 1; i >= 0 && measure(Join()) > room; i--)
            {
                string original = p.Cuttable[i];
                int keep = original.Length;
                while (keep > 1 && measure(Join()) > room)
                {
                    keep--;
                    items[i] = original.Substring(0, keep).TrimEnd() + Ellipsis;
                }
                if (measure(Join()) > room && i > 0) items.RemoveAt(i);   // an item cut to nothing goes
            }
            if (items.Count == 0 || measure(Join()) > room) return p.Tail;
            return Join() + Sep + p.Tail;
        }

        /// <summary>The first value of the field, then "+N" for the others; null when the unit has none.</summary>
        public static string FirstPlus(List<(string Field, string Value)> fields, string field)
        {
            if (fields == null) return null;
            var values = fields.Where(f => f.Field == field && !string.IsNullOrWhiteSpace(f.Value)).Select(f => f.Value.Trim()).Distinct().ToList();
            if (values.Count == 0) return null;
            return values.Count == 1 ? values[0] : values[0] + " +" + (values.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>When the unit was last changed, else created, as "4 Nov 2026" (local time); "undated" without either.</summary>
        public static string Date(VaultCandidate c)
        {
            var when = c?.Changed ?? c?.Created;
            return when == null ? "undated" : FormatDate(when.Value);
        }

        public static string FormatDate(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        /// <summary>
        /// "en-US" when the unit's target variant differs from the project's, "nl-NL" for the source likewise, and
        /// "nl-NL > en-US" when both do; null when neither does. The unit's variants are stored in the direction it was
        /// stored in ("nl-BE>en-US"), so each side is found by its base language, whichever way round the lookup is.
        /// A side stored without a region ("en") is never flagged.
        /// </summary>
        public static string VariantNote(string stored, string sourceLang, string projectSource, string projectTarget)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            var sides = stored.Split('>').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (sides.Count != 2) return null;
            string src = VaultMarkup.BaseLanguage(sourceLang);
            int at = VaultMarkup.BaseLanguage(sides[0]) == src ? 0 : VaultMarkup.BaseLanguage(sides[1]) == src ? 1 : -1;
            if (at < 0) return null;
            string unitSource = sides[at], unitTarget = sides[1 - at];
            string s1 = Differs(unitSource, projectSource), t1 = Differs(unitTarget, projectTarget);
            if (s1 != null && t1 != null) return s1 + " > " + t1;
            return t1 ?? s1;
        }

        static string Differs(string unit, string project)
        {
            if (unit.IndexOf('-') < 0) return null;              // a bare language: nothing to flag
            if (string.IsNullOrEmpty(project) || project.IndexOf('-') < 0) return null;   // the project has no region either
            return string.Equals(unit, project, StringComparison.OrdinalIgnoreCase) ? null : unit;
        }
    }
}
