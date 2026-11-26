# EverTM Matches: EverTM matches in a pane of our own

Design note, 9 Nov 2026. Status: decided, design finished (Michael's answers of 9 Nov are built in, see "Decisions" at the end);
nothing built. Building waits until the metadata work, which holds Plugin Core and the EverTM code, is merged.
Base: Trados main 92a4862, plus the metadata phase 1 branch
`claude/supervault-metadata-design` (e7e6142, core ab2cfb4), which this pane builds on and which is not merged yet.
Name: the pane is called **EverTM Matches** (Michael, 10 Nov). "The pane" below always means EverTM Matches. The wider
plan it belongs to is in `evertm-roadmap.md`.

Code names such as `VaultLookup` and the `SuperVault` folders keep EverTM's old name.

Michael, 9 Nov: "If we had our own EverTM pane, we wouldn't even need to use the translation results pane or Trados
Studio TMs anymore. Especially if the EverTM pane also showed matches and fuzzy matches diff highlighting." And on
metadata: "do not send less. Information is information, and it's useful."

## In short

- **A new pane of its own, called EverTM**, docked in Studio's Editor like TermLens and the Assistant. Not inside
  TermLens and not a tab in the Assistant (section 1).
- **It shows, for the active segment:** EverTM's exact and fuzzy matches (and context matches once metadata phase 2
  has them), best first, with the differences from the current source highlighted.
- **For the selected match it shows everything EverTM knows:** every field with all its values, the unit's own dates
  and users, when it entered EverTM, its language variant, and **the older translations themselves**, each with its
  own dates and fields.
- **The lookup runs off Studio's UI thread.** The segment change only reads the source (microseconds); the 26 ms
  lookup runs in the background; only the newest segment's result is drawn (section 2).
- **Inserting:** double-click, Enter, or a shortcut per match number. Tags are placed exactly as Studio's
  Translation Results gets them from EverTM today, because it is the same core code (section 3).
- **Studio's TM provider stays as it is.** Analyze, Pre-translate, learning on confirm and Studio's Concordance keep
  working through it. The pane reads EverTM, writes the target, and can remove a wrong translation (sections 5 and 8).
- **Diff highlighting reuses what EverTM already computes.** Every match carries a word alignment against the
  document (`VaultMatch.Diff`); Studio draws it today. The pane draws the same alignment (section 4).
- **One core addition is needed:** a call that returns *all* translations of a source, newest first, a page at a
  time. Today the core counts the older ones ("Older translations: 3") but does not return them (section 7).
- **Removing a wrong translation** (a typo, a bad agency translation) from the pane: it leaves a "removed" mark, so
  re-importing the TMX or TM it came from doesn't bring it back. It disappears from every lookup at once, in every
  running Studio, without a rebuild. It can be undone, and it never touches a Studio TM, a TMX file or Workbench
  (section 8).
- **Phase 1** is the pane, its matches, details, older translations, insert and remove. Filters and sorting by
  client, subject and source come with metadata phase 2 (sections 6 and 9).

## 1. Where it lives

### What exists today

| Pane | Docked | How it is built |
|---|---|---|
| TermLens (`TermLensEditorViewPart`) | top of the Editor, pinned | a strip of the source's words with the terms under them; one shared control; listens to `ActiveSegmentChanged` |
| Supervertaler Assistant (`AiAssistantViewPart`) | right, unpinned | tabs: Chat, Batch Operations, Import/Export, Reports, and SuperSearch when that setting is on |
| SuperSearch (`SuperSearchViewPart`) | bottom, unpinned | a shared controller whose control lives either in its own pane or as an Assistant tab |
| TermPicker, SuperVoice | right | their own panes |

Each is a Studio "ViewPart": a class with `[ViewPart]` and `[ViewPartLayout(typeof(EditorController), Dock = ...)]`
attributes, listed in the plugin manifest. Studio remembers where the user drags it.

### The three options

1. **Inside TermLens.** TermLens is a low strip across the top, laid out word by word along the source. A list of
   matches, each up to a full sentence long, plus a details area, does not fit that shape. It would also mean either
   squeezing the terms or making the strip tall. TermLens is already 5,600 lines. Not recommended.
2. **A tab in the Assistant.** The Assistant is mostly on its Chat tab. A tab you have to switch to on every segment
   is a step backwards from Translation Results, which is always visible. It is also only there with an Assistant
   licence. Not recommended.
3. **A pane of its own: recommended.** It can sit where Translation Results sits today, so Michael can close
   Translation Results and put EverTM in its place. It can be seen at the same time as TermLens and the Assistant.

**Proposal:** a new `EverTmViewPart`, named **"EverTM Matches"** (Michael's choice, 10 Nov), docked at the bottom of the Editor and not pinned (as
SuperSearch is), so it does not take space from anyone until opened. Michael drags it where he wants it once; Studio
keeps it there.

**Don't tab it together with Translation Results.** The plugin already knows that Studio's Translation Results
brings itself to the front on every segment change in Studio 2024 (version 18) (comments in `AiAssistantViewPart` around the
batch and Reports code; the Assistant fights this with repeated `Activate()` calls). In the same dock group, the
EverTM tab would lose to Translation Results on every segment. Either close Translation Results or keep it in a
different dock area.

The SuperSearch pattern (a shared controller that can be hosted in two places) is there if an Assistant tab is ever
wanted, but phase 1 builds only the pane: no setting for where it lives.

