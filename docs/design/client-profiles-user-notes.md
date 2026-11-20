# Project templates: notes for the Help (steps 1, 2 and 4, 20.209)

Running notes on what the Help will have to say. Not user-facing yet; the design is in
`docs/design/client-profiles.md` (branch `claude/client-profiles-design`; Order of work, steps 1 and 2).
The feature is called **project template** in all UI and Help text (Michael, 20 Nov; it was "job profile" until then):
it is Studio's own project template, the name Trados, memoQ, CafeTran and Transit all use. Supervertaler stores its
project settings inside it (memory bank, prompts, termbases, client), next to Studio's TMs, termbases and customer.
Not "Supervertaler project template": there is only one file, and the prefix would suggest a second kind. The Help
should say a template can stand for a client, a client plus end client, or a kind of job.

## Step 1: what a template carries

- Supervertaler's project settings now travel with Studio project templates:
  - the memory bank (by name),
  - the translate prompt and the proofread prompt (by their place in the prompt library),
  - the client name,
  - the termbase choices: which Supervertaler termbases are read, which are written to, which one is the
    "Project" termbase, and which are sent to the AI; for MultiTerm termbases, which are switched off in
    Supervertaler and which are sent to the AI.
- How to use it: set up one project for a client as you want it, then in Studio **Create project template**
  from that project. Every new project made from the template starts with the same Supervertaler project settings.
  Nothing to do in Supervertaler.
- A project made from the template takes a copy. Changing the template later does not change projects
  already made from it (that is how Studio's own settings in a template work too). Changing a project does
  not change the template.
- Existing projects: their project settings reach the Studio project the next time a Supervertaler setting is changed
  in them (Settings OK, picking a prompt or a memory bank), or after the project has been switched away from
  and opened again. Before that, a template made from an existing project does not carry it yet. Tip for the
  Help: "open the project, open Supervertaler Settings and click OK, then create the template".

## Things to say plainly

- **A template sent to someone else carries the client name, the memory bank name and the prompt names**
  (not the bank's or the prompts' contents), and the names of the Supervertaler termbases switched on in it.
  Only termbases with a choice switched on are named. Check before sending a template outside your own
  computers. The template's file name often names the client already.
- **A project package or a colleague's project** can carry those values too. On your computer:
  - a memory bank you do not have counts as no memory bank (as before when a recorded bank is missing);
  - a prompt you do not have is replaced by the mode's default prompt in the list; the project keeps the name,
    so if you add that prompt later it is used;
  - a termbase you do not have is switched off for you, noted in the diagnostic log, and kept in the project,
    so the owner gets it back when the project returns.
- **Supervertaler never writes your project settings into a project from a package or GroupShare.** Such a project is the
  agency's, and its settings can travel back to them; your memory bank, prompt names and client name stay in
  Supervertaler's own record on your computer. (The same rule as TM field stamping.) Values the agency's project
  already carries are still used, as above.
- **Termbases are matched by name.** A template made on one computer finds termbases of the same name on
  another. A termbase renamed on the same computer is still recognised. A termbase with a different name on
  the other computer is not found (it counts as missing, above).
- **Not carried:** the reference-drawings folder and the termbase database file (both are paths on one
  computer), the model choices (global), Studio's own TMs and termbases (Studio's template carries those
  itself, as always).
- A termbase created after a project was set up is off in that project until switched on, as before.

## Step 2: applying a project template to a project that already exists

