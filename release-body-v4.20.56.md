Supervertaler for Trados **v4.20.56** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.56.

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

## [4.20.56] – 2026-06-18

### Added (Token usage & costs)

- **Persistent token-usage log.** Every AI call now records its token usage and cost to a monthly log file under `…\Supervertaler\trados\usage\usage-YYYY-MM.jsonl` — covering Translate, Batch Translate, Quick Launcher, AutoPrompt, Proofread and Chat, for every provider including custom / self-hosted endpoints. It stores metadata only (model, token counts, cost, project, file, language pair) and **never the prompt or response text**, so the file stays small and is safe to open in Excel or hand to an institution's monitoring team. On by default; switch it off in Settings → AI Settings.
- **Usage & Costs report.** A new "Usage & Costs report…" button (Settings → AI Settings) opens a window that totals your usage over a date range, grouped by project, client, model, provider, task type, day or month, with a "% from provider" column showing how much is measured vs. estimated. Export the detailed ledger to **CSV or Excel (.xlsx)** for billing or analysis.
- **Monthly budget (advisory).** Set a soft monthly spend limit (Settings → AI Settings). Once this month's logged cost reaches it, starting a batch translation shows a warn-and-continue prompt — it never blocks, and a budget of 0 disables it.
- **Real token counts for Gemini and Ollama.** These two providers previously fell back to a character-based estimate; their actual reported token counts are now captured, so their cost figures are accurate.

### Changed (Pricing)

- **One canonical price list.** Model prices now live in a single `pricing.json` (shared with Supervertaler Workbench). To re-price both products at once — for example to add your own self-hosted model's rate — copy it to `…\Supervertaler\pricing.json` and edit it there; each app prefers that shared copy over its bundled default. A custom model gains a cost figure simply by adding its id and rate to that file.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
