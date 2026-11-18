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
- **Nothing changes until you choose.** Light tasks and AutoPrompt start on their first entry, "Follow Translation
  (Claude Sonnet 5.5)", with your Translation model in the brackets. While a list stands on that entry it follows
  whatever Translation is set to: change Translation and the brackets change with it. To keep a list on one model
  whatever Translation does, pick that model from the entries below it.
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
  Translation model, and the list shows "Follow Translation" again.
- The FuzzyFixer window names the model that made the repair ("96% match from Acme EN-NL, repaired by Claude Haiku
  5.5."). Its requests have their own cards on the Reports tab, headed **FuzzyFixer**, and their own line in the
  Usage & Costs report; until 208 they were listed as "Translate active segment".
- The Reports tab and the Usage & Costs report name the model each request really used.
- **When a cheap model for Light tasks pays.** A provider's prompt cache belongs to one model. FuzzyFixer on the
  same model as Alt+T reuses the memory bank Alt+T has just cached; on another model its first repair pays for the
  bank once, the next ones are cheap. Rule of thumb from list prices (not measured in Studio): with Opus or Fable for
  translation, a small model for Light tasks pays from the second or third repair; with Sonnet, from about the eighth
  repair within an hour.
- **How good is a small model at repairs?** In a test of 30 invented English-Dutch repairs (18 Nov 2026), Claude
  Haiku 4.5 matched Sonnet 5.5 and Opus 5.5, tags included, at a third of Sonnet's cost. Short technical sentences
  only; no memory bank or termbase in the test.

## Claude Haiku 5.5

- Claude's short list now has four models, cheapest first: **Haiku 5.5**, Sonnet 5.5, Opus 5.5, Fable 5.2. Haiku is
  there for Light tasks. A new installation still starts on Sonnet 5.5.
- Price (Anthropic, 18 Nov 2026): $0.10 in and $0.50 out per million tokens, a twentieth of Sonnet 5.5. On a request
  whose prompt is over 100,000 tokens Haiku 5.5 costs five times that, and Supervertaler's cost figures do not yet
  know this: for such a request they show too little. A Light task never comes near that size; a whole long
  document sent with every request can.
- The same 30 test repairs on Haiku 5.5 (18 Nov 2026): 27 like the expected repair, all six tag cases right, for
  less than one cent. Two slips the larger models did not make: an article left out ("moet veiligheidsbril dragen")
  and an informal "je" in a sentence that used "u". So: very cheap and usually right, not as careful as Haiku 4.5,
  Sonnet or Opus. You review the repair in the FuzzyFixer window before it is written.

## Not in this version

- A different provider per kind of task.
- A model per single feature.
