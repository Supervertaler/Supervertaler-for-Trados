# Keyboard Shortcuts

All keyboard shortcuts available in Supervertaler for Trados.

## Terminology

| Shortcut | Action |
|----------|--------|
| `Alt+Down` | Quick-add term to write termbases |
| `Alt+Up` | Quick-add term to project termbase |
| `Ctrl+Alt+T` | Add term (opens dialog with full control) |
| `Ctrl+Alt+N` | Quick-add non-translatable term |
| `Ctrl+Shift+G` | Open Term Picker |
| `Alt+1` ... `Alt+9` | Insert term 1–9 by badge number |

## AI Translation

| Shortcut | Action |
|----------|--------|
| `Ctrl+Alt+A` | AI translate the active segment |

## Navigation and Display

| Shortcut | Action |
|----------|--------|
| `F1` | Context-sensitive help |
| `F2` | Expand selection to word boundaries |
| `F5` | Force refresh TermLens display |

## Shortcuts for Terms 10+

When a segment has more than 9 matched terms, you can still insert terms by number using Alt+digit. TermLens offers two shortcut styles — choose the one you prefer in **Settings > TermLens > Term shortcuts**.

### Sequential (default)

Type the term number digit by digit. Each badge shows the plain term number (10, 11, 12, ...).

| Shortcut | Inserts |
|----------|---------|
| `Alt+1`, `Alt+0` | Term 10 |
| `Alt+2`, `Alt+3` | Term 23 |
| `Alt+4`, `Alt+5` | Term 45 |

After the first digit, TermLens waits briefly for a possible second (or third) digit. If no further digit is pressed, the single-digit term is inserted.

### Repeated digit

Press the **same digit key** multiple times. Each badge shows the repeated digit (11, 222, 3333, ...).

| Presses | Shortcut example | Badge | Terms |
|---------|-----------------|-------|-------|
| 1x | `Alt+1` ... `Alt+9` | **1** – **9** | 1–9 |
| 2x | `Alt+1,1` ... `Alt+9,9` | **11** – **99** | 10–18 |
| 3x | `Alt+1,1,1` ... `Alt+9,9,9` | **111** – **999** | 19–27 |
| 4x | `Alt+1,1,1,1` ... `Alt+9,9,9,9` | **1111** – **9999** | 28–36 |
| 5x | `Alt+1,1,1,1,1` ... `Alt+9,9,9,9,9` | **11111** – **99999** | 37–45 |

{% hint style="info" %}
In both modes, when a segment has 9 or fewer matches, pressing Alt+N inserts immediately with no delay.
{% endhint %}

{% hint style="info" %}
Terms beyond 45 have no keyboard shortcut. Use the **Term Picker** (`Ctrl+Shift+G`) to insert them.
{% endhint %}

---

## See Also

- [TermLens](termlens.md)
- [Supervertaler Assistant](ai-assistant.md)
- [Batch Translate](batch-translate.md)
