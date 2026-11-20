using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Keeps a project's Supervertaler set-up in its Studio settings bundle as well as in the sidecar file, so that
    /// a project template made from the project carries it into every new project (client profiles, step 1). The
    /// rules are <see cref="ProjectProfile"/>'s; this class only reads and writes Studio and keeps what it read.
    ///
    /// <para><b>One reader and one writer.</b> <see cref="ProjectSettings.Load"/> calls <see cref="ApplyTo"/> and
    /// <see cref="ProjectSettings.Save"/> calls <see cref="BeforeSave"/>; nothing else touches the group's values,
    /// so there is no second copy that could save over the first (docs\design\settings-single-source-of-truth.md).</para>
    ///
    /// <para><b>Cache.</b> Per .sdlproj path, what the bundle held when last read or written, with this computer's
    /// termbase list and the project's MultiTerm paths. A project's bundle is read once when it becomes the
    /// project in the editor (<see cref="Invalidate"/> from TermLens on every project change) and then served from
    /// here, so a request costs a dictionary lookup, never a read of the project. The project's GUID is kept with
    /// it; a different project at the same path is read afresh.</para>
    ///
    /// <para><b>Threads.</b> Studio's project objects are read and written on the UI thread only, as everywhere else
    /// in the plugin (TmFieldStampMonitor). Every change to the cache happens there too, so the lock only guards
    /// the dictionary for readers on other threads, and is never held across a Studio call. A background reader
    /// with nothing cached gets the sidecar alone, as before 20.209. A background save, or a save for a project
    /// that is not open, cannot reach the bundle: it marks the sidecar (<see cref="ProjectSettings.ProfileSyncPending"/>)
    /// and the bundle is brought up to date the next time the project is read on the UI thread.</para>
    ///
    /// <para><b>No race between projects.</b> Values are written only to the Studio project whose FilePath equals
    /// the path being saved, resolved at the moment of writing; the cache is keyed by that path.</para>
    ///
    /// <para><b>Scale.</b> One entry per project opened in the session (tens), each a few short strings plus the
    /// termbase list (tens of rows). A read is a lookup plus resolving that list: microseconds.</para>
    /// </summary>
    public static class ProjectProfileStore
    {
        sealed class Entry
        {
            public string ProjectGuid;
            public ProjectProfileValues Group;
            public List<TermbaseIdentity> Catalog;
            public List<string> MultiTermPaths;
        }

        static readonly object _lock = new object();
        static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);
        static int _uiThread;   // 0 outside Studio: then the bundle is never touched

        const string Log = "ProjectProfile";

        /// <summary>Call on Studio's UI thread, once (later calls change nothing).</summary>
        public static void AttachUiThread()
        {
            if (_uiThread == 0) _uiThread = Thread.CurrentThread.ManagedThreadId;
        }

        static bool OnUiThread => _uiThread != 0 && Thread.CurrentThread.ManagedThreadId == _uiThread;

        /// <summary>
        /// Studio's UI thread, for job profiles and the customer list: the attached one, or - should no panel have
        /// attached it yet - a thread that is not a pool thread and has a synchronisation context (WinForms or WPF).
        /// </summary>
        internal static bool IsUiThread => _uiThread != 0
            ? OnUiThread
            : !Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current != null;

        /// <summary>Forget what was read for <paramref name="projectPath"/>: the next read takes it from the project again.</summary>
        public static void Invalidate(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return;
            lock (_lock) _entries.Remove(projectPath.Trim());
        }

        /// <summary>
        /// Puts the project's bundle values over <paramref name="ps"/> (the sidecar), wherever the bundle has one;
        /// leaves <paramref name="ps"/> as it is when the sidecar holds a change the bundle has not received, or the
        /// bundle cannot be read. Never throws.
        /// </summary>
        public static void ApplyTo(string projectPath, ProjectSettings ps)
        {
            try
            {
                if (ps == null || string.IsNullOrEmpty(projectPath)) return;
                var e = Cached(projectPath);
                if (e == null && OnUiThread) e = Fill(projectPath, ps);
                if (e == null || ps.ProfileSyncPending) return;
                ProjectProfile.Overlay(ps, e.Group, e.Catalog, m => ReportOnce(projectPath, m));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(Log, "reading the project settings bundle failed for " + projectPath + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Called by <see cref="ProjectSettings.Save"/> before it writes the sidecar: writes the bundle when its
        /// values differ and the project can be written now, and sets <see cref="ProjectSettings.ProfileSyncPending"/>
        /// to whether the bundle is behind. Never throws.
        /// </summary>
        public static void BeforeSave(string projectPath, ProjectSettings ps)
        {
            if (ps == null || string.IsNullOrEmpty(projectPath)) return;
            try
            {
                var e = Cached(projectPath);
                if (e == null && OnUiThread) e = Fill(projectPath, null);
                if (e == null)
                {
                    // Not open here (another project's file, a bank renamed everywhere), or a
                    // background thread before the project was read: the file carries it.
                    ps.ProfileSyncPending = true;
                    return;
                }

                // This computer's termbases as they are now, not as when the project was read:
                // one created since and switched on must reach the bundle. Saves are user
                // actions and project switches, so one small query each is nothing.
                var catalog = ReadCatalog(ps);
                if (catalog.Count == 0) catalog = e.Catalog;
                var mtPaths = (OnUiThread ? MultiTermPathsNow(projectPath) : null) ?? e.MultiTermPaths;
                var desired = ProjectProfile.FromSidecar(ps, e.Group, catalog, ProjectProfile.MultiTermPaths(mtPaths, e.Group));
                if (desired.SameAs(e.Group))
                {
                    ps.ProfileSyncPending = false;
                    if (OnUiThread) Prime(projectPath, e.ProjectGuid, e.Group, catalog, mtPaths);
                    return;
                }

                ps.ProfileSyncPending = !(OnUiThread && Write(projectPath, e, desired, catalog));
                if (ps.ProfileSyncPending)
                    DiagnosticLog.Log(Log, "project settings bundle not written now for " + projectPath
                        + (OnUiThread ? "" : " (not the UI thread)") + "; written the next time the project is open");
            }
            catch (Exception ex)
            {
                ps.ProfileSyncPending = true;
                DiagnosticLog.Log(Log, "writing the project settings bundle failed for " + projectPath + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Puts a known entry in the cache. What <see cref="Fill"/> does with what it read from Studio; also how
        /// the test outside Studio (tools\project_profile_test.ps1) stands in for a project.
        /// </summary>
        internal static void Prime(string projectPath, string projectGuid, ProjectProfileValues group,
            List<TermbaseIdentity> catalog, List<string> multiTermPaths)
        {
            var e = new Entry
            {
                ProjectGuid = projectGuid ?? "",
                Group = group ?? new ProjectProfileValues(),
                Catalog = catalog ?? new List<TermbaseIdentity>(),
                MultiTermPaths = multiTermPaths ?? new List<string>(),
            };
            lock (_lock) _entries[projectPath.Trim()] = e;
        }

        static Entry Cached(string projectPath)
        {
            lock (_lock) return _entries.TryGetValue(projectPath.Trim(), out var e) ? e : null;
        }

        static void ReportOnce(string projectPath, string message)
        {
            lock (_lock) { if (!_reported.Add(projectPath + "|" + message)) return; }
            DiagnosticLog.WriteAlways(Log, Path.GetFileName(projectPath) + ": " + message);
        }

        /// <summary>
        /// The project now in the editor. A different project at a path that was read before (a project deleted
        /// and made again under the same name) has a different GUID: what was read for the old one is dropped, so
        /// its values can never be served for the new one. UI thread.
        /// </summary>
        public static void ProjectInEditor(string projectPath, string projectGuid)
        {
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(projectGuid)) return;
            var e = Cached(projectPath);
            if (e != null && e.ProjectGuid.Length > 0 && !string.Equals(e.ProjectGuid, projectGuid, StringComparison.OrdinalIgnoreCase))
            {
                Invalidate(projectPath);
                DiagnosticLog.Log(Log, "another project at " + projectPath + " than the one read before: read again");
            }
        }

        /// <summary>
        /// Job profiles (step 2): puts a template's Supervertaler values into <paramref name="project"/>, the template's
        /// wherever it has one (<see cref="JobProfile.MergeForApply"/>), with the job profile's name. Written into the
        /// project's bundle (the caller saves the project) and into <paramref name="ps"/>, the project's settings file,
        /// which is then saved the usual way. A project from a package or GroupShare gets them in Supervertaler's own
        /// record only, as in <see cref="Write"/>. UI thread.
        /// </summary>
        internal static ApplyStep ApplyProfile(FileBasedProject project, ProjectSettings ps, ProjectProfileValues template, string profileName)
        {
            const string what = JobProfile.StepSupervertaler;
            if (!IsUiThread || project == null || ps == null)
                return new ApplyStep(what, StepOutcome.Failed, "not available here (no project, or not Studio's window thread)");
            try
            {
                string path = project.FilePath;
                var current = ProjectBundleSettings.ReadValues(project.GetSettings()) ?? new ProjectProfileValues();
                var merged = JobProfile.MergeForApply(current, template, profileName);
                var catalog = ReadCatalog(ps);
                bool elsewhere = FromElsewhere(project);
                if (!elsewhere)
                {
                    var bundle = project.GetSettings();
                    if (ProjectBundleSettings.WriteValues(bundle, merged)) project.UpdateSettings(bundle);
                }
                // What the project's bundle now holds: the next read is served from here.
                Prime(path, GuidOf(project), elsewhere ? current : merged, catalog, MultiTermPathsOf(project));

                var notes = new List<string>();
                ps.ProfileSyncPending = false;   // the profile is newer than anything the file was waiting to hand on
                ProjectProfile.Overlay(ps, merged, catalog, m => notes.Add(m));
                if (catalog.Count == 0 && ProjectProfile.Lines(template?.Termbases).Count > 0)
                    notes.Add("its Supervertaler termbase choices are kept in the project but not used here: this computer's termbase database could not be read");
                ProjectSettings.Save(path, ps, "job profile");
                DiagnosticLog.Log(Log, "job profile '" + profileName + "' applied to " + Path.GetFileName(path) + ": " + merged.Describe()
                    + (elsewhere ? " (own record only)" : ""));

                var detail = elsewhere
                    ? "kept in Supervertaler's own record only (the project came from a package or GroupShare)"
                    : "written into the project";
                if (notes.Count > 0) detail += "; " + string.Join("; ", notes);
                // What it held, one line each under the bullet (Michael, 20 Nov: "written into the project" said too little).
                foreach (var item in JobProfile.SetupItems(template)) detail += "\r\n      \u2013 " + item;
                return new ApplyStep(what, StepOutcome.Done, detail);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "applying a job profile's Supervertaler set-up failed: " + ex.Message);
                return new ApplyStep(what, StepOutcome.Failed, ex.Message);
            }
        }

        // ─── Studio (UI thread only) ────────────────────────────────

        [MethodImpl(MethodImplOptions.NoInlining)]
        static List<string> MultiTermPathsNow(string projectPath)
        {
            var project = ProjectAt(projectPath);
            return project == null ? null : MultiTermPathsOf(project);
        }

        /// <summary>
        /// Reads the project's bundle into the cache. With <paramref name="ps"/> marked as holding changes the
        /// bundle lacks, writes those first. Null when the project is not open in Studio.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static Entry Fill(string projectPath, ProjectSettings ps)
        {
            var project = ProjectAt(projectPath);
            if (project == null) return null;

            var group = ProjectBundleSettings.ReadValues(project.GetSettings()) ?? new ProjectProfileValues();
            var catalog = ReadCatalog(ps);
            var mtPaths = MultiTermPathsOf(project);
            Prime(projectPath, GuidOf(project), group, catalog, mtPaths);
            var e = Cached(projectPath);
            DiagnosticLog.Log(Log, "read " + Path.GetFileName(projectPath) + ": " + group.Describe()
                + "; " + catalog.Count + " termbase(s) in this computer's database");

            if (ps != null && ps.ProfileSyncPending)
            {
                // The file holds changes made while the bundle was out of reach: they are the newer ones.
                var desired = ProjectProfile.FromSidecar(ps, group, catalog, ProjectProfile.MultiTermPaths(mtPaths, group));
                if (desired.SameAs(group) || Write(projectPath, e, desired, catalog))
                {
                    ps.ProfileSyncPending = false;
                    ProjectSettings.SaveFile(projectPath, ps, "profile sync");
                    e = Cached(projectPath);
                }
            }
            return e;
        }

        /// <summary>Writes <paramref name="desired"/> into the project at <paramref name="projectPath"/> and saves it. False when it could not.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Write(string projectPath, Entry e, ProjectProfileValues desired, List<TermbaseIdentity> catalog)
        {
            try
            {
                var project = ProjectAt(projectPath);
                if (project == null) return false;
                // The project at this path is not the one read: a new project in the old one's place.
                if (!string.IsNullOrEmpty(e.ProjectGuid) && !string.Equals(GuidOf(project), e.ProjectGuid, StringComparison.OrdinalIgnoreCase))
                {
                    Invalidate(projectPath);
                    return false;
                }

                // A project from a package or GroupShare is the agency's: its settings can travel back
                // to their server, and the translator's bank, prompt names and client must not go with
                // them unasked (the same rule as TM field stamping, Michael 11 Nov). Supervertaler's own
                // record keeps the set-up; nothing is written into the project, and nothing is pending.
                if (FromElsewhere(project))
                {
                    if (ReportedFromElsewhere(projectPath))
                        DiagnosticLog.Log(Log, Path.GetFileName(projectPath) + " came from a package or GroupShare: "
                            + "Supervertaler's set-up is kept in its own record only, not written into the project");
                    return true;
                }

                var bundle = project.GetSettings();
                if (ProjectBundleSettings.WriteValues(bundle, desired))
                {
                    project.UpdateSettings(bundle);
                    project.Save();
                    DiagnosticLog.Log(Log, "wrote " + Path.GetFileName(projectPath) + ": " + desired.Describe());
                }
                Prime(projectPath, e.ProjectGuid, desired, catalog, e.MultiTermPaths);
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "could not write the project settings of " + Path.GetFileName(projectPath)
                    + "; Supervertaler's own record keeps the change and tries again when the project is next open: " + ex.Message);
                return false;
            }
        }

        /// <summary>The project in the editor or selected in Projects whose .sdlproj is <paramref name="projectPath"/>; null otherwise.</summary>
        static FileBasedProject ProjectAt(string projectPath)
        {
            var candidates = new List<FileBasedProject>();
            try { candidates.Add(StudioEditor.Controller?.ActiveDocument?.Project as FileBasedProject); } catch { }
            try { candidates.Add(SdlTradosStudio.Application?.GetController<ProjectsController>()?.CurrentProject); } catch { }
            foreach (var p in candidates)
            {
                try
                {
                    if (p != null && string.Equals(p.FilePath?.Trim(), projectPath.Trim(), StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                catch { }
            }
            return null;
        }

        /// <summary>True for a project from a package or GroupShare (TmFieldStamping's own test). False when unknown.</summary>
        static bool FromElsewhere(FileBasedProject project)
        {
            try { return TmFieldStamping.IsFromElsewhere(project.GetProjectInfo().ProjectType); }
            catch { return false; }
        }

        /// <summary>True the first time for a path in this session, so the log says it once.</summary>
        static bool ReportedFromElsewhere(string projectPath)
        {
            lock (_lock) return _reported.Add(projectPath + "|from elsewhere");
        }

        static string GuidOf(FileBasedProject project)
        {
            try
            {
                var info = project.GetProjectInfo();
                return info == null ? "" : info.Id.ToString("D");
            }
            catch { return ""; }
        }

        static List<string> MultiTermPathsOf(FileBasedProject project)
        {
            var list = new List<string>();
            try
            {
                var config = project.GetTermbaseConfiguration();
                if (config?.Termbases != null)
                    foreach (var tb in config.Termbases)
                        if (tb is LocalTermbase local && !string.IsNullOrEmpty(local.FilePath)) list.Add(local.FilePath);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(Log, "could not list the project's MultiTerm termbases: " + ex.Message);
            }
            return list;
        }

        // ─── this computer's termbase database ──────────────────────

        /// <summary>
        /// The termbases in the database the project uses (its own path, else the global one); empty when there is
        /// no database or it cannot be read - the termbase choices are then left alone, never read as "all off".
        /// </summary>
        static List<TermbaseIdentity> ReadCatalog(ProjectSettings ps)
        {
            string db = ps?.TermbasePath;
            if (string.IsNullOrEmpty(db) || !File.Exists(db))
            {
                try { db = SettingsService.Current?.TermbasePath; } catch { db = null; }
            }
            if (string.IsNullOrEmpty(db) || !File.Exists(db)) return new List<TermbaseIdentity>();
            try
            {
                using (var reader = new TermbaseReader(db))
                    return reader.Open() ? reader.GetTermbaseIdentities() : new List<TermbaseIdentity>();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(Log, "could not read the termbase list of " + db + ": " + ex.Message);
                return new List<TermbaseIdentity>();
            }
        }
    }
}
