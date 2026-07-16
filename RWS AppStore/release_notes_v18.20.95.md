# RWS App Store Manager - v18.20.95

Two builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2024 | `18.20.95.0` | `18.0` | `18.9` | `f7f3be2f050cb0115568bbfe5d48511212a515487f88d80e16a4913ac529a5e2` |
| Studio 2026 | `19.20.95.0` | `19.0` | `19.0.9` | `69dc9f9add1a9f5607dddc5019ac59c5f805710ac839cce7cbb2cc39c969a321` |

---

## Changelog

### Added
This release introduces the **Supervertaler MCP Server** – a brand-new way to work: connect an AI assistant directly to your live Trados Studio session.

- **Your AI assistant can now talk directly to your open Trados Studio project.** The new Supervertaler MCP Server connects AI apps that run local MCP servers – Claude Desktop (recommended), Claude Code and others – to your live Trados session. Ask "What's the status of my project?", "How did I translate this term elsewhere?", or "Find all segments containing X" in the AI app's own chat window, and it answers from your real project data: project statistics, segments (with filters and paging), your Supervertaler translation memories, and your termbases. It can also insert a translation into the active segment, exactly like the Assistant's Apply-to-target button. Everything stays on your machine – the connection is local-only and protected by a per-session token, and nothing is exposed to the network. Setup: **Settings → AI Assistant → Connect AI assistant…** – Claude Desktop users install a `.mcpb` extension (Settings → Extensions → Advanced settings → Install extension…); other apps get a copy-paste config snippet. This is the first MCP server that talks to a live Trados Studio editor session. Follow development in [issue #44](https://github.com/Supervertaler/Supervertaler-for-Trados/issues/44).
- **The AI can also make changes – always under your supervision.** `update_segments` writes translations and/or confirmation statuses into the open document ("draft translations for all untranslated segments so I can review them"), and `add_term` adds entries to your Write termbases ("we agreed 'draagarm' = 'support arm' – add it"). Safety rails are built in: AI-written translations get **Draft** status unless another status is explicitly requested, **locked segments are never touched**, updates are capped at 200 segments per call, every change is reported per segment, and nothing is saved to disk until you save in Studio.
- **Your AI assistant can now answer richer questions about your project.** Four new abilities for the [Supervertaler MCP Server](https://docs.supervertaler.com/trados/mcp-server/): it can **search your project's own Trados TMs** – the .sdltm files and GroupShare server TMs attached to the project, the same ones SuperSearch queries – so "how did I translate this before?" finally reaches the memories you actually translate against; it can list the **files** in a merged multi-file document (and you can ask it to work on just one of them – "only look at the contract file"); it can report **project statistics** (analysis bands and per-file confirmation counts – "how many words are left?", "how far along is each file?"); and it can **find inconsistencies** – repeated source sentences you translated differently – which pairs naturally with its ability to then align them ("find all repeated sentences I translated differently, and fix them to match"). See the [prompt cookbook](https://docs.supervertaler.com/trados/mcp-server/) for the full range of what you can ask.
- **Your AI assistant is now a QA partner.** Three quality checks that work on the whole open document: **number checking** ("find segments where the numbers don't match" – decimal/thousand separator differences are handled), **tag checking** (missing or extra inline tags between source and target), and **terminology checking** (source contains a termbase term but the target doesn't use its expected translation or any of its synonyms). Each finding comes back with the segment, the reason, and enough context for the AI to explain it – and, after your approval, fix it. A new **resource listing** tool also lets the AI see which TMs (Trados project TMs, GroupShare, and Supervertaler TMs) and termbases are attached, including read/write flags.

### Changed
- **Documentation now clearly states which AI apps work.** Claude Desktop is fully supported (and recommended), and other clients that run local MCP servers on your own machine (such as Claude Code) also work. ChatGPT's desktop app is **not** supported, because it runs MCP servers in a cloud environment that can't reach the Supervertaler bridge – which stays on your computer by design, so your project never leaves your machine.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases