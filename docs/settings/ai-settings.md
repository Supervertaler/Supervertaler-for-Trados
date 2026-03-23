{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Configure the AI provider, model, and context options used by the Supervertaler for Trados plugin.

## Accessing AI settings

Open the plugin **Settings** dialog and switch to the **AI** tab.

## Provider selection

Choose one of the supported AI providers:

| Provider | Description |
|----------|-------------|
| **OpenAI** | GPT-4.1, GPT-4.1 Mini, GPT-5.4, o4-mini (reasoning) |
| **Claude (Anthropic)** | Claude Sonnet 4.6, Claude Haiku 4.5, Claude Opus 4.6 |
| **Gemini (Google)** | Gemini 2.5 Flash, Gemini 2.5 Pro, Gemini 3.1 Pro (Preview) |
| **Grok (xAI)** | Grok 4.20, Grok 4.1 Fast, Grok 4.20 (Reasoning) |
| **Ollama (Local)** | Run models locally, no API key required |
| **Custom (OpenAI-compatible)** | Any provider with an OpenAI-compatible API |

{% hint style="info" %}
You only need one provider to get started. See [Setting Up API Keys](https://supervertaler.gitbook.io/supervertaler/get-started/api-keys) for instructions on obtaining a key.
{% endhint %}

## API key

Enter the API key for your selected provider. The key is stored locally and never sent anywhere except to the provider's API endpoint.

## Model selection

A dropdown showing available models for the selected provider. The list is fetched automatically when a valid API key is entered.

## Ollama endpoint

When using Ollama as the provider, this field sets the local endpoint URL. Defaults to:

```
http://localhost:11434
```

Change this only if you are running Ollama on a different port or a remote machine.

## Custom OpenAI-compatible provider

For providers that expose an OpenAI-compatible API (e.g., Azure OpenAI, together.ai, local inference servers), configure these fields:

| Field | Description |
|-------|-------------|
| **Display name** | A label for this provider (shown in the provider dropdown) |
| **Endpoint URL** | The base URL for the API (e.g., `https://your-server.com/v1`) |
| **API key** | The authentication key for this endpoint |
| **Model name** | The model identifier to use (e.g., `llama-3-70b`) |

## AI context options

These options control what additional context is included when the AI translates a segment.

### Include termbases in AI prompt

When enabled, terminology matches from active termbases for the current segment are injected into the translation prompt. This helps the AI use the correct, approved terminology.

For [prompt generation](../generate-prompt.md), **TermScan** automatically filters the termbase to only terms that appear in the document's source text, keeping the prompt focused and within token limits.

{% hint style="warning" %}
**Only enable termbases you trust.** The AI will follow your glossary entries even when they are wrong. If a termbase contains inaccurate, outdated, or low-quality translations, the AI will be forced to use them — producing worse results than if no termbase were enabled at all. Modern LLMs are remarkably good at choosing correct terminology on their own. When in doubt, disable termbases and add terms incrementally as you review the AI's output.
{% endhint %}

### Include TM matches

When enabled, translation memory matches for the current segment are included in the prompt. This gives the AI context from previous translations, improving consistency. This setting also controls whether TM reference pairs are included when using [Analyse Project & Generate Prompt](../generate-prompt.md).

### Include full document content

When enabled, all source segments in the current document are sent to the AI so it can determine the document type (legal, medical, technical, marketing, etc.) and provide context-appropriate assistance. This uses more tokens but greatly improves response quality — the AI can tailor its terminology and style recommendations to the specific type of document you are translating.

For very large documents, the content is automatically truncated to the configured maximum. The truncation preserves the beginning and end of the document (first 80% + last 20%).

### Max segments

The maximum number of source segments to include in the AI prompt when document content is enabled. Default: **500**. Range: 100–2000.

Increase this for very large documents where you want the AI to see more content. Decrease it if you want to reduce token usage.

{% hint style="info" %}
This setting is only available when **Include full document content** is enabled.
{% endhint %}

### Include term definitions and domains

When enabled, term definitions, domains, and usage notes from your termbases are included alongside matched terminology in the AI prompt. This gives the AI deeper understanding of your terminology — for example, knowing that a term belongs to the legal domain or has a specific definition helps the AI use it correctly.

{% hint style="success" %}
**Tip:** For the best results, enable all context options. The more information the AI has about your project, document, terminology, and previous translations, the more accurate and consistent its suggestions will be.
{% endhint %}

## Prompt logging

### Log prompts and responses to Reports tab

When enabled, every AI API call is logged to the **Reports** tab in the Supervertaler Assistant panel. Each log entry shows:

* The **feature and prompt name** (e.g. "QuickLauncher · Explain in Context")
* The **model used**, estimated **token counts**, **cost**, and **duration**
* Expandable sections for the **system prompt**, **messages**, and **response**

Click "Show system prompt...", "Show messages...", or "Show response..." to expand a section. Press **Escape** to collapse it. Use **Copy** to copy a single section, or **Copy all** to copy the full prompt details to your clipboard.

This is useful for:

* **Monitoring costs** — see exactly how many tokens each operation uses
* **Debugging prompts** — inspect the full text sent to the AI to understand its behaviour
* **Comparing models** — run the same prompt with different models and compare token usage

{% hint style="info" %}
Prompt logging is off by default to keep the Reports tab clean. Enable it when you want to inspect or audit your AI usage. Log entries are stored in memory only and cleared when Trados restarts.
{% endhint %}

## Batch settings

Configure the **batch size** for the [Batch Translate](batch-translate.md) feature. This determines how many segments are sent to the AI provider in a single request.

- A larger batch size is faster but uses more tokens per request
- A smaller batch size is more granular and easier to review

---

## See Also

- [Prompts](prompts.md)
- [AI Cost Guide](../ai-cost-guide.md)
- [TermLens Settings](termlens.md)
- [Supported LLM Providers (Workbench)](https://supervertaler.gitbook.io/supervertaler/ai-translation/providers)