**Licence (decided):** the pane needs a licence, like TermLens and the Assistant. Without one it shows TermLens's
activation overlay (`ShowLicenseRequired`, gated on `LicenseManager.Instance.HasTier1Access`) and runs no lookups.
EverTM itself (the TM provider in Studio) stays free, as today.

### What it looks like

```
┌ EverTM Matches ────────────────────────────── 871,290 units · 3 matches · 24 ms ┐
│ 1  100%  De pomp wordt [gestart] als de druk daalt.                              │
│          The pump is started when the pressure drops.            2 older ▸       │
│          ACME · PROJ-014 +1 · 4 Nov 2026                    (faint, small grey)  │
│ 2   87%  De pomp wordt ~~uitgeschakeld~~ [gestart] als de druk stijgt.           │
│          The pump is switched off when the pressure rises.                       │
│          Hydraulics_TM.tmx · 2019 · en-US                                        │
│ 3   74%  ...                                                                     │
├──────────────────────────────────────────────────────────────────────────────────┤
│ Match 1                                                                           │
│ Project   PROJ-001; PROJ-014          Client  ACME                               │
│ End client Globex                      Subject Patents; Hydraulics                │
│ Document  PROJ-001_claims.docx         Source  ACME_Globex.tmx; Studio: PROJ-014 │
│ Note      (none)                       Variant nl-BE > en-GB                      │
│ Created   12 Mar 2021, jdoe            Changed 4 Nov 2026, mbeijer               │
│ In EverTM since 9 Nov 2026                                                        │
│ Older translations (2)                                                            │
│   The pump starts when the pressure falls.      ACME · PROJ-003 · 2019            │
│   The pump is activated as the pressure drops.  Workbench: Hydraulics TM · undated│
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **Top: the matches**, best first. Each row: number (for the shortcut), score, a badge for the kind (100%, fuzzy;
  CM when context matches arrive), the stored source with the differences marked, and the stored target. A row says
  "N older" when newest-wins hid other translations.
- **Under each match: one faint summary line** (Michael, 10 Nov), so matches can be told apart without selecting
  each one. Studio's Translation Results stacks the fields in a column beside each match, which makes every match
  very tall and the text cramped. Here a row is always exactly three lines: source, target, summary. Never a
  column, never more lines. The rules:
  - **What it shows:** Client · Project · date, in small grey type. The date is when the unit was last changed (else
    created), as "4 Nov 2026"; a unit with no date shows "undated".
  - **Missing fields are left out**, with their " · ", never "(none)". So a unit with only a project reads
    "PROJ-014 · 4 Nov 2026".
  - **No Client and no Project:** the Source takes their place ("Hydraulics_TM.tmx · 2019",
    "Workbench: Hydraulics TM · undated"), because where a unit came from is then the best clue. The line is never
    empty, so every row keeps the same height.
  - **Several values:** the first value, then "+N" for the others, e.g. "PROJ-014 +1". "First" is the first in the
    order the details area lists them. The details area shows them all.
  - **A narrow pane:** the date is always shown in full. Client and Project are cut at the end with "…" to fit,
    e.g. "ACME · PROJ-0… · 4 Nov 2026". Below about 15 characters only the date is left. The line never wraps.
  - **A variant that differs from the project's** is added at the end, e.g. "· en-US" in an en-GB project, or
    "· nl-NL > en-US" when both sides differ. It shows in every "Language variants" mode, including "Mix them",
    where it's the only sign. Like the date, it's never cut off. A bare language ("en") or a matching variant adds
    nothing. (Language variants: roadmap section 2f.)
  - **Older translations** use the same line, so the two lists read the same way.
  - **Cost:** nothing extra. The fields and dates are already in each match (`VaultCandidate.Fields`, `Changed`,
    `Created`), and only the rows on screen are drawn. There is no extra lookup and no store read, at any store size.
  - End client and Subject aren't on the line: three items fit a narrow pane. They stay in the details area, and
    could replace Project later if Michael finds them more telling.
- **Bottom: the details of the selected match**: every field with all its values, no cut-off column and no tooltip.
  The two areas share a splitter.
- **Status line:** units in EverTM, number of matches, lookup time; or "EverTM is being rebuilt (412,000 of
  871,290)", "EverTM is empty", "EverTM is not in this project's TMs: segments you confirm won't be learned".

## 2. How it gets the active segment and refreshes

### What TermLens does today

- On `EditorController.ActiveDocumentChanged` it moves its `ActiveSegmentChanged` handler to the new document
  (`TermLensEditorViewPart.cs`, `OnActiveDocumentChanged`).
- `OnActiveSegmentChanged` runs on Studio's UI thread. It calls `UpdateFromActiveSegment`, which reads the source
  and posts one render with `BeginInvoke`. Requests that arrive while a render is pending are folded into it
  (`_renderQueued`), and the render reads the active segment when it runs, so the newest state wins.
- One thing in it does run on the UI thread that shouldn't: API term providers (`_fallbackProviders`) are searched
  inside the segment-change handler. Not part of this work, but worth fixing separately.

### What EverTM Matches does

1. **On the UI thread, in the segment-change handler (microseconds):** read the active pair's source `ISegment` into
   EverTM's pieces (text runs and tags in order) and remember which source tag each tag piece is. Read the
   document's language pair and exact target variant (en-GB). Studio's objects may only be touched on the UI
   thread, so this copying must happen here. Nothing else happens here.
2. **Hand the copy to one background worker** with a request number. If a lookup is already running, the new
   request replaces any waiting one; it does not queue behind it. Holding the down-arrow through 200 segments
   therefore costs at most two lookups, not 200.
3. **The worker calls `VaultLibrary.Lookup`**, the same call the provider makes. The index is thread-safe (many
   lookups at once), and `Lookup` also sees units confirmed in the last minute and not yet stored, so a segment you
   just confirmed shows its new translation at once.
4. **Back on the UI thread (`BeginInvoke`):** draw the result **only if its request number is still the newest** and
   the pane is not disposed. A slow result for a segment you have already left is thrown away.

- **Studio's provider runs its own lookup for Translation Results at the same moment.** Two 26 ms lookups on two
  threads per segment is fine. Sharing results between the two would mean a cache, keyed by segment and settings,
  that both sides must keep correct: state that doesn't earn its keep at 26 ms.
- **When the pane is hidden**, segment changes are noted but no lookup runs; showing it looks up the current
  segment.
- **Minimum score:** a fixed 50%, so the pane shows a little more than Translation Results' usual 70%. Up to 20
  matches (newest-wins already removes repeats).
- **Failure paths:** any exception in the worker becomes "EverTM lookup failed: …" in the status line and the
  diagnostic log; it never reaches Studio. A missing, broken or rebuilding index shows its status, not an empty
  list that looks like "no matches". The worker never holds a lock while it touches the UI.

## 3. How a match gets into the target

### Click and keyboard

- **Mouse:** double-click a match row to insert it. **Older translations can be inserted too** (decided): double-click
  one in the details. A single click only selects and shows details.
- **In the pane:** Up/Down to select, Enter to insert.
- **From the editor, without leaving it:** a shortcut per match number. Supervertaler's own shortcuts already take
  Alt+0–9 (TermLens terms) and Ctrl+Alt+0–9 (QuickLauncher slots). **Default (decided): Alt+Shift+1–9** for "insert
  EverTM match N". Before building, check it for clashes against Studio's own Keyboard Shortcuts list in 2022, 2024 and 2026. These
  are Studio actions (`[Action]` + `[Shortcut]` on the `EditorController`, as `TermInsertActions` are), so Michael
  can reassign them in Studio's Options with no setting of ours.
- **After a mouse insert, focus goes back to the editor**, so typing continues in the target. The TermLens popup
  already solves the same problem by inserting only after focus has returned (`TermLensPopupForm.RequestInsert`).
- Insert never confirms. Ctrl+Enter stays Michael's.

### Tags

- The match already comes with its tags mapped onto the document's own tags (`VaultTagMap`, through
  `VaultMatch.Target` and `TargetDocTag`). Each tag piece says which source tag it is.
- The pane builds the target from that: text as text, each mapped tag as a clone of that source tag. This is what
  the Assistant does for AI translations: `SegmentTagHandler.ReconstructTarget` clones source tags into a new target,
  through Studio's `IDocumentItemFactory`. The pane should reuse that, not write a second tag builder.
- When tags could not be placed with confidence, the match is plain text, exactly as in Translation Results today
  (which also gets the formatting penalty). The pane inserts the text and says "inserted without formatting" in the
  status line, so it is never silent.
- **Same placement in both windows:** the provider builds its pieces from Studio's `Segment` (one piece per
  element); the pane builds them from the editor's `ISegment`. If the two orders ever differed, a tag would land in
  a different place in the pane than in Translation Results. Phase 1 needs a test that inserts the same matches both
  ways and compares the results (in `tools/SuperVaultCheck`, which already drives Studio's own types).

### Writing it

- Through `IStudioDocument.ProcessSegmentPair`, wrapped in the plugin's `SegmentWriteCheck.ProcessChecked`. That is
  the path every Supervertaler write uses. It can be undone, it respects locked segments, and it reports when Studio
  quietly declines a write (unaccepted tracked changes).
- **The label (decided): an inserted match is labelled as a TM match, "TM, 87%, EverTM", if Studio allows it.**
  This is an exception to Supervertaler's rule since 20 Oct that everything it writes is labelled as edited by hand
  ("interactive"): inserting a TM match is what Studio itself labels "TM". The difficulty is that Studio's editor
  relabels a `ProcessSegmentPair` write as "interactive" by itself (`SegmentOrigin.cs`). So the first job of the
  build is a test in Studio, trying these in order and keeping the first that sticks:
  1. set `TranslationOrigin` (type "tm", the match %, system "EverTM") inside the same write as the text;
  2. if the editor relabels it, set it again straight after the write, once the editor has finished
     (`IStudioDocument.ChangeTranslationOriginOnSegment`, or the pair's properties, posted for a later UI tick);
  3. if neither sticks, the best Studio allows is what it does itself when you edit an applied TM match:
     "interactive", with "TM, 87%, EverTM" kept as the previous origin.
  Whichever it is, the note and the Help page say what the segment ends up showing, and Michael is told before the
  insert is built on it. The pane must not fight Studio by rewriting the label in a loop.

### What Studio's public API does and doesn't allow

- **Allows:** reading the active segment and its language pair; writing the target with source tags cloned; setting a
  segment's translation origin; editor actions with shortcuts; docking a pane.
- **Does not allow, as far as we know:**
  - putting our pane's results into Studio's Translation Results window;
  - triggering Studio's own "apply translation result N" with a result we choose;
  - knowing which result is selected in Translation Results;
  - hiding EverTM from Translation Results while keeping it for Analyze and Pre-translate.

  So if Translation Results stays open, EverTM's matches show in both windows. Closing Translation Results is the
  clean answer.

  REA could confirm the "does not allow" points from Studio's own DLLs, if any of them turns out to matter.

## 4. Diff highlighting

### What already exists

- **EverTM computes it already.** Each `VaultMatch` carries `Diff` (a `VaultDiffResult`): a word-level alignment of
  the document's tokens against the stored source's tokens, with each step marked Identity, Change, Insert or
  Delete. Words, punctuation, spaces and tags are tokens (`VaultDiff.Tokenize`). Studio draws exactly this
  alignment in Translation Results today (`SuperVaultResults.ToEditDistance`). Nothing new to compute.
- **Workbench's Match Panel** (`modules/translation_results_panel.py`, `modules/tm_diff.py`) shows the same idea in
  three stacked boxes: Current Source, TM Source and TM Target. The TM source is annotated with what the current
  segment changed: deleted words struck through, added words underlined, tags as tokens of their own. Its insert
  keys are the ones proposed here: Up/Down, Enter, and a number per match.
- **The Trados plugin's `TokenDiff`** (for tracked changes) and Workbench's `difflib` would compute a diff again.
  Not needed: the pane uses EverTM's own alignment, so the pane shows exactly the same differences as Translation
  Results and the match score.

### How the pane draws it

- In each match row, the stored source is drawn with the differences marked:
  - words in the stored source that your segment doesn't have: struck through, red;
  - words your segment has and the stored source doesn't: shown in place, underlined, green;
  - changed words: the stored word struck through, followed by yours.
- Tags are drawn as small grey markers, so a tag difference is visible too.
- **Segments over 400 tokens** are scored on word overlap without an alignment (`VaultDiff.MaxAlignTokens`). Those
  rows show "too long to compare" instead of a diff, never a wrong one.
- The target isn't highlighted: EverTM knows which source words differ, but not which target words translate them.
  Guessing would mislead.
- **Drawing:** the plugin's panes are Windows Forms. One owner-drawn list that draws only the rows on screen is
  enough, not a rich-text box per row, and not a browser control (WebView2 brings start-up time and its own profile
  problems: `trados-webview2-profile.md`).

## 5. What stays with Studio's TM provider

The provider (`SuperVaultProvider`) is not touched by this work. It keeps doing everything Studio drives:

| Studio feature | Through | Why the pane can't take it over |
|---|---|---|
| Analyze, Pre-translate | the provider's `SearchSegments` | Studio's batch tasks only talk to TM providers |
| Learning on confirm (Update) | the provider's update calls, into EverTM's inbox | Studio decides when a segment is confirmed and which TMs update |
| Studio's Concordance window | the provider's concordance search | Studio's window |
| Auto-applying the best match on opening a segment | Studio's own setting | Studio picks from all its TMs |
| Penalties and project TM settings | `SearchSettings` handed to the provider | per project, in Studio |
| The match % in the editor's status column | Studio, from the applied match | the pane sets it on insert if Studio keeps it (section 3) |

- **The pane never learns anything itself.** Inserting writes the target only; confirming the segment then learns
  it through the provider, as now. Otherwise one confirmation could be stored twice, with two different times.
- **The pane reads EverTM even when it is not in the project's TMs.** EverTM is one memory for everything, so
  that is useful. But confirmed segments are only learned when EverTM is in the project's TMs with Update ticked, so
  the status line says so once per document.
- **Keep EverTM's "Lookup" box ticked in the project.** Unticking it to keep EverTM out of Translation Results
  would also take it out of Analyze and Pre-translate.

## 6. Filtering and sorting

### What the pane can do with what it has (phase 1)

- **Sort** the matches by score (default) or by date; a click on a column-like header in the row area.
- No filters in phase 1. The details area shows all values, which is what Michael asked for first.

### With metadata phase 2

Phase 2 of the metadata note brings "only / never" rules per project (Subject, Client, Project), client-first
winners and context matches, applied **inside the index search** (Lucene filters), so a filtered-out match makes
room for the next allowed one instead of leaving a gap.

- **The pane uses the same rules as the provider**, through the same `VaultLibrary.Lookup` call. The pane and
  Translation Results then never disagree about what is allowed.
- **Quick filters in the pane:** chips for the Clients, Subjects and Sources present in the current matches ("ACME
  (2) · Globex (1)"), clicked to show only those.
  - These come from the matches already fetched (up to 20), not from a new index search. That's instant, but a
    filter can only show what was fetched. The pane says so: "showing 2 of 9 matches".
  - A filter that searches the whole index again (for example "only ACME, in all of EverTM") would use phase 2's
    Lucene filters. That's one more background lookup, not a new mechanism.
  - Quick filters only change what the pane shows. They never change what Analyze or Pre-translate see, and they are
    not saved. Saved rules belong in the project's EverTM settings, as the metadata note decides.
- **Sort by client:** "this project's client first" comes free with phase 2's client-first winner.
- **"Show what the rules hid":** a switch to see matches the project's rules removed, greyed out. Cheap once the
  rules exist (the same lookup without the filter). Phase 3, if wanted.

## 7. The one core addition: all translations of a source

Today:

- `VaultIndex.Candidates` reads a source's whole group of translations from doc values (cheap numbers, no text).
- It counts the distinct translations (`Older`) and loads the text of the winner only. So "Older translations: 3"
  is all anyone can see, in Studio and in the pane alike.
- Fuzzy candidates are thinned the same way (`NewestPerSource`).

The pane needs a new core call, for the match the user selected:

> `VaultLibrary.Translations(match, page)`: every distinct translation of that match's source in this direction,
> newest first (the exact variant first, as for the winner), each with its own fields, dates and users, **50 at a
> time**.

- It orders the whole group from the doc values (5,000 units: about 15 ms, measured), then reads stored text only
  for the page asked for. Reading one stored unit costs about 0.35 ms, so a page of 50 costs about 18 ms. "Show
  more" fetches the next page.
- It runs on the pane's background worker, only when a match is selected, never for every segment.
- This is a **Plugin Core change**. Only one session at a time may change Plugin Core (`local_33715770` holds it
  now, for metadata phase 1), so it waits for that session's work to be merged, or is handed to it.

**The 20-value cap (decided: keep it for now).** The store keeps at most 20 values per field per unit (metadata note,
section 2). The pane shows all that are stored. When a field has exactly 20 values, the pane says "(20 shown; more
may exist)", so a cut-off list is visible; only then is raising the cap considered, in the store, not in the pane.

## 8. Removing a wrong translation

Michael, 9 Nov: "Yes, add removing." For a typo that got confirmed, or a bad translation that came in with an agency
TM.

### In the pane

- Right-click a match or an older translation and choose **Remove from EverTM**, or press Delete while the pane has
  focus. No "are you sure?" dialog: an undo is better than a dialog people learn to click through.
- The status line then says: "Removed from EverTM: 'The pump is activated…' (2 units). **Undo**". Undo stays
  available until the next removal or until the pane is closed.
- The pane looks the segment up again at once, so the next translation (if any) takes its place.
- If that translation is already in the target, removing it doesn't change the target. Removing only stops EverTM
  offering it.

### What "a translation" means here: the text, in both directions

- **What is removed:** the pair "this source text, this translation", plain text, in this base language pair. That
  covers every stored copy that differs only in formatting (EverTM stores the same sentence with different tags as
  separate units) and every variant (nl-BE or nl-NL, en-GB or en-US).
  - Without that, the same typo would come straight back as a "formatting variant" of what was just removed.
- **Both directions.** A unit is one pair, used both ways: nl "A" ↔ en "X". Removing it removes it for nl > en and
  for en > nl.
  - That's what a translator expects. A bad agency translation is a wrong pairing whichever way round it's read.
  - With a typo in the translation, the reverse direction would only ever match a document with the same typo.
    Losing that costs nothing.
- **Not proposed:** "never offer for this direction" or "for this variant". A spelling-variant problem (en-US
  wording in an en-GB job) is already handled by the exact variant winning first (metadata note 3b). A direction-only
  removal would need a mark per direction checked in every lookup: state that earns nothing for the cases Michael
  describes.

### The "removed" mark (tombstone)

A plain delete would come back the next time Michael re-imports the TMX or Studio TM it came from, or re-imports
Workbench. So removing does two things in one store transaction:

1. **It deletes the unit rows** (and their metadata links). The store's existing triggers log each delete in the
   `changes` table, which is how the index learns about it (below).
2. **It writes a mark** in a new small table, `removed`, keyed by the base language pair plus a hash of each side's
   normalised plain text: the same normalisation exact matches use, so formatting and variants don't matter. The
   mark also keeps a full copy of what was removed (texts with tags, variants, origin, dates, users, field values),
   when and by whom, so it can be restored exactly.

Why a separate table and not a "removed" flag on the unit:

- Many things read `units`: the index build, the export, the counts, the metadata summary. With a flag, each would
  have to remember to skip removed units, and one that forgot would quietly show them again.
- With the rows deleted, nothing can read them by mistake. Only the code that *adds* units has to check the marks,
  and all adding goes through one place, `VaultStore.Writer`.

**What the marks stop:**

- **Imports** (TMX, Studio TM, Workbench): a unit matching a mark is skipped and counted. The summary says "12 units
  skipped: you removed them from EverTM". The metadata merge for re-imported units skips them too.
- **Confirmations through Studio's batch task "Update Main Translation Memories":** skipped. That task stores whatever
  is in the document, including a removed translation that Pre-translate put there before the removal.

**What a mark gives way to: confirming it yourself, in the editor.** If Michael types or applies exactly that
translation and confirms the segment, that is a deliberate act: the mark is deleted and the unit is stored again,
with its old metadata from the mark plus the new confirmation's date and user.

- This needs EverTM to tell an editor confirmation from the batch task. Both reach the provider as updates. Whether
  their settings differ reliably has to be checked in Studio's code before this is built (risk 9). If they can't be
  told apart, the safe default is that **both** bring it back: a removed translation that keeps coming back is
  visible and can be removed again, while one that silently never comes back after a deliberate confirmation is not.

### Where it disappears

With the units gone from the store and the index, it disappears everywhere EverTM is read, with no extra code in
each place:

| Where | How |
|---|---|
| Exact and fuzzy lookups (pane and Translation Results) | the index no longer has it |
| Concordance (Studio's window) | the same |
| Analyze, Pre-translate | the provider's lookups use the same index |
| Batch Translate and other AI features that use TM matches as context | they reach EverTM through Studio's lookups or `VaultLibrary.Lookup`, both on the index |
| "Older translations: N" | counted from the index group, so it drops by one |
| Newest-wins | an older translation of the same source becomes the winner again |
| Supervertaler for memoQ | the store and index are shared, so memoQ stops offering it too |
| TMX export | not included: the unit isn't in the store. No option to include removed translations, since nobody has asked for one |

**What it can't reach:** Studio's Translation Results keeps its results for the segment it is showing until it
looks it up again. Analyze reports already made, and targets already pre-translated, stay as they are.

### At once, in every running Studio, without a rebuild

This reuses how learning updates the index today (`VaultLibrary.SyncIndexLocked`):

- The store's triggers log every change in `changes`. The index applies them in place, one Lucene `DeleteDocuments`
  or `UpdateDocument` per unit, and commits. The cost depends on the number of changes, not on the size of the
  store.
- Every running Studio picks up the commit within 2 seconds (`VaultLibrary.Current` reopens the same index
  generation when it has new commits).
- A removal is 1–5 units, so this takes milliseconds.

Two gaps, both closed the way learning closes them:

1. **This Studio, before the index has caught up.** Another Studio may hold the store's write lock for a long import.
   Removals then go through the inbox (`inbox.db`, as confirmed segments do) and wait for it. Meanwhile this process
   keeps the removed pairs in a small list, and `VaultLibrary.Lookup` filters them out. This is the mirror image of
   the list of units just confirmed that it already adds in (`RecentCandidates`). An entry leaves the list once the
   index has the change.
2. **Another Studio's own "just confirmed" list.** If another Studio confirmed exactly that translation in the last
   minute, its list may offer it for up to a minute. Rare and short; accepted.

- **Failure paths:** if writing the removal fails (disk full, a store locked for too long), it stays in the inbox and
  is retried, as confirmed segments are, and the status line says "Removal waiting: EverTM is busy". The in-process
  filter keeps hiding it meanwhile. A removal that is never written must not leave the filter hiding it forever: the
  filter lives only in this Studio session, so a restart shows the truth.
- **The `learned` table:** if a removed unit was learned from a confirmation, its `learned` row goes in the same
  transaction. Otherwise a later correction of that source would try to update a unit that no longer exists.

### Undo and restoring

- **Immediate undo** in the pane's status line (above).
- **A "Removed translations" list in the EverTM window** (`SuperVaultDialog`): newest first, 50 at a time, each with
  its texts, when it was removed and by whom, and a **Restore** button.
  - Restoring re-inserts the copy kept in the mark, metadata included, and deletes the mark. The index picks it up
    as any new unit.
  - This costs almost nothing, because the marks are kept anyway. Without the list, a removal noticed a week later
    could only be undone by finding and re-importing the original file, and the mark would skip it even then.
- Undo and Restore are the same core call.

### Plugin Core calls needed

These wait for the metadata merge, like the "all translations" call (section 7).

| Call or change | What it does | Scale |
|---|---|---|
| Store schema 4: table `removed` (pair, source hash, target hash; key), the copy as JSON, removed when and by whom | in place, in milliseconds, like schemas 2 and 3 | thousands of rows at most; KBs |
| `VaultLibrary.Remove(match)` → `RemovalId` | finds every unit of that plain pair from the index (the source's group, then the target hash in doc values), then queues the removal through the inbox. Returns at once and never throws | doc values for one group: about 15 ms for 5,000 translations |
| `VaultLibrary.Restore(RemovalId)` | undo and restore | a few rows |
| `VaultLibrary.Removed(page)` | the list for the EverTM window, 50 at a time | one indexed query |
| `VaultStore.Writer`: check the marks before adding | imports and batch updates skip marked pairs; editor confirmations clear the mark | one indexed lookup per added unit, skipped entirely while `removed` is empty. At about 8,000 units a second, the import speed is unchanged |
| `VaultLibrary.Lookup`: filter this process's pending removals | the in-process gap above | a set of a few hashes |
| Inbox: a "remove" and a "restore" kind next to "learn" | the same retry and ordering as confirmations | – |

- **32-bit Studio 2024:** nothing here holds more than one group's numbers or one page of 50 in memory. No rebuild,
  and the index writer is the one learning already uses (16 MB buffer).
- **10M units:** a removal costs the same as at 871k: one group read, a few row deletes, a few Lucene deletes.

### Safety

- **Removing only ever touches EverTM:** its store (`D:\Supervertaler\evertm\evertm.db`), its index and its inbox. It
  never writes to a Studio TM, a TMX file, Workbench's `supervertaler.db`, or the document.
- The pane's message says so: "Removed from EverTM only. Your Studio TMs and files are unchanged." If the wrong
  translation is also in a project TM, Michael fixes it there with Studio's own tools.
- Tests use a scratch store only, never `D:\Supervertaler\evertm`.

## 9. Risks, scale, phasing and effort

### Scale: designed for 10 million units and sentences with thousands of translations

| What | Cost at 10M units | Why it stays small |
|---|---|---|
| Lookup per segment | about 26 ms median today at 871k; candidate selection measured at 22 ms on a 9.3M copy | off the UI thread; at most one running and one waiting |
| Exact group of 5,000 translations | about 15 ms (measured, metadata phase 1) | doc values only; text for the winner only |
| Older translations of that group | about 18 ms per page of 50 | paged; never all 5,000 texts at once |
| Drawing | only the rows on screen | owner-drawn list; long segments wrap |
| Memory | one segment's matches and one page of older translations | nothing cached between segments |
| Fields | up to 20 values × 7 fields, for the selected match only | the details area shows one match |

- **Tested at, designed for:** the measurements above are from Michael's 871,290-unit store copy and a 9.3M-unit
  copy for candidate selection. The pane adds no per-unit work. It needs one check on a 10M-unit copy, with a
  5,000-translation group, in 32-bit Studio 2024, where memory is tightest.

### Risks

1. **The segment label:** Studio's editor may relabel an inserted match as "interactive" whatever we set. Try this
   in Studio first, before building the insert, because it decides how the insert is written (section 3).
2. **Tag placement differing between the pane and Translation Results** (section 3): covered by the comparison test.
3. **Focus:** Translation Results bringing itself to the front, and focus after a mouse insert. Known behaviour, with
   known answers in the code already.
4. **Shortcut clashes** with Studio's defaults in one of the three versions: check before choosing.
5. **Plugin Core is busy:** the "all translations" call has to wait its turn.
6. **The pane and Translation Results both open** show the same matches twice. That's harmless but noisy; the Help
   page should suggest closing Translation Results.
7. **Lookups after a big import:** while EverTM rebuilds its index, lookups use the old index (or none). The pane
   says so in its status line instead of looking empty.
8. **A removal during a full rebuild:** a rebuild reads the store from scratch, so the deleted units aren't in it.
   But this process's filter must stay until the new generation is in use, not only until the old one has the
   change. Test it.
9. **Telling an editor confirmation from the batch task "Update Main Translation Memories"** (section 8): check it in
   Studio's code before building. If they can't be told apart, both bring a removed translation back.

### Phasing

**Phase 1 (after metadata phase 1 is merged): the pane is useful on its own.**

- `EverTmViewPart`, docked bottom, unpinned; a status line; licence overlay without a licence.
- Background lookups on segment change, newest request wins, nothing on the UI thread but copying the source.
- Exact and fuzzy matches, best first, with diff highlighting on the stored source.
- Under each match, the faint one-line summary (Client · Project · date, plus a differing variant; section 1), also
  in the older-translations list.
- Details for the selected match: every field and value, created/changed date and user, when it entered EverTM,
  variant, Source; "N older translations" opening the paged list (the new core call).
- Insert matches and older translations by double-click, Enter, and Alt+Shift+1–9 (after the clash check). Tags as
  the provider places them; focus back to the editor; `SegmentWriteCheck`; labelled "TM, 87%, EverTM" as far as
  Studio allows.
- Sort by score or date.
- Remove from EverTM (section 8): the mark, the in-place index update, the immediate undo, and the "Removed
  translations" list in the EverTM window.
- Tests:
  - stale results are never drawn (segment changed during a lookup);
  - the document is closed during a lookup;
  - a missing, rebuilding or broken index;
  - a lookup that throws;
  - insert into a locked segment, and into one with unaccepted tracked changes;
  - a match without placeable tags;
  - the pane and the provider placing tags identically;
  - a 5,000-translation group, paged;
  - the summary line: missing Client, missing Project, both missing (Source shown), no date, several values ("+N"),
    a very narrow pane ("…", the date and variant kept), a differing variant on one or both sides, a bare language
    (not flagged), and every row the same height;
  - holding the down-arrow through a long file (at most one lookup running);
  - removing:
    - re-importing the source TMX, a Studio TM or Workbench skips the removed pair;
    - its formatting variants and other variants go too;
    - an editor confirmation brings it back, and the batch task doesn't (if the two can be told apart);
    - a second running process stops offering it within 2 seconds;
    - a removal waiting behind another process's import is still hidden here, and is written afterwards;
    - a removal during a full rebuild;
    - undo and restore bring back the metadata;
    - a learned unit removed, then its source corrected;
    - the store's other tables and the protected files are unchanged.
- **Rough effort:** 6–8 working sessions. The pane and its drawing take the most; then the insert and its tests in
  Studio. The "all translations" call takes about one session, and removing about two (core and tests, then the
  pane and the EverTM window).

**Phase 2 (with metadata phase 2):** the project's rules applied in the pane, CM badges, client-first, quick filter
chips, sort by client. The "Language variants" setting (roadmap 2f) comes with them, through the same core lookup:
a penalised match shows its lower score, the details area says "Other variant: -2%", and under "Exact only" other
variants are hidden like any filtered-out unit (counted in "Filtered: N hidden", shown greyed by "Show
filtered-out"). About 2 sessions on top of metadata phase 2 itself.

**Phase 3 (if wanted):**

- EverTM Concordance inside the pane, with full details and older translations (Studio's Concordance window has the
  same cut-off field column);
- "show what the rules hid";
- a compact best-match line in TermLens for people who don't want another pane;
- editing a unit from the pane (removing is in phase 1);
- the same pane in Supervertaler for memoQ.

### For the Help page, later

`trados/evertm.md` would gain a section "EverTM Matches":

- how to open it (View > EverTM Matches), dock it, and close Translation Results;
- what the colours in the diff mean;
- the insert keys;
- that the pane shows every field and value and the older translations;
- removing a wrong translation: what it removes (both directions, all formatting), that re-imports skip it, that
  confirming it yourself brings it back, Undo and the "Removed translations" list, and that Studio TMs and files are
  never touched;
- that Analyze and Pre-translate still use EverTM through the project's TM settings, so EverTM stays in the
  project's TM list with Lookup and Update ticked.

## Studio TM matches in the pane (step 5, built 23 Nov 2026)

Michael, 23 Nov 2026: an option to show matches from the project's Studio TMs too, clearly marked, for translators with
many Studio TMs who prefer this pane. His answers: the same translation once, as EverTM's, "also in <TM>"; EverTM first
on equal scores; the project's penalties applied. Built in `SuperVault/StudioTmMatches.cs`:

- Which TMs: `TmSearcher.FindLookupTms` (Enabled and Lookup ticked, EverTM apart), now also returning each TM's
  `Penalty` from the cascade entry.
- Studio's `SearchSegment` per TM, NormalSearch, Studio's default formatting and multiple-translation penalties, the
  project's provider penalty (applied by hand if the TM doesn't). NormalSearch returns a 100% alone, as Translation
  Results does.
- Each unit is put in EverTM's shape by `VaultLookup.Search(null, document, …, extra: [unit])`, so differences, tag
  placement, numbers and inserting are the pane's own code; the score is set back to Studio's (99 when Studio's 100
  isn't the segment's own text). No Plugin Core change.
- A worker of its own (newest segment wins); TMs searched in parallel, 5 s in all; a TM still running from an earlier
  segment is not searched again; two failures or time-outs skip it for the document. The opened TMs live in one
  `Session` per document, used only on that worker (a lock guards the open-TM table against a search still finishing).
- The last answer is kept for its segment, so the 4-second second look and a removal's new lookup don't search the
  Studio TMs again.
- 23 Nov, after Michael's test: the pane follows EverTM's Enabled and Lookup ticks in the project ("It should follow
  the ticks, like Studio does"; `TmSearcher.EverTmLooksUp`, null outside a project so EverTM is never hidden there).
  This reverses the 9 Nov choice of showing EverTM in every project. Both the EverTM ticks and the Studio TM list are
  read again when the .sdlproj's save time changes (one file-date read per lookup). Studio TM rows were made to stand
  out ("Make the Studio TM matches stand out more"): blue left stripe, Studio-blue score, filled blue TM-name chip;
  EverTM's rows a Studio TM also has keep EverTM's colours with "also in …" in plain blue text (the outlined label drew the eye too much).
- Scale: tested with one scratch .sdltm (a search about 120–220 ms cold); designed for up to ~10 TMs and multi-GB
  .sdltm files. With 50 TMs each segment waits longer for them, but EverTM's matches never do.

## Decisions (Michael, 9 Nov)

Michael: "I agree with all your suggestions, and number 5: yes, it should need a licence."

1. **An inserted match is labelled as a TM match** ("TM, 87%, EverTM") if Studio allows it. If it doesn't, the note
   says what Studio does allow (section 3, "Writing it").
2. **Alt+Shift+1–9** is the default for "insert EverTM match 1–9" (after the clash check; reassignable in Studio).
3. **Older translations can be inserted too.**
4. **The 20-values-per-field limit stays for now.** It is raised only if a list is visibly cut off.
5. **The pane needs a licence**, like TermLens and the Assistant.

6. **Removing a wrong translation from EverTM** is added ("Yes, add removing"): section 8.

Still open: Michael may add to what happens with older translations; the orchestrator will pass that on.

Decided later on 9 Nov (the two removal questions; Michael: "Agree with both suggestions."):

7. **A deliberate confirmation in the editor brings a removed translation back; the batch task "Update Main
   Translation Memories" does not.** If Studio can't tell the two apart, both bring it back. Which of the two applies
   is checked in Studio's code as the first step of building removal (risk 9), and recorded here.
   **Checked 23 Nov 2026 (Studio 2024): they can be told apart.** The editor's confirm builds its ImportSettings in
   `InteractiveTmUpdateImportSettingsBuilder` with `IsDocumentImport = request.DocumentUpdate`, false for a confirm;
   the batch task's `AbstractUpdateTmContentProcessor.Initialize` sets `IsDocumentImport = true`. So EverTM's provider
   passes `FromBatch = settings.IsDocumentImport`, and only an editor confirmation brings a removed translation back.
8. **The "Removed translations" list with Restore stays**, in the EverTM window, as well as the immediate Undo in the
   pane.

Added on 10 Nov (Michael, through the orchestrator):

9. **A faint one-line metadata summary under each match** (Client · Project · date), never a column like Studio's
   Translation Results. Missing fields are left out; Source stands in when there's no Client or Project; several
   values show as the first plus "+N"; a narrow pane cuts Client and Project with "…" and keeps the date (section 1).

10. **Language variants** (roadmap 2f): the summary line flags a variant that differs from the project's, in every
    mode. The pane applies the "Language variants" setting through the core lookup, as Studio's results do.

Nothing else is open in this design. Building starts once the metadata work is merged.
