# A model per kind of task: notes for the help page

User-facing notes, kept while building (20.209), for the Help page on AI Settings. The feature is built and passes
its offline tests; **not yet tried in Trados Studio**.

## What it is

- On **Settings > AI Settings**, under **Provider**, there are now three model lists, in order of how powerful a model
  the work needs:
  - **Light tasks** – small mechanical requests: FuzzyFixer, AutoTagger, Add term with abbreviation, choosing articles
    from a large memory bank, and AutoPrompt's two short helper questions.
  - **Translation** – what **Model** was until 208: Batch Translate, Translate active segment (Alt+T), Chat,
    QuickLauncher, AI Proofreader and FigureLens.
  - **AutoPrompt** – writing your project prompt.
- **Nothing changes until you choose.** Light tasks and AutoPrompt start on their first entry, which names your
  Translation model and says "(same as Translation)". While a list stands on that entry it follows whatever
  Translation is set to.
- The lists offer the models of the provider you have selected. One API key, one company receiving your text.
- The choice is kept per provider: switch from Claude to Gemini and back, and Claude's three choices are as you
  left them.
- The model menus on the Batch Operations tab and under the chat change **Translation** only.
- With **Ollama** or a **custom OpenAI-compatible profile** the two extra lists are greyed out: one server, one
  model.

## Good to know

- **Fetch list**, **Show all models** and **Model ID** belong to Translation, as before. A model you fetched also
  appears in the other two lists when Show all models is ticked.
- If a model you chose for Light tasks or AutoPrompt is later withdrawn by the provider, those requests use your
  Translation model, and the list shows "(same as Translation)" again.
- The FuzzyFixer window, the Reports tab and the Usage & Costs report name the model each request really used.
- **When a cheap model for Light tasks pays.** A provider's prompt cache belongs to one model. FuzzyFixer on the
  same model as Alt+T reuses the memory bank Alt+T has just cached; on another model its first repair pays for the
  bank once, the next ones are cheap. Rule of thumb from list prices (not measured in Studio): with Opus or Fable for
  translation, a small model for Light tasks pays from the second or third repair; with Sonnet, from about the eighth
  repair within an hour.
- **How good is a small model at repairs?** In a test of 30 invented English-Dutch repairs (18 Nov 2026), Claude
  Haiku 4.5 matched Sonnet 5.5 and Opus 5.5, tags included, at a third of Sonnet's cost. Short technical sentences
  only; no memory bank or termbase in the test.

## Not in this version

- A different provider per kind of task.
- A model per single feature.
- A small Claude model in the short list: Haiku is reachable through **Fetch list** and **Show all models**.
