# Supervertaler for Trados

**Terminology insight and AI translation for Trados Studio**

Supervertaler for Trados is a Trados Studio plugin (.sdlplugin) that brings key features from [Supervertaler Workbench](https://supervertaler.com/workbench/) into the Trados ecosystem. It includes the **TermLens** glossary panel and **AI-powered batch and single-segment translation** using OpenAI, Anthropic, and Google LLMs. It relates to Supervertaler Workbench as follows:

- Supervertaler Workbench – free, open-source, standalone tool (Windows/Mac/Linux)
- Supervertaler for Trados – paid plugin (Windows-based, but can run on Mac/Linux via virtualisation, e.g., using Parallels Desktop)

## Pricing

| Plan | Price | Features |
|------|-------|----------|
| **Free trial** | 14 days | Full access to all features |
| **TermLens** | €10/month | Terminology management, termbases, MultiTerm support, Term Picker, quick-add shortcuts |
| **TermLens + Supervertaler Assistant** | €15/month | All TermLens features plus AI Assistant, Batch Translate, Prompt Library, multimodal support |

Purchase a license at [supervertaler.com/trados](https://supervertaler.com/trados/).

## Privacy & Security

This plugin makes **no network calls** except to:
1. **Your chosen AI provider** (OpenAI, Anthropic, Google Gemini, or local Ollama) — only when you use AI features
2. **Lemon Squeezy license API** — for license activation and periodic validation (sends only your license key and a hashed machine fingerprint)

No telemetry, no tracking, no analytics, no data collection. Your API keys are stored locally in `%LocalAppData%\Supervertaler.Trados\settings.json` and are never transmitted anywhere except to your chosen AI provider. The full source code is available here for security audit.

## TermLens — inline terminology display

TermLens renders the full source segment word-by-word in a dedicated panel, with glossary translations displayed directly underneath each matched term. Translators see every term match in context — from both Supervertaler termbases and MultiTerm .sdltb termbases attached to the active Trados project.

<img width="2560" height="1439" alt="image" src="https://github.com/user-attachments/assets/417e4240-c294-4ee8-826c-ac5f05c607c0" />

### How it works

As you navigate between segments in the Trados Studio editor, the TermLens panel updates automatically. It shows the source text word-by-word, scanning it against your loaded termbase. Each matched term appears as a coloured block with the target-language translation directly below it — so you can see all terminology at a glance.

### Features

- **Dedicated terminology panel** — source words flow left to right with translations directly underneath matched terms
- **MultiTerm termbase support** — automatically detects .sdltb termbases attached to the active Trados project and displays their terms as green chips alongside Supervertaler terms; read-only, auto-refreshes when terms are added via Trados's native interface
- **Color-coded by glossary type** — mark glossaries as "Project" in settings to show their terms in pink; all others appear in blue; non-translatable terms appear in yellow; MultiTerm terms appear in green
- **Non-translatable terms** — mark brand names, product codes, or abbreviations that should stay the same across languages; Ctrl+Alt+N to quick-add, or right-click any term to toggle; the source term is copied verbatim as the target
- **Multi-word term support** — correctly matches phrases like "prior art" or "machine translation" as single units
- **Click to insert** — click any translation to insert it at the cursor position in the target segment
- **Alt+digit shortcuts** — press Alt+1 through Alt+9 (or Alt+0 for term 10) to instantly insert a matched term; two-digit chords supported for 10+ matches
- **Term Picker dialog** — press Ctrl+Alt+G to browse all matched terms and their synonyms in a list, with expandable synonym rows
- **F2 expand selection** — make a rough partial selection across word boundaries, press F2, and the selection snaps to complete words; great for verifying source-target alignment in long segments
- **Add terms from the editor** — right-click to add a new term from the active segment's source/target text, with or without a confirmation dialog; partial selections are auto-expanded to full words
- **Adjustable font size** — A+/A- buttons in the panel header for quick on-the-fly size changes, or set the exact size in Settings; persists across restarts
- **Read/Write/Project termbase selection** — choose which termbases to search (Read), which receive new terms (Write — multiple allowed), and which is the project glossary (Project)
- **Standalone database creation** — create a fresh Supervertaler-compatible termbase database from the Settings dialog, no external tools required
- **Glossary management** — add and remove individual glossaries inside a database directly from Settings
- **Bulk Add NT** — paste multiple non-translatable terms at once (one per line) from the Termbase Editor; duplicates are automatically skipped
- **Duplicate prevention** — all insert and update paths check for existing entries with the same source+target in the same termbase, preventing accidental duplicates
- **Merge prompt for similar terms** — when adding a term whose source or target already exists with a different translation, a dialog offers to add the new text as a synonym of the existing entry instead of creating a near-duplicate; includes an "Add & Edit…" option to review metadata before saving
- **TSV import/export** — bulk import and export terms in Supervertaler's TSV format (tab-separated, pipe-delimited synonyms, `[!forbidden]` markers, UUID tracking)
- **Help / About** — "?" button in the panel header shows version, keyboard shortcuts, and links to documentation and support
- **Supervertaler-compatible** — reads and writes Supervertaler's SQLite termbase format directly, so you can share termbases between both tools
- **Auto-detect** — automatically finds your Supervertaler termbase if no file is configured
- **Remembers layout** — dialog sizes and column widths are saved and restored between sessions

### Screenshots
<img width="2560" height="1440" alt="2_TermLens-in-Trados-Studio-2024-popup" src="https://github.com/user-attachments/assets/001f91d9-7c18-4aef-886b-49ed6e6c6d8c" />

---

<img width="2560" height="1440" alt="3_TermLens-in-Trados-Studio-2024-Settings-dialogue" src="https://github.com/user-attachments/assets/2810fda4-f06d-4df5-b97c-03e75c8dec55" />

---

<img width="1411" height="1015" alt="4_TermLens-in-Trados-Studio-2024-Settings-KBS" src="https://github.com/user-attachments/assets/ee14c3ae-43f3-4f76-8ec2-871a9c18e10b" />

## AI Assistant — project-aware chat

The AI Assistant is a separate dockable panel in Trados Studio that provides a multi-turn chat interface with full project context. Ask questions about the current segment, request alternative translations, or get explanations — all with your terminology and TM matches built into the conversation.

### Features

- **Dockable chat panel** — dock it right, bottom, floating, or on a second monitor; position and size persist across sessions
- **Project-aware context** — the assistant automatically sees the current segment (source + target), matched termbase terms, and optionally TM fuzzy matches
- **Image attachments** — paste images from clipboard (Ctrl+V), drag and drop, or browse with the attach button; images are sent to the AI using each provider's vision API
- **Apply suggestions** — right-click any assistant response and choose "Apply to target" to insert the suggestion directly into the active segment
- **Markdown rendering** — responses render with full formatting: headings, bold, italic, inline code, code blocks, tables, and lists
- **AI context control** — choose which termbases contribute to AI prompts and toggle TM match inclusion from the AI Settings panel
- **All providers supported** — OpenAI, Anthropic (Claude), Google (Gemini), Ollama, and custom OpenAI-compatible endpoints

## AI Batch Translation

Supervertaler for Trados includes built-in AI translation powered by OpenAI, Anthropic, and Google LLMs. Translations are glossary-aware — matched terms from your TermLens glossaries are injected into the AI prompt so the model respects your approved terminology.

### Features

- **Batch translate** — translate multiple segments at once from the Batch Translate tab; choose from four scopes: empty segments only, all segments, filtered segments, or filtered empty only
- **Single-segment translate** — press **Ctrl+Alt+A** or right-click → "AI Translate Current Segment" to translate just the active segment
- **Filtered segment support** — use Trados's advanced display filter to narrow down which segments to translate, then batch-translate only those
- **Multiple AI providers** — OpenAI (GPT-4o, GPT-4o mini, o1, o3-mini), Anthropic (Claude Sonnet, Haiku, Opus), and Google (Gemini 2.0 Flash, Gemini 1.5 Pro)
- **Glossary-aware prompts** — AI translations automatically include your approved terms (including non-translatable terms) in the prompt
- **Prompt library** — 14 built-in prompts for domain expertise (Medical, Legal, Patent, Financial, Technical, Marketing, IT), style guides (Dutch, English, French, German, Spanish), and project prompts; create custom prompts with Markdown + YAML frontmatter; compatible with Supervertaler desktop prompt format
- **Composable prompt assembly** — base system prompt + custom domain/style instructions + glossary injection; advanced users can override the base system prompt entirely
- **Configurable settings** — provider, model, API key, and temperature are set in the AI Settings panel and persist across sessions
- **Real-time progress** — batch translations show segment-by-segment progress in a scrollable log panel, with cancel support

## Requirements

- Trados Studio 2024 or later
- .NET Framework 4.8
- Runs on Windows x64, x86, and Windows on ARM (Parallels on Apple Silicon Macs, Surface Pro X, etc.)

## Installation

Supervertaler for Trados ships as a standard `.sdlplugin` file — just double-click it and Trados Studio installs it automatically. No manual file copying required.

Alternatively, you can copy the `.sdlplugin` file manually to:
```
%LocalAppData%\Trados\Trados Studio\18\Plugins\Packages\
```

Restart Trados Studio and the Supervertaler for Trados panel will appear above the editor when you open a document, with tabs for TermLens and upcoming AI features.

> **Upgrading from TermLens?** The old TermLens plugin should be uninstalled first. Your settings will be migrated automatically.

## Building from source

```bash
bash build.sh
```

This runs `dotnet build`, packages the output into an OPC-format `.sdlplugin`, and deploys it to your local Trados Studio installation. Trados Studio must be closed before running the script.

## License

Source available — see [LICENSE](LICENSE) for details. Pre-built binaries are available at [supervertaler.com](https://supervertaler.com).

## Author

Michael Beijer — [supervertaler.com](https://supervertaler.com/trados/)
