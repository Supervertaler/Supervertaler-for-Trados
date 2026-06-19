using System;
using System.Collections.Generic;
using System.Linq;
using Sdl.Core.Settings;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Supervertaler per-project settings that are meant to INHERIT from a Trados
    /// project template. Stored in the Trados project settings bundle
    /// (<c>IProject.GetSettings()/UpdateSettings()</c>), NOT in Supervertaler's own
    /// per-project sidecar JSON.
    ///
    /// The difference is the whole point: the sidecar is keyed by a hash of the
    /// .sdlproj path and lives in Supervertaler's data folder, so it cannot ride a
    /// project template. The Trados settings bundle, by contrast, is captured by
    /// "Create Project Template based on this project" and copied into every
    /// project created from that template — exactly like Trados's own settings and
    /// other plugins' settings groups (a project template inspected during this work
    /// already carried a "MemoQ v 1.0.0.0" group). So a value written here is set
    /// once on the template and inherited by every project spun up from it, which is
    /// what issue #36 (Marco's CLI-automation workflow) asks for.
    /// </summary>
    public class SupervertalerProjectSettingsGroup : SettingsGroup
    {
        /// <summary>
        /// Newline-joined synthetic IDs of MultiTerm (.sdltb/.ttb) termbases that are
        /// explicitly enabled for AI context. The synthetic ID is a stable hash of the
        /// termbase file path (see <see cref="MultiTermProjectDetector"/>), so a template
        /// and the projects created from it — which attach the same termbase paths —
        /// resolve to the same IDs without any path translation at inherit time.
        /// </summary>
        public Setting<string> AiEnabledMultiTermIds => GetSetting<string>("AiEnabledMultiTermIds");
    }

    /// <summary>
    /// Read/write helpers for <see cref="SupervertalerProjectSettingsGroup"/>. All
    /// methods are best-effort and never throw — terminology/settings access must
    /// never be allowed to disrupt the editor.
    /// </summary>
    public static class ProjectBundleSettings
    {
        private static readonly char[] Separators = { '\n', '\r', ',' };

        /// <summary>
        /// Reads the AI-enabled MultiTerm synthetic IDs stored in the project's settings
        /// bundle (inherited from its template, if it was created from one). Returns an
        /// empty list if absent or on any failure.
        /// </summary>
        public static List<long> ReadEnabledMultiTermIds(FileBasedProject project)
        {
            var ids = new List<long>();
            try
            {
                if (project == null) return ids;
                var bundle = project.GetSettings();
                if (bundle == null) return ids;
                var group = bundle.GetSettingsGroup<SupervertalerProjectSettingsGroup>();
                var raw = group?.AiEnabledMultiTermIds?.Value ?? "";
                foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(part.Trim(), out var id) && !ids.Contains(id))
                        ids.Add(id);
                }
                if (ids.Count > 0)
                    DiagnosticLog.Log("ProjectBundle",
                        $"Read {ids.Count} AI-enabled MultiTerm id(s) from the project settings bundle (template-inherited).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("ProjectBundle", "ReadEnabledMultiTermIds failed: " + ex.Message);
            }
            return ids;
        }

        /// <summary>
        /// Mirrors the AI-enabled MultiTerm synthetic IDs into the project's settings
        /// bundle and persists it, so the choice can be baked into a project template
        /// and inherited by future projects. Writes (and dirties the .sdlproj) only when
        /// the value actually changes. Never throws.
        /// </summary>
        public static void WriteEnabledMultiTermIds(FileBasedProject project, IEnumerable<long> ids)
        {
            try
            {
                if (project == null) return;
                var list = (ids ?? Enumerable.Empty<long>()).Distinct().OrderBy(x => x).ToList();
                var newValue = string.Join("\n", list);

                var bundle = project.GetSettings();
                if (bundle == null) return;
                var group = bundle.GetSettingsGroup<SupervertalerProjectSettingsGroup>();
                var setting = group.AiEnabledMultiTermIds;
                var current = setting.Value ?? "";
                if (string.Equals(current, newValue, StringComparison.Ordinal))
                {
                    DiagnosticLog.Log("ProjectBundle",
                        $"Project bundle already holds {list.Count} AI-enabled MultiTerm id(s) — no change written.");
                    return; // no change — don't churn the .sdlproj
                }

                setting.Value = newValue;
                project.UpdateSettings(bundle);
                DiagnosticLog.Log("ProjectBundle",
                    $"Wrote {list.Count} AI-enabled MultiTerm id(s) to the project settings bundle.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("ProjectBundle", "WriteEnabledMultiTermIds failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the AI-enabled MultiTerm synthetic IDs to the bundle of the project
        /// currently open in Trados. Call this at the moment the user changes the "AI"
        /// ticks — the value is then freshest and the right project is unambiguous.
        /// Best-effort; never throws.
        /// </summary>
        public static void WriteForCurrentProject(IEnumerable<long> ids)
        {
            try
            {
                var list = (ids ?? Enumerable.Empty<long>()).ToList();
                var project = ResolveCurrentProject();
                if (project == null)
                {
                    DiagnosticLog.Log("ProjectBundle",
                        $"WriteForCurrentProject: no project open in Trados — {list.Count} AI id(s) not mirrored to a bundle.");
                    return;
                }
                string name = "";
                try { name = project.GetProjectInfo()?.Name ?? ""; } catch { }
                DiagnosticLog.Log("ProjectBundle",
                    $"WriteForCurrentProject: project='{name}', {list.Count} AI-enabled MultiTerm id(s) to mirror.");
                WriteEnabledMultiTermIds(project, list);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("ProjectBundle", "WriteForCurrentProject failed: " + ex.Message);
            }
        }

        private static FileBasedProject ResolveCurrentProject()
        {
            try { return SdlTradosStudio.Application?.GetController<ProjectsController>()?.CurrentProject; }
            catch { return null; }
        }
    }
}
