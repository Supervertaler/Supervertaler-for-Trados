# In-run references: showing the AI its own earlier translation of a repeated passage

**Status:** built, step 2 of the cross-batch consistency design (14 Nov 2026), on Michael's "Yes, build step 2".
Offline tests pass. No real AI request has been made: whether a model follows the block is not yet measured. Not in a
release. Stacked on step 1 (`docs/design/restated-passages.md`), which can ship without it.
**Code:** `core/src/EarlierTranslations.cs` (which earlier segments to show), `TranslationPrompt.BuildReferenceBlock`
in `core/src/TranslationPrompt.cs` (the block), one sentence in `core/src/PromptGenerator.cs`;
`src/Supervertaler.Trados/Core/RestatedCheck.cs` (what the run knows), `BatchTranslator.cs` (`InRunReferences`),
`AiAssistantViewPart.cs` (switched on for Batch Translate; Preview prompt).
**Tests:** `core/tests/EarlierTranslationsTests.cs` (21), `tools/in_run_references_test.ps1` (52 checks); the
test document and its counter in `tools/fixtures/consistency-test/` and `tools/consistency_test_count.ps1`.
**User-facing notes:** `docs/design/in-run-references-user-notes.md`.

## What it does

Batch Translate sends a document a few segments at a time and the AI does not see what it wrote in earlier
requests. Step 1 reports, after the run, where a repeated passage came out worded two ways. Step 2 tries to prevent
it: when a request contains a segment that repeats a passage of another segment that already has a translation, the
request shows the AI that other segment and its translation, and asks it to keep the wording.

"Repeats a passage" means exactly what it means in step 1: the pairs `RestatedPassages.Find` returns (8 identical
source words in a row, with its rules for boilerplate, numbers and lists). One definition for both, so what is shown
during the run and what is reported after it agree. Like step 1 it knows nothing about the kind of document: a
contract clause restating a definition, a manual repeating a warning, a message that differs from another in one
word, a patent's description repeating a claim.

## Where it goes: the user prompt

A request has two parts. The system prompt (instructions, glossary, the document) is built once and is byte for
byte the same in every request of a run; that is what prompt caching pays for. The user prompt holds the segments,
and already carries the translation-memory matches for them. The new block goes there, after the memory matches and
before the segments.

- The system prompt is unchanged. The test compares it across all requests of a run and against a run with the
  feature off.
- A request with nothing to show is byte for byte what it was. The test compares those too, and a core test pins
  the full text of a request without references.

## What counts as known: the document is the memory

An earlier translation is shown only if it is one of:

1. a target that was in the document before the run and that the run will not overwrite (a locked segment, or one
   outside what the scope overwrites);
2. a target this run wrote, recorded only once Studio has taken the write (the rule `RepetitionRun.Commit` follows).

Not known: a target the run is about to overwrite ("All segments"); a segment the AI left out; one in a request that
failed; one Studio declined to write; a segment later in the run; a segment in the same request (the AI sees that
one anyway).

Nothing is written anywhere: no file, cache, memory-bank entry or TM. The knowledge is on the run's
`RestatedCheck`, which lives and dies with the run's translator.

| Situation | What happens | Tested |
|---|---|---|
| Earlier segment left out of the AI's reply | nothing shown later | yes |
| Earlier request failed (HTTP error) | nothing shown later; later requests still go out | yes |
| Studio declined the earlier write | nothing shown later | yes |
| Run stopped | nothing left behind | yes |
| Stopped, then run again on "Empty segments only" | the second run reads the document, and shows the same earlier segment and translation an uninterrupted run would | yes |
| "All segments" over old translations | the old ones are in no request; the new one is shown once written | yes |
| The lookup itself throws | the segment goes out without references; logged | **no** (nothing can make it fail from outside) |

## Short-term memory: a translation memory for the run itself

Michael's framing, 14 Nov 2026, and the one the Help page leads with: the feature is "a translation memory for the
run itself", "a kind of temporary translation memory used by the AI during a batch translation", "short-term
memory". Like a translation memory it gives two kinds of match.

| | A near-twin | A passage |
|---|---|---|
| What | the same sentence but for a few words or numbers | a run of words inside two otherwise different sentences |
| In TM terms | a high fuzzy match | a fragment match |
| Decided by | the finder: the shared words are at least 80% of the longer segment | the finder: 8 identical words in a row |
| Shown | whole, whatever its length, with the differences named | only if the earlier source and translation are each under 400 tokens |

