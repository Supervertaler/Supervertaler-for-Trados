> [!WARNING]
> **Did the update prompt inside Trados send you here to download?**
> Then your plugin predates **v4.19.24** and is still checking GitHub for updates.
> There is no plugin to download on this page - install once from the
> **[RWS App Store](https://appstore.rws.com/plugin/432)** (or *Add-Ins -> RWS App Store*
> inside Studio) and it will check there from then on, and stop warning you about an
> unsigned plug-in at every start.

Supervertaler for Trados **v18.20.188** (Studio 2024) / **v19.20.188** (Studio 2026). Covers 18.20.188.

## 📦 How to install

**Supervertaler for Trados is published through the [RWS App Store](https://appstore.rws.com/plugin/432).** Install it from there, or from inside Studio via **Add-Ins → RWS App Store**. Those builds are signed by RWS, so Studio loads them without an "unsigned plug-in" prompt at every start, and the plugin's own update check keeps you current.

The plugin binary is **not attached to GitHub releases** – this page is the changelog and the record of what changed in each build. App Store updates go through RWS review, so a brand-new fix can take a day or two to appear; if you are waiting on something specific, email support@supervertaler.com and I will send you the build directly.

| Also attached | What it is |
|---|---|
| `Supervertaler-MCP-Server.mcpb` | AI assistant extension for Claude Desktop (optional, see below) |
| `Supervertaler-MCP-Server-exe.zip` | AI assistant server for other local MCP clients, e.g. Claude Code (optional, see below) |

## 🤖 Supervertaler MCP Server (optional)

`Supervertaler-MCP-Server.mcpb` connects **Claude Desktop directly to your live Trados Studio session** – ask about the open project, search your TMs and termbases, run QA checks, have translations drafted into the document, all from Claude's own chat window. To install: download the file, then in Claude Desktop open **Settings → Extensions → Advanced settings** and click **Install extension…** (double-click on the file also works if your system associates `.mcpb` with Claude; drag-and-drop does not). Requires Supervertaler for Trados (this plugin) and works entirely on your own machine. Other MCP clients that run local servers (e.g. **Claude Code**) can use `Supervertaler-MCP-Server-exe.zip` instead: unzip it somewhere permanent and point the client's MCP config at the exe – the plugin's **Settings → AI Settings → Connect AI assistant…** dialog copies a ready-made snippet. **ChatGPT desktop works too** – unzip the exe somewhere permanent and register it as a STDIO server in your Codex config; the [setup guide](https://docs.supervertaler.com/trados/mcp-server/#setting-it-up) gives the exact file and the block to paste. What cannot work is a client that runs the server in the cloud, such as the claude.ai or chatgpt.com websites – the bridge is local to your machine by design. [Documentation](https://docs.supervertaler.com/trados/mcp-server/).

## What's changed

## [18.20.188 / 19.20.188] – 2026-09-07

### Added
- ★ **The document’s list numbering can be sent to the AI as structure context (Word files, opt-in).** Word numbers claims, letters steps and bullets lists as paragraph properties, not text, so the segment grid never contains the `a)` or the `9.` – and neither did anything the AI received. On a real patent the model read six unlettered steps, translated “steps a. to f.” faithfully, and then flagged it as a possible source defect: a note that would have reached the client. With **Send list numbering to the AI as structure context** ticked in AI Settings, Batch Translate, Translate Segment and SuperBench prefix the first segment of every numbered paragraph with the marker Word renders, inside a sentinel – `[#e)]het fixeren…`, `[#9.]Werkwijze…`, `[#•]een behuizing…` – and a rule in the plugin’s own preamble tells the model it is structure to use for cross-references and parallelism, never to translate or reproduce. The rule ships with every request, including prompts you wrote yourself and AutoPrompt’s. The markers are read from the original Word file Studio keeps inside the sdlxliff and computed for the whole document at once, so a list that restarts at claim 11 reads `11.`, and lettered steps that continue from one claim into the next keep counting, exactly as Word shows them. Anything the model echoes back is removed before the target is written and logged in the batch log, and the TMX backup records the segment as Studio has it. Files that are not Word documents get a one-line fallback rule instead: the numbering exists, is not in the text, and is not a defect. Off by default for this version. Issue #109; the same design ships in Supervertaler for memoQ.

### Removed
- **Translate via Workbench (large files) is gone.** The button on the Batch Operations tab that closed the document, handed the sdlxliff to the 64-bit Supervertaler Workbench and swapped the result back in, together with its progress window, the Workbench (.exe) path box in AI Settings and the auto-detection behind it. Supervertaler Workbench is being disconnected from the plugin, and on a docked panel the button also sat on top of the Preview prompt and SuperBench links. Translate, Preview prompt, SuperBench and AutoPrompt now sit on the action row unobstructed.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
