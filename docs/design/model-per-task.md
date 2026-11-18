# A model per kind of task

**Status:** design note, nothing built. Written 18 Nov 2026 after the 208 release, on Michael's idea of 17 Nov:
"do you really need Opus or even Sonnet on a FuzzyFixer fix? Wouldn't Haiku be just fine? ... Opus or even Fable
for AutoPrompt ... Opus and maybe even Sonnet for translation ... Haiku for fuzzy fixing and some other more
mechanical things." Decisions for Michael are at the end.

## What happens today

One provider and one model are chosen for everything. The choice is `AiSettings.SelectedProvider` plus one model
field per provider (`ClaudeModel`, `OpenAiModel`, ...), read through `AiSettings.GetSelectedModel()`
(`src/Supervertaler.Trados/Settings/AiSettings.cs`). There is no override per project, per prompt or per feature.
The pickers on the Batch Operations tab and in the Chat status bar change that same global choice.

Every feature that calls the AI uses it:

| Feature | What the work is | Typical size |
|---|---|---|
| Batch Translate | translation | large, many requests |
| Translate active segment (Alt+T) | translation | one segment plus context |
| Chat, QuickLauncher | open questions about the text | varies |
| AI Proofreader | judging a translation | large |
| FigureLens | reading a drawing (needs a model that reads images) | one image |
| AutoPrompt, the prompt itself | reads the whole document, writes the project prompt | large input, once per project |
| FuzzyFixer | changes a few words in an existing translation | one segment |
| Choosing articles from a large memory bank | picks from a list | small reply |
| AutoPrompt's two helpers (domain, gloss register) | classify, extract | small reply |
| Add term with abbreviation | extracts four fields from one segment | tiny |
| AutoTagger | puts tags into a translated segment | one segment |

SuperBench already takes its own models per run and is left out of this note.

So a translator who picks a strong model for translation pays that model's price for every mechanical step, and one
who picks a cheap model gets a weaker project prompt, although the prompt is written once and steers every segment
after it.

## What it costs today (list prices in `pricing.json`, USD per million tokens)

| Model | Input | Output |
|---|---|---|
| Claude Fable 5.2 | 8.00 | 40.00 |
| Claude Opus 5.5 | 4.00 | 20.00 |
| Claude Sonnet 5.5 | 2.00 | 10.00 |
| Claude Haiku 4.5 | 1.00 | 5.00 |

Measured on 17-18 Nov with Haiku 4.5: a FuzzyFixer repair with a 10,000-token memory bank cost $0.02 the first
time and $0.0024 once the bank was cached. On Sonnet 5.5 the same two requests cost about twice that, on Opus 5.5
four times, on Fable 5.2 eight times. The saving per repair is small in money; over a job with hundreds of fuzzy
matches, and for every user, it adds up, and the repair is no better for the dearer model if the cheap one does it
right. Whether it does is not yet measured (see "Evidence first").

## Proposal

Three roles. Each feature belongs to one.

| Role | Features | What it needs |
|---|---|---|
| **Translation** (the model chosen today) | Batch Translate, Alt+T, Chat, QuickLauncher, AI Proofreader, FigureLens | quality on the text itself |
| **Light tasks** | FuzzyFixer, choosing articles from the bank, AutoPrompt's two helpers, Add term with abbreviation, AutoTagger | to follow a narrow instruction exactly; speed and price |
| **Prompt writing** | AutoPrompt's main request | the strongest reasoning; runs once per project |

What the user sees, on the AI Settings tab only:

```
Provider:        [Claude            v]
Model:           [Claude Sonnet 5.5 v]      <- as today; used for translation and everything not listed below
Light tasks:     [Same as Model     v]      <- FuzzyFixer and other small mechanical requests
Prompt writing:  [Same as Model     v]      <- AutoPrompt
```

- **Both new lists default to "Same as Model".** Nobody's requests change model until they choose one. No new tick
  box, nothing new on the Batch Operations or Chat tabs. The two quick pickers keep changing "Model".
- **Same provider.** The two lists offer the models of the provider chosen above. One API key, one data-processing
  position for the client's text, and no way to send a segment to a second company by mistake. The choices are kept
  per provider, as the model already is, so switching provider and back keeps them.
