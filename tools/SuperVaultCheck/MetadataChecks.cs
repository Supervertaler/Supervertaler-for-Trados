using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sdl.Core.Globalization;
using DocumentProperties = Sdl.FileTypeSupport.Framework.Bilingual.DocumentProperties;
using FileProperties = Sdl.FileTypeSupport.Framework.Bilingual.FileProperties;
using PersistentFileConversionProperties = Sdl.FileTypeSupport.Framework.Native.PersistentFileConversionProperties;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.Core.Tokenization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using Supervertaler.Core.SuperVault;
using Supervertaler.Trados.SuperVault;

/// <summary>
/// EverTM metadata, phase 1, through Studio's own types (docs/design/evertm-metadata.md): a Studio TM's fields and
/// dates imported; matches carrying FieldValues and SystemFields through Studio's Cascade; newest wins and the exact
/// target variant first; a confirmed segment tagged with its project, client, subject, document and user. Test data:
/// Acme / PROJ-001 only.
/// </summary>
static class MetadataChecks
{
    static Action<bool, string> Check;
    static string _scratch;
    static readonly CultureCode NlNl = new CultureCode("nl-NL"), EnGb = new CultureCode("en-GB"), EnUs = new CultureCode("en-US");

    public static void Run(string scratch, Action<bool, string> check)
    {
        Check = check;
        _scratch = Path.Combine(scratch, "metadata");
        Directory.CreateDirectory(_scratch);
        Console.WriteLine("Metadata: kept, shown, newest wins");
        var root = Path.Combine(_scratch, "vault");
        StudioTmFieldsAndDates(root);
        MatchesCarryFieldsAndDates(root);
        NewestAndVariantThroughCascade(root);
        ConfirmedSegmentsAreTagged(root);
    }

    static Segment Seg(CultureCode c, string text) { var s = new Segment(c); s.Add(text); return s; }
    static string Text(Segment s) => s == null ? "" : string.Concat(s.Elements.OfType<Text>().Select(t => t.Value));

    static SearchResults Search(ITranslationProviderLanguageDirection dir, string text, CultureCode source, CultureCode target)
    {
        var cascade = new Cascade<CascadeEntry>(new[] { new CascadeEntry(dir, 0) }, false, true, source, target);
        return cascade.SearchSegment(new SearchSettings { Mode = SearchMode.NormalSearch, MinScore = 70, MaxResults = 10, IsDocumentSearch = true, ComputeTranslationProposal = true }, Seg(source, text));
    }

    static string Field(SearchResult r, string name)
    {
        var fv = r?.MemoryTranslationUnit?.FieldValues?.Lookup(name);
        return fv == null ? "" : string.Join("|", SuperVaultImport.Values(fv).OrderBy(v => v, StringComparer.Ordinal));
    }

