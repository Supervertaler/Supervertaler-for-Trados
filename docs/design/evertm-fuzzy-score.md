# EverTM's fuzzy score, its difference display, and Studio's match repair

Investigation and proposal, 11 Nov 2026; Michael's decisions and what was built since, below. Base: Trados main
fdc14c9, Plugin Core main d7f8c61.

How this was found out:

- Studio 2026, 2024 and 2022's assemblies were read (Michael approved REA; done with ilspy and our IL scanner).
- Everything was measured through Studio's own file TMs in SuperVaultCheck.
- Real text was only ever read from temporary copies: EverTM's store, and the frozen copy of the project and its two
  TMs. No client names or text are in this note, the tests or the commits.

## Decisions (Michael, 11 Nov 2026)

Each is built as its own commit, so it can be tested and merged on its own.

1. **Score like Studio: yes.** Built from the prototype, in our own code (1c, and 1d below). Michael knows it can't
   be exact without Studio's own language resources, and accepts that about one pair in eight scores a few points
   higher than Studio.
2. **Replace a different number: yes, as a second step after the scorer.** Only when it is unambiguous: the same
   count of numbers on both sides, in the same order. Otherwise the proposal is left alone and the difference counts.
   A number put in the wrong place is a real translation error, so the rules are conservative (1e).
3. **EverTM's row may replace your TM's row for the identical unit when EverTM scores higher: yes.** No capping.
   "EverTM never lowers a match from your project TMs" must still hold, and its check stays.
4. **Studio's match repair on EverTM's matches: try it later**, with Michael in Studio, after 1-3. Low priority.
5. **Fix the two display defects now, separately: yes.** Done first: Trados 0c0c1fb, core 989de23.

## In short

- **Why EverTM scores lower (72% where Studio says 81%).** EverTM counts every different word or punctuation mark as
  one whole error against the longer segment. Studio:
  - weighs punctuation and spaces at a tenth of a word;
  - counts "disable"/"disabling" (same stem) and "The"/"the" (case) as nearly the same;
  - counts a moved word as one change, not two;
  - divides by both segments together, not by the longer one;
  - charges nothing for a different number, which it replaces in the translation itself.
