Supervertaler for Trados **v4.20.86** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.85 → 4.20.86.

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

## [4.20.86] – 2026-07-02

### Fixed (Bilingual re-import · no longer freezes and crashes on large multi-file projects)

- **Re-importing a bilingual file into a big merged multi-file project no longer freezes the editor and crashes Trados.** A user re-importing a 1,178-segment, 9-file Bilingual Text (`.txt`) file saw the same segments appear to loop endlessly, then Trados closed with no warning. The cause was the writeback path, not the file (which was structurally perfect): for **every** changed segment it re-scanned the *entire* document's segment list to find the match (an expensive `GetParentParagraphUnit` per segment pair) — roughly **1.4 million SDK model calls** (O(n²)) on a document that size — all on the UI thread, with no memory guardrail. That froze the UI (the "looping" the user saw was a hung, mid-scroll editor) and, on 32-bit Trados Studio 2024, exhausted the ~2 GB address space into a silent crash. Three changes fix it:
  - **O(1) segment lookup.** The document's segments are now indexed once up front into a `(paragraph-unit-id / segment-id) → segment` map, so each change is resolved by a dictionary lookup instead of a full re-scan. This removes the ~1.4M-call blow-up; the writeback is now linear in the number of changes.
  - **32-bit memory watchdog.** The writeback now uses the same guard as Batch Translate (added in 4.20.77): it compacts the heap when memory climbs and **stops gracefully with a clear message** before it can crash the 32-bit host, telling you to finish in Trados Studio 2026 (64-bit) or to re-import with fewer files open. A no-op on 64-bit. Re-importing is safe to repeat — already-applied segments come back as "unchanged".
  - **Responsive progress + Cancel.** A small progress window now shows "Applying changes… N of M" with a **Cancel** button; the loop pumps the UI so it stays responsive instead of appearing frozen.
- **Collision note for merged files.** Paragraph-unit ids are only unique *within* a single `.sdlxliff`, so a merged multi-file document can in principle have colliding ids across files. Re-import now detects this and logs a note (it writes to the first match; full file-aware routing via the manifest's `SourceFileId` is a planned follow-up). The reported project did not collide — all 1,178 ids were unique — so this is hardening, not the fix for that case.

## [4.20.85] – 2026-07-01

### Changed (Bilingual export/import simplified to two round-trippable formats + one report)

- **The bilingual export now offers just three formats, matching the Supervertaler Workbench:**
  - **Word document (`.docx`)** – the 5-column Bilingual Table, re-importable.
  - **Bilingual Text (AI-friendly) (`.txt`)** – the `[SEGMENT NNNN]` plain-text format, re-importable. **New:** promoted from the old "Bracketed" *layout* to a standalone *format* with a `.txt` extension.
  - **HTML report (`.html`)** – read-only, as before.
- **Retired the standalone Markdown (`.md`) format and both Stacked layouts** ("source on top" / "target on top"). They were confusing and duplicated the Table/Text formats. The **Layout** dropdown is gone entirely – each format now has one natural layout (DOCX/HTML → table, Text → bracketed). Existing `.md`/stacked files can **still be re-imported** (the importer stays backward-compatible); they just can't be produced any more.
- **In-field line breaks in the Text format are now encoded as a `[newline]` token** (decoded back to a real break on re-import), so every segment field stays on one physical line. This matches the Workbench's "Bilingual Text" export, so a file produced by either tool round-trips through the other. Older multi-line files without the token still re-import unchanged.
- The multi-file output-mode radios are now format-neutral ("Combine into one file" / "Separate file per file") since they apply to both DOCX and Text exports, not just DOCX.

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
