# Offload Batch Translate to 64-bit Workbench (Design A: TMX hand-off)

**For:** Supervertaler for Trados (trigger + re-import), Supervertaler Workbench (headless engine)
**Author:** Claude (design for Michael), 2026-06-30
**Status:** Design proposal for review - no code changed.

---

## 0. The ask

Trados Studio 2024 is a **32-bit** process (~2-4 GB address-space ceiling, regardless of machine RAM). A large job - the motivating case was a **375 MB PPTX, ~990 segments** - exhausts that ceiling during Batch Translate and crashes Trados inside its *own* renderer (out-of-memory + GDI/Direct2D failures, ending in a fatal CLR execution-engine error). See the diagnostic write-up in issue context; the in-process guardrails shipped in v4.20.77 mitigate but cannot raise a 32-bit ceiling.

The idea: a **one-click button in Batch Operations** that hands the whole batch to **Supervertaler Workbench** (a separate **64-bit** app), runs it **headlessly** (no window popping up), and brings the result **back into Trados** - so the heavy AI work never runs inside the 32-bit process.

We choose **Design A (TMX hand-off)**: Trados extracts the source segments, a headless Workbench translates them and returns a **TMX**, and Trados applies the result via its own **Pre-translate** (which is memory-efficient and tag-aware). Rationale and the rejected alternative (Design B, SDLXLIFF round-trip) are in section 4.

---

## 1. Executive summary

Flow:

1. **Trados (32-bit)** - user clicks **"Translate via Workbench"** on the Batch Operations tab. The plugin builds the same segment list + scope it already uses for Batch Translate, serialises each source (with inline tags), and writes a **job file** (`job.json`) plus the job config (provider, model, prompt, termbase, language pair, options).
2. **Workbench (64-bit), headless** - launched as `supervertaler --batch <job.json> --out <result.tmx>`. It runs the existing `PreTranslationWorker` batch logic with **no GUI window shown**, writes a **TMX** of source->target translation units (and a small `result.json` with counts / errors / token usage), and exits.
3. **Trados** - imports `result.tmx` into a translation memory and applies it via **Pre-translate**. Tag placement is done by Trados for each 100% source match, so the plugin never performs 990 per-segment editor writes (the operation that, together with the AI overhead, pushed the 32-bit process over the edge).

The heavy part (prompt building, AI calls, response holding, document context) happens entirely in the 64-bit process. Trados's peak memory becomes "document open + one bulk pre-translate" instead of "document open + 990-segment AI batch".

**Honest limit:** the file still has to be *open* in 32-bit Trados, and a 375 MB PPTX is near the ceiling just sitting there. This rescues the large-but-not-insane jobs (the majority) and makes them painless; for a genuine monster, doing the whole thing in Workbench (open the source there, never in Trados) or splitting the file remains the answer. The button should say so when it detects an extreme case.

---

## 2. What already exists (the foundation)

### Trados plugin (`Supervertaler-for-Trados`)
- `Core/BatchTranslator.cs` already builds the scoped segment list and (via `SegmentTagHandler.Serialize`) serialises source text with `<tN>` tag markers + a `TagMap`. The job builder reuses this.
- `Core/BatchTranslationBackup.cs` already writes a **TMX** during batch runs ("Auto-backup translations to TMX") - a working TMX writer and format precedent.
- `Core/WorkbenchBridgeClient.cs` already implements the **IPC handshake**: it reads `…\workbench\runtime\sidekick-bridge.json` (`version`, `port`, `token`, `pid`, `startedAt`) and `POST`s to `http://127.0.0.1:<port>/v1/run-prompt` with a bearer token. This pattern (and the shared user-data root) is reused for job/result exchange and Workbench detection.
- `TranslationProviders/SupervertalerTmLanguageDirection.cs` and `Core/TmSearcher.cs` show SDK TM usage (`FileBasedTranslationMemory`), the basis for importing the result TMX into an `.sdltm`.

