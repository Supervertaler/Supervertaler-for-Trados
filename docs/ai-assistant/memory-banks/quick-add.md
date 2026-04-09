---
description: Capture terms and corrections while translating
---

# Quick Add (Ctrl+Alt+M)

While translating in Trados, you can instantly add a term or correction to the active memory bank – and optionally inject it into your active translation prompt so the next Ctrl+T picks it up immediately.

<figure><img src="../../.gitbook/assets/Sv_SuperMemory-Add-to-SuperMemory-dialogue.png" alt="Add to memory bank dialogue"><figcaption><p>The Add to memory bank dialogue</p></figcaption></figure>

## How to use

1. In the Trados editor, select the source text you want to capture (optional -- the full source segment is used if nothing is selected)
2. Press **Ctrl+Alt+M** or right-click and choose **Add to memory bank**
3. Fill in the dialogue:
   * **Term / pattern (what's wrong)** -- the incorrect or ambiguous term (pre-filled from your selection)
   * **Correction** -- the correct translation (pre-filled from target selection, if any). The label adapts to your target language (e.g. "Correct Dutch form")
   * **Notes** -- optional context or explanation
   * **Also append to active translation prompt** -- when ticked, a row is added to the TERMINOLOGY table in your [active prompt](active-prompt.md) so the correction takes effect immediately
4. Click **Add**

The term lands in whichever memory bank is currently selected in the toolbar dropdown. To capture into a different bank, switch the dropdown first and then press Ctrl+Alt+M.

## What happens

* A Markdown article is created in the active memory bank's `02_TERMINOLOGY` folder with YAML frontmatter (source term, target term, domain, status, date)
* If the "append to prompt" option is ticked, a new row is inserted into the active prompt's terminology table -- the prompt is read fresh from disk on every Ctrl+T, so the change is instant

{% hint style="success" %}
**Tip:** Quick Add is the fastest way to build up a memory bank while translating. Spotted a Dunglish pattern? Ctrl+Alt+M, type the correction, and carry on -- your future translations automatically avoid that mistake.
{% endhint %}

## See Also

* [Active Prompt](active-prompt.md)
* [Process Inbox](process-inbox.md)
* [SuperMemory](../memory-banks.md)
