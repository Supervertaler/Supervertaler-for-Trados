using System;
using System.Collections.Generic;
using Sdl.Core.Settings;
using Sdl.ProjectAutomation.FileBased;

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
    ///
    /// <para>Since 20.209 (client profiles, step 1) the project's whole Supervertaler
    /// set-up lives here as well as in the sidecar: memory bank, prompts, client,
    /// termbase choices and a profile name. <see cref="ProjectProfileStore"/> reads and
    /// writes it; <see cref="ProjectProfile"/> holds the rules. An empty value means
    /// "nothing here": the sidecar decides, as before.</para>
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

        /// <summary>
        /// For a project created from a package (or a GroupShare project): stamp its name and client into its Studio
        /// TMs anyway. Off unless switched on for this project (Michael, 11 Nov): such TMs are usually the agency's, and
        /// a private project name must not reach them unasked. Kept in the project, so it goes with it.
        /// </summary>
        public Setting<bool> StampStudioTmsInImportedProject => GetSetting<bool>("StampStudioTmsInImportedProject");

        /// <summary>The project's SuperMemory bank, by name (banks are folders in the shared data folder).</summary>
        public Setting<string> MemoryBankName => GetSetting<string>("MemoryBankName");

        /// <summary>The translate prompt, as its path inside the prompt library (as the sidecar stores it).</summary>
        public Setting<string> TranslatePromptPath => GetSetting<string>("TranslatePromptPath");

        /// <summary>The proofread prompt, as its path inside the prompt library.</summary>
        public Setting<string> ProofreadPromptPath => GetSetting<string>("ProofreadPromptPath");

        /// <summary>The client (end customer) recorded for the project, for the usage ledger.</summary>
        public Setting<string> ClientName => GetSetting<string>("ClientName");

        /// <summary>
        /// The client profile this set-up came from. Empty until a later step writes it; kept here now so that it
        /// already travels template → project → template.
        /// </summary>
        public Setting<string> ProfileName => GetSetting<string>("ProfileName");

        /// <summary>
        /// Supervertaler (SQLite) termbases with at least one choice switched on, one per line:
        /// <c>flags TAB name TAB id TAB created</c>. Flags: R read, W write, P project, A sent to the AI. The name is
        /// the identity (unique in a termbase database); id and creation time only recognise a termbase renamed in
        /// the same database. A termbase not listed is off. See <see cref="ProjectProfile"/>.
        /// </summary>
        public Setting<string> SupervertalerTermbases => GetSetting<string>("SupervertalerTermbases");

        /// <summary>MultiTerm termbases switched off in Supervertaler for this project: paths (or legacy synthetic ids), one per line.</summary>
        public Setting<string> DisabledMultiTermPaths => GetSetting<string>("DisabledMultiTermPaths");
    }

    /// <summary>
    /// Read/write helpers for <see cref="SupervertalerProjectSettingsGroup"/>. All
    /// methods are best-effort and never throw — terminology/settings access must
    /// never be allowed to disrupt the editor.
    /// </summary>
    public static class ProjectBundleSettings
    {
        /// <summary>
        /// Reads the AI-enabled MultiTerm termbases stored in the project's settings bundle
        /// (inherited from its template, if it was created from one) and returns them as
        /// synthetic IDs — the form the rest of the plugin uses. Each stored entry is either
        /// an absolute termbase path (current format) or a bare synthetic ID (pre-4.20.66
        /// format); both are accepted. Returns an empty list if absent or on any failure.
        /// </summary>
        public static List<long> ReadEnabledMultiTermIds(FileBasedProject project)
        {
            try
            {
                if (project == null) return new List<long>();
                var ids = ProjectProfile.ParseMultiTermIds(ReadValues(project.GetSettings())?.AiEnabledMultiTerm);
                if (ids.Count > 0)
                    DiagnosticLog.Log("ProjectBundle",
                        $"Read {ids.Count} AI-enabled MultiTerm termbase(s) from the project settings bundle (template-inherited).");
                return ids;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("ProjectBundle", "ReadEnabledMultiTermIds failed: " + ex.Message);
                return new List<long>();
            }
        }

        /// <summary>
        /// Supervertaler's values in <paramref name="bundle"/>, as stored ("" where there is none). Null only when
        /// the bundle itself is null or cannot be read. Works on any settings bundle, a project's or a template's,
        /// in Studio or outside it.
        /// </summary>
        public static ProjectProfileValues ReadValues(ISettingsBundle bundle)
        {
            if (bundle == null) return null;
            var g = bundle.GetSettingsGroup<SupervertalerProjectSettingsGroup>();
            if (g == null) return null;
            return new ProjectProfileValues
            {
                MemoryBankName = Str(g.MemoryBankName),
                TranslatePromptPath = Str(g.TranslatePromptPath),
                ProofreadPromptPath = Str(g.ProofreadPromptPath),
                ClientName = Str(g.ClientName),
                ProfileName = Str(g.ProfileName),
                Termbases = Str(g.SupervertalerTermbases),
                DisabledMultiTerm = Str(g.DisabledMultiTermPaths),
                AiEnabledMultiTerm = Str(g.AiEnabledMultiTermIds),
            };
        }

        /// <summary>
        /// Writes <paramref name="values"/> into <paramref name="bundle"/>, touching only the settings whose value
        /// differs. True when anything changed (the caller then hands the bundle back to Studio and saves the
        /// project); false when the bundle already held these values.
        /// </summary>
        public static bool WriteValues(ISettingsBundle bundle, ProjectProfileValues values)
        {
            if (bundle == null || values == null) return false;
            var g = bundle.GetSettingsGroup<SupervertalerProjectSettingsGroup>();
            bool changed = false;
            changed |= Set(g.MemoryBankName, values.MemoryBankName);
            changed |= Set(g.TranslatePromptPath, values.TranslatePromptPath);
            changed |= Set(g.ProofreadPromptPath, values.ProofreadPromptPath);
            changed |= Set(g.ClientName, values.ClientName);
            changed |= Set(g.ProfileName, values.ProfileName);
            changed |= Set(g.SupervertalerTermbases, values.Termbases);
            changed |= Set(g.DisabledMultiTermPaths, values.DisabledMultiTerm);
            changed |= Set(g.AiEnabledMultiTermIds, values.AiEnabledMultiTerm);
            return changed;
        }

        private static string Str(Setting<string> s)
        {
            try { return s?.Value ?? ""; }
            catch { return ""; }
        }

        private static bool Set(Setting<string> s, string value)
        {
            value = value ?? "";
            if (string.Equals(s.Value ?? "", value, StringComparison.Ordinal)) return false;
            s.Value = value;
            return true;
        }
    }
}
