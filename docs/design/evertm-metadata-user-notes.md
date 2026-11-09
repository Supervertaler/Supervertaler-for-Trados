# EverTM metadata: notes for the help page

User-facing notes, kept while building, for docs.supervertaler.com/trados/evertm/. Parts not built yet are marked
*(planned)*. Design: `evertm-metadata.md`.

## What EverTM keeps with each translation

- **Fields:** Project, Client, End client, Subject, Document, Source, Note.
  - Subject also takes a TM's "Domain" (memoQ, DéjàVu) and "Fachgebiet".
  - Source is where the unit came from: the TMX file, the Studio TM, the Workbench TM, or "Studio: <project>" for a
    segment you confirmed.
  - At most 20 values per field per unit; a very common sentence ("Figure 1") would otherwise collect hundreds.
- **Dates and users:** when it was created and last changed, and by whom. Other translators' and reviewers' names in an
  agency TM are kept too.
- **Not kept:** other fields (PO numbers, Studio's technical fields such as x-Origin or x-ConfirmationLevel), and
  Workbench's own dates (they are when a unit entered Workbench, not when it was translated).

## Where it comes from

- **TMX files:** whatever the tool calls the fields (CafeTran "Project", memoQ "client"/"domain", Studio
  "x-Project:SingleString", ...). Dates from the unit, or from the file's header when the unit has none.
- **Studio TMs (.sdltm):** the TM's own text and picklist fields of those names, and its creation and change dates and
  users.
- **Workbench:** each TM's name as Source. A name such as `ACME_Globex` (capitals, an underscore, the end client) also
  gives Client "ACME" and End client "Globex". Only names in exactly that form are split.
- **Segments you confirm:** your Studio project's name (Project) and customer (Client), the document's name, your user
  name, and the time you confirmed. If the project's name is `ACME_Globex ...` and it has no customer set, Client is
  "ACME" and End client "Globex". A Subject set in the project's TM update fields (Project Settings > Translation
  Memory > Update) is kept as Subject.

## Units imported before this version

- They have no metadata or dates. **Import the same files again**: nothing is added twice, and the summary says e.g.
  "52,113 translation units read: 0 new, all others already in EverTM (6,201 of them brought metadata or dates)".
  - The counts are of translation units *read*: a unit that is in two of Workbench's TMs is read twice, so "already in
    EverTM" can be larger than EverTM's own count.
- A large re-import (more than a quarter of EverTM) rebuilds the index afterwards.

## What you see in Studio

- Translation Results and Concordance: the fields on each EverTM match (Studio shows the first three: Project,
  Client, Subject; the rest in the tooltip), plus "Older translations: N" when EverTM hid other translations.
- Under the results: the created and changed dates and users. A unit with no known date shows when it entered EverTM,
  by "EverTM".
- EverTM's window: "Translation units: 871,290", "Indexed translation units: 871,290, built ...", and "Metadata: 285
  projects, 55 clients, 15 subjects". While it works: "Indexing: 190,000 of 871,290 translation units. You can close this
  window and keep working." Afterwards: "Last task: ...".
- Every count EverTM shows says "translation units" in full (Michael: "sure to not be misunderstood").

## One translation per sentence: the newest

- A lookup offers one translation per source sentence: the newest, by when it was translated or last confirmed, never
  when it was imported.
- **Your project's language variant first:** in an en-GB project an en-GB translation wins, even an older one; only
  if there is none in en-GB does the newest in another variant show.
- **No date** counts as older than any date. Among undated ones, the one imported last wins.
- **Confirming** a translation makes it the newest, also one that was imported or older.
- The others are **hidden, not deleted**: each still serves the other direction, where it may be the only
  translation. Nothing is lost, and exporting writes them all.
- A confirmation made in another Studio that reaches EverTM late never overrides a newer one.
- Fuzzy matches and Concordance show one translation per source sentence too.
- *(planned)* The client's own translation first ("client first"), then the newest.
- *(planned)* Context matches: a translation made after a particular previous segment wins as a CM when that context
  comes back (Trados only).
- *(planned)* Per-project rules: "only use translation units with Subject: ..." and "never use ...".

## After updating

- The first start rebuilds EverTM's index in the background (about 4–5 minutes with 870,000 translation units); matches go on
  meanwhile, from the old index.
- Install the new version in every Studio you use with EverTM: an older one can't read the new index.

## Privacy

- Client and project names, and other people's user names from agency TMs, are stored only on your PC, in the
  EverTM folder.
- They show in Translation Results and Concordance, so also in screenshots and screen shares.
- An EverTM TMX export includes them.
