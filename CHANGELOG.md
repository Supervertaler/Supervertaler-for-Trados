# Changelog

## [4.16.2] — 2026-03-20

### Added
- **TermScan filtering for prompt generation** — "Analyse Project & Generate Prompt" now scans your document's source segments and filters termbase terms down to only those that actually appear in the document. This produces dramatically smaller, more focused glossaries in the generated prompt (e.g. 123 relevant terms from 2,680 total). The status message shows the filter count: "filtered X relevant from Y total".

### Changed
- **Chat avatars** — each message in the AI Assistant now has a small avatar header: a gray "AI" circle with "Supervertaler Assistant" for AI responses, and a blue person silhouette with "You" for your messages. Makes it easy to tell who said what at a glance.
- **Animated thinking indicator** — the AI Assistant now shows a persistent animated bubble in the chat area while waiting for a response. The bubble cycles through reassuring messages ("Thinking…", "Still working on it…", "Generating response…", etc.) so you always know the AI is still processing. Previously, the thin "Thinking…" label at the bottom could disappear during long operations, making it look like the request had silently failed.
- **System-initiated messages styled as assistant bubbles** — messages triggered by buttons (e.g. "Analyse Project & Generate Prompt") now display with assistant styling (gray, left-aligned) instead of user styling, since you didn't type them yourself.
- **TM reference pairs filtered to confirmed segments** — the prompt generator now only includes human-confirmed segments (Translated, Approved, or Signed-off) as reference pairs. Unconfirmed AI-generated translations are excluded to avoid feeding unverified output back as "correct" references. Pairs are sampled evenly across the document for diversity.

### Fixed
- **Stale prompt dropdown** — deleting prompts from the Prompt Manager no longer leaves ghost entries in the Batch Operations prompt dropdown. The dropdown now refreshes whenever the Prompt Manager closes, regardless of whether you clicked OK or Cancel (because deletions happen immediately on disk).
- **API timeout for large output requests** — prompt generation and other requests that produce long AI responses (> 8,192 tokens) now use a 10-minute timeout instead of timing out prematurely. This prevents the "thinking" indicator from disappearing mid-generation on complex documents.

---

## [4.16.1] — 2026-03-20

### Added
- **One-click plugin update** — the "Update Available" dialogue now has an "Install Update" button that downloads and installs the new version directly, without opening a browser. Just click, restart Trados, and you're running the latest version.
- **"What's new" link in update dialogue** — view the release notes before updating.

### Fixed
- **Prompt generation truncation** — the "Analyse Project & Generate Prompt" feature no longer cuts off long prompts. Output token limit increased from 4,096 to 32,768, allowing comprehensive prompts with large glossaries and TM reference pairs.
- **Correct version in plugin packages** — the `.sdlplugin` manifest now reads the version from the project file instead of using a hardcoded value. The DLL, manifest, and plugin.xml versions are guaranteed to match.
- **Stale assembly references** — fixed two action entries in plugin.xml that were stuck on old version numbers (4.5.0 and 4.10.0). The version bump script now uses pattern matching to catch all references, preventing this from recurring.

---

## [4.16.0] — 2026-03-20

### Added
- **Interactive term popup** — hovering over a term chip now shows an interactive popup instead of a standard tooltip. The popup supports word-wrapped text, stays open when you move the mouse into it, and renders URLs as clickable links.
- **URL metadata field** — term entries now support a URL field for linking to reference material. URLs appear as clickable links in the hover popup, and can be edited in the term entry editor and termbase editor grid.
- **Dismissible proofreading issues** — each issue card in the Reports tab now has a checkbox; ticking it removes the card from the list so you can track which issues you have addressed.

### Changed
- **Proofreading scope labels** — dropdown labels now use correct Trados terminology: "Translated only" and "Translated + approved/signed-off" instead of the previous MemoQ-style "Confirmed" labels.
- **Faster popup close** — the hover popup close delay was reduced from 200ms to 150ms for a snappier feel.

### Fixed
- **Popup text truncation** — long definitions, notes, and other metadata no longer get cut off in the hover popup. Text now word-wraps correctly within the popup.
- **Popup spacing** — removed excessive vertical spacing between metadata lines in the hover popup.

---

## [4.15.0] — 2026-03-20

### Added
- **Grok (xAI) provider support** — Grok is now available as a first-class AI provider alongside OpenAI, Claude, Gemini, Ollama, and Custom OpenAI-compatible endpoints. Three models included: Grok 4.20 (Reasoning), Grok 4.20, and Grok 4.1 Fast. All models support multimodal input (text + images).
- **Source synonym indicator** — the ≡ synonym indicator on term chips now also appears when the entry has source-side synonyms, not just target-side ones.
- **Source synonyms in tooltip** — hovering over a term chip now shows source-side synonyms (prefixed with "Also:") alongside the existing target-side synonym bullets.

### Fixed
- **Merge prompt direction** — the "Similar Term Found" merge dialog now correctly displays terms in the project's language direction when working with inverted termbases.

---

## [4.14.1] — 2026-03-19

### Added
- **Synonym indicator on term chips** — a small indigo ≡ icon now appears in the top-right corner of a term chip when the entry has target synonyms, so you can see at a glance which terms have alternative translations without hovering.
- **"Open Plugins folder" link in update dialog** — when a new version is available, the update notification now includes a clickable link that opens the Plugins/Unpacked folder in Explorer. Essential for Mac/Parallels users who must manually delete the old unpacked folder before installing an update.

### Fixed
- **Metadata indicator always visible** — the amber metadata dot (definition/domain/notes) now appears on all term chips, not only on chips that also have a shortcut badge.
- **Merge prompt respects project direction** — the "Similar Term Found" dialog now displays terms in the project's language direction when working with an inverted termbase (e.g. NL→EN project using an EN→NL termbase). Previously, source and target labels were swapped.

---

## [4.14.0] — 2026-03-19

### Added
- **Analyse Project & Generate Prompt** — new feature that analyses your document's content, terminology, and TM data to automatically generate a comprehensive domain-specific translation prompt using AI. Accessible via the link next to the prompt selector on the Batch Operations tab. The generated prompt appears in the AI Assistant chat, where you can refine it through conversation. Right-click any assistant message → "Save as Prompt…" to save the result to your prompt library.
- **Save as Prompt** — right-click any AI Assistant response and choose "Save as Prompt…" to save it as a reusable `.svprompt` file in your prompt library. The default name is your Trados project name, with automatic version numbering (v2, v3, etc.) if a prompt with that name already exists.

### Changed
- **British English spelling** — all user-facing text now uses British English spelling throughout the plugin and documentation (analyse, customise, organised, etc.).
- **Documentation improvements** — removed duplicate page headings from all help pages, added cross-references for the new Analyse Project & Generate Prompt feature, and added comprehensive documentation for the new feature.

### Fixed
- **Save as Prompt dialog** — fixed buttons being cut off at the bottom of the dialog under certain DPI scaling settings.
- **Synonym language tags in inverted termbases** — when editing a term from an inverted termbase (e.g. EN→NL termbase used in an NL→EN project), synonyms were saved with swapped language tags, causing them to appear on the wrong side. Now correctly reverses the language tags when saving.

---

## [4.13.0] — 2026-03-19

### Changed
- **Simplified built-in prompts** — replaced the 9 domain-specific translate prompts (Medical, Legal, Patent, Financial, Technical, Marketing, IT, Professional Tone, Preserve Formatting) with a single **Default Translation Prompt**. The default prompt is a general-purpose starting point that users can duplicate and customise for their specific domain. The Default Proofreading Prompt and all QuickLauncher prompts are unchanged.
- **Automatic cleanup of retired prompts** — on first launch after the update, the old domain-specific translate prompt files are automatically removed from the prompt library (only if they still contain the original built-in content — user-modified copies are preserved).

