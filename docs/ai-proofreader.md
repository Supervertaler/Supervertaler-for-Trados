{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

The AI Proofreader checks your translated segments for errors using AI. It identifies issues such as mistranslations, omissions, grammar problems, and inconsistencies, and presents the results as clickable issue cards in the **Reports** tab.

## Starting a Proofreading Run

1. Open the **Supervertaler Assistant** panel (View > Supervertaler Assistant)
2. Switch to the **Batch Operations** tab
3. Select **Proofread** (instead of Translate)
4. Choose a **scope** from the dropdown
5. Optionally select a proofreading **prompt** from the prompt selector
6. Click **Proofread**

## Scope

The scope dropdown controls which segments are checked:

| Scope                        | Description                                                                     |
| ---------------------------- | ------------------------------------------------------------------------------- |
| **Confirmed Only**           | Checks only segments with Confirmed status                                      |
| **Translated + Confirmed**   | Checks segments with Translated or Confirmed status                             |
| **All Segments**             | Checks every segment that has target text                                       |
| **Filtered Segments**        | Checks only segments visible after applying a Trados display filter             |
| **Filtered (Confirmed Only)**| Checks confirmed segments within the current filter                             |

## Prompt Selection

When in Proofread mode, the prompt dropdown shows only prompts with the **Proofread** category. This keeps the list focused — translation prompts are hidden.

If no prompt is selected, the AI uses a default proofreading instruction that checks for accuracy, completeness, grammar, and consistency.

{% hint style="info" %}
You can create custom proofreading prompts in the [Prompt Manager](settings/prompts.md). Set the category to **Proofread** so they appear in the dropdown when proofreading.
{% endhint %}

## Reports Tab

Proofreading results appear in the **Reports** tab of the Supervertaler Assistant panel. Each issue is shown as a clickable card containing:

* **Segment number** — the actual per-file segment number as shown in the Trados editor grid
* **Issue description** — what the AI found wrong
* **Suggestion** — the AI's recommended fix (if available)

### Navigating to Issues

Click any issue card to navigate directly to that segment in the Trados editor. This works correctly in multi-file projects — the plugin uses the segment's internal identifiers to find the exact segment.

### Clearing Results

Click the **Clear** button at the top of the Reports tab to remove all results and start fresh.

### Run Summary

After a proofreading run, the Reports tab shows:

* Total number of issues found and segments checked
* Run timestamp and duration in the footer

## Adding Issues as Trados Comments

Check the **"Also add issues as Trados comments"** checkbox in the Batch Operations tab (visible only in Proofread mode) before starting the run. When enabled, each issue found by the proofreader is also inserted as a Trados segment comment, so you can see the issues directly in the editor without switching to the Reports tab.

## Tips

### Start with Confirmed Segments

Use the **Confirmed Only** scope to check segments you consider finished. This avoids noise from segments that are still being worked on.

### Use Domain-Specific Proofreading Prompts

Create custom proofreading prompts tailored to your domain. For example, a medical proofreading prompt can check for correct use of clinical terminology, while a legal proofreading prompt can verify that defined terms are used consistently.

### Review After AI Translation

The AI Proofreader pairs well with [Batch Translate](batch-translate.md). After translating a batch of segments with AI, run the proofreader to catch any issues before final review.

### Combine with Display Filters

Use Trados display filters to isolate specific segments (e.g., segments containing a certain term), then proofread only those filtered segments for targeted quality checks.

---

## See Also

* [Batch Translate](batch-translate.md)
* [Prompts](settings/prompts.md)
* [Supervertaler Assistant](ai-assistant.md)
* [Keyboard Shortcuts](keyboard-shortcuts.md)
