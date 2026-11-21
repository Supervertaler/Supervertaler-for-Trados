# Supervertaler's entries in the Editor right-click menu

Michael, 21 Nov 2026: the entries were in no particular order. They are now grouped by
what they do. Trados places its own entries; it places ours by the number in each
action's `[ActionLayout]`, highest first.

| Number | Group | Entries, in the order Studio shows them |
|---|---|---|
| 11 | AI on the active segment | Translate active segment, Proofread active segment, FuzzyFixer, Auto-tag active segment |
| 10 | QuickLauncher | QuickLauncher |
| 9 | Search | Search the web, SuperSearch |
| 8 | Terms | Quick add to write termbases, Quick add to project termbase, Quick add non-translatable, Add term with abbreviation (AI), Add term to TermLens (dialog) |
| 7 | SuperMemory | Add to SuperMemory, Add segments to SuperMemory |
| 6 | Voice | Toggle voice commands |

How Studio behaves, read off the 209 menu (not documented by RWS):

- **The numbers stay within 6 to 11**, the range used before, so the block keeps its
  place between Studio's "Add bookmark" and "Create new dictionary term". Studio's own
  entries have numbers too, and a higher or lower one could land ours among them.
- **Within one number, Studio sorts by action ID, Z to A** (it held for all five groups
  in 209). The IDs cannot change: Studio stores a user's own shortcuts by ID. So the
  order inside a group follows the IDs, not a choice.
- **A line is drawn above a group when any entry in it has the separator flag** (the last
  `true` in `[ActionLayout]`). In 209 the groups whose entries all had `false` ran on
  with no line. Every entry now has the flag where its group needs a line.

To add an entry: give it its group's number and the separator flag `true`, and check
the order its ID gives it.
