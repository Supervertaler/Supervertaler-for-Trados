Supervertaler for Trados **v4.20.72** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.72.

## 📦 Installing from here (unsigned build – read first)

The plugins attached to this release are the **unsigned** builds. The version on the **RWS App Store is signed and notarised** – that's the recommended channel for most users. These downloads are for trying the latest fixes **before App Store approval** (which can take a few days, especially over a weekend).

**To install:**
1. Download the zip for your Trados version (table below).
2. **Extract it** – inside is a single `.sdlplugin` file.
3. Close Trados Studio, then double-click the `.sdlplugin` to run the Plugin Installer. **Do not rename the file** – Trados matches the filename against the plugin manifest.
4. Trados will warn that the plugin is **not signed**; that is expected for the direct build – click through to continue.

| Download | Trados version |
|---|---|
| `Supervertaler-for-Trados-Studio-2024.zip` | Trados Studio 2024 |
| `Supervertaler-for-Trados-Studio-2026-beta.zip` | Trados Studio 2026 (beta) |

## What's changed

## [4.20.72] – 2026-06-28

### Fixed (Translate active segment · works without the Assistant pane; clearer shortcut list)

- **Ctrl+T "Translate active segment" (and the right-click command) now works even if the Supervertaler Assistant pane was never opened this session.** Like AutoTagger, the handler relied on the lazy/unpinned pane being initialized, so after a Trados restart Ctrl+T could silently do nothing until you opened the pane. It now falls back to a pane-independent path (active document from the editor, settings from disk) that runs the same translation pipeline without opening the pane. The normal path is unchanged when the pane is open. (#41)
- **The duplicate "AI translate current segment" entry in Keyboard Shortcuts is now labelled "Translate active segment (deprecated – use Ctrl+T)".** That legacy action must stay registered (removing it crashes Studio on startup), so it can't be deleted from the shortcut list — but it's now clearly marked as the deprecated duplicate so it's obvious which command is the live one. The active command remains "Translate active segment" (Ctrl+T), the only one in the editor context menu.

### Fixed (Token Usage & Costs · records every AI call, even with the Assistant pane closed)

- **All AI usage is now logged to Token Usage & Costs regardless of whether the Supervertaler Assistant pane was opened.** Usage recording was wired up only when the pane first initialized, so in a session where you never opened the pane, nothing was logged — AutoTagger, Ctrl+T, and any other AI calls were all missed. The ledger subscription now runs at plugin startup (independent of the pane), so every call is recorded. (The pane's handler still drives the Reports tab; usage is not double-counted.)

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
