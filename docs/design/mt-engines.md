# Machine-translation engines: DeepL, Google and Microsoft in SuperBench (and maybe Batch Translate)

**Status:** Decisions taken 30 Oct 2026; SuperBench with MT to be built after API-key step 1 is merged into core;
Batch Translate deferred. No code yet. Michael's six answers are in §12.
**Source:** Michael's proposal of 30 Oct 2026 – classic MT engines as SuperBench contenders (firm), and possibly in
Batch Translate (undecided) – passed on by the orchestrator.
**Line numbers:** Trados as of `63c9715`, core as of `85af3b2`, memoQ `348a864`, Sidekick `0cf5d4b`, Workbench
`5002bb3`. Prices and API facts were read from the vendors' own pages on 30 Oct 2026 (§11).

---

## In short, for Michael

**SuperBench.** Today each of the three SuperBench slots can only hold an AI model. After this, a slot can also hold
**DeepL**, **Google Translate** or **Microsoft Translator**. You could run, for example, A = Claude with your prompt,
B = GPT with your prompt, C = DeepL, on the first 20 segments of your document, and the judge compares the three blind,
as now. What you would see:

- In the dialog, under a slot set to DeepL: *"DeepL gets no prompt or termbase – only the source text, and the
  neighbouring segments as context when document context is on."*
- In the results table and the report: DeepL's translations beside the AI's, with the tags in the same places, so the
  judge cannot tell from the tags which one is the MT engine.
- The judge marks DeepL against your prompt and termbase like every other candidate ("judge by the job"), and the
  report says plainly that DeepL never saw them.
- In the report's Contenders table: DeepL's cost and the number of characters it was charged for, instead of tokens,
  and the time it took (seconds, where an AI model takes a minute).

**What it costs.** These engines charge by the character of source text, not by the token. At list price, for extra
characters beyond what a plan includes:

| Engine | Price per million characters | Free allowance | 20 segments (about 3,000 characters) | A 50,000-word document (about 300,000 characters) |
|---|---|---|---|---|
| DeepL (Growth plan) | $27.50 (€22.00 in Europe) | Free "Developer" key: 1 million characters, once. Growth includes 1 million a month in its fee ($26 a month billed yearly; €29.75 monthly in Europe) | about $0.08 | about $8.25 |
| Google Cloud Translation | $20 | first 500,000 a month | about $0.06 | about $6 |
| Microsoft Translator | $10 | 2 million a month on the free tier | about $0.03 | about $3 |

Within a free allowance, or the characters a DeepL plan includes, you pay nothing extra. Supervertaler cannot see which
plan you have, so it shows the list price and says so.

**Batch Translate with DeepL – what it would and wouldn't do.** You chose "Later": decide after a few weeks of using
SuperBench with MT. Kept here so the facts are in front of you then. If you say yes, you could pick DeepL in the Batch
Translate tab instead of an AI model:

- **It would still:** keep repetitions consistent, write the backup TMX, refuse to overwrite segments with tracked changes
  nobody has accepted, save the project at the end, and log the cost.
- **It would not use:** your prompt, AutoPrompt, SuperMemory, the termbase, TM matches or document context. MT engines
  take none of these. Those controls would be greyed out with one line saying why.
- **Compared with RWS's own free plugins** (DeepL Translation Provider, Google Cloud Translation Provider, Microsoft
  Translator Provider, all by the Trados AppStore Team): theirs work inside Studio's own **Pre-translate Files** batch
  task and the Translation Results window, in project templates, and Google's supports glossaries and custom engines.
  Batch Translate would not do any of that. What Batch Translate would add: no second plugin to install and set up; one
  key shared with Sidekick (you type it once); DeepL's free key works, if DeepL's terms allow it (RWS's DeepL plugin
  says "DeepL API Free is not supported"); and Supervertaler's repetitions, backup and tracked-change protection.
- **memoQ:** no, in any case (your answer). Supervertaler for memoQ is itself an MT plugin, and memoQ already ships its
  own DeepL plugin in the same MT settings.

**ModernMT:** not added (your answer: DeepL, Google and Microsoft only). Its site says it "will sunset by the end of
2026", new registrations are closed, and existing keys stop working on 31 December 2026.

**A local engine (OPUS-MT):** not now (your answer); revisit if users ask. It is free and offline, but an older,
smaller kind of engine than DeepL. A free plugin that runs it in Trados already exists (OPUS-CAT). For "free and
offline" in SuperBench you can already pick a local AI model through Ollama, which, unlike OPUS-MT, can read your
prompt.

**Your keys.** Sidekick already keeps MT keys in `D:\Supervertaler\settings\api-keys.json`. Your file holds a `google`
key today, but none for DeepL or Microsoft. Trados would read the same entries, so a key entered in Sidekick or Trados
works in both. They follow the API-key lock plan exactly like the AI keys: once it is built, they are locked to your
Windows sign-in.

**When it can be built.** The shared part belongs in Plugin Core, where the API-key work (step 1) is under way now.
SuperBench with MT is built after that work is merged into core. Nothing here touches core until then.

Your six answers of 30 Oct 2026 are at the end (§12).

---

## 1. What exists today

### SuperBench (#107)

- **Core** `core\src\SuperBench.cs`: the run record (`Run`, `Contender` with `Provider`, `Model`, `Translations`,
  `Cost`, `InputTokens`, `OutputTokens`, `Elapsed`, `Error`), the blind labels, the judge's two prompts and the Markdown
  report. No CAT-tool types.
