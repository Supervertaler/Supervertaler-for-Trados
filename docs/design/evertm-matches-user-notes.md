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
- **"2 older ▸"** at the right: EverTM has 2 older translations of the same sentence, and shows only the newest.
  Click it to see them (see below).

## The details under the list

- For the match you select: every field with all its values (Project, Client, Subject, End client, Document, Source,
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
- A segment you have just confirmed takes EverTM 1 to 3 seconds to take in. So the pane looks the segment you are on
  up once more after 4 seconds: the segment you confirmed a moment ago then shows up as a fuzzy match for the next one
  (very common in patents, where the next sentence is often a near-repeat). If nothing new turns up, nothing on screen
  changes. (Translation Results doesn't do this: it looks a segment up once.)

## What stays with Studio

- Analyze, Pre-translate, learning on confirm, Studio's Concordance window and auto-applying the best match all still go
  through EverTM in your project's TM list. Keep EverTM there, with Lookup and Update ticked.
- The pane never learns anything itself.

## Later

- Removing a wrong translation from EverTM, with Undo and a "Removed translations" list.
- Filters (by client, subject, source) and the Language variants setting.
