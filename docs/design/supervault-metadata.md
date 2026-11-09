# SuperVault metadata: keeping, showing and filtering on it

Design note, 9 Nov 2026. Status: proposal, no code yet. Base: Trados main a01b262, Plugin Core main 69f9aa1.
Revision 2 (9 Nov): Michael's answers to the first version's questions are built in. The big change is **one
translation per source sentence, newest wins** (section 3).

## In short

- **One translation per source, newest wins.** For a given source sentence and language pair, SuperVault shows only
  its newest translation. "Newest" is the translation's own date (from the TMX or TM, or the moment you confirm it),
  never the import time.
  - The older translations are **hidden, not deleted**. On Michael's real store, deleting them would throw away
    35,000 units that are still the only translation the other way round (section 3e).
  - Hiding happens at lookup, so imports stay as fast as today.
- **Studio already does most of the showing.** Field values on a match appear in Translation Results and Concordance.
  A match's created/changed date and user appear in the info bar under the results. No SuperVault UI is needed.
- **Studio hands SuperVault the project's TM filters on every lookup**, but leaves applying them to each TM. SuperVault
  would apply them inside its index search.
- **Studio's Filters page can't offer SuperVault's own field names.** So per-project rules ("only Subject = Patents")
  live in SuperVault's settings window, which Studio already saves per project.
