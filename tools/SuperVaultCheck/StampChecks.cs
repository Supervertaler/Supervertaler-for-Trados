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
        TmFieldStamping.AddField(without, "Project");
        TmFieldStamping.AddField(without, "Project");   // a second time (another Studio, a second click): nothing more
        var after = TmFieldStamping.FieldsOf(without);
        Check(after != null && after.Count(f => f.Name == "Project") == 1 && after.First(f => f.Name == "Project").ValueType == FieldValueType.MultipleString,
              "adding the field: the TM has one text field \"Project\" (twice is harmless), and the cache sees it at once");
        Check(new FileBasedTranslationMemory(without).FieldDefinitions.Any(f => f.Name == "Project"), "saved in the TM file");
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
