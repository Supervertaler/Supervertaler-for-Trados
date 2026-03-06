# Changelog

## [Unreleased]

### Planned
- **Prompt manager / library** — manage system and custom prompts for AI translation
- **AI chat assistant** — project-aware chat interface docked in Trados
- **TBX support** — to be added simultaneously in both Supervertaler and this plugin

---

All notable changes to Supervertaler for Trados (formerly TermLens) will be documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version numbers follow [Semantic Versioning](https://semver.org/).

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
