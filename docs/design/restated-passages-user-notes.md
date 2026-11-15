# Repeated passages – notes for the Help page and the changelog

Running notes for the user documentation, written while building (14 Nov 2026). Design:
`docs/design/restated-passages.md`. The log calls them "repeated passages"; the code says "restated".

## What the translator sees

At the end of a Batch Translate run, after "Complete: N translated" and the Repetitions line, the log may show:

```
Repeated passages: 2 translated differently (listed).
Repeated passages: Translated differently: segment 12 and segment 340 – source “any information disclosed by one party to the …” (15 words the same)
Repeated passages: Translated differently: segment 48 and segment 207 – source “Disconnect the appliance from the mains supply before …” (11 words the same)
```

If there is nothing to report, there are no "Repeated passages" lines at all.

## What it is for

Batch Translate sends the document a few segments at a time, and the AI does not see what it wrote earlier. When a
document says the same thing twice in the same words, the two translations can differ. This works on any document.
Examples for the Help page (use more than one):

- **Contract:** a definition ("Confidential Information means any information disclosed by one party ...") and a
  later clause that restates it.
- **Manual:** a warning or a step repeated in several chapters, on its own or inside a longer sentence.
- **Software strings:** two messages that are the same sentence but for one word ("The file could not be saved
  because ..." / "The project could not be saved because ...").
- **Patent:** a description sentence that repeats a claim.

## What the lines mean

- **Translated differently.** The two segments contain the same run of source words (eight or more), and the
  translations of that passage are not worded the same. The quoted words are the start of the shared source passage,
  to search for. Open both segments and make them agree.
- **(listed)** Up to 20 cases are listed; a last line says how many more there are.

## What it does not do

- It changes nothing. No segment is rewritten, nothing extra is sent to the AI, and it costs nothing.
- It does not look at single terms translated two ways. It needs eight source words in a row.
- **Short segments** (menu items, buttons, headings under eight words) are not compared. Identical ones are already
  kept the same by "Keep repetitions consistent".
- A phrase that occurs in more than six segments is treated as boilerplate and not compared.
- A segment that is one long list of values ("at least 0.7 mm, preferably at least 0.8 mm, ...") is only compared
  with a segment that is the same list but for a few words.
- It does not compare punctuation or capital letters.
- It only knows the segments the run looked at. With a "Filtered" scope those are the filtered segments.
- Segments the run did not translate (already translated, or locked) are compared by the translation they have, so
  translating a document in two runs works.
- It does not look at the AI's notes for the translator (`[[TC: ...]]` or Trados comments). A note is not part of
  the translation compared, and a note on only one of two near-identical segments is not reported.

## Languages

Any language written with spaces between words, as source or target. Not yet for Chinese, Japanese, Thai, Lao, Khmer
or Burmese, as source or as target: for those no "Repeated passages" lines are written. Korean works.

## How it decides (plain version)

Words only: tags, case and punctuation are ignored. Two segments are a pair when they share eight identical source
words in a row.

For a pair it then looks at the two translations. They may differ in as many places as the two sources do: if the
sources differ in one word, the translations may differ in one place. A difference the sources do not have is
reported.

## Known false alarms

- The same source words sometimes rightly need different wording around them. It is reported anyway. Treat the list
  as places to look, not as errors.
- One changed source word that changes two things in the translation (a noun and, further on, a word that agrees
  with it) is reported.

Known misses: a different word (not just another word order) in the very first or last word or two of a passage;
passages shorter than eight words; passages in more than six segments; a difference of meaning that is worded
consistently.

Measured on one real job (a 460-segment patent, 14 Nov 2026): 22 lines, in under half a second. 15 were real wording
differences, 2 real but trivial (an article), 2 unclear, 3 false alarms. Of six places already known to be
inconsistent, it found four. Not yet measured on any other kind of
document.

## Changelog line for the next release (after 20.207)

Under `### Added`:

> **Batch Translate reports repeated passages that came out worded differently.** When two segments contain the same
> run of source words – a contract clause restating a definition, a warning repeated across the chapters of a manual,
> two messages that differ in one word – and the translations of that part differ, the log lists the two segments at
> the end of the run. Nothing is changed and nothing extra is sent to the AI. Not yet for Chinese, Japanese or Thai.

## Scale statement

Tested at 10,000 segments, designed for 10,000. Finding and comparing takes about a second at that size, once per
run, in the background; Studio's window is not blocked. Segments longer than 1,000 words are left out.