- **Where it shows.** The FuzzyFixer window already names the model it used. Reports cards and the usage log record
  the model of each request, so cost per model is right without further work (`UsageRecord.model`,
  `UsageDimension.Model`). AutoPrompt's "this is a long document" question must quote the price of the prompt-writing
  model, not of "Model".
- **Custom OpenAI-compatible profiles and Ollama:** "Same as Model" only, in the first version. A profile is one
  endpoint with one model.

## What changes in the code

- **One resolver in place of about ten copies.** Today each call site repeats the same block: read
  `SelectedProvider`, call `GetSelectedModel()`, patch the model for a custom profile. This is worth fixing whether or
  not the feature is built: the copies have already drifted (the gloss register never uses a custom profile's
  endpoint or key). Proposed: `AiSettings.Resolve(AiTask task)` returning provider, model, endpoint and key source,
  with `enum AiTask { Translation, Light, PromptWriting }`; every call site asks it. Stateless: it reads settings and
  returns values.
- **Settings:** per provider, two optional model ids (empty means "Same as Model"). No migration needed: absent
  means today's behaviour.
- **A model that has gone.** A provider retires models. If the id stored for a role is no longer in that provider's
  list, the role uses "Model" and the Settings tab shows the list back on "Same as Model". Never a failed request
  because of a stale id, and never silently a different cheap model.
- **The curated model lists need a light model per provider.** Claude's has Sonnet 5.5, Opus 5.5 and Fable 5.2 and
  no Haiku; Haiku is reachable only through "Show all". Add the provider's current small model to each list, with a
  price in `pricing.json` checked against the provider's price page when this is built (not from memory).
- **Plugin Core is not changed** in the first version: selection lives in the Trados plugin's settings. Supervertaler
  for memoQ has its own settings; it can take the same three roles later.
- **Prompt cache.** A provider's cache belongs to one model. FuzzyFixer today reuses the bank Alt+T has just
  cached, because both send the same system prompt to the same model. On a different model it builds its own cache:
  the first repair pays full price for the bank, the following ones are cheap, as measured above. On a job with one
  or two repairs and many Alt+T requests, "Same as Model" can be the cheaper choice. The Help page should say so in
  one sentence.

## Evidence first

Not known: whether a small model repairs fuzzy matches as well as a large one when the repair is structural (a
clause added, word order changed, tags moved), and whether it places tags as reliably for AutoTagger. Before
recommending a light model to users, run one set of about 30 invented fuzzy repairs, with tags and without, through
the small, middle and large model of one provider and compare. A few cents of AI use; needs Michael's go.

## Tests when built

- The resolver: each role, "Same as Model", a role's id that is no longer listed (falls back, says so), a custom
  profile, an unknown provider.
- Each call site asks for the right role (read from the source, as `altt_no_front_test` does for log labels), so a
  new feature cannot quietly fall back to the wrong one.
- Failure paths: the light model's request fails (rate limit, model refused): the error names the model that was
  used; nothing is retried on a dearer model without the user knowing.
- Cost: the usage log prices a FuzzyFixer request by the light model and an AutoPrompt request by the prompt-writing
  model.

## Considered and not proposed

- **Choosing the light model automatically** (always the provider's cheapest). No setting at all, but it changes which
  model handles a user's text without their choice, before we have evidence that it is as good. Can be revisited as
  a default once the evidence is in.
- **A model per feature** (eleven lists). More than anyone asked for; three roles cover the difference that matters.
- **Roles on different providers** (Gemini for light tasks, Claude for translation). A second key, a second
  company receiving client text, and prompt caches that never overlap. Possible later if someone asks.
- **A model field in a prompt file.** Prompts are shared between translators and between Trados and memoQ; a model
  id inside one would break for whoever has another provider.

## Decisions for Michael

1. Two extra lists on the AI Settings tab, both defaulting to "Same as Model", or automatic choice with no
   setting? **DECIDED 18 Nov 2026 (Michael): two lists.**
2. Same provider only, or any provider per role? **DECIDED 18 Nov 2026 (Michael): same provider only.**
3. The split: is AI Proofreader "Translation" (proposed) and AutoTagger "Light tasks" (proposed)?
4. The names on the tab: "Light tasks" and "Prompt writing"?
5. Go for the small comparison run (about 30 invented repairs, three models, a few cents)?
