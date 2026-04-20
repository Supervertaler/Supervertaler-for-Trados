# RWS App Store Manager — v4.19.22.0

**Version number:** `4.19.22.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`

This release covers everything since v4.19.20.0. Headline change: architectural overhaul of termbase direction handling that fixes a long-standing class of bug where reverse-direction termbase entries silently failed to match. Also adds a bulk "Reverse source/target" right-click action to the Termbase Editor.

---

## Changelog

### Fixed (termbase direction handling)

- **Term matching now uses the termbase's declared direction, not per-entry language metadata.** Legacy write-path bugs left many termbases with a mix of entries whose per-entry `source_lang` / `target_lang` tags didn't agree with the termbase's declaration — and TermLens trusted those per-entry tags when deciding whether to invert for reverse-direction projects. The result was entries silently not matching even though their text was correct. Direction decisions now pull the source/target language from the canonical `termbases` table, making matching resilient to corrupted per-entry tags. Root-cause fix for the "my termbase entry exists but TermLens says no match" class of bug.
- **The term entry editor now always shows fields in termbase-declared direction.** Previously the edit dialog inverted field layout when the project direction differed from the termbase's, and did so inconsistently depending on which entry point opened it (the Termbase Editor grid right-click did not invert, but the TermLens chip right-click did). Fields, labels, and save flow are now always in termbase order, matching the Termbase Editor grid.

### Added

- **"Reverse source/target" right-click action in the Termbase Editor.** Fixes one or many reversed-direction entries at once. Menu label dynamically shows the count when multiple rows are selected. Swaps `source_term` ↔ `target_term`, language tags, abbreviations, and flips every linked synonym's language tag, all in one transaction. Right-click on the grid now also preserves multi-row selection when the clicked row was already part of the selection (matches Windows list conventions).

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
