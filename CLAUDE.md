# Supervertaler for Trados – Claude Context

## What this project is
Supervertaler for Trados is a Trados Studio 2024 (v18) plugin that brings key Supervertaler features into the Trados ecosystem. It uses a **tabbed ViewPart** with separate tabs for each feature:

- **TermLens** — live inline terminology display (termbase panel) — fully implemented
- **AI Assistant** — project-aware chat interface in a separate dockable panel — fully implemented (multimodal, TM matches, AI context control)
- **Batch Translate** — AI-powered segment translation — fully implemented (OpenAI/Anthropic/Google)
- **Prompt Library** — custom prompt management — fully implemented (1 default translate prompt, 1 proofreading prompt, 6 QuickLauncher prompts)

### Tech stack
- **Language**: C# / .NET Framework 4.8, SDK-style .csproj
- **Namespace**: `Supervertaler.Trados` (sub-namespaces: `.Controls`, `.Core`, `.Models`, `.Settings`)
- **Build**: `bash build.sh` from repo root (dotnet build → package_plugin.py → deploy)
- **Deploy target**: `%APPDATA%\Trados\Trados Studio\18\Plugins\Packages\Supervertaler for Trados.sdlplugin`
- **Strong-name key**: `src/Supervertaler.Trados/Supervertaler.Trados.snk` — PublicKeyToken: `6afde1272ae2306a`
  (Trados's `DefaultPluginTypeLoader` refuses unsigned assemblies — this is non-negotiable)

---

## UI architecture

The ViewPart ("Supervertaler for Trados") uses a three-layer structure:

```
TermLensEditorViewPart (AbstractViewPartController)
  └── MainPanelControl (UserControl, IUIControl) — tabbed container
        ├── Tab "TermLens" → TermLensControl (terminology panel with header, flow panel)
        ├── Tab "AI Assistant" → launcher panel (activates the dockable AI Assistant panel)
        └── Tab "Batch Translate" → BatchTranslateControl (scope/prompt/provider, translate button, log)

AiAssistantViewPart (AbstractViewPartController) — planned, separate dockable panel
  └── AiAssistantControl — chat UI (message history, input, context)
```

- `TermLensEditorViewPart` owns the lifecycle, settings, and event routing
- `MainPanelControl` is a thin wrapper holding the `TabControl`
- `TermLensControl` is the existing terminology panel (header with A+/A−/gear buttons, FlowLayoutPanel with TermBlock/WordLabel controls)
- Both `_control` (TermLensControl) and `_mainPanel` (MainPanelControl) are lazy singletons; all existing `_control.Value` references work unchanged

---

## SQLite library: Microsoft.Data.Sqlite (not System.Data.SQLite)

We use **`Microsoft.Data.Sqlite`** + SQLitePCLRaw (native DLL: `e_sqlite3.dll`).

**Do NOT switch to `System.Data.SQLite`** — it uses `SQLite.Interop.dll` with a version-fingerprint
hash scheme (`SI04b638e115f7beb4` etc.) that causes `EntryPointNotFoundException` inside Trados
Studio's plugin environment. The root cause: other apps (memoQ, Glossary Converter) ship their
own `SQLite.Interop.dll` with different hashes, and Windows's DLL loader picks the wrong one.
Microsoft.Data.Sqlite uses standard SQLite3 C entry points — no version-hash conflicts.

`AppInitializer.cs` pre-loads `e_sqlite3.dll` by full path and handles `AssemblyResolve` for all
managed DLLs we ship (Microsoft.Data.Sqlite, SQLitePCLRaw, System.Memory, etc.) because Trados
ships older versions of several .NET Standard polyfills.

---

## MultiTerm .sdltb termbase support

TermLens reads MultiTerm `.sdltb` termbases (JET4/MDB format) attached to the active Trados project
and displays their terms as green chips alongside Supervertaler terms.

- **Primary access**: Opens `.sdltb` files directly via `System.Data.OleDb` — tries JET 4.0 first
  (built into Windows for 32-bit processes, i.e. Trados Studio), then ACE OLEDB 16.0–12.0.
  `MultiTermReader.cs` handles this.
- **Fallback**: If no OleDb driver works, `TerminologyProviderFallback.cs` uses Trados's
  `ITerminologyProviderManager` API for per-segment search with LRU caching.
- **Detection**: `MultiTermProjectDetector.DetectTermbases()` reads the project's
  `TermbaseConfiguration` to find `.sdltb` file paths and language index mappings.
- **Auto-refresh**: File modification timestamps are tracked per `.sdltb` file; on each segment
  change, `HasMultiTermFileChanged()` checks if any file was modified and reloads if so.
- **Read-only**: MultiTerm entries have `IsMultiTerm = true` and negative IDs. Edit/delete/NT
  context menu items are suppressed. Green chip color (`#D4EDDA`).
- **Settings**: MultiTerm termbases appear in the settings grid with "[MultiTerm]" label and
  green tint. Read toggles visibility; Write and Project are always disabled.
- **IDs**: Synthetic negative IDs derived from the file path hash, avoiding collision with
  SQLite rowids from Supervertaler termbases.

---

## Key files

| File | Purpose |
|------|---------|
| `src/Supervertaler.Trados/TermLensEditorViewPart.cs` | Main ViewPart controller — Initialize(), segment events, settings, Alt+digit chords |
| `src/Supervertaler.Trados/Controls/MainPanelControl.cs` | Tabbed container (IUIControl) — hosts TermLens tab and future AI tabs |
| `src/Supervertaler.Trados/Controls/TermLensControl.cs` | TermLens terminology panel — header bar, FlowLayoutPanel with term blocks |
| `src/Supervertaler.Trados/Controls/TermBlock.cs` | Individual term chip (custom-painted) + WordLabel for unmatched words |
| `src/Supervertaler.Trados/AppInitializer.cs` | Runs at Trados startup; pre-loads `e_sqlite3.dll`, registers `AssemblyResolve` |
| `src/Supervertaler.Trados/Core/TermbaseReader.cs` | SQLite reader — Open(), LoadAllTerms(), InsertTerm(), InsertTermBatch(), UpdateTerm() |
| `src/Supervertaler.Trados/Core/TermMatcher.cs` | In-memory term matching + incremental AddEntry()/RemoveEntry() + MergeIndex() for MultiTerm |
| `src/Supervertaler.Trados/Core/MultiTermReader.cs` | Opens .sdltb files via OleDb (JET 4.0 / ACE), bulk-loads terms |
| `src/Supervertaler.Trados/Core/MultiTermProjectDetector.cs` | Detects MultiTerm termbases from active Trados project |
| `src/Supervertaler.Trados/Core/TerminologyProviderFallback.cs` | API fallback for per-segment search when OleDb fails |
| `src/Supervertaler.Trados/Settings/TermLensSettings.cs` | JSON settings at `%LocalAppData%\Supervertaler.Trados\settings.json` |
| `src/Supervertaler.Trados/Settings/TermLensSettingsForm.cs` | Settings dialog — termbase picker, termbase management, import/export |
| `src/Supervertaler.Trados/Supervertaler.Trados.plugin.xml` | Extension manifest (UTF-16 LE — edit via Python to preserve encoding) |
| `src/Supervertaler.Trados/Core/HelpSystem.cs` | Context-sensitive help — maps UI elements to GitBook docs pages |
| `build.sh` | Build → package → deploy script; aborts if Trados is running |
| `package_plugin.py` | Creates OPC-format `.sdlplugin` (NOT plain ZIP — needs `[Content_Types].xml`, `_rels/`) |

### External resources

| What | URL / Path |
|------|------------|
| Website (Trados page) | `https://supervertaler.com/trados.html` (source: `Supervertaler-Workbench` repo `docs/trados.html`) |
| Documentation (GitBook) | `https://supervertaler.gitbook.io/trados` |
| Docs source (git-synced) | `docs/` in this repo |

---

## Build / deploy rules

- **Trados must be fully closed** before running `bash build.sh` — it locks plugin files and skips re-extraction if `Unpacked/Supervertaler.Trados/` is non-empty. `build.sh` detects this via `tasklist.exe` and aborts.
- `build.sh` wipes `%LOCALAPPDATA%\Trados\...\Plugins\Unpacked\Supervertaler.Trados\` before deploying so Trados re-extracts cleanly on next start.
- `.sdlplugin` is OPC (Open Packaging Convention), like `.docx`. Requires `[Content_Types].xml` and `_rels/` entries — plain ZIP will silently fail to load.

---

## Naming conventions

- **Plugin name**: "Supervertaler for Trados" (visible in Trados docking header and plugin manager)
- **Terminology panel name**: "TermLens" (tab label inside the ViewPart — kept as the feature name)
- **Action IDs**: Prefixed with `TermLens_` for terminology-related actions (e.g. `TermLens_AddTerm`, `TermLens_TermPicker`); do NOT rename these — users may have custom shortcut overrides
- **Class names**: TermLens-prefixed classes (`TermLensEditorViewPart`, `TermLensControl`, etc.) are the terminology feature; future AI classes will use different naming
- **Settings auto-migrate** from old `%LocalAppData%\TermLens\` to `%LocalAppData%\Supervertaler.Trados\` on first run

---

## SQLite / WAL notes

- `supervertaler.db` uses WAL mode (Write-Ahead Log). Leftover `.db-wal` / `.db-shm` files after non-clean Supervertaler shutdown are harmless — SQLite replays the WAL on next open.
- Connection string uses `SqliteConnectionStringBuilder` with `Mode = SqliteOpenMode.ReadOnly` — safe for concurrent access while Supervertaler has the DB open.

---

## Term add/edit/delete: incremental index updates

The quick-add actions (Alt+Down, Alt+Up) and right-click edit/delete use **incremental in-memory index updates** instead of reloading the entire database:

- **`TermMatcher.AddEntry(TermEntry)`** — inserts one entry into `_termIndex` under both the lowercase key and stripped-punctuation variant. O(1).
- **`TermMatcher.RemoveEntry(long termId)`** — removes entries by ID from all keys.
- **`TermbaseReader.InsertTermBatch()`** — inserts into multiple write termbases in a single SQLite connection + transaction, instead of one connection per termbase.
- **`NotifyTermInserted(List<TermEntry>)`** — adds entries to the index and refreshes the UI. No settings reload, no DB reload.
- **`NotifyTermDeleted(long termId)`** — removes from index and refreshes.
- **`NotifyTermAdded()`** — the old full-reload path. Still used by the settings dialog when the user toggles termbases.

The edit handler (right-click → Edit) does a remove + add of the updated entry.

On app startup or settings change, `LoadTermbase(forceReload: true)` still does a full DB load to ensure consistency.

---

## Non-translatable terms

Terms can be marked as **non-translatable** (brand names, product codes, abbreviations that stay the same across languages). These are stored with `is_nontranslatable = 1` in the `termbase_terms` table and `TargetTerm = SourceTerm`.

- **Visual**: Non-translatable chips render with a **light yellow background** (`#FFF3D0`). Color precedence: non-translatable (yellow) > project (pink) > regular (blue).
- **Keyboard shortcut**: `Ctrl+Alt+N` — quick-adds the selected source text as non-translatable to all Write termbases (target is set to source automatically). Only requires source text selected.
- **Right-click menu**: "Mark as Non-Translatable" / "Mark as Translatable" toggle on any term chip. Uses `TermbaseReader.SetNonTranslatable()` for a lightweight DB update.
- **Add Term dialog**: "Non-translatable" checkbox auto-fills target = source and makes target read-only when checked. Pre-populates from `TermEntry.IsNonTranslatable` in edit mode.
- **Alt+digit insertion**: Works unchanged — inserts `TargetTerm` which equals `SourceTerm` for non-translatables.
- **Term Picker** (Ctrl+Alt+Down): Shows yellow background for non-translatable matches.
- **Termbase Editor**: "NT" checkbox column for toggling per-term.
- **DB migration**: `MigrateSchema()` uses `PRAGMA table_info` to detect the column and `ALTER TABLE ADD COLUMN` if missing. Called from `Open()` (via `HasColumn`) and all static write methods. Idempotent and backward-compatible with older Supervertaler databases.
- **Action ID**: `TermLens_QuickAddNonTranslatable` — do NOT rename (users may have custom shortcut overrides).

---

## License

Source-available license (not MIT). Source code viewable/forkable for personal use, but binary redistribution (.sdlplugin) restricted to copyright holder. Pre-built binaries available at supervertaler.com.

---

## Monetization

- Source code is open on GitHub (source-available license)
- 14-day free trial (no credit card required), full feature access
- Two paid tiers:
  - **TermLens Edition** — €10/month or €120/year (terminology features)
  - **TermLens + Assistant Edition** — €15/month or €180/year (terminology + AI features, priority support)
- Payment platform: TBD (Lemon Squeezy or similar — handles EU VAT)
- License key validation planned: key entered in plugin settings, validated against payment platform API

---

## Planned features

### AI Chat Assistant (implemented)

The AI Assistant is a separate dockable ViewPart (`AiAssistantViewPart` + `AiAssistantControl`) registered in `plugin.xml`. Key implementation details:

- **Separate dockable ViewPart** — fully native Trados dockable panel. Users can dock it right, bottom, floating, or on a second monitor. Position/size persists across sessions automatically.
- **The "AI Assistant" tab is a launcher** — shows an "Open AI Assistant" button that activates the dockable panel.
- **Project-aware context** — the assistant has access to: current segment (source + target), termbase terms (filterable per-termbase via AI Context settings), and TM fuzzy matches (toggleable).
- **Multimodal image support** — users can paste (Ctrl+V), drag-drop, or browse images. Each provider uses its native vision API format (OpenAI content arrays, Claude image blocks, Gemini inline_data, Ollama images array).
- **Markdown rendering** — `MarkdownToRtf` converts LLM markdown output to RTF for display in `ChatBubble` RichTextBox controls.
- **Apply to target** — right-click assistant responses to insert text into the active Trados segment.
- **AI Context control** — `AiSettings.DisabledAiTermbaseIds` filters which termbases contribute terms to prompts; `AiSettings.IncludeTmMatches` toggles TM match injection into the system prompt.
