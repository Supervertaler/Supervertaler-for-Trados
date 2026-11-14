# Restated passages – notes for the Help page and the changelog

Running notes for the user documentation, written while building (14 Nov 2026). Design:
`docs/design/restated-passages.md`.

## What the translator sees

At the end of a Batch Translate run, after "Complete: N translated" and the Repetitions line, the log may show:

```
Restated passages: 6 translated differently – 1 near-identical pair with a note on only one (listed).
Restated passages: Translated differently: segment 12 and segment 340 – source “de klemarm in een richting loodrecht op de …” (15 words the same)
Restated passages: Note on one only: segment 101 has a note, the near-identical segment 219 has none
```

If there is nothing to report, there are no "Restated passages" lines at all.

## What the lines mean

- **Translated differently.** The two segments contain the same run of source words (eight or more), and the
  translations of that passage are not worded the same. Typical in a patent: the description repeats a claim inside a
  longer sentence, hundreds of segments later, and the AI words it differently because it does not see its earlier
  translation. The quoted words are the start of the shared source passage, to search for. Open both segments and
  make them agree; in a patent the claim is usually the one to follow.
- **Note on one only.** The two segments are the same sentence but for a few words (a reference numeral, say). The
  AI left a note for the translator (`[[TC: ...]]`, or a Trados comment if you have chosen that) on one of them and
  not on the other. If the note is about a defect in the source, the other segment probably has the same defect.
- **(listed)** Up to 20 cases of each kind are listed; a last line says how many more there are.

## What it does not do

- It changes nothing. No segment is rewritten, nothing extra is sent to the AI, and it costs nothing.
- It does not look at single terms translated two ways ("removably" / "detachably"). It needs eight source words in
  a row.
- It does not compare punctuation or capital letters.
- It only knows the segments the run looked at. With a "Filtered" scope those are the filtered segments.
- Segments the run did not translate (already translated, or locked) are compared by the translation they have, so
  "claims first, then the rest with Empty segments only" works. Their comments are not read, so the "note on one
  only" line is only for segments this run translated.

## How it decides (plain version)

Words only: tags, case and punctuation are ignored. Two segments are a pair when they share eight identical source
words in a row. A phrase found in more than six segments is treated as boilerplate ("according to any one of the
preceding claims") and pairs nothing.

For a pair it then looks at the two translations: do they share one unbroken stretch of wording about as long as the
passage, or is the shared wording broken by a few different words? A break the source does not have is reported.

## Known false alarms

- The same source words sometimes rightly need different English around them (a clause that opens a sentence in one
  place and follows "characterised in that" in the other). It is reported anyway. Treat the list as places to look,
  not as errors.
- Where two translations differ in the same place as their sources do (a changed reference numeral) it is not
  reported. Occasionally a compound that is one word in Dutch and three in English is, though.

Known misses: a difference in the very first or last word or two of the passage; passages shorter than eight words.

False-alarm rate on a real job: not yet measured.

## Changelog line for the next release (after 20.207)

Under `### Added`:

> **Batch Translate reports restated passages that came out worded differently.** When a sentence repeats part of
> another one word for word – a patent's description restating a claim, say – and the two translations of that part
> differ, the log lists the two segments at the end of the run. It also lists near-identical segments where the AI
> left a note on only one. Nothing is changed and nothing extra is sent to the AI.

## Scale statement

Tested at 10,000 segments, designed for 10,000. Finding and comparing takes about a second at that size, once per
run, in the background; Studio's window is not blocked. Segments longer than 1,000 words are left out.
