# Client profiles: notes for the Help (step 1, 20.209)

Running notes on what the Help will have to say. Not user-facing yet; the design is in
`docs/design/client-profiles.md` (branch `claude/client-profiles-design`; Order of work, step 1).

## What a translator gets

- Supervertaler's per-project set-up now travels with Studio project templates:
  - the memory bank (by name),
  - the translate prompt and the proofread prompt (by their place in the prompt library),
  - the client name,
  - the termbase choices: which Supervertaler termbases are read, which are written to, which one is the
    "Project" termbase, and which are sent to the AI; for MultiTerm termbases, which are switched off in
    Supervertaler and which are sent to the AI.
- How to use it: set up one project for a client as you want it, then in Studio **Create project template**
  from that project. Every new project made from the template starts with the same Supervertaler set-up.
  Nothing to do in Supervertaler.
- A project made from the template takes a copy. Changing the template later does not change projects
  already made from it (that is how Studio's own settings in a template work too). Changing a project does
  not change the template.
- Existing projects: their set-up reaches the Studio project the next time a Supervertaler setting is changed
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
- **Termbases are matched by name.** A template made on one computer finds termbases of the same name on
  another. A termbase renamed on the same computer is still recognised. A termbase with a different name on
  the other computer is not found (it counts as missing, above).
- **Not carried:** the reference-drawings folder and the termbase database file (both are paths on one
  computer), the model choices (global), Studio's own TMs and termbases (Studio's template carries those
  itself, as always).
- A termbase created after a project was set up is off in that project until switched on, as before.

## Open for later steps

- The profile name is stored but nothing sets or shows it yet (step 2 onwards).
- Renaming a memory bank renames it in every project's Supervertaler record at once, but in a Studio project
  only the next time that project is open; a template keeps the old name until it is made again.
- Templates themselves are not edited by Supervertaler yet (step 3/4: the client profile window).
