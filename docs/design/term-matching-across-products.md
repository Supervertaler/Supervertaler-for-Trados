# Term matching across products

Supervertaler for Trados and Supervertaler for memoQ read and write **the same
termbase**. A term saved in one must be findable in the other. Nothing enforces
that, so the rules below are the agreement, arrived at jointly on 2026-09-18/19
after each product broke the other's terms without noticing.

This note lives here for now. It belongs in the shared core repository, and
moves there once the App Store submission is out.

---

## 1. Why this is the silent kind of defect

Both products match by tokenising the segment, folding, and looking the token up
in an index built from the stored terms. If the two disagree about what a word
is or what a character means, the user sees a termbase that works in one tool
and not in the other, with no error anywhere. Both products shipped an instance
of exactly this within a day of each other:

- Trados saved `O₃` as `O`, because .NET's letter-or-digit test rejects a
  subscript digit, so trimming a selection to word edges ate it.
- memoQ, for the same reason, saw no word boundary either side of the `O` in
  `H₂O₂` and matched a one-letter term inside the formula.

Neither produced an error. Both looked like the feature working.

## 2. The fold

Applied when a term is indexed **and** when a segment is read. Change it in one
product only alongside the same change in the other.

**2a. Identical in both products** (memoQ adopted these verbatim on 2026-09-19):

| from | to |
|---|---|
| subscript digits U+2080–U+2089 | the plain digit |
| superscript digits U+2070, U+00B9, U+00B2, U+00B3, U+2074–U+2079 | the plain digit |
| U+207A `⁺`, U+208A `₊` | `+` |
| U+207B `⁻`, U+208B `₋` | `-` |
| U+00B7 `·`, U+2219 `∙`, U+22C5 `⋅`, U+2022 `•` | U+00B7 `·` |

**2b. Trados only — an open cross-product gap.** These two rows predate the
joint work and memoQ does not implement them, confirmed by measurement on
2026-09-19. A term stored with a curly apostrophe matches a segment written with
an ASCII one in Trados and **does not** in memoQ, in the same termbase, with no
error. Apostrophes and non-breaking spaces are far commoner in this source
material than any formula, so this is the larger of the two gaps this note
records. memoQ adopts both lists after the App Store submission; until then the
divergence is real and this section is the record of it.

| from | to |
|---|---|
| U+00A0, U+1680, U+2000–U+200A, U+202F, U+205F, U+3000 | space (U+0020) |
| U+2018 `‘`, U+2019 `’`, U+02BC `ʼ`, U+FF07 `＇` | `'` (U+0027) |

Note that U+200B and the other zero-width characters are deliberately **not** in
the space list: they are not spaces and are removed on the write path instead
(section 7).

**Every mapping is one character to one character.** This is not incidental.

Matching runs on the folded text while highlighting uses offsets into the
original, so a fold that changed a length would silently misplace every
highlight. Both products have a test for it rather than a comment.

With the fold on both sides the *storage* form stops mattering for lookup:
`ClO3-` and `ClO₃⁻` fold to the same thing, so a term saved by either product
is findable by the other.

## 3. The word-character sets differ, and that is allowed

| | Trados | memoQ |
|---|---|---|
| letters, digits, underscore | word | word |
| the fold's **digits** (once folded, ordinary digits) | word | word |
| the fold's **signs and dot** `⁺ ⁻ ₊ ₋ · ∙ ⋅ •` | **word** | not word |
| `. , % & ' * + / -` | **word** | not word |

The third row is the one section 4 turns on: because a charge is not a word
character in memoQ, it falls outside the token there and memoQ's exact lookups
already succeed. Do not "fix" that row to make the products agree — doing so
would reintroduce in memoQ the regression section 4 describes.

Trados's wider set is load-bearing for Dutch legal text — `verkoper(s)`,
`kandidaat-koper`, decimals, percentages — and carries bracket-alias machinery
on top of it. memoQ's narrower set is the more conservative default. Neither is
going to adopt the other's wholesale, and pretending otherwise would mean one
product changing its tokenisation to suit the other's history.

