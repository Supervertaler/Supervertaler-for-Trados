# Consistency test document: answer key

Written by `build_consistency_test.py`; do not edit by hand. The document is invented (Acme / PROJ-001).

- Source: `consistency-test-nl.txt`, Dutch, for translation into British English.
- **105 segments** expected in Trados Studio: one per line (plain text file type, default segmentation).
- Part 1, a contract: segments 1-32. Part 2, a manual: 33-66. Part 3, patent style with the claims at the end: 67-105.

## Planted pairs

| Kind | Segments | Apart | A correct result | Step 1's finder |
|---|---|---|---|---|
| verbatim | 6 and 13 | 7 | must agree | reports a difference |
| verbatim | 5 and 15 | 10 | must agree | reports a difference |
| verbatim | 4 and 19 | 15 | must agree | reports a difference |
| verbatim | 35 and 48 | 13 | must agree | reports a difference |
| verbatim | 35 and 60 | 25 | must agree | reports a difference |
| verbatim | 48 and 60 | 12 | must agree | reports a difference |
| verbatim | 39 and 51 | 12 | must agree | reports a difference |
| verbatim | 68 and 96 | 28 | must agree | reports a difference |
| verbatim | 72 and 96 | 24 | must agree | reports a difference |
| verbatim | 68 and 71 | 3 | must agree | reports a difference |
| verbatim | 71 and 72 | 1 | must agree | reports a difference |
| twin-claim | 71 and 96 | 25 | must agree but for the opening and the last link | reports a difference |
| verbatim | 75 and 97 | 22 | must agree | reports a difference |
| verbatim | 82 and 98 | 16 | must agree | reports a difference |
| reworded | 4 and 20 | 16 | should agree where the wording is the same | cannot see it |
| reworded | 35 and 58 | 23 | should agree where the wording is the same | cannot see it |
| twin-number | 17 and 31 | 14 | must agree but for the number | reports a difference |
| twin-number | 78 and 85 | 7 | must agree but for the number | reports a difference |
| punctuation | 27 and 30 | 3 | must agree word for word | reports a difference |
| defect-twin | 56 and 61 | 5 | must agree but for the colour, and a note on both or on neither | reports a difference, and a note on only one |
| list-under-limit | 54 and 62 | 8 | must agree but for the changed word | reports a difference |
| list-over-limit | 79 and 89 | 10 | must agree but for the changed word | reports a difference, but step 2 cannot show the earlier one |
| list-over-limit | 80 and 90 | 10 | must agree but for the changed words | reports a difference, but step 2 cannot show the earlier one |
| list-under-limit | 83 and 93 | 10 | must agree but for the changed words | reports a difference |

- **verbatim**: the same passage, word for word, inside two different sentences.
- **reworded**: the same thing said with small changes; at most six identical words in a row, so the finder does not pair them.
- **twin-number**: the same sentence but for one number.
- **twin-claim**: a long claim with several "waarbij" clauses, and the summary earlier in the description that repeats it.
- **punctuation**: the same words; one has a comma the other lacks.
- **defect-twin**: the same sentence but for one word, and both say "minimaal 10 minuten en maximaal 5 minuten", which cannot be right. A model that flags one should flag both.
- **list-under-limit / list-over-limit**: two long cascades of preferred values (minimum values, maximum values, ranges) that differ in a word or two. Under 400 tokens the earlier one can be shown to the AI (step 2), provided its translation stays under the limit too; over it, it cannot.

## Boilerplate

Segments 97, 98, 99, 100, 101, 102, 103, 104 open with the same formula ("Lamphouder volgens een van de voorgaande conclusies, met het kenmerk dat..."). The finder must not pair them with each other on the strength of that formula.

## Long segments

| Segment | Words | Tokens |
|---|---|---|
| 23 | 81 | 167 |
| 24 | 70 | 144 |
| 54 | 167 | 359 |
| 62 | 167 | 359 |
| 66 | 76 | 158 |
| 71 | 100 | 205 |
| 79 | 218 | 464 |
| 80 | 216 | 460 |
| 83 | 159 | 327 |
| 84 | 77 | 158 |
| 89 | 218 | 464 |
| 90 | 216 | 460 |
| 93 | 158 | 325 |
| 94 | 62 | 126 |
| 96 | 94 | 193 |

## Words that invite another English word each time

Not checked by steps 1 or 2; the counter only lists what it finds.

| Dutch | Segments |
|---|---|
| afneembaar | 9, 40, 49, 75, 88, 97 |
| aanzienlijk | 5, 15, 46, 65, 81, 87 |
| bevestigen | 12, 40, 68, 71, 75, 91, 96, 97 |
| stevig | 38, 41, 42, 76 |
| onderdeel | 16, 32, 45, 86 |
| controleren | 10, 11, 41, 52, 60, 84 |
| vervolgens | 40, 52, 53, 92 |
