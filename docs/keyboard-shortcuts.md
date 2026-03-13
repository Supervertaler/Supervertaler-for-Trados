# Keyboard Shortcuts

All keyboard shortcuts available in Supervertaler for Trados, with Mac equivalents for users running Trados in Parallels.

{% hint style="info" %}
**Mac users (Parallels):** Ctrl = Control, Alt = Option on Mac keyboards. Check **Parallels → Preferences → Shortcuts** if your modifier key mapping differs.
{% endhint %}

## Terminology

| Shortcut (Windows) | Shortcut (Mac) | Action |
|---------------------|----------------|--------|
| `Alt+Down` | `Option+Down` | Quick-add term to write termbases |
| `Alt+Up` | `Option+Up` | Quick-add term to project termbase |
| `Ctrl+Alt+T` | `Control+Option+T` | Add term (opens dialog with full control) |
| `Ctrl+Alt+N` | `Control+Option+N` | Quick-add non-translatable term |
| `Ctrl+Alt+G` | `Control+Option+G` | Open Term Picker |
| `Alt+1` ... `Alt+9` | `Option+1` ... `Option+9` | Insert term 1–9 by badge number |

## AI Translation

| Shortcut (Windows) | Shortcut (Mac) | Action |
|---------------------|----------------|--------|
| `Ctrl+Alt+A` | `Control+Option+A` | AI translate the active segment |

## Navigation and Display

| Shortcut (Windows) | Shortcut (Mac) | Action |
|---------------------|----------------|--------|
| `F1` | `F1` | Context-sensitive help |
| `F2` | `F2` | Expand selection to word boundaries |
| `F5` | `F5` | Force refresh TermLens display |

## Shortcuts for Terms 10+

When a segment has more than 9 matched terms, you can still insert terms by number using Alt+digit. TermLens offers two shortcut styles — choose the one you prefer in **Settings > TermLens > Term shortcuts**.

### Sequential (default)

Type the term number digit by digit. Each badge shows the plain term number (10, 11, 12, ...).

| Shortcut (Windows) | Shortcut (Mac) | Inserts |
|---------------------|----------------|---------|
| `Alt+10` | `Option+10` | Term 10 |
| `Alt+23` | `Option+23` | Term 23 |
| `Alt+45` | `Option+45` | Term 45 |

After the first digit, TermLens waits briefly for a possible second (or third) digit. If no further digit is pressed, the single-digit term is inserted.

### Repeated digit

Press the **same digit key** multiple times. Each badge shows the repeated digit (11, 222, 3333, ...).

| Presses | Windows | Mac | Badge | Terms |
|---------|---------|-----|-------|-------|
| 1x | `Alt+1` ... `Alt+9` | `Option+1` ... `Option+9` | **1** – **9** | 1–9 |
| 2x | `Alt+11` ... `Alt+99` | `Option+11` ... `Option+99` | **11** – **99** | 10–18 |
| 3x | `Alt+111` ... `Alt+999` | `Option+111` ... `Option+999` | **111** – **999** | 19–27 |
| 4x | `Alt+1111` ... `Alt+9999` | `Option+1111` ... `Option+9999` | **1111** – **9999** | 28–36 |
| 5x | `Alt+11111` ... `Alt+99999` | `Option+11111` ... `Option+99999` | **11111** – **99999** | 37–45 |

{% hint style="info" %}
In both modes, when a segment has 9 or fewer matches, pressing Alt+N inserts immediately with no delay.
{% endhint %}

{% hint style="info" %}
Terms beyond 45 have no keyboard shortcut. Use the **Term Picker** (`Ctrl+Alt+G` / `Control+Option+G`) to insert them.
{% endhint %}

---

## See Also

- [TermLens](termlens.md)
- [Supervertaler Assistant](ai-assistant.md)
- [Batch Translate](batch-translate.md)
