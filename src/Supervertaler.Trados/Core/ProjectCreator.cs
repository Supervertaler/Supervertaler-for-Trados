using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// MCP <c>create_project</c>, the part that needs no Studio: checks a request,
    /// turns it into a <see cref="ProjectPlan"/>, and cleans up a half-made project.
    /// Kept apart from <see cref="ProjectCreatorStudio"/> (which uses the Project
    /// Automation API) so tools\create_project_test.ps1 can run every refusal and
    /// the clean-up offline, against the built DLL, without Studio.
    ///
    /// Rules (docs/design/create-project-from-folder.md):
    /// - refuse before writing anything: a taken name, a missing/bilingual/empty
    ///   file, an unknown language, a missing or unreadable template;
    /// - the project goes in its own new folder, &lt;location&gt;\&lt;name&gt;; an existing
    ///   folder is accepted only when empty;
    /// - clean-up removes only what this run created and never touches the
    ///   user's source files (Studio copies them into the project).
    /// </summary>
    internal static class ProjectCreator
    {
        /// <summary>Extensions that mean "a package or an already-bilingual
        /// file", not a new source document. Refused with a reason.</summary>
        private static readonly string[] BilingualOrPackage =
        {
            ".sdlxliff", ".sdlppx", ".sdlrpx", ".sdlproj", ".sdltpl", ".sdltm", ".sdltb", ".ttb",
        };

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Checks the request and builds the plan. Never writes anything.
        /// <paramref name="defaultLocation"/> is used when the request names no
        /// location (the running Studio's own Projects folder);
        /// <paramref name="registeredNames"/> are the project names already in the
        /// running Studio's list. Errors are plain sentences for the AI to relay.
        /// </summary>
        internal static ProjectPlan Plan(
            string name, string sourceLanguage, IList<string> targetLanguages,
            IList<string> files, string template, string location,
            string defaultLocation, ICollection<string> registeredNames)
        {
            var plan = new ProjectPlan();
            var errors = plan.Errors;

            // ---- name ------------------------------------------------------------
            name = (name ?? "").Trim();
            if (name.Length == 0)
                errors.Add("No project name given.");
            else
            {
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    errors.Add($"The project name \"{name}\" contains a character that cannot be used in a folder name (\\ / : * ? \" < > |).");
                else if (name.EndsWith(".") || ReservedNames.Contains(name.ToUpperInvariant()))
                    errors.Add($"\"{name}\" cannot be used as a folder name in Windows; choose another project name.");
                else if (name.Length > 100)
                    errors.Add("The project name is longer than 100 characters; shorten it.");
                else if (registeredNames != null && registeredNames.Contains(name))
                    errors.Add($"Trados Studio already has a project called \"{name}\". Choose another name.");
            }
            plan.Name = name;

            // ---- template ----------------------------------------------------------
            template = (template ?? "").Trim();
            if (template.Length > 0)
            {
                if (!Path.IsPathRooted(template))
                    errors.Add($"The template must be given as a full path to an .sdltpl file (as list_project_templates returns it), not \"{template}\".");
                else if (!string.Equals(Path.GetExtension(template), ".sdltpl", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"\"{template}\" is not a project template (.sdltpl).");
                else if (!File.Exists(template))
                    errors.Add($"The project template \"{template}\" does not exist (it may have been moved or deleted).");
                else
                {
                    string why = CheckTemplateReadable(template);
                    if (why != null) errors.Add(why);
                    else plan.TemplatePath = Path.GetFullPath(template);
                }
            }

            // ---- languages -------------------------------------------------------
            bool haveTemplate = template.Length > 0;
            sourceLanguage = (sourceLanguage ?? "").Trim();
            if (sourceLanguage.Length == 0)
            {
                if (!haveTemplate)
                    errors.Add("No source language given (e.g. \"nl-NL\"). It may only be left out when a template supplies it.");
            }
            else if (!IsKnownLanguage(sourceLanguage, out var canon))
                errors.Add($"\"{sourceLanguage}\" is not a language Trados Studio knows. Use a language code such as \"nl-NL\", \"en-GB\" or \"de-DE\".");
            else
                plan.SourceLanguage = canon;

            var targets = new List<string>();
            bool badTarget = false;
            foreach (var raw in targetLanguages ?? new List<string>())
            {
                foreach (var part in (raw ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = part.Trim();
                    if (t.Length == 0) continue;
                    if (!IsKnownLanguage(t, out var tc))
                    {
                        badTarget = true;
                        errors.Add($"\"{t}\" is not a language Trados Studio knows. Use a language code such as \"nl-NL\", \"en-GB\" or \"de-DE\".");
                    }
                    else if (plan.SourceLanguage != null && string.Equals(tc, plan.SourceLanguage, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"{tc} is the source language; it cannot also be a target language.");
                    else if (!targets.Contains(tc, StringComparer.OrdinalIgnoreCase))
                        targets.Add(tc);
                }
            }
            if (targets.Count == 0 && !haveTemplate && !badTarget)
                errors.Add("No target language given (e.g. [\"en-GB\"]). It may only be left out when a template supplies it.");
            plan.TargetLanguages = targets;

            // ---- files -----------------------------------------------------------
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileList = new List<string>();
            int errorsBeforeFiles = errors.Count;
            foreach (var raw in files ?? new List<string>())
            {
                var f = (raw ?? "").Trim().Trim('"');
                if (f.Length == 0) continue;
                if (!Path.IsPathRooted(f)) { errors.Add($"\"{f}\" is not a full path; give each file as a full path."); continue; }
                string full;
                try { full = Path.GetFullPath(f); }
                catch { errors.Add($"\"{f}\" is not a valid file path."); continue; }
                if (!seen.Add(full)) continue;   // listed twice: once is enough

                var fileName = Path.GetFileName(full);
                var ext = Path.GetExtension(full);
                if (Directory.Exists(full)) { errors.Add($"\"{full}\" is a folder; list the files in it instead."); continue; }
                if (!File.Exists(full)) { errors.Add($"The file \"{full}\" does not exist."); continue; }
                if (fileName.StartsWith("~$")) { errors.Add($"\"{fileName}\" is an Office lock file, not a document; leave it out."); continue; }
                if (BilingualOrPackage.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"\"{fileName}\" is a Trados {ext} file (bilingual file, package, project, template or resource), not a source document. Open packages with Studio's Open Package instead.");
                    continue;
                }
                long len;
                try { len = new FileInfo(full).Length; } catch { len = -1; }
                if (len == 0) { errors.Add($"\"{fileName}\" is empty (0 bytes)."); continue; }
                string lockWhy = CheckReadable(full);
                if (lockWhy != null) { errors.Add(lockWhy); continue; }
                fileList.Add(full);
            }
            if (fileList.Count == 0 && errors.Count == errorsBeforeFiles)
                errors.Add("No files given. List the source documents to translate, each as a full path.");
            plan.Files = fileList;
            plan.FileRoot = CommonFolder(fileList);

            // ---- folder ----------------------------------------------------------
            location = (location ?? "").Trim().Trim('"');
            if (location.Length == 0) location = defaultLocation ?? "";
            if (location.Length == 0)
                errors.Add("No location given, and Studio's own Projects folder could not be found. Give a location (a folder; the project gets its own subfolder there).");
            else if (!Path.IsPathRooted(location))
                errors.Add($"The location \"{location}\" is not a full path.");
            else if (name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                string folder;
                try { folder = Path.GetFullPath(Path.Combine(location, name)); }
                catch { folder = null; errors.Add($"\"{location}\" is not a valid folder path."); }
                if (folder != null)
                {
                    if (File.Exists(folder))
                        errors.Add($"\"{folder}\" is a file; the project needs a folder of that name.");
                    else if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                        errors.Add($"The folder \"{folder}\" already exists and is not empty. Choose another project name or location; an existing folder is never written into.");
                    else
                    {
                        plan.ProjectFolder = folder;
                        plan.FolderExisted = Directory.Exists(folder);
                        // Never let a source file live inside the folder the
                        // project will be created (and on failure deleted) in.
                        foreach (var f in fileList)
                            if (f.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                errors.Add($"\"{f}\" lies inside the new project folder; pick another location.");
                    }
                }
            }

            return plan;
        }

        /// <summary>
        /// A language Studio knows: a real Windows culture, not a made-up tag
        /// that Windows 10+ would accept as an "unknown" custom culture.
        /// Canonical name back, e.g. "EN-gb" → "en-GB".
        /// </summary>
        internal static bool IsKnownLanguage(string code, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrWhiteSpace(code)) return false;
            code = code.Trim().Replace('_', '-');
            if (!Regex.IsMatch(code, @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")) return false;
            CultureInfo ci;
            try { ci = CultureInfo.GetCultureInfo(code); }
            catch (CultureNotFoundException) { return false; }
            if (ci == null || ci.Equals(CultureInfo.InvariantCulture)) return false;
            if ((ci.CultureTypes & CultureTypes.UserCustomCulture) != 0) return false;
            if (ci.ThreeLetterWindowsLanguageName == "ZZZ") return false;   // "unknown" placeholder culture
            canonical = ci.Name;
            return true;
        }

        /// <summary>Null when a template can be read as XML; otherwise the reason.</summary>
        private static string CheckTemplateReadable(string path)
        {
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var doc = XDocument.Load(s);
                    if (doc.Root == null) return $"The template \"{path}\" is empty.";
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"The template \"{path}\" could not be read as a Studio project template ({ex.GetType().Name}: {ex.Message}).";
            }
        }

        /// <summary>Null when the file can be opened for reading; otherwise why
        /// not (e.g. another program holds it exclusively).</summary>
        private static string CheckReadable(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return $"\"{Path.GetFileName(path)}\" cannot be read (access denied).";
            }
            catch (IOException ex)
            {
                return $"\"{Path.GetFileName(path)}\" cannot be read; another program may have it open ({ex.Message}). Close it and try again.";
            }
        }

        /// <summary>The deepest folder all files share, so subfolders can be kept
        /// in the project (two "intro.docx" in different folders do not clash).</summary>
        internal static string CommonFolder(IList<string> files)
        {
            if (files == null || files.Count == 0) return null;
            var first = Path.GetDirectoryName(files[0]);
            var parts = first.Split(Path.DirectorySeparatorChar);
            int n = parts.Length;
            foreach (var f in files.Skip(1))
            {
                var p = Path.GetDirectoryName(f).Split(Path.DirectorySeparatorChar);
                int i = 0;
                while (i < n && i < p.Length && string.Equals(parts[i], p[i], StringComparison.OrdinalIgnoreCase)) i++;
                n = i;
            }
            if (n == 0) return null;   // different drives: no common folder, all at the top
            var root = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(n));
            if (root.EndsWith(":")) root += Path.DirectorySeparatorChar;
            return root;
        }

        /// <summary>
        /// Groups the files by the subfolder they will get in the project
        /// (relative to <see cref="ProjectPlan.FileRoot"/>; "" = top level), so
        /// AddFiles is called once per subfolder, not once per file.
        /// </summary>
        internal static List<KeyValuePair<string, string[]>> GroupBySubfolder(ProjectPlan plan)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var f in plan.Files)
            {
                string rel = "";
                var dir = Path.GetDirectoryName(f);
                if (plan.FileRoot != null && dir.Length > plan.FileRoot.Length
                    && dir.StartsWith(plan.FileRoot, StringComparison.OrdinalIgnoreCase))
                    rel = dir.Substring(plan.FileRoot.Length).Trim(Path.DirectorySeparatorChar);
                if (!groups.TryGetValue(rel, out var list)) { list = new List<string>(); groups[rel] = list; order.Add(rel); }
                list.Add(f);
            }
            return order.Select(k => new KeyValuePair<string, string[]>(k, groups[k].ToArray())).ToList();
        }

        /// <summary>
        /// Removes what a failed run left in the project folder: the whole folder
        /// when this run created it, otherwise only its contents (it was empty
        /// when the run started – <see cref="Plan"/> refuses anything else).
        /// Returns null on success, else what could not be removed.
        /// </summary>
        internal static string CleanUp(ProjectPlan plan)
        {
            var folder = plan?.ProjectFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            // Belt and braces: the folder is always <location>\<name>, never a
            // drive root or a bare Documents folder.
            if (Path.GetPathRoot(folder).TrimEnd('\\').Length >= folder.TrimEnd('\\').Length)
                return $"refused to clean up \"{folder}\" (a drive root)";

            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    // Studio marks some files read-only; Directory.Delete refuses those.
                    foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    if (!plan.FolderExisted)
                        Directory.Delete(folder, recursive: true);
                    else
                    {
                        foreach (var d in Directory.GetDirectories(folder)) Directory.Delete(d, recursive: true);
                        foreach (var f in Directory.GetFiles(folder)) File.Delete(f);
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // Studio may still hold a file it just wrote for a moment.
                    System.Threading.Thread.Sleep(300);
                }
            }
            return $"could not remove everything in \"{folder}\": {last?.Message}";
        }

        /// <summary>Names of the projects in one Studio's projects.xml (case-insensitive set).</summary>
        internal static HashSet<string> RegisteredProjectNames(string projectsXmlPath)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(projectsXmlPath) || !File.Exists(projectsXmlPath)) return names;
            try
            {
                var doc = XDocument.Load(projectsXmlPath);
                foreach (var item in doc.Descendants("ProjectListItem"))
                {
                    var n = item.Element("ProjectInfo")?.Attribute("Name")?.Value;
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            }
            catch { /* unreadable registry: Studio itself will refuse a clash */ }
            return names;
        }

        /// <summary>The Documents\Studio 20xx… folder of the Studio this build runs in.</summary>
        internal static string RunningStudioDocumentsFolder()
        {
#if STUDIO17
            const string year = "2022";
#elif STUDIO19
            const string year = "2026";
#else
            const string year = "2024";
#endif
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!Directory.Exists(docs)) return null;
                // "Studio 2026 Release", "Studio 2024", "Studio 2022": match the year.
                return Directory.GetDirectories(docs, "Studio " + year + "*")
                    .OrderBy(d => d.Length).FirstOrDefault();
            }
            catch { return null; }
        }
    }

    /// <summary>A checked create_project request. <see cref="Errors"/> empty = good to go.</summary>
    internal sealed class ProjectPlan
    {
        public string Name;
        public string ProjectFolder;
        /// <summary>The folder was there (empty) before we started; clean-up empties it instead of deleting it.</summary>
        public bool FolderExisted;
        public string SourceLanguage;
        public List<string> TargetLanguages = new List<string>();
        public List<string> Files = new List<string>();
        /// <summary>Deepest folder the files share; subfolders below it are kept in the project.</summary>
        public string FileRoot;
        public string TemplatePath;
        public List<string> Errors = new List<string>();
    }
}
