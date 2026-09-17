# Reference images: giving the AI what the pictures say

**Status:** design, not started. Supersedes the approach in
`HANDOFF-image-context.md` (2026-08-16) — that note's reasoning still holds for
patents, but its central assumption does not generalise. See "What changed".

**Date:** 2026-08-21

## The problem

A translator's source document contains pictures. The pictures carry
information the text does not, and sometimes that information decides a
translation. The question is how to get it to the model.

Two implementations exist, in two products, and **both assume the image has a
name that the text uses**:

| | What it does | Where |
|---|---|---|
| Reference-triggered attachment | when a segment says *"figure 3"*, attach `FIG. 3.png` to that request | Workbench, `figure_context_manager.py` |
| Numeral distillation | run vision once over the drawings, cache a numeral→part register as `figures.md`, inject the text | proposed in `HANDOFF-image-context.md`; not built anywhere |

For patents both work, because a patent is the ideal case: every figure is
numbered, every numeral is cited, and the citation is the link.

**For a document whose images have no names, both fail — not degrade, fail.**
A user manual with two unlabelled photos on page 3 offers nothing to trigger on
and nothing to put in a register. The feature is simply absent, and nothing says
so.

## The insight

There are three ways an image is linked to the text, and **only the first needs
a name**:

1. **Citation** — the text names it. *"as shown in figure 3"*. Patents, papers.
2. **Caption** — text attached to it, which is itself a segment being translated.
   Manuals, reports.
3. **Proximity** — it sits between these paragraphs, and nothing refers to it at
   all. Everything else.

### Corrected against a real job, then corrected again (2026-08-24)

The note originally said the anchor *always* exists. One real job disproved
that. Acme (PROJ-001):

| File | Paragraphs | Images | Text runs |
|---|---|---|---|
| `Application as filed.docx` | 303 | **0** | many |
| `Figures as filed.docx` | 21 | **5** | **0** |

The drawings arrived as a separate document with not one `w:t` run in the whole
part; the figure numbers are drawn *inside* the pictures. Citation, caption and
proximity all fail together.

**But the first conclusion drawn from that was wrong too.** It said "folder mode
is the patent path". That is one client's habit on one job — in Michael's words,
*"only something they do sometimes"*. A patent just as often arrives as a single
DOCX with the drawings inline. One sample is not a rule, and neither shape
should be privileged.

The `Images/` folder of neatly named `Fig. 1.png` … `Fig. 2D.png` beside that job
is **not a workflow to design around either.** The translator opened the
drawings, read the numbers off them by eye, and typed them into filenames. That
is the manual work this feature exists to remove — evidence of a gap, not of a
convention.

## The actual requirement

> Any kind of figure, image, or anything visual in a text document can be sent
> as context to the AI.

Not "patents". Not "labelled figures". Not "images we can anchor". Everything
visual, whatever shape it arrives in.

That reorders the whole design. Anchoring is not the organising principle — it
is how we choose *which* visuals accompany *which* request. An image with no
label and no anchor is still usable: it is one of five drawings in this job, and
for a five-drawing job that is frequently enough.

### Four stages, in order

1. **Collect.** Every visual the job has, from wherever it is: inline in the
   source document, in a sibling document, in a configured folder, later from a
   PDF. Inline images, but also the ones easy to forget — inside tables, headers,
   text boxes, and vector EMF/WMF, which is what a patent drawing usually is.

2. **Identify.** A ladder, first hit wins:
   1. a label in the text near it (document mode)
   2. a label in its filename (folder mode)
   3. **read the label off the image itself** — vision. This is the rung that
      removes the manual renaming above, and nothing implements it.
   4. none: it is "image 3 of 5", which is still an identity.

3. **Anchor.** Citation → caption → proximity → the job as a whole. The last is a
   real answer, not a failure: a small drawing set can simply travel with every
   request that plausibly needs it.

4. **Deliver.** Two paths, not exclusive:
   - **Distilled** — one vision pass over the collected visuals produces a text
     manifest the user can read and correct, injected into every prompt. Cheap,
     survives non-vision providers, persists to the next job in the family.
   - **Attached** — the actual picture sent with the requests that genuinely
     need it, chosen by whatever anchor stage 3 produced.

Stage 2.3 and stage 4 are the missing pieces. Stages 1 and 3 are largely built:
`DocxImageExtractor` does document collection with anchors, `ReferenceImages`
does folder collection, and `OnDocumentImagesRequested` reports what a job has.

So the unit of this feature should not be *"a folder of images with figure
numbers"*. It should be **an image with an anchor**:

```
image     the file
label     "FIG. 3", "Table 2", or none
caption   the caption text, or none
anchor    the source text it sits among  <- the part that always exists
```

