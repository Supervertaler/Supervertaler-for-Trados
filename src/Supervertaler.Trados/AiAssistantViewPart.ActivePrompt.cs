using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados
{
    /// <summary>
    /// The MCP tools get_active_prompt and set_active_prompt (bridge
    /// /v1/active-prompt and /v1/set-active-prompt).
    ///
    /// <para><b>Why through the pane.</b> The prompt a run sends is held in three
    /// places that must agree: the global AI settings (in memory, and the mirror
    /// of the open project's choice - FollowProjectPrompt copies it in on every
    /// project switch, SaveCurrentProjectSettings copies it back out), the
    /// project's settings file, and the Batch Operations list, whose selection is
    /// what a run actually reads. A pick in that list writes the first two
    /// through <see cref="BatchPromptChoice.Remember"/> and rebuilds the list
    /// (<see cref="OnBatchPromptPicked"/>). The setter here does the same three
    /// things, on the UI thread, and reads the file back before it reports
    /// success. Writing only the file would be undone at the next project switch.</para>
    ///
    /// <para><b>Both places, like the panel.</b> set_active_prompt changes the
    /// global choice as well as the project's, as a pick in the list does. The
    /// global value is not an independent default here: while a project is open it
    /// IS that project's choice in memory, and leaving it on the old prompt would
    /// leave the list, Alt+T and the next project switch on the old prompt too.
    /// A project with no choice of its own does not inherit it: on opening, such a
    /// project gets the shipped default (FollowProjectPrompt, #135).</para>
    /// </summary>
    public partial class AiAssistantViewPart
    {
        private const string NoDocumentOpen = "No document is open in the Trados editor.";

        /// <summary>Bridge: GET /v1/active-prompt. UI thread.</summary>
        private BridgeActivePromptResponse BridgeGetActivePrompt()
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeActivePromptResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeActivePromptResponse)ctrl.Invoke(new Func<BridgeActivePromptResponse>(BridgeGetActivePrompt));
            // The panel may have no handle yet (never opened this session), in
            // which case InvokeRequired lies - see Core/UiThread.
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeGetActivePrompt());

            if (_activeDocument == null)
                return new BridgeActivePromptResponse { Ok = true, Available = false, Note = NoDocumentOpen };

            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            var project = PromptProjectBlock(projectPath);
            var prompts = FreshPrompts();
            var ps = LoadProjectSettingsQuietly(projectPath);

            return new BridgeActivePromptResponse
            {
                Ok = true,
                Available = true,
                Project = project,
                Translate = DescribeStored(ps, false, prompts),
                Proofread = DescribeStored(ps, true, prompts),
            };
        }

        /// <summary>
        /// Bridge: POST /v1/set-active-prompt. UI thread. Refuses, and changes
        /// nothing, for: no document open, a bad kind, the Settings dialog open,
        /// an unknown path, a memoQ-only prompt, a prompt of the other mode, a
        /// project settings file that cannot be read. Reports success only after
        /// the project's file reads back with the new path.
        /// </summary>
        private BridgeSetActivePromptResponse BridgeSetActivePrompt(BridgeSetActivePromptRequest req)
        {
            var ctrl = _control?.Value;
            if (ctrl == null || ctrl.IsDisposed)
                return new BridgeSetActivePromptResponse { Ok = false, Error = "ai assistant disposed" };
            if (ctrl.InvokeRequired)
                return (BridgeSetActivePromptResponse)ctrl.Invoke(
                    new Func<BridgeSetActivePromptResponse>(() => BridgeSetActivePrompt(req)));
            if (UiThread.InvokeRequired && UiThread.IsAvailable)
                return UiThread.Invoke(() => BridgeSetActivePrompt(req));

            req = req ?? new BridgeSetActivePromptRequest();
            if (_activeDocument == null)
                return new BridgeSetActivePromptResponse
                {
                    Ok = false,
                    Error = NoDocumentOpen + " The active prompt belongs to a project: open one of its documents first."
                };

            if (!ActivePrompt.TryParseKind(req.Kind, out var proofread))
                return new BridgeSetActivePromptResponse
                {
                    Ok = false,
                    Error = "'kind' must be \"translate\" (the default) or \"proofread\"; nothing was changed."
                };
            var kind = proofread ? ActivePrompt.KindProofread : ActivePrompt.KindTranslate;

            var projectPath = TermLensEditorViewPart.GetCurrentProjectPath();
            var projectName = TermLensEditorViewPart.GetCurrentProjectName();
            var project = PromptProjectBlock(projectPath);
            if (string.IsNullOrEmpty(projectPath))
                return new BridgeSetActivePromptResponse
                {
                    Ok = false, Kind = kind, Project = project,
                    Error = "The open document's project could not be identified, so there is nowhere to record "
                          + "the prompt; nothing was changed."
                };

            // The Settings dialog keeps its own copy of the active prompts and puts
            // it back on OK, which would silently undo this change.
            if (Application.OpenForms.OfType<TermLensSettingsForm>().Any())
                return new BridgeSetActivePromptResponse
                {
                    Ok = false, Kind = kind, Project = project,
                    Error = "Supervertaler's Settings window is open in Studio. It holds its own copy of the active "
                          + "prompt and would put that back when closed with OK. Ask the user to close it, then "
                          + "try again; nothing was changed."
                };

            var prompts = FreshPrompts();
            var prompt = ActivePrompt.Validate(req.Path, proofread, prompts, out var error, out var near);
            if (prompt == null)
                return new BridgeSetActivePromptResponse
                {
                    Ok = false, Kind = kind, Project = project, Error = error,
                    NearMatches = near != null && near.Count > 0 ? near : null
                };

            var before = DescribeStored(LoadProjectSettingsQuietly(projectPath), proofread, prompts);

            // Null when the file exists but cannot be read: saving over it would
            // replace the project's real settings with defaults.
            var ps = TermLensEditorViewPart.LoadOrCreateProjectSettings(projectPath, projectName);
            if (ps == null)
                return new BridgeSetActivePromptResponse
                {
                    Ok = false, Kind = kind, Project = project, Previous = before,
                    Error = "This project's Supervertaler settings file could not be read, so it was left alone; "
                          + "nothing was changed. The diagnostic log has the details."
                };

            var newPath = prompt.RelativePath;
            var previousGlobal = _settings?.AiSettings?.GetBatchPrompt(proofread) ?? "";
            try
            {
                SettingsService.Update(s =>
                {
                    if (s.AiSettings == null) s.AiSettings = new AiSettings();
                    BatchPromptChoice.Remember(s.AiSettings, null, proofread, newPath);
                });
                BatchPromptChoice.Remember(null, ps, proofread, newPath);
                ProjectSettings.Save(projectPath, ps);
            }
            catch (Exception ex)
            {
                RestoreGlobalPrompt(proofread, previousGlobal);
                try { DiagnosticLog.Log("BatchPrompt", "set_active_prompt could not store '" + newPath + "': " + ex.Message); } catch { }
                return new BridgeSetActivePromptResponse
                {
                    Ok = false, Kind = kind, Project = project, Previous = before,
                    Error = "The prompt could not be stored: " + ex.Message + ". Nothing was changed."
                };
            }

            // The list shows, and the next run sends, what is stored: rebuilt now,
            // as after a pick (synchronously: this is the UI thread).
            PopulateBatchPromptDropdown();

            // Not reported as done until the file says so. ProjectSettings.Save
            // logs rather than throws on some failures.
            var readBack = LoadProjectSettingsQuietly(projectPath)?.GetBatchPrompt(proofread);
            if (!string.Equals(readBack, newPath, StringComparison.Ordinal))
            {
                RestoreGlobalPrompt(proofread, previousGlobal);
                PopulateBatchPromptDropdown();
                try { DiagnosticLog.Log("BatchPrompt", "set_active_prompt: project file reads back '" + readBack + "', not '" + newPath + "'"); } catch { }
                return new BridgeSetActivePromptResponse
                {
                    Ok = false, Kind = kind, Project = project, Previous = before,
                    Error = "The project's settings file did not take the new prompt (it reads back \""
                          + (readBack ?? "(unreadable)") + "\"). The Studio-wide choice was put back; "
                          + "the diagnostic log has the details."
                };
            }

            try { DiagnosticLog.Log("BatchPrompt", "set_active_prompt (MCP): " + kind + " prompt for "
                      + (projectName ?? "?") + " '" + before.Path + "' -> '" + newPath + "'"); } catch { }

            var after = DescribeStored(LoadProjectSettingsQuietly(projectPath), proofread, prompts);
            var mode = proofread ? "Proofread" : "Batch Translate";
            // Marker-tolerant (#100): a choice stored before the [Trados] marker is
            // the same prompt as the marked path stored now.
            var unchanged = PromptPaths.Match(before.Path, after.Path);
            return new BridgeSetActivePromptResponse
            {
                Ok = true,
                Kind = kind,
                Project = project,
                Previous = before,
                Active = after,
                Note = unchanged
                    ? "\"" + after.Name + "\" was already the active " + kind + " prompt; nothing changed."
                    : "The next " + mode + " run in this project sends \"" + after.Name + "\" instead of \""
                      + before.Name + "\". Tell the user which prompt is now active and which it replaced. "
                      + UndoHint(before, proofread)
            };
        }

        /// <summary>How the caller can put the previous choice back.</summary>
        private static string UndoHint(BridgeActivePromptSlot before, bool proofread)
        {
            var kindArg = proofread ? " and kind \"proofread\"" : "";
            if (before.Path == BatchPromptChoice.BuiltInOnly)
                return "Before, no library prompt was used on purpose; only the user can choose that again, "
                     + "as the first item under Batch Operations > Prompt.";
            if (before.Path.Length == 0)
                return before.EffectivePath != null
                    ? "Before, no prompt was chosen for this project, so the shipped default ran; to get the same "
                      + "result back, call set_active_prompt with path \"" + before.EffectivePath + "\"" + kindArg + "."
                    : "Before, no prompt was chosen and no library prompt ran.";
            if (!before.Exists)
                return "The previous prompt is no longer in the library, so it cannot be set again.";
            return "To undo, call set_active_prompt with path \"" + before.Path + "\"" + kindArg + ".";
        }

        /// <summary>The library as it is on disk now: a prompt saved a moment ago
        /// by save_prompt or Workbench must be found, and a deleted one not.</summary>
        private List<PromptTemplate> FreshPrompts()
        {
            var lib = _promptLibrary ?? new PromptLibrary();
            lib.Refresh();
            return lib.GetAllPrompts();
        }

        private static ProjectSettings LoadProjectSettingsQuietly(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            try { return ProjectSettings.Load(projectPath); }
            catch { return null; }
        }

        /// <summary>One mode's stored choice and what it means, read the way the
        /// Batch list reads it (<see cref="PopulateBatchPromptDropdown"/>).</summary>
        private BridgeActivePromptSlot DescribeStored(ProjectSettings ps, bool proofread,
                                                      List<PromptTemplate> prompts)
        {
            BatchPromptChoice.Stored(_settings?.AiSettings, ps, proofread, out var selected, out var active);
            return ActivePrompt.Describe(selected, active != null ? "project" : "global", proofread, prompts);
        }

        private static BridgePromptProject PromptProjectBlock(string projectPath) => new BridgePromptProject
        {
            Name = TermLensEditorViewPart.GetCurrentProjectName(),
            StudioVersion = SupervertalerBridge.ThisStudioVersion,
            SettingsKey = ProjectSettings.GetProjectKey(projectPath),
        };

        /// <summary>Failure path: the global choice goes back to what it was, so
        /// memory, the project's file and the list keep agreeing.</summary>
        private static void RestoreGlobalPrompt(bool proofread, string previous)
        {
            try
            {
                SettingsService.Update(s => s.AiSettings?.SetBatchPrompt(proofread, previous));
            }
            catch (Exception ex)
            {
                try { DiagnosticLog.Log("BatchPrompt", "set_active_prompt could not restore the global choice: " + ex.Message); } catch { }
            }
        }

        /// <summary>For get_active_project: the prompt each mode's next run sends,
        /// by relativePath, or the Batch list's first item when none. Never throws.</summary>
        private void FillActivePrompts(BridgeProjectSnapshot snapshot)
        {
            try
            {
                var prompts = _promptLibrary?.GetAllPrompts();
                var ps = LoadProjectSettingsQuietly(TermLensEditorViewPart.GetCurrentProjectPath());
                var t = DescribeStored(ps, false, prompts);
                var p = DescribeStored(ps, true, prompts);
                snapshot.ActiveTranslatePrompt = t.EffectivePath ?? t.Name;
                snapshot.ActiveProofreadPrompt = p.EffectivePath ?? p.Name;
            }
            catch { /* orientation call: a prompt problem must not cost the rest */ }
        }
    }
}
