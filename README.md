# TermLens

**Instant terminology insight for every segment**

A Trados Studio plugin that displays terminology matches in a dedicated panel next to the editor — using the same approach as [Supervertaler](https://supervertaler.com).

TermLens renders the full source segment word-by-word in its own panel, with glossary translations displayed directly underneath each matched term. Translators see every term match in context.

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
- **Read/Write/Project termbase selection** — choose which termbases to search (Read), which one receives new terms (Write), and which are project glossaries (Project)
- **Supervertaler-compatible** — reads and writes Supervertaler's SQLite termbase format directly, so you can share termbases between both tools
- **Auto-detect** — automatically finds your Supervertaler termbase if no file is configured

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
