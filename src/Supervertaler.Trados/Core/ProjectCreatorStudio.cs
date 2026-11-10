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
    /// MCP <c>create_project</c>, the part that needs Studio: creates the project
    /// from a checked <see cref="ProjectPlan"/> through the Project Automation API,
    /// prepares it (Scan, Convert to Translatable Format, Copy to Target Languages
    /// – the file steps of Studio's own Prepare) and makes sure it is in the
    /// Projects view.
    ///
    /// Runs on a pool thread (the caller's background job). Only the
    /// ProjectsController calls go to the UI thread, and they do not grow with the
    /// file count. Any failure deletes the half-made project, see <see cref="Run"/>.
    ///
    /// Licence (decoded from Sdl.ProjectAutomation.FileBased.dll in Studio 2022,
    /// 2024 and 2026, 8 Nov 2026; confirmed on a regular Studio 2024 licence,
    /// 10 Nov 2026): the constructor refuses with a LicensingException when the
    /// licence is not valid on this machine, when it runs OUTSIDE Studio without
    /// a Professional licence, on Starter (2022/2024 only), and when the licence
    /// lacks the "AllowProjectCreation" feature. A LicensingException is turned
    /// into a plain sentence, never shown raw.
    /// </summary>
    internal static class ProjectCreatorStudio
    {
        internal sealed class Result
        {
            public bool Ok;
            public string Error;
            public string ProjectFile;
            public List<string> Files = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        /// <summary>How long a UI-thread step may take before progress says Studio
        /// may be showing a message. Normal steps take well under a second.</summary>
        private static readonly TimeSpan UiPatience = TimeSpan.FromSeconds(4);

        internal static Result Run(ProjectPlan plan, Action<string> progress, Action<string> message)
        {
            var result = new Result();
            FileBasedProject project = null;
            bool done = false;
            string step = "setting up the project";
            try
            {
                var info = new ProjectInfo
                {
                    Name = plan.Name,
                    LocalProjectFolder = plan.ProjectFolder,
                };
                // Plan fills the languages from the request or from the template's
                // one language pair, so Studio never has to choose.
                if (plan.SourceLanguage != null) info.SourceLanguage = new Language(plan.SourceLanguage);
                if (plan.TargetLanguages.Count > 0)
                    info.TargetLanguages = plan.TargetLanguages.Select(t => new Language(t)).ToArray();

                progress?.Invoke("setting up the project");
                project = plan.TemplatePath != null
                    ? new FileBasedProject(info, new ProjectTemplateReference(plan.TemplatePath))
                    : new FileBasedProject(info);

                // ---- files: one AddFiles per subfolder, not per file ------------
                step = "adding the files";
                var ids = new List<Guid>();
                int added = 0;
                foreach (var group in ProjectCreator.GroupBySubfolder(plan))
                {
                    var pf = group.Key.Length == 0
                        ? project.AddFiles(group.Value)
                        : project.AddFiles(group.Value, group.Key);
                    if (pf != null) ids.AddRange(pf.Select(f => f.Id));
                    added += group.Value.Length;
                    progress?.Invoke($"adding the files ({added}/{plan.Files.Count})");
                }
                if (ids.Count == 0) throw new StepFailedException("Studio added none of the files.");

                // ---- prepare: the file steps of Studio's Prepare ---------------
                step = "preparing the files";
                progress?.Invoke("preparing the files");
                var problems = new List<string>();   // every warning and error, in full
                EventHandler<TaskStatusEventArgs> onStatus = (s, e) =>
                {
                    try { progress?.Invoke($"preparing the files: {e.PercentComplete}%"); } catch { }
                };
                EventHandler<TaskMessageEventArgs> onMsg = (s, e) =>
                {
                    try
                    {
                        var text = MessageText(e?.Message);
                        if (text == null) return;
                        var level = e.Message.Level.ToString();
                        bool notable = level.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                                    || level.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (notable) lock (problems) { if (problems.Count < 20) problems.Add(text); }
                        message?.Invoke(level + ": " + text);
                    }
                    catch { }
                };
                var task = project.RunAutomaticTasks(ids.ToArray(), new[]
                {
                    AutomaticTaskTemplateIds.Scan,
                    AutomaticTaskTemplateIds.ConvertToTranslatableFormat,
                    AutomaticTaskTemplateIds.CopyToTargetLanguages,
                }, onStatus, onMsg);

                // Studio reports a file it has no file type for as a warning, calls
                // the task "completed" and copies the file unconverted (seen with a
                // .zip in Studio 2024, 10 Nov 2026). So judge by the files, not the
                // status: every source file must have a file type and every target
                // file must be an SDLXLIFF.
                var langs = plan.TargetLanguages.Count > 0
                    ? plan.TargetLanguages
                    : (project.GetProjectInfo().TargetLanguages ?? new Language[0]).Select(l => l.IsoAbbreviation).ToList();
                var unconverted = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in project.GetSourceLanguageFiles().Where(f => f.Role == FileRole.Translatable))
                    if (string.IsNullOrEmpty(f.FileTypeId)) unconverted.Add(DisplayName(f));
                foreach (var lang in langs)
                    foreach (var f in project.GetTargetLanguageFiles(new Language(lang)).Where(f => f.Role == FileRole.Translatable))
                        if (!(f.LocalFilePath ?? "").EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase))
                            unconverted.Add(StripSdlxliff(DisplayName(f)));

                var status = task?.Status.ToString() ?? "";
                bool statusBad = status.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0
                              || status.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0
                              || status.IndexOf("Reject", StringComparison.OrdinalIgnoreCase) >= 0
                              || status.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0;
                if (unconverted.Count > 0 || statusBad)
                {
                    string detail;
                    lock (problems) detail = problems.Count > 0 ? " Studio said: " + string.Join(" | ", problems) : "";
                    throw new StepFailedException(unconverted.Count > 0
                        ? $"Studio could not convert {unconverted.Count} of the {plan.Files.Count} file(s), so the project was not created: " +
                          string.Join(", ", unconverted) + ". Studio has no file type for them, or could not read them. " +
                          "Leave them out and ask again." + detail
                        : $"Studio could not prepare the files (task status {status})." + detail);
                }

                step = "saving the project";
                project.Save();
                result.ProjectFile = project.FilePath;
                lock (problems)
                    foreach (var w in problems) result.Warnings.Add("Studio: " + w);

                foreach (var f in project.GetSourceLanguageFiles().Where(f => f.Role == FileRole.Translatable))
                    result.Files.Add(DisplayName(f));

                // ---- make sure it is in Studio's Projects list -----------------
                step = "adding the project to Studio's Projects list";
                progress?.Invoke(step);
                RegisterInProjectsList(project, result.ProjectFile, progress);

                done = true;
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = Explain(ex, step);
                return result;
            }
            finally
            {
                // Failure path: remove what this run made, Studio's own record of
                // it first, then whatever is left in the folder. Runs whatever
                // threw, including the licence refusal (which writes nothing).
                if (!done)
                {
                    if (project != null)
                    {
                        try { project.Delete(); } catch { /* not saved yet, or already gone */ }
                    }
                    var why = ProjectCreator.CleanUp(plan);
                    result.Error = (result.Error ?? "Creating the project failed.") + (why == null
                        ? " Nothing was left behind."
                        : " Clean-up was incomplete, please check: " + why);
                }
            }
        }

        /// <summary>
        /// A project made through the API inside Studio is put in Studio's
        /// project list by Studio itself (IProjectManagementService.Projects,
        /// which GetAllProjects reads). ProjectsController.Add goes to the
        /// Projects view's AddProjects, which shows a MODAL "This project has
        /// already been opened in Trados Studio and added to the project list"
        /// when the project is already there, and the job then waits for a click
        /// (Studio 2024, 10 Nov 2026, whenever another project was open). So Add
        /// is the last resort: look for the project by path and by id, give
        /// Studio a few moments to list it, reload the list, and only then add it.
        /// </summary>
        private static void RegisterInProjectsList(FileBasedProject project, string path, Action<string> progress)
        {
            Guid id = Guid.Empty;
            try { id = project.GetProjectInfo().Id; } catch { }
            string want = NormalPath(path);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (attempt > 0) System.Threading.Thread.Sleep(500);
                bool reload = attempt == 5;   // last look: reload the list first
                if (OnUi(() => IsListed(want, id, reload), progress)) return;
            }
            OnUi(() =>
            {
                SdlTradosStudio.Application.GetController<ProjectsController>().Add(path);
                return true;
            }, progress);
        }

        private static bool IsListed(string normalPath, Guid id, bool reload)
        {
            var pc = SdlTradosStudio.Application.GetController<ProjectsController>();
            if (reload) pc.RefreshProjects();
            var all = pc.GetAllProjects().ToList();
            if (normalPath != null && all.Any(p => NormalPath(SafePath(p)) == normalPath)) return true;
            if (id == Guid.Empty) return false;
            foreach (var p in all)
            {
                try { if (p.GetProjectInfo().Id == id) return true; } catch { }
            }
            return false;
        }

        /// <summary>
        /// Runs <paramref name="f"/> on the UI thread. If that takes longer than
        /// <see cref="UiPatience"/>, progress says so: Studio may be showing a
        /// message that waits for a click, and the job must not look as if it
        /// were quietly working.
        /// </summary>
        private static T OnUi<T>(Func<T> f, Action<string> progress)
        {
            var t = System.Threading.Tasks.Task.Run(() => UiThread.Invoke(f));
            if (!t.Wait(UiPatience))
            {
                progress?.Invoke("waiting for Studio (a message may be open in Studio – answer it there)");
                try { t.Wait(); } catch { }
            }
            return t.GetAwaiter().GetResult();
        }

        internal static string NormalPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            try { p = Path.GetFullPath(p.Trim()); } catch { }
            return p.TrimEnd('\\').ToUpperInvariant();
        }

        private static string SafePath(FileBasedProject p)
        {
            try { return p?.FilePath; } catch { return null; }
        }

        private static string DisplayName(ProjectFile f)
        {
            try { return Path.Combine(f.Folder ?? "", f.Name ?? "").TrimStart('\\'); }
            catch { return f?.Name ?? "?"; }
        }

        private static string StripSdlxliff(string name) =>
            name.EndsWith(".sdlxliff", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 9) : name;

        /// <summary>The whole of a task message: its source, text and any
        /// exception text, which is where Studio puts the detail ("Pre-Scanning
        /// failed to identify the file type, the following messages were
        /// generated:" is only the first line).</summary>
        private static string MessageText(ExecutionMessage m)
        {
            if (m == null) return null;
            var parts = new List<string>();
            void Add(string x)
            {
                x = (x ?? "").Trim();
                if (x.Length > 0 && !parts.Any(p => p.IndexOf(x, StringComparison.Ordinal) >= 0)) parts.Add(x);
            }
            try { Add(m.Source); } catch { }
            Add(m.Message);
            try
            {
                for (var ex = m.Exception; ex != null; ex = ex.InnerException)
                    Add(ex.Message);
            }
            catch { }
            return parts.Count == 0 ? null : string.Join(" – ", parts);
        }

        /// <summary>Plain words for the AI to relay; the licence refusal above all.</summary>
        private static string Explain(Exception ex, string step)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.GetType().Name == "LicensingException")
                    return "Your Trados Studio licence does not allow creating projects through the API (" +
                           ProjectCreator.Sentence(e.Message, false) + "). Create the project with Studio's New Project instead.";
                if (e is StepFailedException) return ProjectCreator.Sentence(e.Message, true);
            }
            return $"Studio stopped while {step}: {ProjectCreator.Sentence(ex.Message, true)}";
        }

        private sealed class StepFailedException : Exception
        {
            public StepFailedException(string message) : base(message) { }
        }
    }
}