Citation-based lookup then stops being the mechanism and becomes a special
case: when a label exists and the text cites it, use that; otherwise fall back
to the anchor, which is always there.

## This is closer than it looks

Workbench's `image_extractor.py` **already computes exactly this tuple.** It
extracts images from a DOCX and, for each, returns:

```python
(image_path, label_or_None, surrounding_text)
```

Labels are detected from document structure, not guesswork: Word's built-in
`Caption` paragraph style first, then the following paragraph, then the
preceding one, then a pattern match over a wide label vocabulary — `FIG.`,
`Figure`, `Table`, `Diagram`, `Chart`, `Photo`, `Scheme`, `Plate IV`,
`Exhibit A` — falling back to sequential naming when nothing is detectable.

That third slot is described in its own code as *"surrounding text available for
the AI step"*.

**And then it is discarded.** The extractor's output is files on disk with
names. The anchor is computed and thrown away, because the consumer downstream
is a folder.

## The architectural consequence

**A folder has already lost the anchor.** You cannot recover *"page 3, between
'Mount the bracket' and 'Tighten the screws'"* from `IMG_2094.png`. Any design
whose input is a folder is permanently limited to case 1.

So: **two modes, and the anchor is what both produce.**

- **Folder mode** (exists today). Pre-extracted images, matched by filename.
  Right for patents, where the figures often arrive as separate PNGs and never
  passed through a DOCX we can read. Keep it as the manual override.
- **Document mode** (new). Extract from the project's own source file, keeping
  label, caption and anchor. This is the one that serves every other document,
  and the only one that can serve an unnamed image.

The plugin can do this without a new dependency: `DocxImporter` already uses
`DocumentFormat.OpenXml.Packaging.WordprocessingDocument`.

## Stage 4 in detail: how a visual actually reaches a model

There are several routes, and they are easy to mistake for alternatives to each
other. They are not. They differ along **two independent axes**.

**Form** — what the model receives:
- **Pixels.** The image itself, base64 in a vision request. What Workbench does.
- **Text.** A description, distilled once, injected as context. What a
  `figures.md` in a memory bank does.

**Control** — who decides what is included, and when:
- **Pull.** An agent with tools fetches what it wants, when it wants
  (MCP, Claude Desktop). The model decides; cost is paid per fetch.
- **Push.** We assemble the payload before the call (Batch Translate over the
  API). We decide, up front, with no idea which segment will need what.

That gives four cells, and all four are legitimate:

| | **Pixels** | **Text** |
|---|---|---|
| **Pull** (MCP agent) | agent opens image files from a folder as it sees fit — **no tool for this yet** | agent reads `figures.md` via `search_supermemory` — **works today** |
| **Push** (batch API) | base64 images attached to requests — Workbench's `figure_context_manager` | `figures.md` injected into the system prompt — **works today for a hand-written file** |

### What follows from that

**The inventory is shared; only delivery differs.** Collect and identify once
(stages 1–2); every cell above consumes the same result. This is the reason not
to build any one channel as a special case.

**Cost separates push from pull decisively.** Order of magnitude, because the
exact figures move with provider and resolution: a drawing costs on the order of
a thousand-odd input tokens, a paragraph of description costs tens. On the
Acme job — 394 segments, 5 drawings — attaching every drawing to every request
is roughly 5 × 394 image payloads per run, and that is before the source text.
The distilled manifest is one vision pass ever, then well under a kilobyte per
request. Three orders of magnitude is not a tuning question.

For push, then, text is the default and pixels are the exception. For pull the
arithmetic reverses: the agent fetches an image only when it has decided it
needs one, so pixels are both cheap and precise — which is why the empty
pull-pixels cell is the most valuable one to fill.

**Only text can be reviewed.** The user can read a manifest, disagree, and
correct it. Nobody can audit what a model saw in a JPEG. This note already
argues that an occasionally-wrong description injected into every prompt is the
worst failure available here — which is an argument for review, and review needs
text. It is also an argument against text being the *only* form: a description
is lossy and fixed at the moment it was written, and cannot answer a question
its author did not anticipate.

**Push cannot ask.** In a chat the agent can decide "I need to look at figure 3
now". In batch we must guess before the run starts. So the two want different
defaults:

- **MCP / chat:** the manifest as a cheap always-on default, *plus* a tool that
  lists the job's visuals and returns one on request. The agent then escalates
  to pixels by itself, which is exactly the behaviour we want and cannot
  hand-code.
- **Batch / API:** the manifest always. Pixels only for segments that actually
  cite a figure — which is what Workbench's `figure_context_manager` already
  decides, and it needs a label to do it.

### The two gaps

1. **No vision pass anywhere.** Nothing turns a picture into text, in either
   product. Every text-form cell above therefore depends on the user writing
   `figures.md` by hand — which is the same manual work as renaming the files.
