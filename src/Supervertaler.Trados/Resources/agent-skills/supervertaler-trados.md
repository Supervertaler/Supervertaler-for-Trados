---
name: supervertaler-trados
description: Work with the Trados Studio project the user currently has open, through the Supervertaler MCP server - reading and writing segments, terminology, translation memories, comments, and QA verification. Use for any question about the active Trados project, its segments, terms, TM matches or status counts, and for any request to translate, proofread, review, check, QA, edit, confirm, or comment on segments in Studio. ALWAYS use this skill when asked to proofread, check, review or find errors in a Trados project, or to look for mistakes the translator has missed - it defines the required procedure for that task. Also use when the user mentions Trados, Studio, sdlxliff, a termbase, or a translation memory.
---

# Supervertaler for Trados

The user has Trados Studio open with a live translation project. The
`supervertaler` MCP server talks to that running Studio over a local bridge and
is the ONLY correct way to read or change its state.

## FIRST: confirm the tools are there

Try `get_active_project`. If it is unavailable, STOP and tell the user the
Supervertaler MCP server is not connected - do not fall back to another method.

## Never read Trados state from the shell or the filesystem

This is the most common and most damaging mistake. Studio holds the document in
memory; what is on disk is stale or irrelevant.

- `projects.xml` lists projects that exist, NOT the one currently open.
- The `.sdlxliff` on disk is the last SAVED state, not what is in the editor.
- Process lists and window titles tell you nothing reliable about the project.