---

## [4.12.5] — 2026-03-19

### Fixed
- **Fixed duplicate plugin crash** — the `.sdlplugin` package filename (`Supervertaler.Trados`) did not match the `<PlugInName>` in the manifest (`Supervertaler for Trados`), causing Trados to create a second copy of the package under the manifest name. Two copies of the same plugin loaded simultaneously, crashing Trados on startup. The package filename now matches the manifest name. The build script also cleans up the old-name package and unpacked folder to prevent recurrence.

---

## [4.12.4] — 2026-03-19

### Added
- **Automatic stale-plugin detection** — when a new `.sdlplugin` is installed but Trados is still running the old extracted version, the plugin now detects the version mismatch at startup and prompts the user to restart. On restart, the old Unpacked folder is cleaned up and Trados re-extracts the new version automatically. Searches all three possible plugin locations (Roaming, Local, All Users) so the detection works regardless of which install option was chosen.

### Changed
- **Simplified install location guidance** — the installation docs now recommend accepting the default installer option ("All your domain computers") instead of manually switching to "This computer for me only". On non-domain PCs the two options are identical, and accepting the default avoids inconsistency between updates.

---

## [4.12.3] — 2026-03-19

### Fixed
- **Usage statistics checkbox now reflects opt-in choice** — when a user clicked "Yes" in the first-launch usage statistics dialog, the setting was saved to disk but the in-memory settings object was not updated. This caused the checkbox in Settings to appear unchecked until Trados was restarted. The opt-in choice is now synced into the live settings immediately.

---

## [4.12.2] — 2026-03-19

