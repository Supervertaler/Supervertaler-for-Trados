# Stamping project metadata on every translation unit (Studio TMs and EverTM)

Design note, 11 Nov 2026. Status: proposal, nothing built. Base: Trados main fdc14c9, Plugin Core main d7f8c61.

Michael, 11 Nov: "without thinking about anything, the project name of my particular Trados Studio project I'm working
on is always automatically stamped into my translation units. Whether these go to the Trados Studio translation memory
or EverTM", and "I would like this to be optional so that the user can select which metadata fields are automatically
stamped on all translation units."

## In short

- **EverTM already does it**: every confirmed segment gets Project, Client, End client, Subject, Document, the user and
  the time (phase 1). What's new for EverTM is only the choice of which fields.
- **Studio TMs can be made to do it by Studio itself.** At every confirm, and in "Update Main Translation Memories",
  Studio hands every TM the project's *field values on update* (Project Settings > Translation Memory > Update). The
  plugin can fill those in from the project, through the public project API. Then Studio stamps them, into file-based
  and GroupShare TMs alike, in Studio 2022, 2024 and 2026.
- **The one hard limit**: a Studio TM keeps only fields it defines. Studio tells each TM to ignore any other
  (`NewFields = Ignore`, at confirm and in the batch task). So a TM without a "Project" field never gets one stamped,
  whatever the plugin fills in. Adding the field changes the user's TM, so it needs his consent, TM by TM.
- **Recommendation:**
  1. Fill the project's field values from the project, for the fields the project's TMs define. Never overwrite a value
     the user typed. Off until switched on.
  2. Show which attached TMs lack the field, and offer to add it to a file-based TM, with consent, TM by TM. Never to a
     GroupShare TM or one from a package.
  3. A field choice for EverTM, defaulting to what it does now.
- Not a Record Source TU-style wrapper (below).

## 1. What Studio does (read from Studio 2022, 2024 and 2026's own assemblies; nothing tried in Studio yet)

**Where the values come from at confirm.**

- `EditorTranslationProvider.Update` builds the update with `InteractiveTmUpdateImportSettingsBuilder` (2024/2026) or
  `TranslationUpdate` (2022):
  - `ImportSettings.ProjectSettings` = the `TranslationMemorySettings.ProjectSettings` field values;
  - `NewFields = ImportSettings.NewFieldsOption.Ignore`.
- The settings are read **at every confirm**, not cached:
  - 2024/2026: the project's own settings, or the document's settings when the document's language pair has its own TM
    list ("override parent");
  - 2022: always the document's settings, which inherit the language pair's and the project's.
  - So values the plugin writes are used from the next confirm on, in an open document.
- **Update Main Translation Memories** (`AbstractUpdateTmContentProcessor.Initialize`) does the same, from the language
  pair's settings, also with `NewFields = Ignore`.

**What a Studio file TM does with them** (`TranslationMemoryImpl.Importer`):

- It merges the values into the unit's own (`FieldValues.Merge`: a multiple-value text field collects them).
- It then keeps a value only for a field it defines, matched by name. A single value is promoted to a multiple field.
  Other fields are dropped silently (`ComputeAttributeValues` with `Ignore`).
- GroupShare TMs get the same values (`EntityConverters.ToRestUpdateSettings`), with the same rule on the server.
- A value that changes a unit already in the TM makes the import a "merge", so a re-confirmed unit gains the project.

**When Studio's Field Values list is empty** (`TranslationMemoryFieldValuesSettingsPage`, `FieldDefinitionHelper`):

- The list is the **union of the fields defined by the project's Studio TMs** that are enabled, available and have
  **Update** ticked (`performUpdateOnly: true`):
  - under **All Language Pairs**, the TMs of every language pair;
  - under one language pair, that pair's TMs only.
- A field two TMs define with **different types** (text in one, picklist in the other) is left out.
- Picklist items of same-name picklist fields are combined.
- Values already stored in the setting are shown even when no TM defines that field any more.
- **So it is empty whenever none of the Update-ticked Studio TMs defines a custom field.** That is the case for a TM
  made with Studio's defaults. EverTM never appears there: it isn't a Studio TM (`ITranslationMemory`), but it does
  receive the values (it already reads Subject from them).

**What the plugin can do through the public API (`Sdl.ProjectAutomation`).**

- Read the TMs, the project name and the customer: `FileBasedProject.GetProjectInfo()`, the .sdlproj.
- Write `TranslationMemorySettings.ProjectSettings`, the project's and each language pair's:
  - `FileBasedProject.GetSettings()` / `GetSettings(targetLanguage)`, `UpdateSettings(...)`;
  - the plugin already does exactly this for its own per-project setting (`ProjectBundleSettings`);
  - same in 2022, 2024 and 2026.
- Add a field definition to a **file** TM: `FileBasedTranslationMemory.FieldDefinitions.Add(...)`, `Save()`. Not tried
  while Studio has that TM open in an editor: see the open questions.
- It **can't** stamp per confirm into a Studio TM by itself. The update goes from Studio's cascade straight to each TM.
  The only way in is to be the provider in between, which is what Record Source TU does.

## 2. Options for Studio TMs

**A. Fill the project's field values (recommended).**

- When to fill: when a project becomes active, and again when its TM settings change. Studio raises the project and
  settings events the plugin already uses.