    /// <summary>A Studio TM with custom fields, as agency TMs have them; imported through Studio's public TM API.</summary>
    static void StudioTmFieldsAndDates(string root)
    {
        var tm = new FileBasedTranslationMemory(Path.Combine(_scratch, "agency.sdltm"), "agency", NlNl, EnGb,
            FuzzyIndexes.SourceWordBased, BuiltinRecognizers.RecognizeAll, TokenizerFlags.DefaultFlags, WordCountFlags.DefaultFlags);
        tm.FieldDefinitions.Add(new FieldDefinition("Projekt", FieldValueType.SingleString));
        var subject = new FieldDefinition("Fachgebiet", FieldValueType.MultiplePicklist);
        subject.PicklistItems.Add("Patents"); subject.PicklistItems.Add("Chemistry");
        tm.FieldDefinitions.Add(subject);
        tm.FieldDefinitions.Add(new FieldDefinition("PO-number", FieldValueType.SingleString));
        tm.Save();
        var tu = new TranslationUnit(Seg(NlNl, "De reactor wordt verwarmd."), Seg(EnGb, "The reactor is heated."));
        tu.FieldValues.Add(new SingleStringFieldValue("Projekt", "PROJ-001"));
        var fs = new MultiplePicklistFieldValue("Fachgebiet"); fs.Add("Patents"); fs.Add("Chemistry");
        tu.FieldValues.Add(fs);
        tu.FieldValues.Add(new SingleStringFieldValue("PO-number", "PO 123"));
        tm.LanguageDirection.AddTranslationUnitsMasked(new[] { tu }, new ImportSettings(), new[] { true });
        // What Studio itself keeps as the unit's dates and users: EverTM must take exactly those.
        var it = new RegularIterator { MaxCount = 10 };
        var stored = tm.LanguageDirection.GetTranslationUnits(ref it).Single();

        var lib = VaultLibrary.For(root);
        var r = lib.Import((store, p, c) => SuperVaultImport.Sdltm(store, tm.FilePath, p, c), null, CancellationToken.None);
        Check(r.Added == 1, "Studio TM imported: " + r);
        var vault = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
        var hit = Search(vault, "De reactor wordt verwarmd.", NlNl, EnGb).Results.FirstOrDefault();
        Check(Field(hit, "Project") == "PROJ-001", "a TM's \"Projekt\" field kept as Project: " + Field(hit, "Project"));
        Check(Field(hit, "Subject") == "Chemistry|Patents", "a \"Fachgebiet\" picklist kept as Subject: " + Field(hit, "Subject"));
        Check(Field(hit, "Source") == "agency.sdltm", "Source: the TM's file name");
        Check(hit?.MemoryTranslationUnit.FieldValues.Lookup("PO-number") == null, "a field EverTM doesn't keep (PO-number) is dropped");
        var sf = hit?.MemoryTranslationUnit.SystemFields;
        Check(sf != null && Math.Abs((sf.CreationDate - stored.SystemFields.CreationDate.ToUniversalTime()).TotalSeconds) < 1 && sf.CreationUser == stored.SystemFields.CreationUser,
              $"the TM unit's own created date and user: {sf?.CreationDate:u} {sf?.CreationUser} (Studio's: {stored.SystemFields.CreationDate.ToUniversalTime():u} {stored.SystemFields.CreationUser})");
    }

