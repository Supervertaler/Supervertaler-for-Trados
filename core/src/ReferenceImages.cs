using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Supervertaler.Core
{
    /// <summary>
    /// The folder of drawings a user has pointed this project at.
    ///
    /// <para>The premise: you put a job's figures in a folder, tell the plugin
    /// where, and they become reference material for its memory bank. Nothing
    /// here is specific to patents — patents are simply where drawings carry the
    /// most translation-relevant information — so avoid narrowing it to figures
    /// with reference numerals.</para>
    ///
    /// <para><b>Where the folder lives is the user's business, not ours.</b>
    /// Studio projects can be saved anywhere under any name, so there is no
    /// layout to infer from. <see cref="Resolve"/> returns only what the user
    /// set; <see cref="Suggest"/> offers nearby candidates for a dialog to
    /// propose. The distinction is load-bearing: a folder guessed by walking up
    /// the tree can belong to a different job, and drawings from the wrong matter
    /// are worse than no drawings, because the output still looks reasonable.</para>
    ///
    /// <para>Deliberately has no dependency on Studio or on any AI provider, so it
    /// can be exercised without either.</para>
    /// </summary>
    public static class ReferenceImages
    {
        /// <summary>Folder names looked for by convention, in preference order.</summary>
        private static readonly string[] ConventionalNames = { "Images", "Figures", "images", "figures" };

        /// <summary>
        /// Extensions we treat as drawings. Deliberately narrow: a reference
        /// folder full of PDFs and DOCX would otherwise be reported as usable and
        /// then fail in the vision pass, which is a worse failure than saying
        /// "no images found" up front.
        /// </summary>
        private static readonly HashSet<string> ImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

        /// <summary>
        /// The reference-images folder for a project: whatever the user chose,
        /// and nothing else.
        ///
        /// <para><b>There is deliberately no fallback.</b> An earlier version fell
        /// back to a convention search when the setting was unset, which sounds
        /// helpful and is dangerous: project folders have no fixed shape, so a
        /// search that walks upwards can leave the job entirely. A project stored
        /// directly in its job folder, with no <c>Images\</c> of its own, would
        /// have found the one belonging to a SIBLING FILING — or to the client
        /// folder above it — and fed another matter's drawings into this one's
        /// context, silently and plausibly.</para>
        ///
        /// <para>Convention now only ever produces a <see cref="Suggest"/>ion for
        /// the user to accept. Getting drawings from a folder nobody pointed at is
        /// not a feature.</para>
        ///
        /// <para>Returns null when unset or missing. Callers should treat that as
        /// "this project has no drawings", not as an error — most do not.</para>
        /// </summary>
        /// <param name="explicitFolder">The per-project setting; may be null or blank.</param>
        public static string Resolve(string explicitFolder)
        {
            if (string.IsNullOrWhiteSpace(explicitFolder)) return null;
            try
            {
                return Directory.Exists(explicitFolder) ? Path.GetFullPath(explicitFolder) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Folders near the project that look like they hold drawings, for the UI
        /// to offer as a starting point. Never applied automatically.
        ///
        /// <para>Searches the project's own folder and its immediate parent only.
        /// One level up, because a project is commonly kept in a subfolder of the
        /// job folder while the drawings sit beside it — but that is a habit, not
        /// a rule, and going further reaches other jobs. Empty folders are skipped:
        /// suggesting one would waste the user's click.</para>
        ///
        /// <para>Ordered nearest-first, so the most likely candidate leads.</para>
        /// </summary>
        /// <param name="projectFilePath">Path to the .sdlproj file, or its folder.</param>
        public static List<string> Suggest(string projectFilePath)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(projectFilePath)) return found;

            try
            {
                var dir = Directory.Exists(projectFilePath)
                    ? projectFilePath
                    : Path.GetDirectoryName(projectFilePath);

                for (int level = 0; level < 2 && !string.IsNullOrEmpty(dir); level++)
                {
                    foreach (var name in ConventionalNames)
                    {
                        var candidate = Path.Combine(dir, name);
                        if (!Directory.Exists(candidate) || !HasAnyImage(candidate)) continue;

                        var full = Path.GetFullPath(candidate);
                        if (!found.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)))
                            found.Add(full);
                    }

                    dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            catch { /* permissions, malformed path — offer nothing */ }

            return found;
        }

        private static bool HasAnyImage(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder)
                    .Any(f => ImageExtensions.Contains(Path.GetExtension(f)));
            }
            catch { return false; }
        }

        /// <summary>
        /// The image files in a folder, ordered the way a reader would expect.
        ///
        /// <para>Ordering is the whole point of this method. The convention is one
        /// file per figure named for its label — <c>FIG. 1.png</c> …
        /// <c>FIG. 9.png</c> — and an ordinary string sort puts <c>FIG. 10</c>
        /// between <c>FIG. 1</c> and <c>FIG. 2</c>. A vision pass handed the
        /// figures out of order will caption them out of order, and the error is
        /// invisible in the output.</para>
        /// </summary>
        public static List<ReferenceImage> List(string folder)
        {
            var result = new List<ReferenceImage>();
            if (string.IsNullOrWhiteSpace(folder)) return result;

            try
            {
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    if (!ImageExtensions.Contains(Path.GetExtension(path))) continue;
                    result.Add(new ReferenceImage
                    {
                        Path = path,
                        FileName = Path.GetFileName(path),
                        FigureLabel = ParseFigureLabel(Path.GetFileNameWithoutExtension(path)),
                        FigureNumber = ParseFigureNumber(Path.GetFileNameWithoutExtension(path))
                    });
                }
            }
            catch { return result; }

            // Numbered figures first in numeric order, then anything unnumbered
            // alphabetically, so an odd file never displaces the sequence.
            result.Sort((a, b) =>
            {
                if (a.FigureNumber.HasValue && b.FigureNumber.HasValue)
                    return a.FigureNumber.Value.CompareTo(b.FigureNumber.Value);
                if (a.FigureNumber.HasValue) return -1;
                if (b.FigureNumber.HasValue) return 1;
                return StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
            });

            return result;
        }

        // "FIG. 1", "Fig 1", "Figure 1", "FIG-1", "1" — the label as the text
        // would cite it, so the vision pass can label its output without guessing.
        private static readonly Regex FigureRe = new Regex(
            @"^\s*(?:fig(?:ure)?\.?\s*[-_]?\s*)?(\d{1,3})\s*([a-z])?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The figure label, normalised to "FIG. n" (or "FIG. nA"), or
        /// the bare filename when it does not look like a figure at all.</summary>
        public static string ParseFigureLabel(string fileNameWithoutExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) return "";
            var m = FigureRe.Match(fileNameWithoutExtension);
            if (!m.Success) return fileNameWithoutExtension.Trim();

            var suffix = m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : "";
            return "FIG. " + m.Groups[1].Value + suffix;
        }

        /// <summary>The figure's number, or null when the name is not a figure.</summary>
        public static int? ParseFigureNumber(string fileNameWithoutExtension)
        {
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension)) return null;
            var m = FigureRe.Match(fileNameWithoutExtension);
            if (!m.Success) return null;
            return int.TryParse(m.Groups[1].Value, out var n) ? n : (int?)null;
        }
    }

    /// <summary>One drawing on disk.</summary>
    public class ReferenceImage
    {
        public string Path { get; set; }
        public string FileName { get; set; }

        /// <summary>Normalised label, e.g. "FIG. 3". Falls back to the filename
        /// when the name does not follow the convention.</summary>
        public string FigureLabel { get; set; }

        /// <summary>Figure number when the filename yields one; null otherwise.</summary>
        public int? FigureNumber { get; set; }

        public override string ToString() => FigureLabel + " (" + FileName + ")";
    }
}