2. **No image tool in MCP.** 50 tools; three read SuperMemory, none can see a
   picture. So the pull-pixels cell is empty, even though it is the cheapest
   and most precise of the four, because the model only looks when it needs to.

Gap 2 is much the smaller piece of work and does not depend on gap 1.

## Where this meets `figures.md`

The handoff's argument for distilling to text — run vision once, cache, inject
— survives intact, and all four of its reasons still hold:

1. cost is one pass per project, not per segment
2. non-vision providers still get something
3. the artifact is text the user can read and correct
4. it persists to the next job on the same document family

What changes is **what gets distilled**. Not a numeral register — an image
manifest:

> **Image 3** — page 3, between *"Mount the bracket"* and *"Tighten the
> screws"*: photo of a wall bracket held by two screws, one partially driven.

A patent's numeral table is then the specialised form of the same artifact,
produced when the images turn out to be numbered figures whose numerals the
text cites. `figures.md` stays the filename; it stops being patent-shaped.

## What exists today

Worth stating precisely, because the gap is not where it looks.

**In the plugin:**
- `ReferenceImages` — resolve a configured folder, list images, parse figure
  labels, order numerically. No anchors.
- `NumeralInventory` — extract parenthesised numerals from source text with
  their citing sentences; reconcile text against drawings three ways. Built,
  **no UI**.
- `ProjectSettings.ReferenceImagesFolder` + the Library tab's Reference images
  row — records a folder. **Nothing reads it.**
- The bank loader reads any `*.md` at bank root, so a hand-written `figures.md`
  **does** reach every prompt. This half works.

**In Workbench:**
- `figure_context_manager.py` — attaches real images to requests whose segment
  cites a figure. Handles `figure` / `figuur` / `fig.`
- `image_extractor.py` — DOCX → images, with label detection and surrounding
  text.

**Nowhere:** anything that turns images into text. That is the missing piece,
and it is the piece that makes the folder setting mean something.

## Open questions

**Segment ↔ paragraph mapping.** The anchor is a DOCX paragraph; the thing being
translated is a Trados segment. Establishing that correspondence is real work
and is the main unknown here. It may be enough to anchor loosely — "these
images appear in the region of the document you are currently translating" —
rather than exactly.

**What about non-DOCX projects?** A project derived from a PDF has no
parseable source. Folder mode is the answer, and it means unnamed images stay
unsupported there. Worth being explicit rather than pretending otherwise.

**Per batch or per project?** Attaching anchored images to each batch costs
vision tokens per batch. Distilling once costs one pass. The manifest suits
distillation; the attachment path suits the segments that genuinely need the
picture. Probably both, as the handoff argued for its step 6.

**Does the user confirm the manifest?** The handoff insisted on review before
first use, and it was right: an occasionally-wrong description injected into
every prompt is exactly the silent-wrong failure this codebase keeps producing.

## Build order

Reordered to follow the stage-4 analysis: the MCP image tool comes before the
vision pass, because it is smaller, it does not depend on the vision pass, and
it fills the cheapest and most precise of the four delivery cells.

1. **A button for `NumeralInventory`.** ✅ **Done** (20.185). No vision needed,
   and a QA check on its own.
2. **Document-mode extraction** — DOCX → `(image, label, caption, anchor)`,
   ported from Workbench's extractor. ✅ **Done**
   (`Core/DocxImageExtractor.cs`), wired to the **Document images** link, which
   reports what a job's Word files contain and what is missing to use it.
3. **An MCP tool that returns an image.** Gap 2. The agent can already read a
   `figures.md` through `search_supermemory`; it cannot look at a picture. Two
   tools — list the job's visuals, return one by id — and the pull-pixels cell
   is filled. No vision pass required: the *model* is the vision pass, and it
   decides when to spend it. Smallest remaining piece with the largest effect.
4. **The distillation pass** — visuals + anchors + source text → `figures.md`.
   Gap 1, and the piece nothing in either product has. This is what makes batch
   mode work at all, and what removes the hand-renaming of `Fig. 2A.png`.
5. **Review before first use.** Non-negotiable for step 4: a description is
   lossy, fixed at the moment it was written, and injected into every prompt.
6. **Anchored attachment** for the batch segments that genuinely need a picture
   — Workbench's `figure_context_manager` is the reference implementation, and
   it needs step 3's identification to choose.

1 and 2 are done and involved no model calls. 3 involves no vision pass of our
own. Only 4 does.

## Align with Workbench, or diverge deliberately

Workbench maps `"Figure 1.png" → '1'`; this plugin's `ReferenceImages` parses
`FIG. 1`, `Fig 1`, `Figure 1`, `FIG-1`, bare `12`, `FIG. 2A`. Same job, two
conventions. A user with one folder of drawings should not get different results
in the two products — whichever rule wins, it should be one rule.
