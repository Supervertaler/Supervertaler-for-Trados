# A SuperMemory tab in Settings

**Status: SUPERSEDED by `library-tab.md` (2026-08-16, same day).**

This note proposed a separate SuperMemory tab and argued against merging it into
the Prompts tab. That argument was wrong in a specific way worth keeping: it
reasoned about how prompts and banks are used at POINT OF USE, where they really
do answer different questions, and concluded they belong on different tabs. But
a Settings tab is not point of use — nobody picks a prompt or switches a bank
here. It is where you CURATE, and curating both is one activity.

The successor keeps the taxonomy this note worked out (configuration, task
instructions, knowledge) and expresses it as tree ordering rather than as
separate tabs: foundation, then tasks, then knowledge.

Kept because the reasoning trail is the useful part, and because the objections
raised here — two "actives" in one panel, and burying the SuperMemory name —
are still things the Library tab has to handle, even though neither justified a
separate tab.

---

## The gap

A memory bank is a folder of Markdown files. Today the only ways to look at one
are Explorer, Obsidian, or the **Open folder** button on the SuperMemory
toolbar, which just launches Explorer. Everything else about the bank is
inferred: the toolbar dropdown lists bank *names*, the Report button summarises
sizes and token counts, and the chat mentions what it loaded.

That is a legibility problem, and most of today's SuperMemory work was the same
shape:

- files in a bank were silently inert, because the reader used an allow-list
  (defect E)
- `get_supermemory_context` reported the bank it *used*, which read exactly like
  the bank you *asked for* (defect C)
- content was trimmed to fit a budget with nothing saying so
- Quick Add wrote to "the active bank", named only in the confirmation

Each was fixed at the source. But a user who could *see* the bank would have
caught every one of them in seconds, which is the argument for this tab beyond
any single feature.

It also closes a gap already recorded in `CLAUDE.md`: renaming and deleting
banks is listed as not implemented, with the workaround being "rename or delete
the folder under `memory-banks/` directly with Trados closed".

## Should this live in the Prompts tab instead?

**No.** Considered and rejected, along with renaming that tab to something like
"Library".

The two look alike — both are trees of Markdown files the user edits by hand —
and the temptation is to build the tree once. But they answer different
questions:

| | Prompts | Memory banks |
|---|---|---|
| Question | what instructions do I give the AI | what does the AI know about this client |
| Chosen | per run | per client, changes rarely |
| Lifecycle | one personal library across all jobs | one per client or filing, grows during a job |
| Shape | arbitrary nesting, categories in frontmatter | fixed skeleton + `reference/` |
| Operations | new, edit, delete, restore defaults, set active, assign QuickLauncher slot, reorder | new, rename, delete, open folder, switch active, harvest, report |

Only "edit this file" is common to both.

Two specific hazards in merging:

**Two actives in one panel.** Both have an "active" concept marked in the tree.
Defect B (2026-08-16) was a user unable to tell which prompt was active because
one control gave two answers. Adding a second, different "active" to the same
surface invites the same confusion.

**It buries the brand.** SuperMemory is the name used in chat banners, the
Reports tab, the help menu and the marketing. Making it a subfolder of a generic
"Library" tab demotes the product's most distinctive idea.

**What the instinct is right about** is the duplication. `PromptManagerPanel` is
1,864 lines, and writing a second one would be waste. The answer is to extract
the tree-plus-preview pattern as a shared control that both tabs host — same
interaction, same code, each tab still about one thing.

## Scope

### The tree

Two levels, which is simpler than the prompt library's arbitrary nesting:

```
_shared                    (always loaded underneath the active bank)
  brief.md
  terminology.md
  style.md
acme-proj-001      (active - marked)
  brief.md
  terminology.md
  style.md
  figures.md               (optional; written by the reference-images feature)
  reference/               (audit trail - never read into a prompt)
    …
other-bank
  …
```

Two things the tree should say that nothing currently does:

- **which bank is active**, and that `_shared` is loaded alongside it rather
  than being an alternative to it
- **which files are read into prompts.** `reference/` is deliberately not, and a
  user who does not know that will keep putting things there and wondering why
  the AI ignores them. Show it greyed, or under a heading that says so.

### The detail pane

Content of the selected file, editable, saved on leaving the node or on an
explicit Save. Read-only would be the cheaper first cut and still worth
shipping — seeing the bank is most of the value — but hand-editing is how these
files are meant to be maintained, so editing is the target.

### Actions

| Action | Notes |
|---|---|
| New bank | exists already on the toolbar; reuse `UserDataPath.TryCreateMemoryBank` |
| Rename bank | **new** — currently requires closing Trados and renaming the folder |
| Delete bank | **new** — same. Needs a confirm naming the bank, and must refuse the active one or switch away first |
| Open folder | exists; keep, for Obsidian users |
| Refresh | re-read from disk; the files are edited outside the plugin |
| Set active | duplicates the toolbar dropdown, but is the obvious gesture here |

### The reference-images row

A **Reference images** row with a Browse button, which is what prompted this
note.

