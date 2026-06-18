# RWS App Store Manager – v4.20.62.0

**Version number:** `4.20.62.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `c72d8949c0ceb4decfd59796c770c075125e4200db5e5449106bb3081d151e70`

---

## Changelog

*Covers everything since the last App Store release (4.20.51).*

### Added

- **Token Usage & Costs — know what every AI call costs.** Every AI call now records its token usage and cost to a monthly log file (`…\Supervertaler\trados\usage\usage-YYYY-MM.jsonl`), covering Translate, Batch Translate, QuickLauncher, AutoPrompt, Proofread and Chat, for every provider including custom / self-hosted endpoints. It stores **metadata only** (model, token counts, cost, project, file, language pair) and **never the prompt or response text**, so the file stays small and is safe to open in Excel or hand to an institution's monitoring team. On by default; switch it off in Settings → AI Settings.
- **Usage & Costs report.** A new "Usage & Costs report…" button (Settings → AI Settings) opens a window that totals your usage over a date range, grouped by project, client, model, provider, task type, day or month, with a "% from provider" column showing how much is measured vs. estimated. Export the detailed ledger to **CSV or Excel (.xlsx)** for billing or analysis.
- **Monthly budget (advisory).** Set a soft monthly spend limit (Settings → AI Settings). Once this month's logged cost reaches it, starting a batch translation shows a warn-and-continue prompt — it never blocks, and a budget of `0` disables it.
- **Accurate token counts for Gemini and Ollama.** These two providers previously fell back to a character-based estimate; their actual reported token counts are now captured, so their cost figures are accurate.
- **Opt-in diagnostic logging.** Settings → General → Diagnostics → "Enable diagnostic logging" writes a detailed debug trace to `…/trados/logs/diagnostic.log`, with "Open log folder / Open log file / Clear log" buttons. Off by default. Turn it on, reproduce a problem, and send the log — it records, among other things, exactly why a native MultiTerm (.sdltb) termbase is or isn't picked up.

### Changed

- **The redundant "AI translate current segment" entry has been removed from the editor right-click menu.** It was a legacy alias that did exactly the same thing as **"Translate active segment" (Ctrl+T)** — translate the active segment using your Batch Translate settings — so having both, under two different names, was just clutter. The single **"Translate active segment"** command remains, with Ctrl+T.
- **One canonical price list.** Model prices now live in a single `pricing.json` (shared with Supervertaler Workbench). To re-price both products at once — for example to add your own self-hosted model's rate — copy it to `…\Supervertaler\pricing.json` and edit it there; each app prefers that shared copy over its bundled default. A custom model gains a cost figure simply by adding its id and rate to that file.
- **MultiTerm termbase identifiers are now stored as a stable hash of the file path** (FNV-1a) instead of the runtime's `GetHashCode()`, which is documented as unsafe to persist. Hardening only — current behaviour is unchanged; the per-termbase AI / enabled state will now survive a move to a different .NET runtime or bitness.

### Fixed

- **Adding a term with a Chinese (or Korean/Japanese) target no longer saves the whole segment.** When you selected part of the target — e.g. 挂车控制模块 ("trailer control module") — the term that got saved was the entire target segment. The Add Term / Quick-Add actions expand a partial selection to word boundaries, but that expansion stopped only at **whitespace**, and Chinese has no spaces, so it ran to the segment edges. Chinese is now detected as a no-space script (it was already handled for Korean/Japanese on the source side), and the **target**-side expansion now honours the project's target language too, so for these scripts the Add Term actions keep your exact selection. As a side benefit, detecting Chinese also enables CJK suffix-tolerant term matching, so Chinese terms highlight more reliably in TermLens.
- **A MultiTerm termbase's "AI" tick now survives a Trados restart.** A MultiTerm termbase ticked for AI in Settings → Termbases would silently revert to unticked after reopening Trados, so the AI stopped seeing the terminology until you re-ticked it. The per-project save now always runs (it falls back to the Editor's active document when Settings is opened from the AI Assistant panel), so the choice persists.
- **An attached MultiTerm (.sdltb) termbase now shows in the Termbases tab even when Settings is opened without the editor in focus** — e.g. from the AI Assistant panel, or with no document active. The Settings dialog falls back to detecting the active project's MultiTerm termbases directly, so the row (and its "AI" checkbox) appears whenever a .sdltb is attached, however Settings was reached.

---

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
