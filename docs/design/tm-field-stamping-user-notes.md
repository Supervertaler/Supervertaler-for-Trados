# Stamping project and client: notes for the Help pages

User-facing notes, kept while building, for the Help (settings page, and the EverTM page). Design:
`tm-field-stamping.md`. Parts not tried in Studio yet are marked *(to check in Studio)*.

## Studio TMs

- **Where**: Supervertaler's settings > General > **Studio translation memories**:
  - [ ] Stamp the project's name on every unit Studio adds to the project's TMs
  - [ ] Stamp the project's client (its Customer) too
  - Both off until you switch them on.
- **What it does**: when you open a project (or a document in it), Supervertaler fills in the project's field values:
  - in Studio: Project Settings > Translation Memory and Automated Translation > Update, the "Field values" list;
  - the project's name goes in a field called **Project** (also Projekt, Job);
  - its customer goes in a field called **Client** (also Customer, Kunde).
  - Studio then adds them to every unit it stores: each segment you confirm, and the batch task Update Main Translation
    Memories. File and GroupShare TMs alike.
- **A TM stores only fields it has.** A TM made with Studio's default settings has none. **This project's TMs...**
  lists the project's TMs with Yes/No for a Project and a Client field, and can add a missing field to one of your own
  file TMs:
  - one TM at a time, after a warning: it changes the TM;
  - it gets the same name and type as the project's other TMs already use for that field ("client", one value, if
    your main TM has it like that), so Studio sees one field; with no other TM having it, "Project" / "Client" as
    text with several values. If the other TMs already define it in more than one way, or as a list of fixed values,
    nothing is added and the window says why (13 Nov: adding it differently made Studio drop the field);
  - never for a GroupShare TM, a read-only TM file, or a project that came in a package (or from GroupShare);
  - units already in the TM are not changed; only units added from then on carry the field.
  - *(to check in Studio)* whether a TM that is open in a document picks up the new field before the project is
    reopened.
- **Never changed**: a value you typed in the Field values list yourself, or one that was there already.
- **A value that isn't this project's** – a project template that carried an older project's name, say – is kept,
  and **This project's TMs...** says so: 'Project is "PROJ-000"; this project's name is "PROJ-001". It was there
  already, so it is kept.' **Use this project's values...** replaces it, after showing exactly what changes (also in a
  language pair's own list). Units already in your TMs are not changed. Nothing is replaced without that button.
- **Not filled**:
  - a picklist field (a picklist value can't be made up);
  - a field that two of the project's TMs define with different types (Studio leaves it out of its own Field values
    list too). **This project's TMs...** shows "clash: see below" for it and says which TMs define it how (for
    example: TM-A as text with one value, TM-B as text with several values); make them the same in Studio's TM
    settings (Fields). A value that was already there still applies, though Studio no longer lists it: the window
    still shows it.
- **"client" and "Client"** in two TMs show as two rows on Studio's Field values page, one of them empty. That's only
  how Studio lists them: both TMs store the one value (Studio matches field names ignoring capitals).
- **Projects from a package or GroupShare** are left alone, unless you tick "stamp its name and client into its TMs
  anyway" in **This project's TMs...**. That switch is kept in the project. Reason: such TMs are usually the agency's.
- **Per language pair**: a language pair with its own TM list gets the values in its own settings too.
- The values stay in the project's settings, where you can see them, change them or remove them. A renamed project
  keeps the old name there until you clear it.
- Not possible: the document's file name in Studio TMs (Studio's field values are per project, not per document).
  EverTM stores the document's name.
- Cost: the TMs' fields are read in the background when a project opens, once per TM (about 7 ms per TM; ten TMs of 31
  fields: 70 ms the first time, nothing afterwards). Opening a project is not slowed down.

## EverTM

- EverTM's window: "Stamped on segments you confirm: [x] Project [x] Client [x] End client [x] Subject [x] Document
  [x] User". All on by default. Switching one off stops it being stored from then on; units already in EverTM keep
  theirs.
- The time you confirmed a segment has no switch: it decides which translation is the newest.
- With Project off, the Source field says only "Studio", so the project's name isn't stored anywhere.

## Test in Studio 2026 (with Michael, on copies only)

One step at a time, in a test project ("Acme test") with a COPY of a TM; never one of his real TMs.

1. Make a copy of a small TM into a test folder; create the "Acme test" project with it (Update ticked) and one small
   document.
2. In Supervertaler's settings, switch on Project and Client; open **This project's TMs...**: the copy shows "no" for
   both.
3. Add the Project field to the copy (the warning shows); the list shows "yes".
4. Open the document, confirm one segment.
5. Check in Studio: Project Settings > Translation Memory > Update shows Project = "Acme test"; the confirmed unit in
   the copy (Translation Memories view, or a lookup with "Show translation unit field values") carries Project =
   "Acme test".
6. If step 5 shows the value in the settings but not on the unit: close and reopen the document, confirm again (tells
   whether an open document sees a field added meanwhile).
7. Type another value in the Field values list; reopen the project: it stays.
