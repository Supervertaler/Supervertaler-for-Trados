using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Supervertaler.Trados.Core
{
    /// <summary>One Supervertaler termbase in the template editor: its ticks, and whether this computer has it.</summary>
    public sealed class TemplateTermbaseRow
    {
        public string Name = "";
        /// <summary>This computer's termbase; null when the template names one this computer does not have.</summary>
        public TermbaseIdentity Local;
        public bool Read, Write, Project, Ai;
        /// <summary>For a termbase this computer does not have: the template's line, kept as it is.</summary>
        public string StoredLine;
        public bool Missing => Local == null;
        public bool Any => Read || Write || Project || Ai;
    }

    /// <summary>One entry of a prompt list in the template editor.</summary>
    public sealed class TemplatePromptItem
    {
        /// <summary>What is stored: a prompt's place in the library, <see cref="BatchPromptChoice.BuiltInOnly"/>, or "" for not set.</summary>
        public string Value = "";
        public string Label = "";
        public override string ToString() => Label;
    }

    /// <summary>
    /// Project templates, step 4: the rules of the window that edits the Supervertaler project settings stored in a
    /// Studio project template. Pure (no Studio types, no files), so tested outside Studio
    /// (tools\job_profile_step2_test.ps1, section O). Studio's own part of a template (TMs, termbases, file types)
    /// is edited in Studio, as before; the window says so.
    /// </summary>
    public static class ProjectTemplateEdit
    {
        public const string NotSet = "(not set: a project keeps its own)";
        public const string NotOnThisComputer = " (not on this computer)";

        /// <summary>
        /// Every Supervertaler termbase on this computer, alphabetical, with the ticks the template gives it; then the
        /// termbases the template names that this computer does not have, kept as they are (another computer may have
        /// them). A line that cannot be read is kept too, unchanged, so saving never loses what was there.
        /// </summary>
        public static List<TemplateTermbaseRow> TermbaseRows(string stored, IList<TermbaseIdentity> local)
        {
            local = local ?? new List<TermbaseIdentity>();
            var rows = JobProfile.SortByName(local, t => t.Name)
                .Select(t => new TemplateTermbaseRow { Name = t.Name, Local = t }).ToList();
            foreach (var c in ProjectProfile.ParseTermbases(stored))
            {
                var tb = ProjectProfile.Resolve(c, local);
                var row = tb == null ? null : rows.FirstOrDefault(r => ReferenceEquals(r.Local, tb));
                if (row == null)
                {
                    rows.Add(new TemplateTermbaseRow
                    {
                        Name = c.Parsed ? c.Name : c.Line.Trim(),
                        Read = c.Read, Write = c.Write, Project = c.Project, Ai = c.Ai,
                        StoredLine = c.Line,
                    });
                    continue;
                }
                row.Read |= c.Read; row.Write |= c.Write; row.Project |= c.Project; row.Ai |= c.Ai;
            }
            return rows;
        }

        /// <summary>
        /// The template's termbase value from the editor's rows: one line per termbase with a tick (this computer's
        /// in the stored form, the others as they were). Only one Project termbase: the first ticked one keeps it.
        /// Empty when nothing is ticked, which means "a project keeps its own termbase choices".
        /// </summary>
        public static string TermbaseText(IEnumerable<TemplateTermbaseRow> rows)
        {
            var lines = new List<string>();
            bool projectTaken = false;
            foreach (var r in rows ?? Enumerable.Empty<TemplateTermbaseRow>())
            {
                if (r == null) continue;
                bool project = r.Project && !projectTaken;
                projectTaken |= project;
                if (r.Missing)
                {
                    // Kept as the template had it; the editor shows such a row's ticks but does not change them.
                    if (!string.IsNullOrWhiteSpace(r.StoredLine)) lines.Add(r.StoredLine.Trim('\r', '\n'));
                    continue;
                }
                if (!(r.Read || r.Write || project || r.Ai)) continue;
                lines.Add(ProjectProfile.FormatTermbase(r.Local, r.Read, r.Write, project, r.Ai));
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// The entries of a prompt list: "not set", the mode's built-in-only entry, then the library's prompts for the
        /// mode (by their place in the library); and, if the template names a prompt this computer does not have, that
        /// one too, so saving keeps it. <paramref name="prompts"/> is (place in library, category) per prompt.
        /// </summary>
        public static List<TemplatePromptItem> PromptItems(IEnumerable<KeyValuePair<string, string>> prompts, bool proofread,
            Func<string, string, bool> belongsTo, string stored)
        {
            var items = new List<TemplatePromptItem>
            {
                new TemplatePromptItem { Value = "", Label = NotSet },
                new TemplatePromptItem { Value = BatchPromptChoice.BuiltInOnly, Label = proofread ? "(Built-in checks only)" : "(System Prompt only)" },
            };
            var category = proofread ? "Proofread" : "Translate";
            var mine = (prompts ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Key) && (belongsTo == null || belongsTo(p.Value ?? "", category)))
                .Select(p => p.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in JobProfile.SortByName(mine, x => x))
                items.Add(new TemplatePromptItem { Value = path, Label = Label(path) });
            var s = (stored ?? "").Trim();
            if (s.Length > 0 && !items.Any(i => string.Equals(i.Value, s, StringComparison.OrdinalIgnoreCase)))
                items.Add(new TemplatePromptItem { Value = s, Label = Label(s) + NotOnThisComputer });
            return items;
        }

        /// <summary>A prompt's place in the library for people: "Translate/Acme" for "Translate/Acme.md".</summary>
        public static string Label(string path)
        {
            var p = (path ?? "").Trim().Replace('\\', '/');
            return p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 3) : p;
        }

        /// <summary>The memory bank list: "not set", this computer's banks, and the template's own if it is not among them.</summary>
        public static List<TemplatePromptItem> BankItems(IEnumerable<string> banks, string stored)
        {
            var items = new List<TemplatePromptItem> { new TemplatePromptItem { Value = "", Label = NotSet } };
            foreach (var b in JobProfile.SortByName((banks ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal), x => x))
                items.Add(new TemplatePromptItem { Value = b, Label = b });
            var s = (stored ?? "").Trim();
            if (s.Length > 0 && !items.Any(i => string.Equals(i.Value, s, StringComparison.Ordinal)))
                items.Add(new TemplatePromptItem { Value = s, Label = s + NotOnThisComputer });
            return items;
        }

        /// <summary>
        /// The values to write into the template: what the editor shows, and the parts it does not edit (MultiTerm
        /// choices) as the template had them. The profile name is cleared, so the template is known by its file name.
        /// </summary>
        public static ProjectProfileValues Edited(ProjectProfileValues before, string bank, string translate, string proofread,
            string client, IEnumerable<TemplateTermbaseRow> termbases)
        {
            var b = before ?? new ProjectProfileValues();
            return new ProjectProfileValues
            {
                MemoryBankName = (bank ?? "").Trim(),
                TranslatePromptPath = (translate ?? "").Trim(),
                ProofreadPromptPath = (proofread ?? "").Trim(),
                ClientName = (client ?? "").Trim(),
                ProfileName = "",
                Termbases = TermbaseText(termbases),
                DisabledMultiTerm = b.DisabledMultiTerm ?? "",
                AiEnabledMultiTerm = b.AiEnabledMultiTerm ?? "",
            };
        }

        /// <summary>
        /// Null when <paramref name="path"/> can take a new template; otherwise why not, for the user. A new template
        /// never replaces a file: an existing template is updated with its own button, which keeps a backup.
        /// </summary>
        public static string CheckNewTemplatePath(string path, Func<string, bool> fileExists)
        {
            if (string.IsNullOrWhiteSpace(path)) return "No file name was given.";
            if (!path.EndsWith(".sdltpl", StringComparison.OrdinalIgnoreCase)) return "A Studio project template's file name ends in .sdltpl.";
            string name;
            try { name = Path.GetFileNameWithoutExtension(path); } catch (ArgumentException) { return "That is not a valid file name."; }
            if (string.IsNullOrWhiteSpace(name)) return "The template needs a name.";
            bool exists;
            try { exists = fileExists != null && fileExists(path); } catch { exists = false; }
            if (exists) return "There is a template with that name already: \"" + path + "\". To change it, select it in the list and use " +
                               "Update from project; to make another, choose a new name.";
            return null;
        }
    }
}
