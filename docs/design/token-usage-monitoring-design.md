# Token Usage Monitoring System — Design & Analysis

**For:** Supervertaler for Trados (primary), portable to Supervertaler Workbench
**Author:** Claude (overnight investigation for Michael), 2026-06-18
**Status:** Design proposal for review — no code changed.

---

## 0. The ask

**Daniel McCosh (FAU), 2026-06-13, "Token management":**

> "I have been asked to monitor our token usage (which we can't do through GWDG) so that we can eventually move to our own university-hosted models when they open up capacity. I might even be able to access Claude models through this service.
> Is it feasible for Supervertaler to log input and output tokens used (also used for quick launcher functions and batch tasks and prompt generation) persistently into a log file for the custom models we have added?"

So the **minimum concrete request** is: **persistently log input/output tokens per call to a file**, covering **all flows** (quick launcher, batch, prompt generation), **including custom models**.

This document designs that, then generalises it into a token-usage monitoring system genuinely useful to freelance translators and LSPs, and finally lays out the Workbench port.

---

## 1. Executive summary

**The good news: ~80% of the plumbing already exists.** Both products already:

- capture **real, provider-reported token usage** (input / output / cache-read / cache-write) at a **single choke point** per product, and
- already **tag every call with the exact task types Daniel named** (Trados has a `PromptLogFeature` enum: Chat, Translate, BatchTranslate, Proofread, **QuickLauncher**, **PromptGeneration**, ConnectionTest, SuperMemory).

What's missing is **persistence, attribution, and reporting** — not capture. Today the Trados Reports tab holds these entries **in memory only** (max 500, cleared on session end); Workbench captures usage for chat but **not for batch translation**.

**So this is mostly a "persist-and-attribute what we already compute" project**, deliverable in independently-shippable phases:

