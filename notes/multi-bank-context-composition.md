# Memory banks and the context composition question

Captured 2026-04-09 after shipping the multi-bank UI (Steps 4, 5, 5i).

This is an **internal design memo**, not user-facing documentation. It lives
in `notes/` specifically so it is not published to GitBook. Its purpose is to
record two related observations that came up during the Step 6 docs refresh
and that deserve deliberate thought later rather than being papered over
with a quick edit.

---

## 1. Memory banks are not a replacement for TMs and termbases

For most of the memory bank rollout, the launch copy has said something like
"a self-organising knowledge base that **replaces** traditional translation
memories and term bases". That framing made sense as a marketing hook, but
it is wrong as a design statement, and it pushes users towards a false
binary.

The truth is closer to this:

- **Translation memories** give the AI fast, exact-or-fuzzy access to
  previous translations of similar segments. They are the tightest
  anchoring signal available and they are irreplaceable when the source
  text overlaps with anything the translator has touched before.
- **Termbases** give the AI dense, exhaustive term pairs with metadata. For
  glossary-heavy work (pharma, legal, EU, patents) they are still the most
  precise way to force the AI to use a specific wording for a specific
  concept.
- **Memory banks** give the AI the *reasoning* behind decisions: why a term
  was picked, what alternatives were rejected, what a client prefers, what
  the domain's common pitfalls are, how the style guide wants things
  phrased. They are the richest source but also the most expensive in
  tokens and the least deterministic at retrieval time.

These are complementary, not competitive. A well-built memory bank layered
on top of a good TM and termbase is probably the strongest context stack
Supervertaler can offer – but we have not actually tested that hypothesis.

## 2. The open question: is stacking all sources additive or noisy?

This is the one we need to test before we give users guidance on composition.
The suspicion is that for some client–project combinations, layering
everything at once may:

- **Blow the token budget** – if the memory bank already has a detailed
  client profile, a domain article, a style guide, and fifty terminology
  articles, there may not be room left for TM fuzzy matches, let alone
  full document content.
- **Introduce contradictions** – if the termbase says "X → Y" but a memory
  bank terminology article says "X → Y (but for client Acme use Z instead)"
  and the TM has a previous translation using Z, the AI sees three voices
  saying three adjacent things. Does it reliably pick the most specific
  one? Unclear.
- **Dilute the signal** – with a mature memory bank for a specific client,
  the termbase and TM may just be noise – the memory bank already knows
  the answer and says it better. Adding the other two might degrade output
  rather than improve it.

On the other hand, for unfamiliar domains or one-off projects where the
memory bank is empty or only has a thin default bank, TMs and termbases are
still the backbone of good AI output. The right composition almost
certainly depends on how mature the active memory bank is for the current
work.

### Testing plan (rough, not yet executed)

Pick 2–3 real client projects where a mature memory bank exists. For each:

1. Run a batch translation with **memory bank only** (TM matches off,
   termbase metadata off).
2. Run it again with **memory bank + termbase**.
3. Run it again with **memory bank + TM matches**.
4. Run it again with **everything on**.
5. Run it once more with **TM + termbase only, memory bank off** – the
   baseline of what users had before Memory Banks existed.

Score each run for:

- Terminology consistency with the client profile
- Style adherence to the documented style guide
- Raw translation quality
- Token cost per segment

If the pattern holds that "memory bank + TM alone" beats "everything
stacked", we should ship a **context composition profile** feature: the
user picks a preset ("Mature client – memory bank only", "Unfamiliar
domain – TM + termbase + default bank", "Everything on – maximum context")
and the toggles move in lockstep.

Until we have data, any guidance in the user docs on this subject should
be phrased as a soft suggestion, not a recommendation.

## 3. The IA hazard: overlapping functionality, different viewing angles

A second observation from the same conversation: writing help documentation
for Supervertaler is genuinely hard because the same information can be
framed several equally valid ways, and each framing pulls for a different
page structure.

Memory banks are the canonical example. They are simultaneously:

- **A noun** – "a memory bank is a folder with six sub-folders of
  Markdown articles". This framing wants a "Memory banks" page.
- **A context source** – "the AI loads the active bank's client profile,
  domain article, and style guide before every translation". This framing
  wants a section on the "Context awareness" page.
- **A workflow** – "drop files into 00_INBOX, click Process Inbox, the AI
  organises them into structured articles". This framing wants a
  procedural "How to use memory banks" page.
- **A feature** – one of the dozen things Supervertaler for Trados does.
  This framing wants a bullet on the README and the landing page.

Before Step 6, the docs tried to do all four on the **same page**, and the
result was contradictory messaging. Step 6 split them using the principle
**one canonical home per concept, everything else links to it**:

- `context-awareness.md` is the "here is the full menu of what the AI
  sees" page – every context source is a section there, including memory
  banks, so the "not a replacement" framing becomes structurally
  unavoidable.
- `memory-banks.md` is the pure noun page – what they are, how to create
  and switch them, the folder layout, how they sync with the Python
  assistant. No AI loading details.
- `memory-banks/ai-integration.md` is the power-user deep dive on the
  loading algorithm – referenced from the other two, doesn't repeat them.

This principle is the one we should apply every time we add a new feature
that can be framed in multiple ways. The IA cost of **not** applying it is
that readers land on whichever page they happen to find first and walk
away with a different mental model than someone who landed on a different
page.

## 4. What to do with this memo

Nothing urgent. Revisit after:

- Testing the stacking hypothesis (section 2) with real projects.
- Shipping the fuller bank management UI in Settings (rename, delete) and
  deciding whether a "context composition profile" feature belongs next
  to it.
- Considering whether the Python Supervertaler Assistant should share the
  same composition preset system, since banks are already shared between
  products.

If this memo grows substantially, promote it to a proper design doc under
`docs/design/` (orphan from SUMMARY.md so GitBook doesn't publish it) or to
a public design note in the main Supervertaler repo.
