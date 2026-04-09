# Context Awareness

{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** – the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

The Supervertaler Assistant is deeply integrated with your Trados project. Every time you send a chat message, translate a batch of segments, or ask AutoPrompt to draft a prompt, the assistant assembles a fresh snapshot of your current work and hands it to the AI. This snapshot is the **context** – everything the AI sees before it produces a reply.

This page is the single place that lists every context source. Each section is a brief overview with a link to the feature's canonical page for more depth. If you have ever wondered *"what exactly does the AI know when I ask it something?"*, the answer is this page.

## The context sources

Supervertaler draws context from eight sources. They are all independent, they can all be toggled individually, and most of them are on by default.

### 1. Project and file information

The assistant knows which project and file you are working in, the language pair (e.g. Dutch → English), and your current position in the document (e.g. "Segment 42 of 318"). This is always included – there are no user-facing toggles for it.

### 2. Full document content

When enabled, all source segments in the current document are included in the AI prompt. This lets the assistant analyse the document as a whole and determine its type – legal, medical, technical, marketing, financial, scientific – then use that assessment to inform its advice on terminology, style, and translation choices.

For very large documents, the content is automatically truncated to the configured maximum (default: 500 segments). The truncation preserves the first 80 % and the last 20 % of the document so the AI still sees both the beginning and the end.

**Toggle:** AI Settings → *Include full document content*.

### 3. Current segment

The source text you are translating and any target translation you have already entered. Always included – this is the minimum context needed for most AI operations.

### 4. Surrounding segments

Two segments before and two segments after your current position, with their translations where available. This gives the AI local context for cohesion and consistency – it can see how a pronoun was resolved in the previous sentence, or whether the current clause is continuing a thought from the segment above.

Always included – the window size is fixed.

### 5. Translation memory matches

TM fuzzy matches for the current segment are included, showing the match percentage, source text, and target text. This gives the AI reference material from your previous translations – it can see how you or your team rendered a similar phrase last time and stay consistent with that.

**Toggle:** AI Settings → *Include TM matches*.

### 6. Termbase terms

Matched terms from your active termbases are included with their approved translations and synonyms. Optionally, term definitions, domains, and usage notes are also included, giving the AI deeper understanding of your terminology requirements. Terms marked as non-translatable or forbidden are flagged so the AI can respect those constraints.

Both Supervertaler termbases and MultiTerm .sdltb termbases attached to the Trados project contribute. See [TermLens](../termlens.md) and [MultiTerm Support](../multiterm-support.md) for how those termbases are loaded.

**Toggles:** AI Settings → *Include termbase terms* / *Include term metadata* / per-termbase contribution list.

### 7. Memory bank context

If a [memory bank](memory-banks.md) is active, the assistant loads the most relevant articles from it before every AI call:

- the **client profile** matching the current Trados project name, from `01_CLIENTS/`
- the **domain article** matching the document type the AI just detected, from `03_DOMAINS/`
- the most relevant **style guide** from `04_STYLE/`, preferring client-specific guides over general ones
- matching **terminology articles** from `02_TERMINOLOGY/`, which include not just approved translations but also rejected alternatives and the reasoning behind each decision

Unlike a termbase – which gives the AI flat pairs of source and target terms – a memory bank gives it the **reasoning** behind those pairs: the decisions, the caveats, and the client-specific overrides. The two are complementary, not competitive.

Only articles from the **active** memory bank are loaded. If you keep separate banks per client or domain, switch to the relevant one from the Memory Bank dropdown in the toolbar before translating. See [Memory banks → AI Integration](memory-banks/ai-integration.md) for the full loading algorithm and token budget.

**Toggles:** AI Settings → *Include memory bank context* / *Use memory bank in AutoPrompt*.

### 8. Attached files

Files you attach to the chat panel – images (paste, drag-drop, or browse), and documents (DOCX, PDF, PPTX, XLSX, CSV, TMX, SDLXLIFF, TBX, TXT, Markdown, HTML, and more) – are added to the context for that turn. Images are sent through each provider's vision API; documents are text-extracted and appended as prompt context.

Attachments only apply to the turn you attached them on – they do not persist across messages. See [File Attachments](file-attachments.md) for details.

## Composing the context

All eight sources can be combined freely. For most projects, the default composition – project info, current segment, surrounding segments, document content, TM matches, termbase terms, and memory bank context all enabled – is a strong baseline and the one we recommend starting from.

That said, more context is not automatically better. The AI's context window is finite, and large projects with rich termbases and a mature memory bank can easily push the prompt into the 50 000–100 000-token range. At some point:

- adding **TM matches** on top of a memory bank that already knows the client's preferred wordings may introduce noise rather than signal;
- including **full document content** for a very long document may leave too little room for memory bank articles to load;
- layering **three overlapping sources** (TM + termbase + memory bank) on the same concept may produce contradictions the AI has to reconcile on the fly.

{% hint style="info" %}
**Composing tip:** for clients where you have a well-built memory bank, try running a small batch with TM matches disabled and compare the output to a run with everything enabled. The cleaner prompt often produces more consistent terminology, because the memory bank already knows which wording the client prefers and the TM matches are no longer adding anything the bank does not already say better. For unfamiliar domains or one-off projects, keep everything enabled – the TM and termbase are carrying the weight there.
{% endhint %}

{% hint style="warning" %}
We have not yet published composition presets (e.g. "Mature client – memory bank only", "Unfamiliar domain – TM + termbase"). Until we do, the sensible approach is to leave everything enabled by default and experiment on a per-project basis. If you find a configuration that works particularly well for your workflow, drop a note in the [community forum](https://translationtech.io).
{% endhint %}

## Controlling the context

{% hint style="info" %}
You can control exactly what context the assistant receives. In the settings dialogue on the **AI Settings** tab, you can toggle document content, TM matches, term metadata, memory bank context, and select which termbases contribute to the AI prompt.
{% endhint %}

{% hint style="success" %}
**Tip:** The document-type analysis is especially valuable – it helps the AI understand that "consideration" means something different in a legal contract than in a marketing brochure. Keep full document content and memory bank context enabled unless you have a specific reason to disable them.
{% endhint %}

## See Also

- [Supervertaler Assistant](../ai-assistant.md) – Overview
- [AI Settings](../settings/ai-settings.md) – Configure context options
- [Memory banks](memory-banks.md) – Self-organising translation knowledge bases
- [Memory banks → AI Integration](memory-banks/ai-integration.md) – The loading algorithm and token budget for memory bank context
- [File Attachments](file-attachments.md) – Add images and documents to a chat turn
- [TermLens](../termlens.md) – How termbase terms are matched and loaded
