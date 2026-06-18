Supervertaler for Trados **v4.20.53** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.53.

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

## [4.20.53] – 2026-06-15

### Fixed (MultiTerm)

- **An attached MultiTerm (.sdltb) termbase now shows in the Termbases tab even when Settings is opened without the editor in focus.** The grid's MultiTerm list previously came only from the live TermLens editor view part, so if the Settings dialog was opened from the AI Assistant panel, or with no document active in the Editor, an attached .sdltb (and its "AI" checkbox) would silently not appear. The Settings dialog now falls back to detecting the active project's MultiTerm termbases directly, so the row appears whenever a .sdltb is attached, however Settings was reached. (Investigating #36.)

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
