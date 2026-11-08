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
    /// – the file steps of Studio's own Prepare) and adds it to the Projects view.
    ///
    /// Runs on a pool thread (the caller's background job). Only the
    /// ProjectsController calls go to the UI thread, and they do not grow with the
    /// file count. Any failure deletes the half-made project, see <see cref="Run"/>.
    ///
    /// Licence (decoded from Sdl.ProjectAutomation.FileBased.dll in Studio 2022,
    /// 2024 and 2026, 8 Nov 2026): the constructor refuses with a
    /// LicensingException when the licence is not valid on this machine, when it
    /// runs OUTSIDE Studio without a Professional licence, on Starter (2022/2024
    /// only), and when the licence lacks the "AllowProjectCreation" feature. Inside
    /// Studio a Freelance licence passes the edition check; the feature check is
    /// the one New Project needs too. A LicensingException is turned into a plain
    /// sentence, never shown raw.
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

        internal static Result Run(ProjectPlan plan, Action<string> progress, Action<string> message)
        {
            var result = new Result();
            FileBasedProject project = null;
            bool done = false;
            string step = "creating the project";
            try
            {
                var info = new ProjectInfo
                {
                    Name = plan.Name,
                    LocalProjectFolder = plan.ProjectFolder,
                };
                if (plan.SourceLanguage != null) info.SourceLanguage = new Language(plan.SourceLanguage);
                if (plan.TargetLanguages.Count > 0)
                    info.TargetLanguages = plan.TargetLanguages.Select(t => new Language(t)).ToArray();

                progress?.Invoke("creating the project");
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
                if (ids.Count == 0) throw new InvalidOperationException("Studio added none of the files.");

                // ---- prepare: the file steps of Studio's Prepare ---------------
                step = "preparing the files (scan, convert to SDLXLIFF, copy to target languages)";
                progress?.Invoke("preparing the files");
                var errors = new List<string>();
                EventHandler<TaskStatusEventArgs> onStatus = (s, e) =>
                {
                    try { progress?.Invoke($"preparing the files: {e.PercentComplete}%"); } catch { }
                };
                EventHandler<TaskMessageEventArgs> onMsg = (s, e) =>
                {
                    try
                    {
                        var m = e?.Message;
                        if (m == null) return;
                        var text = (m.Message ?? m.ToString() ?? "").Trim();
                        if (text.Length == 0) return;
                        bool isError = m.Level.ToString().IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isError) lock (errors) { if (errors.Count < 20) errors.Add(text); }
                        message?.Invoke((isError ? "Error: " : "") + text);
                    }
                    catch { }
                };
                var task = project.RunAutomaticTasks(ids.ToArray(), new[]
                {
                    AutomaticTaskTemplateIds.Scan,
                    AutomaticTaskTemplateIds.ConvertToTranslatableFormat,
                    AutomaticTaskTemplateIds.CopyToTargetLanguages,
                }, onStatus, onMsg);

                var status = task?.Status.ToString() ?? "";
                if (status.IndexOf("Fail", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("Reject", StringComparison.OrdinalIgnoreCase) >= 0
                    || status.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0
                    || errors.Count > 0)
                {
                    throw new StepFailedException("Studio could not prepare the files" +
                        (errors.Count > 0 ? ": " + string.Join(" | ", errors) : $" (task status {status}).") +
                        " Often this means a file type Studio cannot open, or a file that is open in another program.");
                }

                step = "saving the project";
                project.Save();
                result.ProjectFile = project.FilePath;

                // ---- list what was made ---------------------------------------
                var sourceFiles = project.GetSourceLanguageFiles();
                foreach (var f in sourceFiles.Where(f => f.Role == FileRole.Translatable))
                    result.Files.Add(Path.Combine(f.Folder ?? "", f.Name ?? "").TrimStart('\\'));
                var langs = plan.TargetLanguages.Count > 0
                    ? plan.TargetLanguages
                    : (project.GetProjectInfo().TargetLanguages ?? new Language[0]).Select(l => l.IsoAbbreviation).ToList();
                foreach (var lang in langs)
                {
                    int n = project.GetTargetLanguageFiles(new Language(lang)).Count(f => f.Role == FileRole.Translatable);
                    if (n < result.Files.Count)
                        result.Warnings.Add($"{lang}: {n} of {result.Files.Count} files were prepared.");
                }

                // ---- show it in Studio's Projects view (UI thread) -------------
                step = "adding the project to Studio's Projects list";
                progress?.Invoke(step);
                var path = result.ProjectFile;
                string addError = null;
                UiThread.Invoke(() =>
                {
                    try
                    {
                        var pc = SdlTradosStudio.Application.GetController<ProjectsController>();
                        bool listed = pc.GetAllProjects().Any(p =>
                            string.Equals(SafePath(p), path, StringComparison.OrdinalIgnoreCase));
                        if (!listed) pc.Add(path);
                    }
                    catch (Exception ex) { addError = ex.Message; }
                });
                if (addError != null) throw new StepFailedException("Studio would not add the project to its Projects list: " + addError);

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

        private static string SafePath(FileBasedProject p)
        {
            try { return p?.FilePath; } catch { return null; }
        }

        /// <summary>Plain words for the AI to relay; the licence refusal above all.</summary>
        private static string Explain(Exception ex, string step)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.GetType().Name == "LicensingException")
                    return "Your Trados Studio licence does not allow creating projects through the API (" + e.Message +
                           "). Create the project with Studio's New Project instead.";
                if (e is StepFailedException) return e.Message;
            }
            return $"Creating the project failed while {step}: {ex.Message}.";
        }

        private sealed class StepFailedException : Exception
        {
            public StepFailedException(string message) : base(message) { }
        }
    }
}
