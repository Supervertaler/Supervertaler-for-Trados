using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Supervertaler.Trados.Core;

namespace Supervertaler.Trados
{
    /// <summary>
    /// "Open example project" as a background job in the same registry as
    /// create_project and Batch Translate, so it never runs alongside them and
    /// get_task_status can see it.
    /// </summary>
    public partial class AiAssistantViewPart
    {
        internal const string ExampleTask = "example-project";

        /// <summary>What the job is asked to do.</summary>
        internal enum ExampleAction { Create, OpenExisting, StartAfresh }

        /// <summary>
        /// Starts the job. Returns its id, or null with <paramref name="busy"/>
        /// saying which job is running.
        /// </summary>
        internal static string StartExampleProjectJob(ExampleData data, ExampleDirection d,
            ExampleProject.FolderPlan folder, string projectsFolder, ExampleAction action, out string busy)
        {
            busy = null;
            RunTaskJob job;
            lock (_jobStartLock)
            {
                var running = GetRunningJob();
                if (running != null)
                {
                    busy = "Supervertaler is busy with " + running.Friendly + ". Try again when it has finished.";
                    return null;
                }
                job = new RunTaskJob
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Task = ExampleTask,
                    Friendly = "Open example project",
                    Status = "running",
                    StartedUtc = DateTime.UtcNow,
                    FileCount = 1,
                    Progress = "starting"
                };
                RegisterJob(job);
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ => ExampleJobBody(job, data, d, folder, projectsFolder, action));
            return job.Id;
        }

        /// <summary>The job's state, for the progress window.</summary>
        internal static BridgeTaskStatusResponse ExampleJobStatus(string jobId) => BridgeGetTaskStatus(jobId);

        private static void ExampleJobBody(RunTaskJob job, ExampleData data, ExampleDirection d,
            ExampleProject.FolderPlan folder, string projectsFolder, ExampleAction action)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BridgeLog.Write($"[example-project] job {job.Id} started: {action} {d.Source} to {d.Target}");
            Action<string> progress = p => { lock (job.Sync) job.Progress = p; };
            string error = null, projectFile = null;
            bool notOpened = false;
            var warnings = new List<string>();
            try
            {
                if (action == ExampleAction.OpenExisting)
                {
                    var opened = ProjectOpenerStudio.Run(folder.ExistingProjectFile, true, new List<string>(), d.Target, progress);
                    if (opened.Ok) projectFile = opened.ProjectFile;
                    else error = opened.Error;
                }
                else
                {
                    var plan = folder;
                    if (action == ExampleAction.StartAfresh)
                    {
                        error = ExampleProjectStudio.Remove(folder, progress);
                        if (error == null)
                        {
                            // The old name is free now; plan again, with Studio's
                            // list as it is after the removal.
                            plan = ExampleProject.PlanFolder(projectsFolder, d, RegisteredNames());
                            if (plan.Error != null) error = plan.Error;
                        }
                    }
                    if (error == null)
                    {
                        var r = ExampleProjectStudio.Run(data, d, plan, projectsFolder, RegisteredNames(), progress);
                        if (r.Ok) { projectFile = r.ProjectFile; warnings.AddRange(r.Warnings); notOpened = !r.Opened; }
                        else error = r.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                error = "The example project could not be created: " + ex.Message;
            }
            sw.Stop();
            lock (job.Sync)
            {
                job.FinishedUtc = DateTime.UtcNow;
                job.ProjectFile = projectFile;
                job.Messages.AddRange(warnings);
                // The first line of the closing message (ExampleProjectRunner): it
                // must not say "open in the Editor" when Studio did not open it.
                if (error == null)
                    job.Note = action == ExampleAction.OpenExisting
                        ? "The example project is open in the Editor."
                        : notOpened
                            ? "The example project is ready, but Studio did not open it in the Editor."
                            : "The example project is ready and open in the Editor.";
                if (error == null) job.Status = "done";
                else { job.Status = "failed"; job.Error = error; }
            }
            BridgeLog.Write($"[example-project] job {job.Id} {job.Status} in {sw.Elapsed.TotalSeconds:F1}s" +
                            (error == null ? "" : ": " + error));
        }

        /// <summary>The names in the running Studio's Projects list (projects.xml, read once).</summary>
        internal static HashSet<string> RegisteredNames()
        {
            var docs = ProjectCreator.RunningStudioDocumentsFolder();
            return ProjectCreator.RegisteredProjectNames(docs != null ? Path.Combine(docs, "Projects", "projects.xml") : null);
        }

        /// <summary>The running Studio's Projects folder, or null.</summary>
        internal static string StudioProjectsFolder()
        {
            var docs = ProjectCreator.RunningStudioDocumentsFolder();
            return docs != null ? Path.Combine(docs, "Projects") : null;
        }
    }
}