## 4. The rule that makes different sets agree

> A lookup that fails on the exact token is retried with **unambiguous trailing
> characters** trimmed.

Unambiguous means: a charge or a radical dot — after folding, `+`, `-`, `·` —
which never occur word-internally in a formula. Trimmed only from the **end**,
and only after the exact and punctuation-stripped lookups have failed.

This is not a workaround for a wide character set. It is what lets two products
with different sets agree on what a term means: whichever side treats a trailing
character as word-internal, both end up finding the same entry.

Trados needs the retry because its wider set swallows the charge into the token.
memoQ does not need it today, because the charge falls outside its tokens and
its exact lookups already succeed; it adopts the retry when it adopts anything
wider.

**Measured behaviour, both products, same termbase:**

| stored term | segment | matches |
|---|---|---|
| `MnO₄⁻` | `MnO₄⁻` | yes |
| `MnO₄` | `MnO₄` | yes |
| `MnO₄` | `MnO₄⁻` | yes — via the retry in Trados, directly in memoQ |
| `OH` | `OH∙` | yes |
| `ClO3` | `ClO3-` | yes |
| `MnO₄⁻` | `MnO₄` | **no** — see below |

## 5. Two deliberate asymmetries

**The retry runs in one direction only.** A term stored *with* its charge does
not match a segment that writes the ion bare. Trimming the segment's token is
reading what is there; padding a stored term with a charge the document does not
show would be inventing chemistry. The commoner real case is the first
direction anyway: terms saved before 18.20.192, and terms saved by memoQ, are
spelled without the charge, while documents write it.

**An exact match always wins.** When the termbase holds both `ClO3` and `ClO3-`,
a charged segment gets the charged entry and a bare segment gets the bare one;
the retry only fires when there is no exact entry to prefer. Tested in
`.dev/charge-suffix-test.ps1`.

## 6. The open question: `ClO3` inside `ClO3-`

Chlorate and the chlorate ion are different species, so a row that matches one
against the other is arguably wrong — and it is a match in both products, by
different routes. Position, not yet ratified:

**Tolerate it, because of what the panel is for.** TermLens displays; it does
not correct. Showing the chlorate entry against `ClO3-` tells the translator a
related term exists and lets them judge. The fallback only fires when the
termbase has no exact entry, so the alternative is showing nothing at all.

The cost is real and worth stating: Alt+digit inserts the stored target, so a
translator inserting from a near-match gets the bare term's translation where
the ion was meant. That is an argument for making the chip visibly distinguish a
retry match from an exact one, not for removing the retry.

**The cost is Trados-only today.** memoQ has no insert-by-number shortcut, so a
near-match there is information on screen and nothing more. The case for
tolerating the row is therefore stronger on the memoQ side than on this one, and
whatever is decided should say so rather than describe the trade-off as shared.

To settle after the submission, with this row as the worked example.

## 7. The write path is a contract too

Matching is only half of it: both products write into the same table, so a term
one product stores badly is dead for both. Trados sanitises every term and
synonym write through `TermbaseReader.SanitizeTermWhitespace`, which:

- folds tabs and every space variant from 2b to a plain space;
- **removes zero-width characters entirely** — U+200B zero-width space, U+2060
  word joiner, U+FEFF byte-order mark;
- collapses runs of spaces, and trims.

The zero-width removal is the load-bearing part, and it exists only here: those
characters are not in the fold, so a term stored with one **never matches in
either product**, invisibly. IDML-derived segments are the common source, which
both tools handle. Whatever writes to this termbase needs the equivalent.

## 8. Changing any of this

The fold table, the retry rule and the write-path sanitiser are cross-product
contracts. Change any of them in one product only in the same session as the
other, the way the termbase full-text index triggers were agreed. Tests to keep
green:

- Trados: `.dev/script-chars-test.ps1`, `.dev/charge-suffix-test.ps1`
- memoQ: `tools/scriptchars-test.ps1` (fold and matching, one file)
