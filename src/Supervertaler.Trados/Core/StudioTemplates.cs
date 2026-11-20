using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Settings;
using Sdl.ProjectAutomation.FileBased;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Project templates, step 4: making, updating and editing Studio project templates through Studio's own template
    /// objects. Studio's public API has no template object, so this reaches Studio's internal one the way
    /// <see cref="StudioCustomers"/> reaches the customer list, by reflection on public interface members of
    /// <c>Sdl.ProjectApi</c> (identical in Studio 2022, 2024 and 2026, from their decompiled code):
    /// <c>IProjectsProvider.ProjectTemplates / CreateNewProjectTemplate(description, path, IProject) / Save()</c>, and
    /// <c>IProjectTemplate.FilePath / Settings / UpdateFromProject(IProject) / Save() / DiscardCachedData()</c>.
    ///
    /// <para><b>Why through Studio and not the file.</b> Studio loads each registered template once per session and
    /// writes its in-memory copy back on its own Edit; a file changed behind its back would be stale in Studio and could
    /// be overwritten (findings of step 3). Changing Studio's own object and calling its Save keeps both in step. Only
    /// templates registered in this Studio are changed; for others the caller says how to register them.</para>
    ///
    /// <para><b>Failure paths.</b> Studio's Save rewrites the file in place, so before any change the file is copied to
    /// a backup in %TEMP%. If anything fails, the backup is copied back and Studio's copy is dropped so it reads the file
    /// again; the backup is deleted once it is not needed, and kept (and named) only when putting it back failed. A new
    /// template never replaces an existing file. Supervertaler's own values are always written from Supervertaler's
    /// record (<see cref="ProjectProfileStore.CurrentValues"/>), also for a project from a package, whose Studio
    /// settings hold the agency's values.</para>
    ///
    /// <para><b>Threads and scale.</b> UI thread only. One template per call; looking it up walks Studio's template list
    /// once (tens of templates).</para>
    /// </summary>
    internal static class StudioTemplates
    {
        const string Log = "ProjectTemplates";
        const string Unreachable = "Studio's project templates could not be reached from Supervertaler (this Studio version may keep them differently). Nothing was changed.";

        static bool OnUiThread => ProjectProfileStore.IsUiThread;

        static object Provider(FileBasedProject project) =>
            StudioCustomers.Get(StudioCustomers.Get(project ?? StudioCustomers.AnyProject()
                ?? throw new InvalidOperationException("No Studio project is open or selected. Open or select one (any project), then try again."),
                "InternalProject"), "ProjectsProvider");

        static List<object> Templates(object provider) =>
            ((StudioCustomers.Get(provider, "ProjectTemplates") as IEnumerable) ?? throw new StudioCustomers.BridgeException("ProjectTemplates is not a list"))
                .Cast<object>().ToList();

        static object FindTemplate(object provider, string path) =>
            Templates(provider).FirstOrDefault(t => SamePath(StudioCustomers.Get(t, "FilePath") as string, path));

        static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>The files of the templates this Studio has in its list; null when they cannot be read. UI thread.</summary>
        internal static HashSet<string> RegisteredPaths()
        {
            if (!OnUiThread) return null;
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in Templates(Provider(null)))
                {
                    var p = StudioCustomers.Get(t, "FilePath") as string;
                    if (string.IsNullOrEmpty(p)) continue;
                    try { set.Add(Path.GetFullPath(p)); } catch { set.Add(p); }
                }
                return set;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log(Log, "could not list Studio's templates: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Makes a new template at <paramref name="path"/> from <paramref name="project"/>, as Studio's Create Project
        /// Template does, then writes <paramref name="supervertaler"/> into it and adds it to Studio's list. Null when
        /// done; otherwise why not, for the user. UI thread.
        /// </summary>
        internal static string SaveAsNew(FileBasedProject project, string path, ProjectProfileValues supervertaler)
        {
            var why = ProjectTemplateEdit.CheckNewTemplatePath(path, File.Exists);
            if (why != null) return why;
            return Run("saving the project as a template", () =>
            {
                var provider = Provider(project);
                var internalProject = StudioCustomers.Get(project, "InternalProject");
                object template = null;
                try
                {
                    template = StudioCustomers.Call(provider, "CreateNewProjectTemplate", "", path, internalProject);
                    WriteSupervertaler(template, supervertaler, null, false);
                    StudioCustomers.Call(template, "Save");
                    StudioCustomers.Call(provider, "Save");   // Studio's template list (projects.xml)
                }
                catch
                {
                    // Nothing half-made is left behind: no file, no entry in Studio's list.
                    if (template != null) try { StudioCustomers.Call(template, "Delete"); } catch (Exception ex) { DiagnosticLog.WriteAlways(Log, "removing the half-made template failed: " + ex.Message); }
                    try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { DiagnosticLog.WriteAlways(Log, "deleting the half-made file failed: " + ex.Message); }
                    throw;
                }
                DiagnosticLog.Log(Log, "new template " + path + " from " + Path.GetFileName(project.FilePath) + ": " + supervertaler?.Describe());
                return null;
            });
        }

        /// <summary>
        /// Replaces everything in the template at <paramref name="path"/> with <paramref name="project"/>'s settings, as
        /// Studio's "update the template from this project" does, with <paramref name="supervertaler"/> as its Supervertaler
        /// project settings. Backed up first, put back on failure. Null when done. UI thread.
        /// </summary>
        internal static string UpdateFromProject(FileBasedProject project, string path, ProjectProfileValues supervertaler) =>
            Run("updating a template from a project", () =>
            {
                var provider = Provider(project);
                var template = FindTemplate(provider, path);
                if (template == null) return NotRegistered(path);
                var internalProject = StudioCustomers.Get(project, "InternalProject");
                // Studio's UpdateFromProject replaces the template's settings with the project's, and a project keeps its
                // customer elsewhere: the template lost its customer and its created-by details (Michael, 20 Nov; Studio's
                // wizard does the same). So the template's details are kept and the customer becomes the project's.
                var info = ReadInfo(template);
                var customer = StudioCustomers.ProjectCustomer(project, out var customerError);
                if (customerError != null) customer = info.Customer;   // the project's could not be read: keep the template's
                WithBackup(path, template, () =>
                {
                    StudioCustomers.Call(template, "UpdateFromProject", internalProject);
                    WriteSupervertaler(template, supervertaler, customer ?? "", true, info);
                    StudioCustomers.Call(template, "Save");
                });
                DiagnosticLog.Log(Log, "template " + path + " updated from " + Path.GetFileName(project.FilePath) + ": " + supervertaler?.Describe()
                    + "; customer '" + (customer ?? "") + "'");
                return null;
            });

        /// <summary>The template's own details in Studio's GeneralProjectInfoSettings, kept across an update.</summary>
        sealed class TemplateInfo
        {
            public string Customer, CreatedBy, Description;
            public DateTime? CreatedAt;
        }

        static TemplateInfo ReadInfo(object template)
        {
            var info = new TemplateInfo();
            try
            {
                var bundle = StudioCustomers.Get(template, "Settings") as ISettingsBundle;
                if (bundle == null || !bundle.ContainsSettingsGroup("GeneralProjectInfoSettings")) return info;
                var g = bundle.GetSettingsGroup("GeneralProjectInfoSettings");
                if (g.ContainsSetting("CustomerId") && g.GetSetting<string>("CustomerId", out string c)) info.Customer = c;
                if (g.ContainsSetting("CreatedBy") && g.GetSetting<string>("CreatedBy", out string by)) info.CreatedBy = by;
                if (g.ContainsSetting("Description") && g.GetSetting<string>("Description", out string d)) info.Description = d;
                if (g.ContainsSetting("CreatedAt") && g.GetSetting<DateTime>("CreatedAt", out DateTime at)) info.CreatedAt = at;
            }
            catch (Exception ex) { DiagnosticLog.Log(Log, "could not read the template's details: " + ex.Message); }
            return info;
        }

        /// <summary>
        /// Writes <paramref name="supervertaler"/> and, unless null, the Studio customer <paramref name="customer"/>
        /// ("" for none) into the template at <paramref name="path"/>. Nothing else in it changes. Null when done. UI thread.
        /// </summary>
        internal static string Edit(string path, ProjectProfileValues supervertaler, string customer) =>
            Run("editing a template", () =>
            {
                var provider = Provider(null);
                var template = FindTemplate(provider, path);
                if (template == null) return NotRegistered(path);
                WithBackup(path, template, () =>
                {
                    WriteSupervertaler(template, supervertaler, customer, customer != null);
                    StudioCustomers.Call(template, "Save");
                });
                DiagnosticLog.Log(Log, "template " + path + " edited: " + supervertaler?.Describe() + (customer != null ? "; customer '" + customer + "'" : ""));
                return null;
            });

        static string NotRegistered(string path) =>
            "\"" + Path.GetFileNameWithoutExtension(path) + "\" is not in this Studio's list of project templates, so Supervertaler cannot change it " +
            "here without Studio overwriting the change later. Add it to this Studio first (Projects view > Project Templates > Import), then try again. Nothing was changed.";

        /// <summary>Supervertaler's group and, when asked, the customer, into the template's settings, handed back to Studio.</summary>
        static void WriteSupervertaler(object template, ProjectProfileValues values, string customer, bool setCustomer, TemplateInfo keep = null)
        {
            var bundle = StudioCustomers.Get(template, "Settings") as ISettingsBundle
                         ?? throw new StudioCustomers.BridgeException("the template's Settings is not a settings bundle");
            if (values != null)
                ProjectBundleSettings.WriteValues(bundle, new ProjectProfileValues
                {
                    MemoryBankName = values.MemoryBankName, TranslatePromptPath = values.TranslatePromptPath,
                    ProofreadPromptPath = values.ProofreadPromptPath, ClientName = values.ClientName,
                    // A template is known by its file name: a project's record of the template it came from must not
                    // travel into another template.
                    ProfileName = "",
                    Termbases = values.Termbases, DisabledMultiTerm = values.DisabledMultiTerm, AiEnabledMultiTerm = values.AiEnabledMultiTerm,
                });
            if (setCustomer)
            {
                var g = bundle.GetSettingsGroup("GeneralProjectInfoSettings");
                g.GetSetting<string>("CustomerId", "").Value = customer ?? "";
            }
            if (keep != null)
            {
                var g = bundle.GetSettingsGroup("GeneralProjectInfoSettings");
                if (keep.CreatedBy != null) g.GetSetting<string>("CreatedBy", "").Value = keep.CreatedBy;
                if (keep.Description != null) g.GetSetting<string>("Description", "").Value = keep.Description;
                if (keep.CreatedAt != null) g.GetSetting<DateTime>("CreatedAt", DateTime.MinValue).Value = keep.CreatedAt.Value;
            }
            // Studio's own way: the bundle is set back (ImportSettingsBundle + save to its list), then the template is saved.
            StudioCustomers.Set(template, "Settings", bundle);
        }

        /// <summary>Runs <paramref name="change"/> with the template file backed up; on any failure puts it back and throws.</summary>
        static void WithBackup(string path, object template, Action change)
        {
            string backup = Path.Combine(Path.GetTempPath(), "Supervertaler template backup " + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sdltpl");
            File.Copy(path, backup, false);
            bool keep = false;
            try { change(); }
            catch
            {
                try
                {
                    File.Copy(backup, path, true);
                    try { StudioCustomers.Call(template, "DiscardCachedData"); } catch { }
                    DiagnosticLog.WriteAlways(Log, "a change to " + path + " failed; the file was put back as it was");
                }
                catch (Exception restore)
                {
                    keep = true;
                    DiagnosticLog.WriteAlways(Log, "putting " + path + " back failed (" + restore.Message + "); the backup is " + backup);
                    throw new IOException("The template could not be changed, and putting it back as it was failed too. A copy of the template " +
                                          "as it was is here: " + backup);
                }
                throw;
            }
            finally
            {
                if (!keep) try { File.Delete(backup); } catch { }
            }
        }

        /// <summary>Every failure becomes a message for the user, logged; nothing throws past here.</summary>
        static string Run(string what, Func<string> body)
        {
            if (!OnUiThread) return "Studio's project templates can only be changed from Studio's own window thread. Nothing was changed.";
            try { return body(); }
            catch (StudioCustomers.BridgeException ex)
            {
                DiagnosticLog.WriteAlways(Log, what + ": " + ex.Message);
                return Unreachable;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("No Studio project", StringComparison.Ordinal))
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, what + " failed: " + ex);
                return "Studio could not save the template: " + ex.Message + (ex is IOException ? "" : " The template was left as it was.");
            }
        }
    }
}
