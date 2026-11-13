using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.Core;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// Stamping project metadata (docs/design/tm-field-stamping.md), through Studio's own API: which fields count (Studio's
/// rule), filling never over a typed value, package projects, adding a field to a TM copy (and refusing for read-only,
/// GroupShare and package TMs), a real file-based project in a temp folder, EverTM's switches, and the cost at
/// project open. Test data: Acme / PROJ-001 only.
/// </summary>
static class StampChecks
{
    static Action<bool, string> Check;
    static string _dir;
    static readonly CultureCode NlNl = new CultureCode("nl-NL"), EnGb = new CultureCode("en-GB");

    public static void Run(string scratch, Action<bool, string> check)
    {
        Check = check;
        _dir = Path.Combine(scratch, "stamp");
        Directory.CreateDirectory(_dir);
        Console.WriteLine("Stamping project and client");
        FieldRules();
        Filling();
        ProjectTypes();
        TmFieldsAndAdding();
        FieldClashes();
        StaleValues();
        RealProject();
        EverTmSwitches();
    }

    static FieldDefinition Def(string name, FieldValueType type, params string[] items)
    {
        var d = new FieldDefinition(name, type);
        foreach (var i in items) d.PicklistItems.Add(i);
        return d;
    }

    static string Tm(string name, params FieldDefinition[] fields)
    {
        var path = Path.Combine(_dir, name + ".sdltm");
        var tm = new FileBasedTranslationMemory(path, name, NlNl, EnGb, FuzzyIndexes.SourceWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        foreach (var f in fields) tm.FieldDefinitions.Add(f);
        tm.Save();
        return path;
    }

    static void FieldRules()
    {
        var a = new[] { Def("Project", FieldValueType.MultipleString), Def("Kunde", FieldValueType.SingleString) };
        var b = new[] { Def("Project", FieldValueType.MultipleString), Def("Origin", FieldValueType.SingleString) };
        var u = TmFieldStamping.Union(new[] { a, b });
        Check(u.Count(f => f.Name == "Project") == 1 && u.Count == 3, "the same field in two TMs counts once: " + string.Join(", ", u.Select(f => f.Name)));
        Check(TmFieldStamping.Find(u, VaultFields.Client)?.Name == "Kunde", "a TM's \"Kunde\" field is the Client field");
        var conflict = TmFieldStamping.Union(new[] { a, new[] { Def("Project", FieldValueType.SinglePicklist, "PROJ-001") } });
        Check(TmFieldStamping.Find(conflict, VaultFields.Project) == null, "Project defined as text in one TM and a picklist in another: left out, as Studio does");
        var pick = TmFieldStamping.Union(new[] { new[] { Def("Project", FieldValueType.MultiplePicklist, "PROJ-001") } });
        Check(TmFieldStamping.Find(pick, VaultFields.Project) == null, "a picklist Project field: not filled (a picklist value can't be made up)");
        Check(TmFieldStamping.Find(TmFieldStamping.Union(new[] { new[] { Def("Projekt", FieldValueType.SingleString) } }), VaultFields.Project)?.Name == "Projekt", "\"Projekt\" counts as Project");
    }

    static void Filling()
    {
        var multi = Def("Project", FieldValueType.MultipleString);
        var values = new FieldValues();
        Check(TmFieldStamping.Fill(values, multi, "PROJ-001") && V(values, "Project") == "PROJ-001", "an empty value is filled");
        Check(!TmFieldStamping.Fill(values, multi, "PROJ-002") && V(values, "Project") == "PROJ-001", "a value already there is never changed");
        var typed = new FieldValues();
        typed.Add(new SingleStringFieldValue("Client", "Acme (typed)"));
        Check(!TmFieldStamping.Fill(typed, Def("Client", FieldValueType.SingleString), "Acme") && V(typed, "Client") == "Acme (typed)", "a value the user typed is never changed");
        var empty = new FieldValues();
        empty.Add(new SingleStringFieldValue("Client", ""));
        Check(TmFieldStamping.Fill(empty, Def("Client", FieldValueType.SingleString), "Acme") && V(empty, "Client") == "Acme", "an empty value left in the list is filled");
        Check(TmFieldStamping.Wanted(true, false, "PROJ-001", "Acme").Keys.SequenceEqual(new[] { VaultFields.Project }), "only the switched-on fields");
        Check(TmFieldStamping.Wanted(true, true, "PROJ-001", null).Count == 1, "no customer set: no Client value");
    }

    static void ProjectTypes()
    {
        var own = new[] { ProjectType.Standard, ProjectType.SingleFile };
        Check(own.All(t => !TmFieldStamping.IsFromElsewhere(t)) &&
              Enum.GetValues(typeof(ProjectType)).Cast<ProjectType>().Except(own).All(TmFieldStamping.IsFromElsewhere),
              "a project from a package, GroupShare or elsewhere is treated as not the user's own; Standard and SingleFile are");
    }

    static void TmFieldsAndAdding()
    {
        var with = Tm("acme-with", Def("Project", FieldValueType.MultipleString));
        var without = Tm("acme-without");
        var sw = Stopwatch.StartNew();
        var f1 = TmFieldStamping.FieldsOf(with);
        long first = sw.ElapsedMilliseconds; sw.Restart();
        var f2 = TmFieldStamping.FieldsOf(with);
        long again = sw.ElapsedMilliseconds;
        Check(f1 != null && f1.Any(f => f.Name == "Project") && ReferenceEquals(f1, f2), $"a TM's fields are read once ({first} ms) and then kept ({again} ms)");
        Check(TmFieldStamping.FieldsOf(Path.Combine(_dir, "missing.sdltm")) == null, "a missing TM: no fields, no error");
        var junk = Path.Combine(_dir, "junk.sdltm");
        File.WriteAllText(junk, "not a TM");
        Check(TmFieldStamping.FieldsOf(junk) == null, "a damaged TM: no fields, no error");

        var fileTm = new TmFieldStamping.ProjectTm { Path = without, Uri = FileBasedTranslationMemory.GetFileBasedTranslationMemoryUri(without), Update = true };
        var server = new TmFieldStamping.ProjectTm { Path = null, Uri = new Uri("sdltm.https://groupshare.acme.example/?orgPath=/&tmName=Acme"), Update = true };
        Check(TmFieldStamping.CannotAddField(fileTm, false, "Project") == null, "a field can be added to the user's own file TM");
        Check(TmFieldStamping.CannotAddField(server, false, "Project") != null, "never to a GroupShare TM: " + TmFieldStamping.CannotAddField(server, false, "Project"));
        Check(TmFieldStamping.CannotAddField(fileTm, true, "Project") != null, "never in a project from a package: " + TmFieldStamping.CannotAddField(fileTm, true, "Project"));
        Check(TmFieldStamping.CannotAddField(new TmFieldStamping.ProjectTm { Path = with, Uri = new Uri("sdltm.file:///x"), Update = true }, false, "Project") != null, "not when the TM has the field already");
        var ro = Tm("acme-readonly");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try { Check(TmFieldStamping.CannotAddField(new TmFieldStamping.ProjectTm { Path = ro, Uri = new Uri("sdltm.file:///y"), Update = true }, false, "Project") == "the TM file is read-only", "never to a read-only TM file"); }
        finally { File.SetAttributes(ro, FileAttributes.Normal); }

        TmFieldStamping.FieldsOf(without);   // cached without the field
        TmFieldStamping.AddField(without, new FieldDefinition("Project", FieldValueType.MultipleString));
        TmFieldStamping.AddField(without, new FieldDefinition("Project", FieldValueType.MultipleString));   // a second time (another Studio, a second click): nothing more
        var after = TmFieldStamping.FieldsOf(without);
        Check(after != null && after.Count(f => f.Name == "Project") == 1 && after.First(f => f.Name == "Project").ValueType == FieldValueType.MultipleString,
              "adding the field: the TM has one text field \"Project\" (twice is harmless), and the cache sees it at once");
        Check(new FileBasedTranslationMemory(without).FieldDefinitions.Any(f => f.Name == "Project"), "saved in the TM file");
    }

    static TmFieldStamping.ProjectTm P(string path) => new TmFieldStamping.ProjectTm { Path = path, Uri = FileBasedTranslationMemory.GetFileBasedTranslationMemoryUri(path), Update = true };

    static (string, IEnumerable<FieldDefinition>) Named(string path) => (Path.GetFileNameWithoutExtension(path), TmFieldStamping.FieldsOf(path));

    /// <summary>
    /// The case found on 13 Nov in a real project: the main TM defines Project and "client" as one text value each;
    /// "Add field" on the second TM added Project and Client as several text values. Studio then left Project out (two
    /// definitions), and "client"/"Client" showed as two rows. Now a field is added as the other TMs define it, a clash
    /// is said as such, and both spellings are filled.
    /// </summary>
    static void FieldClashes()
    {
        var main = Tm("clash-main", Def("Project", FieldValueType.SingleString), Def("client", FieldValueType.SingleString));
        var second = Tm("clash-second");
        var tms = new[] { P(main), P(second) };

        // Adding to the second TM: the main TM's exact name and type.
        var project = TmFieldStamping.FieldToAdd(P(second), tms, false, VaultFields.Project);
        var client = TmFieldStamping.FieldToAdd(P(second), tms, false, VaultFields.Client);
        Check(project.field?.Name == "Project" && project.field.ValueType == FieldValueType.SingleString,
              $"adding Project to a TM next to one that has it as one text value: the same (\"{project.field?.Name}\", {project.field?.ValueType})");
        Check(client.field?.Name == "client" && client.field.ValueType == FieldValueType.SingleString,
              $"adding Client next to a TM that spells it \"client\": the same spelling and type (\"{client.field?.Name}\", {client.field?.ValueType})");
        TmFieldStamping.AddField(second, project.field);
        TmFieldStamping.AddField(second, client.field);
        var outcome = new TmFieldStamping.Outcome();
        var bundle = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null);
        TmFieldStamping.FillBundle(outcome, bundle, tms, TmFieldStamping.Wanted(true, true, "PROJ-001", "Acme"), TmFieldStamping.FieldsOf);
        var stored = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>().ProjectSettings.Value;
        Check(outcome.Clashes.Count == 0 && outcome.NoField.Count == 0 && V(stored, "Project") == "PROJ-001" && V(stored, "client") == "Acme",
              "after adding them that way: no clash, both filled (" + outcome + ")");
        // And Studio's own TMs store the values in both, at a confirm.
        var settings = new ImportSettings { ProjectSettings = stored, NewFields = ImportSettings.NewFieldsOption.Ignore };
        foreach (var path in new[] { main, second })
        {
            var tm = new FileBasedTranslationMemory(path);
            tm.LanguageDirection.AddTranslationUnitsMasked(new[] { new TranslationUnit(Seg(NlNl, "De klep sluit."), Seg(EnGb, "The valve closes.")) }, settings, new[] { true });
            var it = new RegularIterator { MaxCount = 5 };
            var tu = tm.LanguageDirection.GetTranslationUnits(ref it).FirstOrDefault();
            Check(V(tu?.FieldValues, "Project") == "PROJ-001" && V(tu?.FieldValues, "client") == "Acme",
                  Path.GetFileNameWithoutExtension(path) + ": Studio's TM stores Project and client on the confirmed unit");
        }

        // The clash as it was made before this fix: several text values in the second TM.
        var oldSecond = Tm("clash-old-second", Def("Project", FieldValueType.MultipleString), Def("Client", FieldValueType.MultipleString));
        var clashed = new[] { P(main), P(oldSecond) };
        var o2 = new TmFieldStamping.Outcome();
        var b2 = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null);
        TmFieldStamping.FillBundle(o2, b2, clashed, TmFieldStamping.Wanted(true, true, "PROJ-001", "Acme"), TmFieldStamping.FieldsOf);
        var s2 = b2.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>().ProjectSettings.Value;
        var line = o2.ToString();
        Check(o2.NoField.Count == 0 && o2.Clashes.Count == 1 && line.Contains("clash-main") && line.Contains("clash-old-second") && line.Contains("one value") && line.Contains("several values") && !line.Contains("no TM"),
              "a Project field two TMs define differently: said as such, naming both TMs and how each defines it, never \"no TM field\": " + line);
        Check(V(s2, "Project") == null, "and not filled (Studio would leave it out)");
        Check(V(s2, "client") == "Acme" && V(s2, "Client") == "Acme" && o2.Filled.Count(f => f.StartsWith("client", StringComparison.OrdinalIgnoreCase)) == 1 && !o2.Kept.Any(),
              "\"client\" and \"Client\": one value covers both (field values ignore case), reported once, not as \"already there\": " + o2);
        // What Studio's own TM does with one "client" value for its "Client" field (several values), at a confirm.
        {
            var only = new FieldValues();
            only.Add(new SingleStringFieldValue("client", "Acme"));
            var tm2 = new FileBasedTranslationMemory(oldSecond);
            tm2.LanguageDirection.AddTranslationUnitsMasked(new[] { new TranslationUnit(Seg(NlNl, "De klep opent."), Seg(EnGb, "The valve opens.")) },
                new ImportSettings { ProjectSettings = only, NewFields = ImportSettings.NewFieldsOption.Ignore }, new[] { true });
            var it2 = new RegularIterator { MaxCount = 5 };
            var tu2 = tm2.LanguageDirection.GetTranslationUnits(ref it2).FirstOrDefault();
            Check(V(tu2?.FieldValues, "Client") == "Acme",
                  "Studio's TM matches field names ignoring case: its \"Client\" (several values) stores a \"client\" value (one value) as Acme, so the spellings share one value");
        }
        // Adding to a third TM now: the others disagree, so nothing is added.
        var third = Tm("clash-third");
        var refused = TmFieldStamping.FieldToAdd(P(third), new[] { P(main), P(oldSecond), P(third) }, false, VaultFields.Project);
        Check(refused.field == null && refused.refused.Contains("more than one way"), "a third TM, when the others already disagree: refused, with the reason: " + refused.refused);
        var refusedClient = TmFieldStamping.FieldToAdd(P(third), new[] { P(main), P(oldSecond), P(third) }, false, VaultFields.Client);
        Check(refusedClient.field == null, "likewise \"client\" and \"Client\" in two TMs: refused (" + refusedClient.refused + ")");

