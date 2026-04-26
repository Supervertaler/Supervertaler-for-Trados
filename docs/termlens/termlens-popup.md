{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** – the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

The **TermLens popup** is a borderless floating version of the docked TermLens panel for the active segment. Designed for keyboard-only term selection on small screens — and for translators who want to insert terms without ever reaching for the mouse.

## When to use it

- **Small screens / laptops** — keeping the docked TermLens panel always-visible can cost too much vertical space, especially for longer source sentences. The popup gives you the same view on demand and disappears when you're done.
- **Pure-keyboard workflows** — Ctrl-tap, cycle, Enter, back to typing. No mouse, no menu hunting.

## Opening and closing

| Key | Action |
|-----|--------|
| **Ctrl** (tap) | Toggle the popup (open if closed, close if open) |
| **Ctrl+Alt+G** | Open the popup (alternative shortcut) |
| **Escape** | Close without inserting |
| Click outside the popup | Close without inserting |

A "Ctrl tap" is a press-and-release of the Ctrl key on its own — no other key in between, and held for less than 400 ms. The same memoQ-style trigger that older versions of Supervertaler used to open the Term Picker dialogue.

## Cycling between matches

When the popup opens, the first match has an amber ring around its source word — that is the **current match** that Enter will insert.

| Key | Action |
|-----|--------|
| **Right** / **Down** / **Tab** | Move the current-match highlight to the next match |
| **Left** / **Up** / **Shift+Tab** | Move it to the previous match |

Cycling wraps: from the last match, Right takes you back to the first.

## Inserting

| Key / action | Result |
|--------------|--------|
| **Enter** | Insert the current match into the target segment, close the popup, return focus to the target cell |
| **Click any chip** | Insert that match into the target segment, close the popup, return focus to the target cell |

Both paths share the same insertion logic — there is no difference between picking by keyboard and picking by mouse.

## Visuals

The popup uses the same chip rendering, colour scheme, and metadata indicators as the docked TermLens panel — pink for project termbase terms, blue for regular Supervertaler terms, yellow for non-translatable, green for MultiTerm. See the [TermLens overview](../termlens.md) for the full colour key.

## TermLens popup vs Term Picker dialogue

Both show the same matches for the active segment. Pick whichever fits your style:

| | TermLens popup (Ctrl tap) | [Term Picker dialogue](term-picker.md) (Ctrl+Shift+T) |
|---|---|---|
| Layout | Source segment with chips underneath each matched word | Sortable, scrollable table |
| Best for | Skimming matches in segment context | Many matches that benefit from sorting / typing-to-jump |
| Keyboard | Arrow / Tab cycles a highlighted match | 0–9 jumps directly; Up / Down navigate |
| Modality | Modeless – click outside to dismiss | Modal – Escape or Cancel to close |

---

## See Also

- [TermLens overview](../termlens.md)
- [Term Picker dialogue](term-picker.md)
- [Keyboard shortcuts](../keyboard-shortcuts.md)
