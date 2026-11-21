# RWS App Store Manager - v18.20.209

Three builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2022 | `17.20.209.0` | `17.0` | `17.9` | `46f95326f9e9aa0be4233ae01c987166f75af6cfc9358931bf8f22ccfcfc7846` |
| Studio 2024 | `18.20.209.0` | `18.0` | `18.9` | `5f96fdbd77d5a801d5d6122ab2c2b8dca672151e0e14230ffa36947149eec3be` |
| Studio 2026 | `19.20.209.0` | `19.0` | `19.9` | `5eb0398df923642681c1f88a6e11f500259936bf425f226ec3c8d145babe8511` |

---

## Changelog

Paste everything between the two rules into the Changelog tab, as it is.

---

New:
- A model per kind of task: Light tasks, Translation and AutoPrompt.
- The QuickLauncher menu (Alt+Q) starts with Translate, FuzzyFixer and Proofread.
- Claude Haiku 5.5 is in the Claude model list.
- Proofread active segment: the AI proofreads the segment you are in (Ctrl+Alt+P).
- See what a project template holds.
- Save, update and edit project templates in Supervertaler.
- Apply a project template to a project you already have.
- Stamping the project’s name skips a TM used in reverse.
- And 1 more.

Changed:
- A project template made from a project now carries Supervertaler’s project settings.
- FuzzyFixer requests are now listed as FuzzyFixer, and its window names the model.
- Prompts written by AutoPrompt now put your termbase before translation memory matches.
- The prompt list's first entry is named after what it sends.

Fixed: 2 fixes.

Full changelog: https://supervertaler.com/changelog/trados/#v18.20.209

---
