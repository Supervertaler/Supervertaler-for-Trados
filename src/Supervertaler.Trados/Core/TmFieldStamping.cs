using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Supervertaler.Core.SuperVault;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// Stamps the Studio project's name and client into its Studio TMs, by filling the project's "field values on
    /// update" (Project Settings > Translation Memory > Update): Studio itself then adds them to every unit it puts in
    /// those TMs, at every confirm and in Update Main Translation Memories. Design: docs/design/tm-field-stamping.md.
    ///
    /// What Studio does with these values (read from Studio 2022, 2024 and 2026): it reads them afresh at every update,
    /// hands them to every TM, and each TM keeps only the fields it defines (<c>NewFields = Ignore</c>). So a value is
    /// filled only for a field one of the project's TMs defines, as Studio's own Field Values page lists them:
    ///  - the TMs with Update ticked and enabled, of every language pair (and a pair's own list where it has one);
    ///  - a field two TMs define with different types is left out, as Studio does;
    ///  - text fields only (a picklist value can't be made up), matched by name with EverTM's spellings
    ///    ("Project", "Projekt", "Job"; "Client", "Customer", "Kunde").
    ///
    /// Never:
    ///  - over a value already there (the user's own, or one filled before);
    ///  - in a project created from a package, or a GroupShare project, unless switched on for that project;
    ///  - a change to a TM (adding a missing field is a separate, explicit action: <see cref="AddField"/>).
    ///
    /// Only file TMs are read for their fields. A GroupShare TM is left out (reading it means signing in to the server,
    /// at every project open); Studio still stamps it with a value filled for a field a file TM defines.
    /// </summary>
    public static class TmFieldStamping
    {
        public const string LogCategory = "TmStamp";

        /// <summary>One TM of the project, as far as stamping is concerned.</summary>
        public sealed class ProjectTm
        {
            public string Path;               // file TMs; null for others
            public Uri Uri;
            public bool IsFile => Path != null;
            public bool Update;               // Update ticked and enabled
            public string Language;           // the target language whose list it is in; null for the project's own list
        }

        /// <summary>What filling did, for the settings window and the log.</summary>
        public sealed class Outcome
        {
            public readonly List<string> Filled = new List<string>();      // "Project = PROJ-001"
            public readonly List<string> Kept = new List<string>();        // a value already there
            public readonly List<string> NoField = new List<string>();     // switched on, but no TM defines it
            public string Skipped;                                         // why nothing was done at all
            public override string ToString() =>
                Skipped ?? ("filled: " + Join(Filled) + "; already there: " + Join(Kept) + "; no TM field: " + Join(NoField));
            static string Join(List<string> l) => l.Count == 0 ? "-" : string.Join(", ", l);
        }

        // ---- the pure part: no Studio project needed (tested offline) ----

        /// <summary>
        /// The fields Studio lists for these TMs' definitions, by its own rule: the union by name and type, then every
        /// name defined with two different types removed.
        /// </summary>
        public static List<FieldDefinition> Union(IEnumerable<IEnumerable<FieldDefinition>> perTm)
        {
            var union = new List<FieldDefinition>();
            foreach (var fields in perTm)
                foreach (var f in fields ?? Enumerable.Empty<FieldDefinition>())
                    if (f != null && !union.Any(u => u.Name == f.Name && u.ValueType == f.ValueType)) union.Add(f);
            var conflicting = new HashSet<string>(union.Where(f => union.Any(o => o.Name == f.Name && o.ValueType != f.ValueType)).Select(f => f.Name));
            return union.Where(f => !conflicting.Contains(f.Name)).ToList();
        }

        /// <summary>The text field for <paramref name="canonical"/> (VaultFields.Project, Client) among <paramref name="fields"/>, or null.</summary>
        public static FieldDefinition Find(IEnumerable<FieldDefinition> fields, string canonical) =>
            fields.FirstOrDefault(f => VaultFields.Canonical(f.Name) == canonical
                                       && (f.ValueType == FieldValueType.SingleString || f.ValueType == FieldValueType.MultipleString));

        /// <summary>
        /// Sets <paramref name="field"/> to <paramref name="value"/> in <paramref name="values"/>, unless it already has a
        /// value. True when it changed something.
        /// </summary>
        public static bool Fill(FieldValues values, FieldDefinition field, string value)
        {
            if (values == null || field == null || string.IsNullOrWhiteSpace(value)) return false;
            var existing = values.Lookup(field.Name);
            // Never over what is there. Read the values themselves: GetValueString quotes a text value, so even an empty
            // one reads "\"\"".
            if (existing != null && SuperVault.SuperVaultImport.Values(existing).Any(v => !string.IsNullOrWhiteSpace(v))) return false;
            if (existing != null) values.Remove(existing);
            values.Add(field.ValueType == FieldValueType.MultipleString
                ? (FieldValue)new MultipleStringFieldValue(field.Name, new List<string> { value.Trim() })
                : new SingleStringFieldValue(field.Name, value.Trim()));
            return true;
        }

        /// <summary>
        /// True for a project Studio didn't create here: from a package (any "Imported..." type), a GroupShare project,
        /// or anything downloaded. Its TMs are usually somebody else's.
        /// </summary>
        public static bool IsFromElsewhere(ProjectType type) =>
            type != ProjectType.Standard && type != ProjectType.SingleFile;

        // ---- reading the TMs' fields: cached per file, so switching documents doesn't reopen TMs ----

        static readonly object CacheGate = new object();
        static readonly Dictionary<string, (DateTime written, long length, List<FieldDefinition> fields)> Cache =
            new Dictionary<string, (DateTime, long, List<FieldDefinition>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A file TM's field definitions, read once and kept while the file's size and write time stay the same (a
        /// field added in Studio's TM settings changes them). Null when the TM can't be opened (logged once per change).
        /// Thread-safe; meant for a background thread.
        /// </summary>
        public static List<FieldDefinition> FieldsOf(string tmPath)
        {
            FileInfo info;
            try { info = new FileInfo(tmPath); if (!info.Exists) return null; }
            catch { return null; }
            lock (CacheGate)
                if (Cache.TryGetValue(tmPath, out var c) && c.written == info.LastWriteTimeUtc && c.length == info.Length) return c.fields;
            List<FieldDefinition> fields;
            try { fields = new FileBasedTranslationMemory(tmPath).FieldDefinitions.ToList(); }
            catch (Exception ex)
            {
                DiagnosticLog.Log(LogCategory, "Could not read the fields of " + System.IO.Path.GetFileName(tmPath) + ": " + ex.Message);
                fields = null;
            }
            lock (CacheGate)
            {
                if (Cache.Count > 200) Cache.Clear();   // a day's work touches a handful of TMs
                Cache[tmPath] = (info.LastWriteTimeUtc, info.Length, fields);
            }
            return fields;
        }

        public static void ForgetCached(string tmPath) { lock (CacheGate) Cache.Remove(tmPath); }

        // ---- the Studio project: read on the UI thread, TMs read anywhere, settings written on the UI thread ----

        /// <summary>The project's Studio TMs (each language pair's list, or the project's where a pair has none of its own). UI thread.</summary>
        public static List<ProjectTm> TmsOf(FileBasedProject project)
        {
            var list = new List<ProjectTm>();
            void Add(TranslationProviderConfiguration config, string language)
            {
                foreach (var e in config?.Entries ?? new List<TranslationProviderCascadeEntry>())
                {
                    var uri = e.MainTranslationProvider?.Uri;
                    if (uri == null) continue;
                    string path = null;
                    if (uri.Scheme.StartsWith("sdltm.file", StringComparison.OrdinalIgnoreCase))
                        try { path = FileBasedTranslationMemory.GetFileBasedTranslationMemoryFilePath(uri); } catch { }
                    else if (!uri.Scheme.StartsWith("sdltm.", StringComparison.OrdinalIgnoreCase)) continue;   // EverTM, MT: not Studio TMs
                    list.Add(new ProjectTm { Path = path, Uri = uri, Update = e.PerformUpdate && e.MainTranslationProvider.Enabled, Language = language });
                }
            }
            Add(project.GetTranslationProviderConfiguration(), null);
            foreach (var lang in project.GetProjectInfo().TargetLanguages ?? new Language[0])
            {
                var own = project.GetTranslationProviderConfiguration(lang);
                if (own != null && own.OverrideParent) Add(own, lang.IsoAbbreviation);
            }
            return list;
        }

        /// <summary>The value for each switched-on field: Project = the project's name, Client = its customer.</summary>
        public static Dictionary<string, string> Wanted(bool project, bool client, string projectName, string customer)
        {
            var d = new Dictionary<string, string>();
            if (project && !string.IsNullOrWhiteSpace(projectName)) d[VaultFields.Project] = projectName.Trim();
            if (client && !string.IsNullOrWhiteSpace(customer)) d[VaultFields.Client] = customer.Trim();
            return d;
        }

        /// <summary>
        /// Fills the project's field values (and a language pair's own, where it has its own TM list). UI thread: it
        /// reads and writes the project's settings. <paramref name="fieldsOf"/> gives each file TM's fields; read them
        /// beforehand on a background thread (<see cref="FieldsOf"/> caches them) so this takes milliseconds.
        /// </summary>
        public static Outcome Apply(FileBasedProject project, List<ProjectTm> tms, Dictionary<string, string> wanted, Func<string, List<FieldDefinition>> fieldsOf)
        {
            var outcome = new Outcome();
            if (wanted.Count == 0) { outcome.Skipped = "nothing switched on"; return outcome; }
            var sw = Stopwatch.StartNew();

            // The project's own list: the union over every language pair's TMs, as Studio's "All Language Pairs" page.
            Fill(outcome, project.GetSettings(), b => project.UpdateSettings(b), tms.Where(t => t.Update && t.IsFile), wanted, fieldsOf);
            // A language pair with its own TM list also gets the values in its own settings.
            foreach (var group in tms.Where(t => t.Language != null).GroupBy(t => t.Language))
            {
                var lang = project.GetProjectInfo().TargetLanguages.FirstOrDefault(l => l.IsoAbbreviation == group.Key);
                if (lang == null) continue;
                Fill(outcome, project.GetSettings(lang), b => project.UpdateSettings(lang, b), group.Where(t => t.Update && t.IsFile), wanted, fieldsOf);
            }
            DiagnosticLog.Log(LogCategory, "'" + project.GetProjectInfo().Name + "': " + outcome + " (" + sw.ElapsedMilliseconds + " ms)");
            return outcome;
        }

        static void Fill(Outcome outcome, Sdl.Core.Settings.ISettingsBundle bundle, Action<Sdl.Core.Settings.ISettingsBundle> save,
                         IEnumerable<ProjectTm> tms, Dictionary<string, string> wanted, Func<string, List<FieldDefinition>> fieldsOf)
        {
            if (FillBundle(outcome, bundle, tms, wanted, fieldsOf)) save(bundle);
        }

        /// <summary>
        /// Fills one settings bundle's field values (the project's, or a language pair's) for these TMs' fields. True when
        /// it changed something; the caller then saves the bundle into the project. Public for the offline checks, which
        /// can't make a Studio project without a full Studio licence but can make a settings bundle.
        /// </summary>
        public static bool FillBundle(Outcome outcome, Sdl.Core.Settings.ISettingsBundle bundle,
                                      IEnumerable<ProjectTm> tms, Dictionary<string, string> wanted, Func<string, List<FieldDefinition>> fieldsOf)
        {
            var fields = Union(tms.Select(t => (IEnumerable<FieldDefinition>)fieldsOf(t.Path) ?? Enumerable.Empty<FieldDefinition>()));
            var group = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>();
            var values = new FieldValues(group.ProjectSettings.Value ?? new FieldValues());
            bool changed = false;
            foreach (var kv in wanted)
            {
                var field = Find(fields, kv.Key);
                if (field == null) { if (!outcome.NoField.Contains(kv.Key)) outcome.NoField.Add(kv.Key); continue; }
                if (Fill(values, field, kv.Value)) { changed = true; outcome.Filled.Add(field.Name + " = " + kv.Value); }
                else outcome.Kept.Add(field.Name);
            }
            if (!changed) return false;
            group.ProjectSettings.Value = values;
            return true;
        }

        // ---- adding a missing field to a file TM: only on the user's explicit OK, TM by TM ----

        /// <summary>
        /// Why this TM can't get a field added, or null when it can: only a file TM, writable, in a project of the user's
        /// own (not from a package or GroupShare), and only when it doesn't have a field of that name already.
        /// </summary>
        public static string CannotAddField(ProjectTm tm, bool projectFromElsewhere, string fieldName)
        {
            if (!tm.IsFile) return "a GroupShare TM: its fields are set up on the server";
            if (projectFromElsewhere) return "this project came from a package or GroupShare: its TMs are usually the agency's";
            try
            {
                var info = new FileInfo(tm.Path);
                if (!info.Exists) return "the TM file was not found";
                if (info.IsReadOnly) return "the TM file is read-only";
            }
            catch (Exception ex) { return "the TM file can't be checked: " + ex.Message; }
            var fields = FieldsOf(tm.Path);
            if (fields == null) return "the TM can't be opened";
            if (fields.Any(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))) return "it already has a field of that name";
            return null;
        }

        /// <summary>
        /// Adds a text field (multiple values, as a TM's own "Project" fields usually are) to a file TM and saves it.
        /// Changes the user's TM: call only after the user said yes to this TM. Throws on failure, with nothing changed.
        /// </summary>
        public static void AddField(string tmPath, string fieldName)
        {
            var tm = new FileBasedTranslationMemory(tmPath);
            if (tm.FieldDefinitions.Any(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))) return;
            tm.FieldDefinitions.Add(new FieldDefinition(fieldName, FieldValueType.MultipleString));
            tm.Save();
            ForgetCached(tmPath);
            DiagnosticLog.WriteAlways(LogCategory, "Added the field \"" + fieldName + "\" to " + System.IO.Path.GetFileName(tmPath) + ", with the user's OK.");
        }
    }
}
