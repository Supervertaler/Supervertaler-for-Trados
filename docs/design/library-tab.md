# The Library tab

**Status:** complete. Steps 1-6 built and verified in Studio (2026-08-21).
Step 6 shipped deliberately *without* the Re-analyse button: nothing consumes the
folder yet, so the row records the folder and says so on its face. The analysis
pass that would give it meaning is `reference-images.md` and issue #69.
**Date:** 2026-08-16
**Supersedes:** `supermemory-settings-tab.md` (same day), which proposed a
separate SuperMemory tab and argued against merging. The reversal is recorded
below rather than deleted, because the reasoning that changed is the useful part.

**Prompted by:** needing somewhere to put the reference-images folder button,
which turned into a better question: why is the memory bank the one thing you
cannot see from inside the plugin?

## The shape

Rename the **Prompts** tab to **Library**, and let it hold everything the user
authors that drives the AI:

```
Library
├── System Prompt            the foundation every custom prompt is added onto
├── AutoTagger               the prompt behind the AutoTagger
├── Custom prompts
│   ├── Proofread
│   ├── QuickLauncher
│   └── Translate
└── SuperMemory
    ├── _shared              loaded alongside the active bank, always
    ├── acme-proj-001    (active)
    │   ├── brief.md
    │   ├── terminology.md
    │   ├── style.md
    │   ├── figures.md       optional; generated from reference drawings
    │   └── reference/       audit trail — never read into a prompt
    └── …
```

**Foundation, then tasks, then knowledge.** The order is the argument: what is
always in play, then what you choose between, then what is true. A user never
has to be taught those three categories — the tree reads correctly without them
being named.

## Why this, having first argued the opposite

The earlier note rejected merging, on two grounds. Both were aimed at a weaker
proposal than this one, and one was simply wrong.

**"It buries the brand."** That objection assumed SuperMemory folded *inside* a
generic tab. Here it is a top-level node carrying its own name, a peer of Custom
prompts. The objection does not survive.

**"Two actives in one panel."** Real, but weaker than claimed: the active prompt
and the active bank sit in separate subtrees under different parents, not in one
flat list. Worth designing for, not a reason to split the tab.

**The mistake.** The earlier note argued prompts and banks answer different
questions — *what do I tell the AI* versus *what does the AI know* — and
concluded they belong apart. The premise is right and the conclusion does not
follow, because it describes **point of use**, and this tab is not point of use.
You do not pick a prompt here (that is Batch Operations, or the QuickLauncher
menu); you do not switch banks here either (that is the SuperMemory toolbar).
This tab is where you **curate**. Curating prompts and curating banks is one
activity — maintaining what the AI knows and how it behaves — and one activity
wants one place.

What survives from the earlier note is the taxonomy itself: configuration, task
instructions, knowledge. It stops being a structure to impose and becomes what
the tree ordering already expresses.

## The central design problem: the toolbar

Not the tree. The tree is easy.

The Prompts tab already carries New, Edit, Delete, Restore, New Folder, Refresh
and reorder arrows. Banks add Rename, Delete, Set active and Open folder.
That is a dozen buttons, most of them meaningless for whatever is selected —
"Restore" against a memory bank, "Set active" against the AutoTagger prompt.

So the toolbar must be **context-sensitive**: it reflects the kind of the
selected node. Roughly:

| Selected | Actions |
|---|---|
| System Prompt / AutoTagger | Edit, Restore default |
| A prompt folder | New prompt, New folder, Rename, Delete |
| A prompt | Edit, Delete, Set active, Assign QuickLauncher slot, reorder |
| SuperMemory root | New bank |
| A bank | Set active, Rename, Delete, Open folder, Add figures from drawings… |
| A bank file | Edit |
| `reference/` | Open folder |

Get this wrong and the tab is unusable however good the tree is. It is the bulk
of the work in this change.

## Where the reference-images feature fits

Images are neither foundation, task, nor knowledge — they are **source
material**. What comes *out* of them is knowledge.

So:

