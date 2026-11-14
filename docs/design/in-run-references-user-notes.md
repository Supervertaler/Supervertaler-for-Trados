# Keeping repeated passages consistent – notes for the Help page and the changelog

Running notes for the user documentation, written while building (14 Nov 2026). Design:
`docs/design/in-run-references.md`. This is the second half of "repeated passages"; the first is the report at the
end of a run (`docs/design/restated-passages-user-notes.md`).

## What it does, in plain English

Batch Translate sends your document to the AI a few segments at a time. The AI does not remember what it wrote a
moment ago, so when the document says the same thing twice, it may word it differently the second time.

Now, when a segment repeats a passage that is already translated somewhere in the same document, Supervertaler
shows the AI that earlier segment and its translation, and asks it to keep the same wording. It works on any kind of
document:

- a contract clause that restates a definition;
- a warning or a step that a manual repeats in several chapters;
- two messages in software that are the same sentence but for one word;
- a patent description that repeats a claim.

## What the translator sees

Nothing new to set up: there is no setting, it is part of Batch Translate. At the end of a run the log says how
often it happened, and what still differs:

```
Repeated passages: 65 segments were shown an earlier translation of a passage they repeat.
Repeated passages: 3 translated differently (listed).
Repeated passages: Translated differently: segment 12 and segment 340 – source “…” (15 words the same)
```

"Preview prompt" shows the block when a segment in the first request repeats a passage that is already translated
in the document. It cannot show translations the run has yet to make, and says so.

## Which translations are shown

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

You can put this to work on purpose:

1. Filter on the part that everything else must follow (the definitions of a contract, the safety warnings of a
   manual, the claims of a patent) and run Batch Translate on "Filtered segments".
2. Review those translations.
3. Remove the filter and run Batch Translate again on "Empty segments only".

In the second run, every segment that repeats a passage from the part you reviewed is shown your reviewed
translation of it. No extra step is needed in between.

## Notes for the translator

If the AI left a note on a segment (`[[TC: ...]]`) and a later segment is almost the same sentence, the AI is shown
the first one with its note, so it can flag the same problem again. It does not go back: if it only notices the
problem in the second segment, the first is not sent again. The end-of-run line "Note on one only" tells you when
that happened.

## What it does not do

- It only helps where eight or more source words in a row are the same. A passage the source words differently is
  not recognised.
- It does not make single terms consistent; that is what the termbase and the prompt's glossary are for.
- Very long segments (over about 200 words) are not shown as an example.
- Not yet for translations into Chinese, Japanese or Thai.
- Alt+T (translate the active segment) and SuperBench are unchanged.
- Not in Supervertaler for memoQ.

## Cost

A little more is sent: on a 460-segment patent about 21,500 extra input tokens, some 7 US cents at 3 USD per million
tokens, on a run of 1 to 5 USD. The large fixed part of each request is unchanged, so the saving from prompt
caching is kept.

## Not yet known

Whether a given AI model actually follows the earlier wording has not been measured on a real job. The tests use a
stand-in for the AI.

## Changelog line for the release this goes into

Under `### Added`, starred:

> ★ **Batch Translate keeps repeated passages consistent.** When a segment repeats a passage that is already
> translated elsewhere in the same document – a contract clause restating a definition, a warning repeated across a
> manual, a patent description repeating a claim – the AI is now shown that earlier segment and its translation and
> asked to keep the wording. Translate and review the part that matters first, then run the rest on "Empty segments
> only", and the rest follows your reviewed wording. The log says how many segments were shown one and lists what
> still differs. Nothing is stored outside the document, and the saving from prompt caching is kept.

Under `### Changed`:

> AutoPrompt no longer tells the AI that it sees nothing of earlier requests, which would contradict the above.
> Prompts you have already saved keep the old sentence; regenerate a prompt to get the new one.

## Scale statement

Tested at 10,000 segments, designed for 10,000. The document's repeated passages are found once per run, in the
background (about 0.6 s at that size); looking them up for each request takes no measurable time.
