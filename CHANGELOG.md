# Changelog

## [4.20.62] – 2026-06-19

### Changed (Editor context menu — the proper fix)

- **The duplicate "AI translate current segment" entry is removed from the editor right-click menu, without crashing Studio.** The earlier crashes were both from removing *too much*: 4.20.57 deleted the action type (but the manifest still referenced it, so the command bar couldn't instantiate it); 4.20.60 deleted the action's entire `<auxiliaryExtensionAttributes>` element from `plugin.xml` (so the startup shortcut-cache loader hit a null and threw). The startup log pinpointed the second one (`ActionService.ReloadShortcutSettings → Extension.get_AuxiliaryExtensionAttributes → NullReferenceException`). The fix keeps the action **registered** and keeps the element present but **empty** (`<auxiliaryExtensionAttributes />`), dropping only the `ActionLayoutAttribute` — exactly the shape three other extensions in this plugin already ship and load fine. Net: no menu entry, no crash. Use **"Translate active segment" (Ctrl+T)**. (The action still appears in the keyboard-shortcuts editor, by design — registration must stay.)


## [4.20.61] – 2026-06-18

### Fixed (Startup crash — abandons the menu-hide attempt)

- **Studio starts reliably again; the duplicate "AI translate current segment" entry stays.** Two different ways of hiding that entry each crashed Studio on startup: deleting the action (4.20.57) failed because Studio instantiates every cached command-bar action on launch, and removing only its menu-layout from `plugin.xml` (4.20.60) made the action service throw a `NullReferenceException` because the cached editor command bar still referenced that item. Studio's persisted command-bar state makes both removals unsafe. This release restores the known-good configuration (the action and its menu placement both present), so Studio launches normally. The entry is a harmless exact duplicate of **"Translate active segment" (Ctrl+T)**; it is being left in place.


## [4.20.60] – 2026-06-18

### Changed (Editor context menu) — REVERTED in 4.20.61

- Removed the action's `ActionLayoutAttribute` from `plugin.xml` to drop the menu entry. **This crashed Studio on startup** (`IActionService` NullReferenceException — the cached command bar still referenced the item). Reverted.


## [4.20.59] – 2026-06-18

### Changed (Editor context menu)

- Attempted to hide the duplicate entry by dropping its C# `[ActionLayout]`. **Ineffective** — Studio reads the menu layout from `plugin.xml`, not the C# attribute. Superseded.


## [4.20.58] – 2026-06-18

### Fixed (Startup crash — reverts 4.20.57)

- **Trados Studio 2024 and 2026 no longer crash on startup.** Version 4.20.57 deleted the legacy "AI translate current segment" action to declutter the right-click menu. But Studio caches the editor command bar by action id, and on startup it tries to instantiate every cached action — so with the action type gone it threw *"Failed to add view command bar extensions for view 'EditorView'"* and exited before the editor loaded. This release restores that action registration, so the cached reference resolves again and Studio starts normally. The action is intentionally kept registered for exactly this backward-compatibility reason; the menu still shows the (harmless) duplicate entry. **If you installed 4.20.57, update to this build.**


## [4.20.57] – 2026-06-18

### Changed (Editor context menu)

- **Removed the duplicate "AI translate current segment" entry from the editor right-click menu.** It was a legacy alias that did exactly the same thing as **"Translate active segment" (Ctrl+T)** — translate the active segment using your Batch Translate settings — and only added clutter (and a confusing second Ctrl+T label). The single **"Translate active segment"** command remains, with Ctrl+T. The legacy action is also no longer listed under the plugin's keyboard-shortcuts settings. **(Superseded: this change crashed Studio on startup — see 4.20.58.)**


## [4.20.56] – 2026-06-18

### Added (Token usage & costs)

- **Persistent token-usage log.** Every AI call now records its token usage and cost to a monthly log file under `…\Supervertaler\trados\usage\usage-YYYY-MM.jsonl` — covering Translate, Batch Translate, Quick Launcher, AutoPrompt, Proofread and Chat, for every provider including custom / self-hosted endpoints. It stores metadata only (model, token counts, cost, project, file, language pair) and **never the prompt or response text**, so the file stays small and is safe to open in Excel or hand to an institution's monitoring team. On by default; switch it off in Settings → AI Settings.
- **Usage & Costs report.** A new "Usage & Costs report…" button (Settings → AI Settings) opens a window that totals your usage over a date range, grouped by project, client, model, provider, task type, day or month, with a "% from provider" column showing how much is measured vs. estimated. Export the detailed ledger to **CSV or Excel (.xlsx)** for billing or analysis.
- **Monthly budget (advisory).** Set a soft monthly spend limit (Settings → AI Settings). Once this month's logged cost reaches it, starting a batch translation shows a warn-and-continue prompt — it never blocks, and a budget of 0 disables it.
- **Real token counts for Gemini and Ollama.** These two providers previously fell back to a character-based estimate; their actual reported token counts are now captured, so their cost figures are accurate.

### Changed (Pricing)

- **One canonical price list.** Model prices now live in a single `pricing.json` (shared with Supervertaler Workbench). To re-price both products at once — for example to add your own self-hosted model's rate — copy it to `…\Supervertaler\pricing.json` and edit it there; each app prefers that shared copy over its bundled default. A custom model gains a cost figure simply by adding its id and rate to that file.


## [4.20.55] – 2026-06-17

### Fixed (Add Term · Chinese and other no-space scripts)

- **Adding a term with a Chinese (or Korean/Japanese) target no longer saves the whole segment.** When you selected part of the target — e.g. 挂车控制模块 ("trailer control module") — the term that got saved was the entire target segment, 挂车控制模块的更换. The Add Term / Quick-Add actions expand a partial selection out to word boundaries, but that expansion stopped only at **whitespace**, and Chinese has no spaces between words, so it ran to the segment edges and swallowed everything. Two causes: (1) the language auto-detection for "no auto-expand" only recognised Korean and Japanese, never Chinese; and (2) the **target**-side expansion ignored the language entirely (only the source side honoured it), so even Korean/Japanese targets were affected. Both are fixed: Chinese is now detected as a no-space script, and the target expansion now honours the project's target language. For these scripts the Add Term actions keep your exact selection. (Chinese has no spaces — and therefore no word boundaries to expand to — so selecting the exact characters you want is the intended workflow, not a temporary limitation; a general word segmenter would tend to over-split multi-character technical terms and pick the wrong span.) As a side benefit, detecting Chinese also enables CJK suffix-tolerant term matching, so Chinese terms highlight more reliably in TermLens. (Reported by a user adding Chinese terminology.)


## [4.20.54] – 2026-06-16

### Fixed (MultiTerm · AI)

- **The MultiTerm "AI" tick now survives a Trados restart.** A MultiTerm termbase ticked for AI in Settings → Termbases would silently revert to unticked after reopening Trados, so the AI stopped seeing the terminology until you re-ticked it. The tick is stored per-project, but the per-project save only ran when the Editor view part had already tracked the active project — which isn't the case when Settings is opened from the AI Assistant panel (the same path 4.20.53 made the row visible from). The tick then lived only in the global settings and was overwritten by the empty per-project overlay on the next restart. The current-project lookup now falls back to the Editor's active document, so the per-project save always runs and the choice persists. (Fixes #36.)

### Changed (MultiTerm · internal)

- **MultiTerm termbase synthetic IDs are now derived from a stable hash of the file path** (FNV-1a) instead of `String.GetHashCode()`. `GetHashCode` is documented as unsafe to persist — it varies across .NET runtimes and between 32-bit and 64-bit processes — so the previous IDs, used as the persistence key for each termbase's AI / enabled state, would not survive a move to a different runtime or bitness. Hardening only; current behaviour is unchanged on the existing .NET Framework build.


## [4.20.53] – 2026-06-15

### Fixed (MultiTerm)

- **An attached MultiTerm (.sdltb) termbase now shows in the Termbases tab even when Settings is opened without the editor in focus.** The grid's MultiTerm list previously came only from the live TermLens editor view part, so if the Settings dialog was opened from the AI Assistant panel, or with no document active in the Editor, an attached .sdltb (and its "AI" checkbox) would silently not appear. The Settings dialog now falls back to detecting the active project's MultiTerm termbases directly, so the row appears whenever a .sdltb is attached, however Settings was reached. (Investigating #36.)


## [4.20.52] – 2026-06-15

### Added (diagnostics)

- **Opt-in diagnostic logging.** Settings → General → Diagnostics → "Enable diagnostic logging" writes a detailed debug trace to `…/trados/logs/diagnostic.log`, with the path shown in the dialog and "Open log folder" / "Open log file" / "Clear log" buttons. Off by default. Turn it on, reproduce a problem, and send the log — it records, among other things, exactly why a native MultiTerm (.sdltb) termbase is or isn't picked up (project termbase configuration, language-index mapping, how many terms the Supervertaler .db and the MultiTerm index load, and any fallback). Aimed at issue #36.


## [4.20.51] – 2026-06-14

### Changed (AI · termbases)

- **Native MultiTerm (.sdltb) termbases are now opt-in for the AI, the same as Supervertaler's own .db termbases.** Previously a MultiTerm termbase attached to your Trados project could have its terms sent to the AI by default. Now its **AI** column (Settings → Termbases) starts unticked, and its terms are included in AI prompts (Chat, AutoPrompt, Batch Translate and Proofread) only when you explicitly tick it. Your Supervertaler .db termbases are unaffected. If you were relying on a MultiTerm termbase feeding the AI, just tick its AI box once after updating.


## [4.20.50] – 2026-06-14

### Added

- **Usage stats now also include the Windows accessibility text size and the in-app UI-scale setting**, alongside the display scale added in 4.20.49. Together the three show the full picture of how large the UI renders for each user (Windows DPI × text size × the in-app slider). Still opt-out, still nothing identifying.


## [4.20.49] – 2026-06-14

### Fixed (high-DPI / Windows display scaling)

- **The Settings dialog now lays out cleanly at any Windows display scale.** A user on a 4K display at 175% display scaling plus 175% text scaling reported clipped buttons, overlapping rows and cut-off text, mainly in the Settings tabs (issue #37). Every tab — General, Termbases, AI Settings, Prompts, Licence and Backup — was rebuilt with `TableLayoutPanel`-based layout, AutoSize buttons and the plugin's `UiScale` system instead of absolute pixel positioning, so labels, fields and buttons size to their (scaled) content and reflow automatically. Nothing clips or overlaps at 100/125/150/175% or any custom scale, and the in-app **UI scale** setting now affects the Settings dialog too. The Batch Operations pane's mode toggle and Scope dropdown, and the editor pencil/glyph buttons, were fixed the same way.

### Added

- **Anonymous usage stats now include the Windows display scale** (e.g. "175"), so the share of users on high-DPI scaling can be seen at a glance. Still opt-out, still nothing identifying — just the DPI percentage alongside the existing OS / Studio version / locale.


## [4.20.48] – 2026-06-12

### Fixed (AI · inline tags)

- **Hardened inline-tag handling against models that mangle tags.** A user reported that Mistral Large recently began emitting empty inline-tag pairs — an opening tag immediately followed by its own closing tag, with the translated text left outside — instead of wrapping the translated words. Two defences: (1) the AI translation prompt now explicitly forbids empty tag pairs and reminds the model of the exact tag format (with a wrong/right example); (2) the tag-placeholder parser is now whitespace- and case-tolerant, so a tag that drifts slightly from the canonical form (a stray space inside it, or the wrong letter case) is still recognised and reconstructed — or cleanly stripped — instead of leaking into the target as literal text. Helps every provider, not just Mistral.


## [4.20.47] – 2026-06-12

### Added (TermLens: Korean / Japanese particle handling)

- **TermLens now recognises terms in Korean and Japanese even when a grammatical particle is attached to the noun.** Previously matching was whole-token, so a clean term like 값 ("value") or 제2 전압 값 ("second voltage value") would not highlight in 값으로 / 제2 전압 값을 / …, because the trailing particle made the segment token differ — and adding a term auto-expanded the selection to the whole token, capturing the particle (saving 장치의 instead of the intended 장치). Both sides are now particle-aware:
  - **Matching** is suffix-tolerant: a single CJK token matches the longest term that is a prefix of it (값 ↦ 값으로), and a multi-word term matches when its final CJK token is a prefix of the segment token (제2 전압 값 ↦ 제2 전압 값으로), with the highlight spanning the attached particle so no text is dropped.
  - **Adding a term** keeps your exact selection instead of expanding to the whitespace word, so the bare noun is saved. (F2 still expands explicitly when you want it.)
  - Controlled by a new **Particle matching** setting (Settings → Termbases): **Auto** (default — on for Korean/Japanese source), **Always on** (e.g. Chinese or another language), or **Always off**. Only CJK-script tokens are prefix-matched, so European languages are unaffected. Addresses issue #34.


## [4.20.46] – 2026-06-12

### Fixed (GPT-5.5 temperature)

- **GPT-5.5 now works – Test Connection and translation no longer fail with an "unsupported temperature" error.** OpenAI's GPT-5.5 only accepts the default temperature value and returns *"Unsupported value: 'temperature' does not support 0.3 with this model. Only the default (1) value is supported."* if any explicit temperature is sent. The plugin sent a fixed `temperature: 0.3` on every OpenAI request for non-reasoning models, so GPT-5.5 failed both the Test Connection check and any actual translation, while GPT-5.4 Mini (which does accept a custom temperature) worked. Models now carry a `SupportsTemperature` flag; GPT-5.5 is marked as temperature-locked, so the parameter is omitted for it across all request paths (Test Connection, single-segment, batch translate, and tool-use), and a heuristic covers any future full GPT-5.x model entered in the custom Model ID field. Reported in issue #36.


## [4.20.45] – 2026-06-12

### Changed (AI pane renamed)

- **The AI pane is called "Supervertaler Assistant" again.** The dockable pane that hosts Chat, Batch Operations, Import/Export and Reports had been shortened to bare "Supervertaler", which collided with the product name (Supervertaler for Trados) and read awkwardly in the documentation ("The Supervertaler supports …"). It is restored to "Supervertaler Assistant" – both the pane/dock-tab caption and the View menu entry – so the pane is clearly distinct from the product and the help docs match. No functional change.


## [4.20.44] – 2026-06-11

### Added (AI termbase selection + AutoPrompt safeguards)

- **Choose which termbases the AI sees with a new "AI" column on the Termbases tab.** Opting a termbase into AI prompts (Chat, AutoPrompt, batch operations) used to be a separate checklist on the AI Settings tab, which was easy to miss: enabling a termbase for term recognition did not include it in the AI context, and the two controls lived on different tabs. The AI-inclusion toggle is now an "AI" column in the termbase grid, right alongside the existing Read / Write / Project toggles, so it sits where you manage termbases. Click the column header to select or deselect all, and MultiTerm termbases can be toggled here too. The old AI Settings checklist has been retired in favour of a pointer to the new column.
- **AutoPrompt warns before generating a prompt from a large termbase.** A small, project-focused termbase produces a far better AutoPrompt glossary than a big general one; a large termbase injects many incidental whole-word matches – common words that merely happen to appear in the document. When the termbase(s) enabled for AI hold more than 50 terms, AutoPrompt now shows a dismissible warning recommending a compact, project-specific termbase before it spends a generation.
- **AutoPrompt records which termbase terms it injected.** Each run now logs the exact list of terms TermScan passed into the prompt – and how many, from which termbases – to the diagnostic log, so the terminology that reaches the AI can be audited after the fact.

### Fixed (AutoPrompt: TermScan false positives)

- **AutoPrompt no longer pulls in irrelevant terms through single-character abbreviations.** TermScan filters a termbase down to the terms that appear in the document, but it also matched a term's abbreviations as whole words with no minimum length, so an entry carrying a one-letter abbreviation (for example "S" for a chemistry term) matched any stray "S" in the text and injected the whole entry into the glossary. The relevance filter now ignores single-character candidates, so this kind of noise no longer reaches the prompt; legitimate two-character abbreviations (UI, API, ID …) are unaffected.


## [4.20.43] – 2026-06-07

### Fixed (Shared TM bridge — language names)

- **Bridged TMs no longer reverse matches when a row's languages are stored as names rather than codes.** v4.20.42 orients each returned row by its own `source_lang`/`target_lang`, but that comparison only understood ISO codes (`en`, `nl-BE`). A Supervertaler TM can hold rows tagged with human-readable language *names* ("English"/"Dutch") – e.g. from segments saved by a project whose language pair was held as a display name. Those rows matched neither side of the per-row check, so orientation fell back to the TM-level direction and swapped a row that was already correctly oriented, inserting the source language into the target. `CulturesCompatible` now normalises through `LanguageUtils` (the same name/code comparator used for termbase direction), so "English" ≡ "en" ≡ "en-US" and "Dutch" ≡ "nl" ≡ "nl-BE". Studio again sees correct project-source → project-target hits. (Workbench v1.10.253 additionally normalises languages to codes on write, so new rows avoid the mixed tagging in the first place.)


## [4.20.42] – 2026-06-02

### Fixed (Shared TM bridge — per-row direction)

- **Bridged TMs now return matches even when the TM mixes both directions.** v4.20.41 made a bridged TM attach to a project regardless of its declared direction, but it then assumed the *whole* TM was stored in one direction and searched only one column — so segments saved into the TM in the *opposite* direction to its declared one were silently missed. (Real case: an nl→en TM into which en→nl segments had been saved from an en→nl project — the lookup searched the English-target column and never found the rows whose English sat in the *source* column.) A Supervertaler TM's direction is a **per-row** property, so the bridge now (1) searches **both** the source and target columns for exact and concordance lookups, and (2) orients **each returned row individually** by its own `source_lang`/`target_lang` (on the base language, en≡en-US, nl≡nl-BE), falling back to the TM-level direction only when a row's languages are missing. Studio still always sees correct project-source → project-target hits.


## [4.20.41] – 2026-06-02

### Fixed (Shared TM bridge)

- **Bridged Supervertaler TMs now attach to a project regardless of their stored direction.** A TM bridged from Supervertaler Workbench was only offered to a Studio project when it was stored in the *exact same* direction as the project — so a Dutch→English TM was invisible to an English→Dutch project, with the misleading "None of the bridged Supervertaler TMs match this project's language pair" message. Matching is now **direction-agnostic and on the base language** (so `en` matches `en-US`, `nl` matches `nl-BE`, in either orientation): a TM is offered whenever it covers the project's two languages, whichever way round it was created. Crucially, the lookup follows through — when a TM is attached in the reverse direction, exact and concordance searches query the TM's *other* column and swap source/target on the way out, so Studio still sees correct project-source → project-target hits rather than empty or backwards results. (Regional-variant matching was already correct; the gap was the direction check.)


## [4.20.40] – 2026-05-29

### Changed (Claude Opus 4.8)

- **Upgraded the curated Claude flagship from Opus 4.7 to Opus 4.8** (`claude-opus-4-8`), Anthropic's new most-capable model (released 28 May 2026, 1M context, same $5 / $25 per 1M token pricing as 4.7). Updated in both the Claude and OpenRouter (`anthropic/claude-opus-4.8`) lists, and in the cost estimator. The Claude list stays a clean three: **Sonnet 4.6** (recommended), **Haiku 4.5** (fast/cheap), **Opus 4.8** (highest quality). Anyone still pointing at Opus 4.7 keeps working via the Model ID field.


## [4.20.39] – 2026-05-29

### Added (SuperSearch: live filter)

- **New "Live" toggle in SuperSearch.** When ticked, typing in the Src/Tgt boxes narrows the *current* results in place (instantly, in memory) instead of re-running a full file/TM search on every keystroke — useful for drilling into a large result set. Press Enter / Search to run a fresh full search and refresh the set. The status bar shows "N of M result(s) (live filter)". Honours the Case/Regex/Word toggles; an incomplete regex while typing simply matches nothing until it's valid.


## [4.20.38] – 2026-05-29

### Changed (SuperSearch: separate Source and Target boxes)

- **SuperSearch now has separate Source and Target search boxes** instead of one box plus a Source/Target/Both scope dropdown. Fill the **Src** box to search source text, the **Tgt** box to search target text, or **both** to find segments whose source matches one term *and* whose target matches another (memoQ-style concordance). Each box's term is highlighted in its own column and preview pane. Find & Replace continues to operate on the target text, using the Target box. (Progressive/live filtering is a follow-up.)


## [4.20.37] – 2026-05-29

### Changed (Clipboard Mode)

- **Stronger output-format instructions for Clipboard Mode translation.** Some web LLMs (notably DeepSeek's web chat) reformatted the reply into a bare list and dropped the "Segment N" headers, so the result couldn't be re-imported into Trados. The clipboard prompt now insists, explicitly, that every "Segment N" header be kept in order with both language lines, and that the output not be turned into a list, table, or prose – because the headers are what the importer uses to map translations back to segments.


## [4.20.36] – 2026-05-28

### Changed (Licence)

- Licence-file save failures are now logged instead of being silently ignored, so a trial that can't persist its start date no longer goes unnoticed.


## [4.20.35] – 2026-05-28

### Changed (Licence)

- Trial-period tracking now also resists system-clock changes.


## [4.20.34] – 2026-05-28

### Changed (Licence)

- More reliable trial-period tracking across reinstalls and data-folder moves.


## [4.20.33] – 2026-05-27

### Changed (Shared TM Bridge: per-hit TM attribution)

- **Each match now identifies which bridged TM produced it.** Previously, every result returned by the bridge stamped `OriginSystem = "Supervertaler"`, so when a user had several bridges attached (e.g. BRANTS + PATENTS), the grey origin strip under each hit in the Translation Results pane just said "Supervertaler" with no way to tell which TM the hit came from. Now the OriginSystem reads e.g. "Supervertaler: BRANTS (URSU-008-BE-EP)" or "Supervertaler: PATENTS", matching the names shown in the project's TM list.


## [4.20.32] – 2026-05-27

### Fixed (Shared TM Bridge: critical – wrong "100%" matches no longer inserted)

- **`SearchTranslationUnit` now does true exact-source lookup instead of concordance.** This is a serious correctness fix. Trados Studio's per-segment matching in the editor and in batch tasks routes through `SearchTranslationUnitsMasked → SearchTranslationUnit`, NOT `SearchSegment` (which the SDK docs imply but Studio doesn't actually call in document context). Prior builds (v4.20.26 – v4.20.31) wrongly implemented `SearchTranslationUnit` as an FTS5 concordance search, then scored every substring hit as 100%. The practical effect: a segment with source "GEDETAILLEERDE BESCHRIJVING" would FTS5-match any TM row whose source or target contained "gedetailleerde" OR "beschrijving" – including a totally different row like "De gedetailleerde beschrijving beschrijft huidige uitvinding in zijn voorkeurdragende uitvoeringsvormen" / "The detailed description describes the preferred embodiments of the present invention" – and Studio would insert and auto-confirm that wrong translation at 100%.
- **Now**: `SearchTranslationUnit` extracts the TU's source segment, runs the same byte-exact `WHERE source_text = $src` lookup as `SearchSegment`, and returns only true 100% matches. Concordance lives where it always should: `SearchText`.
- **Query/result diagnostic logging.** Both `SearchSegment` and `SearchTranslationUnit` now log the query text plus the exact-match count to `%TEMP%\supervertaler-tm-bridge.log`. This made the previous build's bad behaviour easy to diagnose ("`SearchTranslationUnitsMasked` is hot, `SearchSegment` is cold") and gives quick feedback that the fix is producing the right exact-match counts going forward.

### Action for users on prior builds

- **Inspect any segments confirmed against bridged TMs in v4.20.26 – v4.20.31** – their targets may be totally unrelated text drawn from a same-token concordance hit. Filter by "Translated" + "Confirmed by Supervertaler TM" and re-review. The 100% match icon was not trustworthy in those builds.


## [4.20.31] – 2026-05-27

### Fixed (Shared TM Bridge: ACTUAL root cause of the "Object reference" NRE)

- **`SearchResults.SourceSegment` is now guaranteed non-null on every code path.** That was the bug. Studio's internal `SearchResultsMerged.CopyFromSearchResults(SearchResults other)` does `base.SourceSegment = other.SourceSegment.Duplicate();` with **no null guard** – decompiled from `Sdl.LanguagePlatform.TranslationMemory.dll`. Any provider that returns a default-constructed `new SearchResults()` (which has `SourceSegment = null`) makes Studio's `Cascade.MergeSearchResults` throw NullReferenceException deep inside `SegmentAndSubsegmentSearchResultsMerged..ctor`. v4.20.26 through v4.20.30's `SearchText`, `SearchTranslationUnit`, and (for masked-out slots) `SearchSegmentsMasked` / `SearchTranslationUnitsMasked` all returned such bare SearchResults objects – every concordance call or batch operation that hit those paths bombed Studio with the generic "Object reference not set to an instance of an object." dialog.
- **New `NewSearchResults` / `NewSearchResultsFromText` helpers** always pre-populate `SourceSegment` with either a duplicate of the input Segment or a fresh empty Segment stamped with the LD's source culture. Every search method on the LanguageDirection now routes through them. Cascade can now merge our results without choking.
- **`SafeLog` helper for entry logging.** The original `SearchSegment` entry log evaluated `segment.ToPlain()` inside string concatenation – if `ToPlain()` ever threw (it can, on certain tag-only segments), the whole log line threw before writing, the method exited abnormally, and no entry log appeared in `%TEMP%\supervertaler-tm-bridge.log`. `SafeLog` wraps the call in try/catch so a misbehaving stringification can't take down the method that's trying to record it.
- **Entry logging on the remaining concordance methods** (`SearchText`, `SearchTranslationUnit`, `SearchTranslationUnits`, `SearchTranslationUnitsMasked`) – the v4.20.30 build missed these, which is why the "no entries" log was so confusing. With Studio routing concordance and batch searches through them, the entry logs should now appear when the user looks something up in the Concordance window.

### Investigation notes

- The smoking gun was Studio's own log at `%AppData%\Trados\Trados Studio\Studio18\logs\Trados Studio_*.log`, which contained the full stack trace: `Cascade.ExecuteSearchCommand` → `Cascade.MergeSearchResults` → `SegmentAndSubsegmentSearchResultsMerged..ctor(SegmentAndSubsegmentSearchResults results)` → `SearchResultsMerged.CopyFromSearchResults(SearchResults other)` → NRE at `other.SourceSegment.Duplicate()`. Decompiling `Sdl.LanguagePlatform.TranslationMemory.dll` with ilspycmd confirmed `CopyFromSearchResults` does no null check on `SourceSegment`.
- Three prior builds attacked the wrong layer: v4.20.27 added defensive null-handling in our own search methods; v4.20.29 wrongly inferred `SupportsTranslation=true` was the problem (it's the gate Trados uses to even call search); v4.20.30 added entry logging that revealed the call patterns but not the offending field.


## [4.20.30] – 2026-05-27

### Changed (Shared TM Bridge: diagnostic build – reverts v4.20.29's wrong fix)

- **`SupportsTranslation` reverted to `true`.** v4.20.29's hypothesis that this flag meant "MT-style provider" was wrong. The %TEMP%\supervertaler-tm-bridge.log from v4.20.29 made the actual semantics clear: per-segment, Trados queries `LanguageDirection.TranslationProvider` → `Provider.TranslationMethod` → `Provider.SupportsSearchForTranslationUnits` → `Provider.SupportsTranslation` and only proceeds to call `SearchSegment` when that last flag is `true`. Setting it to `false` produced "The translation provider X does not support translation." warnings and zero lookups.
- **Entry logging on every method of the LanguageDirection** – all write methods (`AddTranslationUnit`, `AddTranslationUnits`, `AddOrUpdateTranslationUnits`, `AddTranslationUnitsMasked`, `AddOrUpdateTranslationUnitsMasked`, `UpdateTranslationUnit`, `UpdateTranslationUnits`) and the remaining search variants (`SearchSegments`, `SearchSegmentsMasked`) now log on entry. With `SupportsTranslation = true` back in place, the next failing build will show in the log exactly which method Studio calls between the capability poll and the (still missing) `SearchSegment ENTRY`. That's almost certainly where the residual "Object reference" NRE originates – Studio is probably calling a method we haven't routed cleanly.


## [4.20.29] – 2026-05-27

### Fixed (Shared TM Bridge: root cause of the "Object reference" NRE)

- **`SupportsTranslation` now reports `false`.** The previous v4.20.26/v4.20.27 builds advertised the bridge as `SupportsTranslation = true`, which in the Trados SDK signals an **automated-translation (MT-style) provider** rather than a TM lookup provider. With that flag on, Trados Studio invoked an MT-engine code path that doesn't exist for this provider, and the resulting NRE bubbled up to the UI as "An error has occurred while using the translation provider Supervertaler TM: <name>: Object reference not set to an instance of an object." on every segment, with no entry in our own log because the failure happened *inside* Trados before `SearchSegment` was reached. Stock SDLTM providers report `false` for the same reason; the bridge now matches that convention.
- **Comprehensive property-getter instrumentation** on both `SupervertalerTmProvider` and `SupervertalerTmLanguageDirection`. Every capability flag, every identity property, and every culture accessor logs on read to `%TEMP%\supervertaler-tm-bridge.log`. If a future build sees the bridge polled in a tight loop without ever reaching search, the log now pinpoints exactly which read Studio is making before giving up.

### What translators see

- Bridged Supervertaler Workbench TMs now actually return hits in the Trados TM-results pane and Concordance window. The same loose-locale matching from v4.20.26 (so `nl` matches `nl-NL`) applies.


## [4.20.27] – 2026-05-27

### Fixed (Shared TM Bridge: defensive null-handling + diagnostics for "Object reference" errors)

- **First testing of the v4.20.26 SupervertalerTmProvider against a real Trados Studio session surfaced "An error has occurred while using the translation provider Supervertaler TM: <name>: Object reference not set to an instance of an object." errors on every lookup,** with no usable stack trace in Studio's Messages panel. This release adds the infrastructure to diagnose and prevent that class of failure.
- **New `TmBridgeLog`** (file logger at `%TEMP%\supervertaler-tm-bridge.log`). All provider-side error paths now log with full stack traces before swallowing the exception. Append-only with size-capped rotation.
- **Defensive null-handling in `SearchSegment`**: previously dereferenced `segment.Duplicate()` before null-checking, which would throw NRE on Studio's pre-flight calls. Now null-safe at every level; falls through to an empty `SearchResults` on any failure instead of throwing.
- **`SearchText` and `SearchTranslationUnit`** get the same treatment – defensive null handling, instrumented with `TmBridgeLog` so failures are diagnosable.
- **New `TryBuildSearchResult` helper** wraps the per-row `TranslationUnit` construction in try/catch. One bad row in the result set no longer poisons the rest of the batch; it's logged and skipped.
- **Write methods (`AddTranslationUnit`, `UpdateTranslationUnit`, etc.) no longer throw `NotSupportedException`.** Although `IsReadOnly = true` and `SupportsUpdate = false` should keep Trados from ever calling these, batch-tasks pipelines (notably "Update Main Translation Memories") have been observed to call them speculatively, and the thrown exception bubbles up as a generic provider error. They now return a safe empty `ImportResult` (no-op).

### Changed

- **Sv icon on the "Add Supervertaler TM" picker dialogue** + on the project's translation-provider list (via `TranslationProviderDisplayInfo.TranslationProviderIcon`). Picks up the same shared icon every other plugin dialogue already uses.
- **Culture-pair empty-state logging.** If `BuildTranslationUnit` is ever called with an empty source/target culture, a warning lands in the log – this lets us catch language-pair-filtering bugs without silent fallout.


## [4.20.26] – 2026-05-27

### Added (Shared TM Phase 2: read-only access to Supervertaler Workbench TMs)

- **New translation-provider plugin: "Supervertaler TM Bridge".** Trados can now attach individual TMs that live in Supervertaler Workbench's shared SQLite database (`supervertaler.db`) directly, without any TMX export / import dance. Phase 2 of the Shared TM work tracked in #31, building on the Workbench-side opt-in flag shipped in Workbench v1.10.212.
- **Discovery via Trados' standard "Add → Translation provider" menu.** A new "Supervertaler TM" entry shows up alongside the standard SDLTM, GroupShare, etc. options. Clicking it opens a picker dialogue listing every TM the user has flagged "Bridge" in Workbench's TMs tab, filtered to those matching the current project's language pair (loose match: bare `nl` matches `nl-NL`, etc.). Tick one or more to attach.
- **Live data.** Both products read and write the same `supervertaler.db`. Adding a new entry to a bridged TM via Workbench's editor surfaces in Trados on the next lookup, no sync step. Conversely, Trados's read flows through to anything Workbench shows.
- **Exact match (100%) and concordance search supported.** Studio's TM-results pane shows exact hits with the standard 100% chip; the Concordance window searches source-side AND target-side via the FTS5 index Workbench already maintains.

### Phased scope (this is the read-only half)

- **Read-only in v1.** `SupportsUpdate = false`, `IsReadOnly = true`. Add/Update/Delete methods throw `NotSupportedException` defensively – any consumer ignoring the read-only flag fails loudly rather than silently dropping data. Write-back lands in Phase 3.
- **No fuzzy matching yet.** `SupportsFuzzySearch = false`. The provider returns *only* 100% matches; sub-100 fuzzy comes in Phase 3 once a Levenshtein-on-candidates scorer is in place. For now, fuzzy lookups fall through to any other providers attached to the project (e.g. the user's main SDLTM).

### Implementation notes

- New `Core/TmReader.cs` mirrors the existing `TermbaseReader.cs` shape (read-only SQLite via `Microsoft.Data.Sqlite`, no native-DLL interop). It only reads TMs where `bridged_to_trados = 1`, so freelancers with multiple-client TM libraries don't see cross-client leakage in a Trados session opened on a specific project.
- New `TranslationProviders/SupervertalerTmProvider.cs` + `SupervertalerTmLanguageDirection.cs` + `SupervertalerTmProviderFactory.cs` + `SupervertalerTmProviderWinFormsUI.cs` implement the Trados-side surface area. URI scheme is `supervertaler-tm:///<tm_id>` (using the stable string id Workbench stores in `translation_units.tm_id`).
- plugin.xml gains two `<extension>` entries – one for the factory, one for the WinForms UI – using the `TranslationProviderFactoryAttribute` / `TranslationProviderWinFormsUiAttribute` types from `Sdl.LanguagePlatform.TranslationMemoryApi`.

### What translators see

- **Workbench**: tick the orange "Bridge" checkbox next to a TM in the TMs tab. That's it.
- **Trados**: Project settings → Translation Memory and Automated Translation → Use → Add → Supervertaler TM. Pick the TM(s) from the dropdown.
- **The TM behaves like any other Trados TM** after that – exact matches and concordance hits show up in the standard panes, attribution shows "Supervertaler" as the origin system.


## [4.20.25] – 2026-05-26

### Changed

- **"Term Picker" renamed to "TermPicker"** (one word, Pascal-cased, no hyphen, no space). Pure naming cleanup aligned with Supervertaler Workbench v1.10.205 — both products now use the same name for the same surface.
- **Architecture clarification.** TermLens and TermPicker are now treated as **sibling surfaces** sitting on top of termbases, rather than the old "TermLens — Term Picker" framing where the picker felt like a sub-mode of TermLens. Underneath both sits the termbase layer; TermLens shows matches *in context* (inline chips anchored to source terms — the invention), TermPicker shows them as a flat sortable list with keyboard-driven Enter-to-insert. Same data, different ergonomics.
- **Window title** of the modal dialog: `TermLens — Term Picker` → `TermPicker`.
- **About dialog** keyboard-shortcuts table: "Term Picker (memoQ-style)" / "Term Picker (alternative)" → "TermPicker (memoQ-style)" / "TermPicker (alternative)".
- **Help URL slug** `trados/termlens/term-picker/` → `trados/termlens/termpicker/`. Old URL 301-redirects to the new one via `public/_redirects` on the help site, so existing bookmarks (and F1 from older installed plugin versions where the old slug is still baked into HelpSystem.cs) keep working. The C# field name `HelpSystem.Topics.TermPickerDialog` is unchanged — only its URL value moved.
- **README, HELP-LINKS, CLAUDE.md, and internal code comments** updated to use "TermPicker" everywhere.

### Kept (internal, deliberately not renamed to avoid breaking user data/customisations)

- File `Controls/TermPickerDialog.cs` and class `TermPickerDialog` — already correct camelcase
- File `TermPickerAction.cs` and class `TermPickerAction`
- Model class `TermPickerMatch`
- Plugin.xml action ID `TermLens_TermPicker` (the Ctrl+Shift+P binding) — would break custom shortcut remappings if changed
- Settings property names `TermPickerWidth`, `TermPickerHeight`, `TermPickerColumnWidths` — would wipe persisted layout
- Historical CHANGELOG entries and RWS AppStore release notes — left as-is, they are historical record

### Notes

The rename is mostly text. No behaviour changes. F1 from the dialog still opens the help page (now under the new slug); existing custom shortcut bindings for the Ctrl+Shift+P action still trigger the same code path; persisted dialog size/column widths still load. If you've been using "Term Picker" verbally, "TermPicker" is what we'll call it from here on.


## [4.20.24] – 2026-05-26

### Fixed

- **Bracketed `[SEGMENT NNNN]` Markdown re-import: segments with empty target on export now round-trip correctly.** A user with a multi-file project where many segments were untranslated noticed that some edits made to those rows didn't land in Trados after re-import. Root cause: the lang-line regex used `\s*` after the colon for trailing whitespace, but .NET treats `\s` as matching newlines too — so on a line like `NL: \n` the engine would greedily consume the newline AND the next line, capturing `Status: Unspecified` (etc.) as the body instead of the intended empty string. Subsequent re-import then either wrote the wrong content back or compared against the wrong baseline. Tightened the regex to `[ \t]*` so matching stays on the current line. Also made target selection more tolerant: the parser now picks the **last** non-`Status` lang-line in each block as the target (rather than the second), so a proofreader who edits by inserting an extra `NL: …` line after the empty placeholder still gets their actual translation captured rather than the empty line.

### Added

- **New confirmation-status filter on the Import/Export tab.** Six checkboxes (one per Trados `ConfirmationLevel` value: *Unspecified*, *Draft*, *Translated*, *Approved (translation)*, *Approved (sign-off)*, *Rejected*) let the user restrict the export to only segments in selected statuses. All checked (the default) = no filter, every segment included — matches pre-v4.20.24 behaviour. Unticking any subset narrows the export accordingly. Composes orthogonally with the existing **Include locked segments** option and the multi-file file-selection list.


## [4.20.23] – 2026-05-25

### Changed

- **Cleaned up stale license-tier documentation** to match Lemon Squeezy's current single-product layout. The store now sells exactly one product, **Supervertaler for Trados**, and has done since the multi-tier model was dropped in v4.18.48. Three things touched, no behavioural change (licence validation has been variant-name-agnostic since v4.18.48):
  - Removed three dead constants from `LicenseManager.cs` (`VariantTier1 = "TermLens"`, `VariantTier2 = "TermLens + Supervertaler Assistant"`, `VariantAssistant = "Supervertaler Assistant"`). They were never referenced — `MapVariantToTier()` ignores the variant name entirely and always returns `LicenseTier.Licensed` for any valid key.
  - Updated `MapVariantToTier()`'s XML doc comment to note the single-product Lemon Squeezy layout.
  - Updated `LicenseInfo.VariantName`'s XML doc comment, which previously documented the legacy "TermLens → Tier1" mapping table.
- Old cached `license.json` files from previous installations (with `VariantName` set to any of the legacy strings) still deserialise cleanly and unlock everything.


## [4.20.22] – 2026-05-25

### Fixed

- **Panel rename to "Supervertaler" finally takes effect in Trados.** v4.20.21 changed the `[ViewPart]` C# attribute's `Name` field, but Trados ignores that — it reads the panel title from `<property name="Name">…</property>` inside `Supervertaler.Trados.plugin.xml`. That file is generated from another source (Trados' build tooling) and the auto-rename didn't propagate. Patched the value in-place. The Trados panel header + dockable-tab strip now show **Supervertaler**.


## [4.20.21] – 2026-05-25

### Changed

- **Renamed the dockable panel from "Supervertaler Assistant" to "Supervertaler"** in every user-visible string: the Trados panel title (set via the `ViewPart` attribute), the chat-bubble name (`Supervertaler` instead of `Supervertaler Assistant`), the help-menu top item (`Supervertaler Help`), the legacy-memory-bank first-run dialog header, the "Saved on ... from Supervertaler" memory-bank line, and the QuickLauncher context-menu action (`Send to Supervertaler`). Internal class names and the Lemon Squeezy licence-variant constants are unchanged (variants are contractually bound to existing subscriptions; touching them would invalidate already-issued licences).
- **Renamed the in-panel tab "Import / Export" to "Import/Export"** (no spaces around the slash). Updated the tab title, the tab's heading label, and the matching help-menu item ("Import/Export Help").


## [4.20.20] – 2026-05-25

### Added

- **New "Bracketed `[SEGMENT NNNN]`" layout** for Markdown exports. Matches the Supervertaler Workbench's "AI-readable" segment-export style:
  ```
  [SEGMENT 0001]
  EN: <b>MASHUP APPLICATION PROCESSING SYSTEM</b>
  NL: <b>MASHUP-APPLICATIEVERWERKINGSSYSTEEM</b>

  [SEGMENT 0002]
  ...
  ```
  One block per segment, blank-line separated, with 2-letter ISO language codes labelling each language line. Re-importable just like the other layouts — the bracketed segment-number is the anchor. Some LLMs reportedly handle this format more reliably than markdown tables.
- **Available as the 4th option in the Layout dropdown** on the Import / Export tab: *Bracketed [SEGMENT NNNN] (AI-friendly, Markdown only)*. The Markdown renderer + importer both understand it; DOCX and HTML fall back to Stacked source-on-top (the bracketed format only makes sense as plain text).
- **Multi-file affordance preserved**: when the bilingual file spans multiple source files, a `## 📄 File: <name>` heading appears before the first segment of each new file, same as the other stacked layouts.


## [4.20.19] – 2026-05-25

### Added

- **Markdown bilingual export now shows per-file attribution in multi-file projects.** A user noted the Markdown version lacked any indication of which file each segment came from, while the DOCX version had a dedicated File column + yellow section-break rows between files. Brought to parity:
  - **Table layout**: when the export spans more than one source file, the table grows a 6th **File** column and a section-break row (`| | **📄 File: <name>** | | | | |`) precedes the first segment of each new file. Single-file exports are unchanged.
  - **Stacked layouts** (source-on-top / target-on-top): a `## 📄 File: <name>` heading appears before the first segment of each new file.
- **Re-import handles the new 6-column Markdown table** transparently. The importer auto-detects 5- vs 6-column rows by cell count and shifts its parser to read Status / Notes from the right offset. Older 5-column files (and 6-column files with extra layout) keep parsing cleanly.


## [4.20.18] – 2026-05-25

### Added

- **Locked segments are now visible and filterable in the Import / Export tab.** Two related behaviours:
  - **New checkbox: "Include locked segments (🔒 marked in Status column)"** — default ON (matches pre-v4.20.18 behaviour). When ON, locked segments are exported alongside everything else and get a **🔒** prefix in the Status column (e.g. `🔒 ApprovedTranslation`) so the proofreader sees at a glance which rows won't round-trip back to Trados. When OFF, locked segments are skipped entirely — useful on large projects where most segments are locked-approved and the proofreader should only see what's still editable. A user with a 14-file, 7308-segment project noted that locked segments came through without any indicator, leading them to edit segments that couldn't be saved back to Trados.
  - **Re-import now actually refuses to overwrite locked segments.** `SnapshotLockedSegments()` was a stub returning an empty set since v4.20.7 — meaning re-import would happily write back to locked segments. Now it walks the active document and reads `pair.Properties.IsLocked` for real, populates the lookup, and `BilingualImporter`'s existing `isWriteable` predicate uses it to skip locked segments (they show up under "other issues" in the re-import summary).
- **Locked flag is persisted in the sidecar manifest** as `is_locked: true/false` per segment, so older manifests (without the field) parse cleanly as `false`.


## [4.20.17] – 2026-05-25

### Fixed

- **Help button on the Import / Export tab now opens the right page.** v4.20.16 put a brand-new "?" inside the Import / Export tab content (in addition to the panel-level "?" that's existed in the top-right of the Supervertaler Assistant pane for ages), AND the panel-level button still routed to the Reports help page because its tab-index switch hadn't been updated when Import / Export was inserted between Batch Operations and Reports (case 2 was still mapped to Reports, case 3 to SuperSearch, etc.). Two fixes in one commit:
  - Removed the in-tab "?" button — the panel-level one is the right home for tab help, matching every other tab in the pane.
  - Added `HelpSystem.Topics.ImportExport` and slotted it into both `OnHelpDropdown` and the F1 keyboard handler at the correct tab index (2), shifting Reports → 3 and SuperSearch → 4.

## [4.20.16] – 2026-05-25

### Added

- **Contextual "?" help button on the Import / Export tab.** Top-right corner, anchored to the right edge so it follows panel resizes. Click to open the dedicated [Import / Export help page](https://help.supervertaler.com/trados/import-export/) on help.supervertaler.com — covers formats, layouts, semantic tag markers, multi-file mode, round-trip rules, and the re-import workflow in one place.

## [4.20.15] – 2026-05-25

### Fixed

- **Import / Export tab now scrolls vertically on smaller screens.** A user reported that on laptop monitors the log textbox sat below the bottom of the panel and was unreachable. Set `AutoScroll = true` on the control + computed `AutoScrollMinSize` from the final content height so the scrollbar appears whenever the tab is shorter than its content. The log textbox now has a fixed 160 px minimum height (instead of stretching to fill the panel) so the layout is well-defined regardless of viewport size.
- **Inline-formatting caveat label fits on big-monitor too.** Even at 72 px the four-line paragraph clipped its final phrase on wider monitors (where the text re-flowed differently). Dropped the "rendered in matching bold/italic/underline for preview" sentence (it's already obvious from looking at the table) and the label is now 56 px / three lines.

## [4.20.14] – 2026-05-25

### Fixed

- **Inline-formatting caveat on the Import / Export tab no longer truncates.** The descriptive blurb above the format/layout pickers was clipped at "Text inside semantic markers is rendered" because the label's height (46 px) couldn't hold the full four-line paragraph. Bumped to 72 px and tightened the wording.

## [4.20.13] – 2026-05-25

### Fixed

- **Re-import diff no longer over-reports changes for tag-bearing segments.** A user noted that after exporting 1267 segments and editing literally one word in the bilingual DOCX, the re-import dialog claimed 124 changes to apply. Root cause: the export side serialised each target through `SegmentTagHandler.Serialize()` + `BilingualTagNamer.ApplySemanticNames()` (so cells contain `<b>SEVT</b>` etc.), but the diff-comparison side fetched the current target via plain `pair.Target.ToString()` (which yields `SEVT`). Every segment whose target contained any inline cf-bold / cf-italic / cf-underline formatting therefore read as "changed" even when the proofreader hadn't touched it. `SnapshotCurrentTargets()` now uses the same serialisation pipeline as the exporter, so the diff is apples-to-apples and only actual edits get counted. The writeback path was already converting semantic markers back through `ResolveSemanticNames` + `ReconstructTarget`, so no change needed there.

## [4.20.12] – 2026-05-25

### Fixed

- **Bilingual DOCX table now fills the page width.** v4.20.11 used fixed-DXA column widths (9000 twips ≈ 6.25") which kept the table inside the page edge but left the right half of wider pages (A4 landscape, US Letter landscape, narrow-margin layouts) empty. Switched to percentage-based widths: `TableWidth = Pct 5000` (= 100% of section width) plus per-column percentages summing to 5000, with `TableLayout = Fixed` still on top to keep long content from blowing columns out. Table now expands to fill whatever the page gives it.

## [4.20.11] – 2026-05-25

### Fixed

- **Bilingual DOCX table no longer overruns the page.** v4.20.10 used percentage-width columns with the default `auto` table layout, which let Word stretch the table past the page edge when content (long file names, long source paragraphs) demanded it – the Status column got clipped to "Transla" in multi-file exports. Switched to fixed-DXA column widths (9000 twips total ≈ 6.25", fits A4 portrait + US Letter with default margins) plus a proper `<w:tblGrid>` and `TableLayout = Fixed` so Word respects the declared widths regardless of content length.
- **"None" button now actually clears the segment count.** Empty file selection in multi-file mode was being treated as "no filter, count everything" (a leftover from the single-file branch of the same code path), so clicking **None** left the count at the document total. Now multi-file mode correctly returns 0 segments when no files are checked. Single-file mode (where the file list is hidden) is unchanged.
- **No more blue "selected" highlight on the file rows.** Even though v4.20.10 already replaced `CheckedListBox` with plain `CheckBox` rows, the default WinForms focus rectangle made the focused row look quasi-selected. Added `TabStop = false` + `FlatStyle = System` to each row so it renders as the OS-native checkbox with no focus indicator. One click = one toggle. No selection state.

## [4.20.10] – 2026-05-25

### Fixed

- **Per-file segment attribution now works on real multi-file projects.** v4.20.9 walked paragraph-unit contexts looking for file metadata, but the v4.20.9 diagnostic dump (kindly provided by a user testing a 3-file Dutch project) revealed Trados puts **zero** file-identifying info in PU contexts – only paragraph-styling and header/footer flags. New approach: for each `ProjectFile.LocalFilePath` (the on-disk SDLXLIFF), read the file once and extract every GUID from it via regex. Trados paragraph-unit ids are GUIDs and are globally unique, so the set of GUIDs in file A's SDLXLIFF is exactly the set of PU ids belonging to file A. Then each segment pair's parent PU id is looked up against that table. Runs once per active-document change.
- **As a consequence, all the visible v4.20.9 bugs go away in one shot:**
  - "(0 segments)" beside each filename in the files-to-export list now shows the real count.
  - Checking / unchecking a file in the list now actually changes the "Segments: N" label.
  - Multi-file combined-DOCX export now produces the **6-column table with the File column** plus the **yellow "📄 File: \<name\>" section-break rows** between each file's segments (the renderer auto-switches when segments carry non-empty `SourceFileId`s).
  - "Separate DOCX per file" mode now produces one bilingual file per source file as advertised.

## [4.20.9] – 2026-05-25

### Fixed

- **Multi-file projects: per-file segment counts and the file-selection filter now actually work.** The v4.20.8 attribution map walked `IFile.ParagraphUnits` to figure out which segment belonged to which file, but `ProjectFile` in Studio 18 + 19 doesn't expose that collection at all – the map ended up empty, so checking any file in the list gave "Segments: 0" and Export emitted nothing. Replaced with the right SDK path: walk every segment pair, call `Document.GetParentParagraphUnit(pair)`, then match the paragraph unit's context-stack strings (DisplayName, Description, `FilePath` / `OriginalFilePath` metadata, etc.) against each file's Name / OriginalName / LocalFilePath / basename. Works in single-file and multi-file mode.
- **Graceful degradation when attribution can't be built.** If the SDK still doesn't surface enough info to attribute segments (e.g. an unusual file-type configuration), the file-selection filter is silently ignored and the export proceeds with every segment in the active view. A log line in the Import / Export tab explains what happened. Better than emitting an empty file.

### Changed

- **Files-to-export list redesigned.** Replaced the `CheckedListBox` (which had a confusing distinction between "highlighted" and "checked" – clicking on a row's text would change the highlight without flipping the check, or vice versa) with a scrollable panel of plain checkbox rows. One click = one checkbox toggle. No more selection vs. check confusion.

## [4.20.8] – 2026-05-25

### Added

- **Multi-file bilingual export.** Trados projects with multiple files merged into one editor tab now get a dedicated file list in the Import / Export tab, plus an output-mode chooser:
  - **File list** — a CheckedListBox showing every file in the active (merged) document with its segment count. Quick-select buttons ([Active only] / [All] / [None]) for common selections. The "Segments: N" label tracks the current selection live.
  - **Output mode** — *"Combine into one DOCX"* (default) produces a single bilingual file containing all selected files joined together, OR *"Separate DOCX per file"* asks for a folder and writes one bilingual file per selected source file.
  - **Single-file documents see no change** — the file list, output radio, and per-file UI are all hidden so the tab looks exactly as before.

- **File column + yellow section breaks in the combined DOCX.** When the export contains segments from more than one source file, the bilingual table grows from 5 to 6 columns — `#, Source, Target, File, Status, Notes`. Between each file's segments, a full-width yellow-highlighted section-break row appears reading "📄 File: `<filename>`", so the proofreader can spot file boundaries at a glance.

- **Per-segment file id + name in the manifest.** Each manifest entry now records the segment's `source_file_id` (Trados GUID) and `source_file_name` (e.g. "Chapter2.docx"). Re-import uses the file id to route each diff to the correct file in the merged document. Manifests stay backwards-compatible — older single-file manifests parse cleanly into the new fields as empty strings.

### Fixed

- **"Segments: N" label on the Import / Export tab now actually updates.** Was an unwired UI control sitting at 0 since v4.20.7. Now reflects the active document's total segment count (or the current selection's count, in multi-file mode), updating on every active-document change and file-list selection change.

## [4.20.7] – 2026-05-24

### Added

- **New "Import / Export" tab in the Supervertaler Assistant panel.** A dedicated home for bilingual review workflows that sits between Batch Operations and Reports. Three export formats and three layouts:
  - **Formats:** Word document (`.docx`), Markdown (`.md`), HTML report (`.html`, client-facing, read-only).
  - **Layouts:** *Supervertaler Bilingual Table* (5-column table — `#, Source, Target, Status, Notes` — the canonical round-trippable shape, identical to Supervertaler Workbench's "Bilingual Table" so files can flow between both products), *Stacked source-on-top* (source paragraph above target paragraph, sentence by sentence), and *Stacked target-on-top* (target paragraph above source).
  - Every export writes both the file itself and a small **sidecar manifest JSON** (`<file>.svexport.json`) that records the project name, source filename, language pair, export timestamp, tool version, and the per-segment `(number → Trados ParagraphUnitId / SegmentId)` mapping with a source-text SHA-256 prefix. The manifest lets re-import locate the exact Trados segments even if the proofreader accidentally reorders rows.
  - A **Recent exports** list at the bottom of the tab tracks every export this session with **Open file** / **Open folder** / **Re-import this** buttons next to it.

- **Round-trip re-import (DOCX and Markdown).** Send a bilingual file to a proofreader, get it back, click **📥 Re-import…**, pick the file. Supervertaler:
  - Reads the file's segment rows (DOCX table or Markdown `## Segment N` blocks).
  - Loads the sidecar manifest if present (fully detected by file path: `<file>.svexport.json`); falls back to a current-document mapping with a warning if the sidecar is missing.
  - Diffs each row against the live Trados segment state: classifies every row as *unchanged*, *changed*, *segment-missing*, *source-mismatched*, or *locked / rejected*.
  - Shows a confirmation prompt with counts before any write happens.
  - Applies changes via the same `ProcessSegmentPair` writeback path the AI batch translator uses, so soft-return handling for Excel / Visio segments behaves the same way and locked/rejected segments are skipped automatically.
  - HTML is **not** re-importable by design — the HTML renderer is for client-facing review reports, not editing.

- **Embedded segment markers in every exported file** (`SV_seg_N` bookmarks in DOCX, `<!-- sv-seg:N -->` HTML comments in Markdown / HTML). These are invisible in normal rendering but let future versions match segments by ID even when human numbering drifts.

- **Full inline-tag round-trip with Workbench-style semantic naming.** Source and target text in the bilingual file now go through `SegmentTagHandler.Serialize()` (the same serialisation the batch AI translator already uses for inline-tag-aware prompts) and then through `BilingualTagNamer.ApplySemanticNames()`. The cells contain:
  - `<b>...</b>`, `<i>...</i>`, `<u>...</u>`, `<bi>...</bi>` for recognised cf character-formatting pairs (matches the Supervertaler Workbench's "With Tags" Bilingual Table style)
  - Numbered `<t1>...</t1>` (paired) / `<t2/>` (standalone) for everything else — field codes, page numbers, custom format pairs, line breaks, etc. — so no Trados tag type is ever silently dropped
  - All markers coloured dark red (#7F0001) in the DOCX so the proofreader can see the anchors at a glance
  - On re-import, `BilingualTagNamer.ResolveSemanticNames()` walks the proofreader's edit and converts each `<b>` (etc.) back to the matching `<tN>` via positional matching against a freshly-regenerated TagMap (deterministic numbering as long as the source hasn't drifted — `SourceHash` guards against that). `SegmentTagHandler.ReconstructTarget()` then rebuilds the target segment with the cloned tags wrapped around the proofreader's translated text
  - The proofreader can freely reorder tags to fit target-language word order, remove a marker pair to drop that formatting, or leave a segment unchanged
  - If the marker structure is broken (mismatched, unknown name, unresolvable), the writeback falls back to plain text with a per-segment log entry — same defensive pattern the batch AI translator uses

- **Structural-tag integrity check (toggleable, ON by default).** A new checkbox in the Import / Export tab — *"Refuse to apply edits that drop source-required tags (recommended)"* — protects against the most common production hazard of the round-trip. The check enforces **strict equality on structural tags** between source and the proofreader's edit. "Structural" means `<tN>` markers that didn't get a friendly semantic name from `BilingualTagNamer` — i.e. field codes, page numbers, custom format pairs, line breaks. These drive Trados file structure and must round-trip 1:1; adding one creates a tag the target file can't render, removing one drops a tag Trados expects. **Semantic formatting tags** (`<b>`, `<i>`, `<u>`, `<bi>`) are explicitly *not* counted — the proofreader can freely add or remove character formatting in the target without breaking Trados QA, since those changes only affect cosmetic rendering. When the check is ON, segments where `count(<tN> in source) ≠ count(<tN> in edit)` are classified as a new `TagMismatch` issue and not written; the confirmation dialog breaks the count out explicitly. When OFF (advanced use), tag-mismatched edits are applied verbatim with a per-segment warning in the log — you take responsibility for verifying Trados QA afterwards.

- **Segment-number column centred in the bilingual DOCX table.** Reads more naturally as a vertical column when centred, especially with mixed digit counts (1 / 21 / 121).

- **Inline-formatted text is now rendered with the matching style in the bilingual DOCX.** Previously the `<b>...</b>` / `<i>...</i>` / `<u>...</u>` / `<bi>...</bi>` markers appeared in red as tag anchors, but the text *between* them stayed unstyled. The proofreader can now visually preview how each formatted span will look: text inside `<b>...</b>` renders bold, text inside `<i>...</i>` renders italic, and so on. Markers themselves stay red (so the proofreader can see exactly which text is anchored to a tag and reorder them); nested same-name markers (`<b>x <b>y</b> z</b>`) and combinations (`<bi>`) are handled via a counter-based active-state walk. Numbered structural markers (`<t1>`, `<t2/>`, etc.) don't carry semantic styling so they leave the body-text style alone.

- **Paragraph-level styling (bold / italic / underline) now visible in the bilingual DOCX.** Segments living in a "Heading 1" / "Title" paragraph, or any source paragraph styled bold / italic / underline as a whole, previously appeared as plain text in the bilingual file even though Trados renders them styled in its editor. The export now detects paragraph-level formatting by walking the segment's `IText.Properties.Formatting` (where Trados often pushes paragraph-wide styling down to runs) plus the parent `IParagraphUnit.Properties.Contexts` (for file types that keep it at paragraph level only) and applies matching bold / italic / underline to the source AND target cells in the DOCX. Both probes are wrapped in try/catch and degrade silently if the SDK exposes the shape differently for any one file type. Re-import ignores the styling completely — Trados regenerates paragraph styling from its own metadata on export, so the styling in the bilingual file is purely cosmetic for the proofreader's visual reference. Inline formatting (cf bold/italic tags inside the segment) is unchanged — it's still serialised as `<b>` / `<i>` / `<u>` markers and reconstructed on re-import.

- **DocumentFormat.OpenXml 2.20.0 NuGet dependency** added for DOCX read/write. Pinned at the 2.x line because the 3.x multi-assembly layout doesn't fit the `.sdlplugin` packaging model. Single-DLL deployment alongside the existing iTextSharp / Microsoft.Data.Sqlite stack.

- **DOCX bilingual export header now visually matches the Supervertaler Workbench's "Bilingual Table" format.** Decorative `━ × 50` horizontal line, centered "🌐 Supervertaler Bilingual Table" title in 18pt bold blue, clickable `Supervertaler.com/trados` URL subtitle (10pt blue underlined), second decorative line, then the Project / Source file / Languages / Segments / Exported key-value block, then the amber "⚠️ Important: …" notice with italic instructions. The only visible difference between a file exported by the Trados plugin and one exported by the Workbench is the subtitle URL (`Supervertaler.com/trados` vs `Supervertaler.com/workbench`) — the title text and re-import warning are identical. Files round-trip between both products either way. Paired with Supervertaler Workbench v1.10.167 which switched its subtitle URL to `Supervertaler.com/workbench`.

### Fixed

- **AiAssistantControl: Reports tab badge + tab navigation no longer break when a new tab is inserted.** Previously `UpdateReportsBadge` and `SwitchToReportsTab` were hard-coded to tab index 2; adding the Import / Export tab shifted Reports to index 3 and would have silently broken the badge. Both methods now look the Reports tab up by label, so future tab insertions can't re-break navigation — same fix pattern applied to the Workbench in v1.10.161.
- **Batch Operations → Clipboard Mode: "Copy to Clipboard" button no longer underlaps "Paste from Clipboard".** Companion fix to the v4.20.6 Preview-prompt overlap. The Paste button's Location was pinned at construction time using the Copy button's `.Right`, but `AutoSize` widens Copy *after* layout — so Paste landed too far left, partially hidden under Copy. Added a `SizeChanged` handler on Copy that repositions Paste with the same 8 px gap any time Copy resizes (DPI scale changes, font size changes, etc.). Same `SizeChanged` pattern already used for `_btnTranslate`. Spotted by a user.

## [4.20.6] – 2026-05-24

### Fixed

- **AI Assistant → Batch Operations: "Preview prompt" link no longer overlaps the "Copy to Clipboard" button when Clipboard Mode is on.** The reposition routine was anchored on the (now-hidden) Translate button, so the wider Copy to Clipboard button slid in underneath the link. The routine now picks the rightmost *visible* control on the action row — Translate, Copy to Clipboard, Paste from Clipboard, or the Proofread "Also add issues as Trados comments" checkbox — and places the link clear of all of them. Spotted by a user.

## [4.20.5] – 2026-05-21

### Fixed

- **Overview button icon now renders.** The previous icon used an emoji (📊) outside the button font's range, so it showed as an empty box. Replaced with a glyph (☰) from the same character block as the other toolbar icons.
- **Overview and Summary buttons respond to the first click.** Like the other dock-pane buttons, they now use the Studio first-click-eaten workaround, so a single click works even when the AI Assistant pane was not the active pane (previously the first click was swallowed and you had to click twice).

## [4.20.4] – 2026-05-21

### Added

- **Memory Bank Overview (new toolbar button).** A "📊 Overview" button in the SuperMemory toolbar generates a single, self-contained HTML page and opens it in your browser, so you can see at a glance what is in the active memory bank. It includes: dashboard counts (terminology, domains, clients, style guides); a searchable, sortable terminology table (source → target, domain, client, confidence, status, updated); a list of conflicting term pairs (same source term mapped to different targets); stub/incomplete notes; terminology notes missing a domain; stale notes (not updated in over a year); domain and client coverage; and the most recently updated notes. It reads only note frontmatter, so it is instant even on large banks.
- **AI memory-bank summary (new toolbar button).** A "✨ Summary" button asks the AI for a short, plain-English profile of the active bank — overall size and focus, strongest and thinnest domains, and what needs attention (conflicts, stubs, stale notes) with specific examples. The summary is built from a compact metadata digest rather than full article bodies, so it stays cheap, and the result is posted into the chat.

### Improved

- **AI chat now also matches your question against terminology note *bodies*, not just their term names.** Building on the v4.20.3 retrieval fixes, a note whose body discusses the topic you asked about is now surfaced even when its title/term field does not match your wording. Note bodies are read on demand and cached by file modification time, so external Obsidian edits are picked up automatically. (A persisted SQLite/FTS5 index – useful for very large banks with thousands of notes – remains a documented future step.)

## [4.20.3] – 2026-05-21

### Fixed

- **AI chat now reliably surfaces a memory-bank term you ask about.** When you ask the AI Assistant about a specific term (e.g. when to use "essential"), the relevant SuperMemory terminology note is now force-included in the chat context. Previously the knowledge-base retrieval selected terminology notes only by the open document's domain, client and language – never by your actual question – so a note could be silently skipped even though it existed. Three underlying issues were fixed:
  - The user's chat message is now used to match terminology notes (by source term, target term or filename); a directly-asked-about note is given top priority so it is never dropped by the context size limit.
  - The language-pair relevance signal was reading the wrong frontmatter key (`languages`/`source_language`) and so never matched real notes, which use `language_pair`. It now reads `language_pair` as well.
  - The "include all terminology notes" fallback was gated to banks with 20 or fewer notes, so larger banks could end up contributing nothing. The size cap is removed; the token budget now does the limiting.

## [4.20.2] – 2026-05-21

### Fixed

- **Trailing sentence punctuation is stripped when adding terms to a termbase.** A term pair such as "circumference." is now stored as "circumference". Wrapping quotes and parentheses are preserved, and non-translatables keep a meaningful trailing full stop (e.g. "Inc."). Applies to source terms, target terms and synonyms across the add, quick-add and edit routes. This matches the trailing-punctuation set already used when matching terms during translation, so stored and matched forms stay consistent.

## [4.20.1] – 2026-05-21

### Added

- **Gemini 3.5 Flash is now selectable as a Gemini model.** Google's newest Flash model is offered as a premium, higher-quality option in AI Settings alongside the existing Gemini models. Gemini 3.1 Flash-Lite stays the recommended default; 3.5 Flash costs roughly six times as much per segment, so it is best reserved for difficult content.
  - Gemini 3.5 Flash always performs a "thinking" step, which at its default level would bill several hundred hidden reasoning tokens (charged at the output rate) on even a single short segment. The plugin sends `thinkingConfig.thinkingLevel: minimal` for 3.5 models (and omits the now-unsupported `temperature` parameter for them), keeping the cost of short translation jobs in line with its headline price without affecting quality on normal text.

## [4.20.0] – 2026-05-19

### Added (Trados Studio 2026 support — a second, x64 build of the plugin that runs natively in Studio 2026 and reads its new SQLite-based .ttb termbases)

Trados Studio 2026 (internally Studio 19) is a 64-bit application and ships a redesigned terminology system: the legacy MultiTerm components are gone from the install, and the built-in termbase format is now a SQLite database with the `.ttb` extension (still concept-oriented, still using the MultiTerm-style termbase-definition XML internally, but on a completely new storage engine with an FTS5 full-text index). The existing plugin targeted Studio 2024 (Studio 18), is x86/AnyCPU, and reads `.sdltb` termbases via JET/ACE OleDb — none of which works under Studio 2026. This release adds a parallel build so the same source tree produces two `.sdlplugin` artefacts: the unchanged Studio 2024 build and a new Studio 2026 build.

**One source tree, two builds.** A `TradosStudioVersion` MSBuild property (default `18`) switches the build between Studio 18 and Studio 19: HintPaths (`Studio18` vs `Studio19Beta`), platform target (x86/AnyCPU vs **x64**), output folder (`bin\Studio18` vs `bin\Studio19`), and which `pluginpackage.manifest` is shipped. The Studio 19 manifest declares `RequiredProduct minversion="19.0" maxversion="19.0.9"`. `build.sh` builds both, packages both, and deploys each to its respective Trados plugins folder. `bump_version.py` keeps both manifests in version-lock.

**TtbReader.** A new `Core/TtbReader.cs` reads `.ttb` files directly with `Microsoft.Data.Sqlite` (the same SQLite stack already bundled for the Supervertaler database). It maps the `mtConcepts` / `mtTerms` / `mtIndexes` tables to the existing `TermEntry` model, matching the public surface of `MultiTermReader` so the rest of TermLens is unchanged. Both readers now implement a shared `ITermbaseReader` interface; `TermbaseReaderFactory.Create()` dispatches on file extension (`.ttb` → SQLite, otherwise `.sdltb` → OleDb). Locale handling is case-insensitive to bridge Trados's BCP-47 codes (`en-GB`) with the uppercase codes the `.ttb` store uses (`EN-GB`). The project termbase detector now recognises `.ttb` alongside `.sdltb`.

The legacy terminology-provider plugin API (`Sdl.Terminology.TerminologyProvider.Core`) is preserved in Studio 2026 behind compatibility adapters, so no migration to the new `Sdl.TranslationResourcesApi.TB` namespace was required — the plugin registers and reads terminology exactly as before. The Studio 2026 build ships with `.ttb` support only; `.sdltb` reading is not included in the 2026 build because JET OleDb is 32-bit-only and Studio 2026 is 64-bit (legacy `.sdltb` handling in 2026 is deferred pending RWS guidance on their migration story).

### Fixed (a stack of x64-specific defects that only surfaced once the plugin ran as a 64-bit process under Studio 2026)

**SQLite provider not registered.** Opening any database failed with "You need to call SQLitePCL.raw.SetProvider()". Two compounding causes: the startup init called `Batteries_V2.Init()` with the wrong type name (the classes live in the `SQLitePCL` namespace, not `SQLitePCLRaw`, despite the package name), so it silently no-op'd; and even once corrected, `Assembly.Load("SQLitePCLRaw.batteries_v2")` returned Studio's own bundled copy, registering the provider on a different `raw` instance than the one Microsoft.Data.Sqlite ends up bound to. The fix loads the plugin-folder copies of `SQLitePCLRaw.batteries_v2` and `SQLitePCLRaw.core` by absolute path so initialisation runs against the same instance the data layer uses. Studio 2024 (x86) never hit this because a different DLL-load order happened to register a provider already.

**Arithmetic overflow crash opening TermLens.** `CtrlTapFilter.PreFilterMessage` cast `m.WParam` (an `IntPtr`, 64-bit under x64) straight to `int`, throwing `OverflowException` whenever the WPARAM had high bits set. Now reads via `ToInt64()` and masks to the low byte before narrowing.

**Removed-API build break.** The two-argument `ITerminologyProviderFactory.CreateTerminologyProvider(Uri, ITerminologyProviderCredentialStore)` overload (obsolete in 2024, removed in 2026) is replaced with the single-argument form that exists in both.

**Two-minute editor freeze on first interaction.** Termbase loading (the Supervertaler database plus project termbases) ran synchronously during view-part initialisation, blocking editor activation until it finished — on x64 with a cold disk that was up to two minutes. The load now runs on a background thread; the editor is responsive immediately and term chips populate a few seconds later.

**First click on every plugin-pane button was ignored.** Studio 2026's WPF-based docking host consumes the first `WM_LBUTTONDOWN` while activating a previously-inactive dock pane, so neither `MouseDown` nor `Click` fired — every header/action button across TermLens, SuperSearch, and the AI Assistant required two clicks. A new `Core/ClickThrough` helper pre-emptively activates the pane on `MouseEnter` via a native `SetFocus` call (deliberately not WinForms `Control.Focus`, which would trigger `ScrollControlIntoView` and disturb the panel layout), with a `GotFocus`-based fallback that synthesises the action when the user is still holding the mouse button down on the control. Applied to all pane buttons.

**TermLens corner indicators clipped.** The amber metadata dot and indigo synonym icon at a chip's top-right corner were clipped on the right edge (reading visually as "cut off at the top") because the chip reserved too little right-side padding for the icon and its border. The chip now reserves the indicator's overflow width when an indicator is present. This was a pre-existing defect, fixed in both the Studio 2024 and Studio 2026 builds.

### Changed (AI prompt termbase inclusion is now opt-in by default)

The "Termbases included in AI prompts" list now defaults to **nothing included** — users explicitly enable the termbases they want fed to the AI, rather than everything being included unless explicitly excluded. A one-shot migration (mirroring the existing per-project behaviour) applies the opt-in default on first load: when the global AI termbase list has never been initialised and no explicit choices exist, all termbases are set to excluded. Users with existing explicit selections are left untouched. This is a privacy-first default — sensitive termbase contents are no longer sent to the AI provider unless the user opts in.

### Packaging

`package_plugin.py` now ships the `pluginpackage.manifest.xml` produced by the build (which carries the correct `RequiredProduct` range per Studio version) instead of regenerating it from a hardcoded template that always declared the Studio 2024 range — the cause of the Studio 2026 build initially being silently skipped by Studio's plugin loader.


## [4.19.114] – 2026-05-17

### Fixed (v4.19.113 ↻ refresh button was clipped against the floating gear icon overlay; bumped TermLens header right-padding to give it breathing room)

A user reported that the new ↻ refresh button added in v4.19.113 was being visually clipped against the ⚙ settings gear icon to its right. Investigation: there are two layered controls sharing the same horizontal strip at the top of the TermLens panel:

 - **`MainPanelControl`** (outer wrapper) floats the ⚙ gear (26 px wide) and ? help (26 px wide) buttons **absolutely** at the top-right via `PositionTopButtons`, sitting at `X = Width - 54` and `X = Width - 28` respectively. These are drawn on top of the inner panel.
 - **`TermLensControl._headerPanel`** (inner) hosts the `A` / `A` font buttons, the status label, and (since v4.19.113) the new ↻ refresh button — all `Dock = DockStyle.Right`. The inner panel reserved `Padding.Right = 56 px` to leave space for the gear+help overlay.

The old 56-px reservation just covered the 54-px combined gear+help footprint, with 2 px to spare. v4.19.113's new 24-px refresh button docked at the right edge of the inner content area, putting its right edge at `Width − 56` — only 2 px clear of the gear icon's left edge at `Width − 54`. At 100% DPI this read as "the buttons are touching"; at 150% Windows scaling the gap closed to 4 raw pixels, indistinguishable from clipping.

**Fix.** Bumped `_headerPanel.Padding.Right` from `UiScale.Pixels(56)` to `UiScale.Pixels(84)` — `26(help) + 26(gear) + 24(refresh) + 8(slack)` = 84 px reserved. The refresh button's right edge now sits at `Width − 84`, with a comfortable 30-px gap to the gear icon's left edge at `Width − 54`. At 150% DPI the gap scales to ~46 raw pixels, well clear of any visual collision.

No other layout was affected — the font buttons and status label are docked inside the same right-padded area and shift left in lockstep with the padding bump.


## [4.19.113] – 2026-05-17

### Added (↻ refresh button in TermLens header + automatic refresh when the shared SQLite database is modified by another process — typically the Supervertaler Workbench desktop app)

Companion to the Workbench v1.10.68 / v1.10.69 refresh feature. When both products are open against the same `supervertaler.db` and the user edits terms in Workbench, the Trados plugin's in-memory term index goes stale until the user manually presses F5 (`RefreshTermbaseAction`). Symptoms identical to the Workbench-side bug it mirrors: TermLens keeps showing terms that have been deleted in the other tool, misses newly-added ones, and right-clicking a stale term to edit pops up an empty dialog because the underlying `term_id` no longer exists.

**Two pieces ship together:**

**1. Visible ↻ button in the TermLens header.** Placed left of the existing `A` / `A` font zoomer, styled to match the SuperMemoryToolbar refresh button (24×24 px, light-gray glyph, hover effect, click-feedback). Clicking it raises a new `TermLensControl.RefreshRequested` event; the host (`TermLensEditorViewPart.OnTermLensRefreshRequested`) routes it through `NotifyTermAdded()` — the same code path F5 has always used, so we get the full reload (settings re-read, `LoadTermbase(forceReload: true)`, MultiTerm re-detect, segment redraw) without forking the logic. Brief visual cue: button flips to `✓` and disables for 500 ms so the click registers even when the reload is sub-second. Tooltip explains the use case explicitly and mentions the F5 equivalent. `TermLensControl.CurrentDbPath` exposed so the watcher (below) knows what file to monitor.

**2. Automatic refresh via `FileSystemWatcher`.** Installed on the active `supervertaler.db` at the end of `Initialize()`, right after the existing `LoadTermbase` / `LoadMultiTermTermbases` chain. Three layers of "don't misfire" gating, matching the Workbench v1.10.69 implementation:

 - **2-second debounce** — `_dbDebounceTimer` (re)starts on every `Changed` / `Created` / `Renamed` event; the actual reload only runs once the file has been quiet for 2 s, so a burst of writes (typical: INSERT + activation-table update + SELECT-verify) collapses into one reload.
 - **Snapshot gating** — before reloading, the handler queries five cheap aggregates (`COUNT` + `MAX(id)` from `termbases`; `COUNT` + `MAX(id)` + `MAX(modified_date)` from `termbase_terms`) and compares to the snapshot taken at the end of the last own-reload. If nothing in the termbase tables changed (e.g. it was a TM write that happened to touch the same `.db` file), the reload is skipped entirely.
 - **Own-write integration** — every reload path (`NotifyTermAdded` / `NotifyTermInserted` / `NotifyTermDeleted`) refreshes the snapshot at the end via `RefreshTermbaseDbSnapshot`. So an own-write updates the snapshot synchronously; when the watcher debounce fires ~2 s later for that same write, the comparison sees no change and the reload is correctly skipped. **Zero spurious rebuilds from own-writes.**

The watcher is wrapped in `try / catch` at setup and teardown sites — failure (network drive, exotic filesystem, missing `FileSystemWatcher` permissions) is non-fatal: F5 and the new ↻ button still work as manual fallbacks. Teardown happens cleanly on `Dispose` so we don't leak watchers across document close / reopen cycles. Also re-installed by `NotifyTermAdded` in case Settings just pointed at a different `supervertaler.db`.

End-user effect: edit terms in Workbench → switch to Trados → display already reflects the change (within ~2 s of the Workbench write completing), no F5 / ↻ click needed. The button still lives in the header for the explicit "do it now" trigger and for cases where the watcher couldn't be installed.

Diagnostic output goes to `System.Diagnostics.Debug.WriteLine` (visible in DebugView): `[TermLens] DB auto-refresh: watching supervertaler.db (2s debounce)` on setup, `[TermLens] DB changed externally — auto-refreshing index (termbases:X→Y, terms:N→M)` on each actual refresh, snapshot failures and watcher-setup failures also logged.


## [4.19.112] – 2026-05-17

### Changed (All in-app help links repointed at the new help.supervertaler.com site)

- The Supervertaler help system has migrated off GitBook to a self-hosted Astro/Starlight site at `https://help.supervertaler.com`, deployed via Cloudflare Pages from the public `Supervertaler/Supervertaler-Help` repo. The new site has per-product sidebar filtering (a Trados page shows only the Trados tree; a Workbench page shows only the Workbench tree), a custom search component that groups results by product, and proper folder-based URLs (`/trados/...` instead of GitBook's flat-with-collision-suffix slugs). The migration was driven by GitBook free-plan limitations (no support for two distinct product spaces) and by the recurring pain of search results mixing Workbench and Trados topics.
- Every help-URL constant in `Core/HelpSystem.cs` has been rewritten from the old GitBook slug shape to the new Astro folder-based URLs. The mapping is 1-to-1 against `src/generated/sidebar.js` in the `Supervertaler-Help` repo (which is the authoritative URL map). All 37 constants now point at `https://help.supervertaler.com/trados/...`, with trailing slashes (Astro's canonical form, so the browser hits the page directly instead of via a 301 redirect). The `bidirectional` parameter wasn't needed here; the routing just goes to a different host.
- Two further hardcoded help URLs were also repointed: the `HelpUrl` constant in `Controls/UsageStatisticsDialog.cs` (Help button on the first-run stats dialog) and a comment-only reference in `Controls/TermLensPopupForm.cs`. Verified end-to-end: every help action from inside Trados Studio – Help menu, F1 in panels, "Help" buttons in dialogs – now lands on a 200-OK page on the new domain.
- The old `supervertaler.gitbook.io/help/...` URLs continue to work during a two-week handover window so that any users on older installed versions still get a working help system. After that window, GitBook will be replaced with a single "we've moved" notice and unpublished. The redirect window matters specifically because Trados plugin installs propagate slowly via the RWS App Store update mechanism.
- Implementation note: this commit just bumps the version and ships the URL constants that landed in `717f9a5` (Re-point all help URLs at help.supervertaler.com). No behavioural changes beyond the URL repointing; no UI changes; no new features.


## [4.19.111] – 2026-05-16

### Changed (AutoPrompt-generated prompts now embed the Translator's Comment methodology by default)

- Real translation work routinely runs into mechanical defects in the source: typos, broken words across whitespace, hanging mid-sentence breaks, doubled spaces, stray punctuation, reference-numeral mismatches that are unambiguous in context, missing diacritics. The cleanest workflow for handling these – established in a gold-standard project prompt and validated against real Trados Studio translation work – is for the translator AI to silently correct obvious defects and append a single concise translator's comment at the end of the segment in the form `⟦TC: short description of the fix⟧` (using the mathematical white square brackets U+27E6 / U+27E7, which do not occur in source documents and so are safe as out-of-band markers extractable in post-processing).
- The Trados AutoPrompt meta-prompt (`PromptGenerator.BuildMetaPrompt`) now contains an explicit TRANSLATOR-COMMENT METHODOLOGY block that requires the LLM to embed this silent-correction-with-flagged-comment methodology in every prompt it generates, regardless of source language or domain. The block specifies the exact bracket characters, the rules for one-marker-per-segment, the no-empty-markers rule, the inline `[bracketed text]` convention for translator-supplied words, the comment-body style guidance (5–20 words, noun-phrase, no first-person), the placement (final content of the segment, single space separator), and a list of hard exclusions (numerical values, dates, dosages, claim language, statutory references, headings, identifiers, ambiguous cases).
- The generated prompt is also required to include: (a) the silent-correction methodology in its TRANSLATION MANDATE section, with defect categories adapted to the actual source language (e.g. Dutch -d/-t typos, German missing umlauts, French accent slips, Spanish/Italian conjugation typos), (b) a dedicated TRANSLATOR COMMENT FORMAT section near the end with the exact spec and 4–6 example comment bodies adapted to the language and domain, (c) a check in PREFLIGHT SELF-CHECK and POST-TRANSLATION INTEGRITY that every silent correction has its corresponding marker and no segment without corrections has a marker, (d) a note in OUTPUT FORMAT that ⟦ and ⟧ are the deliberate out-of-band comment delimiter and the sole exception to the "ASCII output only" rule.
- The methodology is always-on for every AutoPrompt-generated prompt in every domain – users who prefer not to use it can edit the generated prompt to remove the TC sections. Per-project opt-out via a UI toggle is a possible future enhancement.
- The markers currently appear inline in the target text. Extraction into Trados Studio segment comments is not yet wired up; this is a separate follow-up. The current change ships the methodology in the prompts so the translator AI produces the markers reliably, ready for whichever extraction pipeline gets built next. The Trados Studio comment panel already exists, so the extraction step is small once the spec is locked.
- Shipped in parallel with Supervertaler Workbench v1.10.46 which makes the same change to its Python meta-prompt so both products' AutoPrompts agree on the methodology and the bracket format.


## [4.19.110] – 2026-05-16

### Changed (AutoPrompt now generates proper Markdown instead of plain text)

- AutoPrompt-generated translation prompts were saved as `.md` files but their content was plain text dressed up as a numbered list (`1. ROLE`, `2. TECHNICAL DOMAIN`, etc., with no Markdown headings, no bullet markers, no bold, no tables). The shared `prompt_library/` folder also held Workbench-generated prompts that *did* use proper Markdown (`## H2` headings, `-` bullets, `**bold**`, `| ... |` tables), so the two products were producing inconsistent artefacts in the same folder despite both using the same `.md` extension. Opening a Trados-generated prompt in Obsidian, VS Code, GitHub, or any Markdown-aware viewer rendered as a wall of text instead of a navigable document.
- `PromptGenerator.BuildMetaPrompt` now contains an explicit Markdown-formatting block telling the LLM to: open with a `# H1` title and one or two `## H2` subtitles; render every major numbered section as a `## H2` heading; use `### H3` for subsections (e.g. `### Absolute requirements`, `### Absolute prohibitions`); use `-` bullet lists for enumerable content; use `**bold**` for emphasised and locked terms; render the PROJECT-SPECIFIC GLOSSARY as a proper Markdown table with header row + alignment row; use `---` horizontal rules between major sections; use fenced code blocks only for actual code/file-path examples.
- The instruction explicitly notes that this Markdown requirement applies only to the **generated prompt itself**, not to the inner "OUTPUT FORMAT" rule that the generated prompt imposes on the translator AI (which still says "translation only, no markdown formatting in the translation output"). The two are different concerns – the prompt is a document for humans + LLMs to read; the translator's per-segment output remains plain target-text only.
- Effect: next AutoPrompt-generated prompt opens cleanly in any Markdown viewer, is far easier to scan and edit (find Section 13 instantly via outline), and matches the format Supervertaler Workbench already produces. Pre-existing plain-text prompts continue to work unchanged – they're still valid system prompts for the translator AI.


## [4.19.109] – 2026-05-16

### Changed (Prompt manager: filename is now the authoritative display name)

- The prompt manager tree displayed the YAML `name:` field from each prompt file's frontmatter, not the filename on disk. Renaming a .md file in Explorer (without editing the YAML inside) left the tree showing the old name even after clicking Refresh, because the tree built itself from `prompt.Name` (which the loader read from YAML and only fell back to filename when YAML had no `name:`). Two sources of truth for what to call a prompt is a confusing UX trap.
- The on-disk filename is now the single source of truth. `PromptLibrary.ParsePromptFile` unconditionally sets `prompt.Name = Path.GetFileNameWithoutExtension(filePath)`, ignoring any YAML `name:` field that may exist for backward compatibility with older files (`ParseYamlFrontmatter`'s `case "name"` is now a no-op). `PromptLibrary.SavePrompt` no longer writes `name:` to the YAML frontmatter at all.
- Effect for users: rename `MyPrompt.md` to `Better Name.md` in Explorer, click Refresh, and the tree shows the new name immediately. No need to also edit the YAML inside the file.
- Backward compatibility: existing prompt files with a YAML `name:` field still load fine – the field is silently ignored on read, and is dropped from the file the next time the prompt is saved through the UI. No mass migration runs. Existing in-app rename flows still work because `SavePrompt`'s "rename file when `prompt.Name` differs from the on-disk filename" logic is unchanged – `prompt.Name` is now set from the user's new name in the Edit dialog, and the file is renamed to match.
- Shipped in parallel with Supervertaler Workbench v1.10.43 which makes the same change to its Python `UnifiedPromptLibrary` so both products stay in sync on the shared `prompt_library/` folder.


## [4.19.108] – 2026-05-15

### Changed (Usage-statistics dialog rewritten: informational, default-on)

- The first-run anonymous-usage-statistics dialog has been rewritten from "Would you like to opt in?" to a polite informational note: *"Supervertaler for Trados sends one anonymous ping at startup so I can see how many people use the plugin. No personal data, no translation content, no termbase info — just plugin version, OS, Trados version, and system locale. If you'd rather not, switch it off below or any time in Settings. — Michael"*. The default action (Enter, Esc, X-close, or the bold "Keep it on" button) keeps stats enabled; only an explicit "Turn it off" click switches them off. The underlying telemetry behaviour is unchanged – same single anonymous ping at startup with the same payload (plugin version, OS, Trados version, locale, virtualization host).
- A new `usageStatisticsAskedV2` flag controls whether the dialog has been shown. The legacy `usageStatisticsAsked` flag stays in settings for backwards compatibility but is no longer checked, so every existing user – including those who clicked No to the previous opt-in dialog – gets the rewritten dialog once after updating, and their previous answer is treated as "you weren't asked under this framing yet." Users who already had stats on stay on (just see the new dialog confirming); users who said No previously get a fresh ask under the new framing (default-on, click "Turn it off" to keep stats off).
- *Background:* the previous opt-in dialog produced near-zero opt-ins – the live stats dashboard showed essentially one active user (me, on dev installs) across the whole plugin user base – because asking users to actively share data they don't benefit from gets near-universal "no thanks." The new framing is honest about what is collected (still: nothing personal, just version + OS + locale once per session) but defaults to enabled so the data actually reflects real plugin usage instead of the opt-in cohort.


## [4.19.107] – 2026-05-14

### Changed (Grok list trimmed to one current model)

- **Grok list is now just Grok 4.3** (`grok-4.3`), xAI's current flagship. The previous entries – Grok 4.20, Grok 4.1 Fast, and Grok 4.20 (Reasoning) – were stale, and some of those slugs are in xAI's 15 May 2026 API retirement. Grok 4.3 is a single unified model (xAI dropped the separate reasoning / non-reasoning variants), priced $1.25 / $2.50 per 1M tokens. Grok stays a supported provider; the list is just current and minimal now. Anyone on an old Grok model keeps working via the Model ID field, and xAI redirects retired slugs to `grok-4.3` anyway.


## [4.19.106] – 2026-05-14

### Changed (Claude list trimmed; Opus pricing corrected)

- **Removed Claude Opus 4.6 from the curated list.** Opus 4.7 is a step-change improvement over 4.6 at the same price, so keeping the previous flagship around was pure redundancy – same reasoning as the recent Gemma and Mistral Nemo trims. The Claude list is now a clean three: **Sonnet 4.6** (recommended), **Haiku 4.5** (fast/cheap), **Opus 4.7** (highest quality). Removed from both the Claude and OpenRouter lists; anyone still on Opus 4.6 keeps working via the Model ID field.
- **Fixed stale Claude Opus pricing in the cost estimator.** `TokenEstimator` had Opus at $15 / $75 per 1M tokens; the correct rate is **$5 / $25** (as Anthropic's docs and this changelog's own v4.19-era Opus 4.7 entry both state), so Opus cost estimates were running ~3× too high. Sonnet ($3/$15) and Haiku ($1/$5) were already correct.


## [4.19.105] – 2026-05-14

### Changed (Mistral list trimmed to two models)

- **Removed Mistral Nemo from the curated list.** Mistral Nemo (`open-mistral-nemo`) is a July 2024 model, since superseded; for translators it offered no real benefit over Mistral Small – similar (negligible) cost, lower quality, and the "open weights" angle is moot when it's called through Mistral's hosted API anyway (the Ollama provider covers genuinely local models). The Mistral list is now a clean two-tier split: **Mistral Large** (flagship – `mistral-large-latest`) and **Mistral Small** (fast, cost-effective workhorse – `mistral-small-latest`), both of which auto-track to the current release via their `-latest` aliases. Anyone still on `open-mistral-nemo` keeps working by entering it in the Model ID field.


## [4.19.104] – 2026-05-14

### Changed (Gemini model lineup refresh)

- **Gemini 2.5 Flash → Gemini 3.1 Flash-Lite** (`gemini-3.1-flash-lite`) as the recommended Gemini model – cheaper ($0.25 / $1.50 per 1M tokens), faster, and positioned by Google for high-volume work like translation. `gemini-2.5-flash` is removed from the curated list; anyone still on it keeps working (it now shows in the Model ID field).
- **Removed Gemma 4 31B from the Gemini list.** It sat within ~1% of Gemma 4 26B MoE on quality while the 26B MoE is 2–2.5× faster, so keeping both was redundant. Gemma 4 26B MoE stays as the open-source option.


## [4.19.103] – 2026-05-14

### Added (AI Settings: custom model ID field – [#24](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/24))

- **New "Custom model ID" field in AI Settings**, below the Model dropdown, available for every cloud provider. Enter an exact model ID to use a model that isn't in the curated dropdown – a new release, a preview model, or an OpenRouter router such as `openrouter/free`. When filled it overrides the dropdown selection; leave it blank to use the model picked from the dropdown.
- Previously only OpenRouter allowed a custom model, and only by typing over the dropdown's display text (a "Name – Description" string) – confusing, since you had to overwrite a description with a bare ID, with no cue that the field was even editable. The OpenRouter dropdown is now a normal locked list like every other provider, and the dedicated Custom model ID field gives all providers one clear, consistent way to enter a model that isn't listed. A model ID saved this way that isn't in the curated list is shown back in the Custom model ID field when Settings reopens (so existing OpenRouter custom-model users migrate seamlessly).

### Changed (OpenAI model lineup: GPT-5.4 → GPT-5.5)

- **The curated OpenAI model is now GPT-5.5** (`gpt-5.5`), OpenAI's new frontier model (released April 2026), replacing GPT-5.4 in both the OpenAI and OpenRouter dropdowns. It's more capable and, at $5 / $30 per 1M tokens, cheaper than GPT-5.4 was.
- **GPT-5.4 Mini stays** as the recommended everyday default — there is no "GPT-5.5 mini", and at ~$0.75 / $4.50 per 1M tokens GPT-5.4 Mini is roughly 7× cheaper than GPT-5.5, which is the right choice for high-volume batch translation. The split is deliberate: GPT-5.4 Mini for everyday/batch work, GPT-5.5 for AutoPrompt and complex tasks.
- Users who had GPT-5.4 explicitly selected are not migrated — `gpt-5.4` still works on the API and now simply shows in the Custom model field; its cost-estimate pricing is kept under the legacy section.

### Fixed (AI Settings: "Connected" status text clipped)

- After clicking **Test Connection**, the green "✓ Connected" status was drawn starting underneath the Test Connection button – the label sat at x=250 while the 160px-wide button reaches x=280, and the button is in front in z-order, so the first few characters were hidden. The status label now starts past the button's right edge.


## [4.19.102] – 2026-05-14

### Added (SuperSearch: search project translation memories — three search modes)

- SuperSearch can now search the project's translation memories, not just its SDLXLIFF files. A new mode dropdown in the search bar offers three modes:
  - **Project files** – the original behaviour: search the project's SDLXLIFF files.
  - **Files + TMs** – search the project files *and* the project's translation memories, in one merged result list.
  - **TMs only** – concordance mode: search only the project's translation memories, the way Studio's built-in Concordance does.
- The selected mode is persisted across sessions until the user changes it (`superSearchMode` setting).
- **New "TMs" button** next to "Files" — opens a selection dialog listing the project's translation memories with checkboxes, so a search can be narrowed to specific TMs. Mirrors the existing Files filter; session-scoped, resets when the project changes.
- TM discovery reads the project's `.sdlproj` for attached translation-provider TMs and also scans the project's `Tm` subfolder. File-based `.sdltm` only — server-based (GroupShare) TMs aren't searched in this version. The project's files and TMs are re-discovered on every search, so a file or TM added to the project mid-session is picked up without reopening it.
- TM hits go through the Trados TM API's concordance search (source-side and/or target-side, following the Scope dropdown) and are then post-filtered with the same Aa / .* / Word options as file search, so the search options behave consistently across files and TMs.
- TM results appear in the same grid: the **File** column is renamed **File/TM** and shows the TM name in blue for TM rows; the **#** column shows the concordance match score. TM results are not navigable — double-clicking a TM row shows a hint pointing to the preview pane (where the text can be selected and copied) — and they are never touched by Replace / Replace All, which still operate only on SDLXLIFF file results. In **TMs only** mode the Replace bar is disabled.
- All project discovery (enumerating SDLXLIFF files, parsing the `.sdlproj`, probing TM paths) runs on a background thread, so the Trados UI thread never blocks on it — during start-up or during a search.
- New file `Core/TmSearcher.cs`; new TM API assembly references; the search-source mode is the `SuperSearchSourceMode` enum.


## [4.19.101] – 2026-05-14

### Added (SuperSearch: Match whole word)

- New **Word** checkbox in the SuperSearch search bar, alongside **Aa** (case sensitive) and **.\*** (regex). When ticked, the query only matches complete words – searching for "cat" no longer matches "category" or "scatter". Applies to both search and Replace / Replace All, so a whole-word search followed by Replace All won't quietly replace partial matches. Ignored when **.\*** (regex) is on – use `\b` in the pattern instead.

### Improved (SuperSearch: tooltips on every control)

- Every control in the SuperSearch search and replace bars now has a tooltip. Previously only **Aa**, **.\*** and **Files** had them; the search box, the Search / Stop buttons, the scope dropdown, the Replace toggle, the **?** button, and the whole replace bar (replacement box, Replace, Replace All) were undocumented.


## [4.19.100] – 2026-05-14

### Added (SuperSearch can be docked as a tab in the Supervertaler Assistant panel)

- **New setting: Settings → General → Panels → "Show SuperSearch as a tab in the Supervertaler Assistant panel".** When enabled, SuperSearch is hosted as a 4th tab inside the Supervertaler Assistant panel (alongside Chat, Batch Operations, and Reports) instead of occupying its own dockable panel – handy for translators who want fewer floating panels. Off by default; the standalone panel remains the default experience.
- Implementation: all of SuperSearch's search / replace / navigate logic was extracted out of `SuperSearchViewPart` into a new host-agnostic `SuperSearchController` (a process-wide singleton), so the same control instance can be hosted by either the standalone ViewPart or the Assistant tab. Both hosts share one control, so search results survive switching between tabs.
- **Requires a Trados restart** to switch hosting modes – a WinForms control can only have one parent, so the host is decided at panel-creation time. The settings screen labels this "(restart required)".
- **Gated on a Supervertaler Assistant licence.** The Assistant panel's upgrade overlay covers the whole panel when unlicensed, which would hide a SuperSearch tab too – so without a licence the toggle is ignored and SuperSearch stays in its own, fully functional panel. This protects SuperSearch's availability rather than restricting it: trial users always have SuperSearch in its original location.
- **Alt+S** and the editor right-click **SuperSearch** action are now mode-aware: they activate whichever host is in use and, in tab mode, switch the Assistant panel to the SuperSearch tab. When SuperSearch is hosted in the tab, opening the standalone panel shows a short placeholder pointing to the new location (Trados always registers the standalone ViewPart from `plugin.xml`, so it can't be hidden outright).

### Fixed (SuperSearch preview pane: text wasn't selectable or copyable – [Supervertaler-Workbench#202](https://github.com/Supervertaler/Supervertaler-Workbench/issues/202))

- A user reported (via the Workbench issue tracker) that text in SuperSearch's results couldn't be selected or copied, so there was no way to copy a previous translation out of SuperSearch to reuse it. For patent and technical translators, SuperSearch is most valuable as a cross-file concordance – "how did I translate this phrase before?" – and the natural next step, copying the prior target verbatim, wasn't possible.
- The fix targets the **preview pane** (the full source/target boxes below the results grid), not the results grid itself: the grid is a `DataGridView` with full-row selection, where individual cell text genuinely can't be selected, and the preview pane shows the complete untruncated text anyway. The preview boxes are read-only `RichTextBox` controls, which already supported mouse selection and Ctrl+C – but had no right-click menu and didn't respond to Ctrl+A.
- Both preview boxes now have a right-click context menu with **Copy** (the current selection, or the whole box if nothing is selected), **Select All**, **Copy source**, and **Copy target**, plus **Ctrl+A** select-all support.


## [4.19.99] – 2026-05-13

### Changed (Workbench Chat: AI Settings dropdown + QuickLauncher menu)

- Workbench's floating Sidekick window was retired in Workbench v1.10.4 and its Chat surface was promoted into Workbench itself. The Trados plugin now reflects that in three user-facing places:
  - **AI Settings → "QuickLauncher prompts go to:" dropdown** – the second option, formerly labelled *Workbench Sidekick*, is now *Workbench Chat*. The dropdown tooltip is rewritten to describe the current destination (Workbench's AI tab → Chat sub-tab) instead of the retired Sidekick window.
  - **Editor right-click → QuickLauncher submenu** – the *&Send to Supervertaler Sidekick* item (only shown when the global setting routes to Workbench) is now *&Send to Supervertaler Workbench Chat*. Tooltip updated to match.
  - **Trados edit-history actionName** – when the Workbench bridge inserts a translation into the active document, the edit-history label that Trados records (visible in Studio's undo display) was *Supervertaler Sidekick*; now *Supervertaler Workbench*.
- The on-disk persisted setting value (`settings.QuickLauncherTarget == "WorkbenchSidekick"`) is **deliberately unchanged**. It's a stable internal identifier – existing users' saved preferences continue to resolve without migration. Same rationale as the Python-side `sidekick_bridge_server.py` module name, the `SidekickBridge` C# class, the `WorkbenchSidekickClient` C# class, and the on-disk `sidekick-bridge.json` handshake filename: all wire-protocol surfaces that pre-date the v1.10.4 retirement and that we keep stable so deployed plugin versions and Workbench installs keep talking to each other.
- Pairs with Workbench v1.10.24, which retargets the bridge to land on the AI top tab's Chat sub-tab (instead of the right-panel Chat next to the editor) when a QuickLauncher prompt arrives. The user explicitly asked for QuickLauncher prompts from Trados to land on the *full-width* Chat surface – they're general-purpose AI conversations, not segment-level translation work, so they deserve more screen real estate to read the response.


## [4.19.98] – 2026-05-12

### Fixed (Proof-read report segments scrambled after ticking an issue's checkbox – [#28](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/28))

- A user reported that after running a proof-read and ticking a checkbox in the report to mark an issue as addressed, the remaining issue cards reordered non-sequentially (e.g. 844, 633, 623, 493, 494, 495). Until the first checkbox click the segments were in order; the click-then-relayout cycle scrambled them.
- Root cause: `List<T>.Sort` in .NET is not stable. The comparer in `OnResultsPanelResize` (`ReportsControl.cs`) sorted prompt-log cards by timestamp (newest first) and returned `0` for any pair of issue cards, expecting them to keep their original insertion order. An unstable sort is free to reorder equal-keyed elements – and did, the moment a card was removed.
- Fix: explicit `SegmentNumber` tie-break for two issue cards. Equal-class issue cards now have a deterministic order regardless of how they sit in `_resultsPanel.Controls`, so any future removal path that triggers a relayout keeps the report sequential.


## [4.19.97] – 2026-05-11

### Added (Supervertaler blue icon on every plugin dialog title bar)

- Every plugin dialog (Settings, Prompt Editor, Termbase Editor, Term Picker, About, Setup, all the Add/Edit/Bulk/Merge/Save dialogs, Usage Statistics, Supported Models, Distill Choice, Legacy Memory Bank Migration, SuperMemory Quick Add) now shows the blue Supervertaler icon in its title bar (and in the Alt+Tab switcher and Windows taskbar) instead of the generic WinForms placeholder icon.
- Bundled the existing `sv-icon-512.png` as a multi-resolution `sv-icon.ico` (16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px) embedded resource, loaded once by a new `Core/IconHelper.AppIcon` and assigned to every Form constructor. The 16-px frame is hand-tuned in the generated .ico so the title bar render stays crisp at 100% DPI; Windows picks the larger frames automatically for high-DPI displays and the taskbar.


## [4.19.96] – 2026-05-11

### Fixed (Prompt Editor: top fields don't align with Prompt content's text area)

- Name / Description / Default-mode combo all anchored their right edges 12 px from the dialog edge, but the Prompt content textbox below has a vertical scrollbar inside it, so its actual **text area** ends ~17 px earlier than its border. The top row looked misaligned with the content area below. Added a 17-px scrollbar-gutter offset to the right margin of Name, Description and the Default combo (Width / x adjusted by the same amount), so now all four right edges align with the Prompt content's text area, not its scrollbar-inclusive border.


## [4.19.95] – 2026-05-11

### Fixed (Prompt Editor: "Default:" combo not aligned with Description / Prompt content right edges)

- The **Description** and **Prompt content** textboxes anchor to the dialog's right edge (so they stretch with the form), but the **Default:** label and combo on the Mode row sat at fixed x coordinates with no right anchor. On a resized or DPI-scaled dialog the combo's right edge drifted leftward relative to Description, making the layout look unbalanced. Anchored both controls to the right edge, with the combo's right margin set to the same 12 px used by the other textboxes — now all four right edges line up regardless of dialog size.


## [4.19.94] – 2026-05-11

### Fixed (Prompt Editor: "Default:" label butting up against the combo box)

- v4.19.93 fixed the checkbox-to-label gap but left the label-to-combo gap too tight — "Default:" was visually merging into the "Assistant" combo dropdown to its right. Pulled `_lblDefaultMode` back to x=420 and pushed `_cboDefaultMode` to x=495 / width=95, leaving ~20 px on both sides of the label.


## [4.19.93] – 2026-05-11

### Fixed (Prompt Editor: "Default:" label still touching "Copy to clipboard" after v4.19.92)

- v4.19.92 pushed `_lblDefaultMode` from x=385 to x=415 but the autoscaled "Copy to clipboard" checkbox is wider in practice than the metric suggested, so the label was still butting up against the checkbox with no visible gap. Pushed `_lblDefaultMode` from x=415 to x=435 and `_cboDefaultMode` from x=470 / width=105 to x=490 / width=100. Combo ends at x=590, still 10 px from the 600 px dialog edge, with no functional change.


## [4.19.92] – 2026-05-11

### Fixed (Prompt Editor: "Default:" label clipped by the "Copy to clipboard" checkbox)

- The Mode row added in v4.19.91 laid out the "Copy to clipboard" checkbox and the "Default:" label slightly too close together — at Segoe UI 9 pt the checkbox's autoscaled text ran into the label, clipping the "D" so the user only saw "efault:". Pushed `_lblDefaultMode` from x=385 to x=415 and `_cboDefaultMode` from x=440 / width=130 to x=470 / width=105. Combo still fits comfortably within the 600 px dialog, the checkbox now has ~25 px breathing room on its right edge, and there's no functional change.


## [4.19.91] – 2026-05-11

### Added (QuickLauncher prompts can now copy to clipboard instead of sending to the AI Assistant)

- A QuickLauncher prompt can now be configured with **two destinations**: the in-Trados AI Assistant (or Workbench Sidekick, per the user's existing global setting) **and** the system clipboard. When both are enabled, the prompt's entry in the **Ctrl+Q QuickLauncher menu** shows a cascading submenu (▶) with two options — *"Send to Supervertaler Assistant"* (`S` accelerator) and *"Copy prompt to clipboard"* (`C` accelerator) — letting the user pick the destination at runtime without leaving the keyboard.
- Use case the feature was built for: a translator who has a claude.ai project set up for the current job and wants to send a per-segment question (using the same variable-substituted prompt that already works inside Trados) into that browser-based project chat instead of the in-Trados chat. The clipboard option does the variable expansion against the active segment exactly as before, copies the result to the system clipboard, and stays out of the way — paste into the external chat (claude.ai, ChatGPT, Gemini, anywhere) and send manually.
- **Default mode** controls which submenu item gets first-Enter activation. The default mode is rendered first in the submenu so the natural keyboard flow (Ctrl+Q → type-to-search → Enter → Enter) fires it without explicit selection.
- Single-mode prompts (the implicit default for every existing prompt — just *"Send to Assistant"*) keep their previous flat menu behaviour: one click, no submenu, no behavioural change. **All current prompts in users' libraries continue to work exactly as before** — the new fields are opt-in via the prompt editor.

### Added (Prompt Editor: Mode row)

- The **Prompt Editor** dialog (Settings → Prompts → Edit / New) now shows a new **Mode** row for prompts whose category starts with `QuickLauncher`. Two checkboxes — `Send to Assistant` (ticked by default) and `Copy to clipboard` — plus a `Default:` dropdown that greys out unless both modes are ticked.
- Built-in (default) prompts can have their mode toggled even though their content stays read-only, because mode selection is a routing preference rather than a content edit — same logic that already let users hide a default prompt from the menu without cloning it first.

### Implementation notes

- New `PromptTemplate.QuickLauncherModes` (`List<string>`, defaults to `["assistant"]`) and `PromptTemplate.DefaultMode` (`string`, defaults to `"assistant"`).
- YAML frontmatter accepts both inline-list syntax (`quicklauncher_modes: [assistant, clipboard]`) and a comma-separated string (`quicklauncher_modes: assistant, clipboard`). Multi-line YAML lists with `- assistant` indented bullets are NOT supported by the existing line-based parser, deliberately — keeps the parser implementation untouched.
- The writer only emits `quicklauncher_modes:` / `default_mode:` when they differ from the implicit defaults, so existing single-mode prompts round-trip without any YAML churn.
- Whitelist of accepted mode values (`assistant`, `clipboard`) — unknown values are silently dropped on parse to keep typos from making prompts unreachable.
- `QuickLauncherAction.Run()` refactored: the per-prompt click handler is split into a dispatcher (`RunPromptInMode`) and two backends (`DispatchToAssistant`, `DispatchToClipboard`). The existing Sidekick / in-Trados-Assistant routing with silent-fallback-on-Sidekick-failure is preserved unchanged within `DispatchToAssistant`.
- Clipboard write uses `TextDataFormat.UnicodeText` and retries up to 3× with 50 ms delays — covers the classic Office / TeamViewer clipboard contention. Silent on success (menu close is itself confirmation); only surfaces a MessageBox if every retry failed.

---

## [4.19.88] – 2026-05-08

### Added (Project field on term entries)

- Term entries now have a **Project** free-text metadata field for tracking which project a term came from (e.g. `PROJ-033 patent` or a job code). Surfaces in the Termbase Editor as a sortable/filterable column, in the term-entry edit dialog as a tooltipped optional field, and in the inline grid editor's row-edit and inline-add paths.
- Schema migration adds `project` and `client` columns to `termbase_terms` if they aren't already present (Workbench-shared databases already had them via the Workbench's own migration; older Trados-only databases pick them up on first launch).
- The Project field is **bookkeeping only** – it is *not* sent to the LLM in translation prompt blocks. Domain and Client cover the LLM-context use case; Project is for the user's own organisation. If you want a term's project context to inform translation, put it in Notes instead.
- The Termbase Editor's search box now also matches against Notes, Domain, Client, and Project (previously only matched Source / Target / Definition).

---

## [4.19.87] – 2026-05-08

### Fixed (Reports tab: cost shown as "free" for unknown models, masking real cost)

- The cost estimator's pricing table only covers the curated provider lists. When users typed a non-curated OpenRouter model ID (e.g. `deepseek/deepseek-v4-pro`) into the model picker, the lookup missed and `EstimateCost` returned `0`, which the Reports tab then rendered as "free" — same label used for genuinely free Ollama models. Misleading: a $0.02 DeepSeek call appeared identical in the log to a free local Ollama call.
- Now `PromptLogEntry` carries an `IsCostKnown` flag set from `TokenEstimator.HasPricing(model)`. When the model isn't in the pricing table the SummaryLine and "Copy all" output show **unknown** instead of "free", with a "(model not in pricing table)" hint in the long-form output. Genuinely free models (Ollama and any explicit zero-rate entry) continue to show "free" exactly as before.

---

## [4.19.86] – 2026-05-08

### Added (Anthropic prompt caching: ~80% input-cost reduction on batch operations)

- **Batch Translate and Batch Proofreader now mark the static portion of the system prompt with `cache_control: ephemeral`**, so the first batch in a run pays a one-time 1.25× cache-write surcharge and every subsequent batch within ~5 minutes pays only 0.1× of the input rate for the cached portion. For a typical 1000-segment Sonnet 4.6 run with full document context, this drops the API bill from ~$3.69 (estimate) down to ~$1.36 (real) – about a 60–65% reduction overall, and ~80% on the cached portion alone.
- New `enablePromptCaching` parameter on `LlmClient.SendPromptAsync`; passed `true` by both batch services. Single-shot callers (chat, AutoPrompt, single-segment translate) leave it `false` to avoid the 1.25× write-surcharge on calls that won't benefit from cache reads.
- Anthropic native: system prompt is sent as a `[{"type":"text","text":"...","cache_control":{"type":"ephemeral"}}]` block.
- OpenRouter → Anthropic: same `cache_control` marker passed through in OpenAI-shape content array.
- Other providers (OpenAI, DeepSeek, Gemini 2.5+) get implicit automatic caching at the provider layer because the system prompt is byte-stable across batches; no marker needed. Grok / Mistral / Ollama: no caching available, flag is a no-op.

### Added (Reports tab: real API-reported token counts and cost, not estimates)

- **The Reports tab now shows real provider-billed token counts and the real cost of each call, with cache-hit tokens broken out separately**, instead of the previous chars/4 + list-price estimate. The `~` prefix on the cost is dropped when actuals are available; when caching contributed, the cache-hit count is shown inline (e.g. "830,000 in (720,000 cached) / 32,000 out · $1.36").
- New `ApiUsage` type in `LlmClient` capturing `RegularInputTokens` / `CacheReadTokens` / `CacheWriteTokens` / `OutputTokens`. Populated from each provider's `usage` block on every successful call.
- Anthropic native: parses `usage.input_tokens` / `output_tokens` / `cache_creation_input_tokens` / `cache_read_input_tokens`.
- OpenAI shape (OpenAI native, OpenRouter, DeepSeek, Mistral, Grok, Custom): parses `usage.prompt_tokens` / `completion_tokens` plus either `prompt_tokens_details.cached_tokens` (OpenAI native) or `cache_creation_input_tokens` / `cache_read_input_tokens` (OpenRouter passing through Anthropic provider).
- New `TokenEstimator.ComputeActualCost` applies cache-aware multipliers per provider: Anthropic 0.1× read / 1.25× write, OpenAI 0.5× read, DeepSeek 0.1× read, Gemini 2.5+ 0.25× read, others passthrough.
- Falls back gracefully to the chars/4 + list-price estimate when usage isn't available (Ollama, parse failures, providers that don't return usage).

### Added (AI Cost Guide: in-app help dropdown + permanent disclaimer link)

- New help dropdown item **AI Cost Guide** added to the Chat / Batch Operations / Reports tab help menus, opening the GitBook page that explains how costs are computed, links to every provider's own usage console, and gives per-model cost-per-document estimates.
- New permanent footer in the Reports tab: "Token counts and costs are estimates · AI Cost Guide" (right-aligned, light text, link). Always visible so users see the disclaimer without having to click anything; tooltip explains the chars/4 heuristic for the legacy estimate path. With the actual-usage feature above, this disclaimer now applies only to providers that don't report usage (Ollama and edge cases) – everything else shows real billed numbers.

### Added (Batch Translate: pre-run truncation warning when document is bigger than `Max segments`)

- When `IncludeDocumentContext` is on and the active document has more segments than the configured `Max segments` cap, the Batch Translate panel now logs a warning before the run starts, naming exactly which segments will be visible to the AI and which will be omitted (per the existing first-80% + last-20% truncation rule). Previously this happened silently inside `TranslationPrompt.BuildSystemPrompt` and users had no way to know their middle-of-document segments weren't being shown to the AI – which can hurt terminology consistency on long jobs.

### Fixed (`DocumentContextMaxSegments` default mismatch)

- The `AiSettings.DocumentContextMaxSegments` property was auto-initialised to `20`, while the AI Settings panel's NumericUpDown range was 100–2000 with a nominal default of `500`. Effect: any user whose persisted JSON didn't yet contain the key was being silently clamped to 100 segments of document context (well below the panel's intended default), which is too small for any non-trivial document. The auto-init now matches the panel's intended default of `500`. Users with an explicitly saved value are unaffected.

### Fixed (cost estimator: Claude Opus 4.6 / 4.7 priced at 1/3 the real rate)

- The estimator's pricing table had Opus 4.6 and 4.7 at $5 / $25 per million input/output tokens. Anthropic's published Opus 4.x rate is $15 / $75 per million – the table was off by 3×, causing the in-app cost estimate to under-shoot the real bill (e.g. estimate ~$0.57 vs Anthropic Console ~$0.92 on a single AutoPrompt call). Corrected to $15 / $75. Sonnet 4.6 ($3 / $15) and Haiku 4.5 ($1 / $5) were already correct.

---

## [4.19.85] – 2026-05-07

### Fixed (Chat header: source preview leaked Trados inline-formatting tags)

- **The "Source: …" preview at the top of the chat panel was showing raw Trados inline-formatting tags – e.g. `Source: "<cf bold=True>SEVT</cf> <cf size=…>` – instead of plain readable text.** The target preview already used `SegmentTagHandler.GetFinalText` (which returns the visible text only), but the source preview called `Source.ToString()` directly, which serialises the segment with all formatting markers.
- Fix at [`AiAssistantViewPart.UpdateContextDisplay`](src/Supervertaler.Trados/AiAssistantViewPart.cs): route the source through `SegmentTagHandler.GetFinalText` too.

---

## [4.19.84] – 2026-05-07

### Fixed (Chat panel: Send / Stop / Clear button labels clipped at high DPI)

- **At 150% Windows display scaling, the bottom-row chat buttons clipped to "Cle" / "Sto" / partial "Send".** The buttons had explicit `Size = new Size(UiScale.Pixels(60 / 48 / 48), 26)` – tight even at 100%, and once the rendered text width at the higher DPI exceeded the pre-scaled width, the labels chopped off.
- Fix at [`AiAssistantControl.cs`](src/Supervertaler.Trados/Controls/AiAssistantControl.cs): switch all three to `AutoSize = true` with `AutoSizeMode.GrowAndShrink` and the previous widths kept as `MinimumSize`. Internal padding (8 px each side) so the labels never touch the button border.

### Fixed (SuperMemory toolbar: "Distill" clipped at high DPI on narrow side panel)

- **In the chat header, "Distill" clipped to "Disti..." at 150% scaling because the toolbar (`Memory Bank` label + dropdown + `?` + Process Inbox + Health Check + Distill) is a single non-wrapping row that ran out of horizontal space when the Trados side panel was at typical width and everything was scaled up.**
- Fix at [`SuperMemoryToolbar.cs`](src/Supervertaler.Trados/Controls/SuperMemoryToolbar.cs): trim the Memory Bank dropdown width from 180 → 130 logical px. Typical bank names ("default", "test-mb", "client-x") still display fully; the saved horizontal room is enough for "Distill" to fit at 150% scaling. Longer names still scroll inside the dropdown when opened.

---

## [4.19.83] – 2026-05-07

### Fixed (TermLens panel: big bold "A" font-size button clipped at the bottom)

- After the v4.19.79 small-A / big-A redesign, the bigger bold "A" on the right of the TermLens header strip was getting its bottom edge clipped at high Windows display scaling – the small regular "A" rendered fully, but the bold one didn't have enough vertical room inside the 28 px-tall header panel.
- Fix at [`TermLensControl.cs`](src/Supervertaler.Trados/Controls/TermLensControl.cs): bump header height 28 → 32, and trim the big-A font from 11 pt → 10 pt. The size delta vs the small 7 pt A is still clearly visible (and tooltips spell out increase / decrease either way), but the bold A now has comfortable clearance even at 150% scaling.

---

## [4.19.82] – 2026-05-07

### Fixed (Settings → Termbases tab: multiple high-DPI clipping issues)

- **At 150% Windows display scaling on Settings → Termbases, three different layout problems were visible at once:**
  - The right-aligned button row clipped to "Ope Export Import − +" – the **Open** button cropped from "Open" to "Ope", and the **− Remove** / **+ Add** buttons lost their text labels entirely (only the symbol remained).
  - In the termbases grid, the bold column headers **"Write"** and **"Terms"** clipped to "Wr..." and "Ter...".
  - The bottom rows ("Panel font size", "Term shortcuts", "Shortcut delay") had their input controls overlapping the labels, because the labels' AutoSize widths grew past the fixed x=130 input column.
- Fix at [`TermLensSettingsForm.cs`](src/Supervertaler.Trados/Settings/TermLensSettingsForm.cs):
  - All five buttons (Open / Export / Import / Remove / Add) now use `AutoSize = true` with `AutoSizeMode.GrowAndShrink` and `MinimumSize` for the previous fixed widths. The right-edge anchoring chain uses each button's measured `PreferredSize.Width` instead of literal pixel offsets.
  - DataGridView column widths bumped: Read/Write 54→80, Project 72→90, CS 40→56, Terms 60→80. (DGV column widths don't participate in AutoScaleMode.Dpi scaling, so they need explicit headroom.)
  - The bottom-row inputs are now positioned at a shared `inputX` computed from the widest of the three labels' actual `PreferredSize.Width`, with the trailing unit labels ("pt", "ms") chained off the input's `Right` edge. NUDs widened from 60/70 to 80/90 to give the digits a comfortable area after autoscale.

---

## [4.19.81] – 2026-05-07

### Fixed (Settings → Prompts: toolbar and system-prompt buttons clipped at high DPI)

- **At 150% Windows display scaling on the Settings → Prompts tab, the toolbar buttons "New", "Restore" and "Refresh" all clipped their last character ("Ne", "Restor", "Refres"), and the two system-prompt buttons below the right-hand pane ("Edit System Prompt" / "Reset to Default") were similarly clipped.** Cause: each button had a hard-coded `Width` (45 / 65 / 130 / 120 px) chosen to fit at 100% scaling – tight even there, and not enough text room left after the AutoScaleMode.Dpi pass at higher DPIs.
- Fix at [`PromptManagerPanel.cs`](src/Supervertaler.Trados/Controls/PromptManagerPanel.cs): switch every toolbar button (via the `CreateToolbarButton` helper) and the two system-prompt buttons to `AutoSize = true` with `AutoSizeMode.GrowAndShrink`. The previous explicit widths are kept as `MinimumSize` so very-short labels still get a comfortable click target. Position the "Reset to Default" button and the status label dynamically against their neighbours' measured `PreferredSize` / `Right` edges instead of fixed x coordinates, so wider buttons at high DPI don't push them on top of each other.

---

## [4.19.80] – 2026-05-07

### Added (Settings: UI scale dropdown can now go below 100%)

- **The Supervertaler UI scale dropdown in Settings → General previously bottomed out at 100%, so users on hi-DPI machines who found Windows' global scaling too aggressive had no way to dial only the plugin back without changing system-wide settings.**
- Add 70%, 80%, 90% to the existing 100% / 110% / 125% / 150% options. Combined with the auto-detected Windows DPI as the base scale, this lets a user on a 4K monitor at 200% Windows scaling drop the plugin to 200% × 0.8 = 160% effective, etc., without affecting the rest of Trados or other apps.
- Floor stops at 70%: below that the system-rendered widgets (NumericUpDown spinners, checkbox boxes) become disproportionately large vs the text, producing weird-looking layouts. The underlying validation in `TermLensSettings.Load` already permits anything `> 0 && <= 3.0`, so the storage layer was always ready.

---

## [4.19.79] – 2026-05-07

### Fixed (About dialog: "Source code available for security audit" clipped at high DPI)

- **At 150% Windows display scaling, the leading "S" of "Source code available for security audit" in the About dialog was clipped behind the shield emoji.** Cause: the link's `Location.X` was `leftPad + 30`, but the shield emoji's AutoSize width grew at high DPI past that 30 px gap, so the wider emoji's bounding box ate into the link.
- Fix at [`AboutDialog.cs`](src/Supervertaler.Trados/Controls/AboutDialog.cs): position the link dynamically from `shieldLabel.Right + 6` instead of a fixed 30 px gap.

### Fixed (TermLens panel: A+/A- font-size buttons hard to tell apart at high DPI)

- **The two font-size buttons at the top of the TermLens panel had the same problem the AI Assistant chat header had** before 4.19.74: design said "A+" and "A−" with a 2-point font-size difference, but at low DPI the +/− glyphs collapsed to thin strokes and both buttons looked like plain "A".
- Fix at [`TermLensControl.cs`](src/Supervertaler.Trados/Controls/TermLensControl.cs): same redesign as the chat header in 4.19.74 – drop the +/− glyphs, use a big bold "A" (11pt) for increase and a small regular "A" (7pt) for decrease, and add explicit "Increase TermLens font size" / "Decrease TermLens font size" tooltips on hover.

---

## [4.19.78] – 2026-05-07

### Fixed (AI Settings: Test Connection text wrapped, Show button clipped at high DPI)

- **At 150% Windows display scaling on Settings → AI Settings, the "Test Connection" button text wrapped to two lines (button was 120 px wide; the scaled text needed more), and the "Show" button next to the API Key field was clipping to "Sho" (button was only 50 px wide).** Same general cause as the NUD fix in 4.19.77 – design-time pixel widths were tight even at 100% and ran out of horizontal room once `AutoScaleMode.Dpi` finished scaling the control.
- Fix at [`AiSettingsPanel.cs`](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs): widen `_btnShowKey` from 50 → 80 and `_btnTestConnection` from 120 → 160. Both now have comfortable padding at any DPI.

---

## [4.19.77] – 2026-05-07

### Fixed (AI Settings: cramped NumericUpDowns at high Windows DPI)

- **At 150% Windows display scaling, the "Batch size" and "Surrounding segments" numeric inputs in Settings → AI Settings rendered with so little room for digits that the value was hard to read** – the system-drawn spinner buttons (which don't scale identically with the rest of the control) ate most of the visual width, leaving only a few pixels for the actual number. The "Surrounding segments" NUD also overlapped its own label, because the label's AutoSize width grew at high DPI but the NUD's x position was fixed at 210 px.
- Fix at [`AiSettingsPanel.cs`](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs): bump both NUDs from `Width = 60` to `Width = 80` so the AutoScaleMode.Dpi pass produces a visibly comfortable text area at any scaling. Also position `_nudSurroundingSegments` dynamically from `_lblSurroundingSegments.Right + 8` instead of the fixed x=210 column the other rows use, so the wider label at high DPI doesn't push it.
- The other two NUDs in this panel (`_nudOllamaTimeout` at width 75, `_nudMaxSegments` at width 80) were already wide enough; no change there.

---

## [4.19.76] – 2026-05-07

### Fixed (Hi-DPI: every Settings dialog and pop-up now scales with Windows display scaling)

- **Earlier today's fix only covered the `BatchTranslateControl` (Batch Operations panel) and the `AiAssistantControl` (Chat header). The other 23 dialogs and panels in the plugin – Settings, AI Settings, Prompts, Termbase Editor, AI Proofreader Reports, About, Setup, every term-add / merge / preview pop-up – still relied on WinForms' default font-based autoscaling, which doesn't reliably propagate through plugin UserControls hosted inside Trados.** Nobody had complained yet, but layout would have squished at 125% / 150% / 200% Windows display scaling on every one of those surfaces.
- Fix: each of those 23 forms / UserControls now sets `AutoScaleMode = AutoScaleMode.Dpi` in its constructor or `BuildUI()` method. This activates WinForms' DPI-based scaling pass, which scales control sizes and positions by the raw `currentDpi / designDpi` ratio – the same mechanism `TermPopup` has used successfully for the term-popup. The two surfaces with their own UiScale-driven layout (`AiAssistantControl`, `BatchTranslateControl`) keep `AutoScaleMode = None` so they don't double-scale on top of UiScale.
- No functional behaviour change at 100% Windows scaling. At 125% / 150% / 200% scaling, plugin dialogs now scale uniformly with the rest of Trados Studio's UI instead of staying at 100% and squishing.

---

## [4.19.75] – 2026-05-07

### Changed (HelpSystem: use canonical GitBook URL slugs, drop reliance on 301 redirects)

- **The `?` help buttons throughout the plugin used to open URLs like `https://supervertaler.gitbook.io/help/trados/termlens`, which GitBook then 301-redirected to the actual published slug `https://supervertaler.gitbook.io/help/features/termlens`.** Two problems with relying on the redirect: (a) URL fragments such as `#reports-tab` are preserved across redirects only because of browser-side fragment carrying (HTTP spec behaviour), not GitBook itself; (b) GitBook may eventually prune legacy redirect entries, especially after repo migrations like the recent docs split into the standalone `Supervertaler-Help` repo.
- Fix at [`HelpSystem.cs`](src/Supervertaler.Trados/Core/HelpSystem.cs): every `Topics.*` constant now holds the canonical slug GitBook actually publishes. The slug is generated from each `## 🧩 Section` header in [`SUMMARY.md`](https://github.com/Supervertaler/Supervertaler-Help/blob/main/SUMMARY.md), so `Overview` becomes `get-started/trados`, `TermLensPanel` becomes `features/termlens`, `BatchTranslate` becomes `features/batch-operations/batch-translate`, and so on. Verified against the live sitemap (`sitemap-pages.xml`): every constant now resolves with HTTP 200 and zero redirects.
- Mirror of the equivalent fix done earlier today on the Workbench side ([`modules/help_system.py`](https://github.com/Supervertaler/Supervertaler-Workbench/blob/main/modules/help_system.py)). Also updates the audit table in [`HELP-LINKS.md`](HELP-LINKS.md) at the repo root – previously last audited 2026-03-13 with the legacy `gitbook.io/trados` base URL.

---

## [4.19.74] – 2026-05-07

### Fixed (Chat panel: long histories caused new responses to vanish)

- **In the AI Assistant Chat tab, after ~23 explain/define prompts or a 40,000+ character AutoPrompt response, the panel would freeze – the OpenRouter / GWDG / Cortecs API would return the response (visible in OpenRouter observability, tokens billed) but the chat would scroll upwards and not display anything new. The only workaround was to clear the chat.** Reported by a user on 2026-05-06; not actually fixed by the v4.19.1 chat-scroll rewrite.
- Root cause at [`AiAssistantControl.UpdateChatScrollRange`](src/Supervertaler.Trados/Controls/AiAssistantControl.cs): the formula `_chatPanel.AutoScrollMinSize = new Size(0, _messageFlow.Top + desiredHeight)` was meant to be a "belt and braces" override of WinForms' automatic scroll-range tracking. But `_messageFlow.Top` is in `_chatPanel`'s scrolled coordinate system: when the user is scrolled down N px into a long chat, Top reads as `-N`, not as the unscrolled top. So at the bottom of a long history the formula collapsed to roughly the viewport height instead of the full content height. WinForms then clamped the scroll position to fit that artificially-small range, the panel snapped back to the top, and the new bubble landed below where the panel now thought the world ended.
- Fix uses `desiredHeight` directly (the actual content height). `_messageFlow` always lives at logical y=0 inside `_chatPanel` – it's the only child, no header above it – so the unscrolled bottom is just the content height. Verified locally and matches the reported symptom exactly.

### Fixed (Plugin layout broke at high Windows DPI scaling)

- **At Windows display scaling above 100% (e.g. 150% on a 2560×1440 panel), the Translate / Proofread radio buttons in Batch Operations overlapped each other, the Scope/Limit/Prompt rows squished together, and the Copy/Paste Clipboard buttons collided.** Reported by a user on a real machine; reproduced locally by flipping Windows scaling to 150%.
- Cause: the existing `UiScale.Factor` defaulted to 1.0 and was only changed by a manual TermLens settings slider. Plugin layouts that called `UiScale.Pixels()` therefore stayed at 100% sizing even as Windows scaled fonts up, and any literal pixel coordinates (e.g. `Location = new Point(100, y)` for the Proofread radio button) couldn't account for the wider AutoSize "Translate" label at higher font sizes.
- Fix at [`UiScale`](src/Supervertaler.Trados/Core/UiScale.cs) now auto-detects the system DPI scale (`Graphics.DpiX / 96`) on plugin startup and uses it as a base layer beneath the user-configurable settings slider. `UiScale.Pixels(N)` and `UiScale.FontSize(N)` now multiply by `SystemScale × UserFactor`, so existing call sites get DPI awareness for free and the slider becomes a per-user multiplier on top of system scaling rather than the only source of scaling. `BatchTranslateControl` and `AiAssistantControl` set `AutoScaleMode = AutoScaleMode.None` so WinForms' built-in font-based autoscaling doesn't apply a second pass on top of UiScale and double up. Every hard-coded `Location` / `Size` / `Width` / `Height` literal in `BatchTranslateControl` is now wrapped through `UiScale.Pixels`, and the Translate / Proofread radio button positions are now computed dynamically from the previous button's measured `PreferredSize` so wider fonts can't push them on top of each other. The Copy / Paste Clipboard buttons get the same dynamic-position treatment.

### Fixed (AI Assistant: A+ / A- font-size buttons hard to tell apart at high DPI)

- **The two font-size buttons at the top of the Chat tab read "A+" and "A−" (with U+2212 minus sign), differing only by the +/− glyph and a 2-point font-size difference. At a user's display DPI the +/− glyphs collapsed into thin strokes and both buttons looked like plain "A". They clicked the smaller-font one expecting "increase" – and got the opposite.**
- Fix at [`AiAssistantControl`](src/Supervertaler.Trados/Controls/AiAssistantControl.cs) drops the +/− glyphs entirely and uses font size as the sole visual cue: a big bold "A" on the right (`UiScale.FontSize(11f)` Bold) and a small regular "A" on the left (`UiScale.FontSize(7f)` Regular). Same pattern Edge and Word reading-view use, immediately readable at any DPI. Both buttons now also have explicit tooltips ("Increase chat font size" / "Decrease chat font size") so anyone hovering sees which is which.

### Improved (Batch Operations: AutoPrompt visible-but-disabled in Proofread mode)

- **Previously the AutoPrompt link disappeared entirely when the user selected Proofread mode**, which led to a confused support thread ("AutoPrompt has vanished – I tested on another machine to make sure it's not my screen resolution"). The link is hidden because AutoPrompt generates a *translation* prompt and doesn't apply to proofread runs, but that intent was invisible.
- Fix at [`BatchTranslateControl`](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) keeps the link visible in Proofread mode but greys it out and prepends an "Available in Translate mode only" line to its tooltip so users immediately understand why it's disabled rather than thinking it's a bug.

---

## [4.19.73] – 2026-05-06

### Fixed (Proofread: report numbering drifted from the Trados grid after merges/splits)

- **In projects with merged or split segments, the segment numbers shown on Reports-tab issue cards no longer matched the numbers Trados shows in the editor grid.** Every merge added a one-segment offset to all subsequent rows, so on a project with three merges the last segment of the file was reported with a number that was 3 lower than the editor grid said. Reported by a user on a real client project.
- Root cause at [`AiAssistantViewPart.cs`](src/Supervertaler.Trados/AiAssistantViewPart.cs:4310): the v4.8.0 multi-file alignment logic numbered segments by counting iterations through `_activeDocument.SegmentPairs` (`fileSegIdx++` per pair) and only used the parsed segment ID as a heuristic for detecting file boundaries. Iteration count and Trados' own segment IDs diverge after a merge: Trados retires the higher ID of the merged pair (so the editor jumps from 3 to 5), but the iteration count keeps marching 1, 2, 3, 4, 5… → off-by-N from then on. Splits had a similar issue.
- Fix uses `pair.Properties.Id.Id` directly as the per-file segment number when it parses as an int, which is exactly the number Trados shows in the grid and which it preserves correctly across merges and splits. Falls back to the iteration counter only when the segment ID isn't parseable (older formats / exotic filters).

### Fixed (AutoPrompt: blank-line bloat in saved prompts)

- **AutoPrompt-generated prompts were arriving in the Edit Prompt dialog with two or three blank lines between sections** instead of a single blank line. Reported by Michael with a screenshot showing the gaps around `1. ROLE`, `2. TECHNICAL DOMAIN`, etc. Cause: `PromptGenerator.ParseGeneratedPrompt` only `.Trim()`s the AI's response between the `===PROMPT_START===` / `===PROMPT_END===` delimiters, and the meta-prompt didn't tell the model to use single blank-line separators – so the model fell back to its default "looks pretty" spacing, which means 2-3 blank lines around section headings.
- Fix at [`PromptGenerator.cs`](src/Supervertaler.Trados/Core/PromptGenerator.cs) now collapses 3+ consecutive newlines to a single blank line in `ParseGeneratedPrompt`, and the meta-prompt under "OUTPUT INSTRUCTIONS" now spells it out: *"Use exactly ONE blank line between sections, paragraphs, and list blocks. Never insert two or more consecutive blank lines."* Belt-and-braces: the regex catches model output that ignores the instruction; the instruction nudges newer / better models toward the right output without the regex having to clean up.

### Fixed (Batch operations: focus steal back to Translation Results between batches – proper fix)

- **The v4.19.66 batch-boundary fix was not enough on slow runs and on the very last batch.** Re-Activating the Supervertaler Assistant viewpart only at the START of the next "Translating batch N+1" message was timed correctly for fast runs (steal happens in the gap, Activate happens after the steal) but lost the race on slow API calls (where the steal landed mid-gap and stuck) and on the last batch (no next-batch message to counter the steal at all – user ended up on Translation Results when the run finished).
- Fix at [`AiAssistantViewPart.cs OnBatchProgress`](src/Supervertaler.Trados/AiAssistantViewPart.cs:3476) now also activates on `"✓ Batch X complete"` messages and uses the same dual sync + deferred `BeginInvoke` `Activate()` pattern that already worked for `OnNavigateToSegment` (v4.19.71). [`OnBatchCompleted`](src/Supervertaler.Trados/AiAssistantViewPart.cs:3567) at end of run also gets the dual Activate. The deferred call runs after Trados' queued focus steal and reliably wins.

---

## [4.19.72] – 2026-05-06

(Folded into 4.19.73 – no separate AppStore release. Same fixes ship in 4.19.73.)

---

## [4.19.71] – 2026-05-06

### Fixed (Reports tab: clicking an issue card kicked focus to Translation Results)

- **Every click on a proofreading issue card sent the user to Trados' built-in Translation Results pane, forcing a manual switch back to the Supervertaler Assistant pane to read the issue details.** Same root cause as the v4.19.66 batch-boundary fix: `SetActiveSegmentPair` fires Trados' active-segment-changed event, and the built-in Translation Results pane reacts by re-running its TM/MT lookups, which on Trados 18 brings its tab to the front. Reported by Michael while working through proofread results — described it as "annoying" and "I have to switch back to the Supervertaler Assistant pane every time I click into a heading."
- Fix at [`AiAssistantViewPart.OnNavigateToSegment`](src/Supervertaler.Trados/AiAssistantViewPart.cs) now calls `Activate()` on the Supervertaler Assistant view-part after navigating, so the user lands back on Reports immediately. Belt-and-braces: a synchronous `Activate()` handles the case where Trados raises its event inline, and a second `Activate()` posted via `BeginInvoke` handles the case where Trados queues the focus steal for a later UI tick — by running after the steal has already happened, the deferred call reliably wins. Same belt-and-braces pattern that worked for v4.19.66.

---

## [4.19.70] – 2026-05-06

### Fixed (Batch Operations: "Also add issues as Trados comments" jumped into the log area on mode switch)

- **The Trados-comments checkbox migrated down into the log panel the first time the user switched to Proofread mode.** Visible in Michael's screenshot: button + Preview prompt link sat correctly on the action row, but the checkbox was floating ~80 px lower, near the Log header. Caused by the `_btnTranslate.SizeChanged` lambda introduced in v4.19.69 capturing `y` by reference. `y` is the running layout cursor in [`BatchTranslateControl.BuildUI`](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs); by the time the lambda fired (button text change "Translate" → "Proofread" → SizeChanged), `y` had been incremented past the action row by the rest of `BuildUI` (Clipboard buttons, log panel header, etc.), so the checkbox got placed at whatever Y the cursor had landed on. Classic closure-over-mutable-loop-variable trap.
- Fix at [BatchTranslateControl.cs](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) captures `y` into a local `actionRowY` before the lambda is created. Both the SizeChanged repositioner and the Preview prompt link's initial Y now reference `actionRowY` so they stay anchored to the action row regardless of when the lambda runs.

---

## [4.19.69] – 2026-05-06

### Fixed (CRITICAL: v4.19.68 broke the build)

- **`ClipboardRelay.FormatForProofreading` was passing `List<string> documentSegments` and `int maxDocumentSegments` to `ProofreadingPrompt.BuildSystemPrompt`** at [ClipboardRelay.cs:107-110](src/Supervertaler.Trados/Core/ClipboardRelay.cs), but v4.19.68 changed `BuildSystemPrompt`'s signature to take `List<(string source, string target)> documentSegments` and dropped the `maxDocumentSegments` parameter. The build error was masked by the `bash build.sh` skip in v4.19.68's "no build for now" instruction. Caught while doing the prompt-preview wiring in v4.19.69 — anyone trying to compile v4.19.68 would have hit it immediately. Fixed by updating `FormatForProofreading`'s parameter type to match and updating its caller in [AiAssistantViewPart.OnCopyToClipboardRequested](src/Supervertaler.Trados/AiAssistantViewPart.cs) to call the new `CollectBilingualDocumentContext` for proofread mode (so the clipboard text faithfully matches what the API path would now send).

### Improved (Batch Operations: "Preview prompt" link + checkbox positioning)

- **New "👁 Preview prompt" link next to the Translate / Proofread button** opens a read-only dialog showing EXACTLY what would be sent to the AI for the current configuration — assembled system prompt (incl. termbase, language-specific checks, full bilingual document context for proofread, and the active custom prompt) plus the numbered segment list. Reuses the same `ClipboardRelay` assembly as Copy-to-Clipboard so what users see is what the LLM would receive. Works in both API mode and Clipboard mode without toggling. The dialog has its own "Copy to clipboard" button as a bonus, so it doubles as a "manual paste into web LLM" path. New file: [PromptPreviewDialog.cs](src/Supervertaler.Trados/Controls/PromptPreviewDialog.cs); event wired through [BatchTranslateControl.PreviewPromptRequested](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) → [AiAssistantViewPart.OnPreviewPromptRequested](src/Supervertaler.Trados/AiAssistantViewPart.cs). Mirrors the equivalent Workbench feature.
- **Fixed the "Also add issues as Trados comments" checkbox text being clipped behind the Proofread button.** The checkbox was hardcoded at `x=140` while the action button at `x=12` is AutoSize – when the text changed from "▶ Translate" to the wider "▶ Proofread", the button extended past x=140 and covered the checkbox's leading characters. Reported by Michael with a screenshot showing "...so add issues as Trados comments" with the leading "Also" hidden. Fixed in [BatchTranslateControl.cs](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) by binding the checkbox position to `_btnTranslate.SizeChanged` (always sits at `_btnTranslate.Right + 8`). Same logic positions the new Preview prompt link, which sits after the rightmost visible control on the row regardless of mode.

---

## [4.19.68] – 2026-05-06

### Improved (Batch Proofread: full bilingual document context + citation discipline + Evidence field)

- **Batch Proofread now sees the entire document, source AND target, when reviewing each batch – not just source-only with a 500-segment cap.** The previous behaviour gave the model the full source for the whole document but bilingual data only for the 20 segments in the current batch. That made target-side consistency claims unverifiable: when the model said "you used X here but Y elsewhere" for the target, it was guessing about segments outside the current batch. New `CollectBilingualDocumentContext` in [AiAssistantViewPart.cs](src/Supervertaler.Trados/AiAssistantViewPart.cs) collects every segment pair (source + target) from the active document and feeds them into a redesigned `# DOCUMENT CONTENT` block in [ProofreadingPrompt.cs](src/Supervertaler.Trados/Core/ProofreadingPrompt.cs). Truncation is gone – proofreading is exactly the workflow where mid-document segments matter for cross-document consistency, so the 500-cap and the 80/20 split it enforced were actively harmful. Token cost roughly doubles (both sides now sent), which is fine for typical patent / legal / technical docs (<500 segments) and reasonable up to a few thousand on Sonnet/Opus.
- **Segment numbers in the model's output are now document-absolute, not within-batch.** [BatchProofreader.cs:137](src/Supervertaler.Trados/Core/BatchProofreader.cs) used to emit `[SEGMENT 0001]` … `[SEGMENT 0020]` for every batch (resetting per batch). The model couldn't tell whether `[SEGMENT 0007]` in its response meant the 7th segment of the document or the 7th of the current batch. Now the prompt uses `segments[i].Index + 1` everywhere, so the same number means the same segment in the user prompt, in `# DOCUMENT CONTENT`, in the model's response, and in any Evidence: citations the model produces.
- **New `Evidence:` field on every issue card – forces the model to ground terminology consistency claims in specific segment citations.** Output format spec in `ProofreadingPrompt.BuildSystemPrompt` now documents an optional Evidence: line; `ParseBatchResponse` captures it; `ProofreadingIssue.Evidence` carries it through to the UI; [ReportsControl.cs](src/Supervertaler.Trados/Controls/ReportsControl.cs) renders it between Issue and Suggestion in italic grey ("the *why* for the issue"). The right-click menu gains a "Copy evidence" option; "Copy all" includes evidence in the clipboard payload. The new Default Proofreading Prompt requires Evidence: for any consistency claim – inconsistency claims without concrete segment citations from `# DOCUMENT CONTENT` are not allowed.
- **Default Proofreading Prompt rewritten** to (a) stop duplicating the hardcoded base in `ProofreadingPrompt.BuildSystemPrompt` (persona, 5 categories, output format, language-specific Dutch/German/French checks, "no corrections" rule – all already auto-included), and (b) add four behaviours genuinely missing from the base: default-to-OK framing, citation discipline against `# DOCUMENT CONTENT`, a "Source query:" prefix for source-side errors (so source typos get flagged without proposing target changes), and explicit boundaries (don't re-engineer the source, don't propose alternative terminology without a citation, don't flag empty-target segments, patent / legal / technical text is deliberately stiff). Targeted at the false-positive patterns observed in real proofreads: the model fabricating "term X used elsewhere" claims, second-guessing the source's substantive claims, and treating stylistic preferences as errors.
- **One-shot migration refreshes outdated default-prompt copies in existing installs** at [PromptLibrary.cs](src/Supervertaler.Trados/Core/PromptLibrary.cs). `EnsureDefaultPrompts` previously skipped the write when the file existed on disk – which meant nobody who'd ever run an earlier version would get the new prompt. New `RefreshOutdatedDefaultProofreadingPrompt` detects the v4.13.0–v4.19.66 default by its distinctive section headers (`## 1. Accuracy`, `## 2. Completeness`, `## 5. Number & Unit Formatting`) and absence of the new content marker (`# Verifying claims against the document`), and deletes the stale copy so the same `EnsureDefaultPrompts` pass writes the new content. User-cloned/edited prompts have `default: false` in their YAML frontmatter and are never touched.

---

## [4.19.67] – 2026-05-06

### Fixed (Anonymous usage statistics: pings going to a non-existent Cloudflare subdomain)

- **The plugin's anonymous usage pings have been silently failing for ~6 weeks.** The Trados Plugin tab on the stats dashboard showed 8 unique users all-time but zero active in the last 30 days, while the Workbench tab kept getting fresh data. Caused by a wrong-subdomain change in v4.18.20 (28 Mar 2026) that pointed pings at `supervertaler-stats.supervertaler.workers.dev` – a Cloudflare account subdomain that doesn't exist. The actual Worker lives at `supervertaler-stats.michaelbeijer-co-uk.workers.dev` (account subdomains are tied to the Cloudflare account, not the GitHub org). The Workbench's Python client was untouched and kept pinging the correct URL throughout, which is why only the Trados tab went quiet.
- Fix at [UsageStatistics.cs:32](src/Supervertaler.Trados/Core/UsageStatistics.cs) restores the correct URL. The 30-day metric on the dashboard will stay depressed for at least 1–2 weeks until existing installs update via the AppStore – `HttpClient.PostAsync` swallows the exception in the existing `catch` block, so users on broken versions will keep failing silently until they upgrade.

---

## [4.19.66] – 2026-05-04

### Fixed (Batch operations: Translation Results pane stole focus at every batch boundary)

- **During Batch Translate / Batch Proofread, every batch boundary kicked the user away from the Supervertaler Assistant pane back to Trados Studio's built-in Translation Results pane.** With a Supervertaler Assistant tab docked next to Translation Results in the bottom dock strip, the user couldn't watch the live progress log because the tab kept switching itself away. Reported by Michael halfway through a 213-segment patent translation across 8 batches.
- Root cause: `ProcessSegmentPair` writes each translated segment via Trados' supported API, which moves the editor's active-segment cursor to the just-written segment. The built-in Translation Results pane is wired to react to active-segment changes and re-runs TM/MT lookups for the new segment, which on Trados 18 brings its tab to the front. During a batch the writes are fast enough that Trados can't keep up – so the focus steal happens once, in the gap between batches, when the model is busy producing the next batch's response.
- Fix at [AiAssistantViewPart.cs OnBatchProgress](src/Supervertaler.Trados/AiAssistantViewPart.cs): when a `BatchProgressEventArgs.Message` starts with `"Translating batch "` or `"Proofreading batch "` (i.e. a new batch is starting), call `Activate()` on the AiAssistantViewPart to re-bring its tab to the front. Triggering on batch-start rather than batch-end is intentional: Trados' focus steal happens in the gap that follows the batch-complete log line, so re-activating at the START of the next batch happens after the steal and reliably wins. There's a brief flicker (Translation Results visible for a fraction of a second between batches) but the user is back on the Supervertaler Assistant log immediately afterwards. Same fix covers Batch Proofread because `BatchProofreader` and `BatchTranslator` share the `OnBatchProgress` handler.
- No change to actual translation behaviour. If the user explicitly switches to Translation Results mid-batch, they'll be kicked back at the next batch boundary – an acceptable trade-off for the much-more-common "stay on the live log" workflow.

---

## [4.19.65] – 2026-05-04

### Fixed (AutoPrompt tooltip: clarify that Clipboard Mode does not apply)

- **The AutoPrompt link tooltip didn't mention that Clipboard Mode is ignored by AutoPrompt.** Reported by Michael while drafting an auto-prompt with Clipboard Mode ticked – reasonable assumption that "this batch operation goes via clipboard" applied to AutoPrompt too, but the AutoPrompt handler at `AiAssistantViewPart.OnGeneratePromptRequested` doesn't reference `IsClipboardMode` at all and always sends the meta-prompt to whichever AI provider is configured in AI Settings. Added a paragraph to the tooltip in [BatchTranslateControl.cs:286-298](src/Supervertaler.Trados/Controls/BatchTranslateControl.cs) documenting this and pointing out the useful pattern it enables: keep Clipboard Mode ticked, click AutoPrompt to generate the prompt via your paid API, then run the bulk Translate via clipboard against a free web-tier model (ChatGPT / Claude.ai / Gemini). Best of both worlds – paid API for the small-but-clever prompt-writing call, free web tier for the expensive bulk translation.
- No behavioural change. `AutoPopDelay` raised to 12 s so the longer tooltip doesn't dismiss before you finish reading.

---

## [4.19.64] – 2026-05-04

### Fixed (CRITICAL: termbase write target persisted across project switches)

- **Quick-add (Alt+Down) and Add Term dialog (Ctrl+Alt+T) were writing new terms to the *previous* project's Write termbase, even after switching projects.** The Settings → Termbases tab correctly showed all termbases unchecked for the new project (because the in-memory overlay had been applied), but the global `TermLensSettings.json` on disk still held the previous project's `WriteTermbaseIds`. Both `QuickAddTermAction` and `AddTermAction` call `TermLensSettings.Load()` to read the current write targets, which read the **disk** copy – getting the stale list and writing terms to the wrong termbase. Reported by Michael with a reproduction case (new project, all termbases unchecked in settings, Alt+Down still wrote two terms to the patents termbase).
- The fix at [TermLensEditorViewPart.cs:1022-1052 / 1557 / 1582](src/Supervertaler.Trados/TermLensEditorViewPart.cs) calls `_settings.Save()` after every `_settings.ApplyProjectOverlay(...)` so the global settings file is kept in sync with the current project's effective Write/Project termbase IDs. Three call sites updated: project-detected-existing, project-detected-new, settings-reloaded, and post-term-added refresh.
- Anyone running v4.19.55–63 should check their Patents termbase (or whatever they had last set as Write before switching to a new project) for stray terms that ended up there by accident. Audit by sorting the Termbase Editor's Created column descending – stray terms will cluster around the project switches.

### Fixed (AI Settings: misleading "Include TM matches" label and tooltip)

- **The "Include TM matches in AI context" checkbox was documented as Chat / QuickLauncher only, but it also gates AutoPrompt's reference-pair sampling.** The section heading note read *"These settings do not apply to Batch Operations."* and the checkbox tooltip claimed *"Only applies to Chat and QuickLauncher – not to Batch Operations."*. In reality `AiAssistantViewPart.OnGeneratePromptRequested()` reads the same `IncludeTmMatches` flag at line 1159 to decide whether to call `CollectTmReferencePairs()` – which walks the active document and samples up to 50 already-translated, human-confirmed segment pairs to ship into the meta-prompt. AutoPrompt is a Batch Operations feature, so users who turned the checkbox off thinking it didn't matter for AutoPrompt were silently losing in-project translation examples. Reported by Michael with a screenshot annotating the contradictory wording.
- The tooltip's other claim – that the checkbox enables *"Translation Memory fuzzy matches for the active segment"* – was true for Chat / QuickLauncher (live TM lookups, fuzzy + exact) but wrong for AutoPrompt: AutoPrompt doesn't do a TM lookup at all; it samples confirmed segments straight from the active document, so 100% / exact matches that have been applied and confirmed are absolutely included alongside fuzzy-edited and from-scratch translations.
- The fix at [AiSettingsPanel.cs:654-695](src/Supervertaler.Trados/Controls/AiSettingsPanel.cs) renames the checkbox to *"Include TM matches in AI context (Chat, QuickLauncher, AutoPrompt)"*, softens the section note to *"Most of these apply to Chat and QuickLauncher only – see each tooltip for exceptions."*, and replaces the tooltip with a longer description that documents both code paths separately and clarifies that other Batch Operations (Translate, Proofread) are unaffected by the checkbox. No behavioural change – the checkbox already controlled both code paths; this release just makes the UI honest about it.

---

## [4.19.63] – 2026-05-04

### Fixed (AI Assistant: provider name in error messages)

- **Error messages now name the actual provider you selected**, not "OpenAI". OpenRouter / DeepSeek / Mistral / Grok / Custom-OpenAI users were seeing things like "OpenAI indicated tool_calls but no calls found in response" because the provider-label helper at [LlmClient.cs:393](src/Supervertaler.Trados/Core/LlmClient.cs#L393) didn't have an OpenRouter case (fell through to the "OpenAI" default), and one tool-use error string was hardcoded to "OpenAI" rather than going through the helper. Now `OpenAiProviderLabel()` includes OpenRouter, and the tool-use error formats with the dynamic label.
- Reported by a user who saw "OpenAI" in the error while running DeepSeek-V4-Pro through OpenRouter.

### Added (AI Assistant: defensive fallback when tool_calls is empty)

- **When `finish_reason == "tool_calls"` but the response contains no tool-call objects, fall through to the text content** instead of throwing. Some providers (notably DeepSeek through OpenRouter, and some Custom-OpenAI gateways) occasionally set the finish reason but omit the `tool_calls` array – the model intended to answer in plain text after all. With this fix, those turns succeed with the model's text reply rather than blowing up the whole conversation. If neither tool calls nor text is present, the error message remains but now uses the dynamic provider label.

---

## [4.19.62] – 2026-05-02

### Fixed (TSV export/import preserves multi-line fields)

- **Termbase TSV export now escapes newlines, tabs, and backslashes** in every text field (uuid, source/target cell, domain, notes, project, client) using backslash-prefixed sequences (`\n`, `\r`, `\t`, `\\`). Pre-fix, a notes field that contained a multi-paragraph response or any other multi-line content would write actual line breaks into the TSV, breaking the one-record-per-line invariant the matching importer relies on – re-importing the file would split the markdown across many phantom rows with the wrong column alignment, scrambling the termbase. **TSV import** now applies the inverse unescape on the same fields, so the round-trip preserves the original formatting exactly.

  The escape format is asymmetric (only Supervertaler unescapes on import), but it survives Excel and other TSV viewers without mis-splitting rows, which is the more important property for a working file.

  TSVs exported before v4.19.62 that contain literal newlines in fields are not silently fixed by this change – they were already broken on disk. Re-export to get a clean copy. Hand-built TSVs without any backslashes import unchanged because the unescape pass is a no-op on backslash-free values.

---

## [4.19.61] – 2026-05-02

### Added (Termbase Editor: sort by creation time)

- **New "Created" column in the Termbase Editor.** Surfaces the `termbase_terms.created_date` value the schema has stored for every term since v1 (default `CURRENT_TIMESTAMP`). Click the column header to sort by date – click again to reverse. Useful for finding and removing recently-added entries: add a term, realise it was wrong, sort by Created descending, the row is at the top. Read-only, formatted `yyyy-MM-dd HH:mm`. `TermEntry.CreatedDate` (nullable `DateTime`) and the `GetAllTermsByTermbaseId` SELECT both updated to expose the column.

---

## [4.19.60] – 2026-05-02

### Added (route QuickLauncher prompts to Workbench Sidekick)

- **`AiSettings.QuickLauncherTarget`** – new string setting (data-member `quickLauncherTarget`, values `"TradosAssistant"` (default) or `"WorkbenchSidekick"`) that picks where Ctrl+Q QuickLauncher prompts run. The default keeps the existing behaviour: prompt + response stay in the in-Trados Assistant chat. The Workbench-Sidekick option posts the expanded prompt to Supervertaler Workbench's Sidekick Chat over a localhost bridge, with the response rendered in Sidekick instead of in Trados.
- **`Core.WorkbenchSidekickClient`** – inverse of the existing `Core.SidekickBridge`. Discovers the Workbench's bridge via the handshake file at `<root>/workbench/runtime/sidekick-bridge.json`, validates the PID is alive, POSTs to `http://127.0.0.1:<port>/v1/run-prompt` with a Bearer token. On any failure (Sidekick not running, stale handshake, network error) the call site silently falls back to the in-Trados Assistant so a missing Sidekick never blocks the user from running their prompt.
- **AI Settings dropdown** – a new "QuickLauncher prompts go to:" combo box in Settings → AI Settings, with a tooltip explaining the two targets and the fallback behaviour.

The Workbench side of this feature ships in Supervertaler-Workbench (modules/sidekick_bridge_server.py and a new `run_prompt_requested` signal handler in `FloatingAssistant`); upgrade Workbench to the matching version for the route to work.

---

## [4.19.59] – 2026-05-02

### Changed (stronger key for the non-matching-termbase confirmation list)

- **Confirmation list now keyed by termbase name instead of ID.** The persistence field added in v4.19.58 (`confirmedNonMatchingWriteTermbaseIds`) used the SQLite `id` column. With `INTEGER PRIMARY KEY AUTOINCREMENT` ID reuse within one database is impossible, but a user who wipes and recreates `supervertaler.db` could end up with stale ID-based confirmations applying to different termbases. The schema declares `name TEXT NOT NULL UNIQUE` on the `termbases` table, so names are stable across rebuilds in a way IDs aren't. Renamed the field to `ConfirmedNonMatchingWriteTermbaseNames` (data-member `confirmedNonMatchingWriteTermbaseNames`) and switched all lookup/add/remove sites to key on `tb.Name`. Renaming a confirmed termbase now triggers a re-ask on the next tick – which matches the principle that consent should be tied to a stable identity.

---

## [4.19.58] – 2026-05-02

### Added (UX guard for non-matching Write / Project termbases)

- **Confirm dialog when assigning a non-matching termbase as Write or Project.** Pre-v4.19.56 the inversion logic would silently swap source/target columns when the user ticked an unrelated-language-pair termbase as Write, writing terms in the wrong direction. v4.19.56 stopped the silent swap, but the user could still tick a non-matching termbase as Write and have new terms written into a database whose language pair didn't match the project. v4.19.58 catches this at tick-time: when the user ticks Write or Project on a termbase whose `LanguageUtils.CompareTermbaseDirection` reports as `Unrelated`, the Settings → Termbases tab now shows a confirm dialog ("This is an EN-NL termbase, but the active project's source language is German…") with a "Yes, add anyway" / "No" choice. Confirmed termbases are remembered in `TermLensSettings.ConfirmedNonMatchingWriteTermbaseIds` so the user is never re-asked for the same termbase. Unticking the box clears the override so a future re-tick re-asks. Header "tick-all" Write skips non-matching termbases that haven't been individually confirmed, so a bulk select can't sneak unrelated termbases through. Read-only ticks (the Read column) are exempt – there's no harm in *reading* a non-matching termbase, only in writing into it.
- **`TermLensEditorViewPart.GetCurrentProjectSourceLanguage()`** – new public static accessor that mirrors the existing `GetCurrentProjectPath` / `GetCurrentProjectName` helpers, used by the Settings dialog to find out the active project's source language at form-open time.

---

## [4.19.57] – 2026-05-02

### Added (regression guard for the language-direction helper)

- **`LanguageUtils.RunStartupSelfTest`** — exercises `CompareTermbaseDirection` against 16 canonical language-name shapes (full names, BCP-47 codes, abbreviated regions, missing/empty inputs, mismatched pairs, same-language-different-region) at plugin startup. Result is logged to `bridge.log` next to the `TermLensSettings.RunStartupSelfTest` entry. Any future regression in the direction-comparison logic surfaces immediately instead of after users notice term lookups going to the wrong column. Verified all 16 cases pass against the v4.19.56 implementation.

---

## [4.19.56] – 2026-05-02

### Fixed (second Codex 5.5 review-pass sweep)

- **Termbase language-direction logic now consistent across all term-write paths.** The v4.19.55 fix only landed in `LoadAllTerms`. Four other call sites still treated "project source ≠ termbase source" as "inverted" (the old broken pattern), so a project termbase whose language pair didn't match the project would still get its sides swapped on add, merge candidates would be searched with swapped columns, and the term editor would pre-fill into the wrong slots. Consolidated the logic into a single `LanguageUtils.CompareTermbaseDirection` helper returning a 4-state enum (NotApplicable, Aligned, Inverted, Unrelated) and routed all five call sites through it: [TermbaseReader.LoadAllTerms](src/Supervertaler.Trados/Core/TermbaseReader.cs), [TermbaseReader.InsertTerm](src/Supervertaler.Trados/Core/TermbaseReader.cs), [QuickAddProjectTermAction](src/Supervertaler.Trados/QuickAddProjectTermAction.cs), [TermMergeChecker](src/Supervertaler.Trados/Core/TermMergeChecker.cs), [TermEntryEditorDialog.IsProjectDirectionInverted](src/Supervertaler.Trados/Controls/TermEntryEditorDialog.cs).
- **SuperSearch active-file replace no longer destroys inline tags.** The pre-fix path read `pair.Target.ToString()`, did a string replace on the flattened text, then `Clear()`-ed the target and re-added a single cloned `IText` – every tag pair, placeholder tag, and formatting span the segment originally contained was wiped. New helper `ReplaceInActiveSegmentPair` walks the existing target's `IText` children depth-first, simulates a per-`IText` replace, and only applies if the result reconstructs the expected flat output. If the search match straddles a tag boundary, the pre-flight detects the mismatch and the helper returns `SpansInlineTags`; the user gets a clear "match spans inline tags – skipped to preserve formatting" status instead of a silent flatten. Both single-Replace and Replace All active-file paths use the helper.
- **SuperSearch disk replace no longer claims success when no real replacement happened.** When an SDLXLIFF target's text is split across multiple `XmlText` siblings separated by inline-tag elements, `node.InnerText` matches across the boundary but the per-text-node replace inside `ReplaceTextInNodes` only changes nodes whose individual value contains the match. Pre-fix, the code would unconditionally `count++` and save the file even if no `XmlText` value actually changed – Replace All would lie about its work. Now the disk path verifies the post-replace `node.InnerText` matches the expected string before counting; segments that span tag boundaries are surfaced as `skipped, X (match spans inline tags)` in the Replace All summary so the user can edit them by hand.

---

## [4.19.55] – 2026-05-02

### Fixed (bug sweep based on a Codex 5.5 review pass)

- **Termbase inversion no longer mis-handles unrelated language pairs.** [TermbaseReader.cs](src/Supervertaler.Trados/Core/TermbaseReader.cs) treated any project-source ≠ termbase-source mismatch as "inverted", which meant a DE-FR termbase loaded into an EN-NL project would get its sides swapped and indexed under languages it has no terms for. Now the inversion check verifies project-source actually matches termbase-target before swapping; entries from termbases whose language pair doesn't match the project on either side are skipped instead of mis-indexed.
- **Batch Translate completion summary now reflects actual writes.** [BatchTranslator.cs](src/Supervertaler.Trados/Core/BatchTranslator.cs) was incrementing `translated` as soon as the LLM returned a parseable response; the Trados write happened later in [AiAssistantViewPart.OnBatchSegmentTranslated](src/Supervertaler.Trados/AiAssistantViewPart.cs) and write failures were logged but not subtracted, so the final "translated N" report could over-count. Added a `WriteSucceeded` flag on the segment-result event args; the handler now runs synchronously on the UI thread and signals failure back, and the translator's counters reflect the real outcome.
- **Plugin no longer deletes `%LocalAppData%\Supervertaler\` on every Trados start.** [UserDataPath.CleanupLegacyFolders](src/Supervertaler.Trados/Settings/UserDataPath.cs) wiped that folder unconditionally on the assumption it was a stale Workbench artifact – ungated by any migration flag, so any user (or future contributor) putting data there would lose it on the next plugin start. The deletion was added on a hunch; it's now removed.
- **API-key fallback parser scans the whole shared settings file.** [LlmClient.ExtractNestedJsonString](src/Supervertaler.Trados/Core/LlmClient.cs) only searched 2000 chars after `"api_keys"`. The shared Workbench settings.json is already 10 KB; a key past the window would silently fail to load. Removed the cap.
- **Build no longer warns about `System.Memory` / `System.Buffers` / `System.Runtime.CompilerServices.Unsafe` version conflicts.** Trados ships older transitive copies than `Microsoft.Data.Sqlite 8.0.0` needs; MSBuild was picking the older "primary" assemblies and hoping for the best. Added explicit `PackageReference` entries to pin the versions SQLite expects, eliminating the MSB3277 warnings and removing the time-bomb risk that a future SQLite update reaches into newer-version-only APIs.

---

## [4.19.54] – 2026-05-02

### Fixed (critical settings-loss regression introduced in v4.19.52)

- **Every saved setting appeared empty.** v4.19.52's "Sidekick Bridge default-true now applies to upgrading users" commit added a second `[OnDeserializing]` callback to `AiSettings` while one already existed. `DataContractJsonSerializer` rejects multiple methods marked with the same callback attribute and throws `InvalidDataContractException` on the first deserialise attempt. `TermLensSettings.Load()`'s broad `catch` swallowed the exception and returned `new TermLensSettings()`, so from the user's perspective every saved setting vanished: termbase disconnected, all termbases reverted to Read = true (because `DisabledTermbaseIds` came back empty), the "share usage statistics?" prompt re-appeared on every Trados start (because `UsageStatisticsAsked` was reset to `false`), QuickLauncher folders rendered as collapsed submenus instead of inline sections (because `QuickLauncherFlatFolders` was empty), QuickLauncher slot shortcuts disappeared (because `QuickLauncherSlots` was empty), and the Settings dialog opened at minimum size every time (because `SettingsFormWidth/Height` were `0`). The settings.json on disk was never corrupted – it simply wasn't being read. Fix: merged the two `[OnDeserializing]` methods into a single callback. Documented the constraint in the source so future contributors don't repeat the mistake.
- **Edit Prompt dialog truncated pastes longer than 32 767 characters.** `TextBox.MaxLength` defaults to `Int16.MaxValue`, which silently drops anything past that point. Patent-sized prompts hit it instantly. Fix: `MaxLength = int.MaxValue` on the prompt-content textbox.
- **Active-prompt tree node label clipped on the right edge** after closing and reopening Settings. Setting `TreeNode.NodeFont` to bold triggers a long-standing WinForms TreeView bug: the node's display rectangle is measured with the regular font and never re-measured for the bold font, so bold characters past the regular-font width get cut off. Fix: dropped the bold; the 📌 emoji + accent colour are already a strong active-prompt marker.

### Added (regression guard)

- **Plugin startup now runs a serialize/deserialize self-test on `TermLensSettings` and writes the result to `bridge.log`.** Round-trips a default `TermLensSettings` through the same `DataContractJsonSerializer` pipeline that `Load`/`Save` use. Any future `[DataContract]` attribute violation – duplicate `[OnDeserializing]`, malformed `[DataMember]`, etc. – surfaces immediately in the plugin log instead of after users notice their settings have disappeared.
- **`TermLensSettings.Load()` no longer silently swallows exceptions.** Unexpected failures are logged with full type, message, and stack trace to `<root>/trados/settings/settings-load-errors.log` so a future regression in this code path is observable.

---

## [4.19.52] – 2026-05-01

### Fixed (Sidekick Bridge default-true behaviour for existing users)

- **`AiSettings.SidekickBridgeEnabled` now correctly defaults to `true` for users with an existing `settings.json`.** Reported by a user upgrading from v4.19.49: the bridge silently skipped startup with `guard: AiSettings.SidekickBridgeEnabled=false – bridge skipped`, despite the property having an initialiser of `= true`. Root cause: `DataContractJsonSerializer` skips constructors during deserialisation, so property initialisers don't run when reading a JSON file written before the property existed – the property defaults to the bool type default (`false`) instead. Fix: added an `[OnDeserializing]` callback `SetDefaultsBeforeDeserialization` that explicitly sets the default to `true` before the JSON parser runs. Any value present in the JSON still overrides, so users who have explicitly disabled the bridge keep that setting.

---

## [4.19.51] – 2026-05-01

### Diagnostics (Sidekick Bridge)

- **Bridge log now ALSO writes to `%TEMP%\Supervertaler-bridge.log`** as a guaranteed-writable fallback. Diagnosing v4.19.50 where the user reported no `bridge.log` even appearing: this rules out the case where `UserDataPath.Root` resolves to a custom location, or where the user data folder isn't writable.
- **Lifecycle tracing extended** – `BridgeLog.Write` now also fires from `AiAssistantViewPart.Initialize()` entry, so we can tell whether the ViewPart is even being instantiated by Trados (ViewParts are lazy – they only initialise when the user activates the panel).
- **First log line of every session now records** the resolved `UserDataPath.Root`, `TradosRuntimeDir`, and `SidekickBridgeFile` paths so we can immediately see where the plugin is looking for its data folder.

---

## [4.19.50] – 2026-05-01

### Diagnostics (Sidekick Bridge)

- **Sidekick Bridge now writes a visible log file** at `~/Supervertaler/trados/runtime/bridge.log` recording every step of startup: which lifecycle gate was hit, whether `HasAssistantAccess` passed, each port-bind attempt and its outcome, the bound port number, and any exceptions with full type and message. The log is truncated on every plugin start so it always reflects the current Trados session. Useful for diagnosing why no `bridge.json` file appears – the most common cause on Windows is `HttpListener` requiring URL ACL registration for non-admin processes, which the log will now make obvious.

---

## [4.19.49] – 2026-05-01

### Added (Sidekick Bridge – plugin half)

- **Localhost HTTP bridge that exposes the active Trados project context to external Supervertaler clients.** Powers the new "Trados-aware mode" in Supervertaler Workbench's floating Sidekick Chat (Workbench side ships in v1.9.411). The bridge listens only on `127.0.0.1` on a random high port, requires a per-session Bearer token, and starts only when the user has Assistant access (paid or trial). Two endpoints are live:
  - `GET /v1/active-context` – returns a snapshot with active segment, surrounding segments, TM matches, termbase hits, and project metadata (same fields the in-Trados Chat already gathers, so answer quality is identical)
  - `POST /v1/insert-translation` – inserts text into the active target segment via the same path as the Apply-To-Target button
- **Discovery via handshake file.** The bridge writes `~/Supervertaler/trados/runtime/bridge.json` with port, token, PID, and start time on startup; deletes it on shutdown. Stale files from hard kills are detected by clients checking PID liveness.
- **Hidden setting** `AiSettings.SidekickBridgeEnabled` (default `true`). No UI checkbox – privacy-conscious users can flip it off by editing `settings.json` directly.

### Notes

- The bridge runs in `AiAssistantViewPart`'s lifecycle: starts in `InitializeFullIfNeeded` (the same gate that already controls in-Trados Chat), stops in `Dispose`.
- All endpoint handlers marshal back to the UI thread before touching `_activeDocument`; the listener itself runs on a dedicated background thread and is concurrency-safe with single-request-at-a-time semantics.
- Workbench Sidekick Chat client + UI ships in v1.9.411 of Supervertaler Workbench; until then the bridge is dormant (no client). The two halves can be released independently.

---

## [4.19.48] – 2026-05-01

### Fixed (AI Settings dialog right-edge clipping)

- **Provider/Model dropdowns and the "Show" button no longer slide under the AutoScroll vertical scrollbar in the AI Settings tab.** When the dialog was resized larger than its default size, the dropdowns and the API-key Show button extended right up to the panel's right edge — but the right edge included the AutoScroll scrollbar's reserved area, so the controls visually clipped against (or hid behind) the scrollbar. Reported by a user. Fix: `LayoutProviderModelRows`, `LayoutApiKeyRow`, and `LayoutTermbasesList` now compute their right margin from `ClientSize.Width` (which excludes the scrollbar) rather than `Width` (which includes it). The dropdowns now keep a clean 16 px gap to the visible right edge regardless of the AutoScroll bar.

---

## [4.19.47] – 2026-05-01

### Fixed (chat status-bar model picker now syncs with AI Settings dialog)

- **Changing the provider/model via the chat status-bar picker now stays in sync with the AI Settings dialog.** Previously, picking a model from the chat picker (e.g. *OpenAI → GPT 5.4 Mini*) would update the chat status bar and the file on disk, but the in-memory copy of `_settings` held by `TermLensEditorViewPart` wasn't refreshed. Opening the Settings dialog from the TermLens panel's gear icon would therefore show stale values (e.g. *OpenRouter / DeepSeek V4 Flash*) that didn't match the chat picker's selection. Reported by a user. The chat picker's `OnModelChangeRequested` now calls a new `TermLensEditorViewPart.NotifyAiSettingsChanged()` after saving — a lightweight refresh that reloads only the `AiSettings` portion of the on-disk settings, without rebuilding termbases.

---

## [4.19.46] – 2026-05-01

### Added (TermLens popup – keyboard-driven metadata, snappier auto-close)

- **Press `I` in the TermLens popup to show extra metadata for the highlighted match.** The same metadata that previously only appeared on mouse hover (definitions, domain, notes, URL, synonyms) can now be revealed entirely from the keyboard. Press `I` again to hide it. The hint at the bottom of the popup has been updated to `← → cycle  ·  Enter insert  ·  E edit  ·  I info  ·  Esc close`. Only affects the floating Ctrl-tap popup; the dockable TermLens pane is unchanged.
- **The TermLens popup now closes automatically on any mouse movement, focus loss, or unrelated keypress.** Previously it would stay open after switching to another application until the user pressed Ctrl or Esc. Now: moving the mouse more than ~4 px, switching to another window, or pressing any key outside the popup's keyboard set (cycle / Enter / E / I / Esc) closes it immediately. Pure modifier presses (Ctrl/Shift/Alt on their own) are ignored so the popup doesn't tear itself down on the keyup that follows the opening Ctrl-tap. The popup is fully keyboard-driven; the cost of this snappier auto-close is that mouse-clicking a chip to insert no longer works (the dockable pane still supports mouse interaction). Only affects the floating Ctrl-tap popup.

---

## [4.19.45] – 2026-05-01

### Added (DeepSeek provider)

- **DeepSeek V4 Pro and DeepSeek V4 Flash added as a dedicated AI provider.** DeepSeek now appears in the AI provider dropdown alongside OpenAI, Claude, Gemini, Mistral, and others. The integration uses DeepSeek's OpenAI-compatible API (`api.deepseek.com/v1`). Enter your DeepSeek API key in **Settings → AI Settings** to use it. V4 Pro is the default (flagship); V4 Flash is listed for high-volume, cost-sensitive workloads.
- **DeepSeek V4 Pro and V4 Flash also available via OpenRouter.** Both models are now included in the OpenRouter model dropdown under the IDs `deepseek/deepseek-v4-pro` and `deepseek/deepseek-v4-flash`, enabling access through an existing OpenRouter API key without a separate DeepSeek account.

---

## [4.19.44] – 2026-04-30

### Changed (Help system – base URL now includes `/help` slug)

- **`HelpSystem.DocsBaseUrl` updated to `https://supervertaler.gitbook.io/help`** to match the renamed GitBook site slug. (GitBook's free plan requires a non-empty slug, so the merged Supervertaler help site uses `/help` as its path; full URLs are now `…/help/trados/<page>` for Trados topics and `…/help/workbench/<page>` for Workbench topics.) Topic constants in `HelpSystem.Topics` are unchanged from v4.19.43 – they continue to start with `trados/`, just appended to the new base. Existing call-sites unaffected.

---

## [4.19.43] – 2026-04-30

### Changed (Help system – unified GitBook site for both products)

- **The Supervertaler GitBook site now hosts documentation for both Supervertaler for Trados and Supervertaler Workbench in a single space**, with content organised under `/trados/` and `/workbench/` URL prefixes. Previously the Workbench shipped its own separate VitePress site at `help.supervertaler.com` which had drifted out of sync with reality; the Trados plugin's own GitBook (`supervertaler.gitbook.io/trados`) was the up-to-date one. Rather than maintain two parallel docs systems on two different toolchains, the Workbench docs were imported alongside the Trados docs in the existing GitBook space. The merged `SUMMARY.md` uses GitBook's `# Part` headings to give each product its own visually-divided sidebar section ("Trados Plugin" and "Workbench") so the two product surfaces don't visually compete.
- **`HelpSystem.cs` topic paths now carry a `trados/` prefix** to match the new file-path layout. Existing call-sites (`HelpSystem.OpenHelp(Topics.TermLensPanel)` etc.) are unchanged – the indirection through the `Topics` constants insulates them from the path rename. The `DocsBaseUrl` constant is now `https://supervertaler.gitbook.io` (root) rather than `…/trados`; the trailing `/trados/` is part of every topic path. **Important: this assumes the GitBook space slug has been renamed from `/trados` to `/` (root) on the GitBook admin side** – if you keep the old slug, every help link will resolve to `…/trados/trados/<page>` (broken). Rename the slug before publishing this version.
- **Documentation site title** to be renamed from "Supervertaler for Trados Help" to "Supervertaler Help" in the GitBook admin (cosmetic; no code change required).

---

## [4.19.42] – 2026-04-30

### Fixed (NullReferenceException when switching projects during batch translation)

- **Switching to a different Trados project while batch translation is running no longer throws "object reference not set to an instance of an object".** Root cause: `OnBatchSegmentTranslated` checked `_activeDocument != null` at the top of the method, but `OnActiveDocumentChanged` could null the field between that check and the subsequent `ProcessSegmentPair` call if the user switched projects at that moment. Fixed by capturing `_activeDocument` in a local variable before the null check so the reference cannot change mid-method. Reported by a user.

---

## [4.19.41] – 2026-04-29

### Fixed (TermLens popup – stayed visible behind editor dialog when E pressed)

- **Pressing E on a TermLens popup match now closes the popup before the term-entry editor opens.** Previously the popup's pixels stayed painted on screen behind the modal editor dialog until the dialog closed. Root cause: `EditCurrentMatch` registered the editor-open inside a `FormClosed` event handler, but the editor's `ShowDialog` blocks the message pump before the area underneath the popup gets repainted, so the popup remained fully visible. Fixed by hiding the popup synchronously, then deferring `Close()` plus `HandleEditCurrentTerm` to the owner form's message loop via `BeginInvoke`. The owner pump processes pending WM_PAINTs for the freshly-uncovered area first, so the editor opens onto a clean screen.

---

## [4.19.40] – 2026-04-29

### Fixed (Alt+Down no longer writes to the project termbase)

- **Alt+Down (Quick-add to write termbases) no longer adds to the project termbase, even when it is also ticked in the Write column.** Previously `QuickAddTermAction` iterated all IDs in `WriteTermbaseIds` without filtering, so any termbase marked as both Write and Project would receive the term from both shortcuts – making Alt+Up and Alt+Down behave identically. The project termbase ID is now skipped in the write-set loop; Alt+Up (project-only) and Alt+Down (write-set-only) are now fully exclusive. The "no write termbases found" warning also notes that the project termbase is excluded so users understand why a write-only configuration might appear empty.

---

## [4.19.39] – 2026-04-29

### Added (Forbidden Term System – active warnings in TermLens)

- **TermLens now actively warns when the source segment contains a target translation that has been marked forbidden in the termbase.** Previously the plugin simply filtered forbidden terms out of all SQL lookups (`WHERE COALESCE(t.forbidden, 0) = 0`), so the user had no idea a term existed at all – equivalent to silent suppression. Now forbidden matches show up in the TermLens chip flow with a strong red background (`#E53935`, hover `#C62828`), white target text, and a strikethrough on the target term (the source is rendered normally – it isn't the source that's forbidden, it's the translation). Hovering the chip shows a `🚫 Forbidden – do not use` tag at the top of the popup. Background colour was deliberately picked to be unmistakably distinct from the soft pink used for project-termbase entries (`#FFE5F0`); a first attempt at salmon (`#FFDAD6`) was too close to pink and got changed in the same release.
- **Term Entry Editor dialog has a "Forbidden term (warn when used in translation)" checkbox** below the Non-translatable checkbox, drawn in dark red so it reads as a warning option. Toggling it persists to `termbase_terms.forbidden` via new `forbidden` parameters on `TermbaseReader.InsertTerm` and `UpdateTerm`. The checkbox correctly reflects DB state when reopening an existing term – required adding `forbidden` to the column list in `GetTermById`, which had been quietly omitting it.
- **Termbase Editor grid now has a 🚫 column** showing the forbidden state for every term at a glance, sized to match the existing NT column. Read-only by design (clicking the cell would otherwise change the visual state without persisting, which was confusing during testing); editing is done through the term-editor dialog. The grid row is patched with the new value when the dialog returns OK so the column updates immediately.

### Fixed (Termbase settings – column sort scrambled checkbox assignments)

- **Sorting the termbase list in Settings → Termbases by Termbase / Terms / Languages no longer corrupts Read / Write / Project / CS checkbox assignments.** Root cause: the save path read checkboxes by row index (`_dgvTermbases.Rows[i]`) and looked up the corresponding termbase via the parallel `_termbases[i]` list, which assumed visual row order matched the underlying list. After a column header click the DataGridView reorders rows in place but the parallel list stays put, so checkbox state for one termbase was being saved against another's ID. Fixed by storing the `TermbaseInfo` (or `MultiTermTermbaseInfo`) reference in each row's `Tag` when the grid is populated, and rewriting the save loop plus all selected-row operations (Open, Distill, Remove, Import, Export) to look up the termbase via `row.Tag` instead of by index. Sort is fully re-enabled.

---

## [4.19.38] – 2026-04-26

### Changed (TermLens popup – feedback when E is pressed on a MultiTerm match)

- **Pressing E on a MultiTerm (green) match in the floating TermLens popup now flashes a short hint in place of the keyboard-shortcut footer instead of silently doing nothing.** The hint reads *"MultiTerm entries are read-only – edit them in Trados → Termbase Viewer."* in muted red, then auto-restores the regular hint after 3.5 seconds. Same E-key behaviour as before for non-MultiTerm matches: editor opens. Decision rationale: Trados Studio 2026 is expected to replace MultiTerm with a SQLite-backed terminology system, so MultiTerm write support isn't worth investing in – the right escape hatch is to send users to Trados's own MultiTerm editor for now.

---

## [4.19.37] – 2026-04-26

### Changed (Term Picker shortcut → Ctrl+Shift+P)

- **The Term Picker dialogue now opens via Ctrl+Shift+P (was Ctrl+Shift+T in v4.19.36).** Ctrl+Shift+T also collides with a Trados Studio binding. Ctrl+Shift+P doesn't collide with anything in either Trados or Supervertaler, and follows the VS Code-style "command palette" convention (P for Picker). Plugin-internal action ID stays `TermLens_TermPicker` so any user-customised remappings survive the rename. Stale "Ctrl+Shift+T" comment in `AddTermAction.cs` (which describes Ctrl+Alt+T, not the picker shortcut at all) fixed as a drive-by.

### Fixed (TermLens popup – grows to fit long segments)

- **The popup no longer truncates chips on long source segments.** Previously the popup was hard-capped at 560 pixels wide regardless of screen size, so a patent-style sentence pushing past 100 characters would show the source segment fine but truncate the target chip text with an ellipsis (e.g. "De onderhavige uitvinding heeft in het algemeen betrekkin…"). Width now scales with the screen – capped at `min(1200 px, screen.Width − 60)` – and the popup shrinks to the actual content width (so short segments still get compact popups). Height cap raised from half-screen to four-fifths so multi-line chip rows aren't cut off either; `AutoScroll` handles anything beyond that. Reported by a user on a long English-Dutch patent sentence.

---

## [4.19.36] – 2026-04-26

### Changed (Term Picker shortcut → Ctrl+Shift+T)

- **The Term Picker dialogue is now opened via Ctrl+Shift+T (was Ctrl+Shift+L since v4.19.28).** Ctrl+Shift+L collides with Trados Studio's own termbase-entry-listing shortcut, so the user-facing combo had to move. Ctrl-tap remains the preferred trigger for the lighter floating TermLens popup; Ctrl+Alt+G remains the popup's keyboard fallback. Plugin-internal action ID is unchanged (`TermLens_TermPicker`) so any user-customised shortcut remappings survive. Docs updated across keyboard-shortcuts.md, termlens.md, term-picker.md, termlens-popup.md and CLAUDE.md.

---

## [4.19.35] – 2026-04-26

### Added (TermLens popup – E opens the term-entry editor)

- **Pressing E on the highlighted match opens the term-entry editor.** Mirrors the docked panel's right-click "Edit Term…" menu but on a single keystroke, so the editor is reachable without leaving the keyboard. The popup snapshots the entry data, closes itself, then routes through the same `OnTermEditRequested` handler the docked panel uses – so the editor sees the same single-/multi-entry flow it always has, including the multi-termbase editing case for entries that exist in more than one termbase. Read-only MultiTerm entries are skipped (same rule as the right-click menu).

### Changed (TermLens popup – keyboard-only, fewer surprises)

- **Removed the "?" help button.** v4.19.33's button never worked reliably (off-screen in v4.19.33, focus-stealing the popup itself in v4.19.34) and was at odds with the popup's keyboard-only design anyway. F1 plumbing removed too – Trados Studio's own F1 binding takes precedence over application-level message filters and beating it would need a Win32 low-level keyboard hook, more complexity than this is worth. Help for the popup is one click away on the [GitBook docs site](https://supervertaler.gitbook.io/trados/features/termlens/termlens-popup).
- **Popup no longer auto-dismisses on focus loss.** v4.19.34 closed the popup whenever focus moved off it, which meant the chip-hover tooltip (the metadata `TermPopup`) silently killed the popup the moment the mouse passed over a chip – reported by a user moving the mouse toward the now-removed "?" button. Closing is now exclusively via Esc, Enter (after insert), Ctrl-tap (toggle), Ctrl+Alt+G (toggle), or clicking a chip (insert).
- Footer hint updated to `← → cycle  ·  Enter insert  ·  E edit  ·  Esc close`.

---

## [4.19.34] – 2026-04-26

### Fixed (TermLens popup – help affordances)

- **The "?" help button is now actually visible.** v4.19.33 added a manually-positioned Label with `Anchor = Top | Right` whose initial Location was computed against the form's pre-resize 560 px width, then `ResizeToContent` shrank the form before the anchor distance was committed – the button silently ended up off-screen to the right. Replaced with a docked bottom-bar Panel that contains the keyboard hint (`Dock = Fill`) and the "?" button (`Dock = Right`); no manual coordinates, no anchor math.
- **F1 inside the popup now opens the TermLens popup help page instead of the Trados Studio help site.** Trados has an Application-level message filter for F1 that fires before `Form.ProcessCmdKey`, so v4.19.33's `case Keys.F1` handler in `ProcessCmdKey` was never reached. Added a `PopupF1Filter : IMessageFilter` (same pattern as `CtrlTapFilter`) registered once on plugin startup that consumes F1 key-down events when the popup is open and focused, before Trados sees them. The `ProcessCmdKey` handler stays as a fallback in case the application filter chain ever misses.

---

## [4.19.33] – 2026-04-26

### Added (TermLens popup – help affordances)

- **Help is now reachable from the popup itself.** A small "?" button in the top-right corner of the popup opens the dedicated help page (https://supervertaler.gitbook.io/trados/features/termlens/termlens-popup) in the default browser, and F1 does the same – keeping parity with the existing dialogs (Add Term, Settings) that have both a visible question-mark affordance and a keyboard equivalent. The popup stays open so the user can read the docs and come back to picking a term.

### Docs

- New help page **TermLens popup** under TermLens describing when to use the popup vs the Term Picker dialogue, the keyboard model, and a side-by-side comparison.
- Keyboard-shortcut reference, TermLens overview, and Term Picker page updated to reflect the v4.19.28 shortcut swap (Ctrl-tap → popup, Ctrl+Shift+L → Term Picker dialogue, Ctrl+Alt+G now the popup's fallback). Stale references in CLAUDE.md cleaned up.

---

## [4.19.32] – 2026-04-26

### Fixed (TermLens – handler leak across document close/reopen cycles)

- **Term-insert / right-click actions in the docked TermLens panel no longer fan out to dead view-part instances.** `TermLensEditorViewPart.Initialize` subscribed seven event handlers to static singletons (`_control.Value` for `TermInsertRequested` / `TermEditRequested` / `TermDeleteRequested` / `TermNonTranslatableToggled` / `FontSizeChanged`, `_mainPanel.Value.SettingsRequested`, `LicenseManager.Instance.LicenseStateChanged`) but `Dispose` only unsubscribed the per-document handlers – so each time Trados tore down and recreated the view-part (document close/reopen, project switch, layout change), the new instance added another set of subscribers while the old set stayed reachable through the singleton's invocation lists. The dead instances continued to dispatch, so a single chip click in the docked panel could fire `Selection.Target.Replace` two, three, or N times depending on how many times the view-part had been recreated in the session – root cause behind the v4.19.30 popup-double-insert bug, which the popup fix sidestepped by skipping the bubble entirely without addressing the leak itself. `Dispose` now unsubscribes all seven (using `Lazy<T>.IsValueCreated` checks to avoid forcing the singletons into existence purely to call `-=`), and the `LicenseStateChanged` lambda was extracted to a named `OnLicenseStateChanged` method so it can be unsubscribed at all.

---

## [4.19.31] – 2026-04-26

### Fixed (TermLens popup – focus returns to target after insert)

- **After picking a term from the floating TermLens popup, focus now returns to the target cell.** Previously the popup was a borderless modeless form opened with `Show()` followed by `popup.Activate()`, which stole focus from the Trados editor on open and gave Windows no owner relationship to use when assigning focus on close – so the target cell was left without keyboard focus, leaving the user with no caret to keep typing in. The popup is now opened via `Show(ownerForm)` where `ownerForm` is the WinForms host of the docked TermLens panel (i.e. the Trados main window from a Win32 perspective), the same pattern the existing Term Picker dialog uses with `ShowDialog(parent)`. The insertion callback also explicitly calls `ownerForm.Activate()` immediately before `Selection.Target.Replace` runs, so the editor is the foreground window when Trados processes the replace and the target cell is re-focused.

---

## [4.19.30] – 2026-04-25

### Fixed (TermLens popup – term inserted twice)

- **Selecting a term from the floating TermLens popup no longer inserts it twice.** Reported by a user – picking "tank vessel" produced "tank vesseltank vessel" in the target. Root cause: the popup's chips bubbled their `TermInsertRequested` event to the docked panel's existing `OnTermInsertRequested` handler (insertion #1) AND the popup also wired a close-on-insert handler that ran in the same chain. When combined with a separate handler-leak in `TermLensEditorViewPart.Initialize` (the `OnTermInsertRequested` subscription accumulates across view-part lifecycles because `Dispose` doesn't unsubscribe), the bubble path could fire the insertion two or more times per chip click. `TermLensControl.BuildSegmentBlocks` now takes an opt-out `wireInsertBubble` parameter so the popup can request blocks with no bubble wiring; the popup wires its own single handler that funnels both mouse-click and keyboard Enter through one `RequestInsert(oneBased)` method, which uses an `_pendingInsertOneBased` guard to make the insertion exactly-once and defers it to `FormClosed` so focus has returned to the Trados editor before `Selection.Target.Replace` runs. The handler-leak in `TermLensEditorViewPart` itself is untouched in this release – see follow-up issue. The docked panel's chip-click flow is unchanged (still uses the bubble).

---

## [4.19.29] – 2026-04-25

### Changed (TermLens popup – Ctrl-tap is now a toggle)

- **Ctrl-tap now toggles the floating TermLens popup open / closed instead of cycling.** Previously the second Ctrl-tap advanced the highlighted "current" match, on the theory that it kept the user on a single key for both opening and stepping. In practice closing was the more common follow-up – once the right term was visible the user wanted out – so the second press now closes the popup. Cycling stays on the keyboard via Right / Down / Tab (next) and Left / Up / Shift+Tab (previous).

---

## [4.19.28] – 2026-04-25

### Changed (TermLens shortcuts swapped)

- **Ctrl-tap now opens the floating TermLens popup; the Term Picker dialog moves to Ctrl+Shift+L.** After v4.19.27 the new floating popup proved fast enough to deserve the most ergonomic shortcut, so it inherits Ctrl-tap from the Term Picker dialog. The picker dialog (list-based UI) remains available on Ctrl+Shift+L for users who prefer that style. Ctrl+Alt+G is now the explicit-key fallback for the popup (was the picker's fallback).

---

## [4.19.27] – 2026-04-25

### Added (TermLens popup – keyboard-only term insertion)

- **New Ctrl+Shift+L shortcut opens a borderless TermLens popup** that mirrors the docked TermLens panel for the active segment. Aimed at small-screen / laptop workflows where keeping the docked panel always-visible costs too much vertical space, and at translators who want to insert terms without ever reaching for the mouse. The popup reuses the docked panel's already-loaded matcher and termbase index (no reload), renders the same `TermBlock` chips with the same colour scheme, and routes mouse-click insertions through the existing TermLens insertion flow. One match is highlighted as "current" with an amber ring around the source word; arrow keys (Right/Down/Tab and Left/Up/Shift+Tab) cycle the highlight, Enter inserts the current match into the target segment and closes the popup, Escape and click-outside close without inserting. Re-pressing Ctrl+Shift+L while the popup is open advances the highlight instead of stacking a second popup, so the user can stay on a single shortcut while skimming through matches. Initial highlight is always match #1 for predictability. The popup positions itself near the cursor and clamps to the working area; long segments wrap and scroll inside a capped popup width.

---

## [4.19.26] – 2026-04-25

### Fixed (merge prompt – reversed-direction termbases)

- **Quick-Add (Alt+Down / context menu / Alt+Up) now offers the merge prompt for partial duplicates in reverse-direction termbases.** Regression introduced in v4.19.13 when the per-termbase swap was moved inside `InsertTermBatch`: `TermMergeChecker.FindMergeMatches` was left searching with project-direction text, so for any termbase declared in the inverse direction of the project (e.g. an EN→NL termbase used in an NL→EN project), the SQL compared the DB English column against the new Dutch text and vice versa – every potential merge candidate was silently missed and a near-duplicate entry was created instead. `FindMergeMatches` now takes an optional `projectSourceLang` and applies the same per-termbase column swap that `InsertTermBatch` does internally; each match carries a new `MergeMatch.TermbaseInverted` flag so the dialogue and the `AddSynonym` calls can land the new text in the correct source-language vs target-language slot of the existing entry. Reported by a user who noticed Quick-Adding "even more preferably between" against an EN→NL PATENTS termbase that already held a "still more preferably between" entry for the same Dutch source – the merge prompt no longer fired and a separate row was created. `MergePromptDialog` was also reworked to expect callers to pass new-term text in project direction (it previously expected termbase-storage direction, which became inconsistent across callers after v4.19.13) and to use per-match `TermbaseInverted` for the existing-entry display, so the dialogue's "You are adding…" line and synonym question now read in project direction even when the termbase is reversed. `QuickAddProjectTermAction` (Alt+Up, single project termbase) was already pre-swapping at the top of the method – its merge flow worked but is now aligned with the new convention; the swap moved local to the `InsertTerm` call.

---

## [4.19.25] – 2026-04-23

### Fixed (term-entry editor – critical, reversed-direction termbases)

- **The Add and Edit dialogues no longer silently corrupt entries in termbases whose declared direction is the inverse of the project's.** The v4.19.22 fix normalised the dialogue's labels to termbase-declared direction (e.g. English on the left when the termbase is en→nl) but did not reconcile the values being loaded into those fields. The result: in a project translating Dutch → English using a termbase declared English → Dutch, adding or editing a term silently wrote the Dutch text into the `source_term` (English) column and the English text into the `target_term` (Dutch) column. The corrupted entry then stopped matching in TermLens because the matcher's index ended up keyed on the wrong-language string. The Edit path now re-reads the entry from the database by ID before populating the dialogue (via `TermbaseReader.GetTermById`), and the Add path detects when the termbase's declared direction is the inverse of the project's (using the same `LanguageUtils.ShortenLanguageName` + primary-language `StartsWith` comparison `TermbaseReader` uses at load time) and swaps the pre-filled values internally so they land in the correctly-labelled fields and are saved into the correct DB columns. Forward-only fix – pre-existing reversed entries remain reversed in the DB and need to be repaired manually via the "Reverse source/target" right-click action in the Termbase Editor or via `tools/repair_termbase_directions.py` for batch cleanup.

### Changed

- **`build.sh` deploy target moved from Roaming to LocalAppData.** Matches the install scope end-users get from the recommended "This computer for me only" option in Trados Plugin Installer, eliminating the dual-scope state Michael's dev machine used to live in (AppStore install in Local, build.sh install in Roaming). `build.sh` now also removes any leftover spaced-name `.sdlplugin` and Unpacked folder from Roaming so `HandlePendingUpdate` doesn't pick the stale copy.

---

## [4.19.24] – 2026-04-23

### Fixed (update channel consolidation – architectural change)

- **The in-plugin updater now reads exclusively from the RWS App Store catalogue.** Previously, the plugin polled GitHub releases in parallel with Studio's own App Store install tracking, creating a second source of truth for "what's installed". The two channels could disagree: the in-plugin GitHub updater would download an unsigned `.sdlplugin` that Studio flagged with the "Unsigned Trados Studio Plug-in Found" dialogue, and on the next restart the App Store install tracking would silently roll back to its last-approved version – occasionally leaving the user stuck on the previous version after a restart and still seeing the unsigned-plugin warning. With the consolidation, every installed build is RWS-signed, the two channels can no longer fight, and the unsigned-plugin dialogue cannot appear as a result of a plugin update. GitHub releases continue to host release notes as documentation; binary `.sdlplugin` assets are no longer attached.

### Fixed (orphan duplicate installs from non-Roaming install scope)

- **The updater now detects the user's original install scope and writes updates back to the same scope.** Previously, the one-click "Install Update" action always wrote the downloaded `.sdlplugin` to `%AppData%\…\Plugins\Packages\` (Roaming) regardless of where Trados Plugin Installer had originally placed the plugin – if the user had chosen "This computer for me only" during install (which lands in `%LocalAppData%\…\Plugins\`) or "This computer for all users" (`%ProgramData%\…\Plugins\`), the update created an orphan duplicate: the new version in Roaming and the old version still sitting in the original scope, with Studio picking whichever looked newer. The updater now scans all three plugin scopes via two new helpers in `UpdateChecker` (`FindCurrentInstallScopePackagesDir` / `FindCurrentInstallScopeUnpackedDir`), finds the one containing the existing install, and overwrites in place – no more stale copies left behind.

### Changed

- App Store catalogue responses are cached locally for 24 hours (at `%LocalAppData%\Supervertaler.Trados\appstore_cache.json`) to avoid unnecessary API traffic. The catalogue API does not expose `ETag` / `Last-Modified` headers, so conditional `If-None-Match` requests aren't possible – a local TTL is the right shape.
- Update-check failures (network errors, parse errors) are now handled silently – they never block Studio startup or surface error dialogues.
- Documentation (`docs/installation.md`) rewritten: Download and Updating sections now describe the AppStore-only flow; the "old version still showing after update" troubleshooting section is annotated as legacy / manual-surgery only since v4.19.24's updater is scope-aware.

---

## [4.19.23] – 2026-04-21

### Added

- **Rename custom OpenAI-compatible endpoints in AI Settings.** Previously, custom endpoints were auto-named "New Endpoint 1", "New Endpoint 2", etc., with no in-app way to give them meaningful labels (the `Name` field existed on the profile object but had no UI). A new pencil button sits next to the existing **+** / **−** buttons on the Custom OpenAI provider panel and opens a small modal dialog: pre-fills the current name, validates live (OK button disabled when empty, unchanged, or colliding with another profile's name – case-insensitive), and updates the combo in place without firing `SelectedIndexChanged` (so unsaved endpoint / model / API-key edits aren't wiped during rename). The rename persists through the existing save path – `SaveCurrentCustomProfile` rebuilds `CustomOpenAiProfiles` from combo items and `SelectedCustomProfileName` is reconciled to the current selection, so a rename survives Settings-dialog OK and Trados restart. Feature request from a user.

---

## [4.19.22] – 2026-04-21

### Fixed (termbase direction handling – architectural overhaul)

- **Term matching now uses the termbase's declared direction, not the per-entry `source_lang` / `target_lang` columns.** Legacy write-path bugs (pre-v4.19.13) left many termbases with a mix of entries whose per-entry language tags didn't agree with the termbase declaration – and `TermbaseReader.LoadAllTerms` trusted those per-entry tags when deciding whether to invert for reverse-direction projects. The result was entries silently not matching even though their text was correct. `LoadAllTerms` now pulls `source_lang` / `target_lang` from the canonical `termbases` table for the inversion decision, making matching resilient to corrupted per-entry tags. Root-cause fix for the "my termbase entry exists but TermLens says no match" class of bug.
- **The term entry editor now always shows fields in termbase-declared direction.** Previously `TermEntryEditorDialog` inverted field layout when the project direction differed from the termbase's, and this happened only sometimes depending on which entry point opened it – the Termbase Editor grid right-click did not invert, but the TermLens chip right-click did. The inconsistency made reverse-direction termbases especially confusing. Fields, labels, and save flow are now always termbase-direction, matching the Termbase Editor grid. The `projectSourceLang` parameter is retained for source compatibility but ignored.

### Added

- **"Reverse source/target" right-click action in the Termbase Editor.** Lets you fix one or many reversed-direction entries at once. Menu label dynamically shows the count when multiple rows are selected (e.g. *"Reverse source/target (12 entries)"*). The operation swaps `source_term` ↔ `target_term`, `source_lang` ↔ `target_lang`, `source_abbreviation` ↔ `target_abbreviation`, and flips every linked synonym's language tag (`'source'` ↔ `'target'`). All in one SQL transaction, so partial failure leaves the DB untouched. Right-click on the grid now also preserves multi-row selection when the clicked row was already selected – matches standard Windows list behaviour.
- **New `TermbaseReader.ReverseTermDirection(dbPath, termIds)`** helper backing the above. Takes a list of term IDs, opens a single ReadWrite connection, does the swaps atomically.

### Tools

- **New `tools/repair_termbase_directions.py`** – stopword-heuristic repair that scans a Supervertaler DB, classifies each legacy-bad entry as tag-only / text-swap / ambiguous, and applies the safe fixes. Conservative by design; skips short single-word entries where language can't be determined confidently.
- **New `tools/ai_repair_termbase_directions.py`** – LLM-powered repair using Claude Sonnet 4.6 for the entries the heuristic can't handle. Reads the Claude API key straight from the plugin's `settings.json`, batches 50 pairs per call, caches responses locally so reruns are free. Operates on one termbase at a time. See `tools/README.md` for caveats and usage.

---

## [4.19.20] – 2026-04-20

### Fixed

- **"Set as active prompt for this project" now reflects in the Batch Translate dropdown immediately and works for any prompt, regardless of which entry point opened the Settings dialog.** Three issues were stacked on top of each other:
  1. **No live sync** – the Batch Translate dropdown only refreshed when the Settings dialog closed, so right-clicking a prompt → "Set as active prompt for this project" had no visible effect until the user clicked OK. `PromptManagerPanel` now raises a static `ActivePromptChangedGlobal` event; `AiAssistantViewPart` subscribes once in `Initialize` and live-refreshes the Batch dropdown with the pending active path while the dialog is still open. Cancelling the dialog still snaps back to the persisted state via the existing unconditional refresh on close.
  2. **Category filter hid the checkmark** – even after clicking OK, prompts whose `Category` was not `Translate` (or `Proofread`, in proofread mode) were silently filtered out of the dropdown, so the checkmark had nowhere to land. The category filter now makes an exception for the active prompt – it always appears in the dropdown regardless of folder, so marking any prompt as active works uniformly. Path-separator normalisation was also extended to the selection match (previously only the active-marker check handled `/` vs `\`), eliminating a subtle mismatch when the stored path used forward slashes.
  3. **Entry-point-dependent wiring** – an earlier iteration subscribed to the event only when the Settings dialog was opened from the AI Assistant gear. Opening Settings from the TermLens gear (which instantiates `TermLensSettingsForm` in a separate code path) left the event with zero subscribers, making the live-sync appear intermittent. The static event used in the final fix is wired once at plugin init, so it catches the change regardless of which code path created the form.

---

## [4.19.15] – 2026-04-20

### Fixed

- **New prompts created at the tree root in the Prompt Manager are now visible in the Batch Translate dropdown.** Previously, creating a new prompt without first selecting a folder left its `Category` empty. The Batch Translate dropdown filters strictly by `Category == "Translate"` (or `"Proofread"`), so root-level prompts were silently excluded – marking one as the active prompt for the project had no visible effect in the Batch panel (the dropdown stayed on `(None – default)`). New prompts now default to the `Translate` category when no folder is selected, so they appear in the Batch dropdown and respect "Set as active prompt for this project" immediately. Existing root-level prompts with empty categories still need to be moved into the `Translate` folder (or re-categorised in the editor) to become visible.

---

## [4.19.14] – 2026-04-17

### Added

- **Claude Opus 4.7 support.** Anthropic's new flagship model (released 2026-04-16) is now selectable in AI Settings under the Claude provider and via the OpenRouter gateway (`anthropic/claude-opus-4.7`). Opus 4.7 has a 1M-token context window, 128k max output, and is Anthropic's most capable generally available model. Pricing is $5 / input MTok, $25 / output MTok – the same as Opus 4.6. Sonnet 4.6 remains the recommended default for most translation work; reach for Opus 4.7 when you need top-tier reasoning or long-context jobs. See [What's new in Claude Opus 4.7](https://platform.claude.com/docs/en/about-claude/models/whats-new-claude-4-7) for details.

### Fixed (cost estimates)

- **Corrected stale pricing for Claude Opus 4.6 and Haiku 4.5.** The internal pricing table had Opus 4.6 at the pre-4.6 rate of $15 / $75 per MTok – Anthropic dropped Opus pricing to $5 / $25 with the 4.6 release. Haiku 4.5 was listed at $0.80 / $4.00, corrected to the current $1.00 / $5.00. Cost estimates shown in the AI Assistant and Batch Translate were over-stating Opus usage and under-stating Haiku usage – now accurate.

### Note on Opus 4.7 tokenizer

- Claude Opus 4.7 uses a new tokenizer that can use **~1.0×–1.35× more tokens** for the same text compared to earlier models. Our pre-send cost estimates (`chars / 4` heuristic) will under-estimate Opus 4.7 costs by a similar margin. Actual billing is based on Anthropic's token counts.

---

## [4.19.13] – 2026-04-17

### Fixed (critical – termbase data integrity)

- **Wrong-direction entries were silently being written to write termbases with mixed declared directions.** `QuickAddTermAction` (Alt+Down / Ctrl+Alt+Shift+T) made a single swap decision using only the **first** write termbase's declared direction, then applied that same swap to **every** termbase in the batch. If you had two or more write termbases with different declared directions (e.g. one EN→NL and one NL→EN), the text for roughly half of them ended up in the wrong source/target columns. The rot compounded on every Alt+Down press. Existing already-wrong entries remain in the DB – clean them up row-by-row with the existing ⇄ reverse button in the Term Entry Editor. Prevention is now in place:
  - `TermbaseReader.InsertTermBatch` takes a new optional `projectSourceLang` parameter and makes a **per-termbase** swap decision inside its loop. Each termbase now stores the text in its own declared direction regardless of the mix of directions in the batch.
  - `QuickAddTermAction` no longer does a global pre-swap; it always passes the project-direction texts through and lets `InsertTermBatch` handle the per-termbase decision.
- **Duplicate check now detects reverse-direction matches.** `InsertTerm` and `InsertTermBatch` used to consider a term "new" if the same pair existed but was stored in the opposite direction (source↔target). That meant users whose termbases already contained wrong-direction entries from the bug above would end up with the concept stored **twice**, once in each direction, on re-add. The check now matches either `(source=A ∧ target=B)` or `(source=B ∧ target=A)` in the same termbase and rejects both as duplicates.

---

## [4.19.12] – 2026-04-16

### Fixed

- **Ctrl+Q (QuickLauncher) now reliably surfaces the Supervertaler Assistant panel.** Previously, selecting a QuickLauncher prompt would submit the message to the chat backend but the dockable Assistant panel could remain auto-hidden, unpinned, or buried behind another dock tab – so the user saw nothing happen. The Chat tab inside the panel was already being selected correctly by `SubmitMessage`; the missing step was activating the panel itself. `RunQuickLauncherPrompt` now calls `instance.Activate()` before submitting, matching the SuperSearch action pattern. Affects both the context-menu QuickLauncher (Ctrl+Q) and the Ctrl+Alt+digit slot shortcuts, since both funnel through the same entry point.

---

## [4.19.11] – 2026-04-15

### Fixed

- **Distill and Process Inbox now ignore Obsidian plugin sidecar files (`.edtz`)** in the SuperMemory inbox. Previously, a `.edtz` sidecar sitting next to a Markdown note would be handed to Distill as "raw material", and Distill would fail with *"Unsupported file format: .edtz"*. The inbox scanners in both Distill and Process Inbox now filter these out silently – they are editor metadata, not knowledge content. Other bank-enumeration code paths (article counts, future bank-management features) share the same filter via a new `MemoryBankReader.IsIgnoredSidecar` helper.

---

## [4.19.10] – 2026-04-13

### Changed

- **British English consistency** – all user-facing licensing messages and UI labels now use "licence" instead of "license" (e.g., "Please enter a licence key", "Licence activated successfully", Settings → Licence), matching the project's British English convention.

### Added

- **Auto-indexing after Process Inbox and Health Check.** The `05_INDICES/` folder is now automatically populated with three master index files after every successful Process Inbox or Health Check run:
  - `master-terminology.md` – a flat table of all source → target term decisions across the bank, with domain, client, confidence, and status columns.
  - `client-summary.md` – one section per client with their `tldr:` or first paragraph.
  - `domain-summary.md` – one section per domain with their `tldr:` or first paragraph.
  These indices are built by scanning frontmatter directly – no LLM call needed, completes in under a second. They make the bank browsable in Obsidian and provide the scan source for future two-pass context loading ([#22](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/22), enhancement #3).

---

## [4.19.9] – 2026-04-13

### Added

- **Enriched frontmatter schema for SuperMemory articles.** Distill, Process Inbox, and Health Check now produce articles with richer YAML frontmatter: `type`, `domain`, `client`, `language_pair`, `confidence` (high/medium/low), `sources` (original filenames for traceability), `tldr` (one-sentence summary for fast scanning), `created`, and `updated`. This lays the groundwork for confidence-based context loading, staleness detection, and the universal bank vision ([#22](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/22)).
- **Confidence scoring in Distill and Process Inbox.** Every article generated by Distill or Process Inbox now carries a `confidence:` field (high/medium/low) based on the authority and quality of the source material. Low-confidence articles are flagged for human review.
- **Source traceability.** Every article now carries a `sources:` frontmatter field listing the original files it was derived from. Terminology articles always quote exact source and target terms verbatim.

### Changed

- **Health Check gains confidence review and frontmatter backfill.** Health Check now identifies articles with `confidence: low` and reports them as needing verification. It also detects articles missing the enriched frontmatter fields and auto-fills them by inferring values from the article content and folder location.
- **Health Check staleness detection improved.** Articles not updated in more than 4 weeks with newer siblings on related topics are now flagged. When a newer article contradicts an older one, the older article is flagged as potentially superseded.
- **Expandable truncated chat responses.** Long AI assistant responses are now truncated at 3,000 characters (up from 1,500) with a clickable "Show full response" link at the bottom of the bubble. Clicking the link expands the bubble to show the complete response inline – no more right-clicking to copy. Most regular chat responses (translation assessments, terminology advice, etc.) now display in full without truncation.

---

## [4.19.8] – 2026-04-13

### Added

- **"Save to memory bank" from chat.** Right-click any assistant message in the AI Assistant chat and select "Save to memory bank" to save the Q&A pair (your question and the AI's response) as an inbox note in the active memory bank. Run Process Inbox afterwards to compile it into the knowledge base. This closes the feedback loop described in [#22](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/22) – useful answers no longer vanish into chat history.

---

## [4.19.7] – 2026-04-13

### Fixed

- **TSV reimport failed for exported files.** The TSV export wrote language display names as column headers (e.g., "Dutch (BE)", "English (US)") which the importer could not reliably match back to language codes. Export now uses fixed "Source" / "Target" headers, matching the Workbench format. Language names in headers from external TSV files are still recognised as a fallback.

### Changed

- **TSV import confirmation dialog.** Before importing, a confirmation dialog now shows the filename, row count, target termbase name, and language pair – giving you a chance to catch mistakes before anything is written.
- **TSV import progress dialog.** Large imports (e.g., 2,700+ terms) now show a progress bar with a running count instead of freezing the UI with a wait cursor.

---

## [4.19.6] – 2026-04-13

### Changed

- **Distill button now offers a choice.** Clicking Distill shows a small dialog with two options: "Distill inbox (N files)" to automatically distill all non-Markdown files sitting in the active memory bank's `00_INBOX/`, or "Select files…" to open the file picker as before. The inbox option is disabled when the inbox has no distillable files. File names are listed in the dialog so you can see what will be processed.

---

## [4.19.5] – 2026-04-13

### Changed

- **Term Picker shortcut – Ctrl tap (memoQ-style).** Pressing and releasing Ctrl alone (without any other key) now opens the Term Picker dialogue, matching memoQ's behaviour. A quick tap of Ctrl triggers it; holding Ctrl for a combo (Ctrl+C, Ctrl+V, etc.) works normally. Maximum hold duration is 400 ms to prevent accidental triggers. The previous shortcut Ctrl+Alt+G is kept as a fallback.

### Fixed

- **Memory bank dropdown empty after licence reactivation.** Deactivating and reactivating a licence mid-session (or starting Trados unlicensed, then activating) left the memory bank dropdown empty and all AI Assistant event handlers unwired. The full post-licence initialisation now runs automatically when the licence state changes to active, without requiring a Trados restart.

---

## [4.19.4] – 2026-04-11

### Fixed

- **Clipboard Mode now respects the Limit spinner.** Previously, clicking "Copy to Clipboard" ignored the Limit value and always copied every matching segment. Now it applies the same limit as the API batch path – set Limit to 20 to copy only the first 20 segments.

---

## [4.19.3] – 2026-04-11

### Changed

- **SuperMemory help URL updated.** The GitBook page for SuperMemory moved from `/features/ai-assistant/memory-banks` to `/features/ai-assistant/super-memory`. In-plugin help links (the `?` button on SuperMemory features) updated to match.

---

## [4.19.2] – 2026-04-11

### Added

- **Crash-recovery TMX backup.** Batch Translate now writes every translated segment to a TMX file as it arrives from the AI. If Trados crashes mid-run, the backup contains everything received so far – import it into any TM to recover. A pre-checked **"Auto-backup translations to TMX"** checkbox and an **"Open folder…"** link appear below the Translate button; untick to disable for a run. Files are saved to `Supervertaler\trados\batch_backups\` and are standard TMX 1.4 – compatible with Trados, memoQ, Wordfast, and any other CAT tool that imports TMX.

### Fixed

- **Reports tab now shows entries newest-first.** Prompt log cards are sorted by timestamp during relayout, so the most recent batch or chat call always appears at the top regardless of the order entries arrived.
- **Termbase AI filtering – word-boundary matching.** `FilterRelevantTerms` now uses `\b` word-boundary regex instead of plain substring matching, so a term like "claim" no longer incorrectly matches "Disclaimer" or "Proclaim".
- **Termbase AI filtering – initialisation flag.** A bug caused `DisabledAiTermbaseIds` to be reset on every settings save, so per-project termbase exclusions were not preserved correctly across saves. Fixed.

---

## [4.19.1] – 2026-04-10

### Fixed

- **Long AI responses are now truncated in the chat.** Assistant messages longer than 1,500 characters are truncated to the first 1,000 characters in the chat bubble, with a note: *"Response truncated (N more characters). Right-click → Copy for the full text."* The full original text is always available via right-click → Copy and is preserved in the chat history. This is what finally makes long chat sessions usable – a single Health Check report can be 25,000+ characters (15 screens tall), and rendering that inline broke the scroll math in multiple subtle ways. With truncation, every bubble fits comfortably in the viewport.
- **Chat scroll rewrite.** Disabled `FlowLayoutPanel.AutoSize` and switched to manual height management, fixing the long-standing "messages disappear into ghost white space" bug on long chat histories. Users no longer need to click **Clear** before every chat message.
- **Health Check always shows a completion summary.** When the AI reports issues without auto-fixing any files (no `### FILE:` markers in the response), the chat now shows a clear *"Health Check complete – no changes applied"* message instead of leaving the user guessing whether the operation finished.
- **User-initiated actions re-engage auto-scroll.** Clicking Send, Health Check, Process Inbox, Distill, or switching memory banks now resets the "user scrolled up" flag so the progress bubble and response land in view – even if the user had been scrolled up reading history.

### Changed

- **SuperMemory is off by default for new installations.** `IncludeSuperMemoryContext` and `IncludeSuperMemoryInAutoPrompt` now default to `false`. Most translators should start with the simpler workflow (TermLens glossaries + AI context awareness) and opt into SuperMemory once they have a populated bank. Existing users are unaffected – their saved `true` values are preserved.
- **Quick Add dialog redesigned (Ctrl+Alt+M).** The field labels are now language-aware: "Source term (Dutch):" and "Target term (English):" instead of the old "Term / pattern (what's wrong):" and "Correction (correct English form):". New "Save as raw note" checkbox lets you dump ambiguous or context-dependent knowledge into `00_INBOX/` for the AI to compile via Process Inbox, instead of forcing it into a rigid source→target pair. Structured articles now use `→` in their filenames instead of `vs`.
- **SuperMemory page renamed to "SuperMemory" in GitBook.** The help page title, sidebar label, and all cross-references now consistently use "SuperMemory" as the heading (was "Memory banks"). URL is preserved at `/memory-banks` for backwards compatibility.

---

## [4.19.0] – 2026-04-09

Headline release: **SuperMemory multi-bank support**. You can now keep several self-contained translation knowledge bases side by side (one per client, per domain, per language pair) and switch between them in a single click from the Supervertaler Assistant toolbar. The two-level terminology ("SuperMemory" = system, "memory bank" = container) is now reflected consistently across the UI, docs, and help system.

### Added – SuperMemory multi-bank support

- **Memory Bank dropdown** in the Supervertaler Assistant toolbar. Lists every bank under your user-data folder with the active bank pre-selected. Switching is immediate: the next chat turn, batch translation and Process Inbox run all read from the new bank, and chat history is preserved across the switch. The active bank is persisted in settings and survives Trados restarts.
- **Create new banks from the toolbar** – pick `+ New memory bank…` at the bottom of the dropdown, enter a short name (lowercase letters, digits, hyphens or underscores) with a live preview of the final folder name, and the bank is created on disk with the full seven-folder skeleton and activated in one click. No need to touch Settings or File Explorer.
- **Bundled template files** – every new bank ships with the canonical `compile.md`, `lint.md`, `query.md`, `translate_with_kb.md` and "Claude dump" helper templates in `06_TEMPLATES/`, so Process Inbox and Health Check work against a fresh bank out of the box.
- **Heal-on-activation prompt** – if you activate an older bank that is missing its canonical template files (a bank created before this release, or one where you deleted a template by accident), the plugin shows a one-time "Missing memory bank templates" dialog offering to restore them from the built-in defaults. Existing template files are never overwritten.
- **Shared with the Python Supervertaler Assistant** – memory banks live in the shared Supervertaler user-data folder with byte-identical layout, so a bank created in Trados works unchanged in the standalone app and vice versa.
- **Legacy single-bank migration** – the first time you open a multi-bank-aware build against an older single-bank installation, the plugin offers to move your existing `memory-bank/` or `supermemory/` folder into the new `memory-banks/<name>/` layout.

### Improved

- **Distill now archives source files dropped in the inbox.** If you drop a TMX, PDF or DOCX directly into a bank's `00_INBOX/` folder and run Distill on it, the source file is moved to `00_INBOX/_archive/` after a successful distill – mirroring how Process Inbox archives the Markdown files it compiles.
- **Process Inbox now recognises non-Markdown files in the inbox.** Instead of silently ignoring TMX, PDF or DOCX files dropped in `00_INBOX/`, the button lights up and clicking it shows a helpful message pointing you at Distill for binary files. Mixed inboxes (both `.md` and binary files) process the Markdown and warn about the rest.
- **Health Check shows progress instantly.** The feature used to scan the entire bank synchronously on the UI thread before adding any chat message, leaving the user staring at a frozen UI for several seconds on mature banks. The scan now runs on a background thread and a "SuperMemory: Health Check – scanning memory bank …" bubble appears the moment you click the button.
- **Next-steps messages in Distill and Process Inbox summaries.** The chat banner after a successful Distill now suggests running Process Inbox next and then Health Check; the Process Inbox summary suggests running Health Check afterwards. Obsidian review is positioned as the optional step, not the main one.
- **Chat context bar is now green.** The "Dutch (BE) → English (GB) | Source: …" line at the top of the Supervertaler Assistant panel is now rendered in forest green (Material Design Green 800) instead of medium grey, making the current language pair and source segment much easier to spot at a glance.

### Fixed

- **Chat auto-scroll behaves correctly on long chat histories.** A combination of WinForms layout quirks caused the chat panel to scroll into a vast area of ghost white space below the last message, forcing users to click **Clear** as a workaround before every chat operation. The plugin now manually manages the chat panel's `AutoScrollMinSize` based on the actual position of the last bubble, so the scroll range always matches the real content. No more ghost white space, no more bouncing when scrolling to the bottom, no more disappearing messages after clicking Send.
- **Process Inbox button is correctly disabled when the inbox is empty.** Previously, the button was unconditionally re-enabled after every long-running SuperMemory operation (Health Check, Distill, AutoPrompt) even when the inbox had no files, leading to a dead-end click. The toolbar now tracks the last known inbox count and respects it after un-busying.
- **"Thinking…" bubble no longer bounces the chat.** The thinking-bubble animation timer used to re-scroll the chat every 2 seconds, which yanked the user back to the bottom every time they tried to scroll up during a long operation. The per-tick re-scroll has been removed; the initial scroll when the bubble is first added is enough.
- **User scroll is respected during long operations.** If you scroll up to read older content while Health Check or Distill is running, the chat stays where you put it. Scroll back to the bottom and auto-scroll re-engages automatically.
- **Reports tab label for Process Inbox.** The Reports tab used to label Process Inbox runs as "SuperMemory: Compile" (a legacy internal name from an earlier version of the feature). It now matches the toolbar button label: "SuperMemory: Process Inbox".
- **Duplicate "Thinking…" bubbles.** `SetThinking(true)` is now idempotent: calling it a second time (e.g. when a slow operation pre-sets thinking before delegating to the agent runner) does not create a second animated bubble.

### Changed

- **SuperMemory is the brand name; memory banks are the containers.** The two-level terminology (similar to Gmail/inbox or Obsidian/vault) is now reflected consistently. Chat banners, Reports tab labels, and the help menu use "SuperMemory:" as the system prefix. The toolbar dropdown and the "+ New memory bank…" sentinel use "memory bank" for the individual container. Both naming choices are stable going forward.
- **Memory Banks help pages reorganised** – the three overlapping pages (`memory-banks.md`, `context-awareness.md`, `memory-banks/ai-integration.md`) now follow a clear "one canonical home per concept" principle. `context-awareness.md` is the single authoritative menu of context sources with memory banks as one section among several; `memory-banks.md` is the noun page covering what SuperMemory is, what memory banks are, how to create and switch them, and how they sync with the Python assistant; `memory-banks/ai-integration.md` is the power-user deep dive on the loading algorithm.
- **Tooltip text** on Process Inbox, Health Check and Distill buttons clarified to refer to "the active memory bank" rather than "your SuperMemory" generically.
- **Quick Add dialog title** changed from "Add to SuperMemory" to "Quick Add to memory bank".

### Docs

- **New design memo** at `notes/multi-bank-context-composition.md` (not published to GitBook) captures the open design question of whether stacking SuperMemory + TMs + termbases is additive or noisy, with a rough testing plan for future evaluation.

### Also included (since 4.18.49, the last published RWS AppStore release)

All the 4.18.50–4.18.57 improvements are rolled into this release:

- **Shorter panel names** – docking tabs and ribbon buttons show "TermLens" and "SuperSearch" instead of "Supervertaler TermLens" and "Supervertaler SuperSearch".
- **SuperSearch improvements** – resizable preview pane (draggable splitter), visible source/target divider, highlight rendering fix (no more "documentsare" collisions), preview pane click reliability, header label clipping fix, match truncation fix (matches no longer show "Da..." instead of "Dawn").
- **SuperSearch screencast** embedded at the top of the SuperSearch help page.
- **Gemma 4 models** – Google's Gemma 4 31B and Gemma 4 26B MoE added to both the Gemini provider and OpenRouter routes.
- **Proofreading false positives for inline tags** – source and target now use the same plain-text extraction so tag markup never reaches the AI proofreader.

---

## [4.18.57] – 2026-04-07

### Changed
- **Shorter panel names** – docking tabs and ribbon buttons now show "TermLens" and "SuperSearch" instead of "Supervertaler TermLens" and "Supervertaler SuperSearch"; "Supervertaler Assistant" is unchanged

### Docs
- **SuperSearch screencast** – embedded the new SuperSearch demo video at the top of the SuperSearch help page
- **Updated help, README, and website** with video links and playlist

---

## [4.18.56] – 2026-04-06

### Added
- **SuperSearch resizable preview** – the preview pane below the results grid can now be dragged up or down to resize via a splitter bar (with hover highlight)

### Improved
- **SuperSearch preview divider** – added a visible vertical line between the Source and Target preview boxes, and refined the header background colour for better visual separation

---

## [4.18.55] – 2026-04-06

### Fixed
- **SuperSearch highlight rendering** – rewrote the highlight painting to draw yellow backgrounds first, then render text once on top, eliminating the "documentsare" word-collision and truncation artefacts from the previous overlay approach
- **SuperSearch preview pane** – preview now also updates on cell click (not just selection change), making it more reliable when clicking between results
- **SuperSearch "Target" label clipped** – increased the preview header row height so descenders (g, y, p) are no longer cut off

---

## [4.18.54] – 2026-04-06

### Fixed
- **SuperSearch highlight truncation** – search matches in the results grid showed truncated text with ellipsis (e.g. "Da..." instead of "Dawn") because the highlight overlay used `EndEllipsis` text formatting; match text is now clipped cleanly

---

## [4.18.53] – 2026-04-06

### Added
- **Gemma 4 on OpenRouter** – added Gemma 4 31B and 26B MoE to the OpenRouter provider list for users who prefer that route

---

## [4.18.52] – 2026-04-06

### Added
- **Gemma 4 models** – added Google's new open-source Gemma 4 31B and Gemma 4 26B MoE models to the Gemini provider. These use the same API key and endpoint, offer strong multilingual quality, and have a 256K context window

---

## [4.18.51] – 2026-04-06

### Fixed
- **Proofreading false positives for inline tags** – source segments were sent to the AI with raw Trados tag markup (e.g. `<field name="Seq" value="2"/>`) while target segments had tags stripped to plain text, causing the AI to flag every auto-numbering field and inline tag as "removed from the target". Both source and target now use the same plain-text extraction so tag markup never reaches the proofreader

---

## [4.18.50] – 2026-04-06

### Fixed
- **SuperMemory toolbar** – Health Check button stayed greyed out after any operation completed; now correctly restores to active blue like the other buttons

---

## [4.18.49] – 2026-04-06

### Changed
- **Single-tier licensing** – replaced the three-tier pricing model (TermLens / Supervertaler Assistant / Bundle) with a single plan: **Supervertaler for Trados** at €20/month or €200/year. All features are now included in every paid licence. Existing subscribers on older plans are automatically upgraded to full access

### Improved
- **Licence panel** – simplified UI with a single purchase link; removed the "Upgrade" link (no longer applicable)
- **About dialogue** – licence status now shows "Licence: Active" instead of tier-specific names
- **Licence overlay** – the AI Assistant panel now shows a generic "A licence is required" message instead of the old tier-specific "upgrade required" text
- **Licensing documentation** – rewritten for the single-tier model

---

## [4.18.48] – 2026-04-05

### Added
- **SuperSearch** – new dockable ViewPart (View > Supervertaler SuperSearch) for cross-file search, find & replace, and segment navigation across all SDLXLIFF files in a Trados project
  - Searches all SDLXLIFF files in the target language folder (avoids duplicate results from the source language folder)
  - Search scope: Source & Target, Source only, or Target only
  - Case-sensitive and regex search options
  - Results grid with File, Segment #, Source, Target, and Status columns
  - Matching text highlighted in yellow in both the results grid and the preview pane
  - **Preview pane** – single-click a result row to see the full source and target text in a detail pane below the grid, with large font and yellow match highlighting
  - **Click-to-navigate** – double-click a result to jump to that segment in the Trados editor (active file); clear status message for cross-file segments
  - **Find & Replace** – collapsible replace bar for target-only replacements; single replace (via Trados API, undoable) and Replace All (with two-step confirmation dialog for irreversible disk modifications)
  - **File selection** – Files button to include/exclude specific project files from the search
  - **Keyboard shortcut** – Alt+S opens SuperSearch; if text is selected in the editor, it auto-fills the search box and runs the search immediately
  - **Right-click context menu** – SuperSearch option in the editor context menu
  - Regex replace supports capture groups ($1, $2, etc.)
  - Status bar shows result count, file count, and search time in milliseconds

---

## [4.18.47] – 2026-04-05

### Added
- **Incognito Mode** – new toggle in AI Settings that tells the AI to anonymise all personal and project data in its responses. Project names, file paths, TM names, user names, and other identifying information are replaced with plausible placeholders. Useful for screen sharing, recording demos, posting screenshots in forums, or any situation where client data should remain confidential

### Fixed
- **TM discovery** – the "List TMs" and "Find TM" tools now scan all `.sdlproj` files for TM references, correctly resolving `sdltm.file:///` URIs and relative paths. This fixes the issue where TMs stored in project folders or custom locations (outside the default `Translation Memories` folder) were not found
- **Studio Tools help** – removed outdated Claude-only language; added active-development notice with support contact

---

## [4.18.46] – 2026-04-05

### Added
- **Multi-provider Studio Tools** – Studio Tools now works with all major AI providers: OpenAI, Gemini, Grok, and Mistral, in addition to Claude. Each provider uses its native function calling API (OpenAI `tools`, Gemini `functionDeclarations`, Claude `tool_use`). Ollama remains chat-only as local models have inconsistent tool support

### Improved
- **AI Assistant help** – restructured into sub-pages: Context Awareness, File Attachments, Studio Tools, and Providers and Models (matching the SuperMemory help structure)

---

## [4.18.45] – 2026-04-05

### Added
- **Project Statistics tool** – ask "What are the word counts for this project?" to get a full analysis breakdown (perfect, context, exact, fuzzy, new, repetitions) per language direction, read directly from the `.sdlproj` file
- **File Status tool** – ask "What is the translation status of the files?" to see per-file confirmation status (not started, draft, translated, approved, signed off) with segment and word counts
- **Project Termbases tool** – ask "What termbases are attached to this project?" to list termbases with their enabled state, file paths, and language index mappings
- **TM Info tool** – ask "Tell me about my English-Dutch TM" to get TM details (language pair, segment count, file size, creation date) read directly from the `.sdltm` SQLite metadata
- **TM Search tool** – ask "Search the TM for 'compliance'" to find how terms or phrases were translated before, with source/target pairs and usage counts

### Improved
- **Studio Tools help page** – expanded with all 9 tools, 30+ example questions organised by category (projects, statistics, progress, termbases, TMs, TM search, combined)

---

## [4.18.44] – 2026-04-05

### Added
- **Studio Tools** – the Supervertaler Assistant can now query your Trados Studio installation using natural language. Ask about your projects, translation memories, or project templates and the AI will look up the answer directly from your local Studio data. Uses Claude's tool use API to automatically call the right query behind the scenes – no special syntax needed, just ask naturally (e.g. "What projects do I have?", "Tell me about the Client Alpha project", "List my TMs"). The thinking indicator shows what tool is running (e.g. "Checking Trados projects…"). Currently supports four read-only tools: list projects (with optional status filter), get project details (languages, files, path), list translation memories, and list project templates. Claude-only for now – other providers continue to work as before without tool use
- **Studio Tools help page** – new documentation page with feature overview, available tools table, and 15+ example questions organised by category

---

## [4.18.43] – 2026-04-04

### Added
- **SuperMemory knowledge base integration** – SuperMemory articles (client profiles, domain knowledge, style guides, terminology decisions) are now automatically loaded into both AI chat and batch translation prompts. Context is selected based on the active Trados project: client profiles are matched by project name, domain articles by document type, and style/terminology articles are always included. Token budget management (4 000 tokens) with priority-based trimming ensures prompts stay within limits
- **Client detection** – fuzzy-matches the Trados project name against SuperMemory `01_CLIENTS/` profile frontmatter to automatically load the right client profile
- **Domain detection** – reuses the existing document analyser to load relevant domain knowledge articles (legal, medical, technical, etc.)
- **SuperMemory on/off toggle** – new "Include SuperMemory knowledge base in AI context" checkbox in AI Settings, enabled by default
- **Client code field for termbases** – new optional "Client" metadata field on term entries (e.g. "ACME", "GLOBEX"); shown in the term editor dialog and included in AI prompts when present
- **Inbox auto-refresh** – a FileSystemWatcher monitors the SuperMemory `00_INBOX` folder and automatically updates the inbox count when files are added externally (e.g. via the Obsidian Web Clipper)
- **SuperMemory toolbar heading** – the toolbar strip now shows a "SuperMemory" label and a "?" help link that opens the SuperMemory documentation
- **SuperMemory help links** – the Chat tab's help dropdown now includes a "SuperMemory Help" item; the toolbar "?" also links to the docs

### Improved
- **SuperMemory documentation** – rewrote the safety/backup section with practical advice (no git jargon); added Obsidian Web Clipper setup instructions; documented the AI context integration, auto-detection, and token budget

---

## [4.18.40] – 2026-04-04

### Added
- **SuperMemory** – a self-organizing, AI-maintained translation knowledge base that replaces traditional TMs and term bases with a living wiki of interlinked Markdown files. Stores client profiles, terminology decisions, domain conventions, and style preferences in a human-readable vault that the AI consults when translating. Inspired by Andrej Karpathy's LLM knowledge base architecture
- **SuperMemory Quick Add (Ctrl+Alt+M)** – capture terms and corrections from the Trados editor into your SuperMemory vault. Also available via right-click in the editor grid. Pre-fills from source/target selection, adapts the correction label to the target language (e.g. "Correct Dutch form"), and optionally appends the term to the active translation prompt's terminology table for immediate effect on the next Ctrl+T
- **Per-project active prompt** – right-click any translation prompt in the Prompt Manager and choose "Set as active prompt for this project". Shown with a pin icon and bold blue text in the Prompt Manager, and with a checkmark in the Batch Translate dropdown. Saved per project so different projects can use different prompts
- **Active prompt auto-selection in Batch Translate** – opening a project with an active prompt set automatically selects it in the dropdown

### Improved
- **Selectable and copyable proofreading report text** – issue descriptions and suggestions in the Reports tab are now selectable text with right-click context menu (Copy issue description, Copy suggestion, Copy all). Clicking the segment number or card background still navigates to the segment
- **Batch Translate prompt dropdown layout** – fixed the AutoPrompt button disappearing off-screen when long prompt names were selected; the dropdown and button now resize properly with the panel width
- **Updated help documentation** – SuperMemory, keyboard shortcuts, batch translate, prompts, and project settings pages updated

### Fixed
- **Active prompt indicator path comparison** – normalised path separators so the active prompt checkmark displays correctly regardless of stored path format

---

## [4.18.39] – 2026-04-04

### Fixed
- **Term direction inversion broken for same-language locale pairs** – Quick-add (Alt+Up, Alt+Enter) and the term entry editor incorrectly swapped source/target terms in projects where source and target are variants of the same language (e.g. en-US → en-GB), because the language name comparison failed on format differences ("English (United States)" vs "English (US)"). Now normalises both names via `ShortenLanguageName` before comparing, so locale variants match correctly while genuinely reversed directions (e.g. NL→EN project with EN→NL termbase) still invert as expected.

### Added
- **SuperMemory help page** – new documentation page describing the upcoming self-organising, AI-maintained translation knowledge base feature

---

## [4.18.38] – 2026-04-02

### Improved
- **Markdown table rendering in term notes/definitions** – wide tables (cells longer than 40 characters) now render as labelled paragraphs with full inline formatting (bold, italic, emoji, inline code) instead of a cramped monospace grid that stripped all formatting; compact tables still use the monospace layout

---

## [4.18.37] – 2026-04-02

### Added
- **OpenRouter provider** – access 200+ models from OpenAI, Anthropic, Google, Mistral, and others with a single API key. Includes a curated dropdown of 8 recommended models (Claude Sonnet/Opus, GPT-5.4/Mini, Gemini 3.1 Pro/Flash, Mistral Small 4, Qwen 3.6 Plus Free) plus an editable model field for typing any OpenRouter model ID

### Fixed
- **AI Settings provider/model dropdowns overflowing the dialog** – the Provider and Model dropdowns could extend beyond the right edge of the Settings dialog due to incorrect anchor margin calculation; now properly sized to fit the dialog width and resize with it
- **Model dropdown too narrow for descriptions** – the dropdown list now auto-sizes its width to fit the longest model description text

---

## [4.18.36] – 2026-04-02

### Improved
- **Respect Trados "Enabled" checkbox for MultiTerm termbases** – MultiTerm termbases disabled in Trados Project Settings → Termbases are now excluded from TermLens; previously all attached termbases were loaded regardless of the Enabled flag
- **Instant refresh when toggling termbases in Project Settings** – a lightweight 2-second polling timer detects changes to the Trados termbase configuration so enabling or disabling a MultiTerm termbase takes effect immediately without needing to change segments

---

## [4.18.35] – 2026-04-02

### Fixed
- **MultiTerm matches disappear after adding or editing a term** – adding a term via the dialog, editing a term via right-click, or saving AI Assistant settings triggered a full Supervertaler termbase reload that replaced the in-memory index without re-merging MultiTerm entries; the green MultiTerm chips would vanish until navigating to a different segment and back. Fixed by re-loading MultiTerm termbases after every full index rebuild. This bug has been present since MultiTerm support was introduced in v3.4.0.

---

## [4.18.34] – 2026-04-02

### Improved
- **Batch Operations provider/model selector** – the provider label in Batch Operations is now clickable and shows the same cascading flyout menu as the Chat tab, allowing quick model switching without opening AI Settings

---

## [4.18.33] – 2026-04-01

### Added
- **Supervertaler Assistant licensing tier** – new standalone plan for AI-only users (€15/month or €150/year); grants access to AI Assistant, Batch Translate, QuickLauncher, and Ctrl+T without requiring TermLens; termbases (including MultiTerm) are still loaded for AI prompt context
- **Three-tier pricing** – TermLens (€10/month), Supervertaler Assistant (€15/month), TermLens + Supervertaler Assistant (€20/month); annual plans include 2 months free

### Improved
- **Settings gear icon** – switched from ⚙ (Segoe UI Symbol) to the native Windows 11 gear icon (Segoe MDL2 Assets) on both panels
- **Settings tab renamed** – "TermLens" tab in Settings renamed to "Termbases" so it makes sense for all tiers

---

## [4.18.32] – 2026-04-01

### Improved
- **Clipboard Mode uses shorter per-segment language labels** – segment lines now use "Dutch:" / "English:" instead of "Dutch (Netherlands):" / "English (United Kingdom):", saving roughly 2,000 tokens on a 500-segment document; the full language names with variants are still stated once in the system prompt
- **Prompt dropdown auto-selects by project name** – when switching to Batch Operations or changing between Translate and Proofread, the prompt dropdown automatically selects the first prompt whose name contains the current Trados project name (e.g. opening the HAYNESPRO project auto-selects the "HAYNESPRO" prompt)

### Fixed
- **Clipboard Mode now uses the selected prompt** – "Copy to Clipboard" was reading a stale prompt selection from settings instead of the current dropdown value, causing it to use the previous prompt; the dropdown selection is now persisted before building the clipboard prompt

---

## [4.18.31] – 2026-04-01

### Fixed
- **Per-project termbase selection no longer carries over to new projects** – when opening a Trados project for the first time, TermLens now starts with clean defaults (all termbases enabled, no Write or Project flags) instead of inheriting the previous project's selections

---

## [4.18.30] – 2026-04-01

### Added
- **Clipboard Mode** – new workflow for translating and proofreading via any web-based LLM (ChatGPT, Claude, Gemini, etc.) without an API key; tick the "Clipboard Mode" checkbox in Batch Operations, click "Copy to Clipboard" to get a fully formatted prompt with numbered bilingual segments (including status annotations, terminology, and document context), paste into any LLM chat, then click "Paste from Clipboard" to import the translations back into Trados with full tag reconstruction and validation

### Changed
- **`built_in` YAML field renamed to `default`** – prompt files now use `default: true` instead of `built_in: true`; the old field name is still accepted for backward compatibility; all internal C# naming updated accordingly (`IsBuiltIn` → `IsDefault`, `EnsureBuiltInPrompts` → `EnsureDefaultPrompts`, etc.)
- **Term Picker shortcut changed from Ctrl+Alt+G to Ctrl+Alt+Down** – updated shortcut binding, About dialog, and all documentation

---

## [4.18.28] – 2026-03-31

### Fixed
- **Batch Translate/Proofread no longer fails with "Cannot determine source/target language"** – the language pair is now cached when a document is opened and when segments are navigated, so the AI Assistant can still resolve languages even when the editor's ActiveFile is temporarily unavailable

---

## [4.18.27] – 2026-03-30

### Added
- **Text transforms in QuickLauncher** – new `type: transform` prompt type that performs local find-and-replace on the active target segment without calling an AI provider; rules are defined as `find:`/`replace:` pairs in the prompt content body with `\uXXXX` Unicode escape support; built-in "Strip U+2028" transform removes invisible InDesign line separators; cleaned text is automatically copied to the clipboard; see [Text Transforms](https://supervertaler.gitbook.io/trados/features/text-transforms) in the help docs

---

## [4.18.26] – 2026-03-30

### Added
- **Batch size spinner in AI Settings** – new "Batch size" control lets users adjust the number of segments sent per API call during Batch Translate and Batch Proofread (range 5–100, default 20)

### Fixed
- **InDesign U+2028/U+2029 line separators no longer corrupt AI translations** – Unicode line and paragraph separators (used by InDesign IDML as forced line breaks) are now replaced with spaces before sending to the AI provider, preventing spurious line breaks in translations

---

## [4.18.25] – 2026-03-30

### Added
- **Customisable Ollama timeout** – new "Timeout (min)" setting in AI Settings lets users override the automatic timeout (3–10 min based on model size) with a custom value up to 120 minutes, useful for long-running jobs on hardware without a dedicated GPU
- **QuickLauncher flat section display** – right-click any QuickLauncher folder in Settings → Prompts and choose "Show as section in menu" to display its prompts as a flat list with a bold header instead of an expandable submenu

### Fixed
- **Duplicate "What file is this segment from?" prompt** – old file variant (without trailing underscore) is now cleaned up on startup
- **Ollama default timeout for 10–12B models** – models in the 10B–12B range (including TranslateGemma 12B) now default to 5 minutes instead of 3

---

## [4.18.22] – 2026-03-29

### Added
- **QuickLauncher folder submenus** – context menu now mirrors your prompt library's folder structure instead of a flat list, making it easier to organise and find prompts
- **New default prompts** – three Explain prompts (brief, detailed, terminology) and two Files prompts (current filename, segment file source) ship out of the box in dedicated subfolders
- **Old explain prompts auto-cleaned** – retired explain prompt variants are automatically deleted on startup to avoid duplicates

### Changed
- **Domain → Category rename** – the `Domain` property on prompts has been renamed to `Category` throughout the UI and codebase for clarity
- **Support links updated** – About dialog, support docs, and help pages now point to the Supervertaler forum on TranslationTech; Groups.io and ProZ.com references removed

---

## [4.18.21] – 2026-03-29

### Fixed
- **Line breaks preserved in Visio and similar formats** – when the source segment stores line breaks as literal `\n` in text content (Visio `.vsdx`, Excel) rather than as separate placeholder tags (DOCX `w:br`), the newlines are now correctly preserved in the translated target instead of being silently dropped

---

## [4.18.20] – 2026-03-28

### Fixed
- **Gemini API key no longer exposed in error messages** – API key moved from URL query parameter to `x-goog-api-key` request header
- **Licence file corruption no longer silently resets to trial** – a warning is now shown when the licence file is unreadable, prompting the user to re-enter their key
- **Gemini response parsing more robust** – regex anchored to the `candidates[].content.parts[].text` response structure to avoid matching wrong fields
- **Unicode escapes in translations handled correctly** – `\uXXXX` sequences in LLM responses are now unescaped to the correct characters

### Changed
- **Diagnostic loggers removed** – temporary term-content loggers in TermbaseReader and TermMatcher that wrote to disk on every segment navigation have been removed
- **ARM64 SQLite DLL declared in manifest** – `runtimes/win-arm64/native/e_sqlite3.dll` now has a corresponding `<File>` entry in the plugin manifest
- **Default model names stay in sync** – provider defaults now reference the `LlmModels` arrays directly instead of hardcoded strings
- **Multi-word term matching optimised** – the list of multi-word terms is now cached and only rebuilt when the termbase index changes
- **Ping URL updated** – usage statistics endpoint updated to the `supervertaler.com` domain
- **Term reader resilient to schema changes** – `ReadTermEntry` uses column name lookup instead of hardcoded positional indices

---

## [4.18.19] – 2026-03-28

### Fixed
- **Line endings preserved in translation** – when the LLM emits a bare newline instead of a `<tN/>` tag placeholder, the correct line ending is now restored in the target segment: soft-return tags (DOCX `↵`) are re-inserted by cloning the source tag; for file formats where the newline is already embedded in text content (Visio, Excel, plain text), the newline is preserved as-is in the target IText node

---

## [4.18.18] – 2026-03-28

### Changed
- **Batch Translate reports consolidated** – a multi-batch translate operation now appears as a single entry in the Reports tab (showing combined token count, cost, and total duration) instead of one entry per sub-batch

---

## [4.18.17] – 2026-03-27

### Added
- **Mistral AI support** – Mistral Large, Mistral Small, and Mistral Nemo are now available as a first-class provider alongside OpenAI, Claude, Gemini, Grok, and Ollama

---

## [4.18.16] – 2026-03-27

### Added
- **Cost protection** – QuickLauncher prompts are now standalone (no chat history sent), preventing accidental high costs from accumulated context; chat messages are constrained by a token budget that automatically trims old messages; a cost warning dialog appears when estimated input exceeds $0.50
- **Enriched API error messages** – common errors (403 model not enabled, 401 invalid key, 429 rate limit, 402 insufficient funds) now show user-friendly guidance instead of raw JSON
- **Help system improvements** – fixed off-by-one in Settings tab help links; Settings help button now shows a dropdown preview consistent with other panels; added help topics for General, QuickLauncher, and Usage Statistics

### Changed
- **OpenAI models streamlined** – replaced GPT-4.1, GPT-4.1 Mini, and o4-mini with GPT-5.4 and GPT-5.4 Mini; default model is now GPT-5.4 Mini; existing users are auto-migrated
- **GPT-5.x API compatibility** – use `max_completion_tokens` instead of `max_tokens` for GPT-5.x models
- **Default prompts** – "Built-in" renamed to "Default" throughout (folder, UI labels, docs); default prompts are now immutable (use Clone to modify); "Show in QuickLauncher menu" checkbox for hiding prompts
- **QuickLauncher menu** – section headers ("Default" / "Custom") replace star indicators
- **AutoPrompt hidden in Proofread mode** – the AutoPrompt link is no longer shown when Batch Operations is in Proofread mode
- **AutoPrompt language softened** – replaced aggressive meta-prompt wording ("FORBIDDEN", "NON-NEGOTIABLE") with clearer, firmer alternatives to reduce LLM safety-filter refusals

### Documentation
- AI Cost Guide updated with GPT-5.4 / GPT-5.4 Mini pricing
- Model lists updated across all docs
- "Built-in Prompts" → "Default Prompts" in help navigation
- Star note added to README

---

## [4.18.15] – 2026-03-26

### Changed
- **Default max segments reduced to 20** – the "Include full document content in AI context" setting now defaults to 20 segments instead of 500, avoiding unexpectedly high API costs for new users

---

## [4.18.14] – 2026-03-26

### Added
- **Generate project brief** – new built-in QuickLauncher prompt that produces a comprehensive Markdown summary of the current project (subject matter, terminology, named entities, translation challenges) for pasting into other AI tools
- **Restore button tooltip** – the Restore button in the Prompt Manager now shows "Restore all built-in prompts to their defaults"

### Changed
- **Prompts now saved as `.md`** – prompt files use standard Markdown (`.md`) extension instead of `.svprompt`; existing `.svprompt` files are auto-migrated on startup; a new `type: prompt` YAML field identifies prompt files
- **QuickLauncher heading opens Prompts tab** – clicking the "Supervertaler QuickLauncher" heading in the Ctrl+Q menu now correctly opens Settings → Prompts instead of AI Settings
- **App label spacing** – added spacing between the "App:" label and dropdown in the prompt editor dialog

---

## [4.18.12] – 2026-03-25

### Fixed
- **Settings accessible when trial expires** – the gear button on both the TermLens and Supervertaler Assistant panels now opens the Settings dialog even when the trial has expired or no licence is active, so users can enter a licence key
- **AI Assistant gear button visible above overlay** – the settings and help buttons are no longer hidden behind the licence overlay
- **Per-project settings now reliably remembered** – fixed a bug where `NotifySettingsChanged` reloaded global settings without re-applying the per-project overlay, causing termbase paths and enabled/disabled lists to revert when switching projects
- **Per-project settings auto-saved on first encounter** – opening a Trados project that has no Supervertaler settings file now auto-creates one from the current configuration
- **Per-project settings saved on shutdown** – settings are now saved when Trados closes, not just on project switch or settings dialog OK

### Changed
- **Clearer expired trial message** – both panels now say "Click the ⚙ button above" instead of the vague "Enter a license key in Settings → License"
- **Human-readable project settings filenames** – per-project files now use the format `{hash} - {project name}.json` instead of just `{hash}.json`; existing files are auto-migrated on load
- **Pretty-printed project settings JSON** – project settings files are now indented for readability

---

## [4.18.11] – 2026-03-25

### Added
- **MultiTerm termbases in AI Settings** – MultiTerm termbases now appear in the "Termbases included in AI prompts" checklist on the AI Settings tab, matching what's shown on the TermLens tab

### Changed
- **Feature renamed: AutoPrompt** – "Analyse Project & Generate Prompt" is now called **AutoPrompt** throughout the UI, docs, and log labels
- **TermScan** naming consistent in docs – the automatic glossary extraction step is consistently referred to as TermScan

---

## [4.18.10] – 2026-03-24

### Added
- **Unified prompt library schema** – prompts now use a consistent YAML frontmatter format (`category`, `app`, `built_in`) shared between Supervertaler Workbench and Supervertaler for Trados
- **App-specific prompt filtering** – prompts tagged `app: "workbench"` are hidden in Trados; prompts tagged `app: "trados"` are hidden in Workbench; `app: "both"` (default) shows everywhere
- **App dropdown in prompt editor** – new "App" field lets you choose whether a prompt is for Both, Trados only, or Workbench only
- **Variable insertion menu reorganised** – Ctrl+, now groups variables into Common and Trados-specific sections
- **MultiTerm diagnostic logging** – loading failures are now logged to `%LocalAppData%\Supervertaler.Trados\multiterm_debug.log` instead of being silently swallowed

### Changed
- **Prompt YAML keys standardised** – `domain` → `category`, `sv_quickmenu`/`quick_run` → `quickmenu`, `quicklauncher_label` → `quickmenu_label`; legacy keys are still accepted for backward compatibility
- **Prompt library cleaned up** – removed duplicate folders and files, fixed malformed YAML frontmatter, standardised variable names (`{{SOURCE_TEXT}}` → `{{SOURCE_SEGMENT}}`)

### Removed
- **Example project removed** – the bundled example project contained a client reference and has been removed from the repo, all releases, and the help system

---

## [4.18.9] – 2026-03-24

### Added
- **Markdown rendering in TermLens popup** – Notes and Definition fields now render Markdown formatting (tables, bold, italic, headings, bullet lists, code blocks) instead of plain text
- **Resizable TermLens popup** – drag the bottom-right corner grip to resize the popup; width is remembered for the rest of the session
- **Copy raw Markdown from AI Assistant** – right-click → Copy on a chat bubble now copies the original Markdown (preserving tables and formatting) instead of stripped plain text

### Changed
- **Wider TermLens popup** – default maximum width increased from 500px to 700px so tables display more clearly
- **AI Assistant uses proper Markdown tables** – system prompt now instructs the AI to use valid Markdown table syntax with pipe delimiters and separator rows
- **User data folder restructured** – Trados settings files (`settings.json`, `license.json`, `chat_history.json`) now live under `trados/settings/` instead of directly in `trados/`; auto-migrated on first run

### Fixed
- **In-plugin purchase links now use live checkout** – the "Buy" links in Settings → License were still pointing to test mode URLs

---

## [4.18.8] – 2026-03-24

### Fixed
- **In-plugin purchase links now use live checkout** – the "Buy" links in Settings → License were still pointing to test mode URLs

---

## [4.18.7] – 2026-03-24

### Changed
- **Trial period reduced to 14 days** – down from 90 days; the free trial now runs for 14 days from first launch
- **Live Lemon Squeezy checkout** – switched from test mode to live payment processing

---

## [4.18.6] – 2026-03-23

### Added
- **Prompt inspector in Reports tab** – every AI API call can now be logged with the full system prompt, messages, response, token counts, and estimated cost; enable via "Log prompts and responses to Reports tab" in AI Settings
- **Expandable prompt sections** – click "Show system prompt...", "Show messages...", or "Show response..." to view the full text; press Escape to collapse; "Copy" copies a single section, "Copy all" copies everything
- **Batch translate and proofread logging** – batch operations now appear in the Reports tab when prompt logging is enabled
- **Prompt name in Reports tab** – entries show the prompt template name (e.g. "QuickLauncher · Explain in Context · 14:32:05")
- **Clone prompt** – right-click any prompt in the Prompt Manager and select "Clone" to create a copy with "(2)" appended
- **QuickLauncher menu heading** – Ctrl+Q menu now shows "Supervertaler QuickLauncher" at the top; click it to open Settings → Prompts tab

### Fixed
- **Tracked changes no longer corrupt term additions** – Add Term, Quick-Add, Non-Translatable, QuickLauncher prompts, and Expand Selection now strip deleted tracked changes, adding only the final text
- **QuickLauncher segment context** – QuickLauncher prompts now pass clean segment text without tracked changes markup
- **Prompt log cards no longer squashed** – the resize handler was recalculating prompt log card heights using the proofreading layout logic, hiding all expandable sections
- **New prompts in subfolders now saved correctly** – creating a prompt while a subfolder was selected would create a wrongly-named folder instead of placing the file in the correct subfolder

---

## [4.18.4] – 2026-03-23

### Fixed
- **Tracked changes no longer confuse AI features** – proofreading, batch translation, AI chat, and prompt generation now read only the final (accepted) text from segments with tracked changes, instead of including both deleted and inserted text
- **GPT-5.4 replaces GPT-5.3** – updated to OpenAI's latest flagship model

### Changed
- **Updated LLM model lineup** – OpenAI: GPT-4.1, GPT-4.1 Mini, GPT-5.4, o4-mini; Gemini: added 3.1 Pro (Preview); Ollama: Qwen 3 bumped to 14B
- **Descriptive model tooltips** – each model now shows a short description to help translators choose the right one

---

## [4.18.3] – 2026-03-23

### New Features
- **Delete prompt folders** – right-click any folder in the prompt library tree and select "Delete Folder" to remove it and all prompts inside (with confirmation dialog)
- **Refresh button in Prompt Manager** – toolbar "Refresh" button reloads prompts from disk, reflecting any changes made outside Trados (e.g. in Windows Explorer or Supervertaler Workbench)
- **UI scale setting** – new General tab in Settings with UI scale selector for high-DPI displays
- **Chat font size controls** – A+/A− buttons in the AI Assistant to adjust chat bubble font size; persisted across sessions

### Fixed
- **Right-click context menu on prompt folders** – right-clicking a folder in the prompt library now correctly selects the folder before showing the context menu (WinForms TreeView doesn't auto-select on right-click)

### Changed
- **DPI-aware UI rendering** – TermLens, AI Assistant chat bubbles, term blocks, and settings dialog now scale correctly on high-DPI displays using the new `UiScale` helper

---

## [4.18.2] – 2026-03-22

### Fixed
- **Manifest version sync** – the `pluginpackage.manifest.xml` version was stuck at 4.18.0.0 while the DLL and plugin.xml were at 4.18.1.0. All three version files are now correctly synced to 4.18.2.

### Changed
- **TermLens colour coding docs** – renamed "Lavender" to "Purple" for abbreviation match chips, as it is more recognisable as a colour name.

---

## [4.18.1] – 2026-03-22

### Fixed
- **Editing terms in inverted-direction termbases corrupted entries** – when editing a term via the multi-entry editor (right-click → Edit Term) and the termbase direction was opposite to the project direction (e.g. EN→NL termbase in a NL→EN project), the source and target terms were saved swapped. This caused the edited entry to disappear from TermLens on the next refresh and left corrupted data in the termbase. The multi-entry editor now correctly detects and handles inverted termbase directions, matching the behaviour of the single-entry editor.

### Added
- **Diagnostic logging for duplicate source terms** – temporary logging to `%LocalAppData%\Supervertaler.Trados\termlens_diag.log` when multiple entries share the same source term. Helps diagnose any remaining edge cases. Will be removed in a future release.

---

## [4.18.0] – 2026-03-22

### Added
- **TreeView-based Prompt Manager** – the Prompts settings tab has been completely redesigned. The flat grid has been replaced with a folder-based tree view that mirrors the on-disk `prompt_library` structure. Click any prompt to see its content in the detail pane on the right. Click "System Prompt" to view and edit the system prompt. Folders can be created, and prompts can be dragged and dropped between folders.
- **QuickLauncher keyboard shortcuts** – assign Ctrl+Alt+1 through Ctrl+Alt+0 to individual QuickLauncher prompts in Settings → Prompts. Each shortcut runs the assigned prompt instantly without opening the menu. Shortcuts are shown next to prompt names in the Ctrl+Q menu.
- **Prompt reordering** – prompts within a folder can be moved up or down using the ▲/▼ buttons in the toolbar. The order is persisted via a `sort_order` field in the YAML frontmatter and applies to the Ctrl+Q menu as well.
- **Quick model switching** – click the provider/model label at the bottom of the AI Assistant chat to change models without opening Settings. Shows "Click to change model" tooltip on hover.

### Changed
- **Improved term popup readability** – definition and notes text is now darker and more readable in the TermLens hover popup.
- **Multi-line term fields** – Definition and Notes in the term entry editor now have expand/collapse buttons (▲/▼) to toggle between compact and expanded views.
- **Resizable Prompts panel** – the divider between the tree and detail pane in Settings → Prompts can now be dragged left or right to resize.
- **Prompt generator respects TM toggle** – the "Analyse Project & Generate Prompt" feature now respects the "Include TM matches in AI context" setting. Previously, TM reference pairs were always collected regardless of this toggle.

### Fixed
- **HttpClient timeout causing prompt generation failures** – the .NET HttpClient's default 100-second timeout was silently overriding our per-request timeout settings (up to 10 minutes). This caused all long-running API calls (prompt generation, large batch translations) to fail after exactly 100 seconds across all providers. Fixed by setting HttpClient.Timeout to infinite and managing timeouts via CancellationToken as intended.
- **Silent timeout errors** – when an API request timed out, the thinking indicator would disappear with no error message. Now shows a diagnostic message with model name, prompt size, and max output tokens to help troubleshoot.
- **Expand buttons in term editor** – fixed z-order issue where the Definition expand button was hidden behind the text box. Both expand buttons now render correctly.

---

## [4.17.1] – 2026-03-21

### Changed
- **Auto-restart after update** – clicking "Install Update" now offers to automatically restart Trados Studio, instead of asking the user to close and reopen it manually. Saves time on the lengthy Trados startup cycle.

---

## [4.17.0] – 2026-03-21

### Added
- **Document attachments** – attach documents directly to AI Assistant messages for context. The AI receives the full extracted text alongside your message. Supported formats: DOCX, DOC, PDF, RTF, PPTX, PPT, XLSX, XLS, CSV, TSV, TMX, SDLXLIFF, XLIFF/XLF, TBX, TXT, Markdown, HTML, JSON, and XML. Drag and drop files onto the chat input, or use the attach button to browse.
- **Quick model switching** – click the provider/model label at the bottom of the AI Assistant chat to instantly switch between models and providers without opening the Settings dialogue. A dropdown menu shows all available models grouped by provider, with the current selection highlighted.
- **Multi-line Definition and Notes fields** – the term entry editor now uses expandable multi-line text areas for Definition and Notes fields, with a pop-open button to toggle between compact (3 lines) and expanded (8 lines) views. Line breaks in definitions and notes are now preserved correctly.

### Changed
- **Unified attach button** – the image-only attach button has been replaced with a universal paperclip (📎) icon that handles both images and documents. The file dialogue is organised into categories: Images, Documents, Spreadsheets, Translation files, and Text files. The tooltip lists all supported formats.
- **Improved term popup formatting** – definition and notes labels are now bold in the hover popup, and multi-line content uses hanging indentation so continuation lines align with the first line of text rather than the label.

---

## [4.16.2] – 2026-03-20

### Added
- **TermScan filtering for prompt generation** – "Analyse Project & Generate Prompt" now scans your document's source segments and filters termbase terms down to only those that actually appear in the document. This produces dramatically smaller, more focused glossaries in the generated prompt (e.g. 123 relevant terms from 2,680 total). The status message shows the filter count: "filtered X relevant from Y total".

### Changed
- **Chat avatars** – each message in the AI Assistant now has a small avatar header: a gray "AI" circle with "Supervertaler Assistant" for AI responses, and a blue person silhouette with "You" for your messages. Makes it easy to tell who said what at a glance.
- **Animated thinking indicator** – the AI Assistant now shows a persistent animated bubble in the chat area while waiting for a response. The bubble cycles through reassuring messages ("Thinking…", "Still working on it…", "Generating response…", etc.) so you always know the AI is still processing. Previously, the thin "Thinking…" label at the bottom could disappear during long operations, making it look like the request had silently failed.
- **System-initiated messages styled as assistant bubbles** – messages triggered by buttons (e.g. "Analyse Project & Generate Prompt") now display with assistant styling (gray, left-aligned) instead of user styling, since you didn't type them yourself.
- **TM reference pairs filtered to confirmed segments** – the prompt generator now only includes human-confirmed segments (Translated, Approved, or Signed-off) as reference pairs. Unconfirmed AI-generated translations are excluded to avoid feeding unverified output back as "correct" references. Pairs are sampled evenly across the document for diversity.

### Fixed
- **Stale prompt dropdown** – deleting prompts from the Prompt Manager no longer leaves ghost entries in the Batch Operations prompt dropdown. The dropdown now refreshes whenever the Prompt Manager closes, regardless of whether you clicked OK or Cancel (because deletions happen immediately on disk).
- **API timeout for large output requests** – prompt generation and other requests that produce long AI responses (> 8,192 tokens) now use a 10-minute timeout instead of timing out prematurely. This prevents the "thinking" indicator from disappearing mid-generation on complex documents.

---

## [4.16.1] – 2026-03-20

### Added
- **One-click plugin update** – the "Update Available" dialogue now has an "Install Update" button that downloads and installs the new version directly, without opening a browser. Just click, restart Trados, and you're running the latest version.
- **"What's new" link in update dialogue** – view the release notes before updating.

### Fixed
- **Prompt generation truncation** – the "Analyse Project & Generate Prompt" feature no longer cuts off long prompts. Output token limit increased from 4,096 to 32,768, allowing comprehensive prompts with large glossaries and TM reference pairs.
- **Correct version in plugin packages** – the `.sdlplugin` manifest now reads the version from the project file instead of using a hardcoded value. The DLL, manifest, and plugin.xml versions are guaranteed to match.
- **Stale assembly references** – fixed two action entries in plugin.xml that were stuck on old version numbers (4.5.0 and 4.10.0). The version bump script now uses pattern matching to catch all references, preventing this from recurring.

---

## [4.16.0] – 2026-03-20

### Added
- **Interactive term popup** – hovering over a term chip now shows an interactive popup instead of a standard tooltip. The popup supports word-wrapped text, stays open when you move the mouse into it, and renders URLs as clickable links.
- **URL metadata field** – term entries now support a URL field for linking to reference material. URLs appear as clickable links in the hover popup, and can be edited in the term entry editor and termbase editor grid.
- **Dismissible proofreading issues** – each issue card in the Reports tab now has a checkbox; ticking it removes the card from the list so you can track which issues you have addressed.

### Changed
- **Proofreading scope labels** – dropdown labels now use correct Trados terminology: "Translated only" and "Translated + approved/signed-off" instead of the previous MemoQ-style "Confirmed" labels.
- **Faster popup close** – the hover popup close delay was reduced from 200ms to 150ms for a snappier feel.

### Fixed
- **Popup text truncation** – long definitions, notes, and other metadata no longer get cut off in the hover popup. Text now word-wraps correctly within the popup.
- **Popup spacing** – removed excessive vertical spacing between metadata lines in the hover popup.

---

## [4.15.0] – 2026-03-20

### Added
- **Grok (xAI) provider support** – Grok is now available as a first-class AI provider alongside OpenAI, Claude, Gemini, Ollama, and Custom OpenAI-compatible endpoints. Three models included: Grok 4.20 (Reasoning), Grok 4.20, and Grok 4.1 Fast. All models support multimodal input (text + images).
- **Source synonym indicator** – the ≡ synonym indicator on term chips now also appears when the entry has source-side synonyms, not just target-side ones.
- **Source synonyms in tooltip** – hovering over a term chip now shows source-side synonyms (prefixed with "Also:") alongside the existing target-side synonym bullets.

### Fixed
- **Merge prompt direction** – the "Similar Term Found" merge dialog now correctly displays terms in the project's language direction when working with inverted termbases.

---

## [4.14.1] – 2026-03-19

### Added
- **Synonym indicator on term chips** – a small indigo ≡ icon now appears in the top-right corner of a term chip when the entry has target synonyms, so you can see at a glance which terms have alternative translations without hovering.
- **"Open Plugins folder" link in update dialog** – when a new version is available, the update notification now includes a clickable link that opens the Plugins/Unpacked folder in Explorer. Essential for Mac/Parallels users who must manually delete the old unpacked folder before installing an update.

### Fixed
- **Metadata indicator always visible** – the amber metadata dot (definition/domain/notes) now appears on all term chips, not only on chips that also have a shortcut badge.
- **Merge prompt respects project direction** – the "Similar Term Found" dialog now displays terms in the project's language direction when working with an inverted termbase (e.g. NL→EN project using an EN→NL termbase). Previously, source and target labels were swapped.

---

## [4.14.0] – 2026-03-19

### Added
- **Analyse Project & Generate Prompt** – new feature that analyses your document's content, terminology, and TM data to automatically generate a comprehensive domain-specific translation prompt using AI. Accessible via the link next to the prompt selector on the Batch Operations tab. The generated prompt appears in the AI Assistant chat, where you can refine it through conversation. Right-click any assistant message → "Save as Prompt…" to save the result to your prompt library.
- **Save as Prompt** – right-click any AI Assistant response and choose "Save as Prompt…" to save it as a reusable `.svprompt` file in your prompt library. The default name is your Trados project name, with automatic version numbering (v2, v3, etc.) if a prompt with that name already exists.

### Changed
- **British English spelling** – all user-facing text now uses British English spelling throughout the plugin and documentation (analyse, customise, organised, etc.).
- **Documentation improvements** – removed duplicate page headings from all help pages, added cross-references for the new Analyse Project & Generate Prompt feature, and added comprehensive documentation for the new feature.

### Fixed
- **Save as Prompt dialog** – fixed buttons being cut off at the bottom of the dialog under certain DPI scaling settings.
- **Synonym language tags in inverted termbases** – when editing a term from an inverted termbase (e.g. EN→NL termbase used in an NL→EN project), synonyms were saved with swapped language tags, causing them to appear on the wrong side. Now correctly reverses the language tags when saving.

---

## [4.13.0] – 2026-03-19

### Changed
- **Simplified built-in prompts** – replaced the 9 domain-specific translate prompts (Medical, Legal, Patent, Financial, Technical, Marketing, IT, Professional Tone, Preserve Formatting) with a single **Default Translation Prompt**. The default prompt is a general-purpose starting point that users can duplicate and customise for their specific domain. The Default Proofreading Prompt and all QuickLauncher prompts are unchanged.
- **Automatic cleanup of retired prompts** – on first launch after the update, the old domain-specific translate prompt files are automatically removed from the prompt library (only if they still contain the original built-in content – user-modified copies are preserved).

---

## [4.12.5] – 2026-03-19

### Fixed
- **Fixed duplicate plugin crash** – the `.sdlplugin` package filename (`Supervertaler.Trados`) did not match the `<PlugInName>` in the manifest (`Supervertaler for Trados`), causing Trados to create a second copy of the package under the manifest name. Two copies of the same plugin loaded simultaneously, crashing Trados on startup. The package filename now matches the manifest name. The build script also cleans up the old-name package and unpacked folder to prevent recurrence.

---

## [4.12.4] – 2026-03-19

### Added
- **Automatic stale-plugin detection** – when a new `.sdlplugin` is installed but Trados is still running the old extracted version, the plugin now detects the version mismatch at startup and prompts the user to restart. On restart, the old Unpacked folder is cleaned up and Trados re-extracts the new version automatically. Searches all three possible plugin locations (Roaming, Local, All Users) so the detection works regardless of which install option was chosen.

### Changed
- **Simplified install location guidance** – the installation docs now recommend accepting the default installer option ("All your domain computers") instead of manually switching to "This computer for me only". On non-domain PCs the two options are identical, and accepting the default avoids inconsistency between updates.

---

## [4.12.3] – 2026-03-19

### Fixed
- **Usage statistics checkbox now reflects opt-in choice** – when a user clicked "Yes" in the first-launch usage statistics dialog, the setting was saved to disk but the in-memory settings object was not updated. This caused the checkbox in Settings to appear unchecked until Trados was restarted. The opt-in choice is now synced into the live settings immediately.

---

## [4.12.2] – 2026-03-19

### Added
- **Parallels / Mac warning in first-run setup** – when running inside Parallels Desktop on a Mac, the setup dialog now shows a yellow warning panel advising users to keep their data folder on the Windows side (`C:\` drive). If the user selects a Mac-side path (`\\Mac\Home\...`), a confirmation dialog explains that SQLite databases do not work reliably on network-mounted filesystems. Non-Parallels users see no change.
- **Parallels / Mac documentation** – new "Running on a Mac (Parallels)" section in the installation help, and a new "Database errors on Mac (Parallels)" troubleshooting entry
- **Updated installer screenshot** – annotated screenshot showing the recommended "This computer for me only" option

---

## [4.12.1] – 2026-03-19

### Fixed
- **Version numbers now consistent across all plugin files** – the plugin.xml and pluginpackage.manifest.xml version attributes were out of sync with the assembly version, which could cause the wrong version to display in Trados. All version files are now aligned. Also rewrote `bump_version.py` to update all three version files (.csproj, plugin.xml, manifest) in a single command.

---

## [4.12.0] – 2026-03-19

### Added
- **`{{TM_MATCHES}}` prompt variable** – QuickLauncher prompts can now include translation memory fuzzy matches (≥70%) from the active segment. The variable expands to a formatted list showing match percentage, TM name, source, and target text. Available in the variable picker (Ctrl+,) and documented in the help system.
- **3 new built-in QuickLauncher prompts** – "Explain (within project context)" uses `{{PROJECT}}` for document-aware term explanation; "Translate segment using fuzzy matches as reference" combines `{{TM_MATCHES}}` with `{{SURROUNDING_SEGMENTS}}` for context-aware translation; "Translate selection in context of current project" uses `{{PROJECT}}` for full-document term translation
- **Opt-in anonymous usage statistics** – on first launch after this update, a dialog asks whether you'd like to share anonymous usage data to help improve the plugin. Only plugin version, OS version, Trados version, and system locale are sent – once per session, on startup. No personal data, translation content, or termbase information is ever collected. The setting can be changed at any time in Settings. Includes Parallels/VM detection to understand how many users run Trados on a Mac. Data is sent to a first-party Cloudflare Worker endpoint (no third-party trackers). ([#7](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/7))

---

## [4.10.12] – 2026-03-18

### Added
- **Termbase rename** – double-click a termbase name in TermLens Settings (or press **F2**) to rename it
- **fix_reversed_entries.py** – `tools/` script to detect and swap term entries that were stored in the wrong direction in a termbase

---

## [4.10.11] – 2026-03-18

### Fixed
- **Term display and immediate chip appearance restored for inverted-direction termbases** – when a project's translation direction is the opposite of a write termbase's declared direction (e.g. NL→EN project using an EN→NL termbase), TermLens now correctly indexes and matches terms after loading from disk (F5 and segment navigation both work), and newly added terms appear as chips immediately after Alt+Down or Alt+Up
- **Edit Term Entry dialog now follows project direction** – column labels, text fields, synonyms, and abbreviation fields are presented in project source → target order (e.g. Dutch | English in a NL→EN project) regardless of the termbase's declared direction; saves still write to the correct termbase columns

---

## [4.10.10] – 2026-03-18

### Fixed
- **Term direction now respects termbase language pair** – when adding terms via Alt+Down, Alt+Up, Ctrl+Alt+T, or the right-click menu, the plugin now compares the active project's source language against the write termbase's source language and swaps source/target text when they are inverted (e.g. working in a NL→EN project but writing to an EN→NL termbase); previously terms were silently inserted in the wrong direction

---

## [4.10.9] – 2026-03-18

### Changed
- **Lavender chip colour for abbreviation matches** – TermLens chips that matched via a source abbreviation now render with a light lavender background instead of the regular blue, making them instantly distinguishable from full-term matches; the shortcut badge on abbreviation chips is purple to match

---

## [4.10.8] – 2026-03-18

### Fixed
- **Smart selection no longer swallows the next word when selection has trailing space** – selecting a single word with a trailing space (e.g. by shift+arrow-key overshoot) now correctly adds just that word to the termbase; previously the expansion algorithm would land past the space and consume the following word (e.g. "trimethoxysilaan of" instead of "trimethoxysilaan")

---

## [4.10.7] – 2026-03-18

### Added
- **Persistent chat history** – the AI Assistant conversation is now saved to disk after each message and restored automatically when Trados restarts; history persists until you click the **Clear** button

---

## [4.10.6] – 2026-03-18

### Added
- **Variable picker in Prompt Editor** – press **Ctrl+,** in the prompt content field to open a variable menu listing all available variables with descriptions; selecting one inserts it at the cursor (mirrors the variable insertion shortcut in the Trados Studio editor)

### Changed
- **CS checkbox replaces Case dropdown in TermLens settings** – the per-termbase case sensitivity control is now a compact checkbox column (header: **CS**) instead of a dropdown showing Insensitive on every row; ticked = case-sensitive, unticked = case-insensitive; the column sits alongside the existing Read/Write/Project checkboxes

---

## [4.10.5] – 2026-03-18

### Added
- **QuickLauncher built-in prompts** – three prompts now ship as built-ins and are created on first run (or via Restore): *Assess how I translated the current segment*, *Define*, and *Explain (in general)*

### Changed
- **Style guide prompts removed** – the five language-specific style guides (Dutch, English, French, German, Spanish) are no longer shipped as built-in prompts; users who want style guide prompts can create their own in the Prompts tab
- **Built-in prompts use `{{SOURCE_LANGUAGE}}`/`{{TARGET_LANGUAGE}}`** – all specialist prompt content updated from legacy `{source_lang}`/`{target_lang}` single-brace format to the current double-brace standard

### Fixed
- **Delete button label clipped in Prompts tab** – the Delete button was too narrow (55 px), causing the label to be cut off; widened to 65 px

---

## [4.10.4] – 2026-03-18

### Fixed
- **Prompts tab: double-click opens wrong prompt after column sort** – after sorting the prompt list by clicking a column header, double-clicking a row now opens the correct prompt; previously it used the visual row index, which diverged from the data list order after sorting
- **"Surrounding segments" spinner overlap** – the spinner for the Surrounding segments setting in AI Settings was positioned too close to its label and appeared partially overlapping it; moved right to give the label room

### Changed
- **`{{PROJECT}}` display in chat** – when a QuickLauncher prompt containing `{{PROJECT}}` is sent, the chat bubble now shows a compact summary (e.g. `[source document – 47 segments]`) instead of the full source document text; the complete text is still sent to the AI unchanged

---

## [4.10.3] – 2026-03-18

### Added
- **`{{PROJECT_NAME}}` variable** – replaced with the Trados project name in QuickLauncher prompts
- **`{{DOCUMENT_NAME}}` variable** – replaced with the active file name in QuickLauncher prompts
- **`{{SURROUNDING_SEGMENTS}}` variable** – replaced with N source segments before and after the active segment, numbered with their actual per-file Trados segment numbers and the active segment marked `← ACTIVE`; N is configurable in Settings → AI Settings → Surrounding segments (default: 5)
- **`{{PROJECT}}` variable** – replaced with all source segments in the active document, numbered with actual Trados segment numbers; multi-file projects include `=== File N ===` headers at file boundaries where segment numbering restarts
- **Surrounding segments setting** – new spinner in AI Settings: "Surrounding segments" (default: 5, range 1–20); controls both the `{{SURROUNDING_SEGMENTS}}` QuickLauncher variable and the context window in the AI Assistant chat

### Changed
- **AI Assistant surrounding context** – was previously hardcoded to 2 segments on each side; now uses the new "Surrounding segments" setting (default 5)

### Notes
- Segment numbers in `{{SURROUNDING_SEGMENTS}}` and `{{PROJECT}}` match the numbers shown in the Trados editor (per-file, 1-based); the same numbering logic used by the AI Proofreader results
- `{{PROJECT}}` is evaluated lazily – only when the prompt template actually contains `{{PROJECT}}`; it has no cost unless used
- Sending a 10,000-word patent as `{{PROJECT}}` to a Sonnet-class model costs approximately $0.04–0.05 per call

---

## [4.10.2] – 2026-03-17

### Changed
- **`quicklauncher_label` YAML field** – the optional short label for the QuickLauncher menu is now set with `quicklauncher_label:` in `.svprompt` frontmatter; the old name `quickmenu_label` still works as a backward-compatible alias

---

## [4.10.1] – 2026-03-17

### Added
- **`{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` variables** – renamed from `{{SOURCE_TEXT}}` / `{{TARGET_TEXT}}` for clarity; the old names continue to work as aliases
- **Ctrl+Q shortcut** – opens the QuickLauncher prompt menu directly from the keyboard; note that Trados's default "View Internally Source" shortcut must be removed first (File → Options → Keyboard Shortcuts)
- **QuickLauncher help page** – new documentation page covering variables, examples, and setup

### Fixed
- **QuickLauncher prompts appear immediately** – newly created or edited prompts now appear in the right-click menu without restarting Trados
- **Case column width** – the Case column in TermLens settings was too narrow to display "Insensitive" in full; widened to fit

---

## [4.10.0] – 2026-03-17

### Added
- **QuickLauncher** – new editor right-click menu entry listing all prompts marked as QuickLauncher; selecting a prompt fills in the current segment's source text, target text, selection, and language pair as variables and submits the expanded prompt directly to the Supervertaler Assistant chat, enabling one-click AI actions without switching panels
- **QuickLauncher prompt support** – prompts are marked as QuickLauncher by adding `sv_quickmenu: true` to their YAML frontmatter (compatible with the same flag used in Supervertaler Workbench), or by setting `category: QuickLauncher`; an optional `quickmenu_label:` field sets a short display name in the menu
- **Segment-level prompt variables** – `{{SOURCE_TEXT}}`, `{{TARGET_TEXT}}`, and `{{SELECTION}}` variables are now substituted in prompts at runtime using the current segment context (compatible with Supervertaler Workbench variable names)
- **Legacy category normalisation** – the old internal category name `quickmenu_prompts` is automatically normalised to `QuickLauncher` when loading prompt files, ensuring forward compatibility

---

## [4.9.0] – 2026-03-17 ([#4](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/4))

### Added
- **Unified user data folder** – Supervertaler for Trados now stores all data (settings, licence, projects, prompts) in a single shared folder alongside Supervertaler Workbench (default: `~/Supervertaler/`); the folder is configured via a shared `%APPDATA%\Supervertaler\config.json` pointer so both products automatically read from the same location
- **First-run setup dialog** – on first launch, a dialog lets you choose the data folder; if an existing Workbench installation is detected its path is pre-filled so you can share data immediately with one click
- **Automatic data migration** – existing settings, licence, project overlays, and custom prompts are copied from the old `%LocalAppData%\Supervertaler.Trados\` location to the new shared folder on first run; old files are left in place as a backup
- **Shared prompt library** – prompts are now read from and written to the shared `prompt_library/` folder; any prompt created in Workbench is immediately visible in the Trados plugin and vice versa, with no configuration required

---

## [4.8.1] – 2026-03-17

### Changed
- **Ctrl+Alt+T opens full Term Entry Editor** – pressing Ctrl+Alt+T to add a new term now opens the full Term Entry Editor dialog instead of the simple Add Term dialog, giving immediate access to definition, domain, notes, and synonym fields when adding a term

### Fixed
- **TermLens subscript/superscript matching** – terms containing Unicode subscript digits (₀–₉) or superscript digits (⁰¹²³⁴⁵⁶⁷⁸⁹) such as H₂O, CO₂, and mm² were not recognised in segments because the tokeniser split them at the script character; the word pattern now includes these Unicode ranges and index keys are normalised so matching works correctly
- **Context-aware help links** – F1 help in the Batch Operations and Reports tabs now opens the correct documentation page for the active context rather than falling back to the generic help home page

---

## [4.8.0] – 2026-03-16

### Added
- **AI Proofreader** – new batch proofreading mode in the Batch Operations tab; select "Proofread" to check translated segments for errors using AI; results appear in the new Reports tab as clickable issue cards with segment number, issue description, and suggestion; clicking a card navigates to the corresponding segment in the editor
- **Reports tab** – new tab in the Supervertaler Assistant panel displaying proofreading results; shows issue count, run duration, and a scrollable list of issue cards; Clear button to reset results
- **Proofread scopes** – five scope options for proofreading: Confirmed only, Translated + Confirmed, All segments, Filtered segments, and Filtered (confirmed only)
- **Segment navigation from reports** – clicking an issue card in the Reports tab navigates directly to the relevant segment in the Trados editor, using the segment's ParagraphUnitId and SegmentId for accurate navigation in multi-file projects
- **Per-file segment numbering** – issue cards in the Reports tab show the actual per-file segment number (matching the Trados editor grid) rather than a cross-file batch index
- **"Also add issues as Trados comments" checkbox** – in the Batch Operations tab (Proofread mode), option to insert proofreading issues as Trados segment comments alongside the Reports tab display
- **Prompt category filtering** – the prompt dropdown in Batch Operations now filters by mode: only "Translate" prompts appear in Translate mode, only "Proofread" prompts in Proofread mode

### Changed
- **Prompt file extension** – built-in and user prompts now use `.svprompt` file extension (previously `.md`), matching the Supervertaler desktop application; existing `.md` prompt files are still loaded for backward compatibility
- **Prompt YAML key renamed** – the `domain` key in prompt YAML frontmatter is now `category`; the parser accepts both for backward compatibility
- **Prompt categories renamed** – "Domain Expertise" and "Style Guides" categories are now "Translate"; "Proofreading" is now "Proofread"
- **Case sensitivity per-termbase dropdown simplified** – removed "Default" option; per-termbase case sensitivity is now simply "Insensitive" (default) or "Sensitive"

---

## [4.7.0] – 2026-03-16

### Added
- **Case-sensitive matching** – new global setting "Case-sensitive matching" (default: off) plus per-termbase override in the settings grid; when enabled, terms only match if the source text has the same letter case as the indexed term; per-termbase setting can be Sensitive or Insensitive
- **Mouse wheel scrolling in AI Assistant chat** – the chat message panel now supports mouse wheel scrolling; previously only the scrollbar worked

### Changed
- **Database schema migration** – `case_sensitive` column automatically added to the `termbases` table on first use; fully backward-compatible

---

## [4.6.0] – 2026-03-16

### Added
- **Abbreviation fields on term entries** – each term entry now has optional **Source Abbreviation** and **Target Abbreviation** fields; when a source abbreviation appears in a segment, TermLens highlights it and shows the target abbreviation underneath, with the full term pair available in the +N tooltip
- **Pipe-separated abbreviation variants** – abbreviation fields support multiple variants separated by `|` (e.g., `GC|G.C.|gc|g.c.`); each variant is indexed and matched independently, so all common forms of an abbreviation are recognised
- **Abbreviation-aware insertion** – clicking or Alt+digit-inserting an abbreviation-matched chip inserts the target abbreviation (first variant) instead of the full target term
- **Abbreviation in AI prompts** – AI translation prompts now include abbreviation pairs alongside their full terms, so the AI knows both forms
- **Abbreviation columns in Term Editor** – the Add/Edit Term dialog includes Source Abbreviation and Target Abbreviation text fields between the primary term fields and the synonyms section
- **Abbreviation columns in Termbase Editor** – the termbase grid shows SrcAbbr and TgtAbbr columns for viewing and editing abbreviations inline

### Changed
- **Database schema migration** – `source_abbreviation` and `target_abbreviation` columns automatically added to existing databases on first write; fully backward-compatible with older Supervertaler databases

---

## [4.5.0] – 2026-03-16

### Added
- **Ctrl+T quick translate** – press **Ctrl+T** to instantly translate the active segment using the same provider, model, and prompt configured in the Batch Translate tab; the translation is applied directly to the target cell, with full tag preservation for segments containing inline formatting; also available via right-click context menu ("Translate active segment"); rebindable in Trados Studio's keyboard shortcut settings
- **AI Settings link in Batch Translate tab** – clickable "AI Settings…" link below the provider display opens the Settings dialog directly on the AI Settings tab for quick access to provider, model, API key, and AI context configuration

### Changed
- **Ctrl+Alt+A retired** – the old standalone single-segment translation shortcut has been unbound; the action is kept for backward compatibility but now redirects to the same Ctrl+T batch-translate pipeline with full tag support

### Fixed
- **Tagged segments not applied in batch translate** – segments containing inline Trados tags (bold, italic, field codes, page numbers, etc.) were translated by the AI but the translation was never written back to the target; the reconstructed target was written to the document model but the Trados editor's own buffer overwrote it with the old (empty) content; now uses the Trados `ProcessSegmentPair` API which handles the edit transaction correctly
- **Last segment in batch sometimes lost** – the final segment translated in a batch could silently lose its translation because no subsequent segment navigation forced the Trados editor to commit the pending edit; the new `ProcessSegmentPair` approach bypasses the editor buffer entirely for tagged segments, and non-tagged segments continue to use the proven `Selection.Target.Replace` path

---

## [4.4.0] – 2026-03-16

### Added
- **Tag-aware AI translation** – segments containing inline Trados tags (bold, italic, field codes, etc.) are now fully supported for both Batch Translate and single-segment AI translation (Ctrl+Alt+A); previously, tags were silently stripped and lost in the target
- **SegmentTagHandler** – new serialization/reconstruction engine that converts Trados `ITagPair` and `IPlaceholderTag` objects into numbered placeholders (`<t1>...</t1>`, `<t2/>`), sends them through the LLM translation pipeline, then reconstructs the target segment with the original tag objects cloned and repositioned to match the translated word order
- **Graceful fallback** – if the LLM drops or corrupts tag placeholders, the plugin falls back to plain-text insertion (stripping placeholders) instead of failing silently

### Improved
- **Translation prompt tag instructions** – replaced generic CAT tool tag preservation instructions with specific numbered placeholder format and examples, improving LLM tag preservation accuracy

---

## [4.3.0] – 2026-03-14

### Added
- **Per-project settings** – switching between Trados projects now automatically saves and restores the Supervertaler database path, enabled/disabled termbases, write targets, project termbase, and AI context termbase filters; settings are stored per-project in `%LocalAppData%\Supervertaler.Trados\projects\` and applied automatically when the active document changes
- **Per-project settings documentation** – new help page documenting how per-project settings work, what's saved per-project vs globally, and how the automatic switching behaves
- **Privacy policy** – [supervertaler.com/privacy](https://supervertaler.com/privacy/)

### Fixed
- **"Add & Edit" crash in Similar Term Found dialog** – pressing "Add & Edit" when merging a term caused an `ArgumentOutOfRangeException` (ordinal 10) because `GetTermById()` had an off-by-one error in its column indexing for optional fields (domain, notes, is_nontranslatable); each field was read one position past its actual column index, and the last field fell off the end of the result set
- **Licence null-status crash** – when the Lemon Squeezy API returned a null or empty `status` field during activation, the licence was treated as invalid even though the key was activated; now treats null status as active when the licence has a valid instance ID
- **Trial period mismatch** – `LicenseInfo.cs` used a hardcoded 14-day trial window while `LicenseManager.cs` used 90 days; unified both to the 90-day constant
- **AI Settings termbase list stale after database switch** – switching Supervertaler databases in the TermLens settings tab didn't update the AI Settings tab's termbase checklist until the dialog was closed and reopened; the AI context panel now refreshes immediately when the termbase list changes
- **Term Picker shortcut documented incorrectly** – the About dialog and help docs showed `Ctrl+Shift+G` but the actual shortcut is `Ctrl+Alt+G`; corrected all references

### Improved
- **Keyboard shortcuts documentation** – added Mac/Parallels equivalents (Ctrl → Control, Alt → Option) to all shortcut tables for users running Trados in Parallels
- **Support email** – updated to support@supervertaler.com
---

## [4.2.2-beta] – 2026-03-13

### Fixed
- **Licence tab help link** – the ? button on the Licence tab now opens the Licensing & Pricing page instead of incorrectly opening TermLens Settings
- **Backup tab help link** – the ? button on the Backup tab now opens the dedicated Backup & Restore page instead of using a stale anchor link
- **Licensing help URL** – corrected the GitBook URL slug from `licensing-and-pricing` (404) to `licensing` (the actual filename-based slug)

### Changed
- **UK English in documentation** – changed all instances of "license" (US) to "licence" (UK) in the online help pages

### Added
- **Example Project link in help menus** – both the TermLens and Supervertaler Assistant help menus (? button) now include an "Example Project" link that opens the documentation page for the downloadable example project
- **Example Project documentation** – new docs page with step-by-step instructions, screenshots, and the example package (patent translation with termbase, TM, and MultiTerm termbase)
- **Help link reference** – new `HELP-LINKS.md` in the repo root documents every help link in the plugin with its online URL and which UI element triggers it

---

## [4.2.1-beta] – 2026-03-12

### Improved
- **Settings toolbar buttons** – the TermLens tab toolbar buttons now use descriptive labels ("+ Add", "− Remove") instead of cryptic symbols; all five buttons (Open, Export, Import, + Add, − Remove) have tooltips explaining their function

### Fixed
- **"Max segments" label overlap** – the "Max segments:" label in AI Settings no longer runs into the number input box

---

## [4.2.0-beta] – 2026-03-12

### Added
- **Update checker** – on startup, the plugin checks GitHub Releases for a newer version and shows a dialog with Download, Skip This Version, and Remind Me Later buttons. Checks once per session, respects skipped versions, and never blocks Trados startup (runs in background)

---

## [4.1.0-beta] – 2026-03-12

### Added
- **Settings backup and restore** – new **Backup** tab in the Settings dialog with Export and Import buttons; export saves all plugin settings (termbase paths, toggle states, font size, shortcut preferences, AI provider keys, model selections, prompt configuration) to a JSON file; import validates the file, creates an automatic backup of current settings, and applies the imported configuration immediately
- **Open settings folder** – clickable link in the Backup tab opens the `%LocalAppData%\Supervertaler.Trados\` folder in Explorer for easy access to settings files
- **Open prompts folder** – clickable link in the Prompts tab opens the `%LocalAppData%\Supervertaler.Trados\prompts\` folder in Explorer

### Fixed
- **Restore button clipped in Prompts tab** – the "Restore" button width was too narrow, causing the label to be truncated

---

## [4.0.2-beta] – 2026-03-12

### Added
- **Dual-mode Alt+digit term shortcuts** – two configurable shortcut styles for inserting terms 10+ (choose in Settings > TermLens > Term shortcuts):
  - **Sequential** (default) – type the term number digit by digit: Alt+4, Alt+5 inserts term 45. Clean sequential badge numbers (10, 11, 12, ...). 1-second timer between digits.
  - **Repeated digit** – press the same digit key multiple times: Alt+5, Alt+5 inserts term 14. Supports up to 5 tiers (45 terms). No timer ambiguity.
- **Term Picker wrap-around navigation** – pressing Down on the last term jumps to the first, and Up on the first jumps to the last

### Changed
- **Term Picker numbering** – the Term Picker now always uses plain sequential numbers (1, 2, 3, ...) regardless of the shortcut style setting, since navigation is done with arrow keys and Enter

---

## [4.0.1-beta] – 2026-03-12

### Fixed
- **Merge dialog buttons clipped** – the "Similar Term Found" dialog's button bar (Add as Synonym, Add & Edit, Keep Both, Cancel) was invisible or partially clipped inside Trados's WPF-hosted plugin environment; replaced the Dock-based panel layout with flat absolute positioning so buttons render reliably at any DPI
- **Merge dialog button text truncated** – widened the "Add as Synonym" and "Add & Edit..." buttons so their labels are no longer cut off

### Added
- **Merge dialog button tooltips** – each button now shows a tooltip on hover explaining what it does

---

## [4.0.0-beta] – 2026-03-12

### Changed
- **90-day free trial** – extended from 14 days; no credit card required, no sign-up
- **Support & Community link** in About dialog now points to `supervertaler.com/trados/#support` (Groups.io mailing list, ProZ forum, GitHub Issues) instead of directly to GitHub Issues; future-proofed so support channels can be updated without rebuilding the plugin
- **Version display** – About dialog now shows the full informational version string (e.g. "4.0.0-beta") rather than the numeric assembly version

### Fixed
- **Shield emoji clipping** – "Source code available for security audit" link in the About dialog was partially obscured by the shield emoji; offset increased to prevent overlap
- **Tooltips on About dialog links** – Documentation and Support & Community links now show tooltips on hover

---

## [4.0.0] – 2026-03-11

### Added
- **Licensing system** – Lemon Squeezy-powered license key activation with two paid tiers: **TermLens** (€10/month – terminology features) and **TermLens + Supervertaler Assistant** (€15/month – all features including AI); 14-day free trial with full access on first install; 30-day offline cache for validation; 2 machine activations per key
- **License tab in Settings** – dedicated tab for entering license keys, activating/deactivating licenses, verifying license status, and managing subscriptions; shows trial countdown, plan name, masked key, and last verification date
- **License status in About dialog** – color-coded license status (blue for trial, green for active, red for expired) with a clickable link that opens the License settings tab directly
- **Feature gating** – TermLens panel and terminology actions require Tier 1+; AI Assistant panel and AI translate action require Tier 2; graceful overlays and messages guide users to purchase or upgrade
- **Security transparency** – "Source code available for security audit" link in the About dialog with tooltip explaining the plugin's network behaviour; links to the public GitHub repository
- **Enhanced AI Assistant context** – the AI chat assistant now sees the full document content (all source segments) so it can determine the document type (legal, medical, technical, etc.) and provide context-appropriate assistance; also includes project/file metadata, surrounding segments, and term definitions/domains/notes
- **AI Context settings** – three new settings in AI Settings: "Include full document content" (with configurable max segments), and "Include term definitions and domains"

### Changed
- **About dialog** – removed duplicate "Plugin Help" link (Documentation link remains); added clickable license status that opens Settings → License tab; added security audit note with GitHub link

### Fixed
- **Settings sync between panels** – changing settings from the TermLens gear icon now immediately reflects in the AI Assistant panel and vice versa; previously each panel had its own in-memory copy that could get out of sync

---

## [3.4.2] – 2026-03-10

### Added
- **Merge prompt for similar terms** – when adding a term whose source or target already exists in the termbase (but with a different translation), a dialog offers to add the new text as a synonym instead of creating a near-duplicate entry; works with Alt+Down, Alt+Up, and Ctrl+Alt+T
- **"Add & Edit" option in merge dialog** – alongside the quick "Add as Synonym" button, the merge dialog now offers "Add & Edit…" which merges the synonym and opens the full Term Entry Editor so the user can review or add metadata before closing
- **Term metadata in tooltips** – hovering over a term chip now shows Domain and Notes fields alongside Definition (previously only Definition was displayed)
- **Metadata indicator on badges** – the shortcut badge number on term chips turns black (instead of white) when the term has metadata (definition, domain, or notes), giving a visual cue to hover for more info

### Changed
- **"MultiTerm Help"** – renamed the context menu item from "MultiTerm Support" to "MultiTerm Help" for consistency
- **"Supervertaler Assistant Help"** – renamed the AI Assistant help menu item from "Assistant Help" to "Supervertaler Assistant Help"
- **Dialog title casing** – "Edit Term Entry" and "Add Term Entry" renamed to sentence case ("Edit term entry" / "Add term entry")

### Fixed
- **Shift+Enter in AI Assistant** – Shift+Enter now correctly inserts a newline in the chat input instead of being intercepted by Trados Studio; uses a thread-local `WH_GETMESSAGE` hook to intercept the key press before Trados's message filters can consume it
- **Paste newlines in AI Assistant** – pasting text with bare `\n` line endings (e.g. copied from Trados segments) now displays correctly; the chat input normalises `\n` to `\r\n` on paste
- **Smart selection expansion** – partial word selections now expand to the shortest matching word at the boundary instead of the longest, preventing over-expansion when selecting near short words (e.g. selecting "o" no longer expands to "output" when "of" is adjacent)
- **Merge dialog cutoff** – the "Similar Term Found" dialog is now wider (520Ã–310) to prevent text truncation on longer term pairs

---

All notable changes to Supervertaler for Trados (formerly TermLens) will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/).

---

## [3.4.1] – 2026-03-10

### Added
- **Select All / Deselect All** links for termbases in AI Settings → AI Context section

### Fixed
- **Settings TextBox overlap** – the database file path TextBox no longer extends over the "Create New..." button when the settings dialog is resized

---

## [3.4.0] – 2026-03-09

### Added
- **MultiTerm termbase support** – TermLens now automatically detects MultiTerm .sdltb
  termbases attached to the active Trados project and displays their terms alongside
  Supervertaler terms; MultiTerm terms appear as green chips in the TermLens panel
- **Read-only MultiTerm terms** – MultiTerm terms are read-only: right-click context menus
  do not show Edit, Delete, or Non-Translatable options for green (MultiTerm) chips;
  tooltips show "[MultiTerm – read-only]"
- **MultiTerm in settings** – detected MultiTerm termbases appear in the Supervertaler
  Settings dialog with a "[MultiTerm]" label and light green row tint; Read checkbox
  toggles visibility; Write and Project columns are always disabled (read-only)
- **Auto-refresh on termbase changes** – when terms are added or removed from a MultiTerm
  .sdltb termbase (e.g. via Trados's native Term Recognition panel), TermLens automatically
  detects the file modification and reloads terms on the next segment change
- **JET 4.0 / ACE OLEDB driver support** – .sdltb files are opened via the built-in
  Microsoft.Jet.OLEDB.4.0 driver (available in all 32-bit Windows processes) with fallback
  to ACE OLEDB 12.0–16.0; no additional driver installation required for Trados Studio
  (which runs as an x86 process)
- **API fallback** – if no OleDb driver can open an .sdltb file, the plugin attempts to
  use Trados's built-in ITerminologyProviderManager API for per-segment term search with
  LRU caching (200 segments)

### Changed
- **Cleaned up MultiTerm diagnostic logging** – removed verbose reflection-based logging
  from the MultiTerm detection and fallback provider code; the multiterm_debug.log file is
  no longer written

---

## [3.3.3] – 2026-03-09

### Added
- **Help button on dialogs** – the Termbase Editor and Edit Term Entry dialogs now show a
  `?` button in the title bar that opens the relevant online help page (matching the pattern
  already used by the Supervertaler Settings dialog)

---

## [3.3.2] – 2026-03-09

### Added
- **Context-sensitive help** – the `?` button on TermLens and Supervertaler Assistant
  panels now opens a dropdown menu with a direct link to the relevant online help page
  and an "About" option; F1 opens contextual help from every dialog (settings, Add Term,
  Term Picker, Termbase Editor, Prompt Editor, Bulk Add NT)
- **HelpSystem** – new `Core/HelpSystem.cs` provides a centralized topic registry and
  URL launcher for all help pages

### Changed
- **Help URL slug** – documentation URLs updated from `gitbook.io/superdocs` to
  `gitbook.io/help` for a cleaner, more intuitive path
- **About dialog access** – the `?` button now shows a dropdown instead of directly
  opening About; About is still accessible via the dropdown menu

---

## [3.3.1] – 2026-03-08

### Added
- **Resizable chat input** – drag the top edge of the chat input area upward to make it
  taller when composing multi-line messages with Shift+Enter; drag down to shrink it back

### Fixed
- **Settings dialog too wide** – the Supervertaler Settings window could become excessively
  wide and extend off-screen; now capped at 800px maximum width, and persisted size is
  validated on restore
- **Chat spacing** – removed remaining double-spacing in AI responses caused by duplicate
  paragraph marks in table rendering
- **Termbases list in AI Settings** – the CheckedListBox no longer stretches the dialog
  horizontally; long termbase names scroll within the list via horizontal scrollbar

---

## [3.3.0] – 2026-03-08

### Added
- **AI Assistant** – project-aware chat interface in a separate dockable Trados panel;
  supports multi-turn conversations with full context from the active segment (source,
  target, termbase terms, TM matches); responses render as Markdown with headings, bold,
  italic, inline code, code blocks, tables, and lists; right-click to copy or apply
  suggestions directly to the target segment
- **Image attachments in chat** – paste images from clipboard (Ctrl+V), drag and drop
  image files, or browse with the attach button; thumbnails appear in an attachment strip
  below the input; images are sent to the AI using each provider's vision/multimodal API
  (OpenAI, Claude, Gemini, Ollama); click thumbnails in chat bubbles to view full-size
- **AI context control** – new "AI Context" section in AI Settings lets you choose which
  termbases contribute terms to AI prompts (independent of TermLens display settings) and
  toggle whether TM (Translation Memory) fuzzy matches are included in the AI context
- **TM match integration** – when enabled in settings, TM fuzzy matches for the active
  segment are included in the AI Assistant's system prompt, showing match percentage,
  source/target text, and TM name so the AI can leverage existing translations
- **Ollama support for AI Assistant** – local Ollama models can be used for the chat
  assistant with configurable endpoint
- **Custom OpenAI-compatible endpoints** – profile-based configuration for any
  OpenAI-compatible API (e.g., Azure OpenAI, LM Studio, vLLM); multiple profiles
  supported with separate endpoint, model, and API key per profile
- **Chat tooltips** – all chat input buttons (Send, Stop, Clear, Attach) now show
  descriptive tooltips explaining their function and keyboard shortcuts

### Changed
- **Attachment icon** – replaced the paperclip emoji (📎) with a clearer photo icon
  from Segoe MDL2 Assets for better visibility in the chat input area
- **Chat rendering** – eliminated extra blank lines between paragraphs in AI responses
  for more compact, readable output
- **Shift+Enter for newlines** – the chat input now supports Shift+Enter to insert line
  breaks without sending the message (Enter alone sends)
- **AI Settings layout** – the AI Context section now repositions dynamically based on
  the selected provider, eliminating wasted space when provider-specific panels (Ollama,
  Custom OpenAI) are hidden; the termbases checklist is taller to show more entries
  without scrolling

### Fixed
- **TermLens header text cutoff** – the word count and match summary in the TermLens
  panel header is no longer truncated by the floating gear and help buttons; added right
  padding to account for the button overlay

---

## [3.2.0] – 2026-03-08

### Added
- **Help / About dialog** – "?" button next to the settings gear opens an About dialog
  showing plugin version, author info, keyboard shortcuts reference, and links to
  website, documentation, and support; email address copies to clipboard on click
- **NT filter in Termbase Editor** – "NT only" checkbox in the toolbar filters the
  term list to show only non-translatable entries; composes with the search filter
- **Bulk Add NT** – "Bulk Add NT" button in the Termbase Editor opens a dialog where
  you can paste multiple non-translatable terms (one per line) for batch import;
  reports how many were added and how many duplicates were skipped
- **Copy cell in Termbase Editor** – Ctrl+C now copies the current cell value instead
  of the entire row; right-click context menu includes a "Copy cell" option
- **Duplicate prevention** – all term insert and update paths now check for existing
  entries with the same source and target term (case-insensitive) in the same
  termbase; quick-add shortcuts (Alt+Down/Up, Ctrl+Alt+T, Ctrl+Alt+N) show a clear
  message when a duplicate is detected; bulk operations report how many duplicates
  were skipped

### Changed
- **Renamed "glossary" to "termbase"** – all user-facing labels, context menus,
  dialogs, and settings now use "termbase" consistently instead of the previous mix
  of "glossary" and "termbase"
- **Shortened language names** – language pair displays throughout the UI
  (Termbase Editor title bar, settings grid, Add Term dialog) now show short names
  like "English" instead of "English (United States)"
- **Sentence case context menus** – right-click menu items in the TermLens panel now
  use sentence case ("Mark as non-translatable") instead of title case
- **Settings dialog database label** – the file path label in settings now reads
  "Database" instead of "Termbase" to avoid confusion with individual termbases
  inside the database

### Fixed
- **Alt+Up word expansion** – quick-add to project termbase (Alt+Up) now expands
  partial word selections to full word boundaries, matching Alt+Down behaviour

---

## [3.1.0] – 2026-03-06

### Added
- **Prompt manager / library** – 14 built-in prompts (domain expertise for Medical,
  Legal, Patent, Financial, Technical, Marketing, IT; style guides for Dutch, English,
  French, German, Spanish; project prompts for professional tone and formatting);
  prompts stored as Markdown files with YAML frontmatter, compatible with Supervertaler
  desktop prompt format
- **Prompt selector in Batch Translate** – dropdown between Scope and Provider lets you
  pick a prompt before translating; selected prompt persists across sessions
- **Prompts tab in Settings** – third tab in the Settings dialog with system prompt
  viewer/editor and full prompt library management (create, edit, delete, restore
  built-in prompts)
- **Composable prompt assembly** – base system prompt (tag preservation, number
  formatting) + custom prompt (domain/style instructions) + glossary terms; custom
  system prompt override available for advanced users
- **Supervertaler desktop prompt discovery** – automatically scans
  `~/Supervertaler_Data/` and `%AppData%\Supervertaler\` for shared prompt libraries
- **Variable substitution** – prompts support `{source_lang}`, `{target_lang}`,
  `{{SOURCE_LANGUAGE}}`, `{{TARGET_LANGUAGE}}` placeholders, replaced at translation
  time with the document's language pair

### Changed
- **Prompts tab side-by-side layout** – the Settings dialog Prompts tab now shows the
  custom prompt library on the left and the system prompt on the right, making better
  use of the available space
- **Prompt variable display simplified** – prompt editor shows only the standard
  `{{SOURCE_LANGUAGE}}` / `{{TARGET_LANGUAGE}}` placeholders; legacy `{source_lang}` /
  `{target_lang}` aliases still work silently for backward compatibility

### Fixed
- **TermLens glossary list no longer cut off** – the TermLens settings tab now uses
  Dock-based panel layout instead of absolute pixel positioning, so the glossary grid
  scales correctly across screen resolutions and DPI settings
- **Prompt library Source column resizable** – the Source column in the prompt list now
  uses proportional FillWeight sizing instead of a fixed width
- **Plugin manifest version updated** – `plugin.xml` now reports v3.1.0 (was stuck at
  2.0.1 since the rename)
- **Windows on ARM support** – the plugin now works on Windows on ARM (Parallels on
  Apple Silicon Macs, Surface Pro X, etc.); ships ARM64 native SQLite binary alongside
  x64 and x86; properly detects process architecture and copies the correct native
  library where SQLitePCLRaw can find it
- **SQLitePCLRaw initialization order** – `AssemblyResolve` handler is now registered
  before native library preloading, and `Batteries_V2.Init()` is called explicitly to
  prevent `TypeInitializationException` on non-standard environments
- **Improved error diagnostics** – database creation errors now show the full inner
  exception chain for easier troubleshooting

---

## [3.0.0] – 2026-03-06

### Added
- **AI batch translation** – translate segments in bulk using LLM providers; supports
  OpenAI (GPT-4o, GPT-4o mini, o1, o3-mini), Anthropic (Claude 3.5 Sonnet, Haiku,
  Opus), and Google (Gemini 2.0 Flash, Gemini 1.5 Pro); configurable via the new AI
  Settings panel accessible from the Batch Translate tab
- **AI single-segment translate** – press **Ctrl+Alt+A** or right-click → "AI Translate
  Current Segment" to translate just the active segment using the configured AI provider
- **Glossary-aware AI prompts** – AI translations automatically include matched
  terminology from your TermLens glossaries in the prompt, so the AI respects your
  approved terms, including non-translatable terms
- **Four batch translate scopes** – "Empty segments only" (default), "All segments",
  "Filtered segments", and "Filtered (empty only)"; filtered scopes translate only
  segments visible in Trados's advanced display filter
- **Live filtered segment counts** – the Batch Translate tab updates segment counts
  in real time when you change the Trados display filter
- **AI Settings panel** – configure provider, model, API key, and temperature directly
  in the Batch Translate tab; settings persist across sessions
- **Batch translate progress** – real-time log panel shows translation progress,
  segment-by-segment results, and any errors; cancel button to stop mid-batch

### Changed
- **Batch Translate tab** – no longer a placeholder; fully functional with scope
  selector, segment counts, translate/cancel buttons, and scrollable log panel
- **AI Settings integrated into Settings dialog** – the gear icon in TermLens now
  opens a tabbed settings dialog with separate tabs for Glossary and AI configuration

---

## [2.1.0] – 2026-03-06

### Added
- **Non-translatable terms** – mark terms as non-translatable (brand names, product
  codes, abbreviations that stay the same across languages); the source term is copied
  verbatim as the target
- **Ctrl+Alt+N quick-add shortcut** – select text in the source or target column and
  press Ctrl+Alt+N to instantly mark it as non-translatable in all Write glossaries
- **Right-click toggle** – right-click any term block and choose "Mark as
  Non-Translatable" or "Mark as Translatable" to toggle the flag without opening a
  dialog
- **Non-translatable checkbox in Add Term dialog** – when checked, the target field
  auto-fills with the source text and becomes read-only
- **Yellow visual distinction** – non-translatable terms appear with a light yellow
  background (#FFF3D0) in the TermLens panel, the Term Picker popup, and the Glossary
  Editor; color precedence: yellow (non-translatable) > pink (project) > blue (regular)
- **NT column in Glossary Editor** – checkbox column to view and toggle
  non-translatable status per term
- **Select/deselect all in Settings** – click the Read, Write, or Project column
  headers to toggle all checkboxes at once; tooltips explain the feature

### Changed
- **Database schema migration** – the `is_nontranslatable` column is automatically
  added to existing databases on first access; fully backward-compatible

---

## [2.0.1] – 2026-03-05

### Changed
- **Faster quick-add term workflow** – Alt+Down and Alt+Up now use incremental
  in-memory index updates instead of reloading the entire termbase database;
  batch inserts use a single SQLite transaction instead of one connection per
  glossary; right-click edit and delete also use the incremental path
- **License changed to source-available** – source code remains viewable and
  forkable for personal use; binary redistribution restricted to copyright holder

---

## [2.0.0] – 2026-03-05

### Added
- **Tabbed ViewPart UI** – the plugin now uses a tabbed panel with separate tabs for
  TermLens (glossary), AI Assistant, and Batch Translate; AI features are placeholder
  tabs that will be implemented in upcoming releases

### Changed
- **Renamed from TermLens to Supervertaler for Trados** – the plugin is now part of the
  Supervertaler product family; the TermLens glossary panel retains its name as a feature
  within the larger plugin
- **New assembly name** – `Supervertaler.Trados.dll` (was `TermLens.dll`); namespace changed
  from `TermLens` to `Supervertaler.Trados`
- **New plugin identity** – Trados treats this as a new plugin; users upgrading from TermLens
  should uninstall the old plugin first
- **Settings auto-migration** – settings are automatically copied from the old
  `%LocalAppData%\TermLens\` location to `%LocalAppData%\Supervertaler.Trados\` on first run

### Fixed
- **Word alignment in TermLens panel** – unmatched words now align vertically with
  matched term source text (fixed margin/padding mismatch and switched to consistent
  GDI+ text rendering)

---

## [1.6.0] – 2026-03-05

### Added
- **F2 expand selection to word boundaries** – press F2 after making a rough
  partial text selection in the source or target pane; the selection automatically
  expands to encompass the complete words at each end (e.g. selecting "et recht"
  becomes "het rechtstreeks")
- **Smart word expansion for term adding** – the Add Term dialog and Quick Add
  Term action now auto-expand partial selections to full word boundaries before
  populating the term pair, so you no longer need pixel-perfect text selection
- **Multiple Write glossaries** – the Write column in Settings now allows checking
  multiple glossaries; new terms are inserted into all Write-checked glossaries at
  once

### Changed
- **Term Picker shortcut** – changed from Ctrl+Shift+G to **Ctrl+Alt+G**
- **Quick Add action renamed** – "Quick add term to glossaries set to 'Read'" →
  "Quick Add Term to Glossary Set to 'Write'" (reflecting its actual behaviour)

### Fixed
- **Duplicate terms in Term Picker** – when the same source term matched at
  multiple positions in a segment (e.g. "cap" appearing twice), it was listed
  multiple times in the picker; matches are now deduplicated and renumbered
  sequentially

---

## [1.5.0] – 2026-03-04

### Added
- **Standalone database creation** – "Create New…" button in Settings creates a fresh
  Supervertaler-compatible SQLite database from scratch, so TermLens can function
  independently without Supervertaler installed
- **Glossary management** – "+" and "−" buttons in Settings to create and delete
  individual glossaries inside a database; new glossary dialog collects name, source
  language, and target language
- **TSV import** – bulk import terms from tab-separated files matching Supervertaler's
  format (pipe-delimited synonyms, `[!forbidden]` markers, UUID-based duplicate
  detection); flexible header mapping supports multiple column name conventions
- **TSV export** – export all terms from a glossary to the same TSV format, so files
  are fully interchangeable between Supervertaler and TermLens
- **Alt+Down quick-add shortcut** – adds the current source/target text directly to
  the Write glossary (replaces the previous Ctrl+Alt+Shift+T binding)
- **Alt+Up quick-add to project glossary** – new action that adds the current
  source/target text directly to the Project glossary (no dialog)

### Changed
- **Project column is now single-select** – the Project column in Settings uses
  radio-button behavior (only one glossary can be the project glossary at a time),
  matching the single Write glossary pattern
- **Context menu reorganised** – the "Add Term to TermLens" actions are now grouped
  under a separator in the editor context menu, with clearer names ("Add Term to
  TermLens (dialogue)" and "Quick add Term to glossaries set to 'Read'")
- **A+/A− button font sizes** – adjusted for better visual balance (A+ uses 9pt,
  A− uses 7pt instead of both using 7.5pt)

### Fixed
- **Term block text truncation** – TermBlock now recalculates its size when the font
  changes (via `OnFontChanged` override), preventing clipped text after A+/A− resizing

---

## [1.4.0] – 2026-03-04

### Added
- **Adjustable font size** – A+ and A− buttons in the TermLens panel header let you
  increase or decrease the font size on the fly while working; also configurable via a
  "Panel font size" control in the Settings dialog; size persists across Trados restarts
- **Dialog size persistence** – the Term Picker dialog remembers its window size and
  column widths between invocations (and across Trados restarts); the Settings dialog
  also remembers its window size

### Changed
- **Subtler expand indicator in Term Picker** – replaced the ► symbol next to source
  terms with a small ▸ triangle in the # column; less visually distracting while still
  indicating which rows have expandable synonyms
- **Double-digit shortcut badges** – numbers 10+ in the TermLens panel now use a
  pill-shaped (rounded rectangle) badge instead of a circle, so double-digit numbers
  are no longer clipped
- **Wider Project column** – increased from 62 px to 72 px in the Settings dialog so
  the "Project" header is no longer truncated

---

## [1.3.0] – 2026-03-04

### Added
- **Alt+digit term insertion** – press Alt+1 through Alt+9 to instantly insert the
  corresponding matched term into the target segment; Alt+0 inserts term 10; for
  segments with 10+ matches, two-digit chords are supported (e.g. Alt+1 then 3
  within 400ms inserts term 13)
- **Term Picker dialog** – press Ctrl+Shift+G to open a modal dialog listing all
  matched terms for the current segment; select by clicking, pressing Enter, or
  typing the term number
- **Synonym expansion in Term Picker** – rows with multiple target translations
  show a ► indicator; press Right arrow to expand and reveal all alternative
  translations, Left arrow to collapse
- **Bulk synonym loading** – target synonyms from the `termbase_synonyms` table are
  now loaded at startup alongside term entries, so the +N badges and Term Picker
  expansion show the correct synonym counts
- **Project glossary column in Settings** – a new "Project" checkbox column in the
  settings dialog lets you mark glossaries as project glossaries; project terms are
  shown in pink, all others in blue (replaces the previous database-driven priority
  colouring which was unreliable)

### Changed
- **Coloring is user-controlled** – pink/blue term colouring is now determined by
  the user's "Project" setting per glossary, not by the database's ranking or
  is_project_termbase fields
- **Wider settings columns** – the Read, Write, and Project checkbox columns in the
  settings dialog are now wide enough for their headers to be fully visible

---

## [1.2.0] – 2026-03-04

### Added
- **Add Term to TermLens** – right-click context menu action in the Trados editor to
  add a new term from the active segment's source and target text; opens a confirmation
  dialog where you can edit the term pair and optionally add a definition before saving
- **Quick add Term to TermLens** – a second context menu action that bypasses the dialog
  and saves the source/target text directly as a new term for faster workflow
- **Keyboard shortcuts** – Add Term defaults to Ctrl+Alt+T, Quick Add to
  Ctrl+Alt+Shift+T (both reassignable via Trados keyboard shortcut settings)
- **Settings: Read/Write columns** – the termbase list in settings is now a grid with
  separate Read and Write checkboxes; Read controls which termbases are searched,
  Write selects the single termbase that receives new terms (radio-button style)

### Changed
- **ViewPart docks above the editor** – TermLens now opens above the translation grid
  (previously docked at the side) and opens pinned/visible instead of auto-hidden
- **Term badge sizing** – the "+N" synonym count badges on term blocks are no longer
  truncated; width calculations now use ceiling rounding instead of integer truncation

---

## [1.1.0] – 2026-03-04

### Changed
- **Renamed project from Termview to TermLens** – all files, namespaces, class names,
  plugin IDs, settings paths, and documentation updated consistently
- **Migrated from System.Data.SQLite to Microsoft.Data.Sqlite** – eliminates the
  `EntryPointNotFoundException` caused by version-fingerprint hash conflicts in Trados
  Studio's plugin environment; uses SQLitePCLRaw with `e_sqlite3.dll` instead of
  `SQLite.Interop.dll`
- Settings path moved from `%LocalAppData%\Termview\` to `%LocalAppData%\TermLens\`
- Updated README with richer description and build instructions

### Technical
- `AppInitializer` now pre-loads `e_sqlite3.dll` by full path via `LoadLibrary` and
  registers `AssemblyResolve` for all managed DLLs we ship (Microsoft.Data.Sqlite,
  SQLitePCLRaw, System.Memory, System.Buffers, etc.)
- `pluginpackage.manifest.xml` Include entries updated to match new dependency set

---

## [1.0.0] – 2026-03-03

First public release.

### Added
- **Word-by-word source segment display** – every word of the active source segment
  is shown in a flowing left-to-right layout, updated as you navigate between segments
- **Terminology highlighting** – words that match a loaded termbase are shown in
  a coloured block (blue for regular terms, pink for project termbases) with the
  target-language translation displayed directly underneath
- **Multi-word term matching** – multi-word entries (e.g. "machine translation") are
  matched and highlighted as a single block, taking priority over single-word matches
- **Click to insert** – clicking a term block inserts the target translation at the
  cursor position in the target segment
- **Termbase settings** – gear button (⚙) in the panel header opens a settings
  dialog for selecting a Supervertaler termbase (`.db`) file; settings are saved to
  `%LocalAppData%\TermLens\settings.json` and the termbase is auto-loaded on startup
- **Auto-detect** – if no termbase is configured, TermLens automatically checks the
  default Supervertaler data directories (`~/Supervertaler_Data/resources/` and
  `%LocalAppData%\Supervertaler/resources/`)
- **Live termbase preview** – the settings dialog shows the termbase name, total
  term count, and source/target language pair after a file is selected

### Technical
- Reads Supervertaler's SQLite termbase format (`supervertaler.db`) directly –
  no separate export step needed
- Docks as a ViewPart below the Trados Studio editor (compatible with Studio 2024 / Studio18)
- Built on .NET Framework 4.8 with strong-name signing (`PublicKeyToken=6afde1272ae2306a`)
- Packaged in OPC format (`.sdlplugin`) as required by the Trados plugin framework
