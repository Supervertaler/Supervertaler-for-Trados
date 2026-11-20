using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// "Apply job profile", Studio side: reads what the dialog needs from the project and writes the plan
    /// (<see cref="JobProfile"/>) into it through Studio's public API. UI thread, except <see cref="Prepare"/>, which
    /// only reads files.
    ///
    /// <para><b>Order and failures.</b> Supervertaler's set-up, then termbases, TMs and the customer, then one save. Each
    /// part is done or reported on its own: one that fails never stops the others, and none is hidden. TMs and
    /// termbases: keep, add (Michael, 19 Nov) - the project's stay as they are, the template's it lacks are added;
    /// nothing is removed. A TM list Studio refuses is put back as it was; when the refusal is over a server TM, the list is written again without the server
    /// TMs. A project from a package or GroupShare gets Supervertaler's part in Supervertaler's own record only, and its
    /// Studio TMs, termbases and customer are left alone (they are the agency's).</para>
    /// </summary>
    internal static class JobProfileStudio
    {
        const string Log = "JobProfile";

        /// <summary>What the plan needs to know about the project. UI thread.</summary>
        internal static ProjectFacts Facts(FileBasedProject project)
        {
            var info = project.GetProjectInfo();
            var f = new ProjectFacts
            {
                Name = info.Name ?? "",
                Path = project.FilePath ?? "",
                Source = info.SourceLanguage?.IsoAbbreviation ?? "",
                Targets = (info.TargetLanguages ?? new Language[0]).Where(l => l != null).Select(l => l.IsoAbbreviation).ToList(),
                FromElsewhere = TmFieldStamping.IsFromElsewhere(info.ProjectType),
            };
            try
            {
                var tbc = project.GetTermbaseConfiguration();
                f.TermbaseIndexes = (tbc?.LanguageIndexes ?? new List<TermbaseLanguageIndex>())
                    .Where(i => i?.ProjectLanguage != null && !string.IsNullOrEmpty(i.TermbaseIndex))
                    .Select(i => new KeyValuePair<string, string>(i.ProjectLanguage.IsoAbbreviation, i.TermbaseIndex)).ToList();
            }
            catch (Exception ex) { DiagnosticLog.Log(Log, "could not read the project's termbase indexes: " + ex.Message); }
            ReadLists(project, f);
            f.CustomerName = StudioCustomers.ProjectCustomer(project, out _) ?? "";
            f.CustomerNames = StudioCustomers.List(project, out _)?.Select(c => c.Name).ToList();
            return f;
        }

        /// <summary>Reads the template and plans it for <paramref name="facts"/>. Any thread: file reads only.</summary>
        internal static string Prepare(string templatePath, ProjectFacts facts, out PreparedProfile prepared)
        {
            var error = JobProfile.Prepare(templatePath, facts, File.Exists, JobProfile.ReadBundleWithStudio, out prepared);
            if (error == null)
            {
                JobProfile.FindReversed(prepared, facts, TmFieldStamping.DirectionOf);
                prepared.Contents = JobProfile.ContentRows(prepared.Template, File.Exists, out prepared.ContentNotes);
            }
            return error;
        }

        /// <summary>
        /// Applies <paramref name="p"/> to <paramref name="project"/> and returns the summary for the user.
        /// <paramref name="addCustomer"/>: the user said yes to adding the template's customer to Studio's list. UI thread.
        /// </summary>
        internal static string Apply(FileBasedProject project, PreparedProfile p, ProjectFacts facts, bool addCustomer)
        {
            var steps = new List<ApplyStep>();
            var t = p.Template;

            // 1. Supervertaler's set-up
            if (t.Supervertaler == null)
                steps.Add(new ApplyStep(JobProfile.StepSupervertaler, StepOutcome.Unchanged, "the template has none"));
            else
            {
                var ps = TermLensEditorViewPart.LoadOrCreateProjectSettings(facts.Path, facts.Name, "project template");
                steps.Add(ps == null
                    ? new ApplyStep(JobProfile.StepSupervertaler, StepOutcome.Failed, "Supervertaler's settings file for this project could not be read, so it was left alone")
                    : ProjectProfileStore.ApplyProfile(project, ps, t.Supervertaler, t.ProfileName));
            }

            if (facts.FromElsewhere)
            {
                steps.Add(new ApplyStep("Studio's TMs, termbases and customer", StepOutcome.Unchanged, "the project came from a package or GroupShare"));
            }
            else
            {
                steps.Add(ApplyTermbases(project, p.Termbases));
                steps.Add(ApplyTms(project, p.Tms, facts));
                steps.Add(ApplyCustomer(project, p.Customer, addCustomer));
            }

            // One save for all of it.
            try { project.Save(); }
            catch (Exception ex)
            {
                steps.Add(new ApplyStep(JobProfile.StepSave, StepOutcome.Failed, ex.Message + " (the changes are in Studio, which saves the project again when it next saves it)"));
                DiagnosticLog.WriteAlways(Log, "saving the project after applying a project template failed: " + ex.Message);
            }

            // The panels follow: TermLens re-reads the project's set-up, TM field stamping looks at the new TMs.
            try { TermLensEditorViewPart.RefreshAfterSettingsChanged(); } catch (Exception ex) { DiagnosticLog.Log(Log, "refresh after applying: " + ex.Message); }
            try { TmFieldStampMonitor.Run("project template"); } catch { }

            var summary = JobProfile.Summary(t.ProfileName, facts.Name, steps);
            DiagnosticLog.Log(Log, summary.Replace("\r\n", " | "));
            return summary;
        }

        static TemplateProvider Provider(TranslationProviderReference r) =>
            new TemplateProvider { Uri = r?.Uri?.OriginalString ?? "", State = r?.State, Enabled = r?.Enabled ?? true };

        static TmListFacts ListFacts(TranslationProviderConfiguration c) => new TmListFacts
        {
            Override = c?.OverrideParent ?? false,
            Providers = (c?.Entries ?? new List<TranslationProviderCascadeEntry>())
                .Where(e => e?.MainTranslationProvider != null).Select(e => Provider(e.MainTranslationProvider)).ToList(),
        };

        static string TermbaseKeyOf(Termbase tb) =>
            tb is LocalTermbase l ? JobProfile.TermbaseKey(true, l.FilePath) : JobProfile.TermbaseKey(false, tb?.Name);

        /// <summary>The project's TM lists and termbases now, for the plan. UI thread.</summary>
        static void ReadLists(FileBasedProject project, ProjectFacts f)
        {
            try
            {
                f.TmsAllPairs = ListFacts(project.GetTranslationProviderConfiguration());
                foreach (var lang in project.GetProjectInfo().TargetLanguages ?? new Language[0])
                    f.TmsPerTarget[lang.IsoAbbreviation] = ListFacts(project.GetTranslationProviderConfiguration(lang));
            }
            catch (Exception ex) { DiagnosticLog.Log(Log, "could not read the project's TMs: " + ex.Message); }
            try
            {
                var tbc = project.GetTermbaseConfiguration();
                f.Termbases = (tbc?.Termbases ?? new List<Termbase>()).Where(tb => tb != null)
                    .Select(tb => new KeyValuePair<string, string>(
                        !string.IsNullOrEmpty(tb.Name) ? tb.Name : Path.GetFileNameWithoutExtension((tb as LocalTermbase)?.FilePath ?? ""),
                        TermbaseKeyOf(tb))).ToList();
            }
            catch (Exception ex) { DiagnosticLog.Log(Log, "could not read the project's termbases: " + ex.Message); }
        }

        /// <summary>Keep, add: the project's termbases, its default, indexes and options stay; the template's it lacks are added.</summary>
        static ApplyStep ApplyTermbases(FileBasedProject project, TermbasePlan plan)
        {
            const string what = JobProfile.StepTermbases;
            if (plan.LeaveAlone) return new ApplyStep(what, StepOutcome.Unchanged, plan.Notes.LastOrDefault() ?? "");
            TermbaseConfiguration current = null;
            try
            {
                current = project.GetTermbaseConfiguration();
                var termbases = (current?.Termbases ?? new List<Termbase>()).Where(tb => tb != null).ToList();
                var have = new HashSet<string>(termbases.Select(TermbaseKeyOf), StringComparer.Ordinal);
                var added = new List<string>();
                foreach (var tb in plan.Add)
                {
                    if (!have.Add(JobProfile.TermbaseKey(true, tb.Path))) continue;   // there since the preview
                    termbases.Add(new LocalTermbase(tb.Path, tb.Name.Length > 0 ? tb.Name : null, tb.SettingsXml.Length > 0 ? tb.SettingsXml : null, tb.FilterName, tb.Enabled));
                    added.Add(tb.Name.Length > 0 ? tb.Name : Path.GetFileNameWithoutExtension(tb.Path));
                }
                if (added.Count == 0) return new ApplyStep(what, StepOutcome.Unchanged, "the project already has the template's termbases");
                var indexes = (current?.LanguageIndexes ?? new List<TermbaseLanguageIndex>()).Where(i => i != null).ToList();
                var skipped = new List<string>();
                foreach (var m in plan.AddIndexes)
                {
                    if (indexes.Any(i => string.Equals(i.ProjectLanguage?.IsoAbbreviation, m.Key, StringComparison.OrdinalIgnoreCase))) continue;
                    Language lang = null;
                    try { lang = new Language(m.Key); } catch { }
                    if (lang == null || !lang.IsValid) { skipped.Add(m.Key); continue; }
                    indexes.Add(new TermbaseLanguageIndex(lang, m.Value));
                }
                project.UpdateTermbaseConfiguration(new TermbaseConfiguration
                {
                    Termbases = termbases,
                    LanguageIndexes = indexes,
                    TermRecognitionOptions = current?.TermRecognitionOptions ?? new TermRecognitionOptions { MinimumMatchValue = 70, SearchDepth = 200 },
                });
                var detail = "added " + string.Join(", ", added);
                var notes = plan.Notes.ToList();
                if (skipped.Count > 0) notes.Add("language(s) Studio does not know left out of the termbase indexes: " + string.Join(", ", skipped));
                if (notes.Count > 0) detail += " (" + string.Join(" ", notes) + ")";
                return new ApplyStep(what, StepOutcome.Done, detail);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "adding termbases to the project failed: " + ex.Message);
                // Studio checks every local termbase in the list, the project's own too.
                var gone = (current?.Termbases ?? new List<Termbase>()).OfType<LocalTermbase>()
                    .Where(l => !string.IsNullOrEmpty(l.FilePath) && !File.Exists(l.FilePath)).Select(l => l.FilePath).ToList();
                return new ApplyStep(what, StepOutcome.Failed, "Studio did not accept them (" + ex.Message + ")"
                    + (gone.Count > 0 ? "; a termbase already in the project is not there: " + string.Join(", ", gone) : "")
                    + "; the project's termbases are as they were");
            }
        }

        /// <summary>Keep, add: every list keeps its TMs, order and switches; the template's it lacks go at the end.</summary>
        static ApplyStep ApplyTms(FileBasedProject project, TmPlan plan, ProjectFacts facts)
        {
            const string what = JobProfile.StepTms;
            if (plan.LeaveAlone) return new ApplyStep(what, StepOutcome.Unchanged, plan.Notes.LastOrDefault() ?? "");

            var targets = project.GetProjectInfo().TargetLanguages ?? new Language[0];
            Language LanguageOf(string code) => targets.FirstOrDefault(l => string.Equals(l.IsoAbbreviation, code, StringComparison.OrdinalIgnoreCase));

            // What the project has now: the base of every new list, and what is put back if Studio refuses one.
            TranslationProviderConfiguration before;
            var beforePerLanguage = new List<(Language lang, TranslationProviderConfiguration config)>();
            try
            {
                before = project.GetTranslationProviderConfiguration();
                foreach (var s in plan.PerTarget)
                {
                    var lang = LanguageOf(s.Target);
                    if (lang != null) beforePerLanguage.Add((lang, project.GetTranslationProviderConfiguration(lang)));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "reading the project's TMs failed: " + ex.Message);
                return new ApplyStep(what, StepOutcome.Failed, "the project's current TMs could not be read (" + ex.Message + "), so nothing was added");
            }

            var addedNames = new List<string>();
            void Write(bool withServerTms)
            {
                addedNames.Clear();
                var allPairs = Extend(before.Entries, plan.AllPairs.Add, withServerTms, addedNames);
                project.UpdateTranslationProviderConfiguration(new TranslationProviderConfiguration(allPairs, before.StopSearchingWhenResultsFound, before.OverrideParent));
                foreach (var s in plan.PerTarget)
                {
                    var b = beforePerLanguage.FirstOrDefault(x => string.Equals(x.lang.IsoAbbreviation, s.Target, StringComparison.OrdinalIgnoreCase));
                    if (b.lang == null) continue;
                    if (s.MakeOwnList)
                        project.UpdateTranslationProviderConfiguration(b.lang, new TranslationProviderConfiguration(
                            Extend(allPairs, s.Add, withServerTms, addedNames), before.StopSearchingWhenResultsFound, true));
                    else
                        project.UpdateTranslationProviderConfiguration(b.lang, new TranslationProviderConfiguration(
                            Extend(b.config.Entries, s.Add, withServerTms, addedNames), b.config.StopSearchingWhenResultsFound, b.config.OverrideParent));
                }
            }

            string Detail() => "added " + (addedNames.Count == 0 ? "none" : string.Join(", ", addedNames.Distinct(StringComparer.OrdinalIgnoreCase)));

            var notes = plan.Notes.Where(n => !n.Contains("sign in")).ToList();
            try
            {
                Write(true);
                return new ApplyStep(what, StepOutcome.Done, Detail() + (notes.Count > 0 ? " (" + string.Join(" ", notes) + ")" : ""));
            }
            catch (Exception first) when (plan.HasServerTms)
            {
                DiagnosticLog.WriteAlways(Log, "adding TMs with server TMs was refused, trying without them: " + first.Message);
                try
                {
                    Write(false);
                    notes.Add("Studio refused the server TMs (" + first.Message + "); add them in Studio's Project Settings once you can sign in.");
                    return new ApplyStep(what, StepOutcome.Done, Detail() + " (" + string.Join(" ", notes) + ")");
                }
                catch (Exception second) { return PutBack(project, before, beforePerLanguage, second); }
            }
            catch (Exception ex) { return PutBack(project, before, beforePerLanguage, ex); }
        }

        /// <summary>The list as it is, plus the template's entries it does not have yet, at the end.</summary>
        static List<TranslationProviderCascadeEntry> Extend(IEnumerable<TranslationProviderCascadeEntry> existing, List<TemplateTmEntry> add,
            bool withServerTms, List<string> addedNames)
        {
            var list = (existing ?? Enumerable.Empty<TranslationProviderCascadeEntry>()).Where(e => e != null).ToList();
            var have = new HashSet<string>(list.Where(e => e.MainTranslationProvider != null).Select(e => JobProfile.TmKey(Provider(e.MainTranslationProvider))), StringComparer.Ordinal);
            TranslationProviderReference Ref(TemplateProvider p) => new TranslationProviderReference(new Uri(p.Uri), p.State, p.Enabled);
            foreach (var e in add.Where(e => withServerTms || !e.Main.IsServerTm))
            {
                if (!have.Add(JobProfile.TmKey(e.Main))) continue;   // there since the preview
                list.Add(new TranslationProviderCascadeEntry(Ref(e.Main),
                    e.ProjectTms.Where(x => withServerTms || !x.IsServerTm).Select(Ref).ToList(), e.Update, e.Lookup, e.Concordance, e.Penalty));
                addedNames.Add(e.Main.DisplayName);
            }
            return list;
        }

        /// <summary>Studio refused the new lists: the old ones go back, so a half-written list never stays behind.</summary>
        static ApplyStep PutBack(FileBasedProject project, TranslationProviderConfiguration before,
            List<(Language lang, TranslationProviderConfiguration config)> beforePerLanguage, Exception why)
        {
            DiagnosticLog.WriteAlways(Log, "setting the project's TMs failed: " + why.Message);
            try
            {
                project.UpdateTranslationProviderConfiguration(before);
                foreach (var x in beforePerLanguage) project.UpdateTranslationProviderConfiguration(x.lang, x.config);
                return new ApplyStep(JobProfile.StepTms, StepOutcome.Failed, "Studio did not accept the template's TMs (" + why.Message + "); the project's TMs are as they were");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "putting the project's TMs back failed: " + ex.Message);
                return new ApplyStep(JobProfile.StepTms, StepOutcome.Failed, "Studio did not accept the template's TMs (" + why.Message
                    + ") and the old list could not be put back (" + ex.Message + "); check them in Studio's Project Settings > Translation Memory and Automated Translation");
            }
        }

        static ApplyStep ApplyCustomer(FileBasedProject project, CustomerDecision d, bool addCustomer)
        {
            const string what = JobProfile.StepCustomer;
            switch (d.Choice)
            {
                case CustomerChoice.LeaveAlone: return new ApplyStep(what, StepOutcome.Unchanged, "the template names none");
                case CustomerChoice.AlreadySet: return new ApplyStep(what, StepOutcome.Unchanged, "\"" + d.Name + "\" already");
                case CustomerChoice.CannotRead: return new ApplyStep(what, StepOutcome.Failed, "Studio's customer list could not be read here, so the project's customer was left alone");
                case CustomerChoice.AskToAdd when !addCustomer:
                    return new ApplyStep(what, StepOutcome.Unchanged, "\"" + d.Name + "\" is not in Studio's customer list and was not added");
            }
            bool added = d.Choice == CustomerChoice.AskToAdd;
            if (StudioCustomers.SetProjectCustomer(project, d.Name, added, out var error))
                return new ApplyStep(what, StepOutcome.Done, "\"" + d.Name + "\"" + (added ? " (added to Studio's customer list)" : ""));
            return new ApplyStep(what, StepOutcome.Failed, error);
        }
    }
}
