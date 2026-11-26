# list_terms and the one-name-per-termbase rule (MCP), checked against the built
# plugin DLL, outside Studio.
#
#  A  TermbaseCatalog.Resolve: by id, name, file name (with/without extension), full
#     path, the old lookup_term " [Trados project termbase]" suffix, any case; an
#     unknown, empty or shared name is an ERROR that lists what is available - never
#     a silent empty result (the check_terminology filter bug).
#  B  A Supervertaler termbase: entries, synonyms, dates; paging (truncated /
#     nextOffset / past the end), 'contains' (case-insensitive, non-ASCII, synonyms),
#     limit capped, an empty termbase.
#  C  A Trados .ttb: concept XML -> terms per language (third language ignored),
#     synonyms, definition/domain/other fields, created by the entry, modified by the
#     LATEST term or entry change, a malformed entry, a source-only entry, duplicate
#     concepts with their own ids; reading every page gives every entry exactly once.
#     TtbReader.LoadAllTerms carries each entry's ConceptId (lookup_term's entryId).
#  D  Failure modes: an empty .ttb, a missing file, a file another program holds
#     exclusively, a language the termbase lacks, a .ttb with uncommitted WAL changes
#     (read through the snapshot, original untouched).
#  E  Read-only: the termbase files and the database are byte-identical afterwards,
#     and no -wal/-shm/-journal file appears beside a termbase.
#  F  Scale: a -Scale-entry .ttb (default 100,000): the last page and a filtered page
#     each take one page's work, not the whole termbase's.
#  G  (32-bit with the Jet engine only) a .sdltb: a COPY of Studio 2024's own sample
#     Printer.sdltb, paged and filtered; held exclusively -> an error; missing -> an error.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\list_terms_test.ps1
#   ... -Dll <path to Supervertaler.Trados.dll>   (default: this repo's build)
#   ... -Scale 100000
# 32-bit builds (Studio 2024 / 2022) need the 32-bit PowerShell, which also runs part G:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\list_terms_test.ps1 -Dll <...\bin\Studio18\Debug\Supervertaler.Trados.dll>
#
# Every fixture is generated in %TEMP% and deleted afterwards; the data folder points
# at %TEMP% (SupervertalerPaths.Set) and the real settings and diagnostic log are
# checked unchanged at the end.
param(
    [string]$Dll,
    [int]$Scale = 100000,
    [string]$SdltbSample = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) "Studio 2024\Projects\Samples\SampleProject\Termbase\Printer.sdltb")
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dll) {
    $want = if ([IntPtr]::Size -eq 8) { @('Studio19\Debug', 'Studio19\Release') } else { @('Studio18\Debug', 'Studio18\Release', 'Studio17\Debug', 'Studio17\Release') }
    foreach ($c in $want) {
        $p = Join-Path $root "src\Supervertaler.Trados\bin\$c\Supervertaler.Trados.dll"
        if (Test-Path $p) { $Dll = $p; break }
    }
}
if (-not $Dll -or -not (Test-Path $Dll)) { throw "Build the plugin first (no Supervertaler.Trados.dll found)" }
$Dll = (Resolve-Path -LiteralPath $Dll).Path
$studio = if ($Dll -match '\\Studio19\\') { "C:\Program Files\Trados\Trados Studio\Studio19" }
          elseif ($Dll -match '\\Studio17\\') { "C:\Program Files (x86)\Trados\Trados Studio\Studio17" }
          else { "C:\Program Files (x86)\Trados\Trados Studio\Studio18" }
Write-Host "Plugin: $Dll ($([IntPtr]::Size * 8)-bit process)"

$real = @()
$cfg = Join-Path $env:APPDATA "Supervertaler\config.json"
$real += $cfg
if (Test-Path -LiteralPath $cfg) {
    try {
        $dataDir = (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json).user_data_path
        if ($dataDir) {
            $real += (Join-Path $dataDir "trados\settings\settings.json")
            $real += (Join-Path $dataDir "trados\logs\diagnostic.log")
            $real += (Join-Path $dataDir "trados\diagnostic.log")
        }
    } catch { }
}
function Snap($paths) { $paths | ForEach-Object { if (Test-Path -LiteralPath $_) { $i = Get-Item -LiteralPath $_; "$_|$($i.LastWriteTimeUtc.Ticks)|$($i.Length)" } else { "$_|absent" } } }
$before = Snap $real

