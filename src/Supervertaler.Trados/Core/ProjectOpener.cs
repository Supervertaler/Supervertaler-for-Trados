using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// MCP <c>open_project</c>, the part that needs no Studio: which registered
    /// project the request means, and which of its files to open. Studio types
    /// stay out of this file so tools\open_project_test.ps1 can test it against
    /// the built DLL. The Studio part is <see cref="ProjectOpenerStudio"/>.
    /// </summary>
    internal static class ProjectOpener
    {
        /// <summary>
        /// Most files one open_project call opens in the Editor. Studio opens
        /// several files as ONE document (as when you select files and choose
        /// Open for Translation), and loading it runs on Studio's UI thread, so
        /// the time Studio is frozen grows with the total size of the files.
        /// Fifty covers ordinary jobs; a larger project is refused with this
        /// number in the message, so the user can name the files they want.
        /// </summary>
        internal const int MaxFilesToOpen = 50;

        internal sealed class Entry
        {
            public string Name;
            public string ProjectFile;
        }

        internal sealed class MatchResult
        {
            public Entry Project;
            public string Error;
        }

        /// <summary>A target-language file of the project, as Studio lists it.</summary>
        internal sealed class FileRef
        {
            public string Name;      // e.g. "spec.docx.sdlxliff"
            public string Folder;    // relative folder in the project, "" or "manuals\"
            public string Language;  // target language code, e.g. "en-GB"
            public object Tag;       // the Studio ProjectFile, carried through untouched
            public string Display => Path.Combine(Folder ?? "", Name ?? "").TrimStart('\\');
        }

        internal sealed class FilePick
        {
            public string Language;
            public List<FileRef> Files = new List<FileRef>();
            public string Error;
        }

        /// <summary>The projects in one Studio's projects.xml, with full .sdlproj paths.
        /// A missing or unreadable file gives an empty list.</summary>
        internal static List<Entry> ReadProjectList(string projectsXmlPath)
        {
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(projectsXmlPath) || !File.Exists(projectsXmlPath)) return list;
            try
            {
                var doc = XDocument.Load(projectsXmlPath);
                var dir = Path.GetDirectoryName(projectsXmlPath) ?? "";
                foreach (var item in doc.Descendants("ProjectListItem"))
                {
                    var name = item.Element("ProjectInfo")?.Attribute("Name")?.Value;
                    var file = item.Attribute("ProjectFilePath")?.Value;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(file)) continue;
                    if (!Path.IsPathRooted(file)) file = Path.Combine(dir, file);
                    list.Add(new Entry { Name = name.Trim(), ProjectFile = file });
                }
            }
            catch { /* unreadable registry: treated as empty, the caller says "not found" */ }
            return list;
        }

        /// <summary>
        /// The project <paramref name="query"/> means: a .sdlproj path (compared as
        /// a full path), else an exact name (ignoring case), else the ONE name that
        /// contains it. Several partial matches are refused with their names: opening
        /// the wrong project is worse than asking.
        /// </summary>
        internal static MatchResult Match(string query, IList<Entry> projects)
        {
            query = (query ?? "").Trim().Trim('"');
            if (query.Length == 0)
                return new MatchResult { Error = "Say which project to open: its name or its .sdlproj file." };
            projects = projects ?? new List<Entry>();

            var file = AsProjectFile(query);
            if (file != null)
            {
                var wanted = NormalPath(file);
                var byPath = projects.FirstOrDefault(p => NormalPath(p.ProjectFile) == wanted);
                if (byPath != null) return new MatchResult { Project = byPath };
                if (!FileExists(file)) return new MatchResult { Error = $"\"{query}\" does not exist." };

                // Say what was compared: a project file of the same name that the
                // list has somewhere else is the likeliest reason for a miss.
                var sameName = projects.Where(p => string.Equals(FileNameOf(p.ProjectFile), FileNameOf(file), StringComparison.OrdinalIgnoreCase))
                    .Take(3).Select(p => p.ProjectFile).ToList();
                return new MatchResult
                {
                    Error = $"\"{query}\" is not in this Studio's Projects list" +
                            $" (looked for {wanted} among {projects.Count} project(s)" +
                            (sameName.Count > 0 ? "; the list has a project file of that name at " + string.Join("; ", sameName) : "") +
                            "). Add it in Studio first (File > Open > Open Project), then ask again."
                };
            }

            var exact = projects.Where(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return new MatchResult { Project = exact[0] };
            if (exact.Count > 1)
                return new MatchResult
                {
                    Error = $"{exact.Count} projects in this Studio are called \"{query}\". Give the .sdlproj file instead: " +
                            string.Join("; ", exact.Take(10).Select(p => p.ProjectFile)) + "."
                };

            var partial = projects.Where(p => p.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return new MatchResult { Project = partial[0] };
            if (partial.Count > 1)
                return new MatchResult
                {
                    Error = $"{partial.Count} projects match \"{query}\": " +
                            string.Join("; ", partial.Take(10).Select(p => "\"" + p.Name + "\"")) +
                            (partial.Count > 10 ? $" and {partial.Count - 10} more" : "") +
                            ". Say which one (its full name or its .sdlproj file)."
                };

            return new MatchResult { Error = $"No project in this Studio's Projects list matches \"{query}\"." };
        }

        /// <summary>
        /// <see cref="Match"/> against Studio's projects.xml and, on a miss, against
        /// Studio's live list (<paramref name="askStudio"/>, a call to Studio's UI
        /// thread, made at most once). projects.xml lags behind the live list: a
        /// project added moments ago - by create_project, say - is in Studio's
        /// list before it is in the file.
        ///
        /// <para>Until 208 the live list was asked for a name and not for a
        /// .sdlproj path, so the path create_project had just returned was
        /// refused as "not in this Studio's Projects list" while the same
        /// project's name was found (seen in Studio 2024). A path is now asked
        /// too, unless no such file exists: Studio cannot open that whatever
        /// its list says.</para>
        /// </summary>
        internal static MatchResult Find(string query, IList<Entry> listed, Func<IList<Entry>> askStudio)
        {
            var match = Match(query, listed);
            if (match.Project != null || askStudio == null) return match;

            if (string.IsNullOrWhiteSpace((query ?? "").Trim().Trim('"'))) return match;
            var file = AsProjectFile(query);
            if (file != null && !FileExists(file)) return match;

            var live = Match(query, askStudio());
            return live.Project != null ? live : match;
        }

        /// <summary>The request as a .sdlproj path, plainly spelled, when it is one; null when it is a name.</summary>
        private static string AsProjectFile(string query)
        {
            var plain = PlainPath(query);
            return plain.EndsWith(".sdlproj", StringComparison.OrdinalIgnoreCase) ? plain : null;
        }

        /// <summary>A path without what can be wrapped around it: spaces, quotes,
        /// forward slashes, a "\\?\" prefix, a trailing backslash.</summary>
        private static string PlainPath(string p)
        {
            p = (p ?? "").Trim().Trim('"').Trim().Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p.Substring(8);
            else if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(4);
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }

        private static bool FileExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }

        private static string FileNameOf(string path)
        {
            try { return Path.GetFileName(PlainPath(path)); } catch { return null; }
        }

        /// <summary>
        /// Which files to open. <paramref name="all"/> = every file of the target
        /// language; otherwise <paramref name="names"/> (file name with or without
        /// .sdlxliff, or folder\name, ignoring case). With several target languages
        /// one must be given. Refuses rather than opening part of what was asked.
        /// </summary>
        internal static FilePick PickFiles(IList<FileRef> targetFiles, bool all, IList<string> names, string language)
        {
            var pick = new FilePick();
            targetFiles = targetFiles ?? new List<FileRef>();
            names = (names ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            if (!all && names.Count == 0) return pick;   // project only

            var languages = targetFiles.Select(f => f.Language).Where(l => !string.IsNullOrEmpty(l))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (languages.Count == 0)
            {
                pick.Error = "The project has no target-language files to open.";
                return pick;
            }

            if (!string.IsNullOrWhiteSpace(language))
            {
                var lang = languages.FirstOrDefault(l => string.Equals(l, language.Trim(), StringComparison.OrdinalIgnoreCase));
                if (lang == null)
                {
                    pick.Error = $"The project has no {language.Trim()} files. Its target languages: {string.Join(", ", languages)}.";
                    return pick;
                }
                pick.Language = lang;
            }
            else if (languages.Count == 1)
                pick.Language = languages[0];
            else
            {
                pick.Error = $"The project has several target languages ({string.Join(", ", languages)}). Say which one to open.";
                return pick;
            }

            var inLang = targetFiles.Where(f => string.Equals(f.Language, pick.Language, StringComparison.OrdinalIgnoreCase)).ToList();

            if (all)
                pick.Files = inLang;
            else
            {
                var missing = new List<string>();
                var ambiguous = new List<string>();
                foreach (var n in names)
                {
                    var hits = inLang.Where(f => NameMatches(f, n)).ToList();
                    if (hits.Count == 0) missing.Add(n);
                    else if (hits.Count > 1) ambiguous.Add($"\"{n}\" ({string.Join(", ", hits.Select(h => h.Display))})");
                    else if (!pick.Files.Contains(hits[0])) pick.Files.Add(hits[0]);
                }
                if (missing.Count > 0 || ambiguous.Count > 0)
                {
                    var parts = new List<string>();
                    if (missing.Count > 0)
                        parts.Add($"Not in the project's {pick.Language} files: {string.Join(", ", missing.Select(m => "\"" + m + "\""))}.");
                    if (ambiguous.Count > 0)
                        parts.Add("More than one file matches " + string.Join("; ", ambiguous) + " – give the folder too.");
                    pick.Error = string.Join(" ", parts) + " Its files: " +
                                 string.Join(", ", inLang.Take(20).Select(f => f.Display)) +
                                 (inLang.Count > 20 ? $" and {inLang.Count - 20} more" : "") + ".";
                    pick.Files.Clear();
                    return pick;
                }
            }

            if (pick.Files.Count > MaxFilesToOpen)
            {
                pick.Error = $"That is {pick.Files.Count} files; open_project opens at most {MaxFilesToOpen} at once, " +
                             "because Studio loads them as one document and is frozen while it does. " +
                             "Name the files you want to work on.";
                pick.Files.Clear();
            }
            return pick;
        }

        private static bool NameMatches(FileRef f, string n)
        {
            n = n.Replace('/', '\\').TrimStart('\\');
            bool Eq(string a) => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(a, n + ".sdlxliff", StringComparison.OrdinalIgnoreCase);
            return Eq(f.Name ?? "") || Eq(f.Display);
        }

        /// <summary>
        /// A path in the one form paths are compared in: full, backslashes, no
        /// quotes or spaces around it, no "\\?\" prefix, no trailing backslash,
        /// upper case. Two spellings of one file compare equal; an 8.3 short name
        /// ("CONSIS~1") and its long name do not, which would need the disk.
        /// </summary>
        internal static string NormalPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = PlainPath(p);
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\').ToUpperInvariant();
        }
    }
}