### Added
- **Parallels / Mac warning in first-run setup** — when running inside Parallels Desktop on a Mac, the setup dialog now shows a yellow warning panel advising users to keep their data folder on the Windows side (`C:\` drive). If the user selects a Mac-side path (`\\Mac\Home\...`), a confirmation dialog explains that SQLite databases do not work reliably on network-mounted filesystems. Non-Parallels users see no change.
- **Parallels / Mac documentation** — new "Running on a Mac (Parallels)" section in the installation help, and a new "Database errors on Mac (Parallels)" troubleshooting entry
- **Updated installer screenshot** — annotated screenshot showing the recommended "This computer for me only" option

---

## [4.12.1] — 2026-03-19

### Fixed
- **Version numbers now consistent across all plugin files** — the plugin.xml and pluginpackage.manifest.xml version attributes were out of sync with the assembly version, which could cause the wrong version to display in Trados. All version files are now aligned. Also rewrote `bump_version.py` to update all three version files (.csproj, plugin.xml, manifest) in a single command.

---

## [4.12.0] — 2026-03-19

### Added
- **`{{TM_MATCHES}}` prompt variable** — QuickLauncher prompts can now include translation memory fuzzy matches (≥70%) from the active segment. The variable expands to a formatted list showing match percentage, TM name, source, and target text. Available in the variable picker (Ctrl+,) and documented in the help system.
- **3 new built-in QuickLauncher prompts** — "Explain (within project context)" uses `{{PROJECT}}` for document-aware term explanation; "Translate segment using fuzzy matches as reference" combines `{{TM_MATCHES}}` with `{{SURROUNDING_SEGMENTS}}` for context-aware translation; "Translate selection in context of current project" uses `{{PROJECT}}` for full-document term translation
- **Opt-in anonymous usage statistics** — on first launch after this update, a dialog asks whether you'd like to share anonymous usage data to help improve the plugin. Only plugin version, OS version, Trados version, and system locale are sent — once per session, on startup. No personal data, translation content, or termbase information is ever collected. The setting can be changed at any time in Settings. Includes Parallels/VM detection to understand how many users run Trados on a Mac. Data is sent to a first-party Cloudflare Worker endpoint (no third-party trackers). ([#7](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/7))

---

## [4.10.12] — 2026-03-18

### Added
- **Termbase rename** — double-click a termbase name in TermLens Settings (or press **F2**) to rename it
- **fix_reversed_entries.py** — `tools/` script to detect and swap term entries that were stored in the wrong direction in a termbase

---

## [4.10.11] — 2026-03-18

### Fixed
- **Term display and immediate chip appearance restored for inverted-direction termbases** — when a project's translation direction is the opposite of a write termbase's declared direction (e.g. NL→EN project using an EN→NL termbase), TermLens now correctly indexes and matches terms after loading from disk (F5 and segment navigation both work), and newly added terms appear as chips immediately after Alt+Down or Alt+Up
- **Edit Term Entry dialog now follows project direction** — column labels, text fields, synonyms, and abbreviation fields are presented in project source → target order (e.g. Dutch | English in a NL→EN project) regardless of the termbase's declared direction; saves still write to the correct termbase columns

---

## [4.10.10] — 2026-03-18

### Fixed
- **Term direction now respects termbase language pair** — when adding terms via Alt+Down, Alt+Up, Ctrl+Alt+T, or the right-click menu, the plugin now compares the active project's source language against the write termbase's source language and swaps source/target text when they are inverted (e.g. working in a NL→EN project but writing to an EN→NL termbase); previously terms were silently inserted in the wrong direction

---

## [4.10.9] — 2026-03-18

### Changed
- **Lavender chip colour for abbreviation matches** — TermLens chips that matched via a source abbreviation now render with a light lavender background instead of the regular blue, making them instantly distinguishable from full-term matches; the shortcut badge on abbreviation chips is purple to match

---

## [4.10.8] — 2026-03-18

### Fixed
- **Smart selection no longer swallows the next word when selection has trailing space** — selecting a single word with a trailing space (e.g. by shift+arrow-key overshoot) now correctly adds just that word to the termbase; previously the expansion algorithm would land past the space and consume the following word (e.g. "trimethoxysilaan of" instead of "trimethoxysilaan")

---

## [4.10.7] — 2026-03-18

### Added
- **Persistent chat history** — the AI Assistant conversation is now saved to disk after each message and restored automatically when Trados restarts; history persists until you click the **Clear** button

---

## [4.10.6] — 2026-03-18

### Added
- **Variable picker in Prompt Editor** — press **Ctrl+,** in the prompt content field to open a variable menu listing all available variables with descriptions; selecting one inserts it at the cursor (mirrors the variable insertion shortcut in the Trados Studio editor)

### Changed
- **CS checkbox replaces Case dropdown in TermLens settings** — the per-termbase case sensitivity control is now a compact checkbox column (header: **CS**) instead of a dropdown showing Insensitive on every row; ticked = case-sensitive, unticked = case-insensitive; the column sits alongside the existing Read/Write/Project checkboxes

---

## [4.10.5] — 2026-03-18

### Added
- **QuickLauncher built-in prompts** — three prompts now ship as built-ins and are created on first run (or via Restore): *Assess how I translated the current segment*, *Define*, and *Explain (in general)*

### Changed
- **Style guide prompts removed** — the five language-specific style guides (Dutch, English, French, German, Spanish) are no longer shipped as built-in prompts; users who want style guide prompts can create their own in the Prompts tab
- **Built-in prompts use `{{SOURCE_LANGUAGE}}`/`{{TARGET_LANGUAGE}}`** — all specialist prompt content updated from legacy `{source_lang}`/`{target_lang}` single-brace format to the current double-brace standard

### Fixed
- **Delete button label clipped in Prompts tab** — the Delete button was too narrow (55 px), causing the label to be cut off; widened to 65 px

---

## [4.10.4] — 2026-03-18

### Fixed
- **Prompts tab: double-click opens wrong prompt after column sort** — after sorting the prompt list by clicking a column header, double-clicking a row now opens the correct prompt; previously it used the visual row index, which diverged from the data list order after sorting
- **"Surrounding segments" spinner overlap** — the spinner for the Surrounding segments setting in AI Settings was positioned too close to its label and appeared partially overlapping it; moved right to give the label room

### Changed
- **`{{PROJECT}}` display in chat** — when a QuickLauncher prompt containing `{{PROJECT}}` is sent, the chat bubble now shows a compact summary (e.g. `[source document — 47 segments]`) instead of the full source document text; the complete text is still sent to the AI unchanged

---

## [4.10.3] — 2026-03-18

### Added
- **`{{PROJECT_NAME}}` variable** — replaced with the Trados project name in QuickLauncher prompts
- **`{{DOCUMENT_NAME}}` variable** — replaced with the active file name in QuickLauncher prompts
- **`{{SURROUNDING_SEGMENTS}}` variable** — replaced with N source segments before and after the active segment, numbered with their actual per-file Trados segment numbers and the active segment marked `← ACTIVE`; N is configurable in Settings → AI Settings → Surrounding segments (default: 5)
- **`{{PROJECT}}` variable** — replaced with all source segments in the active document, numbered with actual Trados segment numbers; multi-file projects include `=== File N ===` headers at file boundaries where segment numbering restarts
- **Surrounding segments setting** — new spinner in AI Settings: "Surrounding segments" (default: 5, range 1–20); controls both the `{{SURROUNDING_SEGMENTS}}` QuickLauncher variable and the context window in the AI Assistant chat

### Changed
- **AI Assistant surrounding context** — was previously hardcoded to 2 segments on each side; now uses the new "Surrounding segments" setting (default 5)

### Notes
- Segment numbers in `{{SURROUNDING_SEGMENTS}}` and `{{PROJECT}}` match the numbers shown in the Trados editor (per-file, 1-based); the same numbering logic used by the AI Proofreader results
- `{{PROJECT}}` is evaluated lazily — only when the prompt template actually contains `{{PROJECT}}`; it has no cost unless used
- Sending a 10,000-word patent as `{{PROJECT}}` to a Sonnet-class model costs approximately $0.04–0.05 per call

---

## [4.10.2] — 2026-03-17

### Changed
- **`quicklauncher_label` YAML field** — the optional short label for the QuickLauncher menu is now set with `quicklauncher_label:` in `.svprompt` frontmatter; the old name `quickmenu_label` still works as a backward-compatible alias

---

## [4.10.1] — 2026-03-17

### Added
- **`{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` variables** — renamed from `{{SOURCE_TEXT}}` / `{{TARGET_TEXT}}` for clarity; the old names continue to work as aliases
- **Ctrl+Q shortcut** — opens the QuickLauncher prompt menu directly from the keyboard; note that Trados's default "View Internally Source" shortcut must be removed first (File → Options → Keyboard Shortcuts)
- **QuickLauncher help page** — new documentation page covering variables, examples, and setup

### Fixed
- **QuickLauncher prompts appear immediately** — newly created or edited prompts now appear in the right-click menu without restarting Trados
- **Case column width** — the Case column in TermLens settings was too narrow to display "Insensitive" in full; widened to fit

---

## [4.10.0] — 2026-03-17

### Added
- **QuickLauncher** — new editor right-click menu entry listing all prompts marked as QuickLauncher; selecting a prompt fills in the current segment's source text, target text, selection, and language pair as variables and submits the expanded prompt directly to the Supervertaler Assistant chat, enabling one-click AI actions without switching panels
- **QuickLauncher prompt support** — prompts are marked as QuickLauncher by adding `sv_quickmenu: true` to their YAML frontmatter (compatible with the same flag used in Supervertaler Workbench), or by setting `category: QuickLauncher`; an optional `quickmenu_label:` field sets a short display name in the menu
- **Segment-level prompt variables** — `{{SOURCE_TEXT}}`, `{{TARGET_TEXT}}`, and `{{SELECTION}}` variables are now substituted in prompts at runtime using the current segment context (compatible with Supervertaler Workbench variable names)
- **Legacy category normalisation** — the old internal category name `quickmenu_prompts` is automatically normalised to `QuickLauncher` when loading prompt files, ensuring forward compatibility

---

## [4.9.0] — 2026-03-17 ([#4](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/4))

### Added
- **Unified user data folder** — Supervertaler for Trados now stores all data (settings, licence, projects, prompts) in a single shared folder alongside Supervertaler Workbench (default: `~/Supervertaler/`); the folder is configured via a shared `%APPDATA%\Supervertaler\config.json` pointer so both products automatically read from the same location
- **First-run setup dialog** — on first launch, a dialog lets you choose the data folder; if an existing Workbench installation is detected its path is pre-filled so you can share data immediately with one click
- **Automatic data migration** — existing settings, licence, project overlays, and custom prompts are copied from the old `%LocalAppData%\Supervertaler.Trados\` location to the new shared folder on first run; old files are left in place as a backup
- **Shared prompt library** — prompts are now read from and written to the shared `prompt_library/` folder; any prompt created in Workbench is immediately visible in the Trados plugin and vice versa, with no configuration required

---

## [4.8.1] — 2026-03-17

### Changed
- **Ctrl+Alt+T opens full Term Entry Editor** — pressing Ctrl+Alt+T to add a new term now opens the full Term Entry Editor dialog instead of the simple Add Term dialog, giving immediate access to definition, domain, notes, and synonym fields when adding a term

### Fixed
- **TermLens subscript/superscript matching** — terms containing Unicode subscript digits (₀–₉) or superscript digits (⁰¹²³⁴⁵⁶⁷⁸⁹) such as H₂O, CO₂, and mm² were not recognised in segments because the tokeniser split them at the script character; the word pattern now includes these Unicode ranges and index keys are normalised so matching works correctly
- **Context-aware help links** — F1 help in the Batch Operations and Reports tabs now opens the correct documentation page for the active context rather than falling back to the generic help home page

---

## [4.8.0] — 2026-03-16

### Added
- **AI Proofreader** — new batch proofreading mode in the Batch Operations tab; select "Proofread" to check translated segments for errors using AI; results appear in the new Reports tab as clickable issue cards with segment number, issue description, and suggestion; clicking a card navigates to the corresponding segment in the editor
- **Reports tab** — new tab in the Supervertaler Assistant panel displaying proofreading results; shows issue count, run duration, and a scrollable list of issue cards; Clear button to reset results
- **Proofread scopes** — five scope options for proofreading: Confirmed only, Translated + Confirmed, All segments, Filtered segments, and Filtered (confirmed only)
- **Segment navigation from reports** — clicking an issue card in the Reports tab navigates directly to the relevant segment in the Trados editor, using the segment's ParagraphUnitId and SegmentId for accurate navigation in multi-file projects
- **Per-file segment numbering** — issue cards in the Reports tab show the actual per-file segment number (matching the Trados editor grid) rather than a cross-file batch index
- **"Also add issues as Trados comments" checkbox** — in the Batch Operations tab (Proofread mode), option to insert proofreading issues as Trados segment comments alongside the Reports tab display
- **Prompt category filtering** — the prompt dropdown in Batch Operations now filters by mode: only "Translate" prompts appear in Translate mode, only "Proofread" prompts in Proofread mode

### Changed
- **Prompt file extension** — built-in and user prompts now use `.svprompt` file extension (previously `.md`), matching the Supervertaler desktop application; existing `.md` prompt files are still loaded for backward compatibility
- **Prompt YAML key renamed** — the `domain` key in prompt YAML frontmatter is now `category`; the parser accepts both for backward compatibility
- **Prompt categories renamed** — "Domain Expertise" and "Style Guides" categories are now "Translate"; "Proofreading" is now "Proofread"
- **Case sensitivity per-termbase dropdown simplified** — removed "Default" option; per-termbase case sensitivity is now simply "Insensitive" (default) or "Sensitive"

---

## [4.7.0] — 2026-03-16

### Added
- **Case-sensitive matching** — new global setting "Case-sensitive matching" (default: off) plus per-termbase override in the settings grid; when enabled, terms only match if the source text has the same letter case as the indexed term; per-termbase setting can be Sensitive or Insensitive
- **Mouse wheel scrolling in AI Assistant chat** — the chat message panel now supports mouse wheel scrolling; previously only the scrollbar worked

### Changed
- **Database schema migration** — `case_sensitive` column automatically added to the `termbases` table on first use; fully backward-compatible

---

## [4.6.0] — 2026-03-16

### Added
- **Abbreviation fields on term entries** — each term entry now has optional **Source Abbreviation** and **Target Abbreviation** fields; when a source abbreviation appears in a segment, TermLens highlights it and shows the target abbreviation underneath, with the full term pair available in the +N tooltip
- **Pipe-separated abbreviation variants** — abbreviation fields support multiple variants separated by `|` (e.g., `GC|G.C.|gc|g.c.`); each variant is indexed and matched independently, so all common forms of an abbreviation are recognised
- **Abbreviation-aware insertion** — clicking or Alt+digit-inserting an abbreviation-matched chip inserts the target abbreviation (first variant) instead of the full target term
- **Abbreviation in AI prompts** — AI translation prompts now include abbreviation pairs alongside their full terms, so the AI knows both forms
- **Abbreviation columns in Term Editor** — the Add/Edit Term dialog includes Source Abbreviation and Target Abbreviation text fields between the primary term fields and the synonyms section
- **Abbreviation columns in Termbase Editor** — the termbase grid shows SrcAbbr and TgtAbbr columns for viewing and editing abbreviations inline

### Changed
- **Database schema migration** — `source_abbreviation` and `target_abbreviation` columns automatically added to existing databases on first write; fully backward-compatible with older Supervertaler databases

---

## [4.5.0] — 2026-03-16

### Added
- **Ctrl+T quick translate** — press **Ctrl+T** to instantly translate the active segment using the same provider, model, and prompt configured in the Batch Translate tab; the translation is applied directly to the target cell, with full tag preservation for segments containing inline formatting; also available via right-click context menu ("Translate active segment"); rebindable in Trados Studio's keyboard shortcut settings
- **AI Settings link in Batch Translate tab** — clickable "AI Settings…" link below the provider display opens the Settings dialog directly on the AI Settings tab for quick access to provider, model, API key, and AI context configuration

### Changed
- **Ctrl+Alt+A retired** — the old standalone single-segment translation shortcut has been unbound; the action is kept for backward compatibility but now redirects to the same Ctrl+T batch-translate pipeline with full tag support

### Fixed
- **Tagged segments not applied in batch translate** — segments containing inline Trados tags (bold, italic, field codes, page numbers, etc.) were translated by the AI but the translation was never written back to the target; the reconstructed target was written to the document model but the Trados editor's own buffer overwrote it with the old (empty) content; now uses the Trados `ProcessSegmentPair` API which handles the edit transaction correctly
- **Last segment in batch sometimes lost** — the final segment translated in a batch could silently lose its translation because no subsequent segment navigation forced the Trados editor to commit the pending edit; the new `ProcessSegmentPair` approach bypasses the editor buffer entirely for tagged segments, and non-tagged segments continue to use the proven `Selection.Target.Replace` path

---

## [4.4.0] — 2026-03-16

### Added
- **Tag-aware AI translation** — segments containing inline Trados tags (bold, italic, field codes, etc.) are now fully supported for both Batch Translate and single-segment AI translation (Ctrl+Alt+A); previously, tags were silently stripped and lost in the target
- **SegmentTagHandler** — new serialization/reconstruction engine that converts Trados `ITagPair` and `IPlaceholderTag` objects into numbered placeholders (`<t1>...</t1>`, `<t2/>`), sends them through the LLM translation pipeline, then reconstructs the target segment with the original tag objects cloned and repositioned to match the translated word order
- **Graceful fallback** — if the LLM drops or corrupts tag placeholders, the plugin falls back to plain-text insertion (stripping placeholders) instead of failing silently

### Improved
- **Translation prompt tag instructions** — replaced generic CAT tool tag preservation instructions with specific numbered placeholder format and examples, improving LLM tag preservation accuracy

---

## [4.3.0] — 2026-03-14

### Added
- **Per-project settings** — switching between Trados projects now automatically saves and restores the Supervertaler database path, enabled/disabled termbases, write targets, project termbase, and AI context termbase filters; settings are stored per-project in `%LocalAppData%\Supervertaler.Trados\projects\` and applied automatically when the active document changes
- **Per-project settings documentation** — new help page documenting how per-project settings work, what's saved per-project vs globally, and how the automatic switching behaves
- **Privacy policy** — [supervertaler.com/privacy](https://supervertaler.com/privacy/)

### Fixed
- **"Add & Edit" crash in Similar Term Found dialog** — pressing "Add & Edit" when merging a term caused an `ArgumentOutOfRangeException` (ordinal 10) because `GetTermById()` had an off-by-one error in its column indexing for optional fields (domain, notes, is_nontranslatable); each field was read one position past its actual column index, and the last field fell off the end of the result set
- **Licence null-status crash** — when the Lemon Squeezy API returned a null or empty `status` field during activation, the licence was treated as invalid even though the key was activated; now treats null status as active when the licence has a valid instance ID
- **Trial period mismatch** — `LicenseInfo.cs` used a hardcoded 14-day trial window while `LicenseManager.cs` used 90 days; unified both to the 90-day constant
- **AI Settings termbase list stale after database switch** — switching Supervertaler databases in the TermLens settings tab didn't update the AI Settings tab's termbase checklist until the dialog was closed and reopened; the AI context panel now refreshes immediately when the termbase list changes
- **Term Picker shortcut documented incorrectly** — the About dialog and help docs showed `Ctrl+Shift+G` but the actual shortcut is `Ctrl+Alt+G`; corrected all references

### Improved
- **Keyboard shortcuts documentation** — added Mac/Parallels equivalents (Ctrl → Control, Alt → Option) to all shortcut tables for users running Trados in Parallels
- **Support email** — updated to support@supervertaler.com
---

## [4.2.2-beta] — 2026-03-13

### Fixed
- **Licence tab help link** — the ? button on the Licence tab now opens the Licensing & Pricing page instead of incorrectly opening TermLens Settings
- **Backup tab help link** — the ? button on the Backup tab now opens the dedicated Backup & Restore page instead of using a stale anchor link
- **Licensing help URL** — corrected the GitBook URL slug from `licensing-and-pricing` (404) to `licensing` (the actual filename-based slug)

### Changed
- **UK English in documentation** — changed all instances of "license" (US) to "licence" (UK) in the online help pages

### Added
- **Example Project link in help menus** — both the TermLens and Supervertaler Assistant help menus (? button) now include an "Example Project" link that opens the documentation page for the downloadable example project
- **Example Project documentation** — new docs page with step-by-step instructions, screenshots, and the example package (patent translation with termbase, TM, and MultiTerm termbase)
- **Help link reference** — new `HELP-LINKS.md` in the repo root documents every help link in the plugin with its online URL and which UI element triggers it

---

## [4.2.1-beta] — 2026-03-12

### Improved
- **Settings toolbar buttons** — the TermLens tab toolbar buttons now use descriptive labels ("+ Add", "− Remove") instead of cryptic symbols; all five buttons (Open, Export, Import, + Add, − Remove) have tooltips explaining their function

### Fixed
- **"Max segments" label overlap** — the "Max segments:" label in AI Settings no longer runs into the number input box

---

## [4.2.0-beta] — 2026-03-12

### Added
- **Update checker** — on startup, the plugin checks GitHub Releases for a newer version and shows a dialog with Download, Skip This Version, and Remind Me Later buttons. Checks once per session, respects skipped versions, and never blocks Trados startup (runs in background)

---

## [4.1.0-beta] — 2026-03-12

### Added
- **Settings backup and restore** — new **Backup** tab in the Settings dialog with Export and Import buttons; export saves all plugin settings (termbase paths, toggle states, font size, shortcut preferences, AI provider keys, model selections, prompt configuration) to a JSON file; import validates the file, creates an automatic backup of current settings, and applies the imported configuration immediately
- **Open settings folder** — clickable link in the Backup tab opens the `%LocalAppData%\Supervertaler.Trados\` folder in Explorer for easy access to settings files
- **Open prompts folder** — clickable link in the Prompts tab opens the `%LocalAppData%\Supervertaler.Trados\prompts\` folder in Explorer

### Fixed
- **Restore button clipped in Prompts tab** — the "Restore" button width was too narrow, causing the label to be truncated

---

## [4.0.2-beta] — 2026-03-12

### Added
- **Dual-mode Alt+digit term shortcuts** — two configurable shortcut styles for inserting terms 10+ (choose in Settings > TermLens > Term shortcuts):
  - **Sequential** (default) — type the term number digit by digit: Alt+4, Alt+5 inserts term 45. Clean sequential badge numbers (10, 11, 12, ...). 1-second timer between digits.
  - **Repeated digit** — press the same digit key multiple times: Alt+5, Alt+5 inserts term 14. Supports up to 5 tiers (45 terms). No timer ambiguity.
- **Term Picker wrap-around navigation** — pressing Down on the last term jumps to the first, and Up on the first jumps to the last

### Changed
- **Term Picker numbering** — the Term Picker now always uses plain sequential numbers (1, 2, 3, ...) regardless of the shortcut style setting, since navigation is done with arrow keys and Enter

---

## [4.0.1-beta] — 2026-03-12

### Fixed
- **Merge dialog buttons clipped** — the "Similar Term Found" dialog's button bar (Add as Synonym, Add & Edit, Keep Both, Cancel) was invisible or partially clipped inside Trados's WPF-hosted plugin environment; replaced the Dock-based panel layout with flat absolute positioning so buttons render reliably at any DPI
- **Merge dialog button text truncated** — widened the "Add as Synonym" and "Add & Edit..." buttons so their labels are no longer cut off

### Added
- **Merge dialog button tooltips** — each button now shows a tooltip on hover explaining what it does

---

## [4.0.0-beta] — 2026-03-12

### Changed
- **90-day free trial** — extended from 14 days; no credit card required, no sign-up
- **Support & Community link** in About dialog now points to `supervertaler.com/trados/#support` (Groups.io mailing list, ProZ forum, GitHub Issues) instead of directly to GitHub Issues; future-proofed so support channels can be updated without rebuilding the plugin
- **Version display** — About dialog now shows the full informational version string (e.g. "4.0.0-beta") rather than the numeric assembly version

### Fixed
- **Shield emoji clipping** — "Source code available for security audit" link in the About dialog was partially obscured by the shield emoji; offset increased to prevent overlap
- **Tooltips on About dialog links** — Documentation and Support & Community links now show tooltips on hover

---

## [4.0.0] — 2026-03-11

### Added
- **Licensing system** — Lemon Squeezy-powered license key activation with two paid tiers: **TermLens** (€10/month — terminology features) and **TermLens + Supervertaler Assistant** (€15/month — all features including AI); 14-day free trial with full access on first install; 30-day offline cache for validation; 2 machine activations per key
- **License tab in Settings** — dedicated tab for entering license keys, activating/deactivating licenses, verifying license status, and managing subscriptions; shows trial countdown, plan name, masked key, and last verification date
- **License status in About dialog** — color-coded license status (blue for trial, green for active, red for expired) with a clickable link that opens the License settings tab directly
- **Feature gating** — TermLens panel and terminology actions require Tier 1+; AI Assistant panel and AI translate action require Tier 2; graceful overlays and messages guide users to purchase or upgrade
- **Security transparency** — "Source code available for security audit" link in the About dialog with tooltip explaining the plugin's network behaviour; links to the public GitHub repository
- **Enhanced AI Assistant context** — the AI chat assistant now sees the full document content (all source segments) so it can determine the document type (legal, medical, technical, etc.) and provide context-appropriate assistance; also includes project/file metadata, surrounding segments, and term definitions/domains/notes
- **AI Context settings** — three new settings in AI Settings: "Include full document content" (with configurable max segments), and "Include term definitions and domains"

### Changed
- **About dialog** — removed duplicate "Plugin Help" link (Documentation link remains); added clickable license status that opens Settings → License tab; added security audit note with GitHub link

### Fixed
- **Settings sync between panels** — changing settings from the TermLens gear icon now immediately reflects in the AI Assistant panel and vice versa; previously each panel had its own in-memory copy that could get out of sync

---

## [3.4.2] — 2026-03-10

### Added
- **Merge prompt for similar terms** — when adding a term whose source or target already exists in the termbase (but with a different translation), a dialog offers to add the new text as a synonym instead of creating a near-duplicate entry; works with Alt+Down, Alt+Up, and Ctrl+Alt+T
- **"Add & Edit" option in merge dialog** — alongside the quick "Add as Synonym" button, the merge dialog now offers "Add & Edit…" which merges the synonym and opens the full Term Entry Editor so the user can review or add metadata before closing
- **Term metadata in tooltips** — hovering over a term chip now shows Domain and Notes fields alongside Definition (previously only Definition was displayed)
- **Metadata indicator on badges** — the shortcut badge number on term chips turns black (instead of white) when the term has metadata (definition, domain, or notes), giving a visual cue to hover for more info

### Changed
- **"MultiTerm Help"** — renamed the context menu item from "MultiTerm Support" to "MultiTerm Help" for consistency
- **"Supervertaler Assistant Help"** — renamed the AI Assistant help menu item from "Assistant Help" to "Supervertaler Assistant Help"
- **Dialog title casing** — "Edit Term Entry" and "Add Term Entry" renamed to sentence case ("Edit term entry" / "Add term entry")

### Fixed
- **Shift+Enter in AI Assistant** — Shift+Enter now correctly inserts a newline in the chat input instead of being intercepted by Trados Studio; uses a thread-local `WH_GETMESSAGE` hook to intercept the key press before Trados's message filters can consume it
- **Paste newlines in AI Assistant** — pasting text with bare `\n` line endings (e.g. copied from Trados segments) now displays correctly; the chat input normalises `\n` to `\r\n` on paste
- **Smart selection expansion** — partial word selections now expand to the shortest matching word at the boundary instead of the longest, preventing over-expansion when selecting near short words (e.g. selecting "o" no longer expands to "output" when "of" is adjacent)
- **Merge dialog cutoff** — the "Similar Term Found" dialog is now wider (520Ã—310) to prevent text truncation on longer term pairs

---

All notable changes to Supervertaler for Trados (formerly TermLens) will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/).

---

## [3.4.1] — 2026-03-10

### Added
- **Select All / Deselect All** links for termbases in AI Settings → AI Context section

### Fixed
- **Settings TextBox overlap** — the database file path TextBox no longer extends over the "Create New..." button when the settings dialog is resized

---

## [3.4.0] — 2026-03-09

### Added
- **MultiTerm termbase support** — TermLens now automatically detects MultiTerm .sdltb
  termbases attached to the active Trados project and displays their terms alongside
  Supervertaler terms; MultiTerm terms appear as green chips in the TermLens panel
- **Read-only MultiTerm terms** — MultiTerm terms are read-only: right-click context menus
  do not show Edit, Delete, or Non-Translatable options for green (MultiTerm) chips;
  tooltips show "[MultiTerm — read-only]"
- **MultiTerm in settings** — detected MultiTerm termbases appear in the Supervertaler
  Settings dialog with a "[MultiTerm]" label and light green row tint; Read checkbox
  toggles visibility; Write and Project columns are always disabled (read-only)
- **Auto-refresh on termbase changes** — when terms are added or removed from a MultiTerm
  .sdltb termbase (e.g. via Trados's native Term Recognition panel), TermLens automatically
  detects the file modification and reloads terms on the next segment change
- **JET 4.0 / ACE OLEDB driver support** — .sdltb files are opened via the built-in
  Microsoft.Jet.OLEDB.4.0 driver (available in all 32-bit Windows processes) with fallback
  to ACE OLEDB 12.0–16.0; no additional driver installation required for Trados Studio
  (which runs as an x86 process)
- **API fallback** — if no OleDb driver can open an .sdltb file, the plugin attempts to
  use Trados's built-in ITerminologyProviderManager API for per-segment term search with
  LRU caching (200 segments)

### Changed
- **Cleaned up MultiTerm diagnostic logging** — removed verbose reflection-based logging
  from the MultiTerm detection and fallback provider code; the multiterm_debug.log file is
  no longer written

---

## [3.3.3] — 2026-03-09

### Added
- **Help button on dialogs** — the Termbase Editor and Edit Term Entry dialogs now show a
  `?` button in the title bar that opens the relevant online help page (matching the pattern
  already used by the Supervertaler Settings dialog)

---

## [3.3.2] — 2026-03-09

### Added
- **Context-sensitive help** — the `?` button on TermLens and Supervertaler Assistant
  panels now opens a dropdown menu with a direct link to the relevant online help page
  and an "About" option; F1 opens contextual help from every dialog (settings, Add Term,
  Term Picker, Termbase Editor, Prompt Editor, Bulk Add NT)
- **HelpSystem** — new `Core/HelpSystem.cs` provides a centralized topic registry and
  URL launcher for all help pages

### Changed
- **Help URL slug** — documentation URLs updated from `gitbook.io/superdocs` to
  `gitbook.io/help` for a cleaner, more intuitive path
- **About dialog access** — the `?` button now shows a dropdown instead of directly
  opening About; About is still accessible via the dropdown menu

---

## [3.3.1] — 2026-03-08

### Added
- **Resizable chat input** — drag the top edge of the chat input area upward to make it
  taller when composing multi-line messages with Shift+Enter; drag down to shrink it back

### Fixed
- **Settings dialog too wide** — the Supervertaler Settings window could become excessively
  wide and extend off-screen; now capped at 800px maximum width, and persisted size is
  validated on restore
- **Chat spacing** — removed remaining double-spacing in AI responses caused by duplicate
  paragraph marks in table rendering
- **Termbases list in AI Settings** — the CheckedListBox no longer stretches the dialog
  horizontally; long termbase names scroll within the list via horizontal scrollbar

---

## [3.3.0] — 2026-03-08

### Added
- **AI Assistant** — project-aware chat interface in a separate dockable Trados panel;
  supports multi-turn conversations with full context from the active segment (source,
  target, termbase terms, TM matches); responses render as Markdown with headings, bold,
  italic, inline code, code blocks, tables, and lists; right-click to copy or apply
  suggestions directly to the target segment
- **Image attachments in chat** — paste images from clipboard (Ctrl+V), drag and drop
  image files, or browse with the attach button; thumbnails appear in an attachment strip
  below the input; images are sent to the AI using each provider's vision/multimodal API
  (OpenAI, Claude, Gemini, Ollama); click thumbnails in chat bubbles to view full-size
- **AI context control** — new "AI Context" section in AI Settings lets you choose which
  termbases contribute terms to AI prompts (independent of TermLens display settings) and
  toggle whether TM (Translation Memory) fuzzy matches are included in the AI context
- **TM match integration** — when enabled in settings, TM fuzzy matches for the active
  segment are included in the AI Assistant's system prompt, showing match percentage,
  source/target text, and TM name so the AI can leverage existing translations
- **Ollama support for AI Assistant** — local Ollama models can be used for the chat
  assistant with configurable endpoint
- **Custom OpenAI-compatible endpoints** — profile-based configuration for any
  OpenAI-compatible API (e.g., Azure OpenAI, LM Studio, vLLM); multiple profiles
  supported with separate endpoint, model, and API key per profile
- **Chat tooltips** — all chat input buttons (Send, Stop, Clear, Attach) now show
  descriptive tooltips explaining their function and keyboard shortcuts

### Changed
- **Attachment icon** — replaced the paperclip emoji (📎) with a clearer photo icon
  from Segoe MDL2 Assets for better visibility in the chat input area
- **Chat rendering** — eliminated extra blank lines between paragraphs in AI responses
  for more compact, readable output
- **Shift+Enter for newlines** — the chat input now supports Shift+Enter to insert line
  breaks without sending the message (Enter alone sends)
- **AI Settings layout** — the AI Context section now repositions dynamically based on
  the selected provider, eliminating wasted space when provider-specific panels (Ollama,
  Custom OpenAI) are hidden; the termbases checklist is taller to show more entries
  without scrolling

### Fixed
- **TermLens header text cutoff** — the word count and match summary in the TermLens
  panel header is no longer truncated by the floating gear and help buttons; added right
  padding to account for the button overlay

---

## [3.2.0] — 2026-03-08

### Added
- **Help / About dialog** — "?" button next to the settings gear opens an About dialog
  showing plugin version, author info, keyboard shortcuts reference, and links to
  website, documentation, and support; email address copies to clipboard on click
- **NT filter in Termbase Editor** — "NT only" checkbox in the toolbar filters the
  term list to show only non-translatable entries; composes with the search filter
- **Bulk Add NT** — "Bulk Add NT" button in the Termbase Editor opens a dialog where
  you can paste multiple non-translatable terms (one per line) for batch import;
  reports how many were added and how many duplicates were skipped
- **Copy cell in Termbase Editor** — Ctrl+C now copies the current cell value instead
  of the entire row; right-click context menu includes a "Copy cell" option
- **Duplicate prevention** — all term insert and update paths now check for existing
  entries with the same source and target term (case-insensitive) in the same
  termbase; quick-add shortcuts (Alt+Down/Up, Ctrl+Alt+T, Ctrl+Alt+N) show a clear
  message when a duplicate is detected; bulk operations report how many duplicates
  were skipped

### Changed
- **Renamed "glossary" to "termbase"** — all user-facing labels, context menus,
  dialogs, and settings now use "termbase" consistently instead of the previous mix
  of "glossary" and "termbase"
- **Shortened language names** — language pair displays throughout the UI
  (Termbase Editor title bar, settings grid, Add Term dialog) now show short names
  like "English" instead of "English (United States)"
- **Sentence case context menus** — right-click menu items in the TermLens panel now
  use sentence case ("Mark as non-translatable") instead of title case
- **Settings dialog database label** — the file path label in settings now reads
  "Database" instead of "Termbase" to avoid confusion with individual termbases
  inside the database

### Fixed
- **Alt+Up word expansion** — quick-add to project termbase (Alt+Up) now expands
  partial word selections to full word boundaries, matching Alt+Down behaviour

---

## [3.1.0] — 2026-03-06

### Added
- **Prompt manager / library** — 14 built-in prompts (domain expertise for Medical,
  Legal, Patent, Financial, Technical, Marketing, IT; style guides for Dutch, English,
  French, German, Spanish; project prompts for professional tone and formatting);
  prompts stored as Markdown files with YAML frontmatter, compatible with Supervertaler
  desktop prompt format
- **Prompt selector in Batch Translate** — dropdown between Scope and Provider lets you
  pick a prompt before translating; selected prompt persists across sessions
- **Prompts tab in Settings** — third tab in the Settings dialog with system prompt
  viewer/editor and full prompt library management (create, edit, delete, restore
  built-in prompts)
- **Composable prompt assembly** — base system prompt (tag preservation, number
  formatting) + custom prompt (domain/style instructions) + glossary terms; custom
  system prompt override available for advanced users
- **Supervertaler desktop prompt discovery** — automatically scans
  `~/Supervertaler_Data/` and `%AppData%\Supervertaler\` for shared prompt libraries
- **Variable substitution** — prompts support `{source_lang}`, `{target_lang}`,
  `{{SOURCE_LANGUAGE}}`, `{{TARGET_LANGUAGE}}` placeholders, replaced at translation
  time with the document's language pair

### Changed
- **Prompts tab side-by-side layout** — the Settings dialog Prompts tab now shows the
  custom prompt library on the left and the system prompt on the right, making better
  use of the available space
- **Prompt variable display simplified** — prompt editor shows only the standard
  `{{SOURCE_LANGUAGE}}` / `{{TARGET_LANGUAGE}}` placeholders; legacy `{source_lang}` /
  `{target_lang}` aliases still work silently for backward compatibility

### Fixed
- **TermLens glossary list no longer cut off** — the TermLens settings tab now uses
  Dock-based panel layout instead of absolute pixel positioning, so the glossary grid
  scales correctly across screen resolutions and DPI settings
- **Prompt library Source column resizable** — the Source column in the prompt list now
  uses proportional FillWeight sizing instead of a fixed width
- **Plugin manifest version updated** — `plugin.xml` now reports v3.1.0 (was stuck at
  2.0.1 since the rename)
- **Windows on ARM support** — the plugin now works on Windows on ARM (Parallels on
  Apple Silicon Macs, Surface Pro X, etc.); ships ARM64 native SQLite binary alongside
  x64 and x86; properly detects process architecture and copies the correct native
  library where SQLitePCLRaw can find it
- **SQLitePCLRaw initialization order** — `AssemblyResolve` handler is now registered
  before native library preloading, and `Batteries_V2.Init()` is called explicitly to
  prevent `TypeInitializationException` on non-standard environments
- **Improved error diagnostics** — database creation errors now show the full inner
  exception chain for easier troubleshooting

---

## [3.0.0] — 2026-03-06

### Added
- **AI batch translation** — translate segments in bulk using LLM providers; supports
  OpenAI (GPT-4o, GPT-4o mini, o1, o3-mini), Anthropic (Claude 3.5 Sonnet, Haiku,
  Opus), and Google (Gemini 2.0 Flash, Gemini 1.5 Pro); configurable via the new AI
  Settings panel accessible from the Batch Translate tab
- **AI single-segment translate** — press **Ctrl+Alt+A** or right-click → "AI Translate
  Current Segment" to translate just the active segment using the configured AI provider
- **Glossary-aware AI prompts** — AI translations automatically include matched
  terminology from your TermLens glossaries in the prompt, so the AI respects your
  approved terms, including non-translatable terms
- **Four batch translate scopes** — "Empty segments only" (default), "All segments",
  "Filtered segments", and "Filtered (empty only)"; filtered scopes translate only
  segments visible in Trados's advanced display filter
- **Live filtered segment counts** — the Batch Translate tab updates segment counts
  in real time when you change the Trados display filter
- **AI Settings panel** — configure provider, model, API key, and temperature directly
  in the Batch Translate tab; settings persist across sessions
- **Batch translate progress** — real-time log panel shows translation progress,
  segment-by-segment results, and any errors; cancel button to stop mid-batch

### Changed
- **Batch Translate tab** — no longer a placeholder; fully functional with scope
  selector, segment counts, translate/cancel buttons, and scrollable log panel
- **AI Settings integrated into Settings dialog** — the gear icon in TermLens now
  opens a tabbed settings dialog with separate tabs for Glossary and AI configuration

---

## [2.1.0] — 2026-03-06

### Added
- **Non-translatable terms** — mark terms as non-translatable (brand names, product
  codes, abbreviations that stay the same across languages); the source term is copied
  verbatim as the target
- **Ctrl+Alt+N quick-add shortcut** — select text in the source or target column and
  press Ctrl+Alt+N to instantly mark it as non-translatable in all Write glossaries
- **Right-click toggle** — right-click any term block and choose "Mark as
  Non-Translatable" or "Mark as Translatable" to toggle the flag without opening a
  dialog
- **Non-translatable checkbox in Add Term dialog** — when checked, the target field
  auto-fills with the source text and becomes read-only
- **Yellow visual distinction** — non-translatable terms appear with a light yellow
  background (#FFF3D0) in the TermLens panel, the Term Picker popup, and the Glossary
  Editor; color precedence: yellow (non-translatable) > pink (project) > blue (regular)
- **NT column in Glossary Editor** — checkbox column to view and toggle
  non-translatable status per term
- **Select/deselect all in Settings** — click the Read, Write, or Project column
  headers to toggle all checkboxes at once; tooltips explain the feature

### Changed
- **Database schema migration** — the `is_nontranslatable` column is automatically
  added to existing databases on first access; fully backward-compatible

---

## [2.0.1] — 2026-03-05

### Changed
- **Faster quick-add term workflow** — Alt+Down and Alt+Up now use incremental
  in-memory index updates instead of reloading the entire termbase database;
  batch inserts use a single SQLite transaction instead of one connection per
  glossary; right-click edit and delete also use the incremental path
- **License changed to source-available** — source code remains viewable and
  forkable for personal use; binary redistribution restricted to copyright holder

---

## [2.0.0] — 2026-03-05

### Added
- **Tabbed ViewPart UI** — the plugin now uses a tabbed panel with separate tabs for
  TermLens (glossary), AI Assistant, and Batch Translate; AI features are placeholder
  tabs that will be implemented in upcoming releases

### Changed
- **Renamed from TermLens to Supervertaler for Trados** — the plugin is now part of the
  Supervertaler product family; the TermLens glossary panel retains its name as a feature
  within the larger plugin
- **New assembly name** — `Supervertaler.Trados.dll` (was `TermLens.dll`); namespace changed
  from `TermLens` to `Supervertaler.Trados`
- **New plugin identity** — Trados treats this as a new plugin; users upgrading from TermLens
  should uninstall the old plugin first
- **Settings auto-migration** — settings are automatically copied from the old
  `%LocalAppData%\TermLens\` location to `%LocalAppData%\Supervertaler.Trados\` on first run

### Fixed
- **Word alignment in TermLens panel** — unmatched words now align vertically with
  matched term source text (fixed margin/padding mismatch and switched to consistent
  GDI+ text rendering)

---

## [1.6.0] — 2026-03-05

### Added
- **F2 expand selection to word boundaries** — press F2 after making a rough
  partial text selection in the source or target pane; the selection automatically
  expands to encompass the complete words at each end (e.g. selecting "et recht"
  becomes "het rechtstreeks")
- **Smart word expansion for term adding** — the Add Term dialog and Quick Add
  Term action now auto-expand partial selections to full word boundaries before
  populating the term pair, so you no longer need pixel-perfect text selection
- **Multiple Write glossaries** — the Write column in Settings now allows checking
  multiple glossaries; new terms are inserted into all Write-checked glossaries at
  once

### Changed
- **Term Picker shortcut** — changed from Ctrl+Shift+G to **Ctrl+Alt+G**
- **Quick Add action renamed** — "Quick add term to glossaries set to 'Read'" →
  "Quick Add Term to Glossary Set to 'Write'" (reflecting its actual behaviour)

### Fixed
- **Duplicate terms in Term Picker** — when the same source term matched at
  multiple positions in a segment (e.g. "cap" appearing twice), it was listed
  multiple times in the picker; matches are now deduplicated and renumbered
  sequentially

---

## [1.5.0] — 2026-03-04

### Added
- **Standalone database creation** — "Create New…" button in Settings creates a fresh
  Supervertaler-compatible SQLite database from scratch, so TermLens can function
  independently without Supervertaler installed
- **Glossary management** — "+" and "−" buttons in Settings to create and delete
  individual glossaries inside a database; new glossary dialog collects name, source
  language, and target language
- **TSV import** — bulk import terms from tab-separated files matching Supervertaler's
  format (pipe-delimited synonyms, `[!forbidden]` markers, UUID-based duplicate
  detection); flexible header mapping supports multiple column name conventions
- **TSV export** — export all terms from a glossary to the same TSV format, so files
  are fully interchangeable between Supervertaler and TermLens
- **Alt+Down quick-add shortcut** — adds the current source/target text directly to
  the Write glossary (replaces the previous Ctrl+Alt+Shift+T binding)
- **Alt+Up quick-add to project glossary** — new action that adds the current
  source/target text directly to the Project glossary (no dialog)

### Changed
- **Project column is now single-select** — the Project column in Settings uses
  radio-button behavior (only one glossary can be the project glossary at a time),
  matching the single Write glossary pattern
- **Context menu reorganised** — the "Add Term to TermLens" actions are now grouped
  under a separator in the editor context menu, with clearer names ("Add Term to
  TermLens (dialogue)" and "Quick add Term to glossaries set to 'Read'")
- **A+/A− button font sizes** — adjusted for better visual balance (A+ uses 9pt,
  A− uses 7pt instead of both using 7.5pt)

### Fixed
- **Term block text truncation** — TermBlock now recalculates its size when the font
  changes (via `OnFontChanged` override), preventing clipped text after A+/A− resizing

---

## [1.4.0] — 2026-03-04

### Added
- **Adjustable font size** — A+ and A− buttons in the TermLens panel header let you
  increase or decrease the font size on the fly while working; also configurable via a
  "Panel font size" control in the Settings dialog; size persists across Trados restarts
- **Dialog size persistence** — the Term Picker dialog remembers its window size and
  column widths between invocations (and across Trados restarts); the Settings dialog
  also remembers its window size

### Changed
- **Subtler expand indicator in Term Picker** — replaced the ► symbol next to source
  terms with a small ▸ triangle in the # column; less visually distracting while still
  indicating which rows have expandable synonyms
- **Double-digit shortcut badges** — numbers 10+ in the TermLens panel now use a
  pill-shaped (rounded rectangle) badge instead of a circle, so double-digit numbers
  are no longer clipped
- **Wider Project column** — increased from 62 px to 72 px in the Settings dialog so
  the "Project" header is no longer truncated

---

## [1.3.0] — 2026-03-04

### Added
- **Alt+digit term insertion** — press Alt+1 through Alt+9 to instantly insert the
  corresponding matched term into the target segment; Alt+0 inserts term 10; for
  segments with 10+ matches, two-digit chords are supported (e.g. Alt+1 then 3
  within 400ms inserts term 13)
- **Term Picker dialog** — press Ctrl+Shift+G to open a modal dialog listing all
  matched terms for the current segment; select by clicking, pressing Enter, or
  typing the term number
- **Synonym expansion in Term Picker** — rows with multiple target translations
  show a ► indicator; press Right arrow to expand and reveal all alternative
  translations, Left arrow to collapse
- **Bulk synonym loading** — target synonyms from the `termbase_synonyms` table are
  now loaded at startup alongside term entries, so the +N badges and Term Picker
  expansion show the correct synonym counts
- **Project glossary column in Settings** — a new "Project" checkbox column in the
  settings dialog lets you mark glossaries as project glossaries; project terms are
  shown in pink, all others in blue (replaces the previous database-driven priority
  colouring which was unreliable)

### Changed
- **Coloring is user-controlled** — pink/blue term colouring is now determined by
  the user's "Project" setting per glossary, not by the database's ranking or
  is_project_termbase fields
- **Wider settings columns** — the Read, Write, and Project checkbox columns in the
  settings dialog are now wide enough for their headers to be fully visible

---

## [1.2.0] — 2026-03-04

### Added
- **Add Term to TermLens** — right-click context menu action in the Trados editor to
  add a new term from the active segment's source and target text; opens a confirmation
  dialog where you can edit the term pair and optionally add a definition before saving
- **Quick add Term to TermLens** — a second context menu action that bypasses the dialog
  and saves the source/target text directly as a new term for faster workflow
- **Keyboard shortcuts** — Add Term defaults to Ctrl+Alt+T, Quick Add to
  Ctrl+Alt+Shift+T (both reassignable via Trados keyboard shortcut settings)
- **Settings: Read/Write columns** — the termbase list in settings is now a grid with
  separate Read and Write checkboxes; Read controls which termbases are searched,
  Write selects the single termbase that receives new terms (radio-button style)

### Changed
- **ViewPart docks above the editor** — TermLens now opens above the translation grid
  (previously docked at the side) and opens pinned/visible instead of auto-hidden
- **Term badge sizing** — the "+N" synonym count badges on term blocks are no longer
  truncated; width calculations now use ceiling rounding instead of integer truncation

---

## [1.1.0] — 2026-03-04

### Changed
- **Renamed project from Termview to TermLens** — all files, namespaces, class names,
  plugin IDs, settings paths, and documentation updated consistently
- **Migrated from System.Data.SQLite to Microsoft.Data.Sqlite** — eliminates the
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

## [1.0.0] — 2026-03-03

First public release.

### Added
- **Word-by-word source segment display** — every word of the active source segment
  is shown in a flowing left-to-right layout, updated as you navigate between segments
- **Terminology highlighting** — words that match a loaded termbase are shown in
  a coloured block (blue for regular terms, pink for project termbases) with the
  target-language translation displayed directly underneath
- **Multi-word term matching** — multi-word entries (e.g. "machine translation") are
  matched and highlighted as a single block, taking priority over single-word matches
- **Click to insert** — clicking a term block inserts the target translation at the
  cursor position in the target segment
- **Termbase settings** — gear button (⚙) in the panel header opens a settings
  dialog for selecting a Supervertaler termbase (`.db`) file; settings are saved to
  `%LocalAppData%\TermLens\settings.json` and the termbase is auto-loaded on startup
- **Auto-detect** — if no termbase is configured, TermLens automatically checks the
  default Supervertaler data directories (`~/Supervertaler_Data/resources/` and
  `%LocalAppData%\Supervertaler/resources/`)
- **Live termbase preview** — the settings dialog shows the termbase name, total
  term count, and source/target language pair after a file is selected

### Technical
- Reads Supervertaler's SQLite termbase format (`supervertaler.db`) directly —
  no separate export step needed
- Docks as a ViewPart below the Trados Studio editor (compatible with Studio 2024 / Studio18)
- Built on .NET Framework 4.8 with strong-name signing (`PublicKeyToken=6afde1272ae2306a`)
- Packaged in OPC format (`.sdlplugin`) as required by the Trados plugin framework