- **Trados** `src\Supervertaler.Trados\Core\SuperBenchRunner.cs`: one `BatchTranslator` run per contender, on a cloned
  `AiSettings` with `SetProviderAndModel(provider, model)` (`:102-103`). Translations are captured from
  `SegmentTranslated`, not written (`:108-117`). Cost comes from `UsageCapture`, which listens to the global
  `LlmClient.PromptCompleted` (`:243-279`). `EstimateCost` prices tokens with `TokenEstimator` (`:287-330`).
- **Trados** `src\Supervertaler.Trados\Controls\SuperBenchDialog.cs`: three slots (provider + model) and a judge.
  Providers are `LlmModels.AllProviderKeys` minus Custom (`:90`). Segments default to 20, at most the whole document
  (`:109-113`).
- **Trados** `AiAssistantViewPart.cs:13170` `PrepareSuperBenchInputs` gathers what a batch would send: segments with
  their tag maps, the prompt, filtered termbase terms, document context and SuperMemory.

### Batch Translate

- `AiAssistantViewPart.cs:10366` `OnBatchTranslateRequested` resolves the key, collects the scope, the terms, the
  prompt, the context, starts the backup TMX (`:10521`) and the SuperMemory selection, then runs
  `BatchTranslator.TranslateAsync` off the UI thread.
- `Core\BatchTranslator.cs` builds one system prompt per run and sends numbered segments in batches of 20. It parses the
  numbered reply, runs the reply check, and hands each result to `Deliver` (`:856`), which applies consistent
  repetitions and raises `SegmentTranslated` **synchronously**.
- `OnBatchSegmentTranslated` (`:10710`) writes through `SegmentWriteCheck.ProcessChecked`, which refuses segments
  holding unaccepted tracked changes. It rebuilds tags from the segment's own `TagMap` and adds the TMX backup entry
  (`:10783`). `SegmentOrigin.MarkEdited` labels every write "interactive", Michael's choice of 20 Oct 2026, with no
  setting (`Core\SegmentOrigin.cs:10-17`).
- Tags reach the model as `<t1>…</t1>` and `<t2/>` (`Core\SegmentTagHandler.cs:76-98`). A reply whose tags cannot be
  placed is written as plain text, never refused (`AiAssistantViewPart.cs:10742-10760`).

### Working MT code elsewhere (reference, not to be copied as is)

- **Sidekick** `lib\quicktrans.ahk:138-298`: Google v2 with the key in the query string, `format=text`; Microsoft v3.0
  with `Ocp-Apim-Subscription-Region` (default "global", from `settings.ini` `[QuickTrans] microsoft_region`); DeepL
  choosing `api-free.deepl.com` when the key ends in `:fx`; ModernMT; MyMemory. One text per request, no tags.
- **Workbench** `Supervertaler.py:66290-66583`: the same four plus Amazon, through the `deepl` Python package for DeepL.
  It maps DeepL targets EN → EN-US, EN-AU → EN-GB, PT → PT-PT, ZH-CN → ZH-HANS, ZH-TW → ZH-HANT (`:66380-66397`).
- **Shared keys** `<data root>\settings\api-keys.json`: Sidekick stores its MT keys under `deepl`, `google`,
  `microsoft`, `modernmt` (`lib\sharedkeys.ahk:12-15`). Michael's file today: `claude`, `deepseek`, `gemini`, `google`,
  `grok`, `openai`, `openrouter`. No `deepl` and no `microsoft`.

### Found along the way

Flagged rather than silently built on:

1. **SuperBench's cost capture can count someone else's run.** `UsageCapture` matches entries on provider, model and
   feature `BatchTranslate` (`SuperBenchRunner.cs:132, 258-262`). A real Batch Translate run on the same model while
   SuperBench runs is counted into that contender. Fix while touching it: tag each contender's run with its own id.
2. **Batch Translate passes language *names*, not codes.** `GetDocumentTargetLanguage` returns
   `Language.DisplayName`, e.g. "English (United Kingdom)" (`AiAssistantViewPart.cs:15493-15510`). Fine for a prompt,
   useless to an MT API. MT needs `Language.CultureInfo.Name` (`en-GB`) alongside, in `SuperBenchInputs` too.
3. **`ApiKeyStore`'s comment listed `google` as an accepted alias**, for Gemini (`core\src\ApiKeyStore.cs:20-22` at
   `85af3b2`), although the code no longer maps it and Sidekick stores the *Google Translate* key under `google`.
   **Already fixed** in core `9eb5379` (branch `api-key-lock-step1`), which drops `google` from that comment. Nothing
   left for the MT build; it only must never add `google` as an alias.
4. **Workbench names its MT keys differently:** `google_translate`, `microsoft_translate`,
   `microsoft_translate_region` (`Supervertaler.py:66298, 66426-66428`). The key lock plan's decision 1 (Workbench joins
   the shared keys) has to map these to `google` and `microsoft`, or Workbench and the plugins keep two Google keys. For
   the key-plan session.
5. **Both reference implementations return an error as the translation** – "[DeepL error: …]"
   (`Supervertaler.py:66417`). Harmless in a lookup window, but in a batch it would be written into the target. The
   engine layer below never returns error text as a translation.
6. **The Microsoft region is not in the shared key file.** It is in Sidekick's `settings.ini`. Trados needs its own (it
   is not a secret).

---

## 2. Which engines, and which API of each

