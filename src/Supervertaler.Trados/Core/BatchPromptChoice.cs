using System;
using System.Collections.Generic;
using System.Linq;
using Supervertaler.Core;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// What a stored batch-prompt choice means, in one place: the Batch
    /// Operations dropdown, the run, Preview prompt, Translate active segment and
    /// the Prompt Manager's "active" tick all go through <see cref="Effective"/>.
    ///
    /// <para>The choice is stored as a prompt's relative path (AiSettings, and
    /// per project in ProjectSettings), and "" meant two things at once: never
    /// chosen, and "(None)" chosen on purpose. Both ran on the built-in
    /// instructions alone, so nearly every Proofread skipped the Default
    /// Proofreading Prompt - its default-to-OK framing, its citation discipline
    /// and its rule against proposing terminology without evidence - and the
    /// dropdown said "(None – default)" as though that were the default prompt.
    /// Seen in the live test of 16 Oct 2026, where such a run raised a
    /// speculative terminology finding of exactly the kind that prompt
    /// suppresses. Translate had the same gap with the Default Translation
    /// Prompt, which Michael chose to close the same way.</para>
    ///
    /// <para>Now a choice never made means the mode's shipped default prompt,
    /// and the deliberate choice of the built-in instructions alone is stored as
    /// <see cref="BuiltInOnly"/>.</para>
    /// </summary>
    internal static class BatchPromptChoice
    {
        /// <summary>
        /// Stored when the translator chooses "(Built-in instructions only)". A
        /// '*' cannot appear in a file name, so this can never name a prompt; a
        /// build that predates it finds no such prompt and sends none, which is
        /// what it means.
        /// </summary>
        public const string BuiltInOnly = "*built-in*";

        /// <summary>The shipped prompts (core PromptLibrary's default
        /// definitions), used when no prompt was ever chosen for the mode.</summary>
        public const string DefaultTranslationPromptName = "Default Translation Prompt";
        public const string DefaultProofreadingPromptName = "Default Proofreading Prompt";

        /// <summary>The mode's shipped default prompt's relative path in
        /// <paramref name="prompts"/>, or null when the library has none: the
        /// shipped copy (IsDefault), not a clone that kept its name.</summary>
        public static string DefaultPath(IEnumerable<PromptTemplate> prompts, bool proofread)
        {
            var name = proofread ? DefaultProofreadingPromptName : DefaultTranslationPromptName;
            var category = proofread ? "Proofread" : "Translate";
            return prompts?
                .FirstOrDefault(p => p != null && p.IsDefault
                    && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                    && Controls.BatchTranslateControl.CategoryBelongsTo(p.Category, category))?
                .RelativePath;
        }

        /// <summary>
        /// The library prompt a run in this mode uses for <paramref name="stored"/>,
        /// or null for the built-in instructions alone.
        /// </summary>
        public static string Effective(string stored, bool proofread, IEnumerable<PromptTemplate> prompts)
        {
            if (stored == BuiltInOnly) return null;
            if (!string.IsNullOrEmpty(stored)) return stored;
            return DefaultPath(prompts, proofread);
        }

        /// <summary>
        /// A prompt picked in the Batch Operations list becomes the mode's active
        /// prompt: globally and, with a project open, for that project - the same
        /// two places the Prompt Manager's "Set as active" writes. Before this the
        /// pick was used for the run but stored nowhere, so every rebuild of the
        /// list (Settings OK or Cancel, Translate/Proofread, another document)
        /// put the stored prompt or the default back (reported by a user).
        /// <paramref name="path"/> is <see cref="BuiltInOnly"/> for the first item.
        /// </summary>
        public static void Remember(AiSettings ai, ProjectSettings project, bool proofread, string path)
        {
            path = path ?? "";
            ai?.SetBatchPrompt(proofread, path);
            if (project == null) return;
            if (proofread) project.ActiveProofreadPromptPath = path;
            else project.ActivePromptPath = path;
        }

        /// <summary>
        /// What the Batch list shows for the stored state: the project's prompt
        /// for the mode when it has one (also the ticked, "active" one), else the
        /// global choice. Every rebuild of the list goes through here.
        /// </summary>
        public static void Stored(AiSettings ai, ProjectSettings project, bool proofread,
                                  out string selected, out string active)
        {
            selected = ai?.GetBatchPrompt(proofread) ?? "";
            active = null;
            var own = project?.GetBatchPrompt(proofread);
            if (!string.IsNullOrEmpty(own))
            {
                active = own;
                selected = own;
            }
        }
    }
}
