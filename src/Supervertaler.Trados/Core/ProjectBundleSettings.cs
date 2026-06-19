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
        /// Newline-joined absolute paths of MultiTerm (.sdltb/.ttb) termbases that are
        /// explicitly enabled for AI context — readable when a template is inspected by
        /// hand. A termbase's identity (its synthetic ID, used everywhere else) is a stable
        /// hash of this path, so a template and the projects created from it — which attach
        /// the same termbase path — resolve to the same termbase. For backwards
        /// compatibility the reader also accepts bare synthetic IDs, which is what builds
        /// up to 4.20.65 wrote here (the setting key is unchanged for that reason).
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
        private static readonly char[] Separators = { '\n', '\r' };

        /// <summary>
        /// Reads the AI-enabled MultiTerm termbases stored in the project's settings bundle
        /// (inherited from its template, if it was created from one) and returns them as
        /// synthetic IDs — the form the rest of the plugin uses. Each stored entry is either
        /// an absolute termbase path (current format) or a bare synthetic ID (pre-4.20.66
        /// format); both are accepted. Returns an empty list if absent or on any failure.
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
                    var entry = part.Trim();
                    if (entry.Length == 0) continue;
                    // A bare integer is a legacy synthetic ID; anything else is a path.
                    long id = long.TryParse(entry, out var parsed)
                        ? parsed
                        : MultiTermProjectDetector.MakeStableSyntheticId(entry);
                    if (!ids.Contains(id)) ids.Add(id);
                }
                if (ids.Count > 0)
                    DiagnosticLog.Log("ProjectBundle",
                        $"Read {ids.Count} AI-enabled MultiTerm termbase(s) from the project settings bundle (template-inherited).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("ProjectBundle", "ReadEnabledMultiTermIds failed: " + ex.Message);
            }
            return ids;
        }

        /// <summary>
        /// Mirrors the AI-enabled MultiTerm termbase paths into the project's settings
        /// bundle and persists it, so the choice can be baked into a project template and
        /// inherited by future projects. Stores absolute paths (readable in template XML);
        /// the synthetic ID used elsewhere is a hash of the same path. Writes (and dirties
        /// the .sdlproj) only when the value actually changes. Never throws.
        /// </summary>
        public static void WriteEnabledMultiTermPaths(FileBasedProject project, IEnumerable<string> paths)
        {
            try
            {
                if (project == null) return;
                var list = (paths ?? Enumerable.Empty<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var newValue = string.Join("\n", list);

                var bundle = project.GetSettings();
                if (bundle == null) return;
                var group = bundle.GetSettingsGroup<SupervertalerProjectSettingsGroup>();
                var setting = group.AiEnabledMultiTermIds;
                var current = setting.Value ?? "";
                if (string.Equals(current, newValue, StringComparison.Ordinal))
                {
                    DiagnosticLog.Log("ProjectBundle",
                        $"Project bundle already holds {list.Count} AI-enabled MultiTerm termbase(s) — no change written.");
                    return; // no change — don't churn the .sdlproj
                }

                setting.Value = newValue;
                project.UpdateSettings(bundle);
                DiagnosticLog.Log("ProjectBundle",
                    $"Wrote {list.Count} AI-enabled MultiTerm termbase path(s) to the project settings bundle.");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("ProjectBundle", "WriteEnabledMultiTermPaths failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the AI-enabled MultiTerm termbase paths to the bundle of the project
        /// currently open in Trados. Call this at the moment the user changes the "AI"
        /// ticks — the value is then freshest and the right project is unambiguous.
        /// Best-effort; never throws.
        /// </summary>
        public static void WriteForCurrentProject(IEnumerable<string> paths)
        {
            try
            {
                var list = (paths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                var project = ResolveCurrentProject();
                if (project == null)
                {
                    DiagnosticLog.Log("ProjectBundle",
                        $"WriteForCurrentProject: no project open in Trados — {list.Count} AI termbase(s) not mirrored to a bundle.");
                    return;
                }
                string name = "";
                try { name = project.GetProjectInfo()?.Name ?? ""; } catch { }
                DiagnosticLog.Log("ProjectBundle",
                    $"WriteForCurrentProject: project='{name}', {list.Count} AI-enabled MultiTerm termbase(s) to mirror.");
                WriteEnabledMultiTermPaths(project, list);
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
