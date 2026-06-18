Supervertaler for Trados **v4.20.49** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.49.

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

## [4.20.49] – 2026-06-14

### Fixed (high-DPI / Windows display scaling)

- **The Settings dialog now lays out cleanly at any Windows display scale.** A user on a 4K display at 175% display scaling plus 175% text scaling reported clipped buttons, overlapping rows and cut-off text, mainly in the Settings tabs (issue #37). Every tab — General, Termbases, AI Settings, Prompts, Licence and Backup — was rebuilt with `TableLayoutPanel`-based layout, AutoSize buttons and the plugin's `UiScale` system instead of absolute pixel positioning, so labels, fields and buttons size to their (scaled) content and reflow automatically. Nothing clips or overlaps at 100/125/150/175% or any custom scale, and the in-app **UI scale** setting now affects the Settings dialog too. The Batch Operations pane's mode toggle and Scope dropdown, and the editor pencil/glyph buttons, were fixed the same way.

### Added

- **Anonymous usage stats now include the Windows display scale** (e.g. "175"), so the share of users on high-DPI scaling can be seen at a glance. Still opt-out, still nothing identifying — just the DPI percentage alongside the existing OS / Studio version / locale.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