Where: Supervertaler Settings > General > **Project templates** > **Project templates...** (the window was called "Apply a job
profile" until step 4). It works on the project open in Studio: the one in the Editor, else the one selected in Projects.

- Pick a template from the list (every template Studio offers under New Project, plus any in Studio's
  Project Templates folders; there is a filter box). The window shows what applying it would change before
  anything changes, and asks once more before applying.
- What is applied:
  - **Supervertaler's project settings** from the template (memory bank, prompts, client, termbase choices). Anything the
    template leaves empty stays as the project has it. The project also records the template's name.
  - **Studio's termbases: keep, add** (Michael, 19 Nov). The project keeps all its termbases, in their order, with
    its default termbase, language indexes and recognition options. The template's termbases that the project
    does not have yet are added at the end; one it already has (same file) is left as it is and listed as
    "already in the project". A termbase file that is not there is left out and named. Server termbases are not
    added: add them in Studio's Project Settings. Language indexes: the template's are added only for languages
    the project does not map yet.
  - **Studio's TMs: keep, add** (and the other translation providers in the list, such as MT). Every TM list of
    the project (the one for all language pairs, and each language pair's own) keeps its TMs, in their order and
    with their switches (lookup, update, penalty). The template's TMs a list does not have yet are added at the end,
    with the template's switches; a TM in both (same file, or same address) keeps the project's switches and is
    listed as "already in the project". A language pair with a list of its own gets the template's TMs for that
    pair (or, if the template has none for it, the template's TMs for all pairs). A language pair without a list of
    its own, where the template's pair has one, gets one: the project's TMs for all pairs plus the template pair's,
    so nothing it used stops applying. A TM file that is not there is left out and named. Server TMs are added,
    and Studio asks you to sign in when it needs to; if Studio refuses them, the rest is added without them and
    the summary says so.
  - **The project's Studio customer**: the one the template names. If Studio's customer list does not have it,
    you are asked whether to add it; say no and the project's customer is left as it is.
- **The open document and new TMs.** An added TM is used straight away: Project Settings shows it, and a
  Concordance search (F3) in the open document finds it (Michael, 20 Nov). No need to close and reopen anything.
- **Applying from the Settings window.** After a project template is applied, the Settings window shows the project's
  new termbase and prompt choices, so pressing OK there keeps them. (Before 20 Nov, OK wrote back the choices the
  window had shown before applying, which switched the template's termbases off again.)
- **Nothing is ever removed** from the project's TM or termbase lists, and nothing is deleted from any TM or
  termbase. A template with no TMs, termbases or customer leaves those as they are. Applying the same profile
  twice adds nothing the second time.
- The preview is a table, grouped into Supervertaler, Translation memories, Termbases and Studio customer: one
  row per TM, termbase, customer or setting, saying Kept, Added, Set or No change. What applying changes is in
  bold. With no project open, the same table shows what the template holds.
- The question before applying is short (the table is on screen): "This will add 1 TM and set the customer.
  Nothing is removed." When nothing would change, it says so and does not ask.
- Afterwards a short summary: one line per change, what could not be done, and the parts with no change in one line.
  The table then shows the project as it is after applying.
- A template that cannot be read (moved, deleted, on a NAS that is offline, damaged) changes nothing; the
  window names the file and says why.
- **Projects from a package or GroupShare**: only Supervertaler's own record is changed. Their Studio TMs,
  termbases and customer are left alone: they are the agency's.
- Not carried by "apply": Studio's other settings in the template (file types, QA, task sequence, analysis
  bands, users). Those still come only when a project is made from the template.

## Step 2: Studio's customer list, edited in Supervertaler

Where: Supervertaler Settings > General > Project templates > **Studio customers...**, and the same button in the
Project templates window.

- Shows Studio's customer list in alphabetical order (by your Windows language, ignoring capitals), with
  **Add**, **Edit** (name and email in one window; double-click a customer opens it too) and **Remove**.
- Changes go straight into Studio's own list while Studio runs (the list Studio's Project Settings and New
  Project offer). Supervertaler never edits projects.xml behind Studio's back.
- Studio's own list is kept alphabetical too: after every add or rename in Supervertaler, and once each time
  Studio starts with Supervertaler. A customer added in Studio itself goes to the end of Studio's list until the
  next start (or the next change made in Supervertaler).
- A name already in the list is refused, also when it differs only in capitals (Studio matches a template's
  customer ignoring capitals, so two such names could not be told apart).
- **Rename keeps the customer**: projects that use it still point at it. Studio may go on showing the old name
  for those projects until their customer is set again (Studio keeps a copy of the name in each project).
- **Remove**: if projects in Studio's project list use the customer, the window says how many and asks first.
  Those projects keep it as their customer; Studio just stops offering it for others.

## Confidentiality (say it in the Help, near "Create project template")

- **A template carries the customer's name** (Studio puts it there) and, since step 1, the client, memory bank
  and prompt names. Check before sending a template to anyone else. The Project templates window says this too.

## Step 4 (first part): seeing what a project template holds

- In the Project templates window, **Show: What it holds** lists everything the chosen template carries, with no project
  to compare it with: Supervertaler's project settings (each part under its own subheading), Studio's TMs with
  their switches (lookup, concordance, update, penalty), each language pair's own TM list marked "[en-GB → nl-NL
  only]", Studio's termbases, term recognition (termbase indexes, minimum match value, search depth, search order,
  terms without a translation), and the customer. A TM or termbase whose file is not there says so, with its folder.
- The template's file is named under the table; clicking it opens its folder in Explorer with the file selected.
- **Show: What applying it to the open project would change** is the step 2 table. With no project open, only
  "What it holds" is offered. Clicking **Apply to this project...** always switches to the applying table first.
- A template with no Supervertaler project settings says so, and how to give it some: set them up in a project
  (Settings, then OK), then **Create Project Template** from that project in Studio.
- A language pair's TM list that Studio ignores (in the template but not switched on for that pair) is not shown.
- Read only, for now. Editing a template comes next (through Studio's own template object; see the step 3 findings).

## Step 4 (second part): saving, updating and editing templates

- **Save project as template...**: Studio's CreateNewProjectTemplate(description, path, project) + Save, as Studio's
  Create Project Template does, then Supervertaler's values from its own record (also for a package project, whose
  bundle holds the agency's values), then the provider's Save (projects.xml). Never replaces a file. A failure removes
  the half-made template and file.
- **Update from project...**: Studio's UpdateFromProject + Save (what the New Project wizard's "update the template"
  does), then Supervertaler's values. Asks first; everything in the template becomes the project's.
- **Edit...**: Supervertaler's project settings (bank, prompts, client, termbase ticks) and the Studio customer. MultiTerm
  choices kept. Termbases the computer lacks, and unreadable lines, kept unchanged. One Project termbase.
- Only templates registered in the running Studio are changed, through Studio's live template object (Settings set back,
  then Save), so Studio's cache and the file agree. Others: the window says to import them first.
- Backup of the file in %TEMP% before any change; put back (and Studio's copy discarded) on failure; deleted after.
- A template's profile name is cleared when written, so a template is known by its file name.
- Not tried inside Studio yet (needs Michael): the three Studio calls on a test template in each version.

## Open for later steps

- The profile name is now set when a project template is applied, but nothing shows it yet.
- Renaming a memory bank renames it in every project's Supervertaler record at once, but in a Studio project
  only the next time that project is open; a template keeps the old name until it is made again.
- Templates themselves are not edited by Supervertaler yet (step 4: the Project templates window).
- Not yet tried inside Studio (step 2): whether Studio's open Projects view and Project Settings show a changed
  customer at once, and whether Studio keeps the sorted customer order when it saves.
