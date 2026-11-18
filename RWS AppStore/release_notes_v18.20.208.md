# RWS App Store Manager - v18.20.208

Three builds ship from this one release (identical feature set, distinct
version numbers so the App Store never sees a collision):

| Build | Version number | Min studio | Max studio | Checksum (SHA-256) |
|-------|----------------|------------|------------|--------------------|
| Studio 2022 | `17.20.208.0` | `17.0` | `17.9` | `03599b4ad4608bd8671f457798d667e2454be4fde0cda5dbfaf8fa35e62d3176` |
| Studio 2024 | `18.20.208.0` | `18.0` | `18.9` | `6209bd64fcfc47c92df2698aea03ae01340624c32966d6b689448c4370f66a94` |
| Studio 2026 | `19.20.208.0` | `19.0` | `19.9` | `f6b021cc382f7dcb46d865625830002108192a6f0f3378760cea2541d44ab196` |

---

## Changelog

Paste everything between the two rules into the Changelog tab, as it is.

---

New:
- FuzzyFixer: the AI repairs a fuzzy match for you (Ctrl+Alt+F).
- “Include full document content in AI context” is now two tick boxes, one on the Chat tab and one on the Batch Operations tab.
- AutoPrompt asks before it sends a very long document.

Changed:
- With Claude, Translate active segment (Alt+T) and Chat now keep the cached part of the request for an hour instead of five minutes.
- The Reports tab now lists AI requests oldest first, with the newest at the bottom, like the chat.
- The licence is now checked at licence.supervertaler.com.
- Requests made with Translate active segment (Alt+T) are now labelled as such.

Fixed: 19 fixes.

Full changelog: https://supervertaler.com/changelog/trados/#v18.20.208

---