- **Phase 1 (= Daniel's literal ask):** write the existing per-call usage entries to an append-only **JSONL log file**, complete the token capture for the two providers that currently fall back to estimates (Gemini, Ollama), and stamp each record with project/file. Ships for Trados.
- **Phase 2:** a queryable **SQLite ledger** + an in-app **Usage & Costs** report + **CSV/XLSX export**.
- **Phase 3:** budgets/alerts, pre-flight cost preview, per-custom-model pricing.
- **Phase 4:** port to Workbench with an **identical schema** so reports/exports are interchangeable.

---

## 2. What already exists (the foundation)

### 2.1 Supervertaler for Trados (C#)

| Capability | Where | Notes |
|---|---|---|
| Per-call token usage object | `Core/LlmClient.cs:24-40` (`ApiUsage`: RegularInput / CacheRead / CacheWrite / Output), exposed as `LlmClient.LastUsage` (`:87`) | Single object after every call |
| Actual usage parsed — Claude | `Core/LlmClient.cs:1103-1124` | input/output + `cache_creation`/`cache_read` |
| Actual usage parsed — OpenAI-shape (OpenAI, Grok, Mistral, DeepSeek, OpenRouter, **Custom**) | `Core/LlmClient.cs:1136-1176` | prompt/completion + cached tokens |
| **Gap** — Gemini usage | `Core/LlmClient.cs:1190-1200` | `usageMetadata` **not parsed** → falls back to chars/4 |
| **Gap** — Ollama usage | `Core/LlmClient.cs:1202-1212` | `prompt_eval_count`/`eval_count` **not parsed** |
| Token estimation (fallback) | `Core/TokenEstimator.cs` (`EstimateTokens` = `(len+3)/4`) | Always available |
| Pricing table + cache-aware cost | `Core/TokenEstimator.cs:14-49` (Pricing), `:127-175` (`ComputeActualCost`, per-provider cache multipliers) | Hardcoded, "March 2026" |
| Dual-track record (estimated **and** actual) | `Models/PromptLogEntry.cs:23-69` (`HasActualUsage`, `IsCostKnown`) | Already distinguishes "free" vs "unknown" |
| **Task-type taxonomy** | `Models/PromptLogEntry.cs:7` `enum PromptLogFeature { Chat, Translate, BatchTranslate, Proofread, QuickLauncher, PromptGeneration, ConnectionTest, SuperMemory }` | **Covers every flow Daniel named** |
| **The choke point**: event after every call | `Core/LlmClient.cs:49-55` `static event EventHandler<PromptLogEntry> PromptCompleted` | Fires for all features |
| Batch aggregation | `Core/BatchTranslator.cs:132-264, 350-386` | Sums tokens across batches, fires **one** consolidated entry; drops to estimate if any sub-batch lacked actual usage |
| Reports UI (ephemeral) | `Controls/ReportsControl.cs:511-665` | In-memory, 500-cap FIFO, **not persisted**, cleared on session end |
| Opt-in toggle | `Settings/AiSettings.cs:312-317` `LogPromptsToReports` | Currently gates the Reports tab |
| Custom model config | `Settings/AiSettings.cs:407-421` `CustomOpenAiProfile { Name, Endpoint, Model, ApiKey }` | **No** per-model price or context window |
| Data dir / persistence | `Settings/UserDataPath.cs` (`<Root>/trados/settings/…`); SQLite via `Microsoft.Data.Sqlite` (termbase DB); per-project JSON `ProjectSettings.cs` keyed by **hash of `.sdlproj`** | Mature; usage store would slot in here |
| Existing telemetry (unrelated) | `Core/UsageStatistics.cs` | Anonymous install ping — **no** tokens/cost/content. Keep separate. |

**Not present:** persistent usage log; project/file/client attribution on log entries; export; budgets; per-custom-model pricing.

### 2.2 Supervertaler Workbench (Python)

| Capability | Where | Notes |
|---|---|---|
| Per-call usage (text + usage dict) | `modules/llm_clients.py` `translate_with_usage()` (`:808-855`); per-provider parse OpenAI `:941-970`, Claude `:1037-1056`, Gemini `:1084-1101` | Gemini usage **is** parsed here (Trados isn't — note the asymmetry) |
| **Gap** — Ollama usage | `modules/llm_clients.py:1491-1684` | only `eval_count` (output), no input |
| Pricing + cost | `modules/llm_pricing.py` (`PRICING`, `estimate_cost()`) | Separate table from Trados |
| Chat usage captured | `modules/chat_backend.py:210-226` | stored in chat history metadata only |
| **Gap** — batch usage **not** captured | `Supervertaler.py` `_translate_batch_with_llm()` (`:7456-7619`) calls `translate()`, **not** `translate_with_usage()` | The main spend goes unrecorded |
| SQLite DB | `modules/database_manager.py` (`supervertaler.db`) | No usage table |
| Telemetry (unrelated) | `modules/usage_statistics.py` | Anonymous ping only |

---

## 3. Design principles

1. **One write path.** Instrument the **single existing choke point** (`PromptCompleted` in Trados; a `record_usage()` sink in Workbench), never N call sites. Every flow already routes through it.
2. **Actual-first, estimate-fallback, always labelled.** Store provider-reported tokens when present; fall back to chars/4 otherwise; **record which** (`source = actual | estimated`). Daniel must be able to trust the custom-model numbers, so completing Gemini/Ollama capture matters.
3. **Attribute every record.** A bare token count is half-useful. Stamp each with: timestamp, product, task type, provider, model (+ custom profile name + endpoint), project, file, language pair, segment count, tokens (regular/cacheRead/cacheWrite/out), cost (+ `cost_known`), duration, success/error, app version.
4. **Local-first and private.** Append-only file + local SQLite in the user-data dir. **No network.** The ledger stores **metadata only — never prompt/response text** (that keeps it lean, shareable, and safe to hand to an institution's monitoring team). This is deliberately distinct from the existing (heavier, opt-in) Reports-tab prompt log.
5. **Custom-model-aware.** Custom OpenAI-compatible endpoints usually **do** return `usage` → capture it. Where a self-hosted server doesn't, fall back to estimate and flag it. Add optional **per-custom-model pricing** so cost is computable for university-hosted models — and if it's left blank, tokens are still logged (Daniel's exact minimum).
6. **Crash-safe and cheap.** One appended line per call; aggregation happens lazily at report time.

---

## 4. The data model (shared across both products)

One record per LLM call (or per aggregated batch). **JSONL** is the durable artifact; **SQLite** mirrors it for in-app queries and is rebuildable from the JSONL.

```jsonc
// one line per call in usage-YYYY-MM.jsonl
{
  "id": "9f1c…",                  // uuid
  "ts": "2026-06-18T09:14:22Z",   // UTC ISO-8601
  "product": "trados",            // trados | workbench
  "app_version": "4.20.55",
  "task": "BatchTranslate",       // PromptLogFeature value
  "provider": "custom_openai",    // openai | claude | gemini | … | ollama | custom_openai
  "model": "claude-sonnet-4-6",   // model id sent to the endpoint
  "profile": "FAU GWDG",          // custom-profile name, if any
  "endpoint": "https://…/v1",     // for custom/self-hosted
  "project": "EP3456789A1",       // human name
  "project_key": "a83f…",         // hash of .sdlproj (stable join key)
  "file": "claims.docx",
  "client": "Brants & Patents",   // optional, see §8
  "src_lang": "en", "tgt_lang": "nl",
  "segments": 312,                // segments covered by this record
  "tokens": {
    "input_regular": 18432, "input_cache_read": 96000,
    "input_cache_write": 4100, "output": 5120
  },
  "source": "actual",             // actual | estimated  (provenance)
  "cost": { "usd": 0.842, "known": true,
            "in_per_m": 3.0, "out_per_m": 15.0 },  // unit prices used
  "duration_s": 7.4,
  "ok": true, "error": null
}
```

```sql
-- usage.db (mirrors the JSONL; rebuildable from it)
CREATE TABLE usage (
  id TEXT PRIMARY KEY,
  ts TEXT NOT NULL,                 -- UTC ISO-8601
  product TEXT NOT NULL,
  app_version TEXT,
  task TEXT NOT NULL,               -- Chat|Translate|BatchTranslate|Proofread|QuickLauncher|PromptGeneration|…
  provider TEXT NOT NULL,
  model TEXT,
  profile TEXT,                     -- custom profile name
  endpoint TEXT,
  project TEXT,
  project_key TEXT,                 -- hash of .sdlproj
  file TEXT,
  client TEXT,
  src_lang TEXT, tgt_lang TEXT,
  segments INTEGER,
  in_regular INTEGER DEFAULT 0,
  in_cache_read INTEGER DEFAULT 0,
  in_cache_write INTEGER DEFAULT 0,
  out_tokens INTEGER DEFAULT 0,
  source TEXT,                      -- actual | estimated
  cost_usd REAL,
  cost_known INTEGER DEFAULT 1,
  in_per_m REAL, out_per_m REAL,
  duration_s REAL,
  ok INTEGER DEFAULT 1,
  error TEXT
);
CREATE INDEX idx_usage_ts ON usage(ts);
CREATE INDEX idx_usage_project ON usage(project_key);
CREATE INDEX idx_usage_model ON usage(provider, model);
```

**Files on disk (Trados):**
- `<Root>/trados/usage/usage-2026-06.jsonl` — monthly-rotated append-only log (Daniel's deliverable).
- `<Root>/trados/usage/usage.db` — SQLite for the in-app report.

`<Root>` resolves via the existing `UserDataPath` (`~/Supervertaler/` by default). Workbench mirrors under `<user_data>/workbench/usage/`.

---

## 5. Where to instrument

### 5.1 Trados — subscribe to the existing event (no new call sites)

```csharp
// New: Core/UsageLogger.cs  — wired once at startup
LlmClient.PromptCompleted += UsageLogger.Record;   // already fires for EVERY feature

static class UsageLogger {
    public static void Record(object _, PromptLogEntry e) {
        if (e.Feature == PromptLogFeature.ConnectionTest) return;     // skip pings
        if (!Settings.PersistUsageLog) return;                        // opt-in toggle
        var rec = UsageRecord.From(e, AmbientCallContext.Current);    // §5.3
        UsageStore.AppendJsonl(rec);   // append a line
        UsageStore.InsertDb(rec);      // upsert into usage.db
    }
}
```

This single subscription captures Chat, Translate, **BatchTranslate** (the aggregate entry), **QuickLauncher**, **PromptGeneration**, Proofread, SuperMemory — i.e. everything.

### 5.2 Close the two "actual usage" gaps (so custom + Gemini + local are real, not estimated)

- **Gemini:** parse `usageMetadata.promptTokenCount` / `candidatesTokenCount` / `cachedContentTokenCount` in `LlmClient.cs:1190` (Workbench already does this — port the logic the other way).
- **Ollama:** parse `prompt_eval_count` (input) + `eval_count` (output) in `LlmClient.cs:1202`. Local models are "free" but Daniel still wants the **token counts** for capacity monitoring.

### 5.3 Attribution — an ambient call context

`PromptLogEntry` lacks project/file today. Rather than thread parameters through every caller, set an **ambient context** at the few entry points that already know it:

```csharp
// set by BatchTranslator, QuickLauncherAction, AiAssistantViewPart before the call
using (AmbientCallContext.Push(new CallContext {
        Project = DocumentContextHelper.GetProjectName(doc),
        ProjectKey = ProjectSettings.KeyFor(doc),
        File = DocumentContextHelper.GetDocumentName(doc),
        SrcLang = srcLang, TgtLang = tgtLang, Segments = n,
        Profile = aiSettings.GetActiveCustomProfile()?.Name })) {
    await client.SendPromptAsync(...);
}
```

(`AsyncLocal<CallContext>` so it flows across the await without touching `LlmClient`'s signature.)

### 5.4 Per-custom-model pricing (so cost works for FAU's models)

Extend `CustomOpenAiProfile`:

```csharp
public decimal? InputPricePer1M { get; set; }   // null = cost unknown
public decimal? OutputPricePer1M { get; set; }
public int? ContextWindow { get; set; }
```

If null → `cost_known=false`, tokens still logged. If FAU later sets a price (or Claude-via-GWDG has known rates), cost is computed — and historical rows can be **recomputed from stored tokens**.

### 5.5 Workbench — one sink + wire batch

- Route **all** calls through a `record_usage(meta)` sink (chat already has the data; **switch `_translate_batch_with_llm` to `translate_with_usage()`** so the main spend is captured).
- New `usage` table (same DDL) in `supervertaler.db` + the JSONL mirror.
- Reuse `llm_pricing.estimate_cost`; add custom-model entries.

---

## 6. Reporting & UX (the translator/LSP value)

A new **"Usage & Costs"** surface (in Trados, extend the existing Reports area / add a sibling tab; in Workbench, a panel). Built on `usage.db`:

- **Group by** day / month / **project** / **client** / model / provider / **task type**.
- **Totals:** calls, input/output tokens, **cache savings**, cost, and **"% actual vs estimated"** coverage (so the user knows how trustworthy the cost figure is).
- **"This project" view** keyed by `.sdlproj` hash → the per-job number an LSP bills against.
- **Export:** CSV / XLSX (per-record and per-grouping) + JSON. This is the artifact for FAU's monitoring and for LSP invoicing.
- **Pre-flight estimate** (high value, cheap): before a large batch we already estimate input tokens — show *"~N segments, ~X tokens, ≈ \$Y"* and a running total during the run.
- **Budgets/alerts (optional):** soft monthly/project cap → non-blocking warning; visible running total. (Recommend warn-only, never a hard stop mid-job.)
- Reuse the existing **provenance disclaimer** pattern (`ReportsControl.cs:150-174`) so estimated figures are clearly labelled.

---

## 7. LSP-specific considerations

- **Per-client / per-project attribution → billing.** Export a client's cost report straight from the ledger. Store `client` per record (source options in §8).
- **Self-hosted / institutional models (the FAU case).** Tokens logged even when cost is unknown; set a price later and recompute. Capacity planning works off token volumes regardless of cost.
- **Multi-translator aggregation.** Because the JSONL schema is identical across translators and across both products, an LSP can simply concatenate everyone's `usage-*.jsonl` and pivot. (A future optional central HTTP sink is possible but **out of scope** — local files keep it private by default.)
- **"My spend" vs "client deliverable"** are separated: cost columns vs project/client columns.

---

## 8. Privacy & data hygiene

- **Metadata-only ledger** (tokens/cost/model/project) — **no prompt or response text**. This is the key privacy decision and what makes the log safe to share with an institution.
- **Opt-in**, but reasonable to default **on** for the usage ledger (it's just counts, unlike the prompt log). New toggle `PersistUsageLog` distinct from `LogPromptsToReports`.
- **Rotation/retention:** monthly JSONL files; a "prune older than N months" option for `usage.db`.
- Entirely separate from the anonymous install ping (`UsageStatistics`) — that stays counts-of-installs only.

---

## 9. Cross-product strategy (Workbench port)

1. **Identical schema** (JSONL fields + table columns) → reports/exports interchangeable; an LSP can merge Trados + Workbench usage.
2. **Single canonical pricing source.** Today there are **two** drifting pricing tables — `TokenEstimator.Pricing` (C#) and `llm_pricing.py` (Python). **Recommend one canonical `pricing.json`** consumed by both (or, minimally, a shared update checklist). Flagged as a real maintenance risk: prices already differ subtly (e.g. Opus rows) between the two.
3. **Same task taxonomy** — map Workbench flows onto the same `task` values.
4. **Same UX concepts**, different toolkits (WinForms vs PyQt).
5. **Port direction for capture gaps:** Gemini usage parsing exists in Workbench → port to Trados; batch-usage capture exists in Trados → port to Workbench. They're mirror images.

---

## 10. Phased implementation plan

| Phase | Scope | Outcome |
|---|---|---|
| **1 — "Daniel MVP"** | `UsageLogger` → JSONL; complete Gemini + Ollama token capture; ambient project/file attribution; `PersistUsageLog` toggle; document the JSONL schema | Persistent token log file for all flows incl. custom models. **Directly answers the email.** Trados only. |
| **2 — Ledger + report** | `usage.db` + in-app "Usage & Costs" report (group/filter) + CSV/XLSX export | Self-serve cost/usage reporting & billing export |
| **3 — Budgets & pricing** | Per-custom-model pricing fields + pre-flight estimate + running total + soft budget alerts | Cost control + estimates for self-hosted models |
| **4 — Workbench port** | Same schema/pricing; wire batch to `translate_with_usage`; usage table + report | Parity; interchangeable exports |

Each phase ships independently. Phase 1 alone closes Daniel's request and is small (one new class + two parsing fixes + an ambient context + a toggle).

---

## 11. Open questions for Michael

1. **Metadata-only ledger** — agree the usage log should **never** contain prompt/response text? (Strong recommendation: yes.)
2. **Client attribution source** — per-project field in `ProjectSettings`, derived from a memory-bank client profile, or a manual tag? (Affects §6 billing views.)
3. **Budgets** — warn-only, or also a hard pre-call block? (Recommend warn-only.)
4. **Canonical pricing file across both products now**, or later? (Drift already exists.)
5. **FAU deliverable** — do they want the **JSONL schema documented** so GWDG/their monitoring can ingest it directly? (Easy win; I can write the spec.)
6. Default for `PersistUsageLog` — **on** (counts only) or off?

---

## 12. One-paragraph answer you can send Daniel now

> Yes — this is very feasible, and most of the groundwork is already in place. Supervertaler already measures input/output tokens (and cache usage) for every AI call, including quick-launcher, batch and prompt-generation, and for your custom/self-hosted models. What it doesn't yet do is write them to a persistent file — that's a small addition. I'm planning a usage log that records, per call, the timestamp, task type, model/endpoint, project/file, token counts and (where the price is known) cost, as an append-only JSONL file you can open in Excel or parse with a script — and I can document the format so it can feed straight into your monitoring. Tokens are logged even for models where we don't know the price, which is exactly your case until the university rates are fixed. I'll have a first version for you shortly.

---

*Code references in this document point at the working tree as of Trados v4.20.55 / Workbench v1.10.284.*