| Engine | API | Why |
|---|---|---|
| **DeepL** | `POST /v2/translate`, JSON, header `Authorization: DeepL-Auth-Key <key>` | The only text API. Free keys use `https://api-free.deepl.com`, paid ones `https://api.deepl.com`. |
| **Google** | Cloud Translation **Basic (v2)**, `POST https://translation.googleapis.com/language/translate/v2?key=…` | Google: "All methods for the Cloud Translation - Basic API (v2) support API keys"; "Advanced API (v3) does not support API keys". v3 needs a service account or OAuth – too much to ask of a translator. The v2 reference (updated 26 Oct 2026) carries no deprecation notice. Same NMT model as v3. |
| **Microsoft** | Translator **v3.0**, `POST https://api.cognitive.microsofttranslator.com/translate?api-version=3.0` | Still the only text version ("Value must be `3.0`"). Header `Ocp-Apim-Subscription-Key`, plus `Ocp-Apim-Subscription-Region` for a regional resource ("optional when using a global translator resource"). |
| ModernMT | – | **Not added.** "ModernMT is evolving into Lara and will sunset by the end of 2026"; "New registrations on ModernMT.com are now closed"; keys work "until December 31, 2026". Lara can be weighed if users ask. |

**DeepL Free vs Pro.** DeepL no longer sells "API Free" and "API Pro" by those names. New customers get **Developer**
(free, "One-time credit of 1 million characters", one key) or **Growth** (paid, 1 million characters a month included).
Existing Free and Pro keys keep working. One rule covers all of them: the key decides the host. Sidekick's rule – a key
ending in `:fx` is a free key and goes to `api-free.deepl.com` – is kept. DeepL's pages no longer state the `:fx` suffix,
so the first build step checks it with a real Developer key (§9).

**Which DeepL model.** `model_type: "prefer_quality_optimized"` – DeepL's best model where the language pair has it,
the classic one otherwise, never an error. Fixed, no setting.

**Google's Translation LLM** (callable from v2 through a model path that names a Google Cloud project) is left out. It
needs a project id, and it is an AI model, which SuperBench already has plenty of.

**Microsoft Translator in Foundry.** Microsoft now sells Translator under "Azure Translator in Foundry Tools". The text
API and its prices are unchanged.

---

## 3. One engine layer, two uses

The layer has two halves, so that Batch Translate can reuse SuperBench's work without a second copy:

**(1) Core: talking to the engines.** `core\src\MachineTranslation.cs` – pure HTTP and JSON, no Trados or memoQ types,
so memoQ (or a memoQ SuperBench) could use it. One static class. No interface, no factory: three engines that differ in
request and reply shape, handled by a switch on the engine id and a few private functions each.

```
MachineTranslation.IsEngine("deepl")            -> true; never true for an AI provider id
MachineTranslation.DisplayName("google")        -> "Google Translate"
MachineTranslation.TranslateAsync(request, ct)  -> result
  request: engine, key, region (Microsoft), source code, target code (BCP 47, e.g. "en-GB"),
           texts (with <tN> placeholders), context (DeepL only, may be null)
  result:  translations (index-aligned; null where none), characters billed, billed-count exact or computed,
           the language codes actually sent, error kind + message (none on success)
MachineTranslation.Chunks(texts, engine)        -> request-sized groups (§3.3)
MachineTranslation.ListPricePerMillion(engine)  -> from pricing.json (§6)
```

Error kinds: `BadKey`, `QuotaUsedUp`, `UnsupportedLanguage`, `RateLimited`, `ServerError`, `Network`, `Malformed`.
`TranslateAsync` never throws for a provider's refusal and never puts error text in `translations`. It throws only
`OperationCanceledException`.

**(2) Trados: delivering the results.** `BatchTranslator` gains a second entry point, `TranslateWithEngineAsync`, beside
`TranslateAsync`. It takes the same segments and raises the same `Progress`, `SegmentTranslated` and `Completed` events
through the same `Deliver` (`BatchTranslator.cs:856`). So:

- **SuperBench (A)** calls it with a capture handler, as it calls `TranslateAsync` today. No AI settings are switched:
  `SetProviderAndModel("deepl", …)` would hand `LlmClient` a provider it does not know.
- **Batch Translate (B)**, deferred (decision 4, "Later"), would call it with the real write handler. Repetitions, the
  TMX backup, the tracked-change refusal and the end-of-run save then apply with no new code. Only the panel and the
  origin label would be new (§7). (A) is built so that this stays true.

MT ids never enter `LlmModels.AllProviderKeys`, so they never appear in AI Settings' provider list, the chat or
AutoPrompt. SuperBench adds them to its own dropdown.

### 3.1 Tags

Trados hands every engine the same `<t1>…</t1>` / `<t2/>` text it gives an AI. Each engine gets it in its own markup
mode, and the reply is turned back into `<tN>` before anything else sees it:

| Engine | Mode | What is sent |
|---|---|---|
| DeepL | `tag_handling: "xml"`, `tag_handling_version: "v2"` | The placeholders as they are (valid XML elements); the text between them XML-escaped. |
| Google | `format: "html"` | Placeholders turned into HTML (pairs as `<span>`, standalone as a void element). Google: XML markup gives "undefined" results. Text HTML-escaped. |
| Microsoft | `textType: "html"` | As Google ("Any HTML needs to be a well-formed, complete element"). |