- **Fields kept:** Project, Client, End client, Subject (memoQ's Domain merged into it), Document, Source, created and
  changed date and user (other translators' and reviewers' names kept, as Michael asked), Note.
- **Phase 1:**
  - keep and show those fields;
  - learn Project and Client from the Studio project;
  - newest wins;
  - re-importing adds metadata and dates to units already there.
- **Phase 2:** "only / never" rules, which hide; and the project's client first when choosing the newest.

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
| Supervertaler Workbench exports (≈85 files) | none | none; the `<header>` has a `creationdate` in 87 of 89 files |

TMX header dates: CafeTran (103 of 103 files), Supervertaler (87 of 89) and Studio (12 of 12) write a `creationdate`
on the `<header>`. LogiTerm, MateCat, OmegaT and the AI aligners mostly don't.

Fill rates across all 890,000 units (non-empty values; distinct values counted, not shown):

| Field (any spelling) | Units with a value | Distinct values |
|---|---|---|
| project | 113,243 | 285 |
| client | 95,922 | 55 |
| subject | 86,483 | 15 |
| domain | 32,376 | 59 |
| document / file name | 80,006 | 278 |

Studio TMs: the six largest readable `.sdltm` files (77,000 units; read from copies in the scratchpad, since deleted):

- System fields on every unit: creation date and user, change date and user, last-used date and user, usage count.
  Users per TM: 2–19 (agency TMs carry other translators' and reviewers' user names).
- Custom fields, all text in this sample (17 fields in 6 TMs), named by whoever set up the TM, sometimes in German:
  `Origin`, `PO-number`, `Product`; `Projekt`, `Datei` (file), `Lektor` (reviewer), `Übersetzer` (translator). Plus
  Studio's own `StructureContext`.
- Studio's field types: single text, multiple text, single picklist, multiple picklist, date, number.

Workbench (`D:\Supervertaler\resources\supervertaler.db`):

- The TM name (64 TMs) is the only real metadata.
- `created_date`/`modified_date` are when the unit entered Workbench (Nov 2025 onwards), not when it was translated.
- `project_id`, `created_by`, `notes`, `context_before`/`context_after`: empty in all 1,554,005 units.

### What Studio can tell us at confirm time

Read from Studio 2026's Editor (`TranslationUpdate.AddSegment`, `InteractiveTmUpdateImportSettingsBuilder`):

- `TranslationUnit.SystemFields`: the Studio user and the dates (Studio sets the current user,
  `OverrideTuUserIdWithCurrentContextUser`).
- `TranslationUnit.DocumentProperties` / `FileProperties`: the document's path and original file name.
- `ImportSettings.ProjectSettings`: the field values the project says to add on update (Project Settings >
  Translation Memory > Update). SuperVault receives these today and ignores them.
- **Not given:** the Studio project's name or customer. SuperVault reads them from the `.sdlproj` in the folder above
  the document (the plugin already parses that XML in `TradosTools.cs`). This works for confirms and for the batch
  task "Update Main Translation Memories" alike, and doesn't depend on which document is active.

### The fields (decided 9 Nov)

Canonical fields, with the spellings mapped onto them (case-insensitive, `x-` and Studio's `:Type` suffix stripped):

| Field | From TMX props | From Studio TM fields | From Workbench | Learned at confirm |
|---|---|---|---|---|
| Project | `project`, `Project`, `x-project`, `Projekt` | Project, Projekt, Job | – | the Studio project's name |
| Client | `client`, `Client`, `x-client`, `Customer`, `Kunde` | the same names | from the TM name, if it follows Michael's CLIENT_Endclient naming (below) | the Studio project's Customer, if set |
| End client | `End_client`, `End client`, `Endkunde` | the same names | the same rule | – |
| Subject | `subject`, `Subject`, `x-Subject`, **`domain`, `x-domain`** (Michael: keep only Subject) | Subject, Fachgebiet, Domain | – | from the project's "field values on update", if named Subject or Domain |
| Document | `x-document`, `x-filename`, `x-MateCAT-filename`, `x-SourceFile` | Datei, SourceFile | – | the document's file name |
| Source | the TMX file name | the `.sdltm` name | the Workbench TM name | "Studio: <project name>" |
| Created / changed (date, user) | `creationdate`/`creationid`, `changedate`/`changeid` (on `<tu>`, or on the `<tuv>` as DéjàVu writes) | system fields | – (Workbench dates are import dates: not used) | the moment of confirming, the Studio user |
| Note | `<note>` (rare: 69 in 890k) | – | – | – |

- **User names:** kept, including other translators' and reviewers' (Michael: "keep them!"). Studio shows the
  changed-by user under the match.
- **Every other field** is dropped. That includes the technical ones (`x-Context`, `x-ContextContent`,
  `x-StructureContext`, `x-Origin`, `x-ConfirmationLevel`, `x-LastUsedBy`, `x-OriginalFormat`, `lastusagedate`,
  `usagecount`, `ltattr-*`, `target_tags`, `x-context-pre`/`-post`, `x-MateCAT-id_*`, `x-rowid`, `corrected`,
  `aligned`, `tuid`) and agency fields such as PO numbers. "Keep every field" stays a possible later option.

**Splitting CLIENT_Endclient names (proposal, deliberately narrow).**

- Michael's own naming is an agency in capitals, an underscore, then the end client (e.g. `ACME_Globex`).
- Only 8 of his 64 Workbench TM names follow it. His job folders mostly use another shape, "CLIENT (job number)".
- Proposal: split **only** these names:
  - Workbench TM names;
  - the Studio project names of learned units;
  - and only when they match exactly: one run of capitals, digits and `. & ' -` before a single underscore, and
    anything after it, with an optional trailing "(…)" dropped.
- The first part becomes Client and the rest End client. Agency-supplied TMX and Studio TM file names are never split.
- The unsplit name is always kept as Source, so a wrong split never loses information.
- The import summary says how many names were split.

## 2. Storage, with duplicates stored once

A unit (one source + one translation) is stored once per language pair, whatever its direction and variants. One
unit can come from several projects, clients and subjects.

**Merge rules** (when an import or a confirmation brings a unit SuperVault already holds):

- Text fields: **the union of values**. At most 20 values per field per unit, keeping the newest. Very common segments
  ("Figure 1", "Claims") would otherwise collect hundreds of project names.
- Created date and user: **the earliest** seen. Changed date and user: **the latest** seen. Confirming a unit that is
  already there sets its changed date to now (section 3f).

**Schema 3** (in place, milliseconds, like schema 2; nothing reads the existing units):

```
ALTER TABLE units ADD COLUMN created INTEGER;      -- seconds since 1970, UTC; NULL = no date known
ALTER TABLE units ADD COLUMN changed INTEGER;
ALTER TABLE units ADD COLUMN created_by INTEGER;   -- vals.id
ALTER TABLE units ADD COLUMN changed_by INTEGER;
ALTER TABLE units ADD COLUMN date_from TEXT;       -- 'unit', 'file' (TMX header) or NULL: how good the date is
CREATE TABLE vals (id INTEGER PRIMARY KEY, field TEXT NOT NULL, value TEXT NOT NULL, UNIQUE (field, value));
CREATE TABLE unit_vals (unit_id INTEGER NOT NULL, val_id INTEGER NOT NULL, added INTEGER,
                        PRIMARY KEY (unit_id, val_id)) WITHOUT ROWID;
CREATE INDEX unit_vals_val ON unit_vals (val_id);
CREATE TRIGGER unit_vals_ai AFTER INSERT ON unit_vals BEGIN INSERT INTO changes (unit_id) VALUES (new.unit_id); END;
CREATE TRIGGER unit_vals_ad AFTER DELETE ON unit_vals BEGIN INSERT INTO changes (unit_id) VALUES (old.unit_id); END;
-- inbox.db: pending gains confirmed_at INTEGER (section 3f)
```

- Values are stored once (`vals`); units refer to them by number.
- The triggers feed the existing change log, so the index picks up metadata and date changes piece by piece. A big
  re-import still triggers a full rebuild (the existing 25% rule).
- Size estimate at 10M units, at Michael's fill rate of about 1.5 values per unit: about 15M `unit_vals` rows, about
  150–200 MB.

**Existing units and re-importing (backfill).**

- Existing units have no metadata and no dates.
- Today, importing a unit SuperVault already holds changes nothing. In schema 3, **a re-import merges the file's
  metadata and dates into the units already there**, so re-importing his TMX files and Studio TMs fills them in
  without duplicating anything.
- The import summary would say e.g. "0 units added, 52,113 already in SuperVault (metadata or dates added to 6,201)".
- Touching more than a quarter of the store rebuilds the index (about 6 minutes at 1.5M units).

## 3. One translation per source, newest wins

Michael, 9 Nov: "if the source is identical, you store only a single translation. That is, if a new one shows up, it
erases anything previously … I don't want to end up with 16 different matches for a single segment."

### The rule

For a lookup in direction X > Y, all units whose X side is the same source (3b) form a **group**. A lookup shows
only one unit per group: **the newest** (3a). The others are hidden, in lookups and in Concordance alike.
Identical translations are already one unit today.

### (a) What "newest" means

A unit's date is its **changed date**, or its created date when there is no changed date. The dates come from:

1. the unit itself (`changedate`/`creationdate` in TMX, the system fields of a Studio TM);
2. when a whole TMX file has no unit dates: its `<header creationdate>`, marked `date_from = 'file'`. This covers
   Supervertaler's and CafeTran's exports;
3. a **confirmed segment counts as the moment it was confirmed**, in Studio, not when SuperVault stored it (3f).

Never the import time: importing an old TM today must not hide the translation you made last month. Workbench's
dates are import dates, so they are not used either.

- **No date at all** counts as older than any dated unit. Among undated units, the one imported last wins.
  - Reason: a dated translation is evidence of when it was made, and an undated import should never hide it.
  - Consequence for Michael's current store: all 871,121 units come from Workbench, so none has a date. Until he
    re-imports his original files (which brings their dates), "newest" means "imported last".
- **Equal dates:** the unit imported last wins. This is rare with dates down to the second.

### (b) What "same source" means

- **Same base language pair and direction:** nl-BE and nl-NL are one language, as today; en > nl and nl > en are
  separate groups.
- **Same source text**, compared as exact matches are compared today: whitespace and invisible characters
  normalised; case and punctuation kept.
- **Formatting does not split a group.** If several units in a group are equally new, the one whose tags fit the
  document (the same tag shape) wins.
  - Measured on the store copy: grouping by text alone hides 22,786 units in nl > en; grouping by text + formatting
    hides only 14,322. Text-only grouping is what keeps "16 matches" from coming back as "100% + 99% formatting
    variants".
- **Target variant (en-GB vs en-US):** not part of the rule; the newest wins across variants, as in Michael's "one
  memory for all variants".
- Fuzzy matches are grouped the same way: a fuzzy source with three translations offers only its newest.

### (c) Metadata when a newer translation wins

Nothing is deleted, so each unit keeps its own Project, Client, Subject and dates. The match shows the winner's
values; the older translations' history belongs to them, not to the winner. The match also carries a field "Older
translations: 3", so Michael knows others exist.

### (d) Clients

One winner across all clients can hide a translation a particular client insists on. The recommendation is
**client first, then newest**, still one match:

- If the project has a client, the group's winner is the newest unit carrying that Client or End client.
- Only if no unit in the group carries it is the winner the newest overall.

This keeps one match per segment and never shows another client's wording when this client's own exists. It needs to
know the project's client at lookup time:

- from the project's SuperVault setting (phase 2);
- or from the active document's Studio project.

Whether Studio's batch tasks pass the document with each lookup is still to be checked. So this comes in **phase
2**; phase 1 uses newest only.

### (e) Michael's real store: what would collapse

Measured read-only on a copy of `D:\Supervertaler\supervault\supervault.db` (871,121 units, schema 1, all from
Workbench, no learned units; the original's checksum was unchanged afterwards):

| | en > nl | nl > en |
|---|---|---|
| Sources with more than one translation (text only) | 26,339 | 17,531 |
| Units hidden by newest-wins | 30,701 | 22,786 |
| Largest group | 104 translations | 474 translations |
| Groups of 2 / 3–5 / 6–15 / 16+ | 23,425 / 2,792 / 118 / 4 | 15,186 / 2,116 / 211 / 18 |
| Units hidden with text + formatting grouping, for comparison | 22,463 | 14,322 |

- **Hidden in both directions** (could be deleted without losing any match): **9,293** units.
- **Hidden in one direction only**: **34,901** units. These are still the only, or the newest, translation the other
  way round.

**Why hide rather than delete.**

- A unit serves both directions. Unit 1 is nl "A" > en "X", unit 2 is nl "A" > en "Y", newer. In nl > en, unit 2
  wins. Deleting unit 1 would also delete the only en > nl match for "X".
- Physical deletion would lose matches for about 35,000 units on today's store.
- Hiding loses nothing and needs no migration: the existing store simply starts collapsing at lookup.
- It can be undone, for example by a later "show all translations" switch for Concordance.

So nothing is lost and **no "losers" export is needed**. If a "tidy up" (physical deletion) is ever wanted, it would
delete only units hidden in both directions, and would first export them to a TMX.

### (f) Confirmed segments: the learned and inbox logic

Checked against today's code (`VaultStore.Writer.Learn`, `VaultInbox`, `VaultLibrary.Learn`):

- **Correcting and re-confirming** already replaces the translation learned before (same source key, the same unit
  updated). That fits.
- **A confirmation that differs from an imported unit** already adds a second unit. With newest-wins it hides the
  imported one at lookup. That fits, and is the point.
- **Changes needed:**
  1. **Re-confirming an existing translation** must make it the newest. Confirming a 100% SuperVault match unchanged,
     or a translation identical to an imported unit, today stores nothing ("already in SuperVault"). It must set that
     unit's changed date and user, and log a change so the index updates. Otherwise an older translation, re-approved
     today, would still lose to a newer one from another TM.
  2. **The confirm time travels with the unit:** `VaultLearnUnit` and `inbox.db` gain `confirmed_at`.
     - Units can wait in the inbox for minutes (another Studio importing), so "now" at storing time would misorder
       two Studios' confirmations.
     - The in-place update of a learned unit happens only if its `confirmed_at` is not older than the unit's changed
       date. A late-arriving older confirmation never overwrites a newer one.
  3. **Units just confirmed and not yet stored** (the read-your-writes list) count as the newest in their group.
- The "1 confirmed: 0 added, 1 already in SuperVault" count stays.

### (g) Studio's "multiple translations" penalty

- Read from Studio 2026: `Cascade.ProcessSearchResults` calls `SearchResults.CheckForMultipleTranslations`. It
  marks down the 100% matches when two or more of them, from any TMs, have different translations.
- With one translation per source, SuperVault never returns two different 100% matches for a segment. **SuperVault on
  its own can no longer trigger the 99% penalty.**
- If a project TM gives a different 100% translation, Studio still applies its penalty between the two TMs. Today's
  rule (SuperVault's match ranks after another TM's match of the same score, so the project TM's match stays as it
  was) keeps handling that.

### Scale at 10M units

- **Import:** no extra work for newest-wins, because nothing is compared or deleted at import. Imports stay as fast
  as today, plus the metadata merge.
- **Lookup:** the index gains, per unit, the date and a formatting fingerprint per side as numeric doc values. That
  is a few bytes per unit after Lucene's bit-packing: about 30–50 MB at 10M.
  - An exact lookup reads the whole group for the source's hash term. Today it stops at a few results; groups reach
    474 on today's store and might reach a few thousand at 10M for sentences like "Figure 1".
  - It picks the winner from the doc values alone, and reads stored text only for the winner. A 5,000-unit group
    costs a few thousand doc-value reads: well under 1 ms.
  - Fuzzy candidates are grouped by their stored source hash among the few hundred candidates already scored: no
    measurable cost.
- To be measured on a 10M-unit copy in phase 1, with the 32-bit Studio 2024 memory check.

### For the export work (Plugin Core, `local_ed12a571`)

- **Hiding is per direction**, so a "newest only" export depends on the direction (nl > en can drop units that
  en > nl still needs).
  - Recommend the export writes **everything** by default (lossless; re-importing gives the same store).
  - Offer "newest only, nl > en" as an option, computed from the index's hash terms in one pass, not by a GROUP BY
    over the store.
- Once schema 3 lands, the export should write each unit's own dates (`creationdate`/`changedate`, `creationid`/
  `changeid`) and its fields as `<prop>`s. Then exporting and re-importing keeps the order of "newest".
- Undated units should be exported without dates, not with the export time.

## 4. Filtering and restricting at 10M units

**Inside the index search, not after it.** A post-filter takes the best N matches, then drops some, and can leave
nothing even when allowed matches exist further down. So restrictions become part of the Lucene query:

- Each kept value is indexed as an untokenised, lower-cased term per field (`f_project`, `f_client`, `f_endclient`,
  `f_subject`, `f_source`). Only postings are needed: with Michael's cardinality roughly 1–2 bytes per value per
  unit, **about 20–40 MB at 10M units**.
- A rule becomes a Lucene `Filter` in a `CachingWrapperFilter`: one bitset per index segment (10M units = 1.25 MB),
  built once per segment and reused until that segment changes. Both lookup paths (exact by hash, fuzzy by words)
  then only see units that pass. Cost per lookup: a bitset check per candidate, well under 1 ms. A restriction can
  make lookups faster.
- **Filtering comes before newest-wins**: the newest *allowed* unit wins. An excluded newer translation never hides an
  allowed older one.
- These are estimates; phase 2 measures them on a 10M-unit copy.

**The rules, as decided 9 Nov.**

- "Only" and "never" rules **hide**; there is no penalty mode for now.
- A unit is hidden only if **all** its subjects are excluded: a sentence from both a patent and a cookbook project
  still shows in a patent job.
- Units with **no** subject still show under "only Patents".
- In Lucene terms, "only Subject = Patents" is a filter for "has Subject = Patents, or has no Subject". "Never
  Subject = Culinary" is a filter that excludes units whose subjects are all Culinary. For this, each unit's index
  entry gets a marker when it has exactly one subject (`f_subject_only`), so "all excluded" stays a term query.

**Showing values on a match** stays index-only: lookups never open `supervault.db`. The values to show go into a stored
Lucene field, about 5–10% of the index size at Michael's fill rate.

**Studio's own filters.** Read from Studio 2026's code:

- `TranslationMemorySettings.CreateSearchSettings` copies the project's **Filters** (penalty filters) and
  **HardFilter** into the `SearchSettings` passed to every TM, a third-party provider included.
- Studio's `Cascade` does not apply them; each TM does. So today SuperVault silently ignores both.
- The expressions are public objects: `AtomicExpression` (a field value plus `Equal`, `NotEqual`, `Smaller`,
  `SmallerEqual`, `Greater`, `GreaterEqual`, `Contains`, `ContainsNot`, `Matches`, `MatchesNot`) and
  `ComposedExpression` (`And`, `Or`, `Not`).
- SuperVault can translate a HardFilter into the same Lucene filter as its own rules, matching field names
  case-insensitively against its canonical names. Two exceptions: `Matches` (a regular expression) and fields it
  doesn't keep. For those it fetches more candidates and filters afterwards, and logs that it did.
- Filter penalties only lower a score, never remove a match. So they can be applied to the few matches returned.

**Per-project rules.**

- When SuperVault's Settings window is opened from a project's TM settings and closed with OK, Studio calls
  `SerializeState()` and saves the result in that project's TM entry (`TranslationMemoriesControl.EditPlugInProvider`).
- It creates one provider object per state (`TranslationProviderCache`).
- Today SuperVault returns an empty state and its factory ignores the state it is given. Both would change.

## 5. Studio's own machinery: what works for a third-party TM

Read from Studio 2026's assemblies (Studio 2022 and 2024 not yet compared). Nothing here has been tried in Studio.

| Studio feature | Works for SuperVault? | Evidence |
|---|---|---|
| Field values shown on matches in **Translation Results** and **Concordance** | **Yes**, from `SearchResult.MemoryTranslationUnit.FieldValues`; no field definitions needed. On by default ("Show field values", 3 values, the rest in a tooltip). | `CompoundTranslationMemorySearchResultsControl.ApplyPreferences`, `TranslationUnitFieldValuesColumn`, defaults in `AbstractTranslationMemorySearchResultsPreferences` |
| Created/changed date and user under the results | **Yes**, from the match's `SystemFields` | `TmInfoBar.UpdateControls` |
| Project **Filters** and **HardFilter** reaching the TM | **Yes**: passed in `SearchSettings` | `TranslationMemorySettings.CreateSearchSettings` |
| Studio applying those filters to SuperVault's matches | **No**: each TM applies them itself | only `TranslationMemoryImpl` reads them |
| The **Filters** page listing SuperVault's fields | **No**: it lists only fields of TMs whose language direction is `ITranslationMemoryLanguageDirection` | `FieldDefinitionHelper.GetTranslationMemories` |
| Filters on fields that another Studio TM in the project defines | **Yes, by name**: the filter reaches SuperVault, and SuperVault can honour it if it keeps a field of that name | as above |
| The **Penalties** page | **Partly**: its penalties arrive in `SearchSettings.Penalties`. SuperVault already applies the formatting ones; status penalties need a confirmation status, which it doesn't keep | `CreateSearchSettings` |
| The "multiple translations" penalty | Applied by Studio's `Cascade` across all TMs' 100% matches; with newest-wins, SuperVault alone no longer triggers it (3g) | `SearchResults.CheckForMultipleTranslations` |
| Project "field values on update" | **Arrive** in `ImportSettings.ProjectSettings` at every confirm. The page that edits them lists only `ITranslationMemory` fields | `InteractiveTmUpdateImportSettingsBuilder` |

**Becoming an `ITranslationMemory`** would give Studio's Filters and field-update pages SuperVault's field names. But
it means 20 members on the TM and 15 on the language direction:

- TM maintenance iterators, batch edit and delete;
- fuzzy-index statistics, `Save`, `Delete`;
- and Studio code that then treats SuperVault as a TM it may upgrade, reindex or health-check.

Not for phases 1–2. A later throw-away experiment, only if the Filters page turns out to matter.

## 6. The UI we'd still need (minimal)

1. **None for showing.** Studio's field column and info bar do it. SuperVault orders the values Project, Client,
   Subject first (Studio shows the first three), then "Older translations: N".
2. **Import:** no field picker. The import summary gains "metadata or dates added to N units" and "N TM names split
   into Client / End client".
3. **SuperVault window:**
   - phase 1: a line "Metadata: 285 projects, 55 clients, 15 subjects; older translations hidden for 17,531 nl > en sources";
   - phase 2, when opened from a project's TM settings: "Use only units with Subject: [ ]" and "Never use units with
     Subject: [ ]", picked from the values SuperVault holds, and the same for Client and Project; plus "This project's
     client: [ ]" for client-first. Saved in the project.

## 7. Privacy

Client and project names, and other translators' and reviewers' user names from agency TMs, will be stored in:

- `D:\Supervertaler\supervault\supervault.db` (tables `vals`, `unit_vals`; user columns on `units`);
- the index under `D:\Supervertaler\supervault\gen-*` (terms and the stored display field);
- `inbox.db` briefly, for learned units.

They are shown:

- in Studio's Translation Results and Concordance windows, and so in screenshots and screen shares;
- in Studio's info bar;
- in the SuperVault window.

Learned "Project" comes from Studio project names, which often contain a client name.

Rules for the build:

- metadata never goes into AI prompts or the MCP server's answers unless a later feature asks for it explicitly;
- a SuperVault TMX export includes it, and should say so;
- tests and docs use Acme / PROJ-001 only.

## 8. Phases

**Phase 1: keep, show, newest wins (useful on its own).**

- Schema 3; import the fields above from TMX and Studio TMs, with the name mapping, Domain merged into Subject, and the
  narrow CLIENT_Endclient split.
- Re-import merges metadata and dates into existing units.
- Learned units:
  - Project and Client from the `.sdlproj` above the document; Document; the user;
  - `confirmed_at` through the inbox;
  - re-confirming bumps the date.
- Matches carry `FieldValues` and `SystemFields`.
- Newest-wins collapse at lookup and in Concordance.
- Tests:
  - merge rules and the 20-value cap;
  - date precedence, including undated units and the file date;
  - two Studios confirming out of order;
  - re-confirming an imported unit;
  - newest-wins in both directions without losing the reverse match;
  - schema upgrade of a stage-1/2 store;
  - re-import backfill;
  - lookups stay index-only;
  - the protected files unchanged.
- Scale: 10M-unit lookup with large groups; re-import of 1.5M with metadata; memory in 32-bit Studio 2024.

**Phase 2: filter and client first.**

- Index terms and cached Lucene filters.
- Honour Studio's HardFilter and filter penalties.
- Per-project "only / never" rules and "this project's client" in the provider state, edited from the SuperVault
  window.
- Client-first winner selection.
- Measured on a 10M-unit copy.

**Phase 3: extras, if wanted.**

- "Keep every field".
- Date filters.
- A Concordance switch to show older translations.
- A "tidy up" that deletes units hidden in both directions, after exporting them.
- The `ITranslationMemory` experiment.
- memoQ: the core is shared, so memoQ gets the stored metadata and newest-wins automatically. Showing fields there is
  separate work.

## Decisions (9 Nov) and remaining questions

Decided with Michael:

1. "Only"/"never" rules **hide**; there is no penalty mode for now.
2. A unit is hidden only if **all** its subjects are excluded.
3. Units with **no** subject still show under "only Patents".
4. **Only Subject**: Domain is merged into it. **Client and End client** are both kept.
5. Confirmed segments take **Project from the Studio project name and Client from Studio's Customer** field.
6. Other translators' and reviewers' **user names are kept**.
7. Re-import backfills metadata and dates (the design is unchanged; the orchestrator will explain it).
8. **One translation per source, newest wins.**

Still open, in plain words:

1. A translation with **no date** counts as older than any dated one. So if your own undated export and an old agency
   TM both translate a sentence, the agency's dated one shows. Fine? (Most of your own exports have a file date,
   which SuperVault then uses.)
2. Only TM names that look exactly like `ACME_Globex` are split into Client and End client, and only for Workbench
   TMs and Studio project names. Is that the right reach?
3. When a client's own translation exists, should it beat a newer translation made for someone else ("client
   first")? This is the recommendation; it comes in phase 2.
4. Newest wins across en-GB and en-US alike. Is that fine, or should your project's English variant come first?
