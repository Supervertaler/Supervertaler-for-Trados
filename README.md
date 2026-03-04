# TermLens

**Instant terminology insight for every segment**

A Trados Studio plugin that displays terminology matches in a dedicated panel next to the editor — using the same approach as [Supervertaler](https://supervertaler.com).

TermLens renders the full source segment word-by-word in its own panel, with glossary translations displayed directly underneath each matched term. Translators see every term match in context.

<img width="2560" height="1440" alt="1_TermLens-in-Trados-Studio-2024" src="https://github.com/user-attachments/assets/0f8af9a5-587b-43a8-b1e1-6a9d4274032f" />

## How it works

As you navigate between segments in the Trados Studio editor, the TermLens panel updates automatically. It shows the source text word-by-word, scanning it against your loaded termbase. Each matched term appears as a coloured block with the target-language translation directly below it — so you can see all terminology at a glance.

## Features

- **Dedicated terminology panel** — source words flow left to right with translations directly underneath matched terms
- **Color-coded by glossary type** — mark glossaries as "Project" in settings to show their terms in pink; all others appear in blue
- **Multi-word term support** — correctly matches phrases like "prior art" or "machine translation" as single units
- **Click to insert** — click any translation to insert it at the cursor position in the target segment
- **Alt+digit shortcuts** — press Alt+1 through Alt+9 (or Alt+0 for term 10) to instantly insert a matched term; two-digit chords supported for 10+ matches
- **Term Picker dialog** — press Ctrl+Shift+G to browse all matched terms and their synonyms in a list, with expandable synonym rows
- **Add terms from the editor** — right-click to add a new term from the active segment's source/target text, with or without a confirmation dialog
- **Adjustable font size** — A+/A− buttons in the panel header for quick on-the-fly size changes, or set the exact size in Settings; persists across restarts
- **Read/Write/Project termbase selection** — choose which termbases to search (Read), which one receives new terms (Write), and which is the project glossary (Project)
- **Standalone database creation** — create a fresh Supervertaler-compatible termbase database from the Settings dialog, no external tools required
- **Glossary management** — add and remove individual glossaries inside a database directly from Settings
- **TSV import/export** — bulk import and export terms in Supervertaler's TSV format (tab-separated, pipe-delimited synonyms, `[!forbidden]` markers, UUID tracking)
- **Supervertaler-compatible** — reads and writes Supervertaler's SQLite termbase format directly, so you can share termbases between both tools
- **Auto-detect** — automatically finds your Supervertaler termbase if no file is configured
- **Remembers layout** — dialog sizes and column widths are saved and restored between sessions

## Screenshots
<img width="2560" height="1440" alt="2_TermLens-in-Trados-Studio-2024-popup" src="https://github.com/user-attachments/assets/001f91d9-7c18-4aef-886b-49ed6e6c6d8c" />


<img width="2560" height="1440" alt="3_TermLens-in-Trados-Studio-2024-Settings-dialogue" src="https://github.com/user-attachments/assets/2810fda4-f06d-4df5-b97c-03e75c8dec55" />


<img width="1411" height="1015" alt="4_TermLens-in-Trados-Studio-2024-Settings-KBS" src="https://github.com/user-attachments/assets/ee14c3ae-43f3-4f76-8ec2-871a9c18e10b" />

## Requirements

- Trados Studio 2024 or later
- .NET Framework 4.8

## Installation

Download the `.sdlplugin` file and copy it to:
```
%LocalAppData%\Trados\Trados Studio\18\Plugins\Packages\
```

Then restart Trados Studio. TermLens will appear as a panel above the editor when you open a document.

## Building from source

```bash
bash build.sh
```

This runs `dotnet build`, packages the output into an OPC-format `.sdlplugin`, and deploys it to your local Trados Studio installation. Trados Studio must be closed before running the script.

Alternatively, open `TermLens.sln` in Visual Studio 2022, restore NuGet packages, and build the solution.

## License

MIT License — see [LICENSE](LICENSE) for details.

## Author

Michael Beijer — [supervertaler.com](https://supervertaler.com)
