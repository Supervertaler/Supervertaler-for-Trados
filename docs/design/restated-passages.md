# Restated passages: finding them, and saying where their translations differ

**Status:** built, step 1 of the cross-batch consistency design (14 Nov 2026). Offline tests pass. Measured on one
real job, a patent (see "Measured on a real job" at the end); no other kind of document yet. Not yet in a release.
**Code:** `core/src/RestatedPassages.cs` (the finder and the comparison, shared with memoQ),
`src/Supervertaler.Trados/Core/RestatedCheck.cs` (what a Batch Translate run collects), and the hooks in
`src/Supervertaler.Trados/Core/BatchTranslator.cs` and `AiAssistantViewPart.cs`.
**Tests:** `core/tests/RestatedPassagesTests.cs` (24), `core/tests/RestatedDocumentTypesTests.cs` (19),
`tools/restated_passages_test.ps1` (32 checks).
**User-facing notes:** `docs/design/restated-passages-user-notes.md`.

## The problem

Batch Translate sends a document ten or twenty segments at a time, and each request starts from scratch. When a
document says the same thing twice, in the same words, inside two different segments, the two translations can come
out worded differently. Examples:

- a contract defines a term and a later clause restates the definition;
- a manual repeats a warning or a step in several chapters, sometimes inside a longer sentence;
- two error messages are the same sentence but for one word;
- a patent's description repeats a claim inside a longer sentence.

It was first measured on the last kind: a 485-segment patent (Acme / PROJ-001) had six such places, 200-400 segments
apart, and one source defect the AI flagged with a `[[TC: ...]]` note in one segment and missed in an almost
identical segment 118 segments on.

Consistent repetitions (`docs/design/consistent-repetitions.md`) only sees whole segments that are identical.
`find_inconsistencies` likewise. Neither sees a passage inside a segment, or two segments that differ in one word.

## Neutral about the document

Michael, 14 Nov 2026: the claims-and-description case matters to him and one or two users; the system must
accommodate a wide range of document types.

The code has no idea what kind of document it is reading. It does not look for claims, headings, numbering or any
structure; any two segments that share a run of words are a pair. Nothing in a class name, message or log line names
a document type. The tests cover a contract (English to German), a manual (English to French), software strings
(English to Spanish), a patent (Dutch to English), and Russian, Greek, Turkish, Chinese and Japanese text.

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
  from `VaultDiff.Tokenize`, the tokeniser EverTM already uses ("e-mail", "don't" and "l'appareil" are one word; a
  number is a word).
- **Index.** Every run of 8 consecutive words gets an entry pointing to the segments that contain it. Two segments
  are compared word by word only if they share such an entry.
- **Boilerplate cap.** A phrase that occurs in more than **6** segments pairs nothing. Otherwise a formula that
  opens every clause would pair each with every other. A pair must also have at least **4** words of its own in its
  long run, outside such phrases: without that, two segments that open with the formula and happen to continue with
  the same word are a pair. (Found by the 10,000-segment test, which returned 554 pairs where 300 were planted.)
- **Numbers are not words of their own.** The long run must have at least **6** different words, numbers not
  counted. "10 mm 12 mm 14 mm 16 mm" is eight words in a row and one word.