    /// <summary>A TMX with fields and dates: what Studio's Translation Results field column and info bar read.</summary>
    static void MatchesCarryFieldsAndDates(string root)
    {
        var tmx = Path.Combine(_scratch, "acme.tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            "<tu creationdate=\"20210304T050607Z\" creationid=\"jan\" changedate=\"20220304T050607Z\" changeid=\"piet\"><prop type=\"Project\">PROJ-001</prop><prop type=\"Client\">Acme</prop><prop type=\"x-domain\">Patents</prop>" +
            "<tuv xml:lang=\"nl-NL\"><seg>De klep opent langzaam.</seg></tuv><tuv xml:lang=\"en-GB\"><seg>The valve opens slowly.</seg></tuv></tu>" +
            "<tu creationdate=\"20190101T000000Z\"><tuv xml:lang=\"nl-NL\"><seg>De klep opent langzaam.</seg></tuv><tuv xml:lang=\"en-GB\"><seg>The valve slowly opens.</seg></tuv></tu>" +
            "</body></tmx>");
        var lib = VaultLibrary.For(root);
        lib.Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);
        var vault = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
        var r = Search(vault, "De klep opent langzaam.", NlNl, EnGb);
        var top = r.Results.FirstOrDefault();
        var names = top?.MemoryTranslationUnit.FieldValues.Select(f => f.Name).ToList() ?? new List<string>();
        Check(names.Take(3).SequenceEqual(new[] { "Project", "Client", "Subject" }), "Studio shows the first three fields: Project, Client, Subject (" + string.Join(", ", names) + ")");
        Check(Field(top, "Older translations") == "1", "plus \"Older translations: 1\"");
        var sf = top?.MemoryTranslationUnit.SystemFields;
        Check(sf != null && sf.ChangeDate == new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc) && sf.ChangeUser == "piet" && sf.CreationUser == "jan",
              $"the info bar's dates and users are the unit's: changed {sf?.ChangeDate:u} by {sf?.ChangeUser}, created by {sf?.CreationUser}");
        Check(r.Results.Count(x => x.ScoringResult.Match >= 99) == 1 && Text(top?.TranslationProposal?.TargetSegment) == "The valve opens slowly.",
              "through Studio's Cascade: one match, the newest translation");
        // Concordance shows the same.
        var conc = vault.SearchText(new SearchSettings { Mode = SearchMode.ConcordanceSearch, MinScore = 50, MaxResults = 10 }, "klep opent");
        Check(conc.Results.Count == 1 && Field(conc.Results[0], "Project") == "PROJ-001", "Concordance: the newest only, with its fields");
    }

    static void NewestAndVariantThroughCascade(string root)
    {
        var tmx = Path.Combine(_scratch, "variants.tmx");
        File.WriteAllText(tmx, "<?xml version=\"1.0\" encoding=\"utf-8\"?><tmx version=\"1.4\"><header srclang=\"nl-NL\" segtype=\"sentence\" datatype=\"plaintext\" o-tmf=\"t\" adminlang=\"en\" creationtool=\"t\" creationtoolversion=\"1\"/><body>" +
            "<tu creationdate=\"20200101T000000Z\"><tuv xml:lang=\"nl-NL\"><seg>De kleur van de behuizing.</seg></tuv><tuv xml:lang=\"en-GB\"><seg>The colour of the housing.</seg></tuv></tu>" +
            "<tu creationdate=\"20250101T000000Z\"><tuv xml:lang=\"nl-NL\"><seg>De kleur van de behuizing.</seg></tuv><tuv xml:lang=\"en-US\"><seg>The color of the housing.</seg></tuv></tu>" +
            "</body></tmx>");
        VaultLibrary.For(root).Import((store, p, c) => VaultTmx.Import(store, tmx, p, c), null, CancellationToken.None);
        var provider = new SuperVaultProvider(root);
        string Best(CultureCode target) => Text(Search(provider.GetLanguageDirection(new LanguagePair(NlNl, target)), "De kleur van de behuizing.", NlNl, target).Results.FirstOrDefault()?.TranslationProposal?.TargetSegment);
        Check(Best(EnGb) == "The colour of the housing.", "an en-GB project gets the en-GB translation, though older: " + Best(EnGb));
        Check(Best(EnUs) == "The color of the housing.", "an en-US project gets the en-US one: " + Best(EnUs));
    }

    /// <summary>A confirm as Studio's Editor hands it over: the project's name and customer from its .sdlproj, the subject from its update fields.</summary>
    static void ConfirmedSegmentsAreTagged(string root)
    {
        var project = Path.Combine(_scratch, "PROJ-001");
        Directory.CreateDirectory(Path.Combine(project, "en-GB"));
        File.WriteAllText(Path.Combine(project, "PROJ-001.sdlproj"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Project Guid=\"x\"><LanguageDirections/><GeneralProjectInfo Name=\"ACME_Globex (PROJ-001)\" Status=\"Started\"/><Customer Guid=\"y\" Name=\"Acme\" Email=\"\"/></Project>");
        var tu = new TranslationUnit(Seg(NlNl, "De ventilator draait sneller."), Seg(EnGb, "The fan turns faster."));
        tu.SystemFields.ChangeUser = "translator";
        tu.DocumentProperties = new DocumentProperties { LastOpenedAsPath = Path.Combine(project, "en-GB", "manual.docx.sdlxliff") };
        tu.FileProperties = new FileProperties { FileConversionProperties = new PersistentFileConversionProperties { OriginalFilePath = @"C:\Jobs\manual.docx" } };
        var settings = new ImportSettings { ProjectSettings = new FieldValues() };
        settings.ProjectSettings.Add(new SingleStringFieldValue("Subject", "Patents"));
        var vault = new SuperVaultProvider(root).GetLanguageDirection(new LanguagePair(NlNl, EnGb));
        var sw = Stopwatch.StartNew();
        vault.AddOrUpdateTranslationUnitsMasked(new[] { tu }, new[] { 0 }, settings, new[] { true });
        Check(sw.ElapsedMilliseconds < 100, $"confirm returns at once ({sw.ElapsedMilliseconds} ms; the .sdlproj is read on the learning thread)");
        SearchResult hit = null;
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed.TotalSeconds < 20)
        {
            hit = Search(vault, "De ventilator draait sneller.", NlNl, EnGb).Results.FirstOrDefault();
            if (Field(hit, "Project") != "") break;
            Thread.Sleep(200);
        }
        Check(Field(hit, "Project") == "ACME_Globex (PROJ-001)", "Project: the Studio project's name, from its .sdlproj: " + Field(hit, "Project"));
        Check(Field(hit, "Client") == "Acme", "Client: the project's customer: " + Field(hit, "Client"));
        Check(Field(hit, "End client") == "Globex", "End client: from the CLIENT_Endclient project name");
        Check(Field(hit, "Subject") == "Patents", "Subject: the project's field value on update");
        Check(Field(hit, "Document") == "manual.docx", "Document: the original file's name");
        Check(hit?.MemoryTranslationUnit.SystemFields.ChangeUser == "translator", "changed by the Studio user who confirmed it");
    }
}
