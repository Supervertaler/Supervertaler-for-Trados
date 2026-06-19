Supervertaler for Trados **v4.20.63** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.63.

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

## [4.20.63] – 2026-06-19

### Fixed (MultiTerm · termbase file locking)

- **Using Supervertaler with a MultiTerm (.sdltb) termbase no longer makes Trados's own terminology throw a `TermBaseDBAccess` / `SEHException (0x80004005)` error.** A `.sdltb` is a Microsoft Access (JET) database, and Supervertaler reads it directly via OleDb to load terms for TermLens and AI prompts. Those readers are opened and disposed correctly, but .NET pools the underlying OleDb connection by default, so the ACE/JET engine kept the file **locked** (via its `.ldb`/`.laccdb` lock file) long after Supervertaler was done with it. When Trados's *own* MultiTerm engine then browsed the same termbase — e.g. right after a Batch Processing task — it collided with that lingering lock and threw *"An external component has thrown an exception."* Supervertaler now disables OleDb connection pooling for `.sdltb` access (`OLE DB Services=-4`) and releases the connection pool on dispose, so the file lock is gone the moment it finishes reading and Trados can access the termbase normally. Reported in issue #36.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
