# Short-term memory in Batch Translate – notes for the Help page and the changelog

Running notes for the user documentation (14 Nov 2026). Design: `docs/design/in-run-references.md`. The report at
the end of a run is described in `docs/design/restated-passages-user-notes.md`.

## The idea, in Michael's words

**A translation memory for the run itself.** Batch Translate now has a short-term memory: a kind of temporary
translation memory that the AI uses while it works through a document.

Lead the Help page with that image. Then:

Batch Translate sends your document to the AI a few segments at a time, and the AI does not remember what it wrote
a moment ago. A translation memory would normally fill that gap, but the segments of the run you are doing right
now are not in any memory yet. So Supervertaler keeps them in mind for the length of the run. When the AI reaches a
segment that repeats something it has already translated in this document, it is shown that earlier segment and its
translation, and asked to keep the wording.

Like a translation memory, it gives two kinds of help:

- **A near-identical segment** (a high fuzzy match). The same sentence but for a few words or numbers: a clause
  with another amount, a step with another part number, a long list of values for another component. The AI is shown
  the earlier sentence and its translation, with the differences listed, and told to keep the wording and change
  only what the source changes.
- **A repeated passage** (a fragment match). The same run of words inside two otherwise different sentences: a
  contract clause that restates a definition, a warning that a manual repeats in several chapters, a patent
  description that repeats a claim.

It works on any kind of document. Nothing is stored: when the run ends, the memory is gone. What remains is your
document.

## What the translator sees

Nothing to set up: there is no setting, it is part of Batch Translate. At the end of a run the log says how often
it was used, and what still differs:

```
Repeated passages: 15 segments were shown an earlier translation of a passage they repeat.
Repeated passages: 3 translated differently (listed).
Repeated passages: Translated differently: segment 71 and segment 96 – source “…” (22 words the same)
```

"Preview prompt" shows the block when a segment in the first request repeats something that is already translated
in the document. It cannot show translations the run has yet to make, and says so.

## Numbers

A near-identical segment is exactly where an AI is tempted to copy. If the earlier sentence says 500 and yours says
250, a careless copy says 500. So the AI is told, in so many words, never to take a number, unit, reference numeral
or name from the earlier translation, and it is given a list of the places where the two sources differ. That
lowers the risk; it does not remove it. **Check the numbers in near-identical segments**, as you would with a
fuzzy match from a translation memory.

## Which translations are remembered

Only translations that are really in the document: ones that were there before the run and that the run leaves
alone, and ones the run itself has written. A segment the AI skipped, or one Trados refused to change, is never
shown. With "All segments", the old translations that are about to be replaced are not shown either.

Nothing is saved anywhere else: not in a translation memory, not in a memory bank, not in a file. If you stop a run
and start again on "Empty segments only", the second run reads the document and carries on as if it had never
stopped.

## The risk, and why it is worth it

The earlier translation is the AI's own. Nobody has checked it. If it is wrong, the mistake can be repeated wherever
the passage comes back. The AI is told the earlier translations are unreviewed and to ignore one that is wrong for
the segment, but it will often follow it.

That is still better than the alternative. One mistake made the same way in five places is one find-and-replace.
The same passage worded five different ways has to be found by reading the whole document.

## Translate the important part first

You can put the short-term memory to work on purpose:

1. Filter on the part that everything else must follow (the definitions of a contract, the safety warnings of a
   manual, the claims of a patent) and run Batch Translate on "Filtered segments".
2. Review those translations.
3. Remove the filter and run Batch Translate again on "Empty segments only".

In the second run, every segment that repeats something from the part you reviewed is shown your reviewed
translation of it. No extra step is needed in between.

## Notes for the translator

If the AI left a note on a segment (`[[TC: ...]]`) and a later segment is almost the same sentence, the AI is shown
the first one with its note, so it can flag the same problem again. It does not go back: if it only notices the
problem in the second segment, the first is not sent again, and nothing at the end of the run reports it.

## What it does not do

- It needs eight or more identical source words in a row. A passage the source words differently is not
  recognised.
- It does not make single terms consistent: if the AI called something a "tab" in one place and a "lip" in eight
  others, it will not go back and change the first. That is what the termbase and the prompt's glossary are for.
- A repeated passage inside a very long segment (over about 200 words) is not shown as an example. A
  near-identical segment is shown whatever its length.
- At most 20 earlier translations, and about 6,000 tokens of them, go with one request. One that does not fit is
  left out whole.
- Not yet for translations into Chinese, Japanese or Thai.
- Alt+T (translate the active segment) and SuperBench are unchanged.
- Not in Supervertaler for memoQ.

## Cost

A little more is sent. On a 105-segment test document about 4,600 extra input tokens; on a 460-segment patent an
estimated 21,500, some 7 US cents at 3 USD per million tokens. Long near-identical segments add about 1,000 tokens
each. The large fixed part of each request is unchanged, so the saving from prompt caching is kept.

## Measured

- On an invented 105-segment test document with Claude Opus 5.5: 10 of 22 planted pairs differed without the
  feature, 3 with it (before near-identical segments were added to it); nothing that agreed got worse.
- On a real 485-segment patent with a cheaper model: 13 of 22 differences gone.
- The near-identical part (long segments, numbers) has been tested offline only, not yet with an AI.

## Changelog lines for the release this goes into

Under `### Added`, starred:

> ★ **Batch Translate has a short-term memory: a translation memory for the run itself.** When a segment repeats
> something already translated in the same document, the AI is shown that earlier segment and its translation and
> asked to keep the wording. A near-identical segment – the same sentence but for a few words or numbers – is shown
> like a fuzzy match, with the differences listed; a passage repeated inside another sentence is shown too. Translate
> and review the part that matters first, then run the rest on "Empty segments only", and the rest follows your
> reviewed wording. Nothing is stored outside the document, and the saving from prompt caching is kept.

Under `### Changed`:

> AutoPrompt no longer tells the AI that it sees nothing of earlier requests, which would contradict the above.
> Prompts you have already saved keep the old sentence; regenerate a prompt to get the new one.

## Scale statement

Tested at 10,000 segments, designed for 10,000. The document's repeated passages are found once per run, in the
background (about 0.6 s at that size); looking them up for each request takes no measurable time. Listing the
differences of a near-identical segment takes about 5 ms for two 220-word segments. Segments over 1,000 words are
left out.
