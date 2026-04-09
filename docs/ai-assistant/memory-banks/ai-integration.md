---
description: How memory banks are loaded into the AI context – the algorithm, ranking, and token budget
---

# AI Integration

This page is the technical deep dive into **how** the Supervertaler Assistant loads a memory bank into its AI context. For the broader picture of all context sources – including memory banks alongside termbases, TM matches, and document content – start with [Context Awareness](../context-awareness.md). For what a memory bank *is* and how to create one, start with [Memory banks](../memory-banks.md).

When memory bank context is enabled in [AI Settings](../../settings/ai-settings.md), every AI call – chat messages, batch translations, single-segment translations, AutoPrompt runs – triggers a fresh load of the active bank before the prompt is sent. The load is deterministic, fast, and scoped to the current project and document.

## What the AI loads

Before every AI call, Supervertaler reads the active memory bank and loads the most relevant articles from it:

1. **Client profile.** The assistant matches your Trados project name against client profile filenames in `01_CLIENTS/`. If your project is called "Acme Legal Contract 2026", it finds the Acme Corporation profile and loads the client's language preferences, terminology decisions, style rules, and project history. The match is a case-insensitive substring search against the filename and the top-level heading of each article.
2. **Domain article.** The assistant analyses your document to detect the domain (legal, medical, technical, marketing, financial, scientific) and loads the matching article from `03_DOMAINS/` with conventions, common pitfalls, and reference material for that field.
3. **Style guide.** The assistant loads the most relevant style guide from `04_STYLE/`, preferring client-specific guides (e.g. `acme-style.md`) over general ones (e.g. `general-en-gb.md`).
4. **Terminology articles.** The assistant loads term articles from `02_TERMINOLOGY/` that match your client, domain, or language pair. These include not just the approved translations, but also rejected alternatives and the reasoning behind each decision – the kind of context a flat termbase entry does not carry.

Only articles from the **active** memory bank are loaded. If you keep separate banks per client or domain, switch to the relevant one from the Memory Bank dropdown in the toolbar before translating. See [Memory banks → Creating and switching banks](../memory-banks.md#creating-and-switching-banks) for the switching workflow.

Workflow folders – `00_INBOX`, `05_INDICES`, and `06_TEMPLATES` – are **not** loaded into the AI context. `00_INBOX` is a processing queue, `05_INDICES` holds auto-generated maps, and `06_TEMPLATES` holds templates for new articles.

## Token budget and prioritisation

To avoid overloading the AI's context window, memory bank context is allocated a token budget of approximately **4000 tokens** per AI call. This is a soft ceiling – smaller banks will simply fit; larger ones are trimmed.

When the active bank contains more relevant content than fits in the budget, articles are prioritised in this order:

1. **Client profile** – highest priority, loaded first. The client profile is often the single most valuable article in the bank because it sets the stage for everything else.
2. **Domain knowledge** – loaded second, with the strongest match for the detected document type.
3. **Style guide** – loaded third, preferring client-specific over general.
4. **Terminology articles** – loaded last, filling whatever budget remains. Articles matching the client take priority over generic ones.

If even the client profile exceeds the budget, Supervertaler logs a warning to the chat history and loads a truncated version rather than silently dropping it.

## How memory banks compare with other context sources

A memory bank does not replace your termbases, translation memories, or document context – it **complements** them, adding a layer of reasoning that flat data sources cannot provide.

| Context source | What it provides | What the memory bank adds |
|---|---|---|
| **Termbases** (Supervertaler + MultiTerm) | Flat term pairs: term A = term B | The *why*: reasoning, rejected alternatives, client-specific overrides |
| **Translation memories** | Previous translations for style anchoring | Domain conventions and style rules that transcend any single segment |
| **Document content** | Document type detection | Domain-specific pitfalls and formatting conventions the AI would not otherwise know |
| **AutoPrompt** | AI-generated translation instructions | Client and domain context for more accurate prompt generation |

All four work together. Termbases give the AI the terms; the memory bank tells it *why* those terms were chosen and what to watch out for. A TM gives it previous translations to anchor against; the memory bank tells it which previous translations are from a client with strict style rules and which are from one-off work that can be safely overridden.

For a discussion of when stacking all four sources may or may not be optimal, see the **Composing the context** section of [Context Awareness](../context-awareness.md#composing-the-context).

## Memory-aware chat

The chat panel is memory-aware by default. When you ask the assistant a question about the current segment, it has access to the active memory bank alongside the document context, terminology, and TM matches – so you can ask things like:

- "What register should I use for this client?"
- "Does this client prefer *whilst* or *while*?"
- "Has this term come up before in this client's projects?"
- "What's the usual translation for *furtherance* in this domain, and why?"

…and the answer comes from your actual KB articles, not from generic training data. If the memory bank does not contain the relevant article, the assistant falls back to its general knowledge and says so.

## Enabling and disabling

Memory bank context can be toggled on or off in [AI Settings](../../settings/ai-settings.md):

- **Include memory bank in AI context** – enables KB context for translations and chat.
- **Use memory bank when generating prompts (AutoPrompt)** – enables KB context when AutoPrompt drafts a new translation prompt.

Both are enabled by default. Disabling them does not delete your memory banks – the content stays on disk and can be re-enabled at any time.

## See Also

- [Context Awareness](../context-awareness.md) – The full menu of context sources, including memory banks as one section among several
- [Memory banks](../memory-banks.md) – What a memory bank is, how to create one, and the folder layout
- [AI Settings](../../settings/ai-settings.md) – Toggles for memory bank context
- [Supervertaler Assistant](../../ai-assistant.md) – Overview of the chat panel
- [Batch Translate](../../batch-translate.md) – Batch translation with full context
