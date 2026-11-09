# SuperVault metadata: keeping, showing and filtering on it

Design note, 9 Nov 2026. Status: proposal, no code yet. Base: Trados main a01b262, Plugin Core main 69f9aa1.

## In short

- **Studio already does most of the showing for us.** If SuperVault puts field values on its matches, Studio's
  Translation Results and Concordance windows show them (up to three per match by default, the rest in a tooltip).
  If it fills in a match's created/changed date and user, Studio's info bar under the results shows them. No
  SuperVault UI is needed for this.
- **Studio hands SuperVault the project's TM filters on every lookup**, but leaves applying them to each TM.
  SuperVault would apply them itself, inside its index search.
- **Studio's Filters page can't offer SuperVault's own field names** unless SuperVault pretends to be a full Studio TM
  (a large interface, with unknown side effects). So per-project rules are best set in SuperVault's own settings
  window, which Studio already saves per project.
- **Michael's real data is mostly bare, but the metadata that exists is valuable and cheap to keep.** In 282 of his
  TMX files (890,000 units), Project, Client and Subject are filled on 10–13% of the units, with few distinct values
  (285 projects, 55 clients, 15 subjects). His Workbench database holds only TM names: its project, user and note
  columns are empty in all 1.55 million rows.
- **Recommendation:** keep a short default set (Project, Client, Subject, Domain, Document, Source, created/changed
  date and user), not every field. First phase: keep and show these, learn Project and Client from the Studio
  project, and add them to existing units on re-import. Filtering comes second.

## 1. Which fields to keep

### What real files contain (read-only survey of Michael's files, 9 Nov)

