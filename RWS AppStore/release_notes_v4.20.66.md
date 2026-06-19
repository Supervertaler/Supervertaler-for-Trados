# RWS App Store Manager – v4.20.66.0

**Version number:** `4.20.66.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `991f808f125ae8e24776cf62e6e471ad08093b8c3d5c7e65ba33caf7e28402ce`

---

## Changelog

*Covers everything since the previous App Store notes (4.20.62). If 4.20.62 was not published to the App Store, combine with `release_notes_v4.20.62.md` for the full list back to 4.20.51.*

### Added

- **The MultiTerm "AI" opt-in now travels with Trados project templates.** Tick a MultiTerm termbase for AI in a project (Settings → Termbases), then save that project as a project template (**Create Project Template based on this project**) — and every new project created from that template inherits the choice automatically, with no per-project re-ticking. This is aimed at automated / CLI-driven project creation, where many projects are spun up from one template each day. It mirrors the opt-in into the **Trados project settings bundle** (which templates capture and pass on), in addition to Supervertaler's own per-project store; the existing explicit opt-in is preserved — the conscious decision just happens once, on the template. The choice is stored by termbase, so it also applies to any other project that attaches the same termbase. Requested in issue #36.

### Fixed

- **Using Supervertaler with a MultiTerm (.sdltb) termbase no longer makes Trados's own terminology throw a `TermBaseDBAccess` / `SEHException (0x80004005)` error.** A `.sdltb` is a Microsoft Access (JET) database, and Supervertaler reads it directly via OleDb to load terms for TermLens and AI prompts. Those readers are opened and disposed correctly, but .NET pools the underlying OleDb connection by default, so the ACE/JET engine kept the file **locked** (via its `.ldb`/`.laccdb` lock file) long after Supervertaler was done with it. When Trados's *own* MultiTerm engine then browsed the same termbase — e.g. right after a Batch Processing task — it collided with that lingering lock and threw *"An external component has thrown an exception."* Supervertaler now disables OleDb connection pooling for `.sdltb` access and releases the connection pool on dispose, so the file lock is gone the moment it finishes reading and Trados can access the termbase normally. Reported in issue #36.

---

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases
