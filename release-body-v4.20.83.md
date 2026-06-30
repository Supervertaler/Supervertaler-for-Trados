Supervertaler for Trados **v4.20.83** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.78 → 4.20.83.

## 📦 Installing from here (unsigned build – read first)

The plugins attached to this release are the **unsigned** builds. The version on the **RWS App Store is signed and notarised** – that's the recommended channel for most users. These downloads are for trying the latest fixes **before App Store approval** (which can take a few days, especially over a weekend).

**To install:**
1. Download the zip for your Trados version (table below).
2. **Extract it** – inside is a single `.sdlplugin` file.
3. Close Trados Studio, then double-click the `.sdlplugin` to run the Plugin Installer. **Do not rename the file** – Trados matches the filename against the plugin manifest.
4. Trados will warn that the plugin is **not signed**; that is expected for the direct build – click through to continue.

| Download | Trados version |
|---|---|
| `Supervertaler-for-Trados-Studio-2024.zip` | Trados Studio 2024 |
| `Supervertaler-for-Trados-Studio-2026-beta.zip` | Trados Studio 2026 (beta) |

## What's changed

## [4.20.83] – 2026-07-01

### Fixed

- **Markdown re-import dropped multi-line segments.** In the *Bracketed [SEGMENT NNNN]* (AI-friendly Markdown) layout, a segment whose text contains a hard line break – e.g. a source `VEILIGHEIDS-` / `HELM` exported across two lines – only had its **first** line re-imported; the continuation line (and the rest of the translation) was silently lost. The importer now reassembles multi-line `NL:` / `EN:` bodies, stopping correctly at the next language line or the `Status:` line. Single-line segments, empty targets, and proofreader-inserted extra lines round-trip exactly as before.

### Improved (Translate via Workbench · resilience)

- **Clear message when Trados can't close the document.** If the offload's `Close(document)` call fails because the 32-bit Trados process is in a degraded / low-memory state (an `AccessViolationException` – "Attempted to read or write protected memory… memory is corrupt"), the plugin now cancels cleanly (your file is untouched) and shows a plain-language prompt to **save, restart Trados Studio, and try again** instead of surfacing the cryptic SDK error.

## [4.20.82] – 2026-06-30

### Improved (Batch Operations · retry + layout)

- **"Retry segments left empty" now applies to a normal Batch Translate too**, not just *Translate via Workbench*. One shared checkbox in Batch Operations controls both: when ticked, any segment the model leaves empty (or fails to write) is re-translated in extra passes (up to 5) until none remain. The token usage from the retries is rolled into the same Trados ledger entry, and the translated/failed counts are corrected as segments fill in.
- **The *Translate via Workbench* button moved to the right of the ▶ Translate button** instead of sitting up among the scope/option controls. It shows only in normal (non-clipboard) Translate mode, alongside the Translate button it complements.

## [4.20.81] – 2026-06-30

### Improved (Translate via Workbench · parity with a normal Batch Translate)

- **The offload now matches a normal Batch Translate much more closely:**
  - **Document context is included.** It's collected from the open document (capped like the normal batch) and sent along – cheap for the media-heavy/text-light files this targets, so there's no reason to drop it.
  - **Token usage is recorded in Trados's Token Usage & Costs.** The AI calls run in Workbench, but the engine now reports tokens back and the plugin logs the cost into the Trados ledger (as a *BatchTranslate · via Workbench* entry, attributed to the project). Cost is computed at the no-cache rate (a slight overestimate).
  - **"Retry segments left empty" checkbox** next to the button: re-translate any segments the model leaves empty, in extra passes.
  - **Scope respects status.** *Empty segments only* and *All segments* as before, plus *All unfinished segments* now translates Not Translated + Draft and leaves Confirmed/Signed-off untouched (it no longer flattens to "All"). Locked segments are always skipped. *Filtered* scopes map to All, since the editor's display filter can't apply to a closed-document run.

## [4.20.80] – 2026-06-30

### Added (Translate via Workbench · finding Workbench without a terminal)

- **The plugin now finds Supervertaler Workbench on its own, and lets you point at it if needed – no terminal required.** Three parts:
  - **Auto-detect:** in addition to the `supervertaler` launcher on PATH, the plugin now probes common install locations for the bundled desktop app (`%LocalAppData%\Programs\Supervertaler`, `Program Files\Supervertaler`, etc.) and the pip `--user` scripts folder.
  - **A "Workbench (.exe)" setting** on *Settings → AI Settings*, with a **Browse** button, so you can point the offload at any Supervertaler executable. Blank = auto-detect.
  - **A friendly prompt when it still can't be found:** instead of a log line, a dialog offers to **locate** Workbench (the choice is remembered) or **open the download page**.

## [4.20.79] – 2026-06-30

### Added (Translate via Workbench · progress window)

- **The "Translate via Workbench" offload now shows a floating progress window** while it runs. Because the document is closed during the offload, the in-editor Batch log was invisible – this top-level window gives live feedback instead: a status line, a progress bar that becomes determinate once batches start (`batch n of N`), and a **Cancel** button (cancels the engine and reopens the document). It closes automatically when the translated document reopens. First live test of the feature (v4.20.78) worked end to end; this adds the missing feedback.

## [4.20.78] – 2026-06-30

### Added (Batch Operations · "Translate via Workbench" – offload large files to 64-bit Workbench)

- **New "Translate via Workbench (large files)" button on the Batch Operations tab** hands the whole document to the 64-bit Supervertaler Workbench, which translates it and hands it back – so jobs too large for 32-bit Trados Studio 2024 finish without crashing. Trados does **no** heavy lifting. Flow: the plugin builds a job (your provider/model/key/prompt/termbase, and the scope from the dropdown), **closes the active document**, runs `supervertaler --translate-sdlxliff` on its `.sdlxliff` (round-trip, inline tags preserved), then **swaps in the translated file and reopens** the document. A `.sv-backup` copy of the original is kept next to it. Requires Supervertaler Workbench v1.10.322+ installed and discoverable (the plugin runs `supervertaler` / `supervertaler-debug` from PATH). The API key is passed from the plugin, so Workbench needs no separate setup. **First release of this feature – please test on a small project first** ([#42](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/42)).

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
