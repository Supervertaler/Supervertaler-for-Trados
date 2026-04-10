---
description: Capture terms, translations, and knowledge while translating
---

# Quick Add (Ctrl+Alt+M)

While translating in Trados, you can instantly capture a term, a translation pair, or a free-form note to the active memory bank – and optionally inject it into your active translation prompt so the next Ctrl+T picks it up immediately.

## How to use

1. In the Trados editor, select the source text you want to capture (optional – the full source segment is used if nothing is selected)
2. Press **Ctrl+Alt+M** or right-click and choose **Add to memory bank**
3. Fill in the dialogue:
   * **Source term** – the source-language term (pre-filled from your selection). The label shows your project's source language, e.g. "Source term (Dutch):"
   * **Target term** – the target-language translation (pre-filled from target selection, if any). The label shows your project's target language, e.g. "Target term (English):"
   * **Notes** – optional context, alternatives, or client preferences
   * **Save as raw note** – when ticked, the entry goes to `00_INBOX/` as a free-form note for the AI to compile via [Process Inbox](process-inbox.md) rather than directly to `02_TERMINOLOGY/` as a structured article. Useful when the knowledge is ambiguous or context-dependent (e.g. "fiche can mean either sheet or plug depending on context")
   * **Also append to active translation prompt** – when ticked, a row is added to the TERMINOLOGY table in your [active prompt](active-prompt.md) so the translation takes effect immediately (only available in structured article mode, not raw note mode)
4. Click **Add**

The entry lands in whichever memory bank is currently selected in the toolbar dropdown. To capture into a different bank, switch the dropdown first and then press Ctrl+Alt+M.

## Two save modes

### Structured article (default)

When "Save as raw note" is **unchecked**, Quick Add creates a finished Markdown article directly in the active memory bank's `02_TERMINOLOGY/` folder with YAML frontmatter (source term, target term, domain, status, date). The article is immediately available to the AI on the next translation – no Process Inbox step needed.

The filename uses the format `source term → target term.md` (e.g. `fiche → plug.md`).

### Raw note

When "Save as raw note" is **checked**, Quick Add writes a free-form Markdown note to `00_INBOX/` instead. The note contains whatever you entered in the source, target, and notes fields, timestamped and labelled as a Quick Add capture. Run [Process Inbox](process-inbox.md) to have the AI compile it into one or more structured articles.

This mode is useful when:
- The knowledge doesn't fit a clean source → target pair (e.g. a term with multiple context-dependent translations)
- You want to capture a general observation or client preference rather than a specific term
- You'd rather let the AI figure out the right article structure

{% hint style="success" %}
**Tip:** Quick Add is the fastest way to build up a memory bank while translating. Spotted an interesting term? Ctrl+Alt+M, type the translation, and carry on – the AI picks it up on the next turn. For ambiguous cases, tick "Save as raw note" and let Process Inbox sort it out later.
{% endhint %}

## See Also

* [Active Prompt](active-prompt.md)
* [Process Inbox](process-inbox.md)
* [SuperMemory](../memory-banks.md)
