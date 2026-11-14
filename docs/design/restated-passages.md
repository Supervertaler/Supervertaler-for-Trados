# Restated passages: finding them, and saying where their translations differ

**Status:** built, step 1 of the cross-batch consistency design (14 Nov 2026). Offline tests pass. Not yet measured
on a real job (see "Measured" at the end), not yet in a release.
**Code:** `core/src/RestatedPassages.cs` (the finder and the comparison, shared with memoQ),
`src/Supervertaler.Trados/Core/RestatedCheck.cs` (what a Batch Translate run collects), and the hooks in
`src/Supervertaler.Trados/Core/BatchTranslator.cs` and `AiAssistantViewPart.cs`.
**Tests:** `core/tests/RestatedPassagesTests.cs` (24), `tools/restated_passages_test.ps1` (32 checks).
**User-facing notes:** `docs/design/restated-passages-user-notes.md`.

## The problem

Batch Translate sends a document ten or twenty segments at a time, and each request starts from scratch. On a
485-segment patent (Acme / PROJ-001) that gave:

- six places where a description sentence repeats a claim word for word, inside a longer sentence, 200-400 segments
  later, and the English came out differently ("in a perpendicular direction to" / "in a direction perpendicular to");
- one source defect the AI flagged with a `[[TC: ...]]` note in one segment and missed in an almost identical
  segment 118 segments on.

Consistent repetitions (`docs/design/consistent-repetitions.md`) only sees whole segments that are identical.
`find_inconsistencies` likewise. Neither sees a passage inside a segment.

## What this step does, and does not

It finds the pairs and reports them at the end of a Batch Translate run. No AI is involved, nothing that is sent
changes (the test compares the requests with the check on and off), nothing is written to the document, no setting is
added, and nothing is kept after the run.

It does not prevent anything. Showing the AI its own earlier translation of a restated passage is step 2 and a
separate decision. The Reports tab, the MCP tool `find_inconsistencies`, memoQ and the Help page are not in this step.

## The finder (`RestatedPassages.Find`)

Input: the source of every segment as plain text, in document order. Output: the pairs of segments that share a run
of at least **8** identical words, with where each shared run sits in both (word positions).

- **Same text** means the same words in the same order. Case, punctuation, spacing and tags decide nothing. A tag
  never splits a word: the text on either side is joined, the rule `RepetitionKey` uses for adjacent text. Words come
  from `VaultDiff.Tokenize`, the tokeniser EverTM already uses ("e-mail" and "don't" are one word; a number is a word).
- **Index.** Every run of 8 consecutive words gets an entry pointing to the segments that contain it. Two segments
  are compared word by word only if they share such an entry.
- **Boilerplate cap.** A phrase that occurs in more than **6** segments pairs nothing. Otherwise "according to any
  one of the preceding claims, characterised in that" would pair every dependent claim with every other. A pair must
  also have at least **4** words of its own in its long run, outside such phrases: without that, two claims that
  open with the formula and happen to continue with the same word are a pair. (Found by the 10,000-segment test, which
  returned 554 pairs where 300 were planted.)
- **One passage with a word changed** is still one passage. After the long run, shorter shared runs (2 words or more
  in the source) within 6 words of it are joined to it. So "... op de lengteas van het huis ..." against "... op de
  dwarsas van het huis ..." is one passage with one break, as long as one side of the break is 8 words.
- **Near-twins**: the shared words are at least 80% of the longer segment.

The constants are in the class, with the reason for each. They are not settings.

## The comparison (`RestatedPassages.Compare`)

There is no word alignment between source and target, so the passage cannot be located in each translation. The
comparison looks at the two translations the same way the finder looks at the two sources, and asks whether the
translations differ in more places than the sources do.

1. If the two sources are word for word the same, the two translations must be too.
2. Otherwise the translations' own shared runs (3 words or more) are grouped into passages. If together they are
   less than half the length the source passage leads one to expect (its word count × the lower of the two segments'
   target-to-source length ratios), the passage was translated differently.
3. Otherwise count the breaks: places where the translations share wording, differ for a few words, and share
   wording again. A break the sources do not have is a wording difference, and the pair is reported. Breaks in the
   middle of a passage and breaks within 5 shared words of its start or end are counted apart, so a difference the
   sources have at the edge cannot excuse one the translations have in the middle.

A pair is skipped, and never reported, when either translation is empty: a segment the AI left out, one Studio
declined to write, one the run did not reach.

### Where it is wrong

False alarms (reported, but fine):

