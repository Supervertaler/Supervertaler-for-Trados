# Changelog

## [4.3.0-beta] — 2026-03-13

### Added
- **Per-project settings** — switching between Trados projects now automatically saves and restores the Supervertaler database path, enabled/disabled termbases, write targets, project termbase, and AI context termbase filters; settings are stored per-project in `%LocalAppData%\Supervertaler.Trados\projects\` and applied automatically when the active document changes
- **Per-project settings documentation** — new help page documenting how per-project settings work, what's saved per-project vs globally, and how the automatic switching behaves

### Fixed
- **Licence null-status crash** — when the Lemon Squeezy API returned a null or empty `status` field during activation, the licence was treated as invalid even though the key was activated; now treats null status as active when the licence has a valid instance ID
- **Trial period mismatch** — `LicenseInfo.cs` used a hardcoded 14-day trial window while `LicenseManager.cs` used 90 days; unified both to the 90-day constant
- **AI Settings termbase list stale after database switch** — switching Supervertaler databases in the TermLens settings tab didn't update the AI Settings tab's termbase checklist until the dialog was closed and reopened; the AI context panel now refreshes immediately when the termbase list changes
- **Term Picker shortcut documented incorrectly** — the About dialog and help docs showed `Ctrl+Shift+G` but the actual shortcut is `Ctrl+Alt+G`; corrected all references

### Improved
- **Keyboard shortcuts documentation** — added Mac/Parallels equivalents (Ctrl → Control, Alt → Option) to all shortcut tables for users running Trados in Parallels

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
- **Merge dialog cutoff** — the "Similar Term Found" dialog is now wider (520×310) to prevent text truncation on longer term pairs

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