TMX: 282 readable files under `D:\SynologyDrive\Jobs\`, 890,000 units in all. Field names below are as the tools
write them.

| Tool (files) | Unit attributes | Unit-level `<prop>` types |
|---|---|---|
| CafeTran (102 files, 597k units) | `creationdate`, `creationid`, `tuid` | `Project`, `Client`, `Subject` (on ~5%), `target_tags`; 69 `<note>`s |
| memoQ 9–12 (≈30 files) | `creationdate`, `creationid`, `changedate`, `changeid` | `client`, `project`, `domain`, `subject` on every unit (often empty), `corrected`, `aligned`, `x-document`; context as `x-context-pre`/`-post` on the `<tuv>` |
| Studio ("SDL Language Platform", 12 files) | `creationdate`, `creationid`, `changedate`, `changeid`, `lastusagedate`, `usagecount` | technical: `x-LastUsedBy`, `x-Origin`, `x-ConfirmationLevel`, `x-OriginalFormat`, `x-Context`, `x-ContextContent`, `x-StructureContext:MultipleString`; custom fields as `x-<Name>:<Type>`, e.g. `x-SourceFile:SingleString`, `x-Quality:Integer`, a picklist `x-<name>:MultiplePicklist` |
| DéjàVu (1 file) | none on `<tu>`; `creationdate`/`creationid` on each `<tuv>` | `x-client`, `x-project`, `x-domain`, `x-filename`, `x-idiom-tm-uda-*`, `x-rowid` |
| Olifant, LogiTerm, MateCat, Smartcat, Lilt, Logoport | mixed | LogiTerm: `Project`/`Client`/`Subject` + `ltattr-match`/`ltattr-score`; MateCat: `x-MateCAT-filename`, `x-MateCAT-id_*` |
| Supervertaler Workbench exports (≈85 files) | none | none |

Fill rates across all 890,000 units (non-empty values; distinct values counted, not shown):

| Field (any spelling) | Units with a value | Distinct values |
|---|---|---|
| project | 113,243 | 285 |
| client | 95,922 | 55 |
| subject | 86,483 | 15 |
| domain | 32,376 | 59 |
| document / file name | 80,006 | 278 |

Studio TMs: the six largest readable `.sdltm` files (77,000 units; read from copies in the scratchpad):

- System fields on every unit: creation date and user, change date and user, last-used date and user, usage count.
  Users per TM: 2–19 (agency TMs carry other translators' and reviewers' user names).
- Custom fields, all text in this sample (17 fields in 6 TMs), named by whoever set up the TM, sometimes in German:
  `Origin`, `PO-number`, `Product`; `Projekt`, `Datei` (file), `Lektor` (reviewer), `Übersetzer` (translator). Plus
  Studio's own `StructureContext`.
- Studio's field types: single text, multiple text, single picklist, multiple picklist, date, number.

Workbench (`D:\Supervertaler\resources\supervertaler.db`):

- The TM name (64 TMs) is the only real metadata. Names are often a client or project name.
- `created_date`/`modified_date` are when the unit entered Workbench (Nov 2025 onwards), not when it was translated.
- `project_id`, `created_by`, `notes`, `context_before`/`context_after`: empty in all 1,554,005 units.

### What Studio can tell us at confirm time

Read from Studio 2026's Editor (`TranslationUpdate.AddSegment`, `InteractiveTmUpdateImportSettingsBuilder`):

- `TranslationUnit.SystemFields`: the Studio user and the dates (Studio sets the current user,
  `OverrideTuUserIdWithCurrentContextUser`).
- `TranslationUnit.DocumentProperties` / `FileProperties`: the document's path and original file name.
- `ImportSettings.ProjectSettings`: the field values the project says to add on update (Project Settings >
  Translation Memory > Update). SuperVault receives these today and ignores them.
- **Not given:** the Studio project's name or customer. SuperVault can find them by walking up from the document path
  to the folder holding the `.sdlproj` and reading its `ProjectInfo` (the plugin already parses that XML in
  `TradosTools.cs`). This works for confirms and for the batch task "Update Main Translation Memories" alike, and
  doesn't depend on which document is active.

### Proposal

**Default set (recommended).** Canonical fields, with the spellings mapped onto them (case-insensitive, `x-` and
Studio's `:Type` suffix stripped):

| Field | From TMX props | From Studio TM fields | From Workbench | Learned at confirm |
|---|---|---|---|---|
| Project | `project`, `Project`, `x-project`, `Projekt` | Project, Projekt, Job | – | the Studio project's name |
| Client | `client`, `Client`, `x-client`, `End_client`, `Customer`, `Kunde` | the same names | – | the Studio project's Customer, if set |
| Subject | `subject`, `Subject`, `x-Subject` | Subject, Fachgebiet | – | from the project's "field values on update", if named Subject |
| Domain | `domain`, `x-domain` | Domain | – | the same |
| Document | `x-document`, `x-filename`, `x-MateCAT-filename`, `x-SourceFile` | Datei, SourceFile | – | the document's file name |
| Source | the TMX file name | the `.sdltm` name | the Workbench TM name | "Studio: <project name>" |
| Created / changed (date, user) | `creationdate`/`creationid`, `changedate`/`changeid` (on `<tu>`, or on the `<tuv>` as DéjàVu writes) | system fields | – (Workbench dates are import dates: not kept) | the TU's system fields |
| Note | `<note>` (rare: 69 in 890k) | – | – | – |

**Keep every field (option, later).** Every prop and custom field that isn't on a technical list. The technical
list is never kept: `x-Context`, `x-ContextContent`, `x-StructureContext`, `x-Origin`, `x-ConfirmationLevel`,
`x-LastUsedBy`, `x-OriginalFormat`, `lastusagedate`, `usagecount`, `ltattr-*`, `target_tags`,
`x-context-pre`/`-post`, `x-MateCAT-id_*`, `x-rowid`, `corrected`, `aligned`, `tuid`.

**Recommendation: the default set.** Michael's examples (Subject, Project) are in it. Most other fields in his data
are noise for a lookup (agency PO numbers, per-unit ids, reviewer names). Some fields have a different value on every
unit, which costs one index term per unit. A 10M-unit SuperVault shouldn't pay that for fields nobody filters on.
"Keep every field" can come later, if he finds a field he misses.

## 2. Storage, with duplicates stored once

A unit is stored once per language pair, whatever its direction and variants. With metadata, one unit can come from
several projects, clients and subjects.

**Merge rules** (when an import or a confirmation brings a unit SuperVault already holds):

- Text fields (Project, Client, Subject, Domain, Document, Source, Note): **the union of values**. At most 20 values
  per field per unit, keeping the newest. Very common segments ("Figure 1", "Claims") would otherwise collect
  hundreds of project names.
- Created date and user: **the earliest** seen. Changed date and user: **the latest** seen.
- A corrected learned translation (today "replaced") keeps the old unit's values. When the learned unit is removed
  because it matches an imported one, its values move to the imported unit.

**Schema 3** (in place, milliseconds, like schema 2; nothing reads the existing units):

```
ALTER TABLE units ADD COLUMN created INTEGER;     -- seconds since 1970, UTC; NULL = unknown
ALTER TABLE units ADD COLUMN changed INTEGER;
ALTER TABLE units ADD COLUMN created_by INTEGER;  -- vals.id
ALTER TABLE units ADD COLUMN changed_by INTEGER;
CREATE TABLE vals (id INTEGER PRIMARY KEY, field TEXT NOT NULL, value TEXT NOT NULL, UNIQUE (field, value));
CREATE TABLE unit_vals (unit_id INTEGER NOT NULL, val_id INTEGER NOT NULL, added INTEGER,
                        PRIMARY KEY (unit_id, val_id)) WITHOUT ROWID;
