# EverTM's fuzzy matches – notes for the Help page

Written from a user's point of view while the work lands (design: `evertm-fuzzy-score.md`). Parts not built yet are
marked *(planned)*.

## How EverTM scores a fuzzy match

- EverTM scores a fuzzy match the way Studio scores one from its own TMs, so an EverTM match and a Studio TM match
  of the same sentence normally show the same percentage, and Analyze puts them in the same band.
- What counts, as in Studio:
  - a missing or extra word counts more than a missing comma or space;
  - a word in another form ("disable"/"disabling"), with another capital letter or without its accent counts as a
    small change;
  - a word that has moved counts as one change, not two;
  - the differences are weighed against both sentences together.
- It can't be exactly Studio's percentage: Studio uses its own word lists and its own way of reading measurements,
  web addresses and dates, which aren't public. Typically the two are the same; about one match in eight is a few
  points higher in EverTM, mostly between 50% and 80%; some lines that are mostly numbers or codes are lower.
- Word forms and common words ("the", "of") are recognised for Danish, Dutch, English, Finnish, French, German,
  Hungarian, Italian, Norwegian, Portuguese, Russian, Spanish and Swedish. In other languages a different word form
  counts as a different word, so a match there can be a few points lower than in Studio, never higher for that
  reason.
- 100% is still only for exactly the same sentence.
- The differences are drawn in the clearer way Michael chose: where a stretch was reworded, EverTM shows the new
  words inserted and the old ones struck, in order, where Studio pairs words across the stretch. So an EverTM row and
  a Studio TM row for the same pair may mark the differences differently; the percentage is what matches.
- Very long segments (over about 200 words) are scored on the words they share and show no differences.

## First start after updating

- Nothing is rebuilt: EverTM works at once.
- The same sentences now get the percentages Studio would give: most fuzzy matches are higher than before (on real
  sentences, typically by 10 to 20 points), and matches that used to fall below your minimum now show. Analyze
  counts for EverTM change accordingly.

## One row, not two

- If EverTM holds the very same unit as one of your project's TMs – the same source sentence and the same
  translation – Studio shows it once in Translation Results: the better match of the two, and your TM's when they
  score the same.
- Now that EverTM scores like Studio, they normally score the same, so you see your TM's row. Where EverTM happens to
  score a point or two higher for the very same unit, you see EverTM's row instead, with the same translation.
- EverTM never lowers a match from your project's TMs: your TM's match keeps its percentage, and Pre-translate uses
  it as before.
- A unit that differs only in its formatting (other tags) is a different unit to Studio, and is shown twice.

## Different numbers *(planned)*

- When the only difference is a number, EverTM puts the document's number into its proposal, as Studio does, and
  doesn't count it as a difference – but only when it can't go wrong: the same numbers in the same order on both
  sides, each changed number only once in the sentence, written the same way. Otherwise the proposal is left as it
  is, the number is shown as a difference, and it counts.
