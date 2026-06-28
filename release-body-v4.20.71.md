Supervertaler for Trados **v4.20.71** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.67 → 4.20.71.

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

## [4.20.71] – 2026-06-28

### Fixed (AutoTagger · usage now recorded in Token Usage & Costs)

- **AutoTagger's AI calls now show up in Token Usage & Costs.** The call passed no prompt "feature", and the usage ledger only records calls that carry one, so AutoTagger runs were silently omitted from the table. They are now logged under a new **"AutoTagger"** task, with token counts and cost like every other AI call.

### Fixed (AutoTagger · works without the Assistant pane, and never pops it open)

- **AutoTagger (Ctrl+Alt+G / right-click) now works reliably even if the Supervertaler Assistant pane was never opened this session, and it no longer opens that pane.** The pane is lazy (unpinned) and the handler previously did nothing until it had been created — so after a Trados restart the command appeared dead until you opened the pane or Settings. AutoTagger is now fully independent of the pane: it reads the active segment from the editor and its settings from disk, so it just works without disturbing your layout.

## [4.20.70] – 2026-06-28

### Added (AutoTagger · AI places inline tags into the target)

- **New AutoTagger feature: the AI looks at where the inline tags sit in the source segment and inserts that same set of tags into your existing translation at the right places, without changing any of the translated words.** Useful when a target has the right translation but is missing its tags or has them in the wrong spots (after MT, pasting, or typing the target by hand), which otherwise trips Trados' tag QA. It validates the result before writing (the tag set must match the source, the words must be unchanged, and tags must be well-formed); it re-inserts the tags into your exact target so punctuation like curly quotes is preserved; and if the AI's output doesn't validate it retries once and otherwise leaves the segment untouched, so it never writes broken tags. Reuses the same tag engine as batch translate.
  - **Where:** editor right-click → "Auto-tag active segment", and the **Ctrl+Alt+G** shortcut. Trados Undo (Ctrl+Z) reverts it.
  - **Shortcut change:** Ctrl+Alt+G now triggers AutoTagger. The floating TermLens popup keeps its **Ctrl-tap** trigger (the redundant Ctrl+Alt+G binding was removed); you can reassign a key to it via Trados' keyboard settings if you like.
  - **Configurable:** the instruction is an editable field under Settings → Prompts → "AutoTagger Instruction" (placeholders `{{SOURCE_TEXT}}`, `{{TARGET_TEXT}}`, `{{TAG_LIST}}`).
  - v1 is single-segment; a batch mode may follow. Mirrors the Supervertaler Workbench AutoTagger. (#39)

## [4.20.69] – 2026-06-28

### Fixed (Import/Export · re-import status line no longer truncated)

- **The re-import status line under the Format/Layout dropdowns now shows its full text.** Introduced in 4.20.68, the line's box was too short, so the longer "export only" messages (e.g. the Word + stacked-layout case) were clipped mid-sentence. The box is now wide and tall enough for the message to wrap fully. Reported by Michael.

## [4.20.68] – 2026-06-28

### Fixed (Import/Export · readable dropdowns + clear re-import status)

- **The Format and Layout dropdowns on the Import/Export tab no longer truncate their longer entries.** WinForms was clipping the drop-down list to the control width, so options like "Supervertaler Bilingual Table (5 columns)" and "Bracketed [SEGMENT NNNN] (AI-friendly, Markdown only)" were cut off mid-text (worse on high-DPI laptops). The popups now size to their widest item, matching the behaviour already used elsewhere in the plugin. Reported by Michael.
- **It's now clear which export can be re-imported.** Re-import support actually depends on the **Format and Layout together**, not the layout alone: Markdown round-trips every layout; Word (.docx) round-trips only the 5-column Bilingual Table (stacked/bracketed are export-only in Word); HTML is always read-only. The old per-item "round-trippable" tag was misleading because it ignored the format. A **live status line under the dropdowns** now states, for the current selection, whether the file can be edited and re-imported (green) or is export-only (amber) — and notes that the Bracketed layout only applies to Markdown. Reported by Michael.

## [4.20.67] – 2026-06-26

### Added (Batch Operations · "All unfinished segments" scope)

- **New Scope option in Batch Operations (Translate mode): "All unfinished segments".** It targets every segment whose status is *not* finalized — that is, everything **except Translated, Approved (translation), and Approved (sign-off)**. In practice it processes **Not Translated, Draft, and Rejected** segments, so you can batch-translate all the work that still needs doing in one go while leaving your confirmed and signed-off segments untouched. Drafts and rejected segments that already have target text are re-translated; empty ones are filled. Unlike "Filtered segments", it runs over the whole document, not the current display filter. The status is matched by name (`Translated` / `ApprovedTranslation` / `ApprovedSignOff`), so it's correct regardless of the ConfirmationLevel enum's ordering (where "Rejected" sorts above "Translated"). The Scope dropdown now also has a tooltip explaining every option, and the segment counter shows "*N* unfinished / *M* total" when this scope is selected. Works in Clipboard Mode too. Requested by Michael.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
