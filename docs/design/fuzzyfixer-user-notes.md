# FuzzyFixer: notes for the help page

User-facing notes, kept while building, for the Help page `trados/fuzzyfixer.md` (in the Help repository, marked
"Planned – not built yet"). First built in October 2026, carried over onto 20.208 in November 2026.
**Not yet tried in Trados Studio**: everything below about the Editor (the shortcut, the menu item, the write, the
status) is what the code does, to be confirmed in Studio before the Help page loses its "Planned" banner.

## What it does

- Repairs a fuzzy translation memory match with AI, for the segment you are in.
- Takes the **best match below 100%** from the translation memories the project looks things up in: the ones that are
  switched on and have **Lookup** ticked in Project Settings > Language Pairs > Translation Memory and Automated
  Translation, for the document's language pair. File TMs, server TMs, and EverTM when it is in that list. A memory
  that is switched off, or has Lookup unticked, is not searched. It searches them itself; it does not read what you
  selected in Translation Results.
- The AI is given your segment, the sentence the match was made for, and the match's translation. It changes only what
  the difference between the two source sentences requires, and whatever the sentence then needs to stay grammatical
  (agreement, word order, articles). The rest of the old translation is kept as it is.
- A match of 99% that differs from your segment only in its numbers counts as a fuzzy match and is repaired.
- It also gets what **Translate active segment** (Alt+T) gets: your translation prompt, the termbase terms that occur
  in the segment, and SuperMemory.
- **It does not send the whole document**, whatever "Include full document content in AI context" says on the Batch
  Operations tab. It sends the **three segments before and the three after** your segment, each with its current
  translation if it has one, so that the repaired words agree with the sentences beside them.

## How to use it

- **Ctrl+Alt+F** in the Editor, or right-click in the Editor > **FuzzyFixer: repair fuzzy match with AI**.
  The same shortcut as in Supervertaler Workbench. Like every Supervertaler shortcut, it can be changed in Studio under
  File > Options > Keyboard Shortcuts.
- Voice command: **"repair match"** (also "fix match").
- A window opens at once and says what it is doing: searching the memories, then asking the AI. While it is open the
  Editor waits.
- When the repair is ready, the window shows **what changed in the match's translation**: removed words struck through
  in red, added words underlined in green.
- **Enter** writes the repair into the target, as a **draft**. **Escape** (or Discard) throws it away. Nothing is
  written before Enter.
- You then review and confirm the segment as usual. Ctrl+Z undoes the write.
- With **Track Changes** on, a repair written over text that is already in the target (the fuzzy match you applied,
  say) is recorded word by word: only the repaired words are marked, not the whole segment.

## When nothing is written

The window (or a message) says why, and the target stays as it was:

