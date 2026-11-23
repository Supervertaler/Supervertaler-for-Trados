# EverTM Matches: notes for the help page

User-facing notes, kept while building, for docs.supervertaler.com/trados/evertm/ (a new section "EverTM Matches").
Design: `evertm-pane.md`. Parts not built yet are marked *(later)*.

## What it is

- A pane of its own that shows EverTM's matches for the segment you are in, with everything EverTM knows about each.
- It can take the place of Studio's Translation Results for EverTM. Studio's other TMs still show in Translation
  Results.
- It needs a Supervertaler licence (or the trial), like TermLens and the Assistant. EverTM itself, in your project's TM
  list, stays free.

## Opening and placing it

- **View > EverTM Matches** in the Editor. It opens at the bottom of the Editor; drag it wherever you like and Studio
  remembers.
- **Don't tab it together with Translation Results.** Studio brings Translation Results to the front on every segment,
  so EverTM Matches would keep disappearing behind it. Either close Translation Results, or put the two in different
  places.
- If Translation Results stays open, EverTM's matches show in both. That's harmless.
- **Closing Translation Results doesn't stick, and it jumps to the front on every segment.** Studio does this itself
  (read from Studio 2024's editor, 22 Nov 2026): each time it looks a segment up, it asks to show Translation
  Results, which reopens it if closed and brings it forward. There is no setting for it. The one exception Studio
  makes: it doesn't bring Translation Results forward when that would hide the **Messages**, **Comments** or **TQA**
  pane. So:
  - drag Translation Results onto the **Comments** (or Messages) pane so the two become tabs of one group, and leave
    the **Comments** tab selected. Keep that group pinned (not auto-hide).
  - From then on Studio no longer brings Translation Results forward; it only makes its tab blink.
  - Click the Translation Results tab whenever you want to see your Studio TMs' matches; click Comments to park it
    again.
  - Give EverTM Matches a place of its own (for example where Translation Results used to be).
  *(to confirm in Studio: how much the tab blinks)*
- **Or let Supervertaler keep Translation Results back:** EverTM Matches > Options > **Keep Translation Results
  behind Supervertaler's panes** (off to start with). Then Translation Results can stay in the same tab group as
  TermLens, EverTM Matches or TermPicker: when Studio brings it forward after a segment change, Supervertaler puts its
  own pane back. The cursor stays in the editor. Click the Translation Results tab yourself and it stays in
  front, until you click a Supervertaler tab again.
  - **Translation Results still shows for a second or two on each segment.** Studio keeps its window busy while it
    looks the segment up in your TMs, and no plugin can act until it has finished. If that flash bothers you, use the
    Comments/TQA parking above instead.
  - Choosing Translation Results from Studio's **View** ribbon doesn't count as choosing it: Supervertaler puts its
    pane back. Click the Translation Results tab instead.

## Options (top right of the pane)

- **Source and target side by side** (the default, as in Translation Results) or **Source above target**, with a thin
  line between the two either way.
- **Lowest match shown:** 50% to 100%; 70% to start with.
- **Most matches shown:** 1 to 20; 10 to start with.
- **Best match first** or **Newest first**.
- **Details shown:** a tick for each item of the details area (Project, Client, Subject, End client, Document, Came from,
  Note, Created, Changed, In EverTM since, Language variants, Older translations, Numbers, Formatting, Differences).
  Untick the ones you don't need; **Show all** brings them all back. The menu stays open while you tick.
- Remembered between Studio sessions, except the order, which starts on best match first.

## EverTM switched off in a project

- The pane follows EverTM's own row in the project's TM list, as Studio does: with its **Enabled** or **Lookup**
  unticked, the pane shows no EverTM matches, and the status line says "No EverTM matches: EverTM is off in this
  project's TMs". Your Studio TMs' matches still show if that option is on.
- Ticking it again works from the next segment once Studio has saved the project settings. A document that isn't in a
  project always shows EverTM's matches.

## What each match shows

- **Number and score.** Green for 100%, orange from 85%, brown below. Best first, from the lowest match you chose.
- **The stored source, with the differences from your segment:**
  - a word the stored sentence has and yours doesn't: struck through in red;
  - a word yours has and the stored one doesn't: green, underlined, where it goes;
  - a changed word: the stored word struck through, then yours;
  - formatting tags: small grey marks (‹ opens, › closes, • stands alone).
  - These are the same differences the score is based on, and the ones Translation Results shows.
  - A very long segment (over 400 words and spaces) can't be compared word by word: the line under it says "too long
    to compare word by word".
- **The stored translation.** A number that differs from your segment is already replaced, as in Translation
  Results.
- **A faint line under it:** Client · Project · date (last changed, else created; "undated" without one).
  - Missing ones are left out. With neither Client nor Project, the Source (the TMX or TM it came from) is shown.
  - Several values: the first and "+2".
  - A language variant other than your project's is added at the end ("· en-US" in an en-GB project).
  - In a narrow pane, Client and Project are shortened with "…"; the date is always shown whole.
- **"2 older ▸"** at the right: EverTM has 2 older translations of the same sentence, and shows only the newest.
  Click it to see them (see below).

## The details under the list

- First line after the match: **Found in**, with a quiet tag per place: green "EverTM" and a blue tag for each Studio
  TM that has it.
- Then **EverTM entry** (or **Studio TM entry** for a match only a Studio TM has): the fields, dates and users.
- **Came from** (EverTM's old "Source") says where EverTM got the entry, in words: "confirmed in Studio project
  BRANTS (HELLO-003)" (learned when you confirmed it there), "imported from Studio TM Acme.sdltm", "imported from TMX
  file Acme.tmx", or a Workbench TM's name. It is history, not a TM that has it now; that's "Found in".

- For the match you select: every field with all its values (Project, Client, Subject, End client, Document, Came from,
  Note), created and changed (date and user), when it entered EverTM, its language variants, and whether a number
  was replaced or the formatting can't be placed.
- A field with 20 values says "(20 shown; more may exist)": EverTM keeps at most 20 values per field.
- Drag the line between the list and the details to give either more room.

## Older translations

- Usually you only want the newest, so that's all the pane shows. To see the others, click **"2 older ▸"** on the
  match (or right-click it and choose **Show older translations**). They're listed under its details:
  **Older translations (2)**. Click "2 older ▸" again, or pick another match, to close them. Best first, by the same rule as the match itself: your project's own variant (en-GB in an en-GB project) first,
  then the newest.
- Each shows its translation and a faint line with its client, project and date. Click one to see all its details.
- **Double-click one, or select it and press Enter, to insert it** instead of the match. It goes in just like a
  match: with your segment's formatting, a different number put right (claim 8 becomes claim 9), labelled as a TM
  match, never confirmed. Alt+Shift+1–9 and the digit keys still insert the matches in the list above.
- A sentence with many translations shows 50 at a time: **Show 50 more** at the right.
- Not wanted at all? Options > Details shown > untick **Older translations**: no "2 older" on the matches, and
  they're never looked up.
- They're looked up only when you click, in the background, so moving through segments is never slowed.
  A segment you confirmed in the last minute isn't listed yet.

## Removing a wrong translation

- A typo you once confirmed, or a bad translation from an agency TM: right-click the match (or an older translation)
  and choose **Remove from EverTM**, or select it and press **Delete**. There's no "are you sure?": the status line
  says what was removed and offers **Undo** until you remove something else.
- What goes: every copy of that sentence with that translation, whatever its formatting or language variant, in both
  directions (it's no longer offered for English > Dutch either).
- The next translation takes its place, usually within a second.
- **Only EverTM changes.** Your Studio TMs, TMX files and the document are never touched, and a translation already
  in the target stays there. If it's also in a project TM, fix it there with Studio's own tools.
- It stays removed: importing a TMX file or Studio TM that has it skips it ("skipped: you removed it from EverTM"),
  and so does Studio's batch task **Update Main Translation Memories**.
- **Confirming it yourself in the editor brings it back** — that's a deliberate choice, so EverTM respects it.
- To bring one back later: EverTM window (Project Settings > Translation Memory > EverTM > Settings) >
  **Removed translations...** > select it > **Restore**. It comes back exactly as it was, with its client, project
  and dates.

## Editing a translation

- Right-click a match (or an older translation) > **Edit in EverTM...**, or select it and press **F2**. As in
  Studio's Edit Translation Unit, a window shows the source and target to edit, and its fields.
- Tags show as grey markers (‹1›Save‹/1›). Type around them: they can't be typed over. If one goes missing, Save
  says so and saves nothing.
- Fields: Project, Client, End client, Subject, Document and Note. Several values: separate them with ";". Where the
  unit came from (Source), its dates and the language variants are shown, not edited.
- **Save** changes EverTM only: never your Studio TMs, files or the target. The status line offers **Undo**.
- What an edit does:
  - Every copy of that translation (any formatting or language variant) becomes one unit with your edited text, in
    the formatting of the one you edited. It keeps its original creation date, and is "changed now, by you", so it's
    the newest translation of that sentence.
  - The old text is marked removed, so importing a file that has it won't bring it back. It shows in
    **Removed translations** as "(edited)", where you can restore it.
  - Changing only the fields marks nothing.
- Undo puts back exactly what was there before.

## Matches from your Studio TMs too

- **Options > Also show matches from the project's Studio TMs** (off to start with). For translators with many Studio
  TMs who'd rather work from this pane than from Translation Results.
- Which TMs: the ones Studio itself looks up in for this document: the project's TM list for its language pair, those
  with Lookup ticked (EverTM itself not counted). File TMs and GroupShare TMs.
- Change a TM's Enabled, Lookup or penalty in Project Settings and the pane follows from the next segment, once
  Studio has saved the project. No need to reopen the document.
- How to tell them apart at a glance: a Studio TM's match has a **blue stripe down its left edge**, its **score in
  Studio blue** (EverTM's are green or orange) and **its TM's name in a pale blue tag** under the text. Its details say
  "Found in" with that TM's blue tag, and a "Studio TM entry" block with its file or server address.
- Studio TMs are named with their file extension ("BRANTS (HELLO-003+004-BE-EP).sdltm"), so they read as Trados
  memories at a glance; a GroupShare TM as "Legal (GroupShare)". In a narrow pane a long name is shortened, and
  keeps its ".sdltm".
- The scores are Studio's own, with the project's penalty for that TM, so they match Translation Results. One
  difference: a "100%" whose source isn't exactly your segment's text (Studio does that when only numbers differ) is
  shown as 99%, as everywhere in Supervertaler.
- As in Translation Results, when a Studio TM has a 100% match it offers that one alone, without its fuzzy matches.
- **The same translation in EverTM and a Studio TM shows once, as EverTM's** (green or orange score, no stripe), with
  "also in Hydraulics_TM.sdltm" in plain blue on its summary line ("also in 2 Studio TMs" when several), and all of
  them under "Found in" in its details. On equal scores EverTM's match comes first. "Most matches shown" counts both.
- EverTM's matches appear straight away; the Studio TMs' are added a moment later ("searching Studio TMs…" in the
  status line). Then the status line says how many Studio TMs were searched, what they found ("2 matches, all also in
  EverTM"), and how long they took.
- Inserting a Studio TM's match works the same way (formatting included); the segment is labelled with that TM's name
  ("TM, 87%, Hydraulics_TM").
- **Edit in EverTM**, **Remove from EverTM** and older translations are for EverTM's matches only. On a Studio TM's
  match, the right-click menu says to edit or delete it in Studio's Translation Results.
- A TM that can't be opened, or takes more than 5 seconds, is left out of that segment and the status line names it
  ("left out: Hydraulics_TM (took over 5 s)"). One that fails or is slow twice is skipped until you open another
  document.

## The status line

- Normally: how many translation units EverTM holds, how many matches, and how long the lookup took.
- Instead, when there's something to know: "EverTM is empty…", "EverTM is being rebuilt…" (matches still come from
  the index before the rebuild), "The EverTM index could not be opened… Rebuild it.", "EverTM lookup failed: …".
- "EverTM is not in this project's TMs with Update ticked: segments you confirm won't be learned". The pane reads
  EverTM in any project, but EverTM only learns your confirmed segments when it is in the project's TM list with
  Update ticked. (Read from the saved project, so it updates after Studio saves the project settings.)
- After inserting: "Match 87% inserted", or "… inserted without formatting"; for a Studio TM's match, "Match 87%
  from Hydraulics_TM inserted".
- With Studio TM matches on: "searching Studio TMs…", then "3 Studio TMs: 2 more matches · 40 ms", and any TM left out.

## Inserting a match

- **Double-click** it, or select it with **Up/Down** and press **Enter**, or press **1** to **9** while the pane has
  the keyboard.
- **From the editor, without leaving it: Alt+Shift+1** to **9** inserts match 1 to 9.
  - **Studio uses the same keys for its own "1. Insert symbol" to "9. Insert symbol", and Studio's own actions always
    win** (read from Studio's ActionService, 22 Nov 2026: on a shared key it takes the first action that is switched on,
    and puts its own before every plugin's; its symbol actions count as on whenever a document is open, even with an
    empty symbol list). So, once: **File > Options > Keyboard Shortcuts > Editor**, select "1. Insert symbol" to
    "9. Insert symbol" and remove their shortcuts (or give them other keys). From then on Alt+Shift+1–9 insert EverTM
    matches. Or give "EverTM: Insert match 1" to 9 keys of your own there. Do this in each Studio version you use.
    (Confirmed by Michael in Studio 2024, 22 Nov 2026.)
  - Put this step prominently on the Help page, near the top of the EverTM Matches section: without it the keys
    silently do nothing.
- After inserting, the cursor is in the target, so you can keep typing (confirmed for Alt+Shift+1).
- **Inserting never confirms the segment.** Confirm it as usual (Ctrl+Enter); that is also when EverTM learns it.
- Formatting tags land exactly where Translation Results puts them. When EverTM can't place them with confidence, the
  text goes in without them and the status line says so.
- It replaces the target, like applying a match from Translation Results. It can be undone (Ctrl+Z), it doesn't write
  into a locked segment, and with unaccepted tracked changes in the target it says so rather than overwriting them.
- **The segment is labelled as a TM match**, "TM, 87%, EverTM", like a match applied from Translation Results: the
  editor shows the match % in its orange box (confirmed by Michael in Studio 2024, 22 Nov 2026).
- A fuzzy match goes in as it was stored; the words your segment has and the stored one doesn't (the green ones in the
  pane) are for you to add, as with any fuzzy match.

## While you move through the document

- The lookup runs in the background: moving from segment to segment is never slowed down by it.
- When you move on, the matches shown turn grey until the new segment's arrive, and grey matches can't be inserted,
  so a match never lands in the wrong segment.
- When the pane is hidden, nothing is looked up.
- A segment you have just confirmed takes EverTM 1 to 3 seconds to take in. So the pane looks the segment you are on
  up once more after 4 seconds: the segment you confirmed a moment ago then shows up as a fuzzy match for the next one
  (very common in patents, where the next sentence is often a near-repeat). If nothing new turns up, nothing on screen
  changes. (Translation Results doesn't do this: it looks a segment up once.)

## What stays with Studio

- Analyze, Pre-translate, learning on confirm, Studio's Concordance window and auto-applying the best match all still go
  through EverTM in your project's TM list. Keep EverTM there, with Lookup and Update ticked.
- The pane never learns anything itself.

## Later

- Filters (by client, subject, source) and the Language variants setting.
