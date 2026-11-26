# EverTM roadmap: from a better AnyTM to the whole TM system

Design note, 10 Nov 2026. Status: decided (Michael's answers at the end, 10 Nov 2026); no code. Building waits for
the metadata merge. Base: Trados main 92a4862; the metadata work on
`claude/supervault-metadata-design` (5cbfb8c, Plugin Core `claude/evertm-metadata-core`), which is not merged yet;
and the EverTM Matches note (`evertm-pane.md`, this branch). Code names such as `VaultStore` and the `SuperVault`
folders keep EverTM's old name.

This note is the map. The details of each piece stay in their own notes and aren't repeated here:

- `evertm-metadata.md` (on the metadata branch): fields, dates, newest wins, filters, context matches;
- `evertm-pane.md`: the EverTM Matches pane, older translations, removing a wrong translation.

## In short

- **The pitch (Michael, 10 Nov), for the Help and the website.** Studio's AnyTM is clunky: reversed copies of every
  TM pile up on disk and confuse people. EverTM is **one memory, for all your work, in every language variant and
  both directions**. Nothing to copy, reverse or keep in step.
- **Where it goes:** EverTM becomes Michael's whole TM system:
  - every unit is tagged (Client, End client, Subject, Project, Document, Source, dates, users);
  - a **manager** shows what is inside, as counts per value, not as a grid of 871,000 rows;
  - **ticks in the manager become a project's filter**, applied wherever EverTM answers;
  - **EverTM Matches** replaces Studio's Translation Results, the way TermLens replaced the termbase window, with a
    button to the manager.
- **Seven stages, each useful on its own** (section 3). The first two are already designed:
  1. merge metadata phase 1;
  2. EverTM Matches.

  Then:
  3. a manager to see what is inside, and export by metadata;
  4. bulk re-tagging and a per-project profile;
  5. the project filter, everywhere;
  6. context matches;
  7. editing and clean-up.
- **One rule decides most of the design:** a filter must apply in *every* place EverTM answers, or EverTM Matches
  and Studio disagree about what is allowed. That means inside the core lookup itself, never in one window.
- **Michael's store today is mostly untagged.** After the Workbench re-import, 17,013 of 871,290 units have a Client.
  So untagged units always pass a filter (decided), and re-tagging (stage 4) comes before the filter (stage 5) to
  fill the fields in. The order of the stages follows from that.
- **Language variants** (2f, Michael, 10 Nov): a setting per profile, "Mix them" (the default, Michael's way),
  "Penalise other variants by N%" or "Exact variant only", for users who replace their Trados TMs with EverTM and
  want en-GB and en-US kept apart. It comes with the profile (stage 4).
- **memoQ:** Supervertaler for memoQ doesn't use EverTM yet. Plugin Core is shared, so once memoQ has an EverTM
  provider, it gets the store, newest wins, removal, and the project filters for free. It does not get the manager
  window, which is Trados UI (section 4).

## 1. What exists or is designed

| Piece | State | Where |
|---|---|---|
| One store for all variants and directions; an index; Studio TM provider; import (TMX, Studio TM, Workbench); TMX export (pair + origins); learning on confirm | on main | `core/supervault`, `src/.../SuperVault` |
| **Metadata phase 1:** fields, dates and users per unit; re-import adds metadata; newest wins with the exact variant first; fields shown in Studio; "Older translations: N"; export writes metadata | built, Michael testing, not merged | `claude/supervault-metadata-design` 5cbfb8c |
| AI Batch Translate uses EverTM's best match (through the provider) | built, same branch | 05ecf59 |
| **Metadata phase 2 (designed):** "only / never" rules per project, applied inside the index search; Studio's HardFilter honoured; client first; context matches | designed | `evertm-metadata.md` sections 3d, 3h, 4, 8 |
| **EverTM Matches (designed, decided):** the pane, its details, older translations (needs a core call), insert, **removing** with a "removed" mark, undo and a Restore list | designed | `evertm-pane.md` |
| A manager, export by metadata, bulk re-tagging, a per-project profile, editing, clean-up | **this note** | – |

### How the pieces fit

```
                  ┌──────────────── Plugin Core (shared with memoQ) ────────────────┐
 imports ───────► │ store (SQLite)  ── changes log ──►  index (Lucene)               │
 confirms ──────► │  units, fields, dates,                 words, hashes, dates,     │
 removals/edits ► │  removed marks, profiles               field terms (f_*)         │
                  │                                         ▲                        │
                  │           Lookup / Concordance / Facets ┘ ◄── project filter ◄──┐│
                  └─────────────────────────────────────────────────────────────────┼┘
   Studio provider (Analyze, Pre-translate,        EverTM Matches pane     EverTM manager
   Translation Results, Concordance, Batch                                 (facets, ticks,
   Translate's TM context)                                                  export, edit)
```

- **Writes** (import, confirm, remove, edit, re-tag) go into the store. Its triggers log every change, and the index
  applies the log in place; every running Studio sees it within 2 seconds. This exists today
  (`VaultLibrary.SyncIndexLocked`).
- **Reads** (every lookup, the pane, the manager's counts) go to the index only. Lookups never open the store.
- **The project filter** is one object handed to the core lookup. Every reader passes the same one, so nobody can
  forget it.

## 2. The parts still to design

### 2a. The manager: a facet view, not a giant grid

Michael: "There needs to be a visual way of showing, in a spreadsheet-like grid, the contents of EverTM, and the user
then selects or deselects things in order to allow it to pass through to their current project."

A grid of 871,000 rows (10M later) shows nothing anyone can take in, and loading it would freeze Studio. What he
wants to see is **what kinds** of data are inside. So the manager is a set of lists side by side, one per field,
like the filter panel of a web shop:

```
┌ EverTM manager ─────────────────────────────────────────────────────────────────────────────┐
│ Language pair [nl ↔ en ▾]   Dates [any ▾]                  871,290 units · 214,118 pass ✓   │
├──────────────────┬──────────────────┬──────────────────┬───────────────────┬────────────────┤
│ Client           │ End client       │ Subject          │ Source            │ Project        │
│ ☑ (none) 854,277 │ ☑ (none) 869,001 │ ☑ (none) 790,113 │ ☑ ACME_Globex 41k │ ☑ (none) …     │
│ ☑ ACME    12,004 │ ☑ Globex   2,289 │ ☑ Patents 61,220 │ ☐ Cooking TM 12k  │ ☑ PROJ-001 …   │
│ ☐ Initech  5,009 │                  │ ☐ Medical 20,041 │ ☑ Studio: …       │                │
│ …                │ …                │ …                │ …                 │ …              │
├──────────────────┴──────────────────┴──────────────────┴───────────────────┴────────────────┤
│ Units that pass (page 1 of 2,142)                        [Export…] [Re-tag…] [Use for project]│
│ De pomp wordt gestart …   The pump is started …   ACME · Patents · PROJ-001 · 4 Nov 2026     │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Columns:** Client, End client, Subject, Project, Source (the TM, TMX file or Workbench TM a unit came from),
  plus a language pair and a date range at the top. Each value shows how many units carry it, and has a tick box.
  "(none)" is a value too, with its own count and tick, so untagged units are a visible choice.
- **Counts update as you tick:** after unticking "Cooking TM", the Subject column shows the counts among the units
  that are left.
- **Below:** the units that pass, 100 per page, newest first, with their fields. Selecting one shows its details as
  EverTM Matches does, with Remove (and later Edit).
- **Buttons:** Export… (2c), Re-tag… (2d), Use for this project (2b). There's also a button from EverTM Matches to
  the manager, and one from the existing EverTM window.

**Where the counts come from, at 10M units.** The orchestrator's sketch said SQL GROUP BY; the index is the better
source:

- Metadata phase 2 already puts each field value into the index as a term (`f_client`, `f_subject` …) for filtering.
  The index knows how many units carry each term without reading any of them, so the counts with nothing ticked are
  instant.
- Counts under the current ticks: Lucene's filter for the ticks is a bitset (10M units = 1.25 MB). Each value's
  count is that bitset intersected with the value's postings. Michael has a few hundred values, about 0.1 ms each,
  so a full refresh of all columns takes well under a second. It runs in the background, and a newer tick cancels it.
- Dates: a histogram from the date doc values, one pass at about 50 ms for 10M units.
- **SQL GROUP BY over `unit_vals`** (15M rows at 10M units) takes several seconds and holds a read transaction. It
  stays the fallback for the EverTM window's one-line summary, as today, not for live counts.
- **Never loaded whole:** at most one page of 100 units, the value lists (hundreds), and one bitset per open
  filter. That fits 32-bit Studio 2024.

**Value lists can be long.** Project has 285 values in Michael's TMX files and will grow by one per job. Each column
gets a search box and sorts by count; values with few units fold under "more…".

### 2b. The project filter: ticks become the project's filter, applied everywhere

**What it is:** for each field, the ticked values, kept as one saved **profile**:

- Semantics as decided in the metadata note (section 4): "only these" hides a unit only if **all** its values for that
  field are unticked. "(none)" ticked lets untagged units through.
- A filter changes what EverTM *offers*. It never deletes or changes a unit.

**Where it applies: every place EverTM answers.**

| Reader | Today | With the filter |
|---|---|---|
| Studio Translation Results, Concordance | provider → `VaultLibrary.Lookup` / `VaultLookup.Concordance` | the provider passes its project's profile |
| Analyze, Pre-translate | the same provider | the same |
| AI Batch Translate's TM context | the same provider (05ecf59) | the same: no own code |
| EverTM Matches | `VaultLibrary.Lookup` | passes the active project's profile |
| The manager's "units that pass" | – | the same filter object |

So the filter is a parameter of the core calls, `Lookup(…, filter)` and `Concordance(…, filter)`, not something a
window does afterwards. A window that filtered afterwards would show gaps where the best match was hidden, and would
disagree with Analyze.

**Where it is stored, and how the provider reads it fast:**

- **Profiles live in the store**, in a small table: id, name, the ticks, changed. They are saved with EverTM's data,
  backed up with it, and shared by every Studio on the machine.
- **A project points to its profile by id**, in EverTM's provider state. Studio already saves that state per project
  when EverTM's settings are opened from the project's TM settings (`SerializeState`, metadata note section 4).
  Today the state is empty. With this, it holds only the id, e.g. `profile=7`.
  - The manager's "Use for this project" writes it the same way. If Studio's API won't let a plugin change an open
    project's TM settings safely, the button opens EverTM's settings from the project's TM settings, as the metadata
    note designs.
- **At lookup, the provider holds its profile in memory.** Every 2 seconds at most, it checks one number in the store
  (`profiles` changed counter), the way the index pointer is checked today. An edit in the manager reaches every
  Studio within 2 seconds, and a lookup never reads the store.
- **The filter itself** is a Lucene filter cached per index segment (metadata note section 4): built once, about
  1.25 MB per profile at 10M units, then a bitset check per candidate. **Well under 1 ms added to the 26 ms lookup.**
  It can even make lookups faster, since fewer candidates pass.
  - 32-bit memory: keep the filters of the last few profiles used (say 4, about 5 MB at 10M units) and drop older
    ones.
- **Projects made from a template share one profile (decided, 10 Nov).** They copy the template's EverTM state, so
  they point to the same profile: a "Patents for ACME" template gives every new ACME patent job the same filter and
  tags. A change for one project then changes them all, so the settings show "This profile is used by projects made
  from the same template" where that can be told, and offer **"Make a copy for this project"** for the odd job that
  needs its own.

### 2c. Export by metadata

- `ExportChoiceDialog` today offers a language pair and the origins (Sources) as ticks, counted with one GROUP BY.
- It becomes **the manager's columns in a dialog**, with the same ticks and counts, plus "Export…". Better still, the
  manager's Export button uses the current ticks, so the dialog *is* the manager.
- The core export (`VaultExport.Export(store, path, pair, origins, …)`) gains the filter in place of `origins`. It
  reads unit ids that pass from the index (a bitset), then streams those units from the store in id order, 2,000 at
  a time. That's the same streaming as today, so memory doesn't depend on store size.
- Removed units are never exported (they aren't in the store; `evertm-pane.md` section 8).

### 2d. Tagging: a per-project profile, and bulk re-tagging

**Today (metadata phase 1),** a confirmed segment is tagged automatically with:

- Project = the Studio project's name;
- Client = the Studio project's Customer, if set;
- Subject = Studio's "field values on update", if named Subject or Domain;
- Document = the file name;
- Source = "Studio: <project>";
- the user, and the confirm time.

Its weaknesses:

- Studio's Customer is often empty, or is the agency rather than the end client.
- Studio has no End client field at all.
- "Field values on update" only lists fields of Studio TMs, so a project that uses only EverTM can't set Subject
  there.

**The profile adds what Studio can't give, set once per project:** Client, End client, Subject. Its "tag new
segments with" part fills those fields on every confirmation from that project:

| Field | Comes from | Priority |
|---|---|---|
| Project, Document, Source, user, date | automatic, as today | unchanged |
| Client | the profile, else Studio's Customer | profile first |
| End client | the profile (or the CLIENT_Endclient split of the project name, as today) | profile first |
| Subject | the profile, else "field values on update" | profile first |

**The same profile is the default filter** (2b), so a new ACME patent job set to "ACME / Globex / Patents" tags its
segments that way and **restricts its matches to ACME, Globex, patent and untagged units automatically (decided,
10 Nov)**. There is no separate "also filter" tick: setting the profile does both.

**Bulk re-tagging** is how the 854,000 untagged units get fields. In the manager, select units by any ticks, e.g.
Source = "ACME_Globex.tmx", then choose Re-tag… and set Subject = Patents, Client = ACME:

- It adds values (the union, as imports do). Removing a value from many units is a separate choice, with a count
  shown first.
- It runs as a background job with progress and Cancel, like imports. It's one transaction per 10,000 units, so a
  cancel or a crash leaves whole batches done, never half a batch.
- The index follows through the changes log. Under a quarter of the store, that happens in place. Above it, it's a
  full rebuild (the existing 25% rule): about 4–6 minutes at 871k, and at 10M units it can take an hour and isn't
  allowed in 32-bit Studio 2024 (`TooLargeFor32Bit`). The dialog says beforehand which will happen and how long it
  may take.
- **A big re-tag is fine with a background rebuild (decided, 10 Nov).** Big re-tags are not split into smaller runs.
  The rebuild runs in the background as rebuilds do today: lookups keep using the old index until the new one is
  ready. Only where 32-bit Studio 2024 refuses a rebuild of that size does the dialog say "do this in Studio 2026"
  before it starts.

### 2e. Editing and clean-up

- **Editing one unit's text** is *remove the old + add the new*. It reuses section 8 of the pane note, so a later
  re-import of the original TMX doesn't bring the wrong text back. The new unit keeps the old one's fields, and
  gets today's date and the user.
- **Editing fields** of one unit adds or removes values. It's the same path as re-tagging, for one unit.
- **Clean-up**, each one a list to review before anything happens, and each removal going through the removed marks
  (so it can be undone and survives re-imports):
  - **Hidden in both directions:** units that newest-wins hides both ways (9,293 on today's store), which can go
    without losing any match. Export them first (metadata note 3e).
  - **Near-duplicates:** the same source with translations that differ only in spaces, punctuation or case; keep the
    newest.
  - **Junk:**
    - source and target identical (not numbers or codes);
    - one side empty or only tags;
    - the target in the wrong language;
    - very long "units" that are whole paragraphs from a bad alignment;
    - mojibake (encoding damage).
- **Cost at 10M:** every check reads the index's doc values or one page at a time, never the whole store. The
  "wrong language" check needs a language detector: a later option, not in the first clean-up.

### 2f. Language variants: mix them, penalise them, or keep them apart

Michael built EverTM to mix variants: nl-BE and nl-NL, en-GB and en-US, both directions. He localises UK/US at the
end, so that suits him. Someone who replaces their Trados TMs with EverTM may want en-US units kept out of an en-GB
project, as their separate Studio TMs did.

**Today (metadata phase 1):**

- Each unit keeps the variant pair it **first** came with (store column `variant`, index doc values `c_xx`).
- Lookups mix all variants.
- Newest-wins prefers the project's exact **target** variant (metadata note decision 12, section 3b), but another
  variant is still offered as a full match with no penalty.
- The source variant never counts.

**The setting: "Language variants", three choices:**

| Choice | An en-US unit in an en-GB project | |
|---|---|---|
| **Mix them** | a full match: 100% stays 100% | **the default**; today's behaviour |
| **Penalise other variants by N%** | 100% shows as 98% (N = 2), like a Studio penalty | the user picks N; 2% suggested |
| **Exact variant only** | never offered | |

**The rules:**

- **Source and target alike.** A unit "differs" if its source variant or its target variant differs from the
  project's. The penalty is taken **once** per match, even when both sides differ: one rule a user can predict
  ("other variant: -2%"), not a sum.
- **A bare language counts as no mismatch** (decided here):
  - A **unit** stored as plain "en" (many TMX files say only "EN") is neither en-GB nor en-US. It is **not penalised
    and not hidden**, under both "Penalise" and "Exact only". Unknown isn't wrong: hiding them would quietly empty
    EverTM for anyone whose old TMs carry no variant. It's the same rule as "units with no metadata pass the filter"
    (decision 1). An exact-variant unit still ranks above it (decision 12), so it only wins when nothing better
    exists.
  - A **project** in plain "en" (no variant) has nothing to compare against: every unit passes, unpenalised, on
    that side.
  - If a user ever wants bare units hidden too, that becomes a tick then. Not now (no premature options).
- **A unit seen in several variants counts as all of them.** This needs one store change, because a unit keeps only
  its first variant today. If an en-GB project confirms a sentence whose text EverTM first learned from an en-US TM,
  the store keeps one unit (the text decides), still marked en-US. Under "Exact only", the user's own en-GB
  confirmation would then be hidden from them. So:
  - every variant pair a unit is seen with (import, re-import or confirmation) is added to it, as values of an
    internal field in `unit_vals` (the table fields already use). The changes log carries it to the index like any
    field;
  - the index holds **all** of a unit's variants per side (format 4, see below), and a unit matches if any of them
    is the project's;
  - existing units start with their one first variant. New confirmations and re-imports fill in the rest.
- **Where it applies: everywhere EverTM gives matches.** It is part of the same filter object as the project filter
  (2b), handed to the core `Lookup` and `Concordance`:
  - Translation Results, Concordance, Analyze, Pre-translate, AI Batch Translate (through the provider);
  - EverTM Matches, and the manager's "units that pass" (Exact only).
- **Billing effect (say so in the Help):** under "Penalise", an en-US 100% counts as 98% in Analyze, so it falls in
  the 95-99% band, and Pre-translate set to "apply only 100%" won't apply it. Under "Exact only", other variants are
  no match at all. That is what such users want, but it changes word counts, so the setting shows a line saying so.
- **Where it is set:**
  - **In the profile** (stage 4), so one client can be "Exact only" while other projects mix.
  - **A global default in EverTM's settings** (the EverTM window), default "Mix them". It applies to projects with no
    profile, and a new profile starts with it. Changing the default later doesn't change existing profiles: each
    profile shows its own choice, so there's nothing hidden to inherit.

**How it works, at 10M units:**

- **Penalise:** the variant is already in the index (doc values), read for the candidates already fetched. No
  extra lookup and no store read. The penalty is subtracted from the candidates' scores before they are ranked and
  cut to the top 20. A penalised match never counts as 100% (or CM).
- **Exact only must be a filter in the query, not a cut afterwards.** Fuzzy candidates are the top 200 by word
  overlap (`CandidateCount`), of which up to 60 are loaded (`LoadCount`). Dropping other variants *after* that
  could drop all 60 and lose a good en-GB match ranked 201st. So "Exact only" joins the project filter's cached
  Lucene filter (2b): units whose variants include the project's, or a bare language. It's a bitset check per
  candidate, well under 1 ms. The exact-match group (read whole, up to 20,000 units from doc values) is filtered in
  memory, as newest-wins already is.
- **Newest-wins under each choice:**
  - "Mix": as today (decision 12: exact target variant first, then newest).
  - "Penalise": unpenalised first (both sides the project's variants, or bare), then newest. So an en-US translation
    only wins a group that has no en-GB or bare one, and then shows 98%.
  - "Exact only": other variants are out of the group. They don't count as "older translations" either, just as
    units a profile filters out don't. EverTM Matches' "Show filtered-out" switch shows them greyed out.
- **"Your own TMs always come first"** (EverTM ranks after another TM's match of the same score,
  `SuperVaultResults.YieldRank`). The two don't conflict:
  - the penalty lowers EverTM's score first. Studio then ranks by score, and only between equal scores does
    YieldRank put EverTM below the other TM;
  - so an en-US 100% shown as 98% ranks below a client TM's 99%, and level with a client TM's 98%, which still comes
    first. The penalty never raises EverTM above anything, and YieldRank never lowers a penalised score further;
  - since a penalised match is never 100%, it can't trigger Studio's "multiple translations" penalty on another
    TM's exact matches.
- **How Studio shows the penalty:** the provider adds it to the result as a Studio penalty, as it does the project's
  provider penalty today (`SuperVaultResults.ApplyPenalty`), so Studio's match info shows why the match is 98%.
  Studio has no "variant" penalty type: which existing type to use (or lowering the base score, if none shows well)
  is checked in Studio when building.

**Cost and place in the stages:**

- The variant set in the index belongs in **stage 3's format-4 bump** (with field and context terms), so it costs
  no extra rebuild.
- The setting itself comes in **stage 4**, with the profile: the profile holds it, and "Exact only" uses the
  filter machinery of 2b. A global-default-only version before stage 4 isn't worth shipping early: without the
  variant set, "Exact only" would hide users' own confirmations (above).
- **Estimate: about 1.5-2 sessions:**
  - the variant set in store and index, inside stage 3: about half a session;
  - the three choices in `Lookup`/`Concordance`, the provider's penalty, the settings and profile UI, and tests:
    about 1-1.5 sessions.

## 3. The stages

Each stage is useful by itself. Effort is in working sessions (one session ≈ one focused day of a Claude session plus
Michael's testing), and is rough.

| # | Stage | Useful on its own because | Plugin Core needs | Sessions |
|---|---|---|---|---|
| 1 | **Merge metadata phase 1** | fields, dates and newest-wins in Studio today | schema 3, index format 3 (built) | testing only |
| 2 | **EverTM Matches** (`evertm-pane.md`) | full details, older translations, insert, remove; Translation Results not needed | `Translations(match, page)`; removed marks (schema 4); `Remove`/`Restore`/`Removed` | 6–8 |
| 3 | **The manager, read-only, + export by metadata** | see what's inside; export only patents, only ACME | index format 4: field terms `f_*` (metadata phase 2, first half) and all of a unit's variants (2f); `Facets(filter)`; `Units(filter, page)`; export by filter | 4–6 |
| 4 | **Re-tagging + per-project profile (tagging) + language variants** | fills in the 854k untagged units; every new job is tagged properly; en-GB and en-US kept apart for those who want it | profiles table (schema 5); re-tag job; profile values in learning; the variant set filled on import and confirm; the "Language variants" choice in `Lookup`/`Concordance` (2f) | 4–6 |
| 5 | **The project filter, everywhere** | matches only from the right client and subject, in Studio and the pane alike | `filter` on `Lookup`/`Concordance`; cached Lucene filters; provider state; Studio's HardFilter; client first (metadata phase 2) | 4–5 |
| 6 | **Context matches** (metadata phase 2, second half) | the right translation for a sentence's context, as a real CM | `unit_ctx`; context terms in the index (another format bump) | 3–4 |
| 7 | **Editing and clean-up** | fix a unit's text or fields; tidy the store | edit = remove + add; clean-up lists | 3–5 |

**Why this order:**

- **2 before 3:** the pane is what Michael uses on every segment. Its removal design creates the removed marks that
  editing and clean-up reuse later.
- **3 before 4 and 5 (decided, 10 Nov):** you can't choose a filter without seeing what there is to filter.
  Read-only is also safe.
- **4 before 5:** with 2% of units carrying a Client, a client filter is useless until re-tagging has filled the
  fields in.
- **6 is independent** of 3–5 and can move anywhere after 2. The metadata note decided context matches count as CM
  in Analyze by default, so they affect billing: a deliberate separate step.

**Index format bumps.** Stages 3 and 6 each add things to the index, so an older plugin can't read the new index, and
every Studio needs the new version (as with format 3). Doing both in one bump (field terms *and* context terms) would
save Michael one round of "install in every Studio" plus one rebuild. Recommended: **build the format-4 index with
both at stage 3**, even if context matching itself switches on only at stage 6.

## 4. What a memoQ side would get

Supervertaler for memoQ has no EverTM code yet. It plugs into memoQ as an MT plugin (`MemoQ.MTInterfaces`), so its
matches show as MT, never as memoQ's TM matches, and memoQ passes no context (metadata note 3h).

| Stage | memoQ gets, once it has an EverTM provider |
|---|---|
| Store, import, newest wins, exact variant first | free: all core |
| Learning from memoQ's `StoreTranslation` | core, plus a small memoQ adapter |
| Removed marks, removal from a list | the core calls; a UI to remove would be memoQ's own |
| Profiles and the project filter | free in the lookup. memoQ even passes Client, Domain, Subject and Project ID with each request, so the profile could be picked automatically from them |
| Manager, export dialog, re-tag dialog | **not free:** they are Trados WinForms code. Plugin Core has no UI today. Either the manager moves into a shared UI library, or memoQ gets its own. Decide when memoQ's EverTM is planned, not now |
| Context matches | no: memoQ passes no context |
| Language variants (2f) | free in the lookup: memoQ passes the project's languages with their variants. Its matches show as MT, so the penalty shows as a lower MT score |

What this means for the core design: every stage puts its logic in core (filters, facets, re-tag job, profiles,
removed marks), with the windows only calling it. That rule costs nothing now and keeps memoQ open.

## 5. Risks

1. **Scale (10M units).**
   - Lookups stay index-only. Facets use term counts and bitsets. Exports stream. Re-tags work in batches.
   - Nothing loads the whole store into memory.
   - Still to measure on a 10M-unit copy: facet refresh with several ticks; a cached filter's first build per index
     segment (estimated tens of ms); a re-tag of 1M units.
2. **32-bit memory (Studio 2024).**
   - About 1.25 MB per cached filter at 10M units, so at most a handful are kept.
   - Pages of 100 units; Lucene's 16 MB write buffer.
   - Full rebuilds above the 32-bit limit are refused there already (`TooLargeFor32Bit`). A re-tag that would need
     one says "do this in Studio 2026" before it starts.
3. **Several Studios on one store.**
   - SQLite's WAL mode with one writer at a time, and the build lock, exist today. Confirmations wait in the inbox
     while another Studio writes, and so would removals.
   - New here: a long re-tag holds the write lock in batches, releasing it between batches so confirmations get
     through.
   - Profile edits reach other Studios within 2 seconds.
   - Two people editing the same profile in two Studios: last save wins, and the manager reloads it when it changes
     underneath. Simple on purpose.
4. **Filter speed in Lookup** (26 ms median now): a cached bitset check per candidate, well under 1 ms. The risk is
   the first lookup after an index change, which rebuilds the filter for the changed segment only (Lucene caches
   per segment). Measure in stage 5.
5. **Schema migrations.**
   - Schema 4 (removed marks) and 5 (profiles) are new tables, added in place in milliseconds as schemas 2 and 3
     were. An older plugin ignores tables it doesn't know, so store changes alone never lock anyone out.
   - Index format bumps do lock out older plugins (stage 3, and 6 unless combined), so they need "install in every
     Studio".
   - Each bump also means one background rebuild: about 4–6 minutes at 871k; at 10M about an hour, and only in
     64-bit Studio.
6. **Filters that hide too much.** Filtered-out units are hidden, not ranked lower (decided), so a forgotten tick can
   quietly empty EverTM for a project. EverTM Matches therefore always says "Filtered: 12 matches hidden by this
   project's profile", and has a **"Show filtered-out" switch** that shows them greyed out (decided, 10 Nov).
   Analyze has no such line, so the profile's name shows in EverTM's entry in the project's TM settings.
7. **Template-shared profiles** (2b): an edit for one project changes others. "Make a copy for this project" and a
   clear label are the answer.
8. **"Exact only" can hide units a user expects** (2f) until re-imports and confirmations have filled in every
   unit's variants. Bare-language units always pass, and EverTM Matches' "Filtered: N hidden" line counts
   variant-hidden units too, so it stays visible.
9. **Privacy.** Client and end-client names become visible in more places (the manager, the pane, exports). The
   metadata note's rules stand: never in AI prompts or MCP answers unless a feature asks; docs and tests use ACME /
   PROJ-001.

## 6. For the Help and the website

- **The pitch:** "Studio's AnyTM gives you a reversed copy of every TM, and they pile up. EverTM is one memory for
  all your work: every language variant, both directions, nothing to copy or keep in step."
- **Then, as stages land:**
  - "Matches with everything EverTM knows" (stage 2);
  - "See what's inside, export what you need" (3);
  - "Tag once per project" (4);
  - "Only the right client's and subject's matches" (5);
  - "Mix en-GB and en-US, or keep them apart: your choice" (4).

## Decisions (Michael, 10 Nov 2026)

Michael: "Agree with all your suggestions. Proceed." So each question's proposal stands:

1. **A project's profile restricts matches automatically.** Setting a project to "ACME / Patents" tags what you
   confirm *and* limits matches to ACME, patent and untagged units. Units with no metadata always pass. (Section 2d.)
2. **Filtered-out matches are hidden, not ranked lower.** EverTM Matches gets a **"Show filtered-out"** switch that
   shows them greyed out, next to its "Filtered: N hidden" line. Analyze, Pre-translate and Translation Results
   simply don't get them. (Section 2b, risk 6.)
3. **A big bulk re-tag is fine with a background index rebuild.** No splitting into smaller runs. 32-bit Studio 2024
   still refuses a rebuild above its size limit, and the dialog says so before starting. (Section 2d.)
4. **Projects made from the same template share one profile**, with "Make a copy for this project" for exceptions.
   (Section 2b.)
5. **Build order as proposed:** the manager (stage 3) before re-tagging and the profile (stage 4) and the project
   filter (stage 5). (Section 3.)

Added later on 10 Nov (Michael, through the orchestrator):

6. **A "Language variants" setting**: Mix them (default), Penalise other variants by N%, Exact variant only. Source
   and target alike; per profile, with a global default in EverTM's settings; everywhere EverTM gives matches.
   Decided in this note: bare-language units are never penalised or hidden; the penalty is taken once per match; a
   unit counts as every variant it has been seen in. Built in stage 4, with its index part in stage 3's format-4
   bump. (Section 2f.)
