# Open example project – user notes

User-facing notes, kept while building, for a Help page (probably a section of "Getting started" in the Help
repository). Not yet in Help.

## What it is

- **Settings > AI Settings > Open example project…** creates a small Trados project to try Supervertaler on, and
  opens it in the Editor. Pressing it saves the settings first (so a key you just pasted in is used) and closes
  the Settings window.
- The document is an invented installation guide for a water heater ("Acme AquaLine AL-50"): about 70 segments,
  with a bulleted list, two numbered lists, two tables, bold and italic text, a hyperlink, numbers with units, and a
  few sentences that are almost the same.
- You choose a **source language** and a **target language**, as for any project. One of them has to be English,
  because the example document is written in English; the dialog says so if neither is. The lists show languages
  ("Dutch", "English"); tick **Show language variants** to choose a variant ("Dutch (Belgium)", "English (United
  Kingdom)"). English comes first, then the prepared languages, then all others. Into English is only possible for a
  prepared language.
- For a **prepared language**: a translation memory with ten entries (three exact matches, one that differs only in
  its numbers, five fuzzy matches between about 70 and 95%, and one weak match below 70%) and a Supervertaler termbase
  with fifteen terms. The translation memory is inside the project folder (`TM\Supervertaler example.sdltm`).
- The termbase is called like the project ("Supervertaler example (English to German)"). It is switched on in the
  example project only (TermLens and the AI), and switched off in all your other projects. Your own termbases are
  switched off in the example project.
- For **every language**: a SuperMemory memory bank named like the project (e.g.
  `supervertaler-example-dutch-to-english`, in your data folder's `memory-banks\` folder), set as the example
  project's bank. It holds a short client brief (keep "Acme" and "AquaLine AL-50" as they are, translate warnings in
  full), two client terms and three style rules (direct instructions, a space between number and unit, button labels
  and display codes unchanged). It is in English, so it fits every language. Change it and translate again to see
  what a memory bank does. A bank of that name that is already there is used as it is, never overwritten.
- For a **language that is not prepared yet**: the document only. Translate active segment, Batch Translate,
  Proofread and QuickLauncher all work; FuzzyFixer and TermLens have nothing to show. A message says so.
- The example's translations were made with AI (GPT-6.1 Sol) and checked by AI (Claude Opus 5.5, every segment scored
  blind; nothing below "correct" went in), plus checks by script (numbers, formatting, product names). The Dutch was
  read by a person. [Confirm once Michael has read it.]
- The prepared languages (22): Chinese (Simplified), Czech, Danish, Dutch, Finnish, French, German, Greek, Hungarian,
  Italian, Japanese, Korean, Norwegian (Bokmål), Polish, Portuguese (Brazil), Portuguese (Portugal), Romanian, Russian,
  Spanish, Swedish, Turkish, Ukrainian. Other variants of these languages (German (Austria), French (Canada) …) use
  the same data. Every other language: document only.

## Things to try (the closing message lists them)

- Translate active segment (Alt+T), or Batch Translate the whole document.
- FuzzyFixer (Ctrl+Alt+F) on a segment with a fuzzy match, e.g. "Press + to raise the temperature in steps of 1 °C."
- Proofread, and QuickLauncher (Alt+Q).
- The memory bank: change a rule in it and translate a segment again.

## Where it goes

- In Studio's own Projects folder, e.g. `C:\Users\<you>\Documents\Studio 2024\Projects\Supervertaler example (English
  to German)\`, and in Studio's Projects list. Delete it in Studio like any other project when you are done.
- A folder of that name that is not an example project is never touched: the new project gets "(2)", "(3)" … added.

## Pressing it again

- **Same languages:** you are asked: **Open it** (as it is, with your changes) or **Start afresh** (deletes the old
  example project with its termbase and memory bank, and makes a new one). Close the example's files in the Editor first, or
  Supervertaler asks you to.
- **Other languages:** a second example project is made next to the first.

## When it cannot

- Your Studio licence does not allow creating projects through the API: Studio's reason is shown; nothing is made.
- Another Supervertaler job is running (Batch Translate, a project being created): try again when it has finished.
- The translation memory, the termbase or the memory bank cannot be added: the project is still made and opened, and
  the message says which part is missing.
- Studio does not open the project in the Editor: the project is still made; the message says so (it no longer
  claims it is open) and tells you to double-click it in the Projects view.
- No API key yet: the project is still made; the message says where to add a key.
