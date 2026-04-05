# RWS App Store Manager — v4.18.48.0

**Version number:** `4.18.48.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `5da23ff47f6f45692ae26c8d2870afe60b6b4b546b02a243190c64c8d86a131f`

---

## Changelog

### Added
- **SuperSearch** — new dockable ViewPart (View > Supervertaler SuperSearch) for cross-file search, find & replace, and segment navigation across all SDLXLIFF files in a project. Search source, target, or both with case-sensitive and regex options. Results grid with file name, segment number, source, target, and confirmation status columns. Matching text highlighted in yellow. Preview pane shows full segment text on single-click. Double-click to navigate to the segment in the editor. Find & Replace in target text with single replace (undoable via Trados API) and Replace All with two-step safety confirmation for irreversible disk modifications. File selection dialog to include/exclude specific project files. Alt+S keyboard shortcut (also works from right-click context menu) with auto-fill from editor selection. Regex replace supports capture groups
- **Incognito Mode** — new toggle in AI Settings that tells the AI to anonymise all personal and project data in its responses. Project names, file paths, TM names, and other identifying information are replaced with plausible placeholders. Useful for screen sharing, recording demos, or posting screenshots in forums
- **Multi-provider Studio Tools** — Studio Tools now works with all major AI providers (OpenAI, Gemini, Grok, Mistral) in addition to Claude, each using its native function calling API
- **5 new Studio Tools** — Project Statistics, File Status, Project Termbases, TM Info, and TM Search tools. Ask natural-language questions about your Trados projects, translation memories, and termbases
- **Studio Tools** — the Supervertaler Assistant can now query your Trados Studio installation using natural language. Ask about projects, translation memories, or project templates and the AI will look up the answer directly from your local Studio data
- **SuperMemory knowledge base integration** — SuperMemory articles (client profiles, domain knowledge, style guides, terminology decisions) are now automatically loaded into AI chat and batch translation prompts, with client and domain auto-detection and a 4,000-token budget
- **SuperMemory** — a self-organising, AI-maintained translation knowledge base that stores client profiles, terminology decisions, domain conventions, and style preferences in a human-readable vault of interlinked Markdown files
- **SuperMemory Quick Add (Ctrl+Alt+M)** — capture terms and corrections from the Trados editor into your SuperMemory vault. Also available via right-click in the editor grid
- **Per-project active prompt** — right-click any translation prompt to set it as the active prompt for the current project, automatically selected in Batch Translate

### Improved
- **AI Assistant help** — restructured into sub-pages: Context Awareness, File Attachments, Studio Tools, and Providers and Models
- **Studio Tools help page** — expanded with all 9 tools and 30+ example questions organised by category
- **SuperMemory documentation** — practical safety/backup advice, Obsidian Web Clipper setup instructions, AI context integration docs
- **Selectable proofreading report text** — issue descriptions and suggestions in the Reports tab are now selectable with right-click copy support

### Fixed
- **TM discovery** — the "List TMs" and "Find TM" tools now scan all `.sdlproj` files for TM references, correctly resolving `sdltm.file:///` URIs and relative paths
- **Term direction inversion** — fixed incorrect term matching direction for same-language locale pairs (e.g. en-US to en-GB)

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
