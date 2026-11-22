using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Supervertaler.Trados.Settings;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// "Open example project", the part that needs Studio. Runs on a pool thread
    /// (a background job, like create_project). The order is chosen so that a
    /// failure leaves nothing half-made:
    /// <list type="number">
    /// <item>the Word document, written to a temporary folder;</item>
    /// <item>the project, through <see cref="ProjectCreatorStudio.Run"/>, which
    ///   removes everything it made if it fails; the TM is made inside the
    ///   project folder just before the project is saved, so the same clean-up
    ///   covers it, and a TM that fails is a warning, not a failure;</item>
    /// <item>the marker file, then the termbase and the project's Supervertaler
    ///   settings: a termbase that fails is removed again and is a warning;</item>
    /// <item>the project is opened in the Editor.</item>
    /// </list>
    /// The temporary folder is deleted whatever happens.
    /// </summary>
    internal static class ExampleProjectStudio
    {
        internal sealed class Result
        {
            public bool Ok;
            public string Error;
            public string ProjectFile;
            public string ProjectName;
            /// <summary>Things that are missing from the project, in plain words for the user.</summary>
            public List<string> Warnings = new List<string>();
            public bool TmAdded;
            public bool TermbaseAdded;
        }

        public const string DocumentFileName = "AquaLine AL-50 guide.docx";
        public const string TmFolderName = "TM";

        internal static Result Run(ExampleData data, ExampleDirection d, ExampleProject.FolderPlan folder,
            string projectsFolder, ICollection<string> registeredNames, Action<string> progress)
        {
            var result = new Result { ProjectName = folder.Name };
            var staging = Path.Combine(Path.GetTempPath(), "sv-example-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // 1. The document.
                progress?.Invoke("writing the document");
                Directory.CreateDirectory(staging);
                var docx = Path.Combine(staging, DocumentFileName);
                ExampleProject.WriteDocx(docx, data, d);

                // 2. The project, with the TM added just before it is saved.
                var plan = ProjectCreator.Plan(folder.Name, d.Source, new[] { d.Target }, new[] { docx },
                    null, projectsFolder, projectsFolder, registeredNames);
                if (plan.Errors.Count > 0)
                {
                    result.Error = "The example project could not be made: " + string.Join(" ", plan.Errors);
                    return result;
                }

                var units = ExampleProject.TmUnits(data, d);
                Func<FileBasedProject, string> addTm = null;
                if (units.Count > 0)
                    addTm = project =>
                    {
                        progress?.Invoke("making the translation memory");
                        var w = AddTm(project, plan.ProjectFolder, d, units);
                        if (w == null) result.TmAdded = true;
                        return w;
                    };

                var created = ProjectCreatorStudio.Run(plan, progress, null, addTm);
                if (!created.Ok)
                {
                    result.Error = created.Error;
                    return result;
                }
                result.ProjectFile = created.ProjectFile;
                foreach (var w in created.Warnings)
                    if (w != null && !w.StartsWith("Studio: ", StringComparison.Ordinal))
                        result.Warnings.Add("The example translation memory could not be added (" + ProjectCreator.Sentence(w, false) +
                                            "), so FuzzyFixer has no matches to repair in this project.");

                // 3. Marker, termbase, settings.
                ExampleProject.WriteMarker(plan.ProjectFolder, new ExampleMarker
                {
                    Source = d.Source, Target = d.Target, DataVersion = data.Version, Termbase = folder.Name
                });

                var terms = ExampleProject.TermPairs(data, d);
                if (terms.Count > 0)
                {
                    progress?.Invoke("making the termbase");
                    var why = AddTermbase(result.ProjectFile, folder.Name, d, terms);
                    if (why == null) result.TermbaseAdded = true;
                    else result.Warnings.Add("The example termbase could not be added (" + ProjectCreator.Sentence(why, false) +
                                             "), so TermLens shows no terms in this project.");
                }

                if (!d.Prepared)
                    result.Warnings.Add("There is no example translation memory or termbase for " +
                                        ExampleProject.EnglishName(d.IntoEnglish ? d.Source : d.Target) +
                                        " yet, so FuzzyFixer and TermLens have nothing to show in this project. Everything else works.");

                // 4. Open it in the Editor.
                var opened = ProjectOpenerStudio.Run(result.ProjectFile, true, new List<string>(), d.Target, progress);
                if (!opened.Ok)
                    result.Warnings.Add("The project was made but could not be opened: " + opened.Error +
                                        " Open it from the Projects view.");
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "The example project could not be made: " + ProjectCreator.Sentence(ex.Message, true);
                return result;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        // ─── The TM ─────────────────────────────────────────────────

        /// <summary>
        /// Makes <c>TM\Supervertaler example.sdltm</c> in the project folder, with
        /// the project's exact languages (a de-AT project gets a de-AT TM), adds
        /// the units and puts the TM in the project's TM list, lookup and update
        /// on. Returns null, or why it failed; a half-made TM file is deleted.
        /// </summary>
        internal static string AddTm(FileBasedProject project, string projectFolder, ExampleDirection d,
            List<KeyValuePair<string, string>> units)
        {
            var dir = Path.Combine(projectFolder, TmFolderName);
            var path = Path.Combine(dir, ExampleProject.NamePrefix + ".sdltm");
            try
            {
                Directory.CreateDirectory(dir);
                CreateTmFile(path, d.Source, d.Target, units);

                var config = project.GetTranslationProviderConfiguration();
                // Lookup, concordance and update on, no penalty: what Studio's own
                // "Add" of a file TM does.
                config.Entries.Add(new TranslationProviderCascadeEntry(path, true, true, true, 0));
                project.UpdateTranslationProviderConfiguration(config);
                return null;
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                return ex.Message;
            }
        }

        /// <summary>The TM file alone, without a project: also what the offline test builds.</summary>
        internal static void CreateTmFile(string path, string source, string target, List<KeyValuePair<string, string>> units)
        {
            var src = CultureInfo.GetCultureInfo(source);
            var tgt = CultureInfo.GetCultureInfo(target);
            var tm = new FileBasedTranslationMemory(path, "Supervertaler example: " + source + " to " + target,
                src, tgt,
                FuzzyIndexes.SourceWordBased | FuzzyIndexes.SourceCharacterBased |
                FuzzyIndexes.TargetWordBased | FuzzyIndexes.TargetCharacterBased,
                BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
            var tus = new List<TranslationUnit>();
            foreach (var u in units)
            {
                var s = new Segment(src); s.Add(u.Key);
                var t = new Segment(tgt); t.Add(u.Value);
                tus.Add(new TranslationUnit(s, t));
            }
            var results = tm.LanguageDirection.AddTranslationUnitsMasked(tus.ToArray(), new ImportSettings(), null);
            int added = results?.Count(r => r != null && r.Action != Sdl.LanguagePlatform.TranslationMemory.Action.Error) ?? 0;
            tm.Save();
            if (added < units.Count)
                throw new InvalidOperationException("only " + added + " of " + units.Count + " translation units went into the memory");
        }

        // ─── The termbase ───────────────────────────────────────────

        /// <summary>
        /// A Supervertaler termbase named like the project, in the user's own
        /// termbase database (made if there is none yet), filled, and set as this
        /// project's only termbase: on for TermLens and the AI in this project, off
        /// in every other one. An earlier termbase of the same name (from "Start
        /// afresh") is replaced. Returns null, or why it failed; a half-made
        /// termbase is deleted again.
        /// </summary>
        internal static string AddTermbase(string projectFile, string name, ExampleDirection d,
            List<KeyValuePair<string, string>> terms)
        {
            string db = null;
            long id = -1;
            // The database takes an empty term without complaint, so check first:
            // nothing is made for a list with a gap in it.
            var empty = terms.Count(t => string.IsNullOrWhiteSpace(t.Key) || string.IsNullOrWhiteSpace(t.Value));
            if (empty > 0) return empty + " of the terms are empty";
            try
            {
                db = DatabasePath(create: true);
                RemoveTermbase(db, name);
                id = TermbaseReader.CreateTermbase(db, name, d.Source, d.Target);
                if (id < 0) return "the termbase database refused a new termbase";
                int added = 0;
                foreach (var t in terms)
                    if (TermbaseReader.InsertTerm(db, id, t.Key, t.Value, d.Source, d.Target) > 0) added++;
                // InsertTerm declines some terms without throwing; a termbase
                // missing terms is not the example, so it counts as a failure.
                if (added < terms.Count)
                    throw new InvalidOperationException("only " + added + " of " + terms.Count + " terms went into the termbase");

                var others = OtherTermbaseIds(db, id);
                var ps = ProjectSettings.Load(projectFile) ?? new ProjectSettings();
                ps.ProjectPath = projectFile;
                ps.ProjectName = name;
                ps.TermbasePath = db;
                ps.ProjectTermbaseId = id;
                ps.WriteTermbaseIds = new List<long> { id };
                ps.DisabledTermbaseIds = others;
                ps.DisabledAiTermbaseIds = new List<long>(others);
                ps.AiTermbaseIdsInitialized = true;
                ProjectSettings.SaveFile(projectFile, ps);

                // Off everywhere else: in the global lists, so a project that has
                // never heard of this termbase does not inherit it.
                SettingsService.UpdateIf(s =>
                {
                    bool changed = NewTermbaseDefaults.ApplyTo(s, id);
                    if (s.DisabledTermbaseIds == null) s.DisabledTermbaseIds = new List<long>();
                    if (!s.DisabledTermbaseIds.Contains(id)) { s.DisabledTermbaseIds.Add(id); changed = true; }
                    return changed;
                });
                return null;
            }
            catch (Exception ex)
            {
                if (db != null && id >= 0)
                {
                    try { TermbaseReader.DeleteTermbase(db, id); } catch { }
                }
                return ex.Message;
            }
        }

        /// <summary>Deletes every termbase called exactly <paramref name="name"/>.</summary>
        internal static void RemoveTermbase(string db, string name)
        {
            if (db == null || !File.Exists(db)) return;
            List<TermbaseInfo> all;
            using (var r = new TermbaseReader(db))
            {
                if (!r.Open()) return;
                all = r.GetTermbases() ?? new List<TermbaseInfo>();
            }
            foreach (var t in all.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                TermbaseReader.DeleteTermbase(db, t.Id);
        }

        private static List<long> OtherTermbaseIds(string db, long id)
        {
            using (var r = new TermbaseReader(db))
            {
                if (!r.Open()) return new List<long>();
                return (r.GetTermbases() ?? new List<TermbaseInfo>()).Select(t => t.Id).Where(x => x != id).ToList();
            }
        }

        /// <summary>
        /// The user's termbase database, in the order TermLens looks for it: the
        /// path in Settings, the data folder, the two older default places. With
        /// <paramref name="create"/>, a new one is made in the data folder when
        /// there is none at all (a first run).
        /// </summary>
        internal static string DatabasePath(bool create)
        {
            var candidates = new List<string>();
            var set = SettingsService.Current?.TermbasePath;
            if (!string.IsNullOrWhiteSpace(set)) candidates.Add(set);
            var own = Path.Combine(UserDataPath.ResourcesDir, "supervertaler.db");
            candidates.Add(own);
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Supervertaler_Data", "resources", "supervertaler.db"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Supervertaler", "resources", "supervertaler.db"));
            var found = candidates.FirstOrDefault(File.Exists);
            if (found != null || !create) return found;
            Directory.CreateDirectory(Path.GetDirectoryName(own));
            TermbaseReader.CreateDatabase(own);
            return own;
        }

        // ─── "Start afresh" ─────────────────────────────────────────

        /// <summary>
        /// Removes an earlier example project: from Studio's Projects list, its
        /// files, its folder and its termbase. Refuses (returns why, removing
        /// nothing) while any of its files is open in the Editor. Must not run on
        /// the UI thread: the Studio calls are sent there.
        /// </summary>
        internal static string Remove(ExampleProject.FolderPlan existing, Action<string> progress)
        {
            var file = existing.ExistingProjectFile;
            var want = ProjectOpener.NormalPath(file);
            progress?.Invoke("removing the earlier example project");

            string refused = UiThread.Invoke(() =>
            {
                var editor = SdlTradosStudio.Application.GetController<EditorController>();
                foreach (var doc in editor.GetDocuments())
                {
                    try
                    {
                        if (doc.Project != null && ProjectOpener.NormalPath(doc.Project.FilePath) == want)
                            return "Close the example project's files in the Editor and try again.";
                    }
                    catch { }
                }
                var pc = SdlTradosStudio.Application.GetController<ProjectsController>();
                var listed = pc.GetAllProjects().FirstOrDefault(p =>
                {
                    try { return ProjectOpener.NormalPath(p.FilePath) == want; } catch { return false; }
                });
                if (listed != null)
                {
                    try { pc.Close(listed); }
                    catch (Exception ex) { return "Studio would not remove it from the Projects list (" + ex.Message + ")."; }
                }
                return null;
            });
            if (refused != null) return refused;

            var marker = ExampleProject.ReadMarker(existing.ExistingFolder);
            try { Directory.Delete(existing.ExistingFolder, true); }
            catch (Exception ex)
            {
                return "The old example project's folder could not be deleted (" + ex.Message +
                       "). Close anything that uses it and try again.";
            }
            try { RemoveTermbase(DatabasePath(create: false), marker?.Termbase ?? existing.ExistingName); } catch { }
            return null;
        }
    }
}