- **Measured** (same pairs through Studio's own TM and EverTM):
  - natural pairs from the store copy: EverTM is **19 points lower** on average; 76% land in a different Analyze band;
  - the frozen project's claims: also 19 lower, 86% in a different band;
  - 1,560 of the 2,250 natural pairs Studio puts at 50-74% fall **below 50%** in EverTM, so EverTM doesn't show them
    at all.
- **A prototype** written from the described behaviour, in our own code:
  - gets Michael's two pairs exactly (81 and 87);
  - is within 0 points (median) on natural pairs, 66% within ±2 (4% today);
  - same speed as today.
- **What the prototype can't match exactly:**
  - Studio's own stopword lists and stemmers;
  - its tokenizer, which treats URLs, measurements and dates as one token.
  - So on 10-16% of natural pairs it scores a few points higher than Studio. "Never higher" can't be guaranteed
    without Studio's language resources, which aren't ours to copy.
- **Two display defects, both ours** (not Studio's drawing):
  1. Every inserted or missing word is handed to Studio one token too early, so the inserted text lands before its
     neighbour ("[, disabling]threshold").
  2. Every changed word is marked as a "resolved substitution", which Studio's source panel deliberately does not
     draw. So "the → a", "disable → disabling" and "wherein → by" show as unchanged.
  - Fixed in ~5 lines (core + plugin). Independent of the score.
- **The single row in Translation Results.** Studio merges results with the same stored source and the same
  translation and keeps the better-scored one. EverTM's 72% lost to the Studio TM's 81%. On a tie Studio keeps the
  project TM's (our ranking). The Help page's "you may see it twice" is wrong for identical units.
- **Match repair (the wrench).** Studio repairs a fuzzy match only when it carries word-alignment data between its
  source and target. Studio TMs have it when their fine-grained alignment has been built. EverTM's matches don't, so
  Studio never repairs them. The "up to 10 words" setting is not a limit on repair: above 10 words Studio repairs
  without its on-the-fly aligner.

## Part 1: the score

### 1a. How Studio scores a fuzzy match (Studio 2026; 2024 and 2022 identical)

Read from `Sdl.LanguagePlatform.Lingua` (`SegmentEditDistanceComputer`, `SimilarityComputers`, `EditDistanceComputer`)
and `Sdl.LanguagePlatform.TranslationMemoryImpl` (`AbstractScorer.ComputeSimplifiedMatchScore`). The weights, rules and
formula below are the same in all three versions (2022's is an instance method, otherwise identical). Described here
in our words.

**Step 1: align the two token lists.**

- Tokens:
  - words;
  - numbers and other placeables (dates, measurements, acronyms, variables, URLs);
  - punctuation marks, one each;
  - whitespace runs;
  - tags.
- The alignment is an edit distance: inserting or deleting a token costs 1, changing a token costs 1 minus how similar
  the two are.
- How similar two tokens are:

  | The two tokens | Similarity |
  |---|---|
  | identical | 1 |
  | the same word apart from case or accents | 0.95 |
  | the same stem (Studio's stemmer for the language: "disable"/"disabling") | 0.95 |
  | two different punctuation marks, or two different spaces | 0.94 |
  | two placeables of the same kind (two numbers) | 0.85 |
  | a placeable with the same value (two equal numbers) | 1 |
  | two tags of the same type | 0.95 |
  | otherwise | 0 |

- A word, a space and a punctuation mark can never be changed into one another.
- Character similarity is not used for the alignment.
- After aligning, a deleted and an inserted token that are (nearly) identical become one **move**. Only among cells the
  lazy alignment actually computed, so not every move is found.

**Step 2: charge each step.**

| Step | Charge |
|---|---|
| a word inserted or missing | 1.5 (0.7 if the word has 1-2 characters) |
| a punctuation mark or space inserted or missing | 0.1 |
| a word changed into the same word (case, accents, stem) | 0.2 |
| a word changed into one that looks alike (character similarity at least 0.75) | 0.7 |
| a word changed into another word | 1.5 |
| a punctuation mark or space changed | 0.1 |
| a placeable replaced by Studio (a different number, date...) | 0 |
| a move | 0.8 |

Character similarity, for the 0.7 band: two words' character edit distance as (length A + length B - 2 × distance) /
(length A + length B). An upper/lower-case difference counts 0.05, an accent 0.1, and one small change in a word of up
to 3 letters 0.3. It is then rounded down to 1, 0.9, 0.75, 0.5 or 0.

**Step 3: the score.**

- weight = the weight of both segments together: 1 per word or placeable, 0.1 per punctuation mark or space, 0 per tag;
- off = floor(100 × charges / weight);
- **stopwords**: when both have more than 5 words and the words they share are mostly stopwords ("the", "of"...), up
  to 7 points more off (by how much the shared words' stopword share exceeds the segments'); for short segments, a
  smaller amount;
- a score with something off is never 100;
- score = 100 - off.
- Penalties (formatting, filters, provider) come after, as for any result.

**Why that gives 81% where EverTM gives 72%.**

- EverTM divides the errors by the longer segment's word count. Studio divides by both segments' weight, so the same
  errors cost about half.
- EverTM counts a punctuation difference as a whole error; Studio as 0.1.
- EverTM counts a changed word as 1 error; Studio charges 1.5, but against the doubled weight.
- "disabling"/"disable" is a near-match for Studio (0.2) and a full change for EverTM (1).
- A different number: nothing for Studio, a change for EverTM.

### 1b. Measured

Harness: `SuperVaultCheck.exe score-compare` (synthetic Acme pairs with one known kind of change each, and natural
pairs found by a Studio file TM made from a copy of EverTM's store), `score-compare-xliff` (a copy of a Studio TM
against copied .sdlxliff files), `score-rescore` (a stored pair set rescored without Studio). For each pair: Studio's
BaseScore from its own file TM, and EverTM's score for the same document segment and stored source.

EverTM today:

| Pairs | n | Mean EverTM - Studio | Within ±2 | EverTM higher | Other Analyze band |
|---|---|---|---|---|---|
| synthetic (Acme), 13 kinds of change | 2,785 | -3.2 | 50% | 7.7% | 36% |
| natural (store copy, 3,000 sentences against 57,000 units) | 2,405 | -19.2 | 4% | 3.7% | 76% |
| the frozen project's claims against its TM copies | 55 / 53 | -18.9 / -18.5 | 6% | 2% | 86% |
| Michael's two pairs | 2 | 72 vs 81, 76 vs 87 | | | both one band lower |

- By length (natural and synthetic together), EverTM - Studio:
  - mean -17 for 0-7 words;
  - -12.5 for 8-15;
  - -9 for 16-30;
  - -4.4 for 31-50;
  - -2.1 above 50.
- The biggest single cause is the denominator: errors over the longer segment instead of over both.
- By kind (synthetic), mean EverTM - Studio:
  - an inserted 3-word phrase with commas -6.4;
  - a rephrased stretch -8.3;
  - a comma added or removed -5;
  - a capital letter -4.5;
  - an inflection -3.6;
  - a different number -3.9 (Studio: 100%).
- Analyze bands, natural pairs: of 2,248 pairs Studio puts at 50-74%, EverTM puts 1,562 below 50% (not shown at all at
  the usual minimum), 652 at 50-74%.

The prototype (`tools/SuperVaultCheck/StudioLikeScore.cs`):

- it follows 1a;
- it uses EverTM's tokenizer and Lucene's Porter/Snowball English stemmer (Apache-2.0);
- its stopword list is the Snowball project's public English list (BSD), not Studio's.

| Pairs | Number change charged | Mean | Median | Within ±2 | Higher | Other band |
|---|---|---|---|---|---|---|
| synthetic | as a word (1.5) | ~0 for every kind except numbers | 0 | 97-100% per kind | 0-3% (moves 17%) | 0-5% |
| natural | as a word | -4.2 | 0 | 56% | 10% | 26% |
| natural | free, as Studio | -1.5 | 0 | 66% | 16% | 15% |
| claims (BEIJER TM copy) | as a word | -7.9 | -8 | 24% | 7% | 56% |
| claims | free, as Studio | -2.6 | 0 | 60% | 13% | 22% |
| Michael's two pairs | either | 81 = 81, 87 = 87 | | | | |

- **Where it still differs:**
  - lower: lines of numbers, codes and formulas, where Studio's placeables score even nonsense pairs at 60-90%;
  - higher (mostly +1 to +3, at 50-80%): segments with measurements ("3 cm", "0.1 wt%"), URLs, "%s" placeholders,
    and pairs where our stopword list differs from Studio's. Without any stopword correction 46% of natural pairs
    come out higher, so the correction matters.
- **A safety margin doesn't fix it:** taking 1 or 2 points off every fuzzy score lowers "higher" only from 16% to
  10% (natural) and costs "within ±2".
- **Speed:** 0.37 ms a pair against 0.33 ms today (harness, tokenizing included). A lookup aligns at most 40
  candidates, so about 1-2 ms more than the 26 ms today.

### 1c. Proposal: VaultDiff scores as described in 1a

**Changes in Plugin Core (`core/src/SuperVault/VaultDiff.cs`, our own code from the description, no RWS code):**

1. Align by similarity: the table in 1a, with stems from a stemmer for the language and accents folded; then moves.
2. Charge and score as in 1a (both segments' weight, the charges, never 100 unless identical).
3. Stopwords: the public Snowball lists, for the languages EverTM holds. Stemmers: the Snowball stemmers.
   - Both come with **Lucene.Net.Analysis.Common** (Apache-2.0, the same project and version as the Lucene.Net EverTM
     already ships; one more DLL).
   - For a language without a stemmer, no stem matching: that only makes the score lower.
4. One token for a URL, an e-mail address and a placeholder ("%s", "{0}"), as Studio does: otherwise they come out
   higher.
5. **Numbers: Michael's choice (Q2).**
   - Either a different number keeps a charge (EverTM stays lower on claim numbers: claims about 8 points lower);
   - or EverTM replaces the number in its proposal the way Studio does (the number of the document where the stored
     source and translation have the same number once each), and then charges nothing: claims within 0-2.
   - Recommended: the replacement, as a second step.

**Effects:**

- **Min-score cut-off:** scores rise for most pairs, so more candidates pass 70%. The pre-filter by length must stay an
  upper bound: a stored segment of m words against n can't score more than
  100 - 70·|n-m| / (1.1·(n+m)), as each missing word costs at least 0.7. It replaces today's
  100·min/max + 10.
- **Top-200 / top-60 selection** (rarest words, Lucene's score): unchanged.
- **The quick estimate used to pick the 40 to align** (word overlap, Dice) must also stay an upper bound. The new score
  is higher than Dice for long, slightly different segments, so the estimate becomes "100 minus the cheapest possible
  charge for the unmatched words". This needs a test against the harness pairs, to show it is never below the real
  score.
- **Index format:** no change, so no rebuild. Words, counts and hashes are as now; stems are computed for the candidates
  at lookup only.
- **Speed:** about 1-2 ms more per lookup (40 alignments × ~0.04 ms more, plus stemming the candidates). To be
  measured at 871k and 10M units.
- **"Never lowers your project TMs' matches":** the ranking rule (YieldRank) is unchanged.
  - With an equal score, the project TM's row is kept, as now.
  - Where EverTM scores a few points higher than Studio for the very same unit, Studio's merge keeps EverTM's row
    instead of the project TM's (same translation, other origin; the TM's wrench repair is then not shown). That is
    the price of the residual "higher" (Q3).
- **Concordance:** untouched (it scores word coverage, not edits).

### 1d. Built: the Studio-like score (decision 1)

**What changed (Plugin Core, our own code from 1a; no RWS code):**

- `VaultDiff.Compare` scores as in 1a: alignment by similarity (case, accents, stem; punctuation and spaces 0.94;
  numbers 0.85; a word, a punctuation mark, a space and a tag never changed into one another), then moves; the
  charges of 1a; off = floor(100 × charges / weight of both segments); the stopword correction; 100 only for
  identical text. A different number is charged as a changed word (1.5) until decision 2.
- Stems and stopwords: `VaultLanguage`, the Snowball stemmers and stopword lists from **Lucene.Net.Analysis.Common**
  (Apache-2.0; lists BSD), the same Lucene.Net version as the index. For Danish, Dutch, English, Finnish, French,
  German, Hungarian, Italian, Norwegian, Portuguese, Russian, Spanish and Swedish; other languages get none (only
  ever a lower score). One more DLL in the plugin (5 MB; listed in the three manifests, `package_plugin.py` and the
  assembly resolver, which a new check verifies).
- Soft hyphens and zero-width characters inside a word no longer split it, so a segment's word count is the index's.
- Segments over 400 tokens are scored on what they share (exact words, punctuation, spaces) with the same charges,
  no alignment, no differences drawn.
- **The differences are drawn with an alignment of their own** (`VaultDisplay`, the display fix's), Michael's choice
  ("clearer highlighting"): the score's Studio-like alignment pairs words across a rephrased stretch as Studio does
  ("whereinby the hardware prefetcherone or..."), the display one shows words inserted and struck, in order. The
  percentage is always the score's. Run only for the rows handed to Studio. Checked on his two pairs: 81 and 87,
  rows as he saw them in Studio on 11 Nov.

**Upper bounds, so no real match is dropped before it is aligned:**

- Before reading a unit, from its word count (index doc values): `VaultLengthBound`. Every word one side has more
  than the other is missing at least as a short word (0.7) against the weight of both; the unit's punctuation is
  unknown, so the bound takes the count that scores highest; a tag the match keeps may split a word, so the unit may
  have up to as many more words as the document has tags. Replaces "min/max words + 10 points", which no longer held
  (the new scores are higher). It lets more through: a 10-word unit against a 20-word segment can now reach 79%.
- Before aligning, from the tokens the unit will be scored on (its tags placed first): `VaultDiff.Estimate`. What
  the two share exactly; every unshared word at best a near-identical change (0.2) or, where one side has more, a
  missing short word (0.7); every unshared punctuation mark or space at least 0.1. Replaces the word-overlap (Dice)
  estimate + 10 points, which the new score exceeds on long, slightly different segments.
- Both checked against the real score on 20,002 random pairs (words dropped, added, changed, moved, capitalised;
  tags inside words; long ones) and Michael's pairs: never below it (core test
  `EstimatesAreNeverBelowTheScore`). Rounding is kept on the safe side (the score rounds up by 1e-9, the
  estimates down).
- The candidates are aligned best estimate first, and the lookup stops when the rows found so far beat the next
  candidate's estimate: the same result as aligning them all.

**Index:** no change and no rebuild. Words, counts and hashes are as before; stems are computed at lookup.

**Measured after the change** (same harness and pairs as 1b; Studio's BaseScore from its own file TM):

| Pairs | n | Mean EverTM - Studio | Median | Within ±2 | EverTM higher | Other Analyze band |
|---|---|---|---|---|---|---|
| all (synthetic and natural) | 5,342 | -1.6 | 0 | 79% | 6.5% | 14% |
| natural (store copy) | 2,557 | -3.2 (was -19.2) | 0 | 60% (was 4%) | 10.5% | 23% (was 76%) |
| synthetic, each kind except numbers | 168-240 each | 0.0 to 0.3 | 0 | 94-100% | 0-17% (moves 17%) | 0-5% |
| synthetic, a different number | 152 | -2.5 | -1 | 72% | 0% | 99% (Studio: 100%) |
| the frozen project's claims (two TM copies) | 55 / 53 | -7.8 / -6.6 (was -18.9 / -18.5) | -8 | 22% / 23% | 9% | 56% (was 86%) |
| Michael's two pairs | 2 | 81 = 81, 87 = 87 | | | | |

- The claims' remaining gap is almost all claim numbers, which Studio replaces itself: decision 2.
- Analyze bands, natural and synthetic: of 2,360 pairs Studio puts at 50-74%, EverTM puts 453 below 50% (it was
  1,562 of 2,248 natural pairs alone).
- The new score follows the prototype within a fraction of a point on every row.

**Speed** (core `real` harness, a copy of EverTM's store, 872,340 units; 111 Workbench sentences, exact and with
one word changed; minimum 70%; median per lookup):

| | Before | After |
|---|---|---|
| 64-bit (Studio 2026) | 30 ms (p90 35) | 37 ms (p90 44) |
| 32-bit (Studio 2024) | 20 ms (p90 26) | 33 ms (p90 53) |

- Found: the same (111/111 exact; 109/111 found first with one word changed), before and after.
- At 10 million units (a copy of the store padded with marked copies of its units to 10,000,000; index 2.5 GB,
  built in 51 minutes), median per lookup: 64-bit 77 → 82 ms (p90 118 → 119), 32-bit 56 → 57 ms (p90 92 → 93).
  The index's candidate search is 75 ms (64-bit) and 50 ms (32-bit) of that, unchanged; scoring adds a few ms. Found:
  111/111 exact and 109/111 first with one word changed, before and after.
- Where the time goes, per lookup (64-bit): the index's candidate search 26 ms (unchanged), the estimates of about
  60 candidates 2 ms, aligning and scoring 4-6 ms (all of them 6.5 ms; 17 ms in 32-bit), the display alignment of
  the rows shown 0.4 ms.
- The display alignment: 0.8 ms for a claim-length row, 10.5 ms for the largest (400 × 400 tokens) in a 64-bit
  release build; at most the number of results (Studio's default 5) such rows per lookup, so 50 ms at the very
  worst.

**At the first start after updating:** nothing is rebuilt; EverTM works at once. Fuzzy matches get Studio-like
percentages, mostly higher than before (natural sentences by about 16 points on average), so matches that used to
fall below the minimum now show, and Analyze's EverTM counts move up a band or two.

### 1e. Built: replacing a different number (decision 2)

`VaultNumbers` (Plugin Core). A number is a run of digits with "." or "," between digits ("1,000.5"), not glued to a
letter ("M8", "2nd", "H2O" are words). EverTM puts the document's number into its proposal **only when all of these
hold**, and otherwise leaves the proposal alone and the difference counts as a changed word (1.5):

1. the document and the stored source have the same number of numbers, and the stored translation has exactly the
   stored source's numbers, written the same way and in the same order;
2. each number that differs occurs in the stored source (and so in the translation) only once;
3. the document's number isn't one of the stored source's own numbers (that would be numbers moved around);
4. the two are written the same way apart from their digits: the same separators in the same places.

| Case | Replaced? |
|---|---|
| claim 8 → claim 9; "(116)" → "(118)" | yes |
| a range "5-10 mm" → "6-12 mm"; "from 5 to 10" → "from 6 to 12" | yes, both ends |
| one of two numbers changed ("8 or 9" → "8 or 10") | yes, that one |
| "3.5" → "4.25", with "3.5" in the translation too | yes |
| "1,000" → "2,000", written alike in the translation | yes |
| a date in the same order in the translation, each part different and unique ("11-10-2026" → "13-12-2026") | yes |
| numbers moved around ("8 and 9" → "9 and 8") | no (rule 3) |
| the changed number occurs twice ("from 4 to 4") | no (rule 2) |
| a date whose day and month are the same number | no (rule 2) |
| a date the translation writes in another order | no (rule 1) |
| decimal comma against point, either way | no (rules 1 and 4) |
| "1,000" → "999" | no (rule 4) |
| a number more or fewer in the document | no (rule 1) |
| the translation has a number the source hasn't, or writes it in words | no (rule 1) |
| "M8" → "M10", "2nd" → "3rd" | no: words, not numbers |
| "5 mm" → "5 cm" | nothing to replace: the unit is a word |

- A replaced number costs nothing toward the score, as in Studio, and the step is marked as Studio marks its own
  replacements (a resolved substitution), so it isn't drawn as a difference, in the score's alignment and in the
  display one alike.
- A segment that differs only in a replaced number scores **99%**: 100% stays for identical text. Studio gives 100%.
- Tags around a number are kept; a number split by a tag isn't a number to the rules, so nothing is replaced then.
- The estimate (1d) counts two different numbers as free, so it stays an upper bound (core tests).
- Checked through the cascade (SuperVaultCheck): claim 8 → 9 is replaced, 99% (Studio's own TM 100%); claims "8 and
  9" → "9 and 8" are not, 88% (Studio 100%: Studio replaces more freely than EverTM does).

**Measured** (the frozen project's claims, two TM copies): mean EverTM - Studio -7.8 → **-6.1** and -6.6 → **-5.3**,
within ±2 22% → 33%. Less than the prototype's -2.6, which replaced every different number: most claim pairs differ
in how many numbers they have ("claim 8 or 9" against a stored "claim 1"), and those are left alone by rule 1.

## Part 2: difference display and repair

### 2d. Source-side highlighting

- **How Studio draws it for a provider:** from the provider's own `ScoringResult.EditDistance`
  (`TranslationMemoryViewPartControl.ShowDocumentSource`, inverted to go from the stored source to the document). It is
  not recomputed.
  - Drawn: inserts, changes and moves.
  - Not drawn: steps marked as resolved by Studio, as it does with its own placeables (`ShowResolvedSubstitutionOperations = false`).
  - Not drawn: the document's missing words (`ShowDeleteOperations = false`).
- **Defect 1, wrong place and order.**
  - In Studio's edit list, a token only the document has carries the position of the stored token that *follows* it.
  - EverTM's `VaultDiff.Align` gives the one *before* it (`B = y - 1`; the same for inserts, `A = x - 1`), so every
    insertion is drawn one token early: "[, disabling]threshold", the comma between "more" and "processors".
  - Reproduced: Studio D5>5 D6>5, EverTM D5>4 D6>4 for "the pump starts quickly now" against "the pump starts now".
  - Fix: `B = y`, `A = x` (Plugin Core).
- **Defect 2, changes not marked.**
  - `SuperVaultResults.ToEditDistance` marks every changed word `EditDistanceResolution.Substitution`, so Studio's
    source panel skips it: "the → a", "disable → disabling", "wherein → by", "determine → determining" look unchanged.
  - Reproduced: 3 of 3 changes in the Acme pair of the same shape.
  - Fix: `Resolution = None` (the plugin), as Studio's own TMs give for a changed word.
- **Granularity.**
  - With these two fixes, EverTM's display has the same kinds of marks as Studio's.
  - With the 1c alignment (moves, near-identical words aligned as changes) it also has the same shape: "the→a" as a
    change, "disable→disabling" as a change, not a deletion plus a long insertion.
  - Recommended: match Studio's. Two TMs side by side that mark the same pair differently make the reader distrust
    one of them, and Studio's finer marks show what to edit.
- Checks: `SuperVaultCheck.exe diff-display`, with an Acme pair of the same shape as the reported one. The two
  defects fail until fixed; then they move into the regular run.

### 2e. Target-side highlighting and the wrench: Studio's Fuzzy Match Repair (upLIFT)

Read from `Sdl.ProjectApi.Translation.FuzzyMatchRepair` and `Sdl.Core.MatchRepair`.

- **Which results Studio repairs** (`FuzzyMatchRepairSegmentFilter`):
  - a fuzzy match (or a 100% with a tag mismatch);
  - whose proposal's origin is TM;
  - and whose proposal carries **`AlignmentData`**, the word alignment between its source and target.
  - It doesn't check which provider the result came from.
- **Why EverTM's matches are never repaired:** their origin is TM, but they carry no AlignmentData.
- **Why only one of Michael's two Studio TMs showed the wrench:** only that TM has alignment data, which a Studio TM
  gets when its fine-grained alignment (the "translation model") has been built.
- **"Perform Match Repair on segments up to a length of 10 words":** not a limit on repair. A segment of more than 10
  words is still repaired, without Studio's partial aligner (`UsePartialAligner = words <= MaxSegmentLength`). That is
  why a 59-word segment was repaired.
- **Where the repair came from:** with Termbases switched off as a repair source, the wrench went: the repair was the
  termbase's "one or more" → "een of meer".
- **Can EverTM's matches be repaired by Studio?** The door is open to a provider: `TranslationUnit.AlignmentData` is a
  public property, and the filter doesn't ask for anything else. But:
  - Studio's public `TranslationUnit.GenerateAlignmentDataAsync()` aligns only placeables (tags, numbers), not words;
  - with that alone, Studio could at most repair short segments (up to the 10 words, where its partial aligner runs)
    and placeables;
  - real repairs of long segments need a word alignment of every stored unit: our own sub-segment aligner, trained on
    EverTM's units (a statistical word aligner over 871k units). A project of its own.
- **Recommendation:**
  - not now;
  - a small experiment first, with Michael in Studio (Q4): EverTM's matches carrying placeable-only alignment data;
  - see whether Studio repairs short segments, and whether anything goes wrong (Analyze counts, the wrench on matches
    that are only partly repaired).
- **Relation to the AI "FuzzyFixer" (session local_1c16f6ea):** complementary.
  - Studio's repair is deterministic and local: termbase, MT or TM fragments, and only for matches with alignment data.
  - FuzzyFixer is our own repair with AI, for any fuzzy, EverTM's included.
  - Where both are on, FuzzyFixer should start from Studio's repaired proposal (the wrench), not undo it. That's for
    its session to settle.

### What the Help page should say

- Today: "If EverTM holds the very same translation, you may see it twice: once from your TM, listed first, and once
  from EverTM, below it."
- Proposed: "If EverTM holds the very same unit as one of your project's TMs, Studio shows it once: the better match of
  the two, and your TM's when they score the same."
- Studio's merge (`SearchResults.Merge` with duplicates removed) treats results with the same stored source and the
  same translation (tags and normalised text) as one and keeps the higher score; on a tie, the result Studio ranks
  first (EverTM's always ranks after).
- A unit differing only in formatting (other tags) is shown twice.
- Checked: SuperVaultCheck, same unit in a Studio TM and EverTM, EverTM first or last: one row, the Studio TM's.

**Decision 3, built (checks only: Studio's merge already does it).** EverTM's row replaces the project TM's for the
very same unit when EverTM scores higher; no capping. `RowChecks` in SuperVaultCheck, EverTM first and last in the
list:

- a tie (Michael's second claim pair, 87% and 87%): one row, the project TM's;
- EverTM higher (a moved word Studio's alignment doesn't see as a move: Studio 92%, EverTM 95%): one row, EverTM's,
  at 95%;
- "EverTM never lowers your project TMs' matches" (no "multiple translations" penalty because of EverTM) stays
  checked by `NeverLowersTheClientTm`.

**The Help wording ("one row, not two"):**

> If EverTM holds the very same unit as one of your project's TMs – the same source sentence and the same
> translation – Studio shows it once in Translation Results: the better match of the two, and your TM's when they
> score the same. Now that EverTM scores like Studio, they usually score the same, so you see your TM's row; where
> EverTM happens to score a point or two higher for the very same unit, you see EverTM's row instead, with the same
> translation. EverTM never lowers a match from your project's TMs. A unit that differs only in its formatting (other
> tags) is a different unit to Studio, and is shown twice.

## Questions for Michael (one at a time, each with a recommendation)

1. **Make EverTM score like Studio (1c), knowing it can't be exact without Studio's own language resources?**
   - Typically the same score as Studio; sometimes a few points higher (about one pair in eight, mostly in the
     50-80% range), sometimes lower on number-heavy lines.
   - Recommended: yes.
2. **Numbers: should EverTM replace a different number in its proposal (as Studio does) and then not count it as a
   difference?**
   - Without it, claims with other claim numbers stay a band lower in EverTM.
   - Recommended: yes, as a second step.
3. **Is it acceptable that, where EverTM scores slightly higher than your project TM for the same unit, Studio shows
   EverTM's row instead of your TM's (the same translation)?**
   - Recommended: yes. The other way, a safety margin, costs accuracy everywhere and doesn't prevent it.
4. **Try, with you at the computer, whether Studio's match repair works on EverTM's matches when they carry simple
   alignment data?**
   - Recommended: yes, after 1-3, on a test project.
5. **Fix the two display defects now, separately from the score?**
   - They are small and independent.
   - Recommended: yes, first.