- **Grammar that rightly differs.** A claim says "characterised in that the widget is mounted" and the description
  "the widget, which is mounted": the same source words can need different English around them. The rule cannot tell
  a justified difference from an unjustified one. It reports every difference.
- **A chance match just outside the passage.** The translations agree over the whole passage, the sentences then go
  their own ways, and three words later happen to share three words ("of the invention"). That reads as a break.
- **A short shared source piece that becomes a longer one in English.** The source side counts pieces from 2 words,
  the target side from 3, because Dutch "de borgpen" is English "the locking pin". A one-word source piece that
  becomes three English words ("klemarmhouder" → "clamping arm holder") still reads as a break the sources lack.
- **One source difference, two target differences.** A changed noun that also changes an article or a verb form
  further on gives two breaks where the sources have one.

Misses (different, not reported):

- **A difference in the last or first two words of the passage.** There is no shared wording beyond it to show that
  the passage went on.
- **Punctuation and capitals.** Never compared.
- **A long rewording at one end.** If more than 6 words differ and less than half the passage is left, rule 2
  reports it; between those, a reworded tail can pass.
- **A difference the sources seem to explain.** If the sources differ in one place and the translations differ in
  one place, they are taken to be the same place.
- **Passages under 8 words**, and phrases in more than 6 segments. A term translated two ways is not this check's
  business; that is case (a) of the design, step 3.
- **Segments over 1,000 words** are left out (comparing a pair costs words × words).

The false-alarm rate can only be measured on real text. That is the open item below.

## Notes on one of two near-twins

For a near-twin pair with both translations present: if exactly one has a note, it is reported. The run knows its own
notes: the `[[TC: ...]]` marker is still on the translation when the run records it, whether it later goes into the
target or into a Trados comment. For a segment the run did not write, the comments are not read, so its notes are
unknown and the pair is not reported (unless the marker is in its target text).

## In Trados

- `CollectSegments` already walks the document on the UI thread before a run. It now also hands each segment's source
  to the run's `RestatedCheck`, and the existing target of each segment the run will not write (locked, or outside
  what the scope overwrites). Two strings per segment.
- `BatchTranslator.Deliver` records a translation once Studio has taken the write, the rule consistent repetitions
  follows. A declined write records nothing.
- Before `Completed` is raised, the finder and the comparison run once, on a pool thread (`Task.Run`), whatever
  thread started the run. If the check throws, it is logged and the run completes without lines.
- `OnBatchCompleted` writes the lines after "Complete: N translated" and the repetitions line, each prefixed
  "Restated passages: ". Nothing is written when there is nothing to report.
- The diagnostic log gets one line per run with counts and the time taken, never text.
- Alt+T and SuperBench use their own translator with no check, as with repetitions.

**State and failure paths.** The check lives on the run's translator. A stopped run reports on what it wrote. A run
that throws releases its translator and the check with it. Nothing is written to disk.

**Scope limits.** With the Filtered scopes only the filtered segments are seen. "Claims first, by hand" works: run
the claims, then the rest with "Empty segments only", and the second run compares its translations with the claims
already in the document.

## Scale

Tested at 10,000 segments, designed for 10,000.

| | Measured (this PC, Debug build) |
|---|---|
| Finder, 10,000 segments of 30 words, 300 planted passages | about 0.4 s |
| A 5,000-segment document followed by a copy of itself (every segment a twin): find and compare | about 1 s |
| Through the Trados collector, 10,000 segments, 200 passages | about 0.5 s |

Memory at 10,000 segments: the word numbers (about 1.2 MB), one index entry per run of 8 words (about 300,000), freed
when the finder returns. Limits that keep the worst case bounded, each tested: a segment over 1,000 words is left out
and counted; at most 50,000 candidate pairs are compared, and the summary says so if that was reached; the log lists
at most 20 cases of each kind and says how many more there are.

## Measured on a real job

Not yet. To be filled in with counts and segment numbers only: how many of the six known restated-claim cases and
the one known twin-defect case are found, and how many other lines are reported. The values 8 (run), 6 (boilerplate)
and 4 (own words) are first choices from the invented document and are to be confirmed or changed by that
measurement.

## Not tested

- The check throwing inside a run (the catch in `RestatedSummaryAsync`): nothing in the collector can make the core
  call fail, so there is no honest way to provoke it from outside.
- A 32-bit run of `tools/restated_passages_test.ps1` (Studio 2024 / 2022 builds); it was run against the Studio 2026
  Debug build.
- Inside Studio. The orchestrator installs test builds.
