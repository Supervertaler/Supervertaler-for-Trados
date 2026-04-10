{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** – the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Configure the AI provider, model, and context options used by the Supervertaler for Trados plugin.

## Accessing AI settings

Open the plugin **Settings** dialogue and switch to the **AI** tab.

## Provider selection

Choose one of the supported AI providers:

| Provider | Description |
|----------|-------------|
| **OpenAI** | GPT-5.4, GPT-5.4 Mini |
| **Claude (Anthropic)** | Claude Sonnet 4.6, Claude Haiku 4.5, Claude Opus 4.6 |
| **Gemini (Google)** | Gemini 2.5 Flash, Gemini 2.5 Pro, Gemini 3.1 Pro (Preview), Gemma 4 31B, Gemma 4 26B MoE |
| **Grok (xAI)** | Grok 4.20, Grok 4.1 Fast, Grok 4.20 (Reasoning) |
| **Mistral AI** | Mistral Large, Mistral Small, Mistral Nemo |
| **[OpenRouter](#openrouter)** | Access 200+ models from all major providers with a single API key |
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

## OpenRouter

[OpenRouter](https://openrouter.ai) is an API gateway that gives you access to 200+ models from OpenAI, Anthropic, Google, Mistral, Meta, and many others – all through a single API key. Instead of managing separate keys for each provider, you sign up once at OpenRouter and use one key for everything.

### Getting started

1. Create a free account at [openrouter.ai](https://openrouter.ai)
2. Go to **Keys** and create an API key
3. In Supervertaler, select **OpenRouter** as the provider and paste your key

### Curated model list

The model dropdown includes a curated selection of the best models for translation:

| Model | Description |
|-------|-------------|
| **Claude Sonnet 4.6** | Recommended – best balance of speed, quality, and cost |
| **Claude Opus 4.6** | Highest quality – best for specialized legal/technical translation |
| **GPT-5.4** | Premium quality – OpenAI's most advanced model |
| **GPT-5.4 Mini** | Fast, affordable, and high quality for everyday translation |
| **Gemini 3.1 Pro** | Google's most advanced model, large context |
| **Gemini 3 Flash** | Fast and affordable – great for large batch jobs |
| **Gemma 4 31B** | Open-source – strong multilingual quality, 256K context |
| **Gemma 4 26B MoE** | Open-source – near-31B quality at a fraction of the cost |
| **Mistral Small 4** | Very fast and cheap – good multilingual support |
| **Qwen 3.6 Plus (Free)** | Free – no API costs, good general-purpose quality |

### Using any OpenRouter model

The model dropdown is **editable** when OpenRouter is selected. You can pick from the curated list or type any OpenRouter model ID directly – for example, `meta-llama/llama-3.1-70b-instruct` or `deepseek/deepseek-r1`. Browse all available models at [openrouter.ai/models](https://openrouter.ai/models).

### Pricing

OpenRouter adds a **5.5% platform fee** on top of the underlying provider's token price. For example, if Claude Sonnet 4.6 costs $3/$15 per million tokens at Anthropic, it costs approximately $3.17/$15.83 through OpenRouter. For a typical 5,000-word translation costing $0.50, the OpenRouter fee adds less than 3 cents.

{% hint style="info" %}
OpenRouter also offers some **free models** (marked with "Free" in the dropdown). These have no API cost at all – they are rate-limited but perfectly usable for testing or light workloads.
{% endhint %}

## AI context options

These options control what additional context is included in AI prompts. The settings are split into two groups depending on which features they apply to.

### Which settings apply where

| Setting | Chat & QuickLauncher | Batch Operations |
|---------|:--------------------:|:----------------:|
| Termbases in AI prompts | Yes | Yes |
| Include full document content | Yes | Yes |
| Max segments | Yes | Yes |
| Include term definitions and domains | Yes | Yes |
| Log prompts to Reports | Yes | Yes |
| Include TM matches | Yes | No |
| Surrounding segments | Yes | No |

### AI context (Batch operations, Chat and QuickLauncher)

These settings apply to **all** AI features – Chat, QuickLauncher, Batch Translate, and Batch Proofread.

#### Include full document content

When enabled, all source segments in the current document are sent to the AI so it can determine the document type (legal, medical, technical, marketing, etc.) and provide context-appropriate assistance. This uses more tokens but greatly improves response quality – the AI can tailor its terminology and style to the specific type of document you are translating.

For very large documents, the content is automatically truncated to the configured maximum. The truncation preserves the beginning and end of the document (first 80% + last 20%).

For Batch Operations, the document content is included once in the system prompt (shared across all batches), so the AI knows what kind of document it is translating even when processing individual batches of segments.

#### Max segments

The maximum number of source segments to include in the AI prompt when document content is enabled. Default: **500**. Range: 100–2000.

Increase this for very large documents where you want the AI to see more content. Decrease it if you want to reduce token usage.

{% hint style="info" %}
This setting is only available when **Include full document content** is enabled.
{% endhint %}

#### Include term definitions and domains

When enabled, term definitions, domains, and usage notes from your termbases are included alongside matched terminology in the AI prompt. This gives the AI deeper understanding of your terminology – for example, knowing that a term belongs to the legal domain or has a specific definition helps the AI use it correctly in both chat responses and batch translations.

#### Include termbases in AI prompt

Select which termbases are included in AI prompts. Terminology matches from enabled termbases are injected into the prompt to help the AI use the correct, approved terminology.

For [AutoPrompt](../generate-prompt.md), **TermScan** automatically filters the termbase to only terms that appear in the document's source text, keeping the prompt focused and within token limits.

{% hint style="warning" %}
**Only enable termbases you trust.** The AI will follow your glossary entries even when they are wrong. If a termbase contains inaccurate, outdated, or low-quality translations, the AI will be forced to use them – producing worse results than if no termbase were enabled at all. Modern LLMs are remarkably good at choosing correct terminology on their own. When in doubt, disable termbases and add terms incrementally as you review the AI's output.
{% endhint %}

### AI context (Chat and QuickLauncher)

These settings apply only to the **Supervertaler Assistant** chat window and **QuickLauncher** prompts. They do **not** affect Batch Translate or Batch Proofread.

#### Include TM matches

When enabled, translation memory matches for the current segment are included in the prompt. This gives the AI context from previous translations, improving consistency. This setting also controls whether TM reference pairs are included when using [AutoPrompt](../generate-prompt.md).

{% hint style="info" %}
TM matches are per-segment and require a Trados TM lookup for each segment. Batch Operations skip this to keep processing fast.
{% endhint %}

#### Surrounding segments

The number of segments before and after the active segment to include as context. Default: **5** (five segments on each side). Range: 1–20.

This provides the AI with local context around the segment you are working on. It is also used for the `{{SURROUNDING_SEGMENTS}}` variable in [QuickLauncher prompts](prompts/prompt-variables.md).

{% hint style="info" %}
Batch Operations do not use this setting because each batch already contains a group of segments that provide context for each other.
{% endhint %}

{% hint style="success" %}
**Tip:** For the best results, enable all context options. The more information the AI has about your project, document, terminology, and previous translations, the more accurate and consistent its suggestions will be.
{% endhint %}

### SuperMemory context

These two toggles control whether [SuperMemory](../ai-assistant/memory-banks.md) knowledge base articles are included in the AI context.

#### Include memory bank in AI context

When enabled, the AI loads client profiles, domain knowledge, style guides, and terminology reasoning from the active memory bank before every translation and chat message. This gives the AI the _reasoning_ behind your terminology decisions, not just the terms themselves.

{% hint style="warning" %}
**Off by default.** SuperMemory is a power-user feature that most translators should opt into deliberately. The simpler workflow – TermLens glossaries + the AI context options above – covers the majority of needs. Enable this toggle once you have a populated memory bank and want the AI to consult it.
{% endhint %}

#### Use memory bank when generating prompts (AutoPrompt)

When enabled, SuperMemory articles are included in the [AutoPrompt](../generate-prompt.md) meta-prompt so that generated translation prompts reflect your established client conventions, terminology reasoning, and style guides. Only effective when "Include memory bank in AI context" is also enabled.

## Prompt logging

### Log prompts and responses to Reports tab

When enabled, AI operations are logged to the **Reports** tab in the Supervertaler Assistant panel. Each log entry shows:

* The **feature and prompt name** (e.g. "QuickLauncher · Explain in Context")
* The **model used**, estimated **token counts**, **cost**, and **duration**
* Expandable sections for the **system prompt**, **messages**, and **response**

{% hint style="info" %}
**Batch Translate** operations appear as a single consolidated entry showing the combined token count, cost, and total duration for the entire operation – regardless of how many sub-batches were processed.
{% endhint %}

Click "Show system prompt...", "Show messages...", or "Show response..." to expand a section. Press **Escape** to collapse it. Use **Copy** to copy a single section, or **Copy all** to copy the full prompt details to your clipboard.

This is useful for:

* **Monitoring costs** – see exactly how many tokens each operation uses
* **Debugging prompts** – inspect the full text sent to the AI to understand its behaviour
* **Comparing models** – run the same prompt with different models and compare token usage

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
