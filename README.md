# TermLens

**Instant terminology insight for every segment**

A Trados Studio plugin that displays terminology matches in a dedicated panel next to the editor — using the same approach as [Supervertaler](https://supervertaler.com).

TermLens renders the full source segment word-by-word in its own panel, with glossary translations displayed directly underneath each matched term. Translators see every term match in context without having to switch to a separate termbase lookup window.

## How it works

As you navigate between segments in the Trados Studio editor, the TermLens panel updates automatically. It shows the source text word-by-word, scanning it against your loaded termbase. Each matched term appears as a coloured block with the target-language translation directly below it — so you can see all terminology at a glance.

## Features

- **Dedicated terminology panel** — source words flow left to right with translations directly underneath matched terms
- **Color-coded by termbase** — project termbases (pink) vs. regular termbases (blue) at a glance
- **Multi-word term support** — correctly matches phrases like "prior art" or "machine translation" as single units
- **Click to insert** — click any translation to insert it at the cursor position in the target segment
- **Supervertaler-compatible** — reads Supervertaler's SQLite termbase format directly, so you can share termbases between both tools
- **Auto-detect** — automatically finds your Supervertaler termbase if no file is configured

## Requirements

- Trados Studio 2024 or later
- .NET Framework 4.8

## Installation

Download the `.sdlplugin` file and copy it to:
```
%LocalAppData%\Trados\Trados Studio\18\Plugins\Packages\
```

Then restart Trados Studio. TermLens will appear as a panel below the editor when you open a document.

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
