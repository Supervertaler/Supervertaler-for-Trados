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
    /// part is done or reported on its own: one that fails never stops the others, and none is hidden. A TM list Studio
    /// refuses is put back as it was; when the refusal is over a server TM, the list is written again without the server
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
            f.CustomerName = StudioCustomers.ProjectCustomer(project, out _) ?? "";
            f.CustomerNames = StudioCustomers.List(project, out _)?.Select(c => c.Name).ToList();
            return f;
        }

        /// <summary>Reads the template and plans it for <paramref name="facts"/>. Any thread: file reads only.</summary>
        internal static string Prepare(string templatePath, ProjectFacts facts, out PreparedProfile prepared) =>
            JobProfile.Prepare(templatePath, facts, File.Exists, JobProfile.ReadBundleWithStudio, out prepared);

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
                var ps = TermLensEditorViewPart.LoadOrCreateProjectSettings(facts.Path, facts.Name, "job profile");
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
                DiagnosticLog.WriteAlways(Log, "saving the project after applying a job profile failed: " + ex.Message);
            }

            // The panels follow: TermLens re-reads the project's set-up, TM field stamping looks at the new TMs.
            try { TermLensEditorViewPart.RefreshAfterSettingsChanged(); } catch (Exception ex) { DiagnosticLog.Log(Log, "refresh after applying: " + ex.Message); }
            try { TmFieldStampMonitor.Run("job profile"); } catch { }

            var summary = JobProfile.Summary(t.ProfileName, facts.Name, steps);
            DiagnosticLog.Log(Log, summary.Replace("\r\n", " | "));
            return summary;
        }

        static ApplyStep ApplyTermbases(FileBasedProject project, TermbasePlan plan)
        {
            const string what = JobProfile.StepTermbases;
            if (plan.LeaveAlone) return new ApplyStep(what, StepOutcome.Unchanged, plan.Notes.LastOrDefault() ?? "");
            try
            {
                var current = project.GetTermbaseConfiguration();
                var options = current?.TermRecognitionOptions ?? new TermRecognitionOptions { MinimumMatchValue = 70, SearchDepth = 200 };
                if (plan.Recognition != null)
                {
                    options = new TermRecognitionOptions
                    {
                        MinimumMatchValue = plan.Recognition.MinimumMatchValue,
                        SearchDepth = plan.Recognition.SearchDepth,
                        ShowWithNoAvailableTranslation = plan.Recognition.ShowWithNoAvailableTranslation,
                        SearchOrder = Enum.TryParse(plan.Recognition.SearchOrder, true, out TermbaseSearchOrder order) ? order : options.SearchOrder,
                    };
                }
                var config = new TermbaseConfiguration
                {
                    Termbases = plan.Termbases.Select(tb => (Termbase)new LocalTermbase(tb.Path,
                        tb.Name.Length > 0 ? tb.Name : null, tb.SettingsXml.Length > 0 ? tb.SettingsXml : null, tb.FilterName, tb.Enabled)).ToList(),
                    LanguageIndexes = new List<TermbaseLanguageIndex>(),
                    TermRecognitionOptions = options,
                };
                var skipped = new List<string>();
                foreach (var m in plan.Indexes)
                {
                    Language lang = null;
                    try { lang = new Language(m.Key); } catch { }
                    if (lang == null || !lang.IsValid) { skipped.Add(m.Key); continue; }
                    config.LanguageIndexes.Add(new TermbaseLanguageIndex(lang, m.Value));
                }
                project.UpdateTermbaseConfiguration(config);
                var detail = string.Join(", ", plan.Termbases.Select(tb => tb.Name.Length > 0 ? tb.Name : Path.GetFileNameWithoutExtension(tb.Path)));
                var notes = plan.Notes.ToList();
                if (skipped.Count > 0) notes.Add("language(s) Studio does not know left out of the termbase indexes: " + string.Join(", ", skipped));
                if (notes.Count > 0) detail += " (" + string.Join(" ", notes) + ")";
                return new ApplyStep(what, StepOutcome.Done, detail);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "setting the project's termbases failed: " + ex.Message);
                return new ApplyStep(what, StepOutcome.Failed, "Studio did not accept them (" + ex.Message + "); the project's termbases are as they were");
            }
        }

        static ApplyStep ApplyTms(FileBasedProject project, TmPlan plan, ProjectFacts facts)
        {
            const string what = JobProfile.StepTms;
            if (plan.LeaveAlone) return new ApplyStep(what, StepOutcome.Unchanged, plan.Notes.LastOrDefault() ?? "");

            var info = project.GetProjectInfo();
            var languages = plan.PerTarget
                .Select(kv => (kv, lang: (info.TargetLanguages ?? new Language[0]).FirstOrDefault(l => string.Equals(l.IsoAbbreviation, kv.Key, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.lang != null).ToList();

            // What the project has now, to put back if Studio refuses part of the new lists.
            TranslationProviderConfiguration before;
            var beforePerLanguage = new List<(Language lang, TranslationProviderConfiguration config)>();
            try
            {
                before = project.GetTranslationProviderConfiguration();
                foreach (var x in languages) beforePerLanguage.Add((x.lang, project.GetTranslationProviderConfiguration(x.lang)));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteAlways(Log, "reading the project's TMs failed: " + ex.Message);
                return new ApplyStep(what, StepOutcome.Failed, "the project's current TMs could not be read (" + ex.Message + "), so they were left alone");
            }

            void Write(bool withServerTms)
            {
                project.UpdateTranslationProviderConfiguration(ToConfig(plan.AllPairs, withServerTms));
                foreach (var x in languages) project.UpdateTranslationProviderConfiguration(x.lang, ToConfig(x.kv.Value, withServerTms));
            }

            string Describe()
            {
                var names = plan.AllPairs.Entries.Concat(plan.PerTarget.SelectMany(kv => kv.Value.Entries))
                    .Select(e => e.Main.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return names.Count > 0 ? string.Join(", ", names) : "none";
            }

            var notes = plan.Notes.Where(n => !n.Contains("sign in")).ToList();
            try
            {
                Write(true);
                return new ApplyStep(what, StepOutcome.Done, Describe() + (notes.Count > 0 ? " (" + string.Join(" ", notes) + ")" : ""));
            }
            catch (Exception first) when (plan.HasServerTms)
            {
                DiagnosticLog.WriteAlways(Log, "the TM lists with server TMs were refused, trying without them: " + first.Message);
                try
                {
                    Write(false);
                    notes.Add("Studio refused the server TMs (" + first.Message + "); add them in Studio's Project Settings once you can sign in.");
                    return new ApplyStep(what, StepOutcome.Done, Describe() + " without the server TMs (" + string.Join(" ", notes) + ")");
                }
                catch (Exception second) { return PutBack(project, before, beforePerLanguage, second); }
            }
            catch (Exception ex) { return PutBack(project, before, beforePerLanguage, ex); }
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

        static TranslationProviderConfiguration ToConfig(TemplateTmList list, bool withServerTms)
        {
            TranslationProviderReference Ref(TemplateProvider p) => new TranslationProviderReference(new Uri(p.Uri), p.State, p.Enabled);
            var entries = list.Entries.Where(e => withServerTms || !e.Main.IsServerTm).Select(e => new TranslationProviderCascadeEntry(
                Ref(e.Main), e.ProjectTms.Where(x => withServerTms || !x.IsServerTm).Select(Ref).ToList(),
                e.Update, e.Lookup, e.Concordance, e.Penalty)).ToList();
            return new TranslationProviderConfiguration(entries, list.StopWhenFound, list.OverrideParent);
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