`ReferenceImages.Suggest()` supplies the browse dialog's starting folder; it
never applies one on its own, because a folder guessed by walking up the tree can
belong to a different job (see that class's remarks).

**Scoped to the project, not the bank, and labelled to say so** — e.g.
*"Reference images for Acme (PROJ-001)"*. This is the one place the tab's
bank-centric framing and the setting disagree, and the label has to carry the
difference.

## The open question: per project or per bank?

Genuinely unresolved, and worth deciding before the browse button is wired.

**Per project** (what `ProjectSettings.ReferenceImagesFolder` does today) is
always correct, because drawings belong to a job.

**Per bank** would be simpler in this UI and survives the Studio project being
deleted and recreated — and it is right *if* a bank maps 1:1 to a filing.

The observed banks do not agree with each other. `acme-proj-001` and
`acme-proj-002` are per-filing, so per-bank would be right for them.
`client-b_machines`, `client-c_ops` and `client-d` read as per-client, and would
span many jobs with different drawings — per-bank would then attach one job's
figures to all of them.

So per project is the safer default: it is never wrong, only sometimes
repetitive. Revisit if it proves annoying in practice.

Related, from the handoff and still open: sibling BE/EP filings share drawings.
Per-project handles that naturally — both projects point at one folder — which is
another point in its favour.

## What belongs where — a working hypothesis

**Not settled.** Recorded because it is the lens that decides placement
questions, and because writing it down is the only way to find out whether it
survives contact with real use. Argue with it rather than inheriting it.

The Prompts tab currently holds three things: the **System Prompt**, the
**AutoTagger Instruction**, and the user's **custom prompts**. Asked whether any
of them belong with SuperMemory, the useful move is to sort by *what the text
is* rather than by what format it happens to be in:

| | What it is | Who authors it | When it changes |
|---|---|---|---|
| System Prompt | how the assistant behaves | ships with a default | almost never |
| AutoTagger Instruction | how one feature behaves | ships with a default | almost never |
| Custom prompts | what task to perform | the user | chosen per run |
| SuperMemory | what is true about this client | the user | grows during a job |

That is three kinds, not two: **configuration**, **task instructions**, and
**knowledge**.

### What follows

**None of the three belongs with SuperMemory.** All are instructions — *how* or
*what to do*. SuperMemory is knowledge — *what is true*. The distinction holds up
operationally: you choose a prompt, you never choose a brief.

**The misfit is elsewhere.** System Prompt and AutoTagger Instruction do not
belong with custom prompts either. They are product configuration, shipping with
defaults and edited about once a year, sitting inside a library of content the
user authors and picks between. AI Settings, next to the other "how the tool
behaves" controls, fits them better. Worth considering on its own merits, not as
part of this tab.

**Prompts and banks are already entangled, and the fix runs the other way.** The
production prompt for Acme (PROJ-001) contains a long *"STRUCTURAL FACTS
DERIVED FROM THE DRAWINGS"* section — numeral disambiguations, why *schot* and
*scheiding* must not collapse into one English word, what `(15)` is. That is
knowledge about a filing living inside a task instruction.
`HANDOFF-image-context.md` says as much: the prompt is *"the current delivery
mechanism"* and *"the point of this feature is that it should stop being
necessary to embed it."*

So the direction of travel is moving content **out of prompts and into banks**,
until a prompt says "translate this the way you have been told" and the bank
says what you have been told. The reference-images feature is the first step of
that migration, not a side quest.

### Where it is weak

**Users write prompts that are mostly knowledge.** A prompt named "Client X
house style" is knowledge wearing a prompt's clothes, and nothing stops it. The
taxonomy describes where things *should* sit, not where they *do* — so it is a
guide for new surfaces, not a licence to move a user's existing files.

**The style boundary is genuinely blurry.** `style.md` is knowledge and a
proofreading prompt is an instruction, but both encode "how to write". Which one
should hold "never use the Oxford comma for this client" is not obvious, and the
honest answer may be that it depends on whether it applies to one task or to all
of them.

**Three categories may be one too many for a UI.** Configuration and task
instructions are distinct in principle, but a user looking for "the AI's
instructions" may not care which is which. Splitting them across two tabs could
cost more than the tidiness is worth.

## Build order

1. **Extract the shared tree-plus-preview control** from `PromptManagerPanel`.
   Behaviour-preserving; the Prompts tab must look and act identically after it.
2. **SuperMemory tab, read-only.** Tree, content preview, active-bank marking,
   the `reference/` distinction. Shippable and useful alone.
3. **Editing** in the detail pane.
4. **Rename and delete banks.** Closes the `CLAUDE.md` gap.
5. **The reference-images row.** Unblocks `HANDOFF-image-context.md` step 1.

Step 1 is the risky one, because it touches a working 1,864-line control that
had a defect this morning. Do it on its own, and check the Prompts tab against
its current behaviour before moving on.

Steps 2 and 5 are what the images feature needs; 3 and 4 are independently
valuable and can wait.

## Risks

**Editing files the AI reads.** The detail pane writes files that go straight
into prompts. A save that corrupts `terminology.md` degrades every subsequent
translation quietly. Straight text editing, no clever parsing on save.

**Two editors, one file.** These banks are shared with Obsidian and the Python
assistant, and the plugin does not lock them. Re-read on focus, and do not
assume the buffer is current.

**Extraction regressions.** Step 1's whole risk. The Prompts tab is
load-bearing — the QuickLauncher menu, Batch Operations and the active-prompt
selection all read from it.