- What to fill: for each field the user switched on, if a matching field exists (same rule as Studio's list):
  - match by name, using EverTM's spellings ("Project", "Projekt", "Job"; "Client", "Customer", "Kunde");
  - text fields only;
  - and only when its value is empty: set the project's name (or its customer, ...).
- Where: the project's settings, plus a language pair's own settings when that pair overrides the TM list.
- Studio then stamps at every confirm and in Update Main TMs, in file and GroupShare TMs. The values show on Studio's
  own Field Values page, where the user can see, change or remove them.
- Costs:
  - the .sdlproj gains a setting, like any project setting;
  - it travels with the project, into packages and templates;
  - a renamed project keeps the old value until the user clears it.
- Never:
  - overwrite a value the user typed;
  - choose a picklist value (a picklist field gets nothing, unless the project's name is already one of its items;
    proposed: nothing);
  - touch a TM.

**B. Add the missing field to a file TM, with consent (recommended as a separate, explicit action).**

- In Supervertaler's settings, for the open project, list each attached TM and whether it has the fields switched on.
- For a file TM without one, offer **Add field "Project" to this TM**, for each TM, after a plain warning: "This changes
  the TM: every unit added to it from now on can carry the project name."
- Then A fills it.
- Never offered for:
  - GroupShare TMs (needs server rights, and they're the agency's);
  - read-only files;
  - TMs of a project created from a package (agency TMs: a private project name must not leak into a client's TM);
  - Studio's AutoSuggest / LC providers.

**C. A wrapper provider, as Record Source TU (not recommended).**

- The user replaces each TM in the project with "wrapper + TM". The wrapper adds the fields at update.
- It could even add missing fields itself (`NewFields = AddToSetup`), which is exactly the change to the user's TM that
  needs consent.
- Costs:
  - every project's TM list changes;
  - it wraps lookups too;
  - file TMs only (as Record Source TU);
  - and more for us to keep working across Studio versions.
- It gains only per-document values (the file name per unit), which A can't do: Field Values are per project and
  language pair, not per document.

**D. Do nothing for Studio TMs, explain the manual way.** Studio already does it when the TM has the field and the value
is typed. This is the fallback if A turns out not to reach the open editor.

## 3. Part B: which fields, for EverTM and for Studio TMs

One small section in Supervertaler's settings ("Stamped on translation units"), two columns:

| Field | EverTM (default) | Studio TMs (default) | Value |
|---|---|---|---|
| Project | on | off | the Studio project's name |
| Client | on | off | the project's customer; else the first part of a CLIENT_Endclient name |
| End client | on | off | the rest of a CLIENT_Endclient project name |
| Subject | on | – | already the user's own field value in Studio; EverTM takes it from there |
| Document | on | – (not possible per document, see C) | the document's file name |
| User | on | – (Studio stamps its user itself) | the Studio user |

- The confirm **time** is not a choice for EverTM: newest-wins needs it.
- "Studio TMs" applies to every project. A project created from a package stays off unless switched on for that
  project, with the warning above.
- No other options: not per TM, and no custom field names. Michael asked for a choice of fields, nothing more.

## 4. Failure paths

- **Read-only TM file**: A writes only project settings, so it's unaffected. B isn't offered.
- **TM offline or unavailable**: Studio leaves it out of the field list. A treats it the same way: nothing filled for
  fields only it defines.
- **Field of another type** (a "Project" picklist): not filled.
- **The user's own value**: never overwritten.
  - The value counts as ours only if the plugin wrote it and it still equals the project's name. Remembering that takes
    one stored line per project.
  - Proposed: don't track it; "only when empty" is enough.
- **Two TMs with "Project" of different types**: Studio drops the field from its list. A does the same.
- **Package projects**: off by default for Studio TMs. EverTM stamps them locally as now: EverTM is Michael's own.
- **The project API refuses or throws** (a GroupShare project, a project being saved): nothing is filled, logged
  once. Confirming is never affected; A never runs at confirm time.
- **Settings written while Studio's Project Settings dialog is open**: Studio would overwrite them on OK. A runs on
  project/settings events, not while the dialog is open.

## 5. Sizes (estimates)

- **A**: about 250 lines in the plugin:
  - the field match, mirroring `FieldDefinitionHelper`;
  - writing the settings;
  - the events;
  - plus offline checks with a real FileBasedProject and file TMs in a temp folder (SuperVaultCheck style).
- **B**: about 150 lines plus a list in the settings page. It needs one test in Studio first (see Q3).
- **EverTM field choice**: about 80 lines:
  - the plugin fills only the chosen fields;
  - core: `VaultLearnUnit.Info` should split End client only when End client is chosen;
  - a small Plugin Core change (I'll say before touching core).
- **The settings section**: about 100 lines.
- No change to EverTM's store or index.

## 6. Open questions for Michael (each with a recommendation)

1. **Studio TMs: switch on with Project only, or Project and Client?** Recommendation: Project only, as he asked.
   Client is one tick away.
2. **For a TM without a "Project" field: offer to add it (B), or only say which TMs lack it?** Recommendation: offer,
   TM by TM, with the warning. Never for GroupShare or package TMs.
3. **Is it fine that I test B once in his Studio 2026, on a copy TM, with a project open?** Recommendation: yes. The
   API can add the field; what isn't known is whether the open editor sees it before the project is reopened.
4. **Packages: should Studio-TM stamping stay off for projects created from a package, unless switched on per
   project?** Recommendation: yes. A private project name must never reach an agency's TM unasked.
5. **EverTM: keep all fields on by default (as now), with the choice to switch any off?** Recommendation: yes.

## 7. Not in this note

- memoQ (it already stamps project and client itself).
- The document's file name in Studio TMs (only a wrapper could do it: option C).
- Changing what EverTM stores.