        // A picklist in another TM: not copied, nothing added.
        var pick = Tm("clash-picklist", Def("Project", FieldValueType.SinglePicklist, "PROJ-001"));
        var withPick = TmFieldStamping.FieldToAdd(P(third), new[] { P(pick), P(third) }, false, VaultFields.Project);
        Check(withPick.field == null && withPick.refused.Contains("list of fixed values"), "next to a picklist Project field: refused (" + withPick.refused + ")");
        Check(TmFieldStamping.FieldsOf(pick).Single(f => f.Name == "Project").PicklistItems.Select(i => i.Name).SequenceEqual(new[] { "PROJ-001" }), "the picklist TM is untouched");
        // A TM that already has the field under another name.
        var kunde = Tm("clash-kunde", Def("Kunde", FieldValueType.SingleString));
        var already = TmFieldStamping.FieldToAdd(P(kunde), new[] { P(kunde) }, false, VaultFields.Client);
        Check(already.field == null && already.refused.Contains("Kunde"), "a TM with \"Kunde\": it already has the Client field (" + already.refused + ")");
        // Still refused: GroupShare, read-only, a package project.
        var server = new TmFieldStamping.ProjectTm { Path = null, Uri = new Uri("sdltm.https://groupshare.acme.example/?orgPath=/&tmName=Acme"), Update = true };
        Check(TmFieldStamping.FieldToAdd(server, new[] { P(main), server }, false, VaultFields.Project).field == null, "a GroupShare TM: still refused");
        Check(TmFieldStamping.FieldToAdd(P(third), new[] { P(main), P(third) }, true, VaultFields.Project).field == null, "a project from a package: still refused");
        var ro = Tm("clash-readonly");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try { Check(TmFieldStamping.FieldToAdd(P(ro), new[] { P(main), P(ro) }, false, VaultFields.Project).refused == "the TM file is read-only", "a read-only TM file: still refused"); }
        finally { File.SetAttributes(ro, FileAttributes.Normal); }
        // No other TM has it: the default, as before.
        var lone = TmFieldStamping.FieldToAdd(P(third), new[] { P(third) }, false, VaultFields.Project);
        Check(lone.field?.Name == "Project" && lone.field.ValueType == FieldValueType.MultipleString, "no other TM has it: \"Project\", several text values, as before");
    }

    /// <summary>
    /// A value already there that isn't this project's (a template carrying an older project's name): kept by stamping,
    /// shown, and replaced only by the window's button; also when Studio no longer lists the field, and in a language
    /// pair's own list.
    /// </summary>
    static void StaleValues()
    {
        var wanted = TmFieldStamping.Wanted(true, true, "PROJ-001", "Acme");
        var values = new FieldValues();
        values.Add(new SingleStringFieldValue("Project", "PROJ-000"));
        values.Add(new MultipleStringFieldValue("client", new List<string> { "Acme" }));
        var stale = TmFieldStamping.StaleValues(values, wanted, null);
        Check(stale.Count == 1 && stale[0].Field == "Project" && stale[0].Value == "PROJ-000" && stale[0].Wanted == "PROJ-001",
              "Project \"PROJ-000\" in a project called PROJ-001: shown as not this project's; the matching client isn't");
        Check(!TmFieldStamping.Fill(values, Def("Project", FieldValueType.SingleString), "PROJ-001") && V(values, "Project") == "PROJ-000",
              "stamping still never replaces it");
        var outcome = new TmFieldStamping.Outcome();
        var bundle = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null);
        bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>().ProjectSettings.Value = values;
        var tm = Tm("stale-tm", Def("Project", FieldValueType.SingleString));
        TmFieldStamping.FillBundle(outcome, bundle, new[] { P(tm) }, wanted, TmFieldStamping.FieldsOf);
        Check(outcome.ToString().Contains("PROJ-000") && outcome.ToString().Contains("not this project's"), "the outcome line names the value kept: " + outcome);

        // Hidden: two TMs define Project differently, so Studio doesn't list it, but the value is still in the settings.
        var hidden = TmFieldStamping.StaleValues(values, wanted, null);
        var clashing = TmFieldStamping.Clashing(TmFieldStamping.Uses(new[] { ("TM-A", (IEnumerable<FieldDefinition>)new[] { Def("Project", FieldValueType.SingleString) }), ("TM-B", new[] { Def("Project", FieldValueType.MultipleString) }) }, VaultFields.Project));
        Check(hidden.Count == 1 && clashing.Count == 2, "a value in a field Studio leaves out (a clash) is still found and shown");

        // Only the button replaces it, keeping the value's kind; a typed value of a switched-off field is not even listed.
        Check(TmFieldStamping.Replace(values, "Project", "PROJ-001") && V(values, "Project") == "PROJ-001" && values.Lookup("Project") is SingleStringFieldValue,
              "the button's replace: PROJ-001, still one text value");
        var typed = new FieldValues();
        typed.Add(new SingleStringFieldValue("Client", "Acme Ltd (typed)"));
        Check(TmFieldStamping.StaleValues(typed, TmFieldStamping.Wanted(true, false, "PROJ-001", "Acme"), null).Count == 0,
              "a client typed in a project with only Project switched on: not listed, never replaced");
        Check(!TmFieldStamping.Fill(typed, Def("Client", FieldValueType.SingleString), "Acme") && V(typed, "Client") == "Acme Ltd (typed)",
              "a value the user typed is never replaced by stamping");
        var pick = new FieldValues();
        pick.Add(new SinglePicklistFieldValue("Project", new PicklistItem("PROJ-000")));
        Check(!TmFieldStamping.Replace(pick, "Project", "PROJ-001"), "a picklist value is never replaced (not Supervertaler's to make up)");

        // A language pair's own list: found with its language.
        var de = TmFieldStamping.StaleValues(new FieldValues { new SingleStringFieldValue("Projekt", "PROJ-000") }, wanted, "de-DE");
        Check(de.Count == 1 && de[0].Where == "de-DE" && de[0].Canonical == VaultFields.Project, "in a language pair's own list (\"Projekt\", de-DE): found, with its language");
    }

    static string V(FieldValues values, string name)
    {
        var fv = values?.Lookup(name);
        return fv == null ? null : string.Join("|", SuperVaultImport.Values(fv));
    }

    /// <summary>
    /// What the plugin writes into a project's settings, and what Studio's own file TM then does with it at a confirm.
    /// Studio won't make a project outside Studio without a full licence, so the project's settings are a settings
    /// bundle made the same way Studio makes them; reading a project's TM list is left to the test in Studio.
    /// </summary>
    static void RealProject()
    {
        var tmWith = Tm("proj-tm-with", Def("Project", FieldValueType.MultipleString), Def("Client", FieldValueType.SingleString));
        var tmWithout = Tm("proj-tm-without");
        var tms = new[] { tmWith, tmWithout }.Select(p => new TmFieldStamping.ProjectTm { Path = p, Uri = FileBasedTranslationMemory.GetFileBasedTranslationMemoryUri(p), Update = true }).ToList();
        tms.Add(new TmFieldStamping.ProjectTm { Path = null, Uri = new Uri("sdltm.https://groupshare.acme.example/?orgPath=/&tmName=Acme"), Update = true });   // a GroupShare TM: left out, no error
        var bundle = Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null);
        var sw = Stopwatch.StartNew();
        var fields = tms.Where(t => t.IsFile).ToDictionary(t => t.Path, t => TmFieldStamping.FieldsOf(t.Path), StringComparer.OrdinalIgnoreCase);
        long read = sw.ElapsedMilliseconds; sw.Restart();
        var outcome = new TmFieldStamping.Outcome();
        bool changed = TmFieldStamping.FillBundle(outcome, bundle, tms.Where(t => t.IsFile), TmFieldStamping.Wanted(true, true, "ACME_Globex (PROJ-001)", "Acme"), p => fields[p]);
        long apply = sw.ElapsedMilliseconds;
        var stored = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>().ProjectSettings.Value;
        Check(changed && V(stored, "Project") == "ACME_Globex (PROJ-001)" && V(stored, "Client") == "Acme",
              $"the field values now hold Project and Client ({outcome}); reading the TMs {read} ms, filling {apply} ms");

        // The user changes the value in Studio's Field Values page: the next run leaves it.
        var tmSettings = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>();
        var v = new FieldValues(tmSettings.ProjectSettings.Value);
        v.Remove("Client");
        v.Add(new SingleStringFieldValue("Client", "Acme Ltd (typed)"));
        tmSettings.ProjectSettings.Value = v;
        var second = new TmFieldStamping.Outcome();
        bool again = TmFieldStamping.FillBundle(second, bundle, tms.Where(t => t.IsFile), TmFieldStamping.Wanted(true, true, "ACME_Globex (PROJ-001)", "Acme"), p => fields[p]);
        var now = bundle.GetSettingsGroup<Sdl.ProjectAutomation.Settings.TranslationMemorySettings>().ProjectSettings.Value;
        Check(!again && V(now, "Client") == "Acme Ltd (typed)", "a second run changes nothing, and the typed value stays: " + second);

        var none = new TmFieldStamping.Outcome();
        bool nothing = TmFieldStamping.FillBundle(none, Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null), tms.Where(t => t.Path == tmWithout), TmFieldStamping.Wanted(true, true, "PROJ-001", "Acme"), p => fields[p]);
        Check(!nothing && none.NoField.Count == 2, "a project whose TMs have no such field: nothing filled, and said so: " + none);

        // What Studio hands every TM at a confirm (ImportSettings.ProjectSettings, NewFields = Ignore), applied by its own file TM.
        var settings = new ImportSettings { ProjectSettings = now, NewFields = ImportSettings.NewFieldsOption.Ignore };
        foreach (var path in new[] { tmWith, tmWithout })
        {
            var tm = new FileBasedTranslationMemory(path);
            var r = tm.LanguageDirection.AddTranslationUnitsMasked(new[] { new TranslationUnit(Seg(NlNl, "De pomp start."), Seg(EnGb, "The pump starts.")) }, settings, new[] { true })[0];
            var it = new RegularIterator { MaxCount = 5 };
            var tu = tm.LanguageDirection.GetTranslationUnits(ref it).FirstOrDefault();
            if (path == tmWith)
                Check(r.Action == Sdl.LanguagePlatform.TranslationMemory.Action.Add && V(tu?.FieldValues, "Project") == "ACME_Globex (PROJ-001)" && V(tu.FieldValues, "Client") == "Acme Ltd (typed)",
                      "Studio's TM with the fields stores them on the unit: " + V(tu?.FieldValues, "Project") + " / " + V(tu?.FieldValues, "Client"));
            else
                Check(r.Action == Sdl.LanguagePlatform.TranslationMemory.Action.Add && tu != null && tu.FieldValues.Count == 0, "Studio's TM without them stores the unit, without fields, no error");
        }

        // Scale: ten TMs of 31 fields each, the cost at project open.
        var ten = Enumerable.Range(1, 10).Select(i => Tm("scale-" + i, Enumerable.Range(1, 30).Select(k => Def("Field " + k, FieldValueType.SingleString)).Concat(new[] { Def("Project", FieldValueType.MultipleString) }).ToArray())).ToList();
        sw.Restart();
        foreach (var p in ten) TmFieldStamping.FieldsOf(p);
        long cold = sw.ElapsedMilliseconds; sw.Restart();
        foreach (var p in ten) TmFieldStamping.FieldsOf(p);
        long warm = sw.ElapsedMilliseconds; sw.Restart();
        var tenTms = ten.Select(p => new TmFieldStamping.ProjectTm { Path = p, Uri = FileBasedTranslationMemory.GetFileBasedTranslationMemoryUri(p), Update = true }).ToList();
        TmFieldStamping.FillBundle(new TmFieldStamping.Outcome(), Sdl.Core.Settings.SettingsUtil.CreateSettingsBundle(null), tenTms, TmFieldStamping.Wanted(true, true, "PROJ-001", "Acme"), TmFieldStamping.FieldsOf);
        long fill = sw.ElapsedMilliseconds;
        Console.WriteLine($"        scale: 10 TMs x 31 fields: first read {cold} ms (thread pool), cached {warm} ms, filling the settings {fill} ms (UI thread)");
        Check(warm < 50 && fill < 50, $"ten TMs of 31 fields: read in {cold} ms the first time (thread pool), {warm} ms afterwards; filling on the UI thread {fill} ms");
    }

    static Segment Seg(CultureCode c, string t) { var s = new Segment(c); s.Add(t); return s; }

    /// <summary>EverTM's switches, through the provider as Studio confirms: a switched-off field is not stored.</summary>
    static void EverTmSwitches()
    {
        var root = Path.Combine(_dir, "evertm");
        Supervertaler.Trados.Settings.SettingsService.Update(s => s.EverTmStampOff = new List<string> { VaultFields.Project, VaultLearnUnit.UserField });
        try
        {
            var vault = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
            var tu = new TranslationUnit(Seg(NlNl, "De klep lekt."), Seg(EnGb, "The valve leaks."));
            tu.SystemFields.ChangeUser = "translator";
            var settings = new ImportSettings { ProjectSettings = new FieldValues() };
            settings.ProjectSettings.Add(new SingleStringFieldValue("Subject", "Patents"));
            vault.AddOrUpdateTranslationUnitsMasked(new[] { tu }, new[] { 0 }, settings, new[] { true });
            Sdl.LanguagePlatform.TranslationMemory.SearchResult hit = null;
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 20)
            {
                var cascade = new Cascade<CascadeEntry>(new[] { new CascadeEntry(vault, 0) }, false, true, NlNl, EnGb);
                hit = cascade.SearchSegment(new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 70, MaxResults = 5 }, Seg(NlNl, "De klep lekt.")).Results.FirstOrDefault();
                if (hit?.MemoryTranslationUnit.FieldValues.Lookup("Subject") != null) break;
                Thread.Sleep(200);
            }
            var fv = hit?.MemoryTranslationUnit.FieldValues;
            Check(fv != null && fv.Lookup("Subject") != null && fv.Lookup("Project") == null && hit.MemoryTranslationUnit.SystemFields.ChangeUser == null,
                  "EverTM with Project and User switched off: Subject stored, no Project, no user");
        }
        finally { Supervertaler.Trados.Settings.SettingsService.Update(s => s.EverTmStampOff = new List<string>()); }
    }
}