CREATE INDEX unit_vals_val ON unit_vals (val_id);
CREATE TRIGGER unit_vals_ai AFTER INSERT ON unit_vals BEGIN INSERT INTO changes (unit_id) VALUES (new.unit_id); END;
CREATE TRIGGER unit_vals_ad AFTER DELETE ON unit_vals BEGIN INSERT INTO changes (unit_id) VALUES (old.unit_id); END;
```

- Values are stored once (`vals`). A unit refers to them by number. With 285 projects and 55 clients this stays
  small.
- The triggers feed the existing change log, so the index picks up metadata changes piece by piece, as it does for
  learned units. A big re-import still triggers a full rebuild (the existing 25% rule).
- Size estimate at 10M units, assuming 1.5 values per unit as in Michael's TMX data (most units have none): about
  15M `unit_vals` rows, about 150–200 MB in `supervault.db`.

**Existing units and re-importing.** Existing units have no metadata. Today, importing a unit SuperVault already
holds changes nothing ("already in SuperVault"). In schema 3, **a re-import merges the file's metadata into the units
already there**. So re-importing his TMX files and Studio TMs fills in the metadata without duplicating anything.
The import summary would say e.g. "0 units added, 52,113 already in SuperVault (metadata added to 6,201)". Cost:
touching more than a quarter of the store rebuilds the index (about 6 minutes at 1.5M units), otherwise it updates
in place. Workbench has no metadata worth backfilling beyond the TM name (Source).

## 3. Filtering and restricting at 10M units

**Inside the index search, not after it.** A post-filter takes the best N matches, then drops some, and can leave
nothing even when allowed matches exist further down. So restrictions become part of the Lucene query:

- Each kept value is indexed as an untokenised term per field (`f_project`, `f_client`, `f_subject`, `f_domain`,
  `f_source`), lower-cased. It is not stored there, so the index needs only its postings: with Michael's cardinality
  roughly 1–2 bytes per value per unit, **about 20–40 MB at 10M units**. The term dictionary is negligible.
- Dates as numeric doc values (day number, 2–3 bytes per unit after Lucene's bit-packing): **about 20–30 MB per date
  field at 10M units**. Phase 2 needs them only if Michael wants date filters.
- A restriction such as "only Subject = Patents" becomes a Lucene `Filter`, wrapped in `CachingWrapperFilter`: one
  bitset per index segment (10M units = 1.25 MB), built once per segment and reused until that segment changes.
  Learning adds small new segments, so only their small bitsets are rebuilt. Both lookup paths (exact match by hash,
  fuzzy candidates by words) then only see units that pass. Cost per lookup: a bitset check per candidate, well under
  1 ms. A restriction can make lookups faster, because fewer candidates are scored.
- These are estimates. Phase 2 measures them on a 10M-unit copy (lookup time with and without a restriction, index
  size, memory in 32-bit Studio 2024).

**Showing values on a match** stays index-only: lookups today never open `supervault.db`, and they shouldn't start. The
values to show (a short string per unit, for units that have metadata) go into a stored Lucene field, compressed by
Lucene. That is about 5–10% of the index size at Michael's fill rate.

**Studio's own filters.** Read from Studio 2026's code:

- `TranslationMemorySettings.CreateSearchSettings` copies the project's **Filters** (penalty filters) and
  **HardFilter** into the `SearchSettings` passed to every TM, a third-party provider included.
- Studio's `Cascade` does not apply them. Each TM does: Studio's own TM in `TranslationMemoryImpl` (`HardFilter` in its
  search, `AbstractScorer.ApplyFilterPenalties`). So today SuperVault silently ignores both.
- The expressions are public objects: `AtomicExpression` (a field value plus `Equal`, `NotEqual`, `Smaller`,
  `SmallerEqual`, `Greater`, `GreaterEqual`, `Contains`, `ContainsNot`, `Matches`, `MatchesNot`) and
  `ComposedExpression` (`And`, `Or`, `Not`).
- SuperVault can translate a HardFilter into the same Lucene filter as its own rules, matching field names
  case-insensitively against its canonical names. Two exceptions: `Matches` (a regular expression) and fields it
  doesn't keep. For those it fetches more candidates and filters afterwards, and logs that it did.
- Filter penalties only lower a score, never remove a match. So they can be applied after scoring, to the few
  matches returned.

**Per-project rules.** Studio keeps a provider's state per project:

- When SuperVault's Settings window is opened from a project's TM settings and closed with OK, Studio calls
  `SerializeState()` and saves the result in that project's TM entry (`TranslationMemoriesControl.EditPlugInProvider`).
- It creates one provider object per state (`TranslationProviderCache`). So "only Subject = Patents" or "never Subject =
  Culinary" can live in the project, with no file of our own.
- Today SuperVault returns an empty state and its factory ignores the state it is given. Both would change.

## 4. Studio's own machinery: what works for a third-party TM

Read from Studio 2026's assemblies (Studio 2022 and 2024 not yet compared). Nothing here has been tried in Studio.

| Studio feature | Works for SuperVault? | Evidence |
|---|---|---|
| Field values shown on matches in **Translation Results** and **Concordance** | **Yes**, from `SearchResult.MemoryTranslationUnit.FieldValues`; no field definitions needed. On by default ("Show field values", 3 values, the rest in a tooltip). | `CompoundTranslationMemorySearchResultsControl.ApplyPreferences`, `TranslationUnitFieldValuesColumn`, defaults in `AbstractTranslationMemorySearchResultsPreferences` |
| Created/changed date and user under the results | **Yes**, from the match's `SystemFields` | `TmInfoBar.UpdateControls` |
| Project **Filters** and **HardFilter** reaching the TM | **Yes**: passed in `SearchSettings` | `TranslationMemorySettings.CreateSearchSettings` |
| Studio applying those filters to SuperVault's matches | **No**: each TM applies them itself | only `TranslationMemoryImpl` reads them |
| The **Filters** page listing SuperVault's fields | **No**: it lists only fields of TMs whose language direction is `ITranslationMemoryLanguageDirection` | `FieldDefinitionHelper.GetTranslationMemories` |
| Filters on fields that another Studio TM in the project defines (e.g. a Studio TM with a "Project" field) | **Yes, by name**: the filter reaches SuperVault, and SuperVault can honour it if it keeps a field of that name | as above |
| The **Penalties** page | **Partly**: its penalties arrive in `SearchSettings.Penalties`. SuperVault already applies the formatting ones; status penalties need a confirmation status, which it doesn't keep | `CreateSearchSettings` |
| Project "field values on update" | **Arrive** in `ImportSettings.ProjectSettings` at every confirm. The page that edits them lists only `ITranslationMemory` fields | `InteractiveTmUpdateImportSettingsBuilder` |

**Becoming an `ITranslationMemory`** would give Studio's Filters and field-update pages SuperVault's field names. But
it means 20 members on the TM and 15 on the language direction:

- TM maintenance iterators, batch edit and delete;
- fuzzy-index statistics, `Save`, `Delete`;
- and Studio code that then treats SuperVault as a TM it may upgrade, reindex or health-check.

Not for phase 1. Phase 3 could test it in a throw-away branch, and only if the Filters page turns out to matter.

## 5. The UI we'd still need (minimal)

1. **None for showing.** Studio's field column and info bar do it. SuperVault orders the values Project, Client,
   Subject first, because Studio shows only the first three.
2. **Import:** no picker in phase 1, just the default set. The import summary gains "metadata added to N units".
   "Keep every field" would be one checkbox in the SuperVault window (phase 3).
3. **SuperVault window:**
   - phase 1: a line "Metadata: 285 projects, 55 clients, 15 subjects";
   - phase 2, when opened from a project's TM settings: "Use only units with Subject: [ ]" and "Never use units with
     Subject: [ ]", picked from the values SuperVault holds, and the same for Client and Project. Saved in the
     project.

## 6. Privacy

Client and project names, and other translators' user names from agency TMs, will be stored in:

- `D:\Supervertaler\supervault\supervault.db` (tables `vals`, `unit_vals`);
- the index under `D:\Supervertaler\supervault\gen-*` (terms and the stored display field);
- `inbox.db` briefly, for learned units.

They are shown:

- in Studio's Translation Results and Concordance windows, and so in screenshots and screen shares;
- in Studio's info bar;
- in the SuperVault window.

Learned "Project" comes from Studio project names, which often contain a client name.

Rules for the build:

- metadata never goes into AI prompts or the MCP server's answers unless a later feature asks for it explicitly;
- a future SuperVault TMX export would include it, and should say so;
- tests and docs use Acme / PROJ-001 only.

## 7. Phases

**Phase 1: keep and show (useful on its own).**

- Schema 3.
- Import the default set from TMX and Studio TMs, with the name mapping.
- Workbench TM name as Source.
- Re-import merges metadata into existing units.
- Learned units get Project and Client (read from the `.sdlproj` above the document), Document,
  the user and the dates, and the project's field values on update.
- Matches carry `FieldValues` and `SystemFields`, so Studio shows them.
- Tests:
  - merge rules;
  - the 20-value cap;
  - schema upgrade of a stage-1/2 store;
  - re-import backfill;
  - the index stays lookup-only;
  - the protected files unchanged.
- Scale: re-import of 1.5M units with metadata, timed; memory in 32-bit Studio 2024.

**Phase 2: filter.**

- Index terms and the cached Lucene filters.
- Honour Studio's HardFilter and filter penalties.
- Per-project "only / never" rules in the provider state, edited from the SuperVault window.
- Measured on a 10M-unit copy.

**Phase 3: extras, if wanted.**

- "Keep every field".
- Date filters.
- The `ITranslationMemory` experiment.
- memoQ: the core is shared, so memoQ gets the stored metadata automatically. Showing it there is a separate piece
  of work.

## Open questions for Michael

1. When a project is set to "only Patents", should matches from other subjects be **hidden**, or shown **with a lower
   percentage** (a penalty), as Studio's filters can do?
2. A sentence that occurs in both a patent and a cookbook project: should it show in a cooking project? (Proposal:
   hide a unit only if *all* its subjects are excluded.)
3. Most units have no subject at all. In a project set to "only Patents", should those still show? (Proposal: yes.)
4. Are "Domain" and "Subject" the same thing to you, or separate? And "End client" vs "Client"?
5. Is it fine for learned units to take their Project from the Studio project name, and their Client from Studio's
   Customer field? Do you fill in Customer in your Studio projects?
6. Agency TMs contain other translators' and reviewers' user names. Keep them (shown under the match), or drop them?
7. To add metadata to what SuperVault already holds, you'd re-import your TMX files and Studio TMs once. That takes a
   while: the whole 1.5M would take minutes, and rebuilds the index. Is that acceptable, or should SuperVault offer
   to do it for a folder in one go?
8. Is a unit's original creation date useful to you (e.g. "prefer recent translations"), or just nice to see?
