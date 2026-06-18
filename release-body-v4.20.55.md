Supervertaler for Trados **v4.20.55** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.54 → 4.20.55.

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

## [4.20.55] – 2026-06-17

### Fixed (Add Term · Chinese and other no-space scripts)

- **Adding a term with a Chinese (or Korean/Japanese) target no longer saves the whole segment.** When you selected part of the target — e.g. 挂车控制模块 ("trailer control module") — the term that got saved was the entire target segment, 挂车控制模块的更换. The Add Term / Quick-Add actions expand a partial selection out to word boundaries, but that expansion stopped only at **whitespace**, and Chinese has no spaces between words, so it ran to the segment edges and swallowed everything. Two causes: (1) the language auto-detection for "no auto-expand" only recognised Korean and Japanese, never Chinese; and (2) the **target**-side expansion ignored the language entirely (only the source side honoured it), so even Korean/Japanese targets were affected. Both are fixed: Chinese is now detected as a no-space script, and the target expansion now honours the project's target language. For these scripts the Add Term actions keep your exact selection (there is no word auto-expansion yet, so select the precise characters you want). As a side benefit, detecting Chinese also enables CJK suffix-tolerant term matching, so Chinese terms highlight more reliably in TermLens. (Reported by a user adding Chinese terminology.)

## [4.20.54] – 2026-06-16

### Fixed (MultiTerm · AI)

- **The MultiTerm "AI" tick now survives a Trados restart.** A MultiTerm termbase ticked for AI in Settings → Termbases would silently revert to unticked after reopening Trados, so the AI stopped seeing the terminology until you re-ticked it. The tick is stored per-project, but the per-project save only ran when the Editor view part had already tracked the active project — which isn't the case when Settings is opened from the AI Assistant panel (the same path 4.20.53 made the row visible from). The tick then lived only in the global settings and was overwritten by the empty per-project overlay on the next restart. The current-project lookup now falls back to the Editor's active document, so the per-project save always runs and the choice persists. (Fixes #36.)

### Changed (MultiTerm · internal)

- **MultiTerm termbase synthetic IDs are now derived from a stable hash of the file path** (FNV-1a) instead of `String.GetHashCode()`. `GetHashCode` is documented as unsafe to persist — it varies across .NET runtimes and between 32-bit and 64-bit processes — so the previous IDs, used as the persistence key for each termbase's AI / enabled state, would not survive a move to a different runtime or bitness. Hardening only; current behaviour is unchanged on the existing .NET Framework build.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