An agent that goes looking through `Documents\Studio 2026 Release\` is answering
a different question from the one it was asked, slowly and wrongly.
`get_active_project` answers it in one call.

Do not use PowerShell, file reads, or directory listings to determine anything
about a Trados project.

**The same goes for analysing it.** Do not write scripts to check, count,
compare or audit a project's content. The tools below already do that work
against the live document, the real termbase and Studio's own rules, and your
own reading does the rest. Writing code here feels productive and is not: on a
measured run it produced 666 findings of which 6 of the 7 survivors were wrong,
while the defects that mattered — a claim inconsistent with its sibling, a
sentence contradicting the one above it — were still sitting in the text
unread. **Call the tools, then read.**

## Choosing a tool

| The user asks about | Call |
|---|---|
| which project/file is open, languages, segment counts | `get_active_project` |
| the segments themselves | `get_segments` (use its filters, do not fetch everything) |
| the segment they are sitting on | `get_active_segment` |
| a term | `lookup_term`, `check_terminology` |
| what the TM says for a sentence | `search_studio_tm` |
| progress, how much is left | `get_project_statistics`, `get_coverage` |
| tags, numbers, spacing, consistency | `check_tags`, `check_numbers`, `check_nbsp`, `find_inconsistencies` |
| Studio's own QA | `run_verification` |
| what was changed | `get_tracked_changes` |
| client and domain knowledge | `get_supermemory_context`, `search_supermemory` |

If more than one Studio is running, call `list_trados_instances` first and ask
the user which project they mean, then `select_trados_instance`.

## Proofreading and QA

**This is a five-step procedure. Follow it in order. Do not improvise a
different one, and do not skip step 3 — it is where nearly all the value is.**

1. Load the decisions
2. Run the checking tools
3. Read the bilingual text
4. Verify every candidate finding
5. Report

### 1. Load the decisions

Before reporting anything, call `get_supermemory_context` and consult the
project termbase (`lookup_term`, `check_terminology`). The translator has
already settled many questions, with the reasoning written down — a deliberate
exception to a term, a gloss dropped on purpose, a rendering chosen against the
obvious one.

**A finding that contradicts a recorded decision is not a finding.** Reporting
one wastes the translator's time and asks them to re-defend a call they already
made. Termbase notes in particular carry exceptions that the headline term
pair does not: one entry on a real project names a specific segment number and
says outright that a terminology flag there is a false positive. An agent that
read only the term pair raised exactly that flag.

If a recorded decision looks genuinely wrong, say so once, citing the note —
do not present it as a fresh defect.

### 2. Run the checking tools

**Do not write or run a script during a proofread.** This is a rule, not a
preference. If you believe a script is genuinely needed, stop and ask the user
first, saying what you would check and why no tool covers it. Do not write one
and then mention it afterwards.

| Job | Tool |
|---|---|
| terminology against the user's termbase | `check_terminology` |
| numbers, dates, measurements | `check_numbers` |
| inline tags | `check_tags` |
| non-breaking spaces | `check_nbsp` |
| the same source translated two ways | `find_inconsistencies` |
| Studio's own QA rules | `run_verification` |
| the text itself | `get_segments`, then READ it |

These are termbase-aware and Trados-aware. Hand-rolled regex is not, and it
produces noise at a rate that buries anything real. A measured run on a
675-segment patent: a script produced 666 raw findings, filtered itself to 7,
and 6 of those 7 were wrong — including two "termbase violations" where the
target followed the termbase correctly and the script had matched a substring
of a different, also-correct term. `check_terminology` would have raised none
of them, because it compares against the actual termbase entries.

Steps 1 and 2 together are about six tool calls and a few minutes. If you have
spent longer than that before reaching step 3, you have gone wrong.

### 3. Read the bilingual text

**This is the step that finds things, and it is the one an agent with a shell
is most tempted to skip.** There is no shortcut: call `get_segments` in batches
(use `limit`/`offset`) and read the source against the target.

Pattern matching cannot reach any of the following. Reading finds all of them:

- **A paragraph that repeats an earlier one almost word for word, but ends
  differently.** Two parallel embodiment paragraphs where one measures a
  percentage against "the material" and the other against "the fibre material"
  is a substantive drafting slip, and it is invisible to every check.
- **A claim that does not match its sibling claim**, or the description
  sentence that supports it. Claim sets must be read as a set.
- **A sentence that contradicts the one before it** — a step that heats a
  liquid so it can be sprayed, followed by a sentence saying this makes it more
  viscous.
- **Arithmetic.** Figures quoted in the prose against the table they come from;
  percentages and differences that are supposed to add up.
- **Cross-references.** "according to claim N", "steps a to f", figure numbers.
- **A term that drifts across a document**, or a formula rendered two ways in
  otherwise identical positions.
- **Register and idiom** — whether the English reads as English.

You cannot do this by sampling. Read all of it.

### 4. Verify every candidate finding

Before reporting anything, re-read the segment it came from and check it is
real. Tool output and first impressions both produce false positives, and a
wrong finding is worse than a missed one: it may become a comment sent to a
client over the translator's name. Discard anything you cannot still defend
after a second look, and anything a recorded decision already settles.

### 5. Report

Report findings by the segment `number` the user sees in Studio, quote the
text, and say what is wrong in one line. Separate what is genuinely wrong from
what is merely worth a look. Say plainly if you found nothing — that is a
useful result, not a failure.

If asked not to change anything, change nothing — including confirmation
statuses, and including comments.

## Reading is free; writing is not

These change the user's work and must never be called without them asking for
that specific change:

`update_segments` `find_and_replace` `pretranslate` `insert_into_active_segment`
`mark_reviewed` `save_document` `export_target` `update_tm` `add_comment`
`update_comment` `delete_comment` `add_term` `update_term` `delete_term`
`import_project_termbase` `save_prompt`

Everything else only reads. Prefer reading, and show the user what you found
before proposing a change.

## Writing segments safely

- **Address segments by the ids from `get_segments`.** Cite the `number` the
  user sees in Studio's grid when talking to them - never invent a number, and
  in merged documents give the file name too, because numbers restart per file.
- **Pass `fp` back.** Each segment from `get_segments` carries a short
  fingerprint of its source. Send it on the matching `update_segments` item so
  the write is checked against the segment you actually read. If the source has
  moved underneath you, the write is refused instead of landing on the wrong
  row.
- **Copy inline tags from the SOURCE field**, not from the existing target -
  Studio verifies the target's tags against the source. Use each marker
  (`<t1>...</t1>`, `<t2/>`, `<b>...</b>`) at most once, and never invent a
  number the source does not have.
- A target written without an explicit status becomes **Draft**, so the user can
  review it. Leave it that way unless they ask for Translated.

## Two that behave unusually

- **`pretranslate`** runs in the BACKGROUND and returns a `jobId` immediately -
  poll `get_task_status`. It writes from the LAST SAVED state, so the document
  must be saved first (with the user's OK), and they may need to close and
  reopen it to see the results.
- **`export_target`** and **`save_document`** touch files on disk. Ask first.

## Never write into the project folder

The project's `Studio\` subfolder holds the `.sdlxliff` files Studio currently
has open. Never create, edit, move, or delete anything under it, and never
"fix" a translation by editing a file there. Segment changes go through
`update_segments` and nowhere else. The same goes for any `.sdlproj`, `.sdltm`
or `.sdltb` in the project.

Reading files elsewhere in the project folder - the source Word document, a PDF,
a figures folder, reference material - is fine and often useful.

## memoQ

memoQ is served by a DIFFERENT MCP server with a different, smaller tool set
(`stage_translations`, `get_staged`, `get_confirmed_pairs`, `clear_staged`).
Nothing in this skill applies to it. If the user is working in memoQ and only
the Trados server is connected, say so rather than improvising.