- **`figures.md` is a bank file** and appears in the tree exactly like
  `brief.md`. Nothing special.
- **The images folder is provenance**, and belongs with the artifact rather than
  in a settings row somewhere. Selecting `figures.md` shows its content with its
  origin above it — *Generated from: …\PROJ-001\Images* — plus **Browse…**
  and **Re-analyse**.
- **When a bank has no `figures.md`**, selecting the bank offers **Add figures
  from drawings…**, which asks for the folder and runs the analysis.

That makes the relationship visible instead of documented: you can see that this
file came from those pictures, and change the pictures it came from.

`ReferenceImages.Suggest()` supplies the browse dialog's starting folder. It is
never applied on its own — a folder guessed by walking up the tree can belong to
a different job, and drawings from the wrong matter are worse than none, because
the output still reads plausibly.

The folder itself stays in `ProjectSettings` (per project), for reasons in the
next section, even though it is surfaced here next to a bank.

## Resolved: per project

Decided per project and shipped that way; `ProjectSettings.ReferenceImagesFolder`
is what the Browse button writes. The reasoning:

**Per project** — what `ProjectSettings.ReferenceImagesFolder` does today — is
always correct, because drawings belong to a job. It also handles sibling BE/EP
filings sharing one drawings folder: both projects point at it.

**Per bank** would be simpler here, and survives the Studio project being deleted
and recreated — but only if a bank maps 1:1 to a filing.

The observed banks disagree with each other. `acme-proj-001` and
`acme-proj-002` are per-filing. `client-b_machines`, `client-c_ops`
and `client-d` read as per-client and would span many jobs with different drawings;
per-bank would attach one job's figures to all of them.

Per project is the safer default: never wrong, only sometimes repetitive.

If per-bank ever earns its place, it belongs as a *fallback* under the per-project
setting, not as a replacement for it.

## What the tree must say that nothing currently does

- **Which bank is active**, and that `_shared` is loaded *alongside* it rather
  than being an alternative to it.
- **Which files are read into prompts.** `reference/` deliberately is not — it is
  the audit trail, so a derived claim can be checked against its source. A user
  who does not know that will keep putting things there and wonder why the AI
  ignores them.
- **Which prompt is active**, without the display and the tick disagreeing.
  Defect B (2026-08-16) was exactly that failure in the Batch Operations
  dropdown.

## Build order

1. **Extract the tree-plus-preview control** from `PromptManagerPanel`.
   Behaviour-preserving: the Prompts tab must look and act identically
   afterwards.
2. **Context-sensitive toolbar** over that control, still prompts-only. The
   riskiest visible change, done while there is only one kind of node to get
   right.
3. **Rename the tab to Library, add the SuperMemory subtree, read-only.** Tree,
   content preview, active-bank marking, the `reference/` distinction.
   Shippable and useful alone.
4. **Editing** bank files in the detail pane.
5. **Rename and delete banks.** Closes the gap `CLAUDE.md` records, where the
   workaround is renaming folders by hand with Trados closed.
6. **The reference-images row on `figures.md`**, with Browse and Re-analyse.
   Unblocks `HANDOFF-image-context.md` step 1.

Steps 1 and 2 touch a working 1,864-line control that had a defect this morning.
Do them separately and check the Prompts tab against current behaviour before
going near SuperMemory.

## Risks

**Extraction regressions.** Step 1's whole risk. The Prompts tab is
load-bearing: the QuickLauncher menu, Batch Operations and active-prompt
selection all read from it.

**Editing files the AI reads.** The detail pane writes files that go straight
into prompts. A save that corrupts `terminology.md` degrades every subsequent
translation quietly. Straight text editing, no clever parsing on save.

**Two editors, one file.** Banks are shared with Obsidian and the Python
assistant, and the plugin does not lock them. Re-read on focus; never trust the
buffer.

**A bigger tab is a slower tab.** Prompts loads a folder tree today. Adding
every bank and its files means more I/O on open. Load bank contents lazily, on
node expansion.
