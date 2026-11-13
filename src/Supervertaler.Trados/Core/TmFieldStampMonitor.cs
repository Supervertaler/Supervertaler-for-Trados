using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Runs <see cref="TmFieldStamping"/> for the project in use: when a project becomes the current one, when a
    /// document opens, and when the switches change. Does nothing while both Studio-TM switches are off (the default).
    ///
    /// <para><b>Never delays Studio.</b> On the UI thread it only reads what Studio holds in memory (the project's
    /// name, type and TM list). The TMs' field definitions and the project file's customer are read on the thread pool;
    /// the result is written back on the UI thread, a GetSettings/UpdateSettings of a few milliseconds, and only when a
    /// value actually changes. A TM's fields are read once and kept until the file changes
    /// (<see cref="TmFieldStamping.FieldsOf"/>), so switching documents reopens nothing.</para>
    ///
    /// <para><b>State:</b> the editor and projects controllers (set once, for the life of Studio, like
    /// <see cref="UnsegmentedTextMonitor"/>), the UI thread's context, and a counter so an older run that finishes after
    /// a newer one started writes nothing.</para>
    /// </summary>
    internal static class TmFieldStampMonitor
    {
        static EditorController _editor;
        static ProjectsController _projects;
        static SynchronizationContext _ui;
        static int _run;

        /// <summary>What the last run did, for the settings window ("filled: Project = PROJ-001 ..."); null before any.</summary>
        internal static string LastOutcome { get; private set; }

        /// <summary>Call on the UI thread. Only the first call does anything.</summary>
        public static void Attach(EditorController editor)
        {
            if (editor == null || _editor != null) return;
            _editor = editor;
            _ui = SynchronizationContext.Current;
            editor.ActiveDocumentChanged += (s, e) => Run("document");
            try
            {
                _projects = SdlTradosStudio.Application?.GetController<ProjectsController>();
                if (_projects != null) _projects.CurrentProjectChanged += (s, e) => Run("project");
            }
            catch (Exception ex) { DiagnosticLog.Log(TmFieldStamping.LogCategory, "No project events: " + ex.Message); }
            SettingsService.Changed += (s, e) => Post(() => Run("settings"));
            Run("start");
        }

        static void Post(Action a)
        {
            if (_ui != null) _ui.Post(_ => a(), null); else a();
        }

        static FileBasedProject CurrentProject()
        {
            try { if (_editor?.ActiveDocument?.Project is FileBasedProject p) return p; } catch { }
            try { return _projects?.CurrentProject; } catch { return null; }
        }

        /// <summary>UI thread. Starts a run for the project in use; returns at once.</summary>
        internal static void Run(string why)
        {
            try
            {
                var s = SettingsService.Current;
                if (s == null || (!s.StampStudioTmProject && !s.StampStudioTmClient)) return;   // the default: nothing at all
                var project = CurrentProject();
                if (project == null) return;
                var info = project.GetProjectInfo();
                if (TmFieldStamping.IsFromElsewhere(info.ProjectType) && !AllowedInImportedProject(project))
                {
                    LastOutcome = "'" + info.Name + "' came from a package or GroupShare: not stamped (switched off for such projects unless you switch it on for this one).";
                    DiagnosticLog.Log(TmFieldStamping.LogCategory, LastOutcome);
                    return;
                }
                var tms = TmFieldStamping.TmsOf(project);
                string name = info.Name, projectFile = SafeFilePath(project);
                bool wantProject = s.StampStudioTmProject, wantClient = s.StampStudioTmClient;
                int run = Interlocked.Increment(ref _run);
                Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    string customer = null;
                    if (wantClient && projectFile != null)
                        try { customer = SuperVault.StudioProjects.ReadProject(projectFile).Item2; }
                        catch (Exception ex) { DiagnosticLog.Log(TmFieldStamping.LogCategory, "Could not read the project's customer: " + ex.Message); }
                    var fields = new Dictionary<string, List<Sdl.LanguagePlatform.TranslationMemoryApi.FieldDefinition>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tm in tms.Where(t => t.IsFile && t.Update))
                        if (!fields.ContainsKey(tm.Path)) fields[tm.Path] = TmFieldStamping.FieldsOf(tm.Path);
                    long readMs = sw.ElapsedMilliseconds;
                    var wanted = TmFieldStamping.Wanted(wantProject, wantClient, name, customer);
                    Post(() =>
                    {
                        if (run != _run) return;   // a newer run started meanwhile: it writes, not this one
                        try
                        {
                            if (CurrentProject()?.FilePath != project.FilePath) return;   // the user moved on
                            var outcome = TmFieldStamping.Apply(project, tms, wanted, p => fields.TryGetValue(p, out var f) ? f : null);
                            LastOutcome = "'" + name + "': " + outcome;
                            DiagnosticLog.Log(TmFieldStamping.LogCategory, "(" + why + ") " + tms.Count + " TM(s), read in " + readMs + " ms");
                        }
                        catch (Exception ex) { DiagnosticLog.Log(TmFieldStamping.LogCategory, "Could not fill the field values: " + ex.Message); }
                    });
                });
            }
            catch (Exception ex)
            {
                // Studio between projects, a GroupShare project the API won't read: nothing stamped, never an error.
                DiagnosticLog.Log(TmFieldStamping.LogCategory, "Not run (" + why + "): " + ex.Message);
            }
        }

        static string SafeFilePath(FileBasedProject p) { try { return p.FilePath; } catch { return null; } }

        /// <summary>The per-project switch for a project from a package or GroupShare (kept in the project). UI thread.</summary>
        internal static bool AllowedInImportedProject(FileBasedProject project)
        {
            try { return project.GetSettings().GetSettingsGroup<SupervertalerProjectSettingsGroup>().StampStudioTmsInImportedProject.Value; }
            catch { return false; }
        }

        internal static void SetAllowedInImportedProject(FileBasedProject project, bool allowed)
        {
            var bundle = project.GetSettings();
            bundle.GetSettingsGroup<SupervertalerProjectSettingsGroup>().StampStudioTmsInImportedProject.Value = allowed;
            project.UpdateSettings(bundle);
        }

        /// <summary>The project in use, for the settings window. UI thread.</summary>
        internal static FileBasedProject Project => CurrentProject();
    }
}
