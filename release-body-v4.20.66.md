Supervertaler for Trados **v4.20.66** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.66.

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

## [4.20.66] – 2026-06-19

### Added (MultiTerm · AI opt-in inherits from project templates)

- **The MultiTerm "AI" opt-in now travels with Trados project templates.** Tick a MultiTerm termbase for AI in a project, then save that project as a project template (Create Project Template based on this project) — and every new project created from that template inherits the choice automatically, with no per-project re-ticking. This is aimed at automated / CLI-driven project creation, where many projects are spun up from one template each day (issue #36). It works by mirroring the opt-in into the **Trados project settings bundle** (which templates capture and pass on), in addition to Supervertaler's own per-project store; the existing explicit opt-in is preserved — the conscious decision just happens once, on the template. The choice is stored as the termbase's path, so it applies to any project that attaches the same termbase.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