$temp = Join-Path $env:TEMP ("sv-list-terms-test-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $temp | Out-Null

$binDir = Split-Path $Dll
[void][Reflection.Assembly]::LoadFrom($Dll)
Add-Type -ReferencedAssemblies @('System.Runtime.Serialization', 'System.Core', 'Microsoft.CSharp', 'System.Xml', 'System.Xml.Linq', $Dll) -TypeDefinition @"
using System; using System.IO; using System.Linq; using System.Reflection; using System.Collections; using System.Collections.Generic;
using System.Diagnostics; using System.Security.Cryptography; using System.Text;
using Supervertaler.Trados.Core;

public static class ListTermsTest {
    public static int Pass, Fail;
    static string[] _dirs;
    static Assembly P;
    static Type RefT, CatalogT, ListingT, ConnT, PathsT;
    const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static void Install(string[] dirs) { _dirs = dirs; AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object s, ResolveEventArgs e) {
        var n = new AssemblyName(e.Name).Name;
        foreach (var d in _dirs) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
    static void Check(bool ok, string label) {
        if (ok) { Pass++; Console.WriteLine("  ok   " + label); }
        else { Fail++; Console.WriteLine("  FAIL " + label); }
    }

    // ── reflection over the plugin's internal types ──
    static object Ref(long id, string name, string kind, string file, string src, string tgt) {
        var r = Activator.CreateInstance(RefT);
        RefT.GetField("Id").SetValue(r, id); RefT.GetField("Name").SetValue(r, name);
        RefT.GetField("Kind").SetValue(r, kind); RefT.GetField("FilePath").SetValue(r, file);
        RefT.GetField("SourceLang").SetValue(r, src); RefT.GetField("TargetLang").SetValue(r, tgt);
        return r;
    }
    static IList Refs(params object[] refs) {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(RefT));
        foreach (var r in refs) list.Add(r);
        return list;
    }
    static object ResolveTb(IList all, string wanted, out string error) {
        var args = new object[] { all, wanted, null };
        var r = CatalogT.GetMethod("Resolve", BF).Invoke(null, args);
        error = (string)args[2];
        return r;
    }
    static long IdOf(object r) { return (long)RefT.GetField("Id").GetValue(r); }
    static BridgeTermListResponse List(IList all, string db, string wanted, int? offset, int? limit, string contains) {
        return (BridgeTermListResponse)ListingT.GetMethod("BuildResponse", BF)
            .Invoke(null, new object[] { all, db, wanted, offset, limit, contains });
    }

    // ── SQLite fixtures (dynamic: no compile-time reference to Microsoft.Data.Sqlite) ──
    static dynamic Open(string path) {
        dynamic cn = Activator.CreateInstance(ConnT, "Data Source=" + path + ";Pooling=False");
        cn.Open(); return cn;
    }
    static void Exec(dynamic cn, string sql, params object[] kv) {
        dynamic cmd = cn.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i + 1 < kv.Length; i += 2) cmd.Parameters.AddWithValue((string)kv[i], kv[i + 1] ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery(); cmd.Dispose();
    }
    static long Scalar(dynamic cn, string sql) { dynamic cmd = cn.CreateCommand(); cmd.CommandText = sql; object v = cmd.ExecuteScalar(); cmd.Dispose(); return Convert.ToInt64(v); }

    static void CreateTtbSchema(dynamic cn) {
        Exec(cn, "CREATE TABLE mtIndexes (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Locale TEXT NOT NULL)");
        Exec(cn, "CREATE TABLE mtConcepts (ConceptId INTEGER PRIMARY KEY AUTOINCREMENT, Text TEXT NOT NULL, Incomplete INTEGER NOT NULL)");
        Exec(cn, "CREATE TABLE mtTerms (TermId INTEGER PRIMARY KEY AUTOINCREMENT, TermText TEXT NOT NULL, IndexId INTEGER NOT NULL, ConceptId INTEGER NOT NULL)");
        Exec(cn, "CREATE INDEX idx_terms_conceptid ON mtTerms(ConceptId)");
        Exec(cn, "INSERT INTO mtIndexes (Id, Name, Locale) VALUES (1,'Dutch','NL'), (2,'English','EN'), (3,'German','DE')");
    }
    static string Tr(string type, string user, string date) {
        return "<trG><tr type=\"" + type + "\">" + user + "</tr><dt>" + date + "</dt></trG>";
    }
    static string Lg(string locale, string name, string inner) { return "<lG><l lang=\"" + locale + "\" type=\"" + name + "\"/>" + inner + "</lG>"; }
    static string Tg(string term, string extra) { return "<tG><t>" + System.Security.SecurityElement.Escape(term) + "</t>" + (extra ?? "") + "</tG>"; }
    static void AddConcept(dynamic cn, long id, string xml, params object[] terms) {
        Exec(cn, "INSERT INTO mtConcepts (ConceptId, Text, Incomplete) VALUES (@id, @x, 0)", "@id", id, "@x", xml);
        for (int i = 0; i + 1 < terms.Length; i += 2)
            Exec(cn, "INSERT INTO mtTerms (TermText, IndexId, ConceptId) VALUES (@t, @ix, @c)", "@t", terms[i + 1], "@ix", terms[i], "@c", id);
    }
    static string Plain(long id, string nl, string en) {
        return "<cG><c>" + id + "</c>" + Tr("origination", "alice", "2026-01-01T09:00:00") + Tr("modification", "alice", "2026-01-01T09:00:00")
             + Lg("NL", "Dutch", Tg(nl, null)) + Lg("EN", "English", Tg(en, null)) + "</cG>";
    }

    static string Hash(string path) { using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(s)); }
    static string[] Sidecars(string path) { return new[] { "-wal", "-shm", "-journal" }.Where(x => File.Exists(path + x)).ToArray(); }

    public static void Run(string temp, int scale, string sdltbSample) {
        P = typeof(BridgeTermListResponse).Assembly;
        RefT = P.GetType("Supervertaler.Trados.Core.TermbaseRef", true);
        CatalogT = P.GetType("Supervertaler.Trados.Core.TermbaseCatalog", true);
        ListingT = P.GetType("Supervertaler.Trados.Core.TermbaseListing", true);
        PathsT = P.GetType("Supervertaler.Core.SupervertalerPaths", true);
        PathsT.GetMethod("Set", BF).Invoke(null, new object[] { temp });
        var sqlAsm = Assembly.LoadFrom(Path.Combine(_dirs[0], "Microsoft.Data.Sqlite.dll"));
        var bat = Assembly.LoadFrom(Path.Combine(_dirs[0], "SQLitePCLRaw.batteries_v2.dll"));
        bat.GetType("SQLitePCL.Batteries_V2").GetMethod("Init").Invoke(null, null);
        ConnT = sqlAsm.GetType("Microsoft.Data.Sqlite.SqliteConnection", true);

        // ── fixtures ──
        var db = Path.Combine(temp, "supervertaler.db");
        typeof(TermbaseReader).GetMethod("CreateDatabase", BF).Invoke(null, new object[] { db });
        long acmeId, emptyId, sharedSvId;
        using (dynamic cn = Open(db)) {
            Exec(cn, "INSERT INTO termbases (name, source_lang, target_lang) VALUES ('Acme Client', 'nl', 'en')");
            acmeId = Scalar(cn, "SELECT last_insert_rowid()");
            Exec(cn, "INSERT INTO termbases (name, source_lang, target_lang) VALUES ('Empty TB', 'nl', 'en')");
            emptyId = Scalar(cn, "SELECT last_insert_rowid()");
            Exec(cn, "INSERT INTO termbases (name, source_lang, target_lang) VALUES ('Shared Name', 'nl', 'en')");
            sharedSvId = Scalar(cn, "SELECT last_insert_rowid()");
            var ins = "INSERT INTO termbase_terms (source_term, target_term, source_lang, target_lang, termbase_id, definition, notes, created_date, modified_date) VALUES (@s, @t, 'nl', 'en', @tb, @d, @n, @c, @m)";
            Exec(cn, ins, "@s", "overstappen op", "@t", "transition to", "@tb", acmeId.ToString(), "@d", "Change of scheme", "@n", null, "@c", "2026-09-11 08:00:00", "@m", "2026-11-20 16:30:00");
            Exec(cn, ins, "@s", "het nieuwe pensioen", "@t", "the new pension", "@tb", acmeId.ToString(), "@d", null, "@n", "client preference", "@c", "2026-09-11 08:01:00", "@m", "2026-09-11 08:01:00");
            Exec(cn, ins, "@s", "Één loket", "@t", "single point of contact", "@tb", acmeId.ToString(), "@d", null, "@n", null, "@c", "2026-09-11 08:02:00", "@m", "2026-09-11 08:02:00");
            long firstId = Scalar(cn, "SELECT MIN(id) FROM termbase_terms");
            Exec(cn, "INSERT INTO termbase_synonyms (term_id, synonym_text, language, display_order) VALUES (@id, 'over gaan naar', 'source', 1)", "@id", firstId);
            Exec(cn, "INSERT INTO termbase_synonyms (term_id, synonym_text, language, display_order) VALUES (@id, 'switch to', 'target', 1)", "@id", firstId);
        }

        var ttb = Path.Combine(temp, "Acme Pensions 11-9-2026.ttb");
        int n = 1200;
        using (dynamic cn = Open(ttb)) {
            CreateTtbSchema(cn);
            Exec(cn, "BEGIN");
            // 1: synonyms, fields, a German term to ignore, a later term-level change by bob
            AddConcept(cn, 1, "<cG><c>1</c><dG><d type=\"Definition\">Building up pension rights</d></dG><dG><d type=\"Subject\">Pensions</d></dG>"
                + Tr("origination", "alice", "2026-09-11T08:00:00") + Tr("modification", "alice", "2026-09-11T08:00:00")
                + Lg("NL", "Dutch", Tg("pensioen opbouwen", Tr("modification", "alice", "2026-09-12T10:00:00")))
                + Lg("EN", "English", Tg("accrue pension", "<dG><d type=\"Status\">preferred</d></dG>" + Tr("modification", "bob", "2026-11-20T16:30:00")) + Tg("build up pension", null))
                + Lg("DE", "German", Tg("Rente aufbauen", null)) + "</cG>",
                1, "pensioen opbouwen", 2, "accrue pension", 2, "build up pension", 3, "Rente aufbauen");
            // 2 and 3: the same entry twice
            AddConcept(cn, 2, Plain(2, "dekkingsgraad", "coverage ratio"), 1, "dekkingsgraad", 2, "coverage ratio");
            AddConcept(cn, 3, Plain(3, "dekkingsgraad", "coverage ratio"), 1, "dekkingsgraad", 2, "coverage ratio");
            // 4: unreadable XML; 5: source only; 6: non-ASCII
            AddConcept(cn, 4, "<cG><c>4</c><lG>broken", 1, "kapot");
            AddConcept(cn, 5, "<cG><c>5</c>" + Lg("NL", "Dutch", Tg("alleen bron", null)) + "</cG>", 1, "alleen bron");
            AddConcept(cn, 6, Plain(6, "ÉÉN KEER", "once"), 1, "ÉÉN KEER", 2, "once");
            for (long i = 7; i <= n; i++) AddConcept(cn, i, Plain(i, "term " + i, "term-en " + i), 1, "term " + i, 2, "term-en " + i);
            Exec(cn, "COMMIT");
        }
        var emptyTtb = Path.Combine(temp, "Empty.ttb");
        using (dynamic cn = Open(emptyTtb)) CreateTtbSchema(cn);

        ConnT.GetMethod("ClearAllPools", BF).Invoke(null, null);
        string ttbHash = Hash(ttb), dbHash = Hash(db), emptyHash = Hash(emptyTtb);

        var svAcme = Ref(acmeId, "Acme Client", "supervertaler", null, "nl", "en");
        var svEmpty = Ref(emptyId, "Empty TB", "supervertaler", null, "nl", "en");
        var svShared = Ref(sharedSvId, "Shared Name", "supervertaler", null, "nl", "en");
        var tAcme = Ref(-4242, "Termbase Acme Pensions", "trados-ttb", ttb, "Dutch", "English");
        var tShared = Ref(-77, "Shared Name", "trados-ttb", emptyTtb, "Dutch", "English");
        var all = Refs(svAcme, svEmpty, svShared, tAcme, tShared);
        string err;

        Console.WriteLine("A  one name per termbase");
        Check(ResolveTb(all, "Termbase Acme Pensions", out err) == tAcme, "the project's name for a Trados termbase");
        Check(ResolveTb(all, "termbase acme PENSIONS", out err) == tAcme, "any case");
        Check(ResolveTb(all, "Acme Pensions 11-9-2026", out err) == tAcme, "its file name (what list_resources used to show)");
        Check(ResolveTb(all, "Acme Pensions 11-9-2026.ttb", out err) == tAcme, "its file name with extension");
        Check(ResolveTb(all, ttb, out err) == tAcme, "its full path");
        Check(ResolveTb(all, "Termbase Acme Pensions [Trados project termbase]", out err) == tAcme, "the old lookup_term name with its suffix");
        Check(ResolveTb(all, "-4242", out err) == tAcme, "a Trados termbase's (negative) id");
        Check(ResolveTb(all, acmeId.ToString(), out err) == svAcme, "a Supervertaler termbase's id");
        Check(ResolveTb(all, " Acme Client ", out err) == svAcme, "a Supervertaler termbase's name, trimmed");
        var none = ResolveTb(all, "Acme Pensioen", out err);
        Check(none == null && err.Contains("no termbase called 'Acme Pensioen'") && err.Contains("Termbase Acme Pensions (id -4242)") && err.Contains("Acme Client (id " + acmeId + ")"),
              "an unknown name is an error listing every termbase with its id");
        none = ResolveTb(all, "Shared Name", out err);
        Check(none == null && err.Contains("more than one") && err.Contains("id " + sharedSvId) && err.Contains("id -77"), "a shared name is refused with both ids");
        Check(ResolveTb(all, "-77", out err) == tShared, "...and the id picks one");
        none = ResolveTb(all, "  ", out err);
        Check(none == null && err.Contains("name the termbase"), "an empty name is an error");
        none = ResolveTb(all, "999999", out err);
        Check(none == null && err.Contains("no termbase called '999999'"), "an unknown id is an error");
        none = ResolveTb(Refs(), "Acme Client", out err);
        Check(none == null && err.Contains("No termbases are available"), "with no termbases at all, the error says so");

        Console.WriteLine("B  a Supervertaler termbase");
        var r = List(all, db, "Acme Client", null, null, null);
        Check(r.Ok && r.Total == 3 && r.Returned == 3 && !r.Truncated && r.NextOffset == null, "three entries, one page (" + r.Total + ")");
        Check(r.Termbase.Id == acmeId && r.Termbase.Name == "Acme Client" && r.Termbase.Kind == "supervertaler", "reports the termbase's id, name and kind");
        var e0 = r.Entries[0];
        Check(e0.Source.SequenceEqual(new[] { "overstappen op", "over gaan naar" }) && e0.Target.SequenceEqual(new[] { "transition to", "switch to" }),
              "main terms first, then synonyms, on the right side");
        Check(e0.Definition == "Change of scheme" && e0.Created == "2026-09-11T08:00:00" && e0.Modified == "2026-11-20T16:30:00", "definition and ISO dates");
        Check(r.Entries[1].Notes == "client preference" && r.Entries[0].Id < r.Entries[1].Id, "notes; id order");
        r = List(all, db, acmeId.ToString(), 0, 2, null);
        Check(r.Ok && r.Returned == 2 && r.Truncated && r.NextOffset == 2 && r.Note.Contains("offset=2"), "limit 2: truncated, nextOffset 2");
        r = List(all, db, "Acme Client", 2, 2, null);
        Check(r.Ok && r.Returned == 1 && !r.Truncated && r.NextOffset == null, "the last page is not truncated");
        r = List(all, db, "Acme Client", 10, 2, null);
        Check(r.Ok && r.Returned == 0 && r.Total == 3 && !r.Truncated && r.Note.Contains("past the end"), "an offset past the end: no entries, ok, and it says so");
        r = List(all, db, "Acme Client", null, null, "SWITCH");
        Check(r.Ok && r.Total == 1 && r.Entries[0].Source[0] == "overstappen op", "contains matches a synonym, ignoring case");
        r = List(all, db, "Acme Client", null, null, "één");
        Check(r.Ok && r.Total == 1 && r.Entries[0].Source[0] == "Één loket", "contains folds non-ASCII case");
        r = List(all, db, "Acme Client", null, null, "nothing like this");
        Check(r.Ok && r.Total == 0 && r.Note.Contains("No entry has"), "contains with no match: ok, total 0, says so");
        r = List(all, db, "Empty TB", null, null, null);
        Check(r.Ok && r.Total == 0 && r.Returned == 0 && r.Note.Contains("no entries"), "an empty termbase: ok, total 0, says so");
        r = List(all, db, "Acme Client", null, 9999, null);
        Check(r.Ok && r.Limit == 500 && r.Note.Contains("capped"), "a limit over 500 is capped and says so");
        r = List(all, db, "No Such TB", null, null, null);
        Check(!r.Ok && r.Error.Contains("no termbase called") && r.Entries == null, "an unknown termbase is an error, not an empty list");
        r = List(all, null, "Acme Client", null, null, null);
        Check(!r.Ok && r.Error.Contains("database was not found"), "no database: an error");

        Console.WriteLine("C  a Trados .ttb");
        r = List(all, db, "Termbase Acme Pensions", 0, 10, null);
        Check(r.Ok && r.Total == n && r.Returned == 10 && r.Truncated && r.NextOffset == 10, "total " + n + ", first page of 10");
        Check(r.Termbase.Kind == "trados-ttb" && r.Termbase.File == ttb && r.Termbase.Id == -4242, "kind, file and id");
        var c1 = r.Entries[0];
        Check(c1.Id == 1 && c1.Source.SequenceEqual(new[] { "pensioen opbouwen" }) && c1.Target.SequenceEqual(new[] { "accrue pension", "build up pension" }),
              "terms per language with synonyms; the German term is left out");
        Check(c1.Definition == "Building up pension rights" && c1.Domain == "Pensions" && c1.Fields != null && c1.Fields.Contains("Status (English): preferred"),
              "definition, domain, and other fields labelled by language");
        Check(c1.CreatedBy == "alice" && c1.Created == "2026-09-11T08:00:00", "created: the entry's origination");
        Check(c1.ModifiedBy == "bob" && c1.Modified == "2026-11-20T16:30:00", "modified: the latest change, even when only a term changed");
        Check(r.Entries[1].Id == 2 && r.Entries[2].Id == 3 && r.Entries[1].Source[0] == r.Entries[2].Source[0], "a duplicated concept shows twice, with its own ids");
        Check(r.Entries[3].Id == 4 && r.Entries[3].Source.Count == 0 && r.Entries[3].Notes.Contains("could not be read"), "an unreadable entry keeps its id and says so; the page survives");
        Check(r.Entries[4].Source[0] == "alleen bron" && r.Entries[4].Target.Count == 0, "a source-only entry is listed with no target");
        var seen = new List<long>(); int offset = 0; int calls = 0;
        while (true) {
            var pg = List(all, db, "-4242", offset, 500, null);
            if (!pg.Ok) { Check(false, "paging error: " + pg.Error); break; }
            seen.AddRange(pg.Entries.Select(x => x.Id)); calls++;
            if (!pg.Truncated) break;
            offset = pg.NextOffset.Value;
        }
        Check(calls == 3 && seen.Count == n && seen.Distinct().Count() == n && seen.SequenceEqual(seen.OrderBy(x => x)), "reading every page (3 calls) gives every entry once, in order");
        r = List(all, db, "Termbase Acme Pensions", null, null, "één keer");
        Check(r.Ok && r.Total == 1 && r.Entries[0].Id == 6, "contains folds non-ASCII case in a .ttb");
        r = List(all, db, "Termbase Acme Pensions", null, null, "COVERAGE");
        Check(r.Ok && r.Total == 2 && r.Entries.Select(x => x.Id).SequenceEqual(new long[] { 2, 3 }), "contains on the target side, both duplicates");
        r = List(all, db, "Termbase Acme Pensions", null, null, "Rente");
        Check(r.Ok && r.Total == 0, "contains ignores a third language");
        using (var reader = new TtbReader(ttb)) {
            reader.Open();
            var index = reader.LoadAllTerms("Dutch", "English", -4242, "Termbase Acme Pensions");
            var hits = index["dekkingsgraad"];
            Check(hits.Count == 2 && hits.Select(h => h.ConceptId).OrderBy(x => x).SequenceEqual(new long?[] { 2, 3 }), "TermLens entries carry their ConceptId (lookup_term's entryId)");
        }

        Console.WriteLine("D  failure modes");
        r = List(all, db, "-77", null, null, null);
        Check(r.Ok && r.Total == 0 && r.Note.Contains("no entries"), "an empty .ttb: ok, total 0");
        var missing = Ref(-5, "Gone", "trados-ttb", Path.Combine(temp, "gone.ttb"), "Dutch", "English");
        r = List(Refs(missing), db, "Gone", null, null, null);
        Check(!r.Ok && r.Error.Contains("missing"), "a missing .ttb: an error naming the file");
        var german = Ref(-6, "Wrong pair", "trados-ttb", ttb, "Dutch", "French");
        r = List(Refs(german), db, "Wrong pair", null, null, null);
        Check(!r.Ok && r.Error.Contains("no index for 'French'") && r.Error.Contains("Dutch, English, German"), "a language the termbase lacks: an error listing its languages");
        using (new FileStream(ttb, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            BridgeTermListResponse locked = null; Exception thrown = null;
            try { locked = List(all, db, "-4242", null, null, null); } catch (Exception ex) { thrown = ex; }
            Check(thrown == null && locked != null && !locked.Ok && !string.IsNullOrEmpty(locked.Error), "a .ttb another program holds exclusively: an error, not an exception (" + (locked == null ? "" : locked.Error) + ")");
        }
        var walTtb = Path.Combine(temp, "wal.ttb");
        using (dynamic w = Open(walTtb)) {
            CreateTtbSchema(w);
            Exec(w, "PRAGMA journal_mode=WAL"); Exec(w, "PRAGMA wal_autocheckpoint=0");
            AddConcept(w, 1, Plain(1, "nieuw", "new"), 1, "nieuw", 2, "new");
            var walRef = Ref(-8, "Live", "trados-ttb", walTtb, "Dutch", "English");
            var walBefore = File.Exists(walTtb + "-wal") ? new FileInfo(walTtb + "-wal").Length : -1;
            r = List(Refs(walRef), db, "Live", null, null, null);
            Check(r.Ok && r.Total == 1 && r.Entries[0].Target[0] == "new", "a .ttb with uncommitted WAL changes (Studio has it open): read through the snapshot, change included");
            Check(walBefore > 0 && new FileInfo(walTtb + "-wal").Length == walBefore, "...and its -wal file is left as it was");
        }

        Console.WriteLine("E  read-only");
        ConnT.GetMethod("ClearAllPools", BF).Invoke(null, null);
        Check(Hash(ttb) == ttbHash && Hash(emptyTtb) == emptyHash, "the .ttb files are byte-identical");
        Check(Hash(db) == dbHash, "the Supervertaler database is byte-identical");
        Check(Sidecars(ttb).Length == 0 && Sidecars(emptyTtb).Length == 0, "no -wal/-shm/-journal file appeared beside a .ttb");

        Console.WriteLine("F  scale: " + scale + " entries");
        var big = Path.Combine(temp, "big.ttb");
        using (dynamic cn = Open(big)) {
            CreateTtbSchema(cn);
            Exec(cn, "BEGIN");
            dynamic c = cn.CreateCommand();
            c.CommandText = "INSERT INTO mtConcepts (ConceptId, Text, Incomplete) VALUES (@id, @x, 0); " +
                            "INSERT INTO mtTerms (TermText, IndexId, ConceptId) VALUES (@s, 1, @id); " +
                            "INSERT INTO mtTerms (TermText, IndexId, ConceptId) VALUES (@t, 2, @id)";
            dynamic pid = c.Parameters.AddWithValue("@id", 0L); dynamic px = c.Parameters.AddWithValue("@x", "");
            dynamic ps = c.Parameters.AddWithValue("@s", ""); dynamic pt = c.Parameters.AddWithValue("@t", "");
            for (long i = 1; i <= scale; i++) {
                string s = (i % 100 == 0 ? "needle " : "bron ") + i, t = "target " + i;
                pid.Value = i; px.Value = Plain(i, s, t); ps.Value = s; pt.Value = t;
                c.ExecuteNonQuery();
            }
            c.Dispose();
            Exec(cn, "COMMIT");
        }
        var bigRef = Ref(-9, "Big", "trados-ttb", big, "Dutch", "English");
        List(Refs(bigRef), db, "Big", 0, 1, null);   // warm-up: JIT, SQLite engine
        var sw = Stopwatch.StartNew();
        r = List(Refs(bigRef), db, "Big", scale - 100, 100, null);
        var lastMs = sw.ElapsedMilliseconds;
        Check(r.Ok && r.Total == scale && r.Returned == 100 && !r.Truncated && r.Entries[99].Id == scale, "the last page of " + scale + " (" + lastMs + " ms)");
        sw.Restart();
        r = List(Refs(bigRef), db, "Big", 0, 500, "NEEDLE");
        var filtMs = sw.ElapsedMilliseconds;
        Check(r.Ok && r.Total == scale / 100 && r.Returned == Math.Min(500, scale / 100), "a filtered page: " + r.Total + " matches (" + filtMs + " ms)");
        Check(lastMs < 3000 && filtMs < 5000, "both well under a few seconds - one page's work, not the termbase's");

        if (IntPtr.Size == 4 && File.Exists(sdltbSample)) {
            Console.WriteLine("G  a .sdltb (copy of Studio's sample)");
            var sd = Path.Combine(temp, "sample.sdltb");
            File.Copy(sdltbSample, sd);
            var sdHash = Hash(sd);
            var sdRef = Ref(-10, "Sample", "multiterm", sd, "English", "German");
            r = List(Refs(sdRef), db, "Sample", 0, 5, null);
            Check(r.Ok && r.Total > 5 && r.Returned == 5 && r.Truncated, ".sdltb: " + (r.Ok ? r.Total + " entries, first page of 5" : r.Error));
            if (r.Ok) {
                Check(r.Entries.All(x => x.Id > 0) && r.Entries.Any(x => x.Source.Count > 0 && x.Target.Count > 0), ".sdltb: entries with ids, English and German terms");
                Check(r.Entries.Any(x => x.CreatedBy != null && x.Modified != null), ".sdltb: created/modified users and dates");
                int total = r.Total; var all2 = new List<long>(); int off = 0;
                while (true) { var pg = List(Refs(sdRef), db, "Sample", off, 7, null); all2.AddRange(pg.Entries.Select(x => x.Id)); if (!pg.Truncated) break; off = pg.NextOffset.Value; }
                Check(all2.Count == total && all2.Distinct().Count() == total, ".sdltb: every page together gives every entry once");
                var first = r.Entries.First(x => x.Source.Count > 0).Source[0];
                var probe = first.Substring(0, Math.Min(4, first.Length)).ToUpperInvariant();
                var f = List(Refs(sdRef), db, "Sample", null, null, probe);
                Check(f.Ok && f.Total >= 1 && f.Total < total, ".sdltb: contains '" + probe + "' narrows the list (" + (f.Ok ? f.Total.ToString() : f.Error) + ")");
                r = List(Refs(sdRef), db, "Sample", total + 5, 5, null);
                Check(r.Ok && r.Returned == 0 && r.Note.Contains("past the end"), ".sdltb: past the end says so");
            }
            using (new FileStream(sd, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                BridgeTermListResponse locked = null; Exception thrown = null;
                try { locked = List(Refs(sdRef), db, "Sample", null, null, null); } catch (Exception ex) { thrown = ex; }
                Check(thrown == null && locked != null && !locked.Ok && locked.Error.Contains("could not open"), ".sdltb held exclusively: an error, not an exception");
            }
            Check(Hash(sd) == sdHash, ".sdltb: byte-identical afterwards");
            var gone = Ref(-11, "Gone", "multiterm", Path.Combine(temp, "gone.sdltb"), "English", "German");
            r = List(Refs(gone), db, "Gone", null, null, null);
            Check(!r.Ok && r.Error.Contains("missing"), ".sdltb missing: an error naming the file");
        } else {
            Console.WriteLine("G  .sdltb: skipped (needs the 32-bit PowerShell and " + sdltbSample + ")");
        }
        ConnT.GetMethod("ClearAllPools", BF).Invoke(null, null);
    }
}
"@

[ListTermsTest]::Install([string[]]@($binDir, $studio))
try {
    [ListTermsTest]::Run($temp, $Scale, $SdltbSample)
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
$after = Snap $real
if ((Compare-Object $before $after) -eq $null) { [ListTermsTest]::Pass++; Write-Host "  ok   real settings and diagnostic log unchanged" }
else { [ListTermsTest]::Fail++; Write-Host "  FAIL real files changed:"; Compare-Object $before $after | Out-Host }
Write-Host ""
Write-Host ("{0} passed, {1} failed" -f [ListTermsTest]::Pass, [ListTermsTest]::Fail)
if ([ListTermsTest]::Fail -gt 0) { exit 1 }
