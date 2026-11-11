using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// MCP <c>open_project</c>, the part that needs Studio: opens a project from
    /// Studio's Projects list as the current project and, if asked, opens its
    /// target files in the Editor.
    ///
    /// <para>Studio's API (same signatures in 2022, 2024 and 2026):
    /// <c>ProjectsController.Open(FileBasedProject)</c> calls Studio's own
    /// <c>IProjectsViewService.OpenProject</c> - what double-clicking a project in
    /// the Projects view does (decompiled with REA from the 2026 DLL, 10 Nov 2026;
    /// <c>ActivateProject</c> ends in the same call).
    /// <c>EditorController.Open(IEnumerable&lt;ProjectFile&gt;, EditingMode)</c>
    /// opens several files as one document, as "Open for Translation" does with
    /// several files selected.</para>
    ///
    /// <para>Runs on a pool thread (a background job). The lookup and the file
    /// choice happen off the UI thread; only the Studio calls go to it. While a
    /// UI step runs, progress counts the seconds ("opening 2 file(s) in the
    /// Editor (8 s)"). Only past <see cref="Patience"/> does it add that a message
    /// may be open in Studio (upgrade an old project, project in use): opening is
    /// legitimately slow - two small files took 12 s in Studio 2022 on
    /// 10 Nov 2026 with no message at all - so a short threshold cried wolf.</para>
    /// </summary>
    internal static class ProjectOpenerStudio
    {
        internal sealed class Result
        {
            public bool Ok;
            public string Error;
            public string ProjectName;
            public string ProjectFile;
            public string Language;
            public List<string> OpenedFiles = new List<string>();
        }

        /// <summary>How long a UI step may take before progress says a message may
        /// be open in Studio: 30 s, plus 2 s per file opened in the Editor.</summary>
        internal static TimeSpan Patience(int files) => TimeSpan.FromSeconds(30 + 2 * Math.Max(0, files));

        internal static Result Run(string projectQuery, bool allFiles, IList<string> fileNames, string language,
            Action<string> progress)
        {
            var result = new Result();
            try
            {
                progress?.Invoke("finding the project");

                // 1. Which project: this Studio's projects.xml, read off the UI thread.
                var studioDocs = ProjectCreator.RunningStudioDocumentsFolder();
                var xml = studioDocs != null ? Path.Combine(studioDocs, "Projects", "projects.xml") : null;
                var match = ProjectOpener.Match(projectQuery, ProjectOpener.ReadProjectList(xml));

                // projects.xml can lag behind Studio's live list (a project added
                // moments ago). Only on a miss: ask Studio itself.
                if (match.Project == null && !(projectQuery ?? "").Trim().EndsWith(".sdlproj", StringComparison.OrdinalIgnoreCase))
                {
                    var live = OnUi(() => LiveProjectList(), progress, "looking in Studio's Projects list", 0);
                    var liveMatch = ProjectOpener.Match(projectQuery, live);
                    if (liveMatch.Project != null) match = liveMatch;
                }
                if (match.Project == null)
                {
                    result.Error = match.Error + OtherStudioHint(projectQuery, studioDocs);
                    return result;
                }
                result.ProjectName = match.Project.Name;
                result.ProjectFile = match.Project.ProjectFile;

                // 2. Studio's own object for it (never a second FileBasedProject on
                // the same file - Studio would then hold two copies of the project).
                progress?.Invoke("opening the project");
                var wanted = ProjectOpener.NormalPath(match.Project.ProjectFile);
                var project = OnUi(() => Projects().GetAllProjects()
                    .FirstOrDefault(p => ProjectOpener.NormalPath(SafePath(p)) == wanted), progress, "finding the project in Studio", 0);
                if (project == null)
                {
                    result.Error = $"Studio's Projects list names \"{match.Project.Name}\" but Studio has not loaded it " +
                                   $"({match.Project.ProjectFile}). Open it once from the Projects view.";
                    return result;
                }

                // 3. Pick the files before anything visible happens, so a bad file
                // name refuses without opening even the project.
                ProjectOpener.FilePick pick = null;
                if (allFiles || (fileNames != null && fileNames.Count > 0))
                {
                    var refs = TargetFiles(project);
                    pick = ProjectOpener.PickFiles(refs, allFiles, fileNames, language);
                    if (pick.Error != null)
                    {
                        result.Error = "Nothing was opened. " + pick.Error;
                        return result;
                    }
                }

                OnUi(() => { Projects().Open(project); return true; }, progress, "opening the project", 0);

                if (pick != null && pick.Files.Count > 0)
                {
                    var files = pick.Files.Select(f => (ProjectFile)f.Tag).ToList();
                    OnUi(() =>
                    {
                        var editor = StudioEditor.Controller;
                        if (files.Count == 1) editor.Open(files[0], EditingMode.Translation);
                        else editor.Open(files, EditingMode.Translation);
                        return true;
                    }, progress, $"opening {files.Count} file(s) in the Editor", files.Count);
                    result.Language = pick.Language;
                    result.OpenedFiles = pick.Files.Select(f => f.Display).ToList();
                }

                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                var inner = (ex as AggregateException)?.InnerException ?? ex;
                result.Error = "Opening the project failed: " + inner.Message;
                return result;
            }
        }

        private static ProjectsController Projects() =>
            SdlTradosStudio.Application?.GetController<ProjectsController>()
            ?? throw new InvalidOperationException("Studio's Projects list is not available.");

        /// <summary>Studio's live list, name + file. UI thread. One GetProjectInfo
        /// per project - only used when projects.xml did not find it.</summary>
        private static List<ProjectOpener.Entry> LiveProjectList()
        {
            var list = new List<ProjectOpener.Entry>();
            foreach (var p in Projects().GetAllProjects())
            {
                try
                {
                    var path = SafePath(p);
                    var name = p.GetProjectInfo()?.Name;
                    if (!string.IsNullOrWhiteSpace(name) && path != null)
                        list.Add(new ProjectOpener.Entry { Name = name, ProjectFile = path });
                }
                catch { /* a project Studio cannot read is not one we can open */ }
            }
            return list;
        }

        /// <summary>The project's target-language translatable files. Reads the
        /// project's file list, not the files themselves; off the UI thread.</summary>
        private static List<ProjectOpener.FileRef> TargetFiles(FileBasedProject project)
        {
            var refs = new List<ProjectOpener.FileRef>();
            var info = project.GetProjectInfo();
            foreach (var lang in info.TargetLanguages ?? new Language[0])
            {
                foreach (var f in project.GetTargetLanguageFiles(lang).Where(f => f.Role == FileRole.Translatable))
                {
                    refs.Add(new ProjectOpener.FileRef
                    {
                        Name = f.Name,
                        Folder = f.Folder,
                        Language = lang.IsoAbbreviation,
                        Tag = f
                    });
                }
            }
            return refs;
        }

        /// <summary>" It is in Studio 2024's list, not this Studio's." when another
        /// installed Studio knows the name; else "".</summary>
        private static string OtherStudioHint(string query, string runningStudioDocs)
        {
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                foreach (var dir in Directory.GetDirectories(docs, "Studio *"))
                {
                    if (runningStudioDocs != null && string.Equals(dir, runningStudioDocs, StringComparison.OrdinalIgnoreCase)) continue;
                    var m = ProjectOpener.Match(query, ProjectOpener.ReadProjectList(Path.Combine(dir, "Projects", "projects.xml")));
                    if (m.Project != null)
                        return $" \"{m.Project.Name}\" is in the Projects list of {Path.GetFileName(dir)}, not of this Studio: " +
                               "open it in that Studio (list_trados_instances / select_trados_instance if it is running).";
                }
            }
            catch { }
            return "";
        }

        /// <summary>Runs <paramref name="f"/> on the UI thread; meanwhile progress
        /// says <paramref name="step"/> with the seconds so far, and from
        /// <see cref="Patience"/> on that a message may be open in Studio.</summary>
        private static T OnUi<T>(Func<T> f, Action<string> progress, string step, int files)
        {
            progress?.Invoke(step);
            var t = System.Threading.Tasks.Task.Run(() => UiThread.Invoke(f));
            var patience = Patience(files);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!t.Wait(TimeSpan.FromSeconds(1)))
                progress?.Invoke(ProgressText(step, sw.Elapsed, patience));
            return t.GetAwaiter().GetResult();
        }

        /// <summary>"opening 2 file(s) in the Editor (12 s)"; past the patience,
        /// "… (45 s) – Studio may be showing a message: answer it there".</summary>
        internal static string ProgressText(string step, TimeSpan elapsed, TimeSpan patience)
        {
            var text = $"{step} ({(int)elapsed.TotalSeconds} s)";
            return elapsed < patience
                ? text
                : text + " – this is taking long: Studio may be showing a message (answer it there)";
        }

        private static string SafePath(FileBasedProject p)
        {
            try { return p?.FilePath; } catch { return null; }
        }
    }
}
