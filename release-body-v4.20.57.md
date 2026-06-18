Supervertaler for Trados **v4.20.57** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.57.

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

## [4.20.57] – 2026-06-18

### Changed (Editor context menu)

- **Removed the duplicate "AI translate current segment" entry from the editor right-click menu.** It was a legacy alias that did exactly the same thing as **"Translate active segment" (Ctrl+T)** — translate the active segment using your Batch Translate settings — and only added clutter (and a confusing second Ctrl+T label). The single **"Translate active segment"** command remains, with Ctrl+T. The legacy action is also no longer listed under the plugin's keyboard-shortcuts settings.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
