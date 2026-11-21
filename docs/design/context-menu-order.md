# Supervertaler's entries in the Editor right-click menu

Michael, 21 Nov 2026: the entries were in no particular order. They are now grouped by
what they do. Trados places its own entries; it places ours by the number in each
action's layout, highest first.

**Studio reads the layout from the manifest, `src/Supervertaler.Trados/Supervertaler.Trados.plugin.xml`,
not from the `[ActionLayout]` attributes.** The same goes for names and shortcuts. Change
both, or the change does nothing (the first install of this tidy showed the old menu).
`tools/check_manifest_actions.py` runs in `build.sh` and stops the build when they disagree.

| Number | Group | Entries |
|---|---|---|
| 11 | AI on the active segment | Translate active segment, Proofread active segment, FuzzyFixer, Auto-tag active segment |
| 10 | QuickLauncher | QuickLauncher |
| 9 | Search | Search the web, SuperSearch |
| 8 | Terms | Add term to TermLens (dialog), Add term with abbreviation (AI), the three Quick add entries |
| 7 | SuperMemory | Add to SuperMemory, Add segments to SuperMemory |
| 6 | Voice | Toggle voice commands |

How Studio behaves, read off the 209 menu against its manifest (not documented by RWS):

- **The numbers stay within 6 to 11**, the range used before, so the block keeps its
  place between Studio's "Add bookmark" and "Create new dictionary term". Studio's own
  entries have numbers too (in its assemblies, not in a manifest we can read), and a
  higher or lower one could land ours among them.
- **Within one number, the order is Studio's.** In 209 it followed neither the manifest
  order nor the IDs nor the names consistently, so treat it as unpredictable. To fix an
  order inside a group, the entries need numbers of their own, which needs more room
  than 6 to 11 and so knowledge of Studio's own numbers.
- **A line is drawn above a group when any entry in it has the separator flag** (the last
  argument of the layout). In 209 the groups whose entries all had `false` ran on with
  no line, and entries sharing a number never had a line between them.

To add an entry: give it its group's number and the separator flag `true`, in the
attribute and in the manifest.
