{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Batch Translate lets you translate multiple segments at once using AI. It is located in the **Supervertaler Assistant** panel, on the **Batch Operations** tab.

{% hint style="info" %}
The Batch Operations tab also supports **Proofread** mode for AI-powered quality checking. See [AI Proofreader](ai-proofreader.md) for details.
{% endhint %}

<figure><img src=".gitbook/assets/image (3).png" alt=""><figcaption></figcaption></figure>

## Starting a Batch Translation

1. Open the **Supervertaler Assistant** panel (View > Supervertaler Assistant)
2. Switch to the **Batch Translate** tab
3. Choose a **scope** from the dropdown
4. Choose a **prompt** from the prompt selector
5. Click **Translate**

## Scope

The scope dropdown controls which segments are translated:

| Scope                   | Description                                                            |
| ----------------------- | ---------------------------------------------------------------------- |
| **Empty Segments Only** | Translates segments that have no target text                           |
| **All Segments**        | Translates every segment in the file                                   |
| **Filtered Segments**   | Translates only the segments currently visible after applying a filter |
| **Filtered Empty Only** | Translates empty segments within the current filter                    |

## Prompt Selection

Choose a prompt to guide the AI translation style and domain. The prompt selector shows:

* **Default Translation Prompt** – a general-purpose prompt that works well for most content types. Use it as-is or duplicate it in the Prompt Manager and customise it for your domain.
* **Custom prompts** – your own prompts created in the Prompt Manager

{% hint style="info" %}
For specialised fields (medical, legal, patent, etc.), create a custom prompt with domain-specific terminology rules and instructions. A tailored prompt is the single most effective way to improve translation quality.
{% endhint %}

## Provider and Model

The current AI provider and model are displayed below the prompt selector. To change them, open the settings dialog (gear icon in the TermLens header) and go to the **AI Settings** tab.

## Progress and Logging

During translation:

* A **progress bar** shows overall completion
* A **real-time log** displays the status of each segment as it is translated
* The **Stop** button aborts the batch at any time – segments already translated are kept

## Translate Active Segment (Ctrl+T)

Press **Ctrl+T** to translate the active segment instantly. This uses the same provider, model, and prompt as Batch Translate, so you can switch prompts or providers and immediately use them for single segments with Ctrl+T.

Ctrl+T is also available via right-click in the editor ("Translate active segment").

### How it works

1. The active segment's source text is sent to the AI provider configured in AI Settings
2. The selected prompt (from the Batch Translate tab) is applied, along with termbase terms
3. The translation is written directly into the target cell
4. Inline tags (bold, italic, field codes, etc.) are preserved in the translation

## Tips

### Translate Empty Segments First

Start by translating only the empty segments (scope: **Empty Segments Only**). Review the results, then fix any issues. This avoids overwriting segments you have already edited.

### Generate a Domain-Specific Prompt Automatically

Click **Analyse Project & Generate Prompt…** next to the prompt dropdown. Supervertaler analyses your entire document, detects the domain, and uses AI to generate a comprehensive translation prompt with terminology rules, style guidelines, and anti-truncation controls — all tailored to your specific project. See [Analyse Project & Generate Prompt](generate-prompt.md) for details.

### Create Domain-Specific Prompts Manually

For specialised content, you can also duplicate the Default Translation Prompt in the Prompt Manager and add domain-specific instructions (terminology rules, style preferences, formatting requirements). A tailored prompt is the single most effective way to improve translation quality.

### Combine with TM

If your project has a translation memory, TM matches are shown alongside AI translations. You can pre-translate with TM first (using Trados's built-in batch tasks), then use Batch Translate to fill in the remaining empty segments with AI.

### Review After Batch

AI translation is a first draft. After a batch run:

1. Review each translated segment
2. Fix any terminology or style issues
3. Confirm segments with **Ctrl+Enter** (Trados default)

***

## See Also

* [Analyse Project & Generate Prompt](generate-prompt.md)
* [AI Proofreader](ai-proofreader.md)
* [Supervertaler Assistant](ai-assistant.md)
* [TermLens](termlens.md)
* [Keyboard Shortcuts](keyboard-shortcuts.md)