- **Escaping uses the framework** (`SecurityElement.Escape`, `WebUtility.HtmlEncode`/`HtmlDecode`), never a homemade
  table. Google returns entities even for an apostrophe (Sidekick's comment at `quicktrans.ahk:276-278`).
- **`tag_handling_version: "v2"` explicitly.** DeepL: v1 is "currently the default, will become deprecated", v2 is the
  default only for accounts that first used tags after 1 Dec 2025. Naming it stops the behaviour changing under us when
  DeepL switches the default.
- **DeepL also gets** `split_sentences: "nonewlines"` (a segment is already one unit; a soft return must not split it)
  and `preserve_formatting: true` (segments are often table cells and headings; DeepL otherwise "fixes" their capitals
  and punctuation).
- **Shorter is cheaper.** Google and Microsoft bill every character sent, markup included (Google: "If you translate
  `<p>こんにちは</p>` to English, it counts as 12 characters"; Microsoft's FAQ counts "all markup"). `<t1>` costs 4
  characters, `<span id="t1">` costs 14. The notation used is the shortest one that survives (§9, step 1).
- **When the tags do not survive**, the existing rules apply unchanged. `ReplyCheck.TagDifference` logs it, and the
  writer falls back to plain text (`AiAssistantViewPart.cs:10742-10760`). Nothing new is refused.

**What the judge sees.** Every contender's output, MT included, reaches the judge in `<tN>` notation, so markup gives
no clue which candidate is the engine. The judge's "TAGS" criterion then measures MT tag handling exactly as it
measures the AI's.

### 3.2 Language codes

From `Language.CultureInfo.Name` (finding 2), mapped per engine, with the code actually sent kept in the result and
printed in the report ("English (Canada) sent to DeepL as EN-GB"):

| Trados | DeepL target | Google | Microsoft |
|---|---|---|---|
| en-US | EN-US | en | en |
| any other en-* | EN-GB | en | en |
| pt-BR | PT-BR | pt (Brazil is Google's default) | pt |
| any other pt-* | PT-PT | pt-PT, if Google's list has it | pt-pt |
| zh-CN, zh-SG, zh-Hans* | ZH-HANS | zh-CN | zh-Hans |
| zh-TW, zh-HK, zh-MO, zh-Hant* | ZH-HANT | zh-TW | zh-Hant |
| es-419, es-MX and other Latin American Spanish | ES-419, if DeepL's list has it; else ES | es | es |
| nb-NO, no | NB | no | nb |
| sr-Latn-*, sr-Cyrl-* | SR, if DeepL's list has it | sr | sr-Latn / sr-Cyrl |
| everything else | base code, upper case | base code | base code |

- **DeepL sources** carry no variant (EN, PT, ZH).
- **Plain EN or PT is never sent as a DeepL target.** DeepL's own language list shows `en` with
  `"usable_as_target": false`.
- **The cells marked "if … has it" are not guesses to ship.** DeepL's and Microsoft's language pages render their tables
  with JavaScript, and the fetch came back empty. Build step 1 (§9) records each engine's live language list (DeepL
  `GET /v3/languages`, Google `GET …/v2/languages`, Microsoft `GET /languages?api-version=3.0&scope=translation`,
  which needs no key) as test fixtures. The table is settled from those.
- **No language list is fetched at run time.** An unsupported pair is reported from the engine's own refusal on the
  first request (§5), which costs nothing.

### 3.3 Request size

- **DeepL:** by size – "Total request size 128 KiB", no count limit.
- **Google v2:** "The maximum number of strings is 128."
- **Microsoft:** at most 1,000 texts and 50,000 characters per request.

`Chunks` fills each request up to the smaller of 50 segments and the engine's limit. One timeout per request (60 s)
and one HTTP set-up shared with `LlmClient` (proxy, TLS).

### 3.4 DeepL's extras: context, glossary, custom instructions

- **`context`** – text DeepL reads but does not translate, and "Characters included in the context parameter will not be
  counted toward billing." When the run has document context on, DeepL gets the neighbouring source segments (up to
  about 2,000 characters before the chunk), for free. Google and Microsoft have nothing equivalent. **Decided**
  (decision 2, "Neighbours as context").
- **Glossary** (`glossary_id`): would carry the termbase to DeepL. **Not for now** (decision 2); later only if SuperBench
  shows DeepL losing mainly on terminology. A glossary has to be created and deleted through the API per language pair,
  Developer keys may hold only one, and it does nothing for Google or Microsoft.
- **`custom_instructions`**: "Up to 10 custom instructions … each with a maximum of 300 characters" – too small to carry
  a Supervertaler prompt. Not used.
- **`formality`**: not used. No setting until someone asks.

---

## 4. Fairness in SuperBench

What each contender gets, stated in the dialog and the report, so nobody misreads the result:

| | AI contender | MT contender |
|---|---|---|
| Source segments, with tags | yes | yes |
| Selected prompt / custom system prompt | yes | no |
| Termbase terms that occur | yes | no |
| TM matches (#116), list markers (#109) | yes, if on | no |
| Document context | yes, if on | DeepL: neighbouring segments as `context`, if on (decision 2); Google, Microsoft: no |
| SuperMemory | yes, if on | no |

- **The report's Settings line** gains, e.g.: "MT engines got no prompt or termbase; DeepL got the neighbouring
  segments as context." The Contenders table names the engine as, e.g., "DeepL (machine translation)" with its
  characters instead of tokens.
- **The judge's yardstick – decided (decision 3, "Yes, judge by the job").** Today the judge reads "TRANSLATION
  INSTRUCTIONS THE CANDIDATES WERE GIVEN" and "APPROVED TERMS THE CANDIDATES WERE GIVEN" (`SuperBench.cs:103, 109`).
  With an MT contender that heading would be false. Saying which candidate did *not* get them would unblind the judge.
  So the headings become "THE PROJECT'S TRANSLATION INSTRUCTIONS" and "THE PROJECT'S APPROVED TERMS". Every candidate
  is judged against what the project needs, which is the question Michael asked – "is AI with my prompt better than
  DeepL *on my text*?". The report, not the judge, says plainly that the MT contenders never saw them.
- **No change to blindness.** Labels stay shuffled. The one thing a judge might still notice is DeepL's style, which
  nothing can hide.

---

## 5. Errors, one contender at a time

Each contender fails on its own. The other contenders and the judge carry on, as today: the failed column says why, and
the judge sees "(no translation returned)" for its segments (`SuperBench.cs:124`).

| What happened | DeepL | Google | Microsoft | Message (dialog and report) | What the run does |
|---|---|---|---|---|---|
| Key wrong or missing | 403 | 400/403 with "API key not valid" (to confirm, §9) | 401000 | "DeepL refused the key. Check it in Settings → AI Settings → Machine translation." For DeepL also: "A free key ends in :fx." For Google also: "Is the Cloud Translation API enabled for this key's project?" For Microsoft also: "Is the region right?" | Stop this contender at once. |
| Characters used up | 456 | 403 "Daily Limit Exceeded" | 403001 | "DeepL: this key's character allowance is used up." | Stop this contender at once – never send the remaining chunks only to have each refused. |
| Language pair not offered | 400 "Value for 'target_lang' not supported." | 400 (to confirm) | 400019, 400023, 400036 | "DeepL does not translate English (United Kingdom) → Welsh." | Stop this contender. |
| Too many requests | 429, 529 | 403 "User Rate Limit Exceeded" | 429000–429002 | none, unless it persists | Wait (honouring `Retry-After`, else 2, 4, 8 s) and retry, at most three times, then fail that chunk. |
| Server or network trouble | 500, 503, 504 | 5xx | 5xx | "DeepL did not answer (server error 503)." | Retry once, then fail that chunk and go on with the next. |
| Reply has a different number of translations than texts sent | – | – | – | logged | Fail that chunk. Never pair translations with segments by guesswork. |
| Cancelled | | | | "Cancelled." | Stop. What came back so far stays in the table, as today. |

Messages use the engine's display name and never echo the key. A missing key gets today's wording ("No API key set for
DeepL…"); an unreadable one (after the key lock, §8) gets the key plan's M2.

---

## 6. Cost: estimate, report, Reports tab

**Prices live in the shared `pricing.json`**, in a new `"mt"` section, US dollars per million characters:

```json
"mt": { "deepl": 27.50, "google": 20.00, "microsoft": 10.00 }
```

- **The same override rule as the AI prices:** the user's `pricing.json` first, then the bundled one.
- **Workbench reads the same file.** It must ignore an unknown top-level key; to be checked before release.
- **US dollars throughout**, as every other cost in Supervertaler. DeepL charges European customers €22.00. The report
  says "list price, US dollars".
- **The rates are DeepL's Growth price for characters beyond the plan, Google's above its 500,000 free a month, and
  Microsoft's S1 pay-as-you-go** (§11).
- **An MT id must never reach `TokenEstimator.CostCeiling`.** It would price characters as tokens at the dearest AI
  rate. A test pins this.

**Before the run (estimate).** `SuperBenchRunner.EstimateCost` adds, per MT contender: characters as they would be sent
(source text plus converted tags) × list price. The line reads, e.g., "estimated cost about $0.41 for the three runs and
the judge (DeepL at list price; free within your plan or free allowance)".

**After the run (report and dialog).** Characters actually billed × list price:

- **DeepL:** exact – the request sets `show_billed_characters: true`, and DeepL returns `billed_characters`.
- **Microsoft:** exact – the `X-metered-usage` response header ("the number of characters for which the user is
  charged").
- **Google:** computed by Google's published rule (every character sent, markup and whitespace included). Labelled
  "computed".

`SuperBench.Contender` gains `Characters` (core). The report's column becomes "Tokens in / out · characters" and shows
whichever applies.

**Reports tab and usage log.** The MT run fires one aggregated `PromptLogEntry`, like a batch:

- Provider is the engine id, Model is the engine's model ("prefer_quality_optimized" for DeepL, "nmt" for Google and
  Microsoft), and Cost is the billed cost.
- `PromptLogEntry` gains `Characters` (core), so the Reports tab shows "3,104 characters" instead of "0 tokens".
- The monthly AI budget's pre-flight counts MT costs too, through the same log.

---

## 7. (B) Batch Translate with an MT engine

**Deferred (decision 4, "Later"):** Michael decides after a few weeks of using SuperBench with MT. This section is
kept so that decision can be made with the facts and costs in front of him. Most of it would already exist after (A).

### What stops applying, and how the panel says so

| Feature | With an MT engine |
|---|---|
| Prompt selection, AutoPrompt, Preview prompt | Greyed out |
| Custom system prompt, Clipboard Mode | Not used / greyed out |
| Termbase injection (#102), TM matches (#116) | Not sent |
| SuperMemory, document context | Not sent (DeepL: context, per decision 2) |
| List markers (#109) | Not sent (the prompt-only sentinel has no MT equivalent) |
| `[[TC]]` comments | Never produced |
| Reply check (output contract) | Not needed (an MT engine does not add notes); the tag check still logs |
| "Retry segments left empty" | Applies to chunks that failed on a temporary error only |

One line above the greyed controls: *"DeepL translates the source text only – your prompt, termbase and SuperMemory
are not used."*

### What still applies, unchanged

Scopes and the segment limit. Consistent repetitions (`Deliver`). The backup TMX, with the engine named in each unit's
note. The refusal of segments with unaccepted tracked changes (`SegmentWriteCheck`). The project save at the end. The
cost log and the budget pre-flight.

### Origin label (decision 5 – not asked; deferred with decision 4)

Since 20 Oct 2026 every Supervertaler write is labelled "interactive", Michael's choice for AI output too, with no
setting. For raw output of an MT engine the case is different:

- **What Studio's own plugins do:** they label it "nmt" with the engine's name as the origin system.
- **What agencies rely on:** they filter on that label, and often pay less for MT post-editing.
- **What happens once the translator edits the segment:** Studio relabels it "interactive", with "nmt" kept as the
  previous origin – exactly as it does after RWS's DeepL plugin.

So the proposal, to be put to Michael if he says yes to (B), is "nmt" plus the engine name, for MT-engine writes only.
AI writes stay "interactive". This needs one
live probe, like consistent repetitions' "auto-propagated" probe: does Studio keep "nmt" when a plugin sets it after a
`ProcessSegmentPair` write? Studio labels such writes "interactive" by itself (`SegmentOrigin.cs:36-44`).

### Compared with RWS's free MT plugins

| | RWS DeepL / Google / Microsoft providers | Batch Translate + engine |
|---|---|---|
| Studio's Pre-translate Files batch task, TM first then MT | yes | no – run Pre-translate for the TM, then Batch Translate "empty segments only" |
| Translation Results window, per segment as you move | yes | no (Alt+T would not offer MT) |
| Project templates, several files, Trados Cloud | yes | open document only |
| Google glossaries, custom (AutoML) engines | yes | no |
| DeepL free (Developer) key | "DeepL API Free is not supported" | yes, subject to DeepL's terms (below) |
| Separate plugin to install and configure | yes, one per engine | no |
| Key shared with Sidekick, locked to the computer | no | yes |
| Consistent repetitions, backup TMX, tracked-change protection | no | yes |
| Cost in Supervertaler's log and budget | no | yes |

### What it costs

**To build**, on top of (A):

- the panel changes;
- the origin label and its live probe;
- the TMX note;
- one Help page;
- one Studio-slot check.

That is roughly one worker session plus one live check, against perhaps three sessions for (A).

**To keep**, every year:

- **API changes**, mostly DeepL's. In the past year DeepL replaced its plans, added tag handling v2 (default to change),
  moved its language list to v3, and added style rules and custom instructions. Google v2 and Microsoft v3.0 have been
  stable for years. ModernMT shows the other risk: a whole engine closing.
- **Support questions**, mostly about keys. Google's console (project, billing, enabling the API, key restrictions) and
  Azure (resource, region) confuse people. With (A) only SuperBench users meet this; with (B), anyone who would
  otherwise ask RWS.

### DeepL's terms for free keys

DeepL lists "CAT tool integration" under Growth only, and RWS's DeepL plugin refuses free keys. Whether a Developer key
may be used from a CAT-tool plugin is not stated on the pages read. SuperBench is the use the Developer plan exists for
("Test out the API for free"). Before Batch Translate advertises free keys (B), DeepL's API terms are read for this.

### memoQ

Supervertaler for memoQ is itself an MT plugin (`src\Supervertaler.MemoQ\SupervertalerMTEngine.cs`). memoQ ships its
own DeepL MT plugin – Supervertaler's prompt editor already recognises rows it filled ("DeepL MT Plugin",
`src\Supervertaler.PromptEditor\MtMarks.cs:126`). Its Addins folder also has Lara, Globalese and NiuTrans plugins
(`C:\Program Files\memoQ\memoQ-12\Addins\`). A memoQ user picks DeepL in the same MT settings where they pick
Supervertaler, and memoQ's Pre-translate uses it. **(B) would add nothing there; Michael: no (decision 4).** The core
layer would let a future memoQ SuperBench include MT engines with no further engine work.

---

## 8. Keys

- **Where entered:** Trados Settings → AI Settings gains a "Machine translation" group – DeepL key, Google Cloud
  Translation key, Microsoft Translator key and region – each with a Test button. The Test button calls DeepL
  `GET /v2/usage` (free; shows characters used and left), or translates one word for Google and Microsoft. Sidekick's
  own window is unchanged.
- **Where stored:** through core's `ApiKeyStore`, under the ids already in use – `deepl`, `google`, `microsoft`. A key
  entered in Sidekick appears in Trados and the other way round. No new file, no new format.
- **The region** (Microsoft) is a Trados setting, empty meaning a global resource (no region header). It is not a
  secret and stays out of the key file. Sidekick keeps its own copy in `settings.ini`. Sharing it is not worth a new
  shared file.
- **The lock plan applies unchanged.** `docs\design\api-key-protection.md` (core branch `api-key-protection`) already
  lists Sidekick's MT keys in `api-keys.json` and says every product keeps entries it does not know ("Trados must not
  drop Sidekick's DeepL key", §3). MT keys get M1/M2 when unreadable and the locked file after step 2, like any other.
  The MT build therefore comes after key step 1 is in core, and reads keys only through `ApiKeyStore`, resolved once
  per run, never cached.
- **One thing for the key-plan session** (finding 4): Workbench's `google_translate` and `microsoft_translate` must map
  to `google` and `microsoft` when Workbench joins the shared keys. (The `google` alias comment, finding 3, is already
  fixed in core `9eb5379`.)

---

## 9. Build order and tests

**Step 0 – this note, Michael's decisions.** Done: decisions taken 30 Oct 2026 (§12). The orchestrator sets the build
timing.

**Step 1 – a small live spike, before any product code.** With Michael's go and three keys: a free DeepL Developer
key, a free Microsoft F0 resource, and his existing Google key (if it is a Cloud Translation key).

- Send 30 tagged segments from the test fixtures, in each candidate notation, to each engine. Keep the notation that
  loses the fewest tags, and the shortest among equals.
- Record each engine's language list and error replies (bad key, unsupported pair) as offline fixtures.
- Confirm DeepL's `:fx` rule and whether DeepL bills tags (`show_billed_characters`).

This costs nothing within the free allowances. The script is run as `powershell -NoProfile -File`, keeps keys out of
its text, and lives in the gitignored `.dev\`.

**Step 2 – core, after API-key step 1 is merged into core and the core slot is free:**

- `MachineTranslation.cs`;
- the tag conversions, and DeepL's `context` from the neighbouring segments (decision 2);
- the `"mt"` price section;
- `PromptLogEntry.Characters` and `SuperBench.Contender.Characters`;
- the judge headings (decision 3).

A memoQ handoff note says nothing changes for memoQ's behaviour.

**Step 3 – Trados (A):**

- the AI Settings MT group;
- `BatchTranslator.TranslateWithEngineAsync`;
- language codes in `SuperBenchInputs`;
- the SuperBench dialog, runner, estimate and report;
- the `UsageCapture` fix (finding 1);
- a Help page.

Then one Studio-slot live check: SuperBench with DeepL on the example project.

**Step 4 – Trados (B): deferred** (decision 4, "Later"). Built only if Michael says yes after a few weeks of SuperBench
with MT (§7); decision 5 is put to him then.

### Offline tests (core `tests\` and Trados `.dev\`; no network, a fake HTTP handler)

- **Tags:** a round trip per engine and notation for nested pairs, standalone tags, line-break tags, and text holding
  `&`, `<`, `>`, quotes and an apostrophe. Google's entity-escaped replies are decoded. A reply that loses a tag is
  logged, not refused.
- **Language codes:** the full table above, against the recorded live lists. An unknown culture falls back to the base
  code. Plain EN or PT is never sent to DeepL as a target.
- **Every failure in §5, per engine:** bad key; quota used up mid-run, after which no further chunk is sent; unsupported
  pair; 429 with and without `Retry-After`, with the retry cap honoured; 5xx; timeout; malformed JSON; a reply with one
  translation fewer than sent (that chunk fails, nothing is misaligned); an empty translation. Cancel during a request,
  and between chunks.
- **No error text ever lands in a translation:** an assertion over every failure case.
- **SuperBench:** one MT contender failing leaves the other two and the judge intact. The report shows characters and
  cost for MT, tokens for AI. The estimate uses characters × list price. An MT id never reaches `CostCeiling`. A
  concurrent Batch Translate on the same provider is not counted into a contender (finding 1).
- **Keys:** read through `ApiKeyStore` only. A missing key gets today's message. Tests use a temporary data root and
  locked-file path, never the user's.
- **Repetitions with MT (B):** the existing repetition tests rerun through `TranslateWithEngineAsync`.

### Scale

**SuperBench** samples 20 segments by default, at most the whole document. At 20 segments an MT contender is one
request. **Batch Translate** takes a whole document. Tested offline at 20 and 2,000 segments; designed for 10,000
segments (about 1.5 million characters) in one run:

- **Requests:** 200 at 50 segments each, written as each returns, as today.
- **Memory:** only the current request's texts.
- **Quota:** a run that runs out of characters stops after one refused request, not 200.
- **Rate limits:** Microsoft's F0 tier allows "2 million characters per hour", so a large document on a free Microsoft
  key can hit the hourly limit. That arrives as 429 and is retried within the cap, then reported.

### Engineering checklist (global CLAUDE.md)

- **No dependency injection:** one static class; no engine interface or factory.
- **Nothing hand-rolled:** escaping and JSON through the framework.
- **No new state:** no cached keys, language lists or "last engine". The price table is read like the AI one.
- **Failure-path leaks:** the `UsageCapture` subscription and the TMX backup are already released in `finally`;
  `TranslateWithEngineAsync` follows the same pattern.
- **Races:** finding 1 is fixed; MT runs off the UI thread and writes through the existing synchronous handler.
- **No premature settings:** the only new setting is the Microsoft region, which a regional key needs.

---

## 10. A local engine (OPUS-MT): not now

- **What it is:** Helsinki-NLP's OPUS-MT – one small neural model per language direction (Marian NMT), runnable on an
  ordinary CPU.
- **Licences:** the code is MIT. The models are "CC-BY 4.0" per the project README; the Dutch→English model card on
  Hugging Face says Apache 2.0 – one to check if it is ever shipped.
- **Dutch:** a Dutch→English model exists. The only published score for it is on short Tatoeba sentences, which the
  project itself calls "too optimistic".
- **Still maintained, slowly:** README-only commits through 2026; the Tatoeba-Challenge repository had commits in
  September 2026; newer "transformer-big" models date from August 2024.
- **What running it alongside Trados would take:** a local translation service (Python with `transformers<5`, or a
  Marian server). The ready-made answer is **OPUS-CAT** (Tommi Nieminen): a free Windows MT engine with a free Trados
  plugin on the RWS AppStore ("completely free, secure, and confidential neural machine translation for over a thousand
  language pairs"), including fine-tuning on the job's own material.
- **Quality:** no published comparison of OPUS-MT against DeepL or Google was found. The OPUS dashboard compares it
  only with open models such as NLLB. These are 2019–2024 neural models of the classic kind, far smaller than the
  commercial engines, so they should not be expected to beat DeepL. Against the local AI models Supervertaler already reaches
  through Ollama, they lose the prompt, termbase and context. In return they need no GPU.
- **Decided (decision 6, "No, not now"):**
  - Build nothing now; revisit if users ask.
  - For "free and offline" in SuperBench, an Ollama model is already a contender.
  - If users ask for OPUS-MT, the cheapest route is a SuperBench contender that calls a running OPUS-CAT engine, the
    way Ollama is called. That only works if OPUS-CAT's local service is open to other programs, which has not been
    checked.

---

## 11. Sources (read 30 Oct 2026)

- **DeepL plans and prices:** https://www.deepl.com/en/pro-api, API tab (Developer: free, one-time 1M characters;
  Growth: $26.00/month billed annually with 12M characters a year, $27.50 per extra million; United Kingdom (EUR):
  €23.80 annual or €29.75 monthly with 1M a month, €22.00 per extra million).
- **DeepL API:** https://developers.deepl.com/docs/api-reference/translate (context not billed, `tag_handling_version`,
  `model_type`, `custom_instructions`, 128 KiB request); …/docs/xml-and-html-handling/xml;
  …/docs/best-practices/error-handling (403, 456, 429/529); …/docs/resources/usage-limits (billing by source length);
  …/docs/languages/using-the-languages-api.
- **Google:** https://cloud.google.com/translate/pricing (NMT $20 per million over the first 500,000 a month, "applied
  as $10 credit every month"; charged characters include markup); https://cloud.google.com/translate/docs/authentication
  (v2 keys, v3 none); https://cloud.google.com/translate/docs/reference/rest/v2/translate (`format`, 128 strings);
  https://cloud.google.com/translate/troubleshooting.
- **Microsoft:** https://azure.microsoft.com/en-us/pricing/details/translator/ (F0 2M characters a month; S1 standard
  text translation $10 per million);
  https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/v3/translate (`textType`,
  `X-metered-usage`); …/text-translation/reference/authentication (region header); …/service-limits (1,000 elements,
  50,000 characters, hourly limits); …/text-translation/reference/status-response-codes;
  https://learn.microsoft.com/azure/ai-services/translator/translator-faq (markup counted).
- **ModernMT:** https://www.modernmt.com/pricing (sunset), https://www.modernmt.com/api/.
- **RWS AppStore** (https://appstore.rws.com/, searches "DeepL", "Google", "Microsoft", "OPUS"): DeepL Translation
  Provider, Google Cloud Translation Provider, Microsoft Translator Provider (all free, Trados AppStore Team); OPUS-CAT
  (NMT).
- **OPUS-MT:** https://github.com/Helsinki-NLP/Opus-MT, https://huggingface.co/Helsinki-NLP/opus-mt-nl-en,
  https://github.com/Helsinki-NLP/Tatoeba-Challenge, https://opus.nlpl.eu/dashboard/.

Not confirmed, and settled by build step 1: the DeepL, Google and Microsoft language tables; DeepL's `:fx` rule; whether
DeepL bills tags; Google's bad-key and unsupported-pair replies.

---

## 12. Decisions taken (30 Oct 2026)

Put to Michael one at a time by the orchestrator, each with its recommendation; his answers in his words.

1. **Which engines go into SuperBench?** – **"Yes, all three".**
   - DeepL, Google Translate and Microsoft Translator. No ModernMT, which closes at the end of 2026.
   - (Recommended: all three. They share one layer, so the second and third cost little, and Microsoft's free
     2 million characters a month make it the cheapest to try.)
2. **What DeepL may use in SuperBench.** – **"Neighbours as context".**
   - Option (b): the neighbouring segments as context when document context is on. DeepL does not charge for this.
   - No glossary for now. Option (c), the termbase as a DeepL glossary, only later, and only if SuperBench shows DeepL
     losing mainly on terminology.
   - (Options were (a) source text only, (b) and (c). Recommended: (b) now, (c) later.)
3. **Should the judge mark DeepL against your prompt and termbase, which DeepL never saw?** – **"Yes, judge by the
   job".**
   - The judge marks MT contenders against the project's prompt and termbase, like every other candidate. The report
     says plainly that they never saw them (§4).
4. **Batch Translate with MT engines: yes, no, or later?** – **"Later".**
   - Decided after a few weeks of using SuperBench with MT. §7 stays in this note for that decision.
   - memoQ: no.
   - (Recommended: later. If DeepL wins on some of his jobs, Batch Translate with DeepL is worth its upkeep. If not,
     RWS's free plugins already do plain MT better inside Studio's own Pre-translate.)
5. **Only if 4 is yes: how should a segment DeepL wrote be labelled?** – **Not asked: deferred with decision 4.**
   - To be put to Michael if he says yes to (B). Recommendation then: machine translation ("nmt") with the engine's
     name, as Studio's own DeepL plugin does, after one live probe. AI writes stay "interactive", his choice of
     20 October.
6. **A local MT engine (OPUS-MT): build it?** – **"No, not now".**
   - Revisit if users ask. Ollama already gives SuperBench a free, offline contender that reads the prompt.