- No memory the project looks things up in has a match at or above the lowest score.
- The best match is 100%: nothing to repair. Apply it from Translation Results.
- The project has no translation memory, or all of them are switched off or have Lookup unticked.
- No AI provider or API key is set up.
- The AI answered with an error (the window shows the AI's own message), or returned nothing usable.
- The AI returned the match unchanged.
- The AI's repair doesn't carry exactly your segment's tags.
- The segment is locked.
- A batch translation is running.
- You closed the window (that also cancels the AI request).
- Studio declined the write (for example unreviewed tracked changes with Track Changes off): the usual message.

A segment that is already **confirmed** is not refused: the window says it is confirmed, and Enter replaces its
translation and makes it a draft again.

If Studio reports an internal error straight after taking the repair, the repair is in the document and counts as
written; a message says so.

## Settings

- No new settings. The lowest match it will repair is the existing **Settings > AI Settings > Lowest TM match to send
  (%)** (70 unless changed). It works whether or not "Send TM matches" is ticked for Batch Translate.
- It uses the AI model chosen for translation, local models included.
- Needs a licence, like the other AI features.

## Good to know

- **Tags.** The match comes out of the memory as plain text, so the AI places your segment's tags in the repair, as it
  does when translating. A repair with missing, extra or other tags is not written.
- **Cost.** One AI request per repair, smaller than an Alt+T request that sends the document. Usage is recorded with
  Translate active segment.
- **The match percentage** is the memory's own score from Supervertaler's search. It can differ by a few points from
  the percentage in Translation Results, where Studio applies your penalties.
- **One segment at a time.** There is no "repair all fuzzy matches" yet.
- **It does not start from Studio's own repaired proposal** (upLIFT Fuzzy Repair). It starts from the match as the
  memory holds it.

## Selling point (for the Help page and marketing copy)

The idea: FuzzyFixer is AI-powered, so it can repair a fuzzy match with an understanding of the sentence, where the
match repair built into CAT tools is mechanical.

Wording that is true and fair to Studio:

- Trados Studio's own Match Repair (upLIFT Fuzzy Repair, since Studio 2017) puts words and phrases from your termbase,
  TM fragments or machine translation into the old translation. It does not rewrite the sentence around them.
- FuzzyFixer gives the AI both source sentences and the old translation, and the AI rewrites only what the difference
  requires, so grammar and word order can follow the change.
- It is AI repair inside your own CAT tool, with the model you chose. **Never "the first"**: Smartling announced LLM
  fuzzy match repair in May 2024, and XTM, memoQ AGT, Phrase and Bureau Works also give fuzzy matches to an LLM.
- It stands at the end of a long line of mechanical features (Déjà Vu Assemble and DeepMiner, CafeTran Auto-assembling,
  memoQ Fragment assembly and MatchPatch, Studio's upLIFT), not against them. The verified facts are in the Help page
  draft.
- It costs one AI request per repair, and the translator still reviews the result.
- No client names.

### An illustration (invented text)

A claim for an Acme water heater, English to Dutch. The segment:

> in response to the water temperature exceeding a second upper limit, switching off, by the control unit, the heating
> element (12) after a set time has passed, and keeping, by the control unit, the heating element (12) switched off
> until the water temperature falls below the first lower limit;

The memory has an 82% match, made for a claim worded as an apparatus feature ("… switch off the heating element (12)
after a set time has passed, wherein the heating element (12) is configured to stay switched off …"):

> als reactie op het overschrijden van de tweede bovengrens door de watertemperatuur, het verwarmingselement (12) uit
> te schakelen nadat een ingestelde tijd is verstreken, waarbij het verwarmingselement (12) is ingericht om
> uitgeschakeld te blijven totdat de watertemperatuur onder de eerste ondergrens daalt;

A mechanical repair can swap "de tweede" for "een tweede" and drop in "door de regeleenheid", but the clause it leaves
is not Dutch. A good repair rebuilds the two changed clauses and leaves the start and the ending alone:

> als reactie op het overschrijden van een tweede bovengrens door de watertemperatuur, het uitschakelen, door de
> regeleenheid, van het verwarmingselement (12) nadat een ingestelde tijd is verstreken, en het, door de regeleenheid,
> uitgeschakeld houden van het verwarmingselement (12) totdat de watertemperatuur onder de eerste ondergrens daalt;

This example is written by hand to show what the feature is for. It is the text `tools\fuzzyfixer_test.ps1` uses, and
**not** the output of a model. The repair instruction has been tried on a real model once, on one segment, outside
Studio; nobody has judged the result yet. Say nothing about quality in public copy until it has been tried on real
work.

## Not checked yet

- Anything in Studio itself: that Ctrl+Alt+F reaches the action in the Editor of Studio 2022, 2024 and 2026 (only the
  Alignment view claims that key by default, read from Studio's program files), where the menu item lands in the
  right-click menu, the write, the Draft status, undo, Track Changes.
- A real AI answer inside Studio, with the termbase, the neighbours, SuperMemory and the prompt.
- Whether Studio's own Match Repair works on EverTM matches. FuzzyFixer does take EverTM matches, through the search
  Batch Translate uses.
