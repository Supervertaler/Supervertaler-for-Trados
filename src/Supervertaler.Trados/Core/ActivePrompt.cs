using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// The rules behind the MCP tools get_active_prompt and set_active_prompt,
    /// kept free of Studio so they can be tested outside it
    /// (tools\active_prompt_test.ps1). The pane does the storing and the UI
    /// refresh (AiAssistantViewPart.ActivePrompt.cs); this decides what a stored
    /// choice means and which prompts may be made active.
    ///
    /// <para>What a stored choice means is <see cref="BatchPromptChoice"/>'s
    /// business, not this class's: "" is the mode's shipped default prompt,
    /// <see cref="BatchPromptChoice.BuiltInOnly"/> is no library prompt, and a
    /// path that no longer resolves sends no library prompt either
    /// (AiAssistantViewPart.PromptContentFor).</para>
    /// </summary>
    internal static class ActivePrompt
    {
        public const string KindTranslate = "translate";
        public const string KindProofread = "proofread";

        /// <summary>
        /// What one mode's stored choice means for the next run.
        /// <paramref name="source"/> says where the choice came from ("project"
        /// or "global"), as <see cref="BatchPromptChoice.Stored"/> decided it.
        /// </summary>
        public static BridgeActivePromptSlot Describe(string stored, string source, bool proofread,
                                                      IList<PromptTemplate> prompts)
        {
            stored = stored ?? "";
            var slot = new BridgeActivePromptSlot { Path = stored, Source = source };
            var builtInName = proofread
                ? Controls.BatchTranslateControl.BuiltInItemProofread
                : Controls.BatchTranslateControl.BuiltInItemTranslate;

            if (stored == BatchPromptChoice.BuiltInOnly)
            {
                slot.Exists = true;
                slot.Name = builtInName;
                slot.Note = "Chosen on purpose: no library prompt. A run uses the built-in instructions only.";
                return slot;
            }

            if (stored.Length == 0)
            {
                var fallback = BatchPromptChoice.DefaultPath(prompts, proofread);
                var def = Find(prompts, fallback);
                // About the prompt the choice stands for: "exists: false" next to
                // an empty path reads as "your prompt is missing", which it is not.
                slot.Exists = def != null;
                if (def != null)
                {
                    slot.EffectivePath = def.RelativePath;
                    slot.Name = def.Name;
                    slot.Note = "No prompt chosen for this mode, so a run uses the shipped default, \""
                              + def.Name + "\".";
                }
                else
                {
                    slot.Name = builtInName;
                    slot.Note = "No prompt chosen for this mode and the shipped default is not in the library, "
                              + "so a run uses the built-in instructions only.";
                }
                return slot;
            }

            var p = Find(prompts, stored);
            if (p == null)
            {
                slot.Exists = false;
                slot.Name = builtInName;
                slot.Note = "That prompt is no longer in the prompt library, so a run sends no library prompt "
                          + "(built-in instructions only). Choose another with set_active_prompt.";
                return slot;
            }

            slot.Exists = true;
            slot.EffectivePath = p.RelativePath;
            slot.Name = p.Name;
            return slot;
        }

        /// <summary>
        /// Reads a 'kind' argument. Null or empty is translate; anything other
        /// than the two words is an error rather than a guess.
        /// </summary>
        public static bool TryParseKind(string kind, out bool proofread)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            proofread = k == KindProofread;
            return k.Length == 0 || k == KindTranslate || k == KindProofread;
        }

        /// <summary>
        /// The library prompt <paramref name="path"/> names, if it may become the
        /// active prompt for the mode; otherwise null and why.
        ///
        /// <para>The mode rule is the Prompt Manager's "Set as active prompt for
        /// this project": a prompt in the Proofread folder is a proofreading
        /// prompt, any other is a translation prompt, and QuickLauncher prompts
        /// are never active. The Batch list follows it too: it lists the active
        /// prompt even when its folder is not the mode's.</para>
        /// </summary>
        public static PromptTemplate Validate(string path, bool proofread, IList<PromptTemplate> prompts,
                                              out string error, out List<string> nearMatches)
        {
            error = null;
            nearMatches = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Pass 'path': a relativePath from list_prompts.";
                return null;
            }

            var p = Find(prompts, path);
            if (p == null)
            {
                nearMatches = NearMatches(path, proofread, prompts);
                error = "No prompt at \"" + path + "\" in the Trados prompt library; nothing was changed. "
                      + (nearMatches.Count > 0
                          ? "Did you mean one of nearMatches? Pass its path exactly."
                          : "Call list_prompts and pass a relativePath from it.");
                return null;
            }

            if (string.Equals((p.App ?? "").Trim(), "memoq", StringComparison.OrdinalIgnoreCase))
            {
                error = "\"" + p.Name + "\" is marked for memoQ only (app: memoq), so Supervertaler for Trados "
                      + "cannot run it; nothing was changed.";
                return null;
            }

            if (p.IsQuickLauncher)
            {
                error = "\"" + p.Name + "\" is a QuickLauncher prompt (the editor's right-click menu), "
                      + "which cannot be the active prompt; nothing was changed.";
                return null;
            }

            var isProofreadPrompt = IsProofreadCategory(p);
            if (isProofreadPrompt != proofread)
            {
                error = isProofreadPrompt
                    ? "\"" + p.Name + "\" is a proofreading prompt (Proofread folder); pass kind \"proofread\" "
                      + "to make it the active proofreading prompt. Nothing was changed."
                    : "\"" + p.Name + "\" is not in the Proofread folder, so it is a translation prompt; "
                      + "pass kind \"translate\" (or omit kind). Nothing was changed.";
                return null;
            }

            return p;
        }

        /// <summary>
        /// The sentence save_prompt's reply ends with: how the prompt just saved can
        /// be used, by the same rules as <see cref="Validate"/>, so the chat is never
        /// told to call set_active_prompt for a prompt it would refuse.
        /// </summary>
        public static string AfterSaveHint(PromptTemplate p)
        {
            if (p == null) return "";
            if (string.Equals((p.App ?? "").Trim(), "memoq", StringComparison.OrdinalIgnoreCase))
                return "It is marked for memoQ only, so Supervertaler for Trados cannot run it and it cannot be "
                     + "made the active prompt here.";
            if (p.IsQuickLauncher)
                return "It is a QuickLauncher prompt: it appears in the editor's right-click menu and is never "
                     + "the active prompt.";
            if (IsProofreadCategory(p))
                return "Saving does NOT make it the active proofreading prompt: the next Proofread run still uses "
                     + "the prompt that was active before. To use this one, call set_active_prompt with path \""
                     + p.RelativePath + "\" and kind \"proofread\" (after telling the user), or the user picks it "
                     + "under Batch Operations > Prompt in Proofread mode.";
            return "Saving does NOT make it the active prompt: the next Batch Translate run still uses the "
                 + "prompt that was active before. To use this one, call set_active_prompt with path \""
                 + p.RelativePath + "\" (after telling the user), or the user picks it under "
                 + "Batch Operations > Prompt.";
        }

        /// <summary>Whether a prompt belongs to the Proofread mode, by the same rule
        /// as the Prompt Manager and the Batch list.</summary>
        public static bool IsProofreadCategory(PromptTemplate p) =>
            p != null && Controls.BatchTranslateControl.CategoryBelongsTo(p.Category, "Proofread");

        /// <summary>
        /// Up to five prompts of the mode whose names come closest to the one
        /// asked for, for an error message. Ranked by edit distance between file
        /// names (marker and extension ignored), and only those close enough to
        /// be a plausible typo or a renamed copy. A library is tens to a few
        /// hundred prompts of short names, so a full scan is cheap.
        /// </summary>
        public static List<string> NearMatches(string path, bool proofread, IList<PromptTemplate> prompts)
        {
            var wanted = StemOf(path);
            if (wanted.Length == 0 || prompts == null) return new List<string>();

            return prompts
                .Where(p => p != null && !p.IsQuickLauncher && IsProofreadCategory(p) == proofread
                            && !string.Equals((p.App ?? "").Trim(), "memoq", StringComparison.OrdinalIgnoreCase))
                .Select(p =>
                {
                    var stem = StemOf(p.RelativePath);
                    var d = Distance(wanted, stem);
                    // A name that contains the other (a "v2", a client prefix) is
                    // close whatever its length difference.
                    if (stem.Contains(wanted) || wanted.Contains(stem)) d = Math.Min(d, 1);
                    return new { p.RelativePath, d, limit = Math.Max(3, Math.Max(wanted.Length, stem.Length) / 3) };
                })
                .Where(x => x.d <= x.limit)
                .OrderBy(x => x.d)
                .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(x => x.RelativePath)
                .ToList();
        }

        private static PromptTemplate Find(IList<PromptTemplate> prompts, string path)
        {
            if (prompts == null || string.IsNullOrWhiteSpace(path)) return null;
            return prompts.FirstOrDefault(p => p != null && string.Equals(p.RelativePath, path, StringComparison.OrdinalIgnoreCase))
                ?? prompts.FirstOrDefault(p => p != null && PromptPaths.Match(p.RelativePath, path));
        }

        private static string StemOf(string path)
        {
            var n = PromptPaths.Normalise(path);
            return n.Length == 0 ? "" : Path.GetFileNameWithoutExtension(n).Trim().ToLowerInvariant();
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