### Workbench (`Supervertaler-Workbench`, `D:\Dev\Sv\Supervertaler`)
- `PreTranslationWorker` (a `QThread`, `Supervertaler.py:7419`) is the existing batch engine - the logic the headless run reuses.
- `modules/supervertaler_bridge_server.py` (`SupervertalerBridgeServer`) + `modules/trados_bridge_client.py` already run a localhost bridge with the `sidekick-bridge.json` handshake, started from the GUI app. Today it serves single `/v1/run-prompt` calls only.
- `main()` (`Supervertaler.py:71906`) builds a GUI `QApplication` and shows the window. **There is no headless/CLI mode yet** - that is the main new piece on the Workbench side.
- `pyproject.toml` already defines console entry points (`supervertaler`, `supervertaler-debug`).

---

## 3. Design A in detail

### 3.1 Trigger (Trados UI)
- A **button** on the Batch Operations tab (Translate mode), near the Clipboard Mode control. A button (not a checkbox) because it's an action, not a mode.
- Working name: **"Translate via Workbench"** (alternatives in section 7), with helper text: *"Runs the batch in the 64-bit Workbench and brings the results back - for files too large for 32-bit Trados."*
- On click: **detect Workbench** (a recent-enough version) via the shared user-data root / a `--version` probe / the bridge handshake file. If not found or too old, show a short dialog with the download link instead of failing.

### 3.2 Job contract (`job.json`)
Written by Trados to a temp dir under the shared user-data root. Proposed shape:

```jsonc
{
  "schemaVersion": 1,
  "product": "trados",
  "sourceLang": "en-US",
  "targetLang": "nl-NL",
  "scope": "EmptyOnly",
  "provider": "openai",
  "model": "gpt-5.4-mini",
  "promptName": "…",            // or inline custom system prompt
  "customSystemPrompt": "…",
  "includeDocumentContext": false,
  "termbase": [ { "source": "…", "target": "…", "note": "…" } ],
  "segments": [ { "number": 1, "source": "…<t0>…</t0>…" } ],
  "out": "…/result.tmx",
  "resultMeta": "…/result.json"
}
```

