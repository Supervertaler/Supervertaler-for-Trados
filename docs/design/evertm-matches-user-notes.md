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

## Options (top right of the pane)

- **Source and target side by side** (the default, as in Translation Results) or **Source above target**, with a thin
  line between the two either way.
- **Lowest match shown:** 50% to 100%; 70% to start with.
- **Most matches shown:** 1 to 20; 10 to start with.
- **Best match first** or **Newest first**.
- **Details shown:** a tick for each item of the details area (Project, Client, Subject, End client, Document, Source,
  Note, Created, Changed, In EverTM since, Language variants, Older translations, Numbers, Formatting, Differences).
  Untick the ones you don't need; **Show all** brings them all back. The menu stays open while you tick.
- Remembered between Studio sessions, except the order, which starts on best match first.

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
- **"2 older"** at the right: EverTM has 2 older translations of the same sentence, and shows the newest.
  *(later: the older translations themselves, and inserting one)*

## The details under the list

- For the match you select: every field with all its values (Project, Client, Subject, End client, Document, Source,
  Note), created and changed (date and user), when it entered EverTM, its language variants, how many older
  translations there are, and whether a number was replaced or the formatting can't be placed.
- A field with 20 values says "(20 shown; more may exist)": EverTM keeps at most 20 values per field.
- Drag the line between the list and the details to give either more room.

## The status line

- Normally: how many translation units EverTM holds, how many matches, and how long the lookup took.
- Instead, when there's something to know: "EverTM is empty…", "EverTM is being rebuilt…" (matches still come from
  the index before the rebuild), "The EverTM index could not be opened… Rebuild it.", "EverTM lookup failed: …".
- "EverTM is not in this project's TMs with Update ticked: segments you confirm won't be learned". The pane reads
  EverTM in any project, but EverTM only learns your confirmed segments when it is in the project's TM list with
  Update ticked. (Read from the saved project, so it updates after Studio saves the project settings.)
- After inserting: "Match 87% inserted", or "… inserted without formatting".

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

## What stays with Studio

- Analyze, Pre-translate, learning on confirm, Studio's Concordance window and auto-applying the best match all still go
  through EverTM in your project's TM list. Keep EverTM there, with Lookup and Update ticked.
- The pane never learns anything itself.

## Later

- The older translations of a sentence, with their own dates and fields, 50 at a time, and inserting one.
- Removing a wrong translation from EverTM, with Undo and a "Removed translations" list.
- Filters (by client, subject, source) and the Language variants setting.
