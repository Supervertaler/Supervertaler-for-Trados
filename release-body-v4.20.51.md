Supervertaler for Trados **v4.20.51** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.50 → 4.20.51.

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

## [4.20.51] – 2026-06-14

### Changed (AI · termbases)

- **Native MultiTerm (.sdltb) termbases are now opt-in for the AI, the same as Supervertaler's own .db termbases.** Previously a MultiTerm termbase attached to your Trados project could have its terms sent to the AI by default. Now its **AI** column (Settings → Termbases) starts unticked, and its terms are included in AI prompts (Chat, AutoPrompt, Batch Translate and Proofread) only when you explicitly tick it. Your Supervertaler .db termbases are unaffected. If you were relying on a MultiTerm termbase feeding the AI, just tick its AI box once after updating.

## [4.20.50] – 2026-06-14

### Added

- **Usage stats now also include the Windows accessibility text size and the in-app UI-scale setting**, alongside the display scale added in 4.20.49. Together the three show the full picture of how large the UI renders for each user (Windows DPI × text size × the in-app slider). Still opt-out, still nothing identifying.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