**API keys:** prefer that Workbench uses **its own stored keys** for the chosen provider (don't ship keys between processes). If Workbench has no key for that provider, fail with a clear "set your <provider> key in Workbench" message. (Open question 6.2.)

### 3.3 Headless Workbench CLI (the load-bearing piece)
- New invocation: `supervertaler --batch <job.json> --out <result.tmx>` (console entry point, e.g. `supervertaler-batch`, so no GUI subsystem).
- `main()` branches on `--batch`: build a `QApplication` **without showing the main window** (an event loop is still needed for `PreTranslationWorker`/`QThread`), run the batch over `job.segments` with the job config, write the **TMX** (source -> target TUs) and a `result.json`:

```jsonc
{ "ok": true, "translated": 990, "failed": 0,
  "usage": { "inputTokens": …, "outputTokens": …, "costUsd": … },
  "errors": [] }
```

- Exit code reflects success/failure. Progress can be streamed to stdout (line-per-batch) so Trados can show a live log, or Trados can poll `result.json`.
- Reuses the existing translation pipeline (prompt building, provider calls, batch response parsing) - **no second implementation of translation**.

### 3.4 Return + apply in Trados
- Trados imports `result.tmx` into a TM, then applies it. Two ways to apply, in order of preference:
  - **A1 (preferred): Trados native Pre-translate.** Memory-efficient and it re-inserts tags for each 100% source match - so the plugin does **zero** per-segment editor writes. Requires invoking Trados's Pre-translate batch task programmatically (SDK automatic task) **or** guiding the user to run Batch Tasks -> Pre-translate after import. (Open question 6.1.)
  - **A2 (fallback): plugin write-back from the TMX.** Reuses the existing write-back path, but reintroduces the per-segment editor writes we are trying to avoid - only acceptable as a fallback for small remainders.
- **Which TM to import into:** prefer a dedicated/scratch TM (or the project's main TM if the user opts in) to avoid silently polluting a delivery TM. (Open question 6.3.)

### 3.5 Tag fidelity
Sources go into the TMX with their serialised tags; Trados's Pre-translate re-inserts tags for 100% source matches. **Trados owns tag placement** end-to-end, which is the low-risk path (Design B would put SDLXLIFF tag round-trip on Workbench instead).

### 3.6 Progress & UX
- Headless = **no Workbench window**. Trados shows progress in the existing Batch log (from CLI stdout or `result.json` polling) and a **Cancel** that terminates the CLI process.
- On completion: summary (translated/failed, tokens/cost from `result.json`), then the import + pre-translate step.

### 3.7 Files, cleanup, usage logging
- Job/result files live under the shared user-data root (e.g. `…\workbench\runtime\offload\<jobid>\`), deleted on success (kept on failure for diagnostics).
- Usage is logged **once, by Workbench** (it made the calls); Trados should not double-count. The returned `result.json.usage` can be surfaced in the Trados Reports/Usage view as an informational, Workbench-attributed entry.

---

## 4. Why Design A over Design B

- **Design A (TMX hand-off, chosen):** reuses the existing TMX writer, Trados's robust tag-aware Pre-translate, and the existing bridge handshake. Trados never writes 990 segments through the live editor. Main constraint: TMX application is by 100% source match - exactly the batch (empty/unconfirmed) case.
- **Design B (SDLXLIFF hand-off, deferred):** hand the `.sdlxliff` to Workbench, translate + write it back in 64-bit, Trados reopens. More complete (Trados does almost nothing), but two hard dependencies: Workbench must round-trip SDLXLIFF **tags** perfectly, and the file is open/locked in Trados. Higher risk; revisit as a v2 once A is proven and Workbench SDLXLIFF write-back is solid.

---

## 5. Phasing

- **P1 - Workbench headless batch CLI (engine).** `supervertaler --batch <job.json> --out <result.tmx>`, reusing `PreTranslationWorker`; window never shown; writes TMX + `result.json`. Independently testable from a shell. **This is the contract everything else depends on - nail it first.**
- **P2 - Trados button + round-trip.** "Translate via Workbench" button; job builder (reuse `BatchTranslator` scope + `SegmentTagHandler`); Workbench detection; invoke CLI; import TMX; apply via Pre-translate (A1) with A2 fallback; progress + cancel.
- **P3 - Polish.** Not-installed/too-old -> download path; settings/key parity messaging; scratch-vs-project TM choice; extreme-size advisory ("split the file / do it fully in Workbench"); Reports usage attribution.

---

## 6. Risks & open questions

1. **Programmatic Pre-translate (6.1).** Can the plugin trigger Trados's Pre-translate batch task cleanly via the SDK, or do we guide the user to click it? If neither is satisfactory, A2 (plugin write-back) is the fallback, accepting some per-segment writes.
2. **API-key handoff (6.2).** Use Workbench's own keys (preferred, no secrets crossing processes) vs passing keys in `job.json`. Decide and message clearly when a key is missing on the Workbench side.
3. **Which TM to import into (6.3).** Scratch TM vs project TM; avoid polluting a delivery TM without consent.
4. **Headless QApplication (6.4).** `PreTranslationWorker` is a `QThread` and needs an event loop; confirm a windowless `QApplication` runs the batch to completion and exits cleanly in a console-subsystem invocation.
5. **Workbench dependency & versioning (6.5).** Detect presence + a minimum version; handle absent/old gracefully. Consider a `--batch`-capability flag in the version probe.
6. **Licensing (6.6).** Confirm a Workbench batch run is available to a Trados-plugin licensee (both are Supervertaler) without a separate Workbench licence gate, or define the expected behaviour.
7. **The residual ceiling (6.7).** The source file is still open in 32-bit Trados; opening/saving a 375 MB PPTX may itself be too much. The button should detect extreme size and advise full-Workbench / splitting rather than promising success.
8. **Tag edge cases (6.8).** Non-100% source variance (whitespace, punctuation) can cause a pre-translate miss; reuse the lenient-match thinking already used elsewhere, or ensure source text in the TMX matches what Trados will look up.

---

## 7. Naming options for the button

- **"Translate via Workbench"** (clear, neutral)
- **"Translate via Workbench (large files)"** (signals the benefit)
- **"Offload to Workbench (64-bit)"**
- **"Send to Supervertaler Workbench"** (Michael's phrasing; doesn't hint at the why)

Recommendation: **"Translate via Workbench (large files)"** with the helper line in 3.1.