- **A segment that mostly repeats itself is compared with its near-twins only.** A list of values ("at least
  0.7 mm, preferably at least 0.8 mm, preferably at least 0.9 mm, ...") shares long runs with every other such list by
  the accident of its formula, and the two translations cannot be lined up. A segment counts as repeating itself when
  fewer than 70% of its three-word sequences are different from one another, every number counting as the same word.
  Both rules came from the real measurement below, where half the first lines were of this kind.
- **One passage with a word changed** is still one passage. After the long run, shorter shared runs (2 words or more
  in the source) within 6 words of it are joined to it.
- **Near-twins**: the shared words are at least 80% of the longer segment.

The constants are in the class, with the reason for each. They are not settings.

## The comparison (`RestatedPassages.Compare`)

There is no word alignment between source and target, so the passage cannot be located in each translation. The
comparison looks at the two translations the same way the finder looks at the two sources, and asks whether the
translations differ in more places than the sources do.

1. If the two sources are word for word the same, the two translations must be too.
2. **Near-twins are compared whole.** The places where the two translations differ are counted (the stretches left
   over when the longest sequence of words they have in common, in order, is taken out), and so are the places where
   the two sources differ. More in the translations than in the sources is reported. Position does not matter, so a
   changed word that English has second and Spanish has fifth is still one place. (Added after the software-strings
   test: "The file / The project could not be saved ..." was a false alarm under rule 4.)
3. Otherwise the translations' own shared runs (3 words or more) are grouped into passages. If together they are
   less than half the length the source passage leads one to expect (its word count × the lower of the two segments'
   target-to-source length ratios), the passage was translated differently.
4. Otherwise count the breaks: places where the translations share wording, differ for a few words, and share
   wording again. A break the sources do not have is a wording difference, and the pair is reported. Breaks in the
   middle of a passage and breaks within 5 shared words of its start or end are counted apart, so a difference the
   sources have at the edge cannot excuse one the translations have in the middle.

5. Last, the passage's first and last words. If the four words just outside the shared wording are, in both
   translations, the same words but for one ("in a perpendicular direction [to the base ...]" against "in a
   direction perpendicular [to the base ...]"), and the sources have no such thing there, the passage starts or ends
   differently and the pair is reported. Added after the real measurement: a known case was missed without it.

A pair is skipped, and never reported, when either translation is empty: a segment the AI left out, one Studio
declined to write, one the run did not reach.

### Where it is wrong

False alarms (reported, but fine):

- **Grammar that rightly differs.** The same source words can need different wording around them: a clause that
  stands alone in one place and follows "provided that" in another. The rule cannot tell a justified difference from
  an unjustified one. It reports every difference.
- **A chance match just outside the passage.** The translations agree over the whole passage, the sentences then go
  their own ways, and three words later happen to share three words. That reads as a break.
- **One source difference, two target differences.** A changed noun that also changes an article, an adjective
  ending or a verb form further on gives two places where the sources have one. More likely the more the target
  language inflects.
- **A short shared source piece that is longer in the translation.** Outside near-twins the source side counts pieces
  from 2 words and the target side from 3. A one-word source piece that becomes three target words still reads as a
  break the sources lack.

Misses (different, not reported):

- **A passage in more than 6 segments.** A warning that stands inside ten different sentences of a manual counts as
  boilerplate and is not compared at all (pinned by a test). Ten *identical* segments are consistent repetitions'
  business and get one translation there. Whether to compare each occurrence with the first instead of dropping the
  phrase is an open question for after the first real measurement.
- **Short segments.** Under 8 words in a row there is no pair. 400 software strings of two to six words that share
  most of their words give no pairs and no lines (tested): no noise, and no help either. Identical short strings
  are, again, consistent repetitions' business.
- **A different word in the first or last two words of a passage** (outside near-twins). The same words in another
  order are caught by rule 5; another word altogether ("at right angles" / "perpendicular") is not, because nothing
  shows that the passage went on.
- **A list of values against a different list of values** is not compared at all (see the finder), so "range" in
  one and "interval" in the other for the same source word goes unreported there. One such true difference was lost
  on the real document when the rule went in, against 14 lines of noise.
- **Punctuation and capitals.** Never compared.
- **A difference the sources seem to explain.** If the sources differ in one place and the translations differ in
  one place, they are taken to be the same place.
- **A single term translated two ways.** Not this check's business; that is case (a) of the design, step 3.
- **Segments over 1,000 words** are left out (comparing a pair costs words × words).

The false-alarm rate can only be measured on real text. That is the open item below.

## Languages

Nothing assumes Dutch or English. Words are runs of letters, digits and combining marks in any script, compared
without case (`ToLowerInvariant`). Tested: Dutch, English, German, French (an apostrophe stays inside its word),
Spanish, Russian, Greek, Turkish.

**Languages written without spaces between words are not supported yet: Chinese, Japanese, Thai, Lao, Khmer,
Burmese.** The tokeniser makes one "word" of a whole clause there. What happens, by test:

- such a *source* never has 8 words in a row, so nothing pairs and no lines are written;
- for a *translation* into such a language (English to Chinese, say) the sources pair, but the pair is skipped
  rather than judged, so no lines are written either. Without this the rule above would have called every pair
  "translated differently". `RestatedPassages.WrittenWithoutSpaces` decides: more than a tenth of the letters are in
  one of those scripts, so a name quoted in kanji inside an English sentence does not switch it off.

Korean is written with spaces and is treated like any other language. Supporting the unspaced languages would mean
counting characters instead of words; not built.

## Notes on one of two near-twins

For a near-twin pair with both translations present: if exactly one has a note, it is reported. The run knows its own
notes: the `[[TC: ...]]` marker is still on the translation when the run records it, whether it later goes into the
target or into a Trados comment. For a segment the run did not write, the comments are not read, so its notes are
unknown and the pair is not reported (unless the marker is in its target text).

The marker is a convention of AutoPrompt-written prompts. A document translated with a prompt that does not use it
has no notes, and simply gets no such lines (tested).

## In Trados

- `CollectSegments` already walks the document on the UI thread before a run. It now also hands each segment's source
  to the run's `RestatedCheck`, and the existing target of each segment the run will not write (locked, or outside
  what the scope overwrites). Two strings per segment.
- `BatchTranslator.Deliver` records a translation once Studio has taken the write, the rule consistent repetitions
  follows. A declined write records nothing.
- Before `Completed` is raised, the finder and the comparison run once, on a pool thread (`Task.Run`), whatever
  thread started the run. If the check throws, it is logged and the run completes without lines.
- `OnBatchCompleted` writes the lines after "Complete: N translated" and the repetitions line, each prefixed
  "Repeated passages: " (the plainer word for a log; the code says "restated" to keep it apart from consistent
  repetitions). Nothing is written when there is nothing to report.
- The diagnostic log gets one line per run with counts and the time taken, never text.
- Alt+T and SuperBench use their own translator with no check, as with repetitions.

**State and failure paths.** The check lives on the run's translator. A stopped run reports on what it wrote. A run
that throws releases its translator and the check with it. Nothing is written to disk.

**Scope limits.** With the Filtered scopes only the filtered segments are seen. Translating a document in two runs
works: the second run ("Empty segments only") compares its translations with those already in the document.

## Scale

Tested at 10,000 segments, designed for 10,000.

| | Measured (this PC, Debug build) |
|---|---|
| The real job: 460 segments, up to 380 words each | about 0.4 s |
| Finder, 10,000 segments of 30 words, 300 planted passages | about 0.4 s |
| A 5,000-segment document followed by a copy of itself (every segment a twin): find and compare | about 1 s |
| Through the Trados collector, 10,000 segments, 200 passages | about 0.5 s |
| 400 short software strings | under 10 ms |

Memory at 10,000 segments: the word numbers (about 1.2 MB), one index entry per run of 8 words (about 300,000), freed
when the finder returns. Comparing a near-twin pair takes a table of words × words (4 MB at the 1,000-word limit),
one pair at a time. Limits that keep the worst case bounded, each tested: a segment over 1,000 words is left out
and counted; at most 50,000 candidate pairs are compared, and the summary says so if that was reached; the log lists
at most 20 cases of each kind and says how many more there are.

## Measured on a real job

14 Nov 2026, with Michael's permission, read-only: the saved output of the Batch Translate run on the Acme /
PROJ-001 patent (the run's backup TMX, as the AI wrote it, before any correction). 460 translated segments. Only
counts and positions are recorded here; positions are positions in the saved file (1-460), not Studio's numbers,
because the bilingual file was not read. Every line was read and judged by the session that built this.

| | Pairs found | Lines reported | Time |
|---|---|---|---|
| As first built (8 words, cap 6, 4 own words) | 245 | 42 | 0.44 s |
| + at least 6 different non-number words | 187 | 33 | 0.40 s |
| + self-repeating segments compared with near-twins only | 132 | 18 | 0.36 s |
| + same words in another order at a passage's edge | 132 | 22 | 0.39 s |

The 22 lines as it now stands:

| Judgement | Lines | Positions in the saved file |
|---|---|---|
| A known restated case, found | 5 (3 cases) | 56+411, 60+411; 146+411, 146+420; 259+436 |
| Another real wording difference for the same source | 9 | 28+154, 39+374, 69+217, 70+218, 93+244, 147+295, 178+314, 198+324, 301+351 |
| Real but trivial (an article) | 3 | 339+346, 339+441, 346+441 |
| Could not tell from the differences alone | 2 | 282+293, 282+411 |
| False alarm | 3 | 138+246, 293+348, 417+439 |

- **Known restated cases.** The inventory describes four of the six. Three are found. Two of them were found as
  first built; the third (same words in another order at the very start of the passage) only after rule 5. The fourth
  is a matter of meaning (two different things given one term), which a comparison of wording cannot see. The other
  two of the six are not described anywhere this session could read, so they could not be checked.
- **The twin with a note on only one.** Not measurable from this file: the run wrote its notes as Trados comments,
  so the saved translations carry none. What can be said: 19 near-twin pairs are found, and the only two that lie
  close enough together to be the known pair (108 apart in the saved file, 118 in Studio) are both among them. In a
  live run the notes are known and the line would be written; that is covered by the offline tests only.
- **False alarms.** One is a 380-word near-twin whose sources differ in 45 places and whose translations differ in
  46 (an "a"/"an" that follows a changed noun). One is a number range that the source writes as one word and the
  translation as two. One is two unrelated sentences sharing 8 words. So 3 of 22, or 5 of 22 counting the unclear
  ones.
- **What the first version got wrong.** 14 of its lines compared one list of values with a different one, and 6 more
  paired on runs that were numbers and a unit. None was worth reading. One true difference was lost with them.

One document, one kind. The values 8, 6 and 4 held; the three rules above were added. A contract, a manual and
software strings have only been tested on invented text.

## Not tested

- The check throwing inside a run (the catch in `RestatedSummaryAsync`): nothing in the collector can make the core
  call fail, so there is no honest way to provoke it from outside.
- A 32-bit run of `tools/restated_passages_test.ps1` (Studio 2024 / 2022 builds); it was run against the Studio 2026
  Debug build.
- Inside Studio. The orchestrator installs test builds.
- Right-to-left languages (Arabic, Hebrew). They are written with spaces, so they should behave like the others.
