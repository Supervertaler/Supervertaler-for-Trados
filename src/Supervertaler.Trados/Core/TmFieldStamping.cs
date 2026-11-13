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
    ///  - a field two TMs define with different types is left out, as Studio does, and said so (naming the TMs);
    ///  - two names for the same field: "Kunde" and "Client" are two fields, both filled; "client" and "Client" are two
    ///    rows on Studio's Field Values page but one value (field values and TMs ignore case), filled once;
    ///  - text fields only (a picklist value can't be made up), matched by name with EverTM's spellings
    ///    ("Project", "Projekt", "Job"; "Client", "Customer", "Kunde").
    ///
    /// Never:
    ///  - over a value already there (the user's own, or one filled before); one that isn't this project's is shown
    ///    in "This project's TMs", with a button to replace it (<see cref="ReplaceStale"/>);
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
            /// <summary>Left out because two TMs define it differently ("Project: TM-A one text value, TM-B several").</summary>
            public readonly List<string> Clashes = new List<string>();
            public string Skipped;                                         // why nothing was done at all
            public override string ToString()
            {
                if (Skipped != null) return Skipped;
                var s = "filled: " + Join(Filled) + "; already there: " + Join(Kept);
                if (Clashes.Count > 0) s += "; left out, the TMs define it differently: " + Join(Clashes);
                if (NoField.Count > 0) s += "; no TM has the field: " + Join(NoField);
                return s;
            }
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
        public static FieldDefinition Find(IEnumerable<FieldDefinition> fields, string canonical) => FindAll(fields, canonical).FirstOrDefault();

        /// <summary>
        /// Every text field for <paramref name="canonical"/>: two TMs may call it differently ("Kunde" and "Client"), and
        /// each is filled. ("client" and "Client" share one value: field values ignore case.)
        /// </summary>
        public static List<FieldDefinition> FindAll(IEnumerable<FieldDefinition> fields, string canonical) =>
            fields.Where(f => VaultFields.Canonical(f.Name) == canonical && IsText(f.ValueType)).ToList();

        static bool IsText(FieldValueType t) => t == FieldValueType.SingleString || t == FieldValueType.MultipleString;

        /// <summary>How a field type reads to a user.</summary>
        public static string Describe(FieldValueType t)
        {
            switch (t)
            {
                case FieldValueType.SingleString: return "text, one value";
                case FieldValueType.MultipleString: return "text, several values";
                case FieldValueType.SinglePicklist: return "a list of fixed values, one";
                case FieldValueType.MultiplePicklist: return "a list of fixed values, several";
                case FieldValueType.DateTime: return "a date";
                case FieldValueType.Integer: return "a number";
                default: return t.ToString();
            }
        }

        /// <summary>One TM's definition of a field: which TM, the field's exact name, its type.</summary>
        public sealed class FieldUse
        {
            public string Tm; public string Name; public FieldValueType Type;
            public override string ToString() => Tm + ": \"" + Name + "\" (" + Describe(Type) + ")";
        }

        /// <summary>Every definition of <paramref name="canonical"/> among these TMs (name = the TM's name for messages).</summary>
        public static List<FieldUse> Uses(IEnumerable<(string tm, IEnumerable<FieldDefinition> fields)> tms, string canonical)
        {
            var list = new List<FieldUse>();
            foreach (var (tm, fields) in tms)
                foreach (var f in fields ?? Enumerable.Empty<FieldDefinition>())
                    if (f != null && VaultFields.Canonical(f.Name) == canonical && !list.Any(u => u.Tm == tm && u.Name == f.Name))
                        list.Add(new FieldUse { Tm = tm, Name = f.Name, Type = f.ValueType });
            return list;
        }

        /// <summary>
        /// The definitions Studio drops: a name (exactly as spelled) that two TMs define with different types. Studio
        /// then leaves the field off its Field Values page and stamps nothing new into it. Empty when there's no clash.
        /// </summary>
        public static List<FieldUse> Clashing(List<FieldUse> uses) =>
            uses.Where(u => uses.Any(o => o.Name == u.Name && o.Type != u.Type)).ToList();

        /// <summary>A clash said plainly: "Project": TM-A defines it as text, one value; TM-B as text, several values.</summary>
        public static string DescribeClash(List<FieldUse> clashing) =>
            string.Join("; ", clashing.GroupBy(u => u.Name).Select(g => "\"" + g.Key + "\": " +
                string.Join(", ", g.Select(u => u.Tm + " defines it as " + Describe(u.Type)))));

        /// <summary>
        /// The field to add to a TM so that it matches the project's other TMs, or why none may be added. When another
        /// TM already defines the field (under any of its names), the new one gets exactly that TM's name and type, so
        /// Studio sees one field, not a clash; with no such TM, <paramref name="defaultName"/> as text with several
        /// values. Refused when this TM already has the field, when the others define it as something Supervertaler
        /// can't fill (a list of fixed values), or when they already define it in more than one way.
        /// </summary>
        public static (FieldDefinition field, string refused) FieldToAdd(IEnumerable<FieldDefinition> own,
            IEnumerable<(string tm, IEnumerable<FieldDefinition> fields)> others, string canonical, string defaultName)
        {
            var mine = (own ?? Enumerable.Empty<FieldDefinition>()).FirstOrDefault(f => VaultFields.Canonical(f.Name) == canonical);
            if (mine != null) return (null, "it already has the field (\"" + mine.Name + "\")");
            var uses = Uses(others, canonical);
            if (uses.Count == 0) return (new FieldDefinition(defaultName, FieldValueType.MultipleString), null);
            var kinds = uses.GroupBy(u => (u.Name, u.Type)).ToList();
            if (kinds.Count > 1)
                return (null, "the project's other TMs already define it in more than one way (" + string.Join("; ", uses) +
                              "), so a new field would match only some of them. Make them the same in Studio's TM settings first");
            var one = uses[0];
            if (!IsText(one.Type))
                return (null, one + ": a field of that name that isn't text would clash with it, and Supervertaler can't fill " + Describe(one.Type));
            return (new FieldDefinition(one.Name, one.Type), null);
        }

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
            var perTm = tms.Select(t => (tm: TmName(t), fields: (IEnumerable<FieldDefinition>)fieldsOf(t.Path) ?? Enumerable.Empty<FieldDefinition>())).ToList();
            var fields = Union(perTm.Select(x => x.fields));
            var group = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>();
            var values = new FieldValues(group.ProjectSettings.Value ?? new FieldValues());
            bool changed = false;
            foreach (var kv in wanted)
            {
                // A spelling Studio drops (two TMs define it differently) is said as such, whether or not another
                // spelling can still be filled.
                var clashing = Clashing(Uses(perTm, kv.Key));
                if (clashing.Count > 0)
                {
                    var what = DescribeClash(clashing);
                    if (!outcome.Clashes.Contains(what)) outcome.Clashes.Add(what);
                }
                var found = FindAll(fields, kv.Key);
                if (found.Count == 0)
                {
                    if (clashing.Count == 0 && !outcome.NoField.Contains(kv.Key)) outcome.NoField.Add(kv.Key);
                    continue;
                }
                // Field values are looked up ignoring case, and so are a TM's fields: "client" and "Client" share one value.
                foreach (var field in found.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    if (Fill(values, field, kv.Value)) { changed = true; outcome.Filled.Add(field.Name + " = " + kv.Value); }
                    else
                    {
                        var there = ValueOf(values, field.Name);
                        outcome.Kept.Add(there != null && there != kv.Value.Trim() ? field.Name + " (\"" + there + "\", not this project's \"" + kv.Value.Trim() + "\")" : field.Name);
                    }
                }
            }
            if (!changed) return false;
            group.ProjectSettings.Value = values;
            return true;
        }

        /// <summary>A TM's name for messages: its file name, or the server TM's name.</summary>
        public static string TmName(ProjectTm tm) => tm.IsFile ? System.IO.Path.GetFileNameWithoutExtension(tm.Path) : tm.Uri?.ToString();

        /// <summary>The text of a value in the field values, its values joined by " | "; null when it has none.</summary>
        public static string ValueOf(FieldValues values, string fieldName)
        {
            var fv = values?.Lookup(fieldName);
            if (fv == null) return null;
            var all = SuperVault.SuperVaultImport.Values(fv).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            return all.Count == 0 ? null : string.Join(" | ", all);
        }

        // ---- a value already there that isn't this project's: shown, replaced only on the user's word ----

        /// <summary>A value in a settings bundle's field values that differs from what this project would stamp.</summary>
        public sealed class Stale
        {
            public string Canonical;    // VaultFields.Project or Client
            public string Field;        // the field's name as stored
            public string Value;        // what is there
            public string Wanted;       // this project's name or customer
            public string Where;        // null: all language pairs; else a target language with its own TM list
        }

        /// <summary>
        /// The values in <paramref name="values"/> for the switched-on fields that are not this project's (a template
        /// that carried an older project's name, say). Read from the values themselves, whatever the TMs define, so one
        /// Studio no longer lists (two TMs defining it differently) still shows: Studio still applies it.
        /// </summary>
        public static List<Stale> StaleValues(FieldValues values, Dictionary<string, string> wanted, string where)
        {
            var list = new List<Stale>();
            if (values == null) return list;
            foreach (var fv in values)
            {
                var canonical = VaultFields.Canonical(fv.Name);
                if (canonical == null || !wanted.TryGetValue(canonical, out var want)) continue;
                var there = ValueOf(values, fv.Name);
                if (there != null && !string.Equals(there, want.Trim(), StringComparison.Ordinal))
                    list.Add(new Stale { Canonical = canonical, Field = fv.Name, Value = there, Wanted = want.Trim(), Where = where });
            }
            return list;
        }

        /// <summary>
        /// Puts <paramref name="value"/> in place of what <paramref name="fieldName"/> holds, keeping the value's kind
        /// (one text value or several). Only for a text value, and only when the user asked for it (the window's
        /// button): stamping itself never replaces a value. True when it changed something.
        /// </summary>
        public static bool Replace(FieldValues values, string fieldName, string value)
        {
            var existing = values?.Lookup(fieldName);
            if (existing == null || string.IsNullOrWhiteSpace(value)) return false;
            FieldValue replacement;
            if (existing is MultipleStringFieldValue) replacement = new MultipleStringFieldValue(fieldName, new List<string> { value.Trim() });
            else if (existing is SingleStringFieldValue) replacement = new SingleStringFieldValue(fieldName, value.Trim());
            else return false;   // a list of fixed values: not Supervertaler's to make up
            values.Remove(existing);
            values.Add(replacement);
            return true;
        }

        /// <summary>
        /// Replaces the stale values in the project's settings (and a language pair's own) with this project's. UI
        /// thread; only on the user's word. Returns how many values were replaced.
        /// </summary>
        public static int ReplaceStale(FileBasedProject project, List<Stale> stale)
        {
            int n = 0;
            foreach (var group in stale.GroupBy(x => x.Where))
            {
                var lang = group.Key == null ? null : project.GetProjectInfo().TargetLanguages.FirstOrDefault(l => l.IsoAbbreviation == group.Key);
                if (group.Key != null && lang == null) continue;
                var bundle = lang == null ? project.GetSettings() : project.GetSettings(lang);
                var tmSettings = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>();
                var values = new FieldValues(tmSettings.ProjectSettings.Value ?? new FieldValues());
                int here = group.Count(x => Replace(values, x.Field, x.Wanted));
                if (here == 0) continue;
                tmSettings.ProjectSettings.Value = values;
                if (lang == null) project.UpdateSettings(bundle); else project.UpdateSettings(lang, bundle);
                n += here;
                DiagnosticLog.WriteAlways(LogCategory, "Replaced " + here + " field value(s) " + (lang == null ? "for all language pairs" : "for " + group.Key) + " with this project's, with the user's OK.");
            }
            return n;
        }

        // ---- adding a missing field to a file TM: only on the user's explicit OK, TM by TM ----

        /// <summary>
        /// Why this TM can't get a field added, or null when it can: only a file TM, writable, in a project of the user's
        /// own (not from a package or GroupShare). What field it would get, and whether that clashes, is
        /// <see cref="FieldToAdd"/>'s.
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
            var mine = fields.FirstOrDefault(f => VaultFields.Canonical(f.Name) == VaultFields.Canonical(fieldName));
            if (mine != null) return "it already has the field (\"" + mine.Name + "\")";
            return null;
        }

        /// <summary>
        /// The field to add to <paramref name="tm"/> for <paramref name="canonical"/>, matching the project's other file
        /// TMs (every language pair's), or why not. See <see cref="FieldToAdd"/>.
        /// </summary>
        public static (FieldDefinition field, string refused) FieldToAdd(ProjectTm tm, IEnumerable<ProjectTm> projectTms, bool projectFromElsewhere, string canonical)
        {
            var why = CannotAddField(tm, projectFromElsewhere, canonical);
            if (why != null) return (null, why);
            var others = projectTms.Where(t => t.IsFile && !string.Equals(t.Path, tm.Path, StringComparison.OrdinalIgnoreCase))
                                   .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                                   .Select(t => (TmName(t), (IEnumerable<FieldDefinition>)FieldsOf(t.Path) ?? Enumerable.Empty<FieldDefinition>()));
            return FieldToAdd(FieldsOf(tm.Path), others, canonical, canonical);
        }

        /// <summary>
        /// Adds <paramref name="field"/> (its name and type: <see cref="FieldToAdd"/>'s) to a file TM and saves it.
        /// Changes the user's TM: call only after the user said yes to this TM. Throws on failure, with nothing changed.
        /// Nothing happens if the TM has a field of that name already (another Studio, a second click).
        /// </summary>
        public static void AddField(string tmPath, FieldDefinition field)
        {
            var tm = new FileBasedTranslationMemory(tmPath);
            if (tm.FieldDefinitions.Any(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase))) return;
            tm.FieldDefinitions.Add(new FieldDefinition(field.Name, field.ValueType));
            tm.Save();
            ForgetCached(tmPath);
            DiagnosticLog.WriteAlways(LogCategory, "Added the field \"" + field.Name + "\" (" + Describe(field.ValueType) + ") to " + System.IO.Path.GetFileName(tmPath) + ", with the user's OK.");
        }
    }
}
