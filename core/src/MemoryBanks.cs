using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Supervertaler.Core
{
    /// <summary>
    /// Where memory banks live on disk, and what a freshly created one contains.
    ///
    /// The root is shared: Supervertaler for Trados, Supervertaler for memoQ,
    /// Workbench and the Python assistant all read the same
    /// <c>&lt;root&gt;/memory-banks/</c>. That makes the layout a contract between
    /// four programs rather than an implementation detail of any one of them.
    /// </summary>
    public static class MemoryBanks
    {
        public static string Root => Path.Combine(SupervertalerPaths.Root, "memory-banks");

        /// <summary>Names that are not banks: tooling, deletions, and Obsidian's own state.</summary>
        private static readonly HashSet<string> NotBanks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_to_delete", ".trash", ".obsidian" };

        /// <summary>
        /// The banks on disk, alphabetically, with the shared overlay last.
        ///
        /// <c>_shared</c> is included because a caller may legitimately want to
        /// read it, but it is not a bank you select: it is layered underneath
        /// whichever bank you do select, and loses to it where they disagree.
        /// </summary>
        public static IReadOnlyList<string> List()
        {
            try
            {
                if (!Directory.Exists(Root)) return new string[0];

                return Directory.GetDirectories(Root)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Where(n => !NotBanks.Contains(n))
                    .Where(n => !n.StartsWith(".", StringComparison.Ordinal))
                    .OrderBy(n => IsSharedName(n) ? 1 : 0)
                    .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        public static bool IsSharedName(string bankName)
        {
            return !string.IsNullOrWhiteSpace(bankName)
                && string.Equals(bankName.Trim(), MemoryBankReader.SharedBankName,
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The folder for a bank, or null when the name does not resolve to one.
        ///
        /// Returns null rather than a path that does not exist, because every
        /// caller here is answering a question from outside the process - an MCP
        /// tool call naming a bank - and "no such bank" is a better answer than an
        /// empty one.
        /// </summary>
        public static string DirFor(string bankName)
        {
            if (string.IsNullOrWhiteSpace(bankName)) return null;

            var wanted = bankName.Trim();

            // A name that IS one of the folders on disk resolves to that folder,
            // whatever it is called. This has to come first, because Sanitize
            // strips leading and trailing underscores, dots and spaces - so a
            // bank the user called "_archive" was listed by List(), offered in
            // the picker, and then resolved to "archive", which does not exist.
            // The selection failed silently and the prompt simply lost its
            // client rules.
            //
            // Matching against the enumerated directories rather than probing a
            // constructed path is what keeps this safe: the candidate must equal
            // an entry List() returned, so no traversal survives the comparison.
            foreach (var name in List())
            {
                if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                var exact = Path.Combine(Root, name);
                if (Directory.Exists(exact)) return exact;
            }

            // The shared bank is the one name sanitisation must not touch: its
            // leading underscore is exactly what the rule strips. Kept for the
            // case where it exists but List() could not enumerate.
            var safe = IsSharedName(wanted) ? MemoryBankReader.SharedBankName : Sanitize(wanted);
            if (string.IsNullOrEmpty(safe)) return null;

            var dir = Path.Combine(Root, safe);
            return Directory.Exists(dir) ? dir : null;
        }

        /// <summary>
        /// A bank name reduced to something safe to put in a path. Deliberately
        /// strict: these names arrive over a localhost bridge, and a name is never
        /// worth a path traversal.
        /// </summary>
        public static string Sanitize(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "";

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(rawName.Trim()
                .Where(c => Array.IndexOf(invalid, c) < 0)
                .ToArray());

            return cleaned.Trim('.', '_', ' ');
        }

        public static string SkeletonBody(string fileName, string bankName)
        {
            switch (fileName)
            {
                case "brief.md":
                    return "# " + bankName + "\r\n\r\n" +
                "Who this client is and anything standing that applies to all their\r\n" +
                "work: language pair, register, house preferences, things they have\r\n" +
                "asked for or rejected before.\r\n\r\n" +
                "## Standing instructions\r\n\r\n" +
                "- \r\n\r\n" +
                "## How far to trust this\r\n\r\n" +
                "Say where this came from - a supplied style guide is worth more than\r\n" +
                "something inferred from one review round, and future-you cannot tell\r\n" +
                "the difference unless it is written down.\r\n\r\n" +
                "## Files\r\n\r\n" +
                "- [terminology.md](terminology.md) - term decisions, one table\r\n" +
                "- [style.md](style.md) - prose rules and approved boilerplate\r\n" +
                "- `reference/` - source material, unmodified\r\n";

                case "terminology.md":
                    return "# Terminology - " + bankName + "\r\n\r\n" +
                "One row per decision. Keep it a table: a table can be scanned and\r\n" +
                "corrected in seconds, which is the only reason errors get caught.\r\n\r\n" +
                "**Scope** says how far a row travels - `project`, `client`, or\r\n" +
                "`domain`. A row that proves true for a second client belongs in the\r\n" +
                "`" + MemoryBankReader.SharedBankName + "` bank instead; move it there rather than copying it,\r\n" +
                "or the two drift apart.\r\n\r\n" +
                "| Source | Target | Scope | Note |\r\n" +
                "|---|---|---|---|\r\n" +
                "|  |  |  |  |\r\n";

                case "style.md":
                    return "# Style - " + bankName + "\r\n\r\n" +
                "Prose rules and approved boilerplate: how things are phrased, rather\r\n" +
                "than which term is used. Quote the approved wording in full - a rule\r\n" +
                "you have to reconstruct from a description is a rule that gets\r\n" +
                "applied inconsistently.\r\n\r\n" +
                "## 1. \r\n\r\n";

                default:
                    return null;
            }
        }
    }
}
