using System;
using System.IO;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Compares and resolves stored prompt paths in a way that survives the
    /// product marker in prompt filenames.
    ///
    /// Since core 7053813 a prompt's filename carries a marker derived from its
    /// app field - "Define [Trados].md" - regenerated on every write and stripped
    /// on every read. The plugin remembers its selected prompt as a RELATIVE
    /// PATH, in two places: <c>AiSettings.SelectedPromptPath</c> globally, and
    /// <c>ProjectSettings.ActivePromptPath</c> inside every project's own
    /// settings file. A literal comparison of either against the library breaks
    /// the moment the file is renamed - by the one-time built-in migration, or by
    /// any later save from the editor - and the failure is silent: the prompt
    /// stops resolving and the job runs on the inline fallback instructions.
    ///
    /// A migration could repair the global setting, but not the per-project
    /// copies without walking every project ever saved. Tolerant matching needs
    /// no migration at all: the marker is ignored on both sides of every
    /// comparison, so a path stored before the rename keeps resolving after it.
    /// </summary>
    internal static class PromptPaths
    {
        /// <summary>
        /// A relative path with separators unified and the product marker
        /// removed from its filename, so two spellings of one prompt compare
        /// equal. Empty for null.
        /// </summary>
        public static string Normalise(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return "";

            var p = relativePath.Trim().Replace('/', '\\');
            var dir = Path.GetDirectoryName(p) ?? "";
            var stem = Path.GetFileNameWithoutExtension(p);
            var ext = Path.GetExtension(p);

            // TrimEnd on both branches: StripAppTag trims after removing a marker,
            // and a stem without one should normalise the same way.
            stem = PromptLibrary.StripAppTag(stem).TrimEnd();

            return dir.Length == 0 ? stem + ext : dir + "\\" + stem + ext;
        }

        /// <summary>True when both paths name the same prompt, marker or no marker.</summary>
        public static bool Match(string a, string b)
        {
            var na = Normalise(a);
            if (na.Length == 0) return false;
            return string.Equals(na, Normalise(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The prompt a stored path refers to: an exact hit first, then a
        /// marker-tolerant scan. Null when nothing matches or the inputs are empty.
        /// </summary>
        public static PromptTemplate Find(PromptLibrary library, string relativePath)
        {
            if (library == null || string.IsNullOrWhiteSpace(relativePath)) return null;

            var exact = library.GetPromptByRelativePath(relativePath);
            if (exact != null) return exact;

            foreach (var p in library.GetAllPrompts())
            {
                if (p != null && Match(p.RelativePath, relativePath))
                    return p;
            }
            return null;
        }
    }
}
