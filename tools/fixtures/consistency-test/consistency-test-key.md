# Consistency test document: answer key

Written by `build_consistency_test.py`; do not edit by hand. The document is invented (Acme / PROJ-001).

- Source: `consistency-test-nl.txt`, Dutch, for translation into British English.
- **100 segments** expected in Trados Studio: one per line (plain text file type, default segmentation).
- Part 1, a contract: segments 1-32. Part 2, a manual: 33-66. Part 3, patent style with the claims at the end: 67-100.

## Planted pairs

| Kind | Segments | Apart | A correct result | Step 1's finder |
|---|---|---|---|---|
| verbatim | 6 and 10 | 4 | must agree | reports a difference |
| verbatim | 5 and 15 | 10 | must agree | reports a difference |
| verbatim | 4 and 19 | 15 | must agree | reports a difference |
| verbatim | 35 and 48 | 13 | must agree | reports a difference |
| verbatim | 35 and 61 | 26 | must agree | reports a difference |
| verbatim | 48 and 61 | 13 | must agree | reports a difference |
| verbatim | 39 and 51 | 12 | must agree | reports a difference |
| verbatim | 68 and 91 | 23 | must agree | reports a difference |
| verbatim | 71 and 91 | 20 | must agree | reports a difference |
| verbatim | 74 and 92 | 18 | must agree | reports a difference |
| verbatim | 80 and 93 | 13 | must agree | reports a difference |
| reworded | 4 and 20 | 16 | should agree where the wording is the same | cannot see it |
| reworded | 35 and 58 | 23 | should agree where the wording is the same | cannot see it |
| twin-number | 17 and 31 | 14 | must agree but for the number | reports a difference |
| twin-number | 77 and 82 | 5 | must agree but for the number | reports a difference |
| punctuation | 27 and 30 | 3 | must agree word for word | reports a difference |
| defect-twin | 56 and 59 | 3 | must agree but for the colour, and a note on both or on neither | reports a difference, and a note on only one |
| list-under-limit | 54 and 62 | 8 | must agree but for the changed word | reports a difference |
| list-over-limit | 78 and 86 | 8 | must agree but for the changed word | reports a difference, but step 2 cannot show the earlier one |

- **verbatim**: the same passage, word for word, inside two different sentences.
- **reworded**: the same thing said with small changes; at most six identical words in a row, so the finder does not pair them.
- **twin-number**: the same sentence but for one number.
- **punctuation**: the same words; one has a comma the other lacks.
- **defect-twin**: the same sentence but for one word, and both say "minimaal 10 minuten en maximaal 5 minuten", which cannot be right. A model that flags one should flag both.
- **list-under-limit / list-over-limit**: two long lists of values that differ in one word. Under 400 tokens the earlier one can be shown to the AI (step 2), provided its translation stays under the limit too; over it, it cannot.

## Boilerplate

Segments 92, 93, 94, 95, 96, 97, 98, 99 open with the same formula ("Lamphouder volgens een van de voorgaande conclusies, met het kenmerk dat..."). The finder must not pair them with each other on the strength of that formula.

## Long segments

| Segment | Words | Tokens |
|---|---|---|
| 23 | 81 | 167 |
| 24 | 70 | 144 |
| 54 | 160 | 349 |
| 62 | 160 | 349 |
| 66 | 76 | 158 |
| 78 | 212 | 456 |
| 81 | 77 | 158 |
| 86 | 212 | 456 |
| 89 | 62 | 126 |
| 91 | 62 | 126 |

## Words that invite another English word each time

Not checked by steps 1 or 2; the counter only lists what it finds.

| Dutch | Segments |
|---|---|
| afneembaar | 9, 40, 49, 74, 85, 92 |
| aanzienlijk | 5, 15, 46, 65, 79, 84 |
| bevestigen | 13, 40, 68, 74, 87, 91, 92 |
| stevig | 38, 41, 42, 75 |
| onderdeel | 16, 32, 45, 83 |
| controleren | 11, 12, 41, 52, 61, 81 |
| vervolgens | 40, 52, 53, 88 |
