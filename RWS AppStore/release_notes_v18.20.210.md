# RWS App Store Manager - v18.20.210

Three builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2022 | `17.20.210.0` | `17.0` | `17.9` | `725d34f315dbc508ed1b2bb896cf29b5720bb066ace4fe7b2dbe25c83e8ba758` |
| Studio 2024 | `18.20.210.0` | `18.0` | `18.9` | `9a542561559c254bc713a0a513ac69f2ce694de022781aa266d0cf3a7f4d7b97` |
| Studio 2026 | `19.20.210.0` | `19.0` | `19.9` | `d4adef71fbbcaa9bab5f8c384f23e616abdcf7d9ac6acb38fda3513e4f1d2469` |

---

## Changelog

Paste everything between the two rules into the Changelog tab, as it is.

---

New:
- EverTM Matches: EverTM's matches in a pane of their own (View > EverTM Matches).
- EverTM Matches can show your Studio TMs' matches too.
- EverTM Matches follows EverTM's ticks in the project's TM list, as Studio does.
- EverTM: correcting a segment you'd confirmed could overwrite or remove an unrelated unit in EverTM (versions 206 to 209).
- Remove MT marks: a batch task that takes the machine translation mark off your files.
- Supervertaler warns you when Trados Studio is still running in the background.
- Open Supervertaler Settings from anywhere in Studio: Ctrl+Alt+Shift+S, or the Add-Ins tab.
- An AI chat can now see and change which prompt your project uses.
- And 2 more.

Changed:
- The Supervertaler MCP Server can also connect to Supervertaler Workbench.
- A Copy button next to every API key in the AI Settings.
- A prompt you save after AutoPrompt is now the one Batch Translate uses.
- Supervertaler for Trados has its own prompt library.
- Supervertaler's entries in the Editor's right-click menu are grouped by what they do.
- During the trial, with the anonymous usage statistics on, the trial check-in also says how many AI requests have been made.
- The anonymous usage statistics report the computer's processor type, not the one Trados is emulated as.

Fixed: 9 fixes.

Full changelog: https://supervertaler.com/changelog/trados/#v18.20.210

Supervertaler missing after the update? See https://docs.supervertaler.com/trados/troubleshooting/#the-installer-finishes-but-the-plugin-never-appears

---