**Near-twins were added after the first measurements** (Michael: "Yes, add the TM-style match for near-identical
segments"). The passage rule's 400-token limit (about 200 words) kept every long near-identical pair from being
shown: the long cascades of values on the real job, and pair 80+90 on the test document. A passage inside 400 words
of something else is no example, so that limit stays for passages. A twin is the whole sentence, so it does not
apply. `VaultDiff` is not changed and its alignment is not used: only its tokeniser, to count.

**No new comparison of every segment with every other.** Whether two segments are twins is already known from the
finder's pass (`RestatedPair.NearTwin`), which only looks at pairs that share an indexed run of 8 words. Naming a
twin's differences aligns the two sources word by word, for a twin that is about to be shown and no other: words x
words, at most 1,000 x 1,000 (the finder leaves longer segments out), about 5 ms for two 220-word segments.

## Which earlier segments, and how many

`EarlierTranslations.For`: of the segments that share something with this one and have a known translation, a twin
first, then the one sharing the most words; at most **2** per segment; the same translation is not shown twice; each
other segment appears once, as a twin if it is one. A translation in a language written without spaces (Chinese,
Japanese, Thai, ...) is not shown, as step 1 does not judge it.

Per request: at most **20** earlier translations and **24,000 characters** of them (about 6,000 tokens), taken in
the order of the segments. One that does not fit is left out whole; none is ever cut short; a shorter one after it
that still fits is shown. A long twin with its translation is about 3,000 characters, so eight fit. Two long twins
in one request are simply both shown.

The other segment may come later in the document. If part of a document was translated first (run 1 on a filter,
run 2 on "Empty segments only"), run 2 is shown run 1's translations wherever they stand.

## Numbers are the risk

A twin's numbers differ from the segment's own (500 euro against 250; one cascade of values against the same
cascade with three other values). A model that copies the earlier translation copies its numbers. A wrong number is
worse than an inconsistent word. Three things are done about it:

1. The block says, only when a twin is present: keep the wording and the sentence structure, change only what your
   source changes, and "NEVER copy a number, unit, reference numeral or name from the earlier translation: take
   every one of them from your own segment's source, in its own place."
2. Each twin entry has a line `differs in:` that names the places where the two sources differ, as "yours X /
   earlier Y" (at most 12, then "and N more"), so the numbers that must change are in front of the model.
3. It is testable: the counter for the test document checks, for every planted twin, that each translation has the
   numbers of its own source and none that only the other source has. The test document has a long cascade whose
   twin differs in three numbers for exactly this.

Whether a given model heeds it is not yet measured. The user notes tell the translator to check numbers in
near-identical segments, as with any fuzzy match.

## The block

```
**EARLIER TRANSLATIONS FROM THIS DOCUMENT – KEEP THE SAME WORDING**

Some of the segments below repeat a passage that stands elsewhere in this document and has already been
translated. That other segment and its translation are shown here. Where your segment has the same source wording,
translate that passage with the same wording as the earlier translation, so that the document reads consistently -
unless that wording is wrong for this segment, in which case translate from the source. The earlier translations
have not been reviewed and can contain mistakes. Translate the rest of each segment as usual, and translate only the
segments in the list below: the earlier segments are shown for reference and must not be returned. Inline tags are
left out of the earlier texts; take the tags from your own segment.

Segment 5 – repeats “any information disclosed by one party to the …” (15 words)
  earlier source:      ...
  earlier translation: ...

Segment 31 – the same sentence as an earlier segment but for a few words or numbers
  differs in:          yours “250” / earlier “500”
  earlier source:      ...
  earlier translation: ...
```

(One paragraph in the prompt; wrapped here.) Tags are left out because the earlier segment numbers its tags
differently from this one. When an earlier translation ends in a `[[TC: ...]]` note, one more sentence says what
the note is and that the AI should flag its own segment the same way if it has the same problem.

## Notes travel forward, not back

The earlier translation is shown with its note, whether the run keeps notes in the target or writes them as Trados
comments (the run records the translation before the note is taken off). So a near-identical segment later in the
run sees that the AI found a problem in the first. An earlier twin that got no note is not sent again when a later
one gets one, and nothing reports it: step 1's line for a note on one of two near-twins was dropped on 15 Nov 2026
(see `docs/design/restated-passages.md`). A segment translated in an earlier run whose note is a Trados comment is
shown without it, because comments are not read.

## The risk

The earlier translation is the AI's own and unreviewed, and it becomes a model. A mistake in it can be repeated in
every segment that restates the passage. Two answers, neither complete:

- the block says the earlier translations are unreviewed, and to translate from the source where the wording is
  wrong for the segment;
- a mistake made the same way everywhere is one find-and-replace, while the same passage worded five ways needs a
  full read to find. Consistent and wrong is cheaper to fix than inconsistent.

Translating the part that matters first and checking it before the rest (see the user notes) turns the risk
around: the model for the rest is then a reviewed one.

## AutoPrompt

Prompts written by AutoPrompt told the translating AI "There is no memory between requests, so terminology must
come from the glossary and reference translations below - never from choices made in an earlier batch." That would
contradict the block. The meta-prompt (`PromptGenerator.BuildMetaPrompt`, input handling) now says each request
stands alone, that of earlier requests the AI sees only what a request itself shows (memory matches, and earlier
translations of a repeated passage), and that terminology must never come from an assumption about an earlier
batch. Prompts already saved keep the old sentence. It does little harm: it allows "reference translations", and
the block is explicit. A prompt that forbids using anything but the glossary would work against the block.

## Preview prompt

Preview shows the first request. It now includes the block for segments that repeat a passage translated in the
document at that moment, and always ends with a line saying that during a run, translations from the run's own
earlier requests are added, which a preview cannot show. The preview's lookup runs on the UI thread, with the rest
of the preview (about half a second at 10,000 segments); the run's never does.

## Switched on per run, no setting

`BatchTranslator.InRunReferences`, set by Batch Translate only, like `Repetitions`. Alt+T and SuperBench use their
own translator and send what they sent before. **There is no setting**: it is always on for Batch Translate. Whether
one is wanted is Michael's to decide when he has tried it.

## The end-of-run report

Step 1's lines stay and now show what is left. One line is added when anything was shown:

```
Repeated passages: 65 segments were shown an earlier translation of a passage they repeat.
Repeated passages: 3 translated differently (listed).
Repeated passages: Translated differently: segment 12 and segment 340 – source “…” (15 words the same)
```

## Cost

From the stand-in tests: the instruction is 821 characters, about 205 tokens, once per request that has anything to
show. Each earlier translation adds its source, its translation and about 110 characters of labels.

Estimated on the saved real run of step 1's measurement (460 segments, batch size 10, counts only, no request made):
65 segments would have been shown 87 earlier translations in 27 of 46 requests, adding about 86,000 characters, or
about 21,500 input tokens. At 3 USD per million input tokens that is about 0.07 USD on a run the design puts at 1 to
5 USD. These tokens are in the user prompt, so they are never cached, and they do not disturb the caching of the
system prompt.

## Scale

Tested at 10,000 segments, designed for 10,000. The pairs are found once per run, at the first lookup (about 0.6 s
at 10,000 segments, on the run's thread). After that 10,000 lookups and 10,000 recorded writes took 0.18 s together.
Memory is step 1's: two strings per segment for the run.

## Known limits

- Whether a given model follows the block is unmeasured. The stand-in server proves the plumbing, not the effect.
- Only passages step 1 pairs: 8 identical source words in a row. A passage the source rewords gets no reference.
- Segments in the same request get none from each other.
- A passage (not a twin) inside a segment over 400 tokens is not shown. A twin is shown at any length up to the
  finder's 1,000 words.
- Whether a model keeps its own numbers when shown a twin is unmeasured.
- A resumed run does not see notes that an earlier run wrote as Trados comments.
- memoQ is not covered: memoQ calls the plugin in parallel and the plugin never sees existing targets there.

## Not tested

- The catch around the lookup in `BatchTranslator.WithReferences`.
- The preview's wording inside Studio (the lookup it uses is tested; the dialog is not).
- 32-bit builds (Studio 2024 / 2022); `tools/structure_context_test.ps1`, which needs a Release build.
- Anything inside Studio, and any real AI request.
