Supervertaler for Trados **v4.20.48** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.45 → 4.20.48.

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

## [4.20.48] – 2026-06-12

### Fixed (AI · inline tags)

- **Hardened inline-tag handling against models that mangle tags.** A user reported that Mistral Large recently began emitting empty inline-tag pairs — an opening tag immediately followed by its own closing tag, with the translated text left outside — instead of wrapping the translated words. Two defences: (1) the AI translation prompt now explicitly forbids empty tag pairs and reminds the model of the exact tag format (with a wrong/right example); (2) the tag-placeholder parser is now whitespace- and case-tolerant, so a tag that drifts slightly from the canonical form (a stray space inside it, or the wrong letter case) is still recognised and reconstructed — or cleanly stripped — instead of leaking into the target as literal text. Helps every provider, not just Mistral.

## [4.20.47] – 2026-06-12

### Added (TermLens: Korean / Japanese particle handling)

- **TermLens now recognises terms in Korean and Japanese even when a grammatical particle is attached to the noun.** Previously matching was whole-token, so a clean term like 값 ("value") or 제2 전압 값 ("second voltage value") would not highlight in 값으로 / 제2 전압 값을 / …, because the trailing particle made the segment token differ — and adding a term auto-expanded the selection to the whole token, capturing the particle (saving 장치의 instead of the intended 장치). Both sides are now particle-aware:
  - **Matching** is suffix-tolerant: a single CJK token matches the longest term that is a prefix of it (값 ↦ 값으로), and a multi-word term matches when its final CJK token is a prefix of the segment token (제2 전압 값 ↦ 제2 전압 값으로), with the highlight spanning the attached particle so no text is dropped.
  - **Adding a term** keeps your exact selection instead of expanding to the whitespace word, so the bare noun is saved. (F2 still expands explicitly when you want it.)
  - Controlled by a new **Particle matching** setting (Settings → Termbases): **Auto** (default — on for Korean/Japanese source), **Always on** (e.g. Chinese or another language), or **Always off**. Only CJK-script tokens are prefix-matched, so European languages are unaffected. Addresses issue #34.

## [4.20.46] – 2026-06-12

### Fixed (GPT-5.5 temperature)

- **GPT-5.5 now works – Test Connection and translation no longer fail with an "unsupported temperature" error.** OpenAI's GPT-5.5 only accepts the default temperature value and returns *"Unsupported value: 'temperature' does not support 0.3 with this model. Only the default (1) value is supported."* if any explicit temperature is sent. The plugin sent a fixed `temperature: 0.3` on every OpenAI request for non-reasoning models, so GPT-5.5 failed both the Test Connection check and any actual translation, while GPT-5.4 Mini (which does accept a custom temperature) worked. Models now carry a `SupportsTemperature` flag; GPT-5.5 is marked as temperature-locked, so the parameter is omitted for it across all request paths (Test Connection, single-segment, batch translate, and tool-use), and a heuristic covers any future full GPT-5.x model entered in the custom Model ID field. Reported in issue #36.

## [4.20.45] – 2026-06-12

### Changed (AI pane renamed)

- **The AI pane is called "Supervertaler Assistant" again.** The dockable pane that hosts Chat, Batch Operations, Import/Export and Reports had been shortened to bare "Supervertaler", which collided with the product name (Supervertaler for Trados) and read awkwardly in the documentation ("The Supervertaler supports …"). It is restored to "Supervertaler Assistant" – both the pane/dock-tab caption and the View menu entry – so the pane is clearly distinct from the product and the help docs match. No functional change.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Support & community forum: https://forum.supervertaler.com/
