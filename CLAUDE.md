# Supervertaler for Trados – Claude Context

## What this project is
Supervertaler for Trados is a Trados Studio 2024 (v18) plugin that brings key Supervertaler features into the Trados ecosystem. It uses a **tabbed ViewPart** with separate tabs for each feature:

- **TermLens** – live inline terminology display (termbase panel) – fully implemented
- **AI Assistant** – project-aware chat interface in a separate dockable panel – fully implemented (multimodal, TM matches, AI context control)
- **Batch Translate** – AI-powered segment translation – fully implemented (OpenAI/Anthropic/Google)
- **Prompt Library** – custom prompt management – fully implemented (1 default translate prompt, 1 proofreading prompt, 6 QuickLauncher prompts)

### Tech stack
- **Language**: C# / .NET Framework 4.8, SDK-style .csproj
- **Namespace**: `Supervertaler.Trados` (sub-namespaces: `.Controls`, `.Core`, `.Models`, `.Settings`)
- **Build**: `bash build.sh` from repo root (dotnet build → package_plugin.py → deploy)
- **Deploy target**: `%LocalAppData%\Trados\Trados Studio\18\Plugins\Packages\Supervertaler for Trados.sdlplugin` (matches the recommended "This computer for me only" Trados Plugin Installer option; switched from Roaming in v4.19.25)
- **Strong-name key**: `src/Supervertaler.Trados/Supervertaler.Trados.snk` – PublicKeyToken: `6afde1272ae2306a`
  (Trados's `DefaultPluginTypeLoader` refuses unsigned assemblies – this is non-negotiable)

---

## UI architecture

The ViewPart ("Supervertaler for Trados") uses a three-layer structure:

```
TermLensEditorViewPart (AbstractViewPartController)
  └── MainPanelControl (UserControl, IUIControl) – tabbed container
        ├── Tab "TermLens" → TermLensControl (terminology panel with header, flow panel)
        ├── Tab "AI Assistant" → launcher panel (activates the dockable AI Assistant panel)
        └── Tab "Batch Translate" → BatchTranslateControl (scope/prompt/provider, translate button, log)

AiAssistantViewPart (AbstractViewPartController) – planned, separate dockable panel
  └── AiAssistantControl – chat UI (message history, input, context)
```

- `TermLensEditorViewPart` owns the lifecycle, settings, and event routing
- `MainPanelControl` is a thin wrapper holding the `TabControl`
- `TermLensControl` is the existing terminology panel (header with A+/A−/gear buttons, FlowLayoutPanel with TermBlock/WordLabel controls)
- Both `_control` (TermLensControl) and `_mainPanel` (MainPanelControl) are lazy singletons; all existing `_control.Value` references work unchanged

---

## SQLite library: Microsoft.Data.Sqlite (not System.Data.SQLite)

We use **`Microsoft.Data.Sqlite`** + SQLitePCLRaw (native DLL: `e_sqlite3.dll`).

**Do NOT switch to `System.Data.SQLite`** – it uses `SQLite.Interop.dll` with a version-fingerprint
hash scheme (`SI04b638e115f7beb4` etc.) that causes `EntryPointNotFoundException` inside Trados
Studio's plugin environment. The root cause: other apps (memoQ, Glossary Converter) ship their
own `SQLite.Interop.dll` with different hashes, and Windows's DLL loader picks the wrong one.
Microsoft.Data.Sqlite uses standard SQLite3 C entry points – no version-hash conflicts.

`AppInitializer.cs` pre-loads `e_sqlite3.dll` by full path and handles `AssemblyResolve` for all
managed DLLs we ship (Microsoft.Data.Sqlite, SQLitePCLRaw, System.Memory, etc.) because Trados
ships older versions of several .NET Standard polyfills.

---

## MultiTerm .sdltb termbase support

TermLens reads MultiTerm `.sdltb` termbases (JET4/MDB format) attached to the active Trados project
and displays their terms as green chips alongside Supervertaler terms.

- **Primary access**: Opens `.sdltb` files directly via `System.Data.OleDb` – tries JET 4.0 first
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
| `src/Supervertaler.Trados/TermLensEditorViewPart.cs` | Main ViewPart controller – Initialize(), segment events, settings, Alt+digit chords |
| `src/Supervertaler.Trados/Controls/MainPanelControl.cs` | Tabbed container (IUIControl) – hosts TermLens tab and future AI tabs |
| `src/Supervertaler.Trados/Controls/TermLensControl.cs` | TermLens terminology panel – header bar, FlowLayoutPanel with term blocks |
| `src/Supervertaler.Trados/Controls/TermBlock.cs` | Individual term chip (custom-painted) + WordLabel for unmatched words |
| `src/Supervertaler.Trados/AppInitializer.cs` | Runs at Trados startup; pre-loads `e_sqlite3.dll`, registers `AssemblyResolve` |
| `src/Supervertaler.Trados/Core/TermbaseReader.cs` | SQLite reader – Open(), LoadAllTerms(), InsertTerm(), InsertTermBatch(), UpdateTerm() |
| `src/Supervertaler.Trados/Core/TermMatcher.cs` | In-memory term matching + incremental AddEntry()/RemoveEntry() + MergeIndex() for MultiTerm |
| `src/Supervertaler.Trados/Core/MultiTermReader.cs` | Opens .sdltb files via OleDb (JET 4.0 / ACE), bulk-loads terms |
| `src/Supervertaler.Trados/Core/MultiTermProjectDetector.cs` | Detects MultiTerm termbases from active Trados project |
| `src/Supervertaler.Trados/Core/TerminologyProviderFallback.cs` | API fallback for per-segment search when OleDb fails |
| `src/Supervertaler.Trados/Settings/TermLensSettings.cs` | JSON settings at `%LocalAppData%\Supervertaler.Trados\settings.json` |
| `src/Supervertaler.Trados/Settings/TermLensSettingsForm.cs` | Settings dialog – termbase picker, termbase management, import/export |
| `src/Supervertaler.Trados/Supervertaler.Trados.plugin.xml` | Extension manifest (UTF-16 LE – edit via Python to preserve encoding) |
| `src/Supervertaler.Trados/Core/HelpSystem.cs` | Context-sensitive help – maps UI elements to GitBook docs pages |
| `build.sh` | Build → package → deploy script; aborts if Trados is running |
| `package_plugin.py` | Creates OPC-format `.sdlplugin` (NOT plain ZIP – needs `[Content_Types].xml`, `_rels/`) |

### External resources

| What | URL / Path |
|------|------------|
| Website (Trados page) | `https://supervertaler.com/trados.html` (source: `Supervertaler-Workbench` repo `docs/trados.html`) |
| Documentation | `https://docs.supervertaler.com/trados/` (Astro/Starlight site on Cloudflare Pages, source in [`Supervertaler-Help`](https://github.com/Supervertaler/Supervertaler-Help) repo) |
| Docs source (git-synced) | `docs/` in this repo |

---

## Build / deploy rules

- **Changing a keyboard shortcut means editing `Supervertaler.Trados.plugin.xml` too — the `[Shortcut]` attribute alone does nothing.** Studio registers shortcuts from the *manifest*, which is checked in and hand-maintained (`bump_version.py` only rewrites versions in it; nothing regenerates it from the assembly). Change the attribute alone and the build succeeds, the plugin loads, and the old key is still bound — with the right-click menu still showing it. Caught the hard way when QuickLauncher moved to Alt+Q in 20.184.
  - The value is a `System.Windows.Forms.Keys` flags enum as a plain integer: `Q`=`0x51`=81, `Control`=`0x20000`=131072, `Alt`=`0x40000`=262144, `Shift`=`0x10000`=65536. So Ctrl+Q is 131153 and Alt+Q is 262225. Find the `<extension>` block for the action class, then its `ShortcutAttribute` → `constructorArgs` → `arg`.
  - **UTF-16 LE with a BOM**: edit via Python with `newline=''` on **both** the read and the write. Universal-newline mode on read turns CRLF into LF and the write then loses one byte per line — an 1756-byte diff on a file where the intended change is zero bytes. Check the byte-size delta afterwards; it should be 0 for a same-digit-count swap.
  - Also update: the About dialog's shortcut list, the help docs (a shortcut is typically referenced on 5-10 pages), and the conflict table if the old or new key collides with a Trados default.
- **Trados must be fully closed** before running `bash build.sh` – it locks plugin files and skips re-extraction if `Unpacked/Supervertaler.Trados/` is non-empty. `build.sh` detects this via `tasklist.exe` and aborts.
- `build.sh` wipes `%LocalAppData%\Trados\...\Plugins\Unpacked\Supervertaler for Trados\` before deploying so Trados re-extracts cleanly on next start. It also removes any leftover spaced-name `.sdlplugin` and Unpacked folder from `%AppData%\Trados\...\Plugins\` (the deploy target before v4.19.25) so `HandlePendingUpdate` doesn't pick the stale Roaming copy.
- `.sdlplugin` is OPC (Open Packaging Convention), like `.docx`. Requires `[Content_Types].xml` and `_rels/` entries – plain ZIP will silently fail to load.

---

## Ad-hoc PowerShell tests: use `-File`, never a long inline `-Command`

- **Windows Defender silently kills long inline PowerShell that reflectively loads the built plugin.** The signature is `Trojan:Win32/ClickFix.IIN!MTB`, and it is a false positive — but the process is terminated *before it runs*, so the caller gets empty output rather than an error. That reads like "the method returned nothing", i.e. a code bug, and sends you debugging the wrong thing. It killed the figures smoke test four times in 93 seconds on 2026-09-07 (23:33–23:35), and had been doing it since at least 1 September.
  - The heuristic matches the *shape* of a ClickFix payload: `powershell -NoProfile -ExecutionPolicy Bypass -Command` plus a long inline script, `[Reflection.Assembly]::LoadFrom`, then `GetMethod(...).Invoke(...)`, launched from Git's `bash.exe` via `eval`. Every ingredient is innocent here; the silhouette is not.
  - **Fix: write the script to a file under `.dev/` and run `powershell -NoProfile -File ".dev\<name>.ps1"`.** Verified to run clean with real-time protection fully on — the heuristic keys on the inline command-line string, and `-File` presents none. Do *not* add a Defender exclusion for the Dev tree; it needs admin and trades real protection for a cosmetic problem.
  - `.dev/` is gitignored. These scripts hardcode real client document paths, and **this repo is public** — never move one to a tracked location. See *Confidentiality* above.
- **Two traps when moving an inline block into a script file:**
  - `$args` is the automatic script-argument array inside a `-File` script. The inline version assigned straight over it. Name the array something else (`$rfArgs`) — several `FiguresFile` methods write back through it and it is read again after `Invoke`.
  - `New-Object "System.Collections.Generic.HashSet[string]"` inline in an argument array hands `Invoke` a `PSObject` wrapper that will not convert to `ISet<string>`. Build it as its own typed variable: `[System.Collections.Generic.HashSet[string]]::new()`.
- **Loading the plugin outside Studio needs an `AssemblyResolve` hook.** `Sdl.Core.PluginFramework` and friends live in `C:\Program Files (x86)\Trados\Trados Studio\Studio18`, not next to the build output, and nothing resolves them outside Studio's own process. `.dev/figures-smoke.ps1` has a working hook to copy.

---

## Release channels (GitHub vs RWS App Store)

Two channels with **different semantics – do not mirror one onto the other.** `CHANGELOG.md` is the single source of truth both draw from.

**Single-channel distribution (from 2026-08-02): the plugin binary ships ONLY through the RWS App Store.** GitHub releases carry the notes, the tag, and the MCP server assets – never the `.sdlplugin`.

| | **GitHub releases** | **RWS App Store** |
|---|---|---|
| Tracks | **builds** | **approvals** |
| Mutability | immutable, append-only, **never deleted** | rolling – delete the unapproved one and re-upload a bigger cumulative one as needed |
| Changelog baseline | the **previous GitHub release tag** (auto via `gh`) | the last **published** App Store version (passed manually) |
| Carries the plugin? | **No** – notes + MCP assets only | **yes** – the signed `.sdlplugin`, the only channel that does |

Rules:
- **Never delete a GitHub release.** The App Store delete/re-upload dance stays entirely on the App Store side and never touches a GitHub release.
- **Cut a GitHub release** whenever you'd submit to the App Store (Sunday cadence) **plus** for any urgent mid-week build a user is actively waiting on. Tag = the exact built version.
- **Submit to the App Store late on Sunday, not earlier in the week.** RWS is closed at weekends and nothing submitted before Monday gets looked at any sooner — so a Wednesday submission buys no review time, it only freezes the build four days early. Submitting late on Sunday means every fix landed that week ships in the same review round. A GitHub release can be cut whenever a build is ready; only the App Store submission waits for the Sunday slot.
- **Never attach the plugin to a GitHub release.** Landed in **18.20.156** (2026-08-02), reversing the "attach both zips" policy that ran from v4.20.45 to 18.20.155, for three reasons – recorded in the 20.156 changelog entry and `tools/github_release.py`'s docstring: App Store builds are RWS-signed and GitHub ones were not, so every Studio start nagged about an "unsigned plug-in"; the in-plugin update check queries the App Store catalogue, so a GitHub installer ran a build *newer* than the catalogue and was silently never told about updates again; and 67 releases predating the v4.20.34 trial anchor were still downloadable, any of which could reset the 14-day trial indefinitely. Those old assets were deleted on 2026-08-02.
- **The MCP assets MUST stay attached.** The plugin's *Settings → AI Settings → Connect AI assistant…* dialog points users at `/releases/latest` for the `.mcpb`, so a release without it leaves that button pointing at nothing. `github_release.py` hard-fails rather than release without it (`--no-mcpb` overrides).
- **Pre-approval hand-offs go direct, not public.** For a user who needs a fix before it clears App Store review, share the built zip privately (e.g. an expiring Drive link) and keep that folder empty between hand-offs – a standing public link recreates exactly the problem above.
- **The zips are still built** (`dist/`), just not attached: `build.sh` relies on it and they are what gets hand-shared above.
- **Why zips, not bare `.sdlplugin`, when sharing:** any transport that rewrites spaces in the filename – GitHub Releases turns `Supervertaler for Trados.sdlplugin` into `Supervertaler.for.Trados.sdlplugin` – breaks Trados, which extracts to `Unpacked/<sdlplugin-filename-without-extension>/` and matches the manifest `<PlugInName>`; a dotted name reintroduces the duplicate-package crash. The hyphenated zip preserves the exact inner filename. The `.sdlplugin` names are **load-bearing – never rename them.**
- **The App Store renames the file too, and differently.** You upload `Supervertaler for Trados (Studio 2026).sdlplugin`; RWS serves it as `Supervertaler for Trados.sdlplugin`, stripping the suffix to match the listing name. Since Studio extracts to `Unpacked/<filename-without-extension>/`, the SAME build unpacks under two different names depending on where it came from – `Supervertaler for Trados` from the App Store, `Supervertaler for Trados (Studio 2026)` from `build.sh`. Confirmed by installing from the store and watching the folders (2026-08-30).
  - This is why `build.sh` sweeps **every** `Supervertaler*` package and unpacked folder rather than only the name it installs under. Before that, running it on a machine with an App Store install left both packages side by side, each declaring `PlugInName` "Supervertaler for Trados" – two copies of one plugin loaded at once, which is the startup crash recorded in the 20.34 changelog. This machine was one `build.sh` run away from it.
  - So: never assume the installed filename. Anything that cleans up, detects or compares installs has to match on the prefix, not on a literal name.

Tooling:
- `build.sh` calls `python tools/github_release.py --zip-only` after building, producing `Supervertaler-for-Trados-Studio-2024.zip` and `Supervertaler-for-Trados-Studio-2026.zip` in `dist/`. **It does NOT copy anything into `RWS AppStore/`** – `appstore_release.py` stages the two `.sdlplugin` files itself, from the same `dist/` copies it takes the notes' checksums from, in the same run. That is deliberate and load-bearing: build.sh used to mirror on every build, so ordinary development after a staging silently replaced the binaries while the notes went on describing the old ones. It drifted twice in one working day before the staging moved. Never restore the mirror to build.sh; if you need the staged files refreshed, re-run `appstore_release.py`, which rewrites the notes to match. **The App Store Manager form takes the `.sdlplugin`, not the zip** — so the mirror is deliberately the bare plugin, and the zips stay in `dist/`. The zip rule in the next section is about *transports that rewrite filenames*; a web upload form is not one, so do not "fix" this by mirroring zips across.
- `python tools/github_release.py --create` auto-detects the last GitHub tag, extracts the `CHANGELOG.md` delta since it, writes `release-body-v<ver>.md` (App Store install preamble + MCP asset table + links), builds the `.mcpb`, and runs `gh release create v<ver>` with the two MCP assets attached. Run without `--create` for a dry run; `--since <ver>` overrides the baseline.
- **`git push` the release commit BEFORE `--create`.** `gh release create` tags whatever the *remote's* default branch points at, not your local HEAD — so an unpushed commit gets a release whose notes describe changes the tagged tree does not contain, silently and with no warning from either tool. Happened on v18.20.173. Recovering means pushing and then moving the ref (`gh api --method PATCH repos/.../git/refs/tags/<tag> -f sha=<commit> -F force=true`) — move the tag, never delete the release.
- It titles the release with the bare tag; recent convention is a descriptive title (`v18.20.159 / 19.20.159 – <headline changes>`), set afterwards with `gh release edit <tag> --title "…"`.
- **Mark a flagship feature with a leading `★` on its `CHANGELOG.md` bullet.** `appstore_release.py` lifts the bold headline of every starred bullet into a **Highlights** lead above the full changelog, and strips the star from the bullet itself. An App Store reader coming from a version two dozen releases back faces well over a hundred bullets, and the tool flattens the `### Added (…)` parentheticals that carry the headline in `CHANGELOG.md`, so without this the flagship is just one bullet among many. Use it sparingly — marking everything highlights nothing. Only the App Store notes read the marker; GitHub release bodies ignore it.
- `python tools/appstore_release.py <last_published_version>` generates the App Store notes from its own (published-version) baseline, into `RWS AppStore/release_notes_v<ver>.md`, including both builds' version numbers, min/max Studio versions and SHA-256 checksums. It buckets bullets by their `### Added/Changed/Fixed` heading in `CHANGELOG.md`, so a miscategorised bullet lands in the wrong section.

---

## Confidentiality: never use real client names in examples

**Use `Acme` for a client and `PROJ-001` for a case or job reference.** Real customer
names and case numbers must not appear in anything that leaves this machine – docs,
`CHANGELOG.md`, App Store release notes, GitHub issues and comments, commit messages,
design notes under `docs/design/`, or source comments.

The work here is patent and legal translation under confidentiality, and the examples
read exactly as well with a placeholder. This is not hypothetical: on 2026-08-25 a
client name was found on the public docs site, and the sweep that followed found it in
four docs pages, the changelog, three App Store release-note files, four source files,
several design notes, a published GitHub release body and two issue comments. It had
been public since roughly v18.20.153.

**The trap is real project output.** With a project open, its name appears in
`bridge.log`, in the handshake files under `trados/runtime/`, and in any MCP tool
result. Pasting a real session's output into an issue, a changelog entry or a doc is
how it happened – substitute before writing, not after. Treat anything matching
`XXXX-000-XX-XX` as identifying.

## Naming conventions

- **Plugin name**: "Supervertaler for Trados" (visible in Trados docking header and plugin manager)
- **Terminology panel name**: "TermLens" (tab label inside the ViewPart – kept as the feature name)
- **Action IDs**: Prefixed with `TermLens_` for terminology-related actions (e.g. `TermLens_AddTerm`, `TermLens_TermPicker`); do NOT rename these – users may have custom shortcut overrides
- **Class names**: TermLens-prefixed classes (`TermLensEditorViewPart`, `TermLensControl`, etc.) are the terminology feature; future AI classes will use different naming
- **Settings auto-migrate** from old `%LocalAppData%\TermLens\` to `%LocalAppData%\Supervertaler.Trados\` on first run

---

## SQLite / WAL notes

- `supervertaler.db` uses WAL mode (Write-Ahead Log). Leftover `.db-wal` / `.db-shm` files after non-clean Supervertaler shutdown are harmless – SQLite replays the WAL on next open.
- Connection string uses `SqliteConnectionStringBuilder` with `Mode = SqliteOpenMode.ReadOnly` – safe for concurrent access while Supervertaler has the DB open.

---

## Term add/edit/delete: incremental index updates

The quick-add actions (Alt+Down, Alt+Up) and right-click edit/delete use **incremental in-memory index updates** instead of reloading the entire database:

- **`TermMatcher.AddEntry(TermEntry)`** – inserts one entry into `_termIndex` under both the lowercase key and stripped-punctuation variant. O(1).
- **`TermMatcher.RemoveEntry(long termId)`** – removes entries by ID from all keys.
- **`TermbaseReader.InsertTermBatch()`** – inserts into multiple write termbases in a single SQLite connection + transaction, instead of one connection per termbase.
- **`NotifyTermInserted(List<TermEntry>)`** – adds entries to the index and refreshes the UI. No settings reload, no DB reload.
- **`NotifyTermDeleted(long termId)`** – removes from index and refreshes.
- **`NotifyTermAdded()`** – the old full-reload path. Still used by the settings dialog when the user toggles termbases.

The edit handler (right-click → Edit) does a remove + add of the updated entry.

On app startup or settings change, `LoadTermbase(forceReload: true)` still does a full DB load to ensure consistency.

---

## Non-translatable terms

Terms can be marked as **non-translatable** (brand names, product codes, abbreviations that stay the same across languages). These are stored with `is_nontranslatable = 1` in the `termbase_terms` table and `TargetTerm = SourceTerm`.

- **Visual**: Non-translatable chips render with a **light yellow background** (`#FFF3D0`). Color precedence: non-translatable (yellow) > project (pink) > regular (blue).
- **Keyboard shortcut**: `Ctrl+Alt+N` – quick-adds the selected source text as non-translatable to all Write termbases (target is set to source automatically). Only requires source text selected.
- **Right-click menu**: "Mark as Non-Translatable" / "Mark as Translatable" toggle on any term chip. Uses `TermbaseReader.SetNonTranslatable()` for a lightweight DB update.
- **Add Term dialog**: "Non-translatable" checkbox auto-fills target = source and makes target read-only when checked. Pre-populates from `TermEntry.IsNonTranslatable` in edit mode.
- **Alt+digit insertion**: Works unchanged – inserts `TargetTerm` which equals `SourceTerm` for non-translatables.
- **TermLens popup** (Ctrl tap) and **TermPicker** (Alt+P): Show yellow background for non-translatable matches. (Both keys had moved: Ctrl+Alt+G became the AutoTagger, and TermPicker left Ctrl+Shift+P in 20.135.)
- **Termbase Editor**: "NT" checkbox column for toggling per-term.
- **DB migration**: `MigrateSchema()` uses `PRAGMA table_info` to detect the column and `ALTER TABLE ADD COLUMN` if missing. Called from `Open()` (via `HasColumn`) and all static write methods. Idempotent and backward-compatible with older Supervertaler databases.
- **Action ID**: `TermLens_QuickAddNonTranslatable` – do NOT rename (users may have custom shortcut overrides).

---

## License

Source-available license (not MIT). Source code viewable/forkable for personal use, but binary redistribution (.sdlplugin) restricted to copyright holder. Pre-built binaries available at supervertaler.com.

---

## Monetization

- Source code is open on GitHub (source-available license)
- 14-day free trial (no credit card required), full feature access
- Single paid tier: **Supervertaler for Trados** – €20/month or €200/year (all features)
- Payment platform: Lemon Squeezy (handles EU VAT)
- License key validation: key entered in plugin settings, validated against Lemon Squeezy API
- Annual plans include 2 months free (equivalent discount)
- Legacy: old TermLens (€10) and Assistant (€15) variant names from Lemon Squeezy are still accepted and grant full access

---

## Planned features

### AI Chat Assistant (implemented)

The AI Assistant is a separate dockable ViewPart (`AiAssistantViewPart` + `AiAssistantControl`) registered in `plugin.xml`. Key implementation details:

- **Separate dockable ViewPart** – fully native Trados dockable panel. Users can dock it right, bottom, floating, or on a second monitor. Position/size persists across sessions automatically.
- **The "AI Assistant" tab is a launcher** – shows an "Open AI Assistant" button that activates the dockable panel.
- **Project-aware context** – the assistant has access to: current segment (source + target), termbase terms (filterable per-termbase via AI Context settings), TM fuzzy matches (toggleable), and the active memory bank (see below).
- **Multimodal image support** – users can paste (Ctrl+V), drag-drop, or browse images. Each provider uses its native vision API format (OpenAI content arrays, Claude image blocks, Gemini inline_data, Ollama images array).
- **Markdown rendering** – `MarkdownToRtf` converts LLM markdown output to RTF for display in `ChatBubble` RichTextBox controls.
- **Apply to target** – right-click assistant responses to insert text into the active Trados segment.
- **AI Context control** – `AiSettings.DisabledAiTermbaseIds` filters which termbases contribute terms to prompts; `AiSettings.IncludeTmMatches` toggles TM match injection into the system prompt.

### SuperMemory / multi-bank support (implemented)

**SuperMemory** is the user-facing brand name for the self-organising translation knowledge base system (chat banners, Reports tab labels, help menu, marketing copy all use this). **Memory banks** are the individual containers within SuperMemory – self-contained folders, one per client/domain/project, that the user can switch between. The two-level terminology matches how Gmail uses "Gmail" for the product and "inbox" for the container, or Obsidian uses "Obsidian" for the product and "vault" for the container.

Memory banks are stored as interlinked Markdown files under `<Root>/memory-banks/<bank-name>/`. They share the exact on-disk layout (including the seven-folder skeleton: `00_INBOX`, `01_CLIENTS`, `02_TERMINOLOGY`, `03_DOMAINS`, `04_STYLE`, `05_INDICES`, `06_TEMPLATES`) with the Python Supervertaler Assistant, so banks created in either product are immediately visible to the other.

- **Multiple banks** – users can keep several banks side by side (one per client, per domain, or per language pair). The active bank is persisted in `AiSettings.ActiveMemoryBankName` and survives Trados restarts.
- **Toolbar dropdown** – the `SuperMemoryToolbar` Memory Bank combo lists every bank under `<Root>/memory-banks/` (via `UserDataPath.ListMemoryBanks()`) with the active one selected. Switching is immediate: `AiAssistantViewPart.OnMemoryBankChanged` persists the new bank, invalidates the cached `MemoryBankReader`, restarts the inbox watcher, and drops a confirmation banner into the chat.
- **Create from the dropdown** – the last entry in the combo is a `"+ New memory bank…"` sentinel. Selecting it reverts the combo to the previously active bank (via the `_lastRealSelection` tracker) and fires a `NewMemoryBankRequested` event. `AiAssistantViewPart.OnNewMemoryBankRequested` shows a small modal dialog with a live sanitisation preview, calls `UserDataPath.TryCreateMemoryBank`, and reuses `OnMemoryBankChanged` to switch to the new bank.
- **Sanitisation rules** – `UserDataPath.SanitizeBankName` mirrors the Python assistant's `sanitise_bank_name` exactly: lowercase, whitespace → hyphen, strip anything outside `[a-z0-9-_]`, trim leading/trailing separators. Names are filesystem identifiers, not display labels.
- **Legacy migration** – `UserDataPath.TryMigrateLegacySingleBank` moves pre-multi-bank `<Root>/memory-bank/` or `<Root>/supermemory/` folders into the new layout on first run, surfaced via a first-run dialog.
- **The Library tab** (Settings → Library, formerly “Prompts”) shows every bank and its files under a `SuperMemory` node, below the prompt folders – foundation, then tasks, then knowledge. Files render as Markdown, read-only; **Edit** opens `BankFileEditorDialog`. Right-click a bank for **Set as active / Rename / Delete / Open bank folder**. See `docs/design/library-tab.md`.
- **Renaming or deleting a bank must release our own handles first.** `StartInboxWatcher` puts a `FileSystemWatcher` on the ACTIVE bank's `reference/` folder, and Windows will not rename a folder above an open handle – so renaming the active bank failed with “Access to the path is denied” and blamed Obsidian. Bracket the move with `AiAssistantViewPart.ReleaseMemoryBankHandles()` / `ReacquireMemoryBankHandles()`, in a `finally`.
- **Renaming the active bank must carry `AiSettings.ActiveMemoryBankName` across.** It stores a NAME, and the reader treats a missing bank as an empty one – so without this SuperMemory contributes nothing to every prompt, silently. Deleting the active bank is refused outright rather than switching away from it.
- **Deleted banks are moved to `memory-banks/.trash/<name>-<timestamp>`**, not deleted. `ListMemoryBanks` skips dot-prefixed folders, so a bank vanishes from every list while staying restorable by renaming the folder back. `_shared` can be neither renamed nor deleted: the reader loads it by that exact name.
- **Writing a bank file from code must preserve its line endings.** The banks disagree – most files are CRLF, `_shared/brief.md` is LF-only – so writing back whatever WinForms produced rewrites every line and turns a one-word edit into a whole-file diff in Obsidian and git. `BankFileEditorDialog` detects each file's convention and restores it; anything else that writes these files (including the MCP write tools in issue #68) has to do the same.

### Context composition (open design question)

Memory banks are **one of several context sources** the assistant consults, alongside termbases, TM matches, document content, and segment metadata. The docs framing ("replaces traditional TM/TB") was a marketing simplification that got rewritten in Step 6 – memory banks complement the other sources, they do not replace them. Whether stacking all sources at once is additive or noisy is an open question that needs empirical testing with real projects. See `notes/multi-bank-context-composition.md` for the memo.
