{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

This feature uses AI to analyse your entire project and generate a comprehensive, domain-specific translation prompt tailored to your document. The generated prompt includes terminology rules, style guidelines, anti-truncation controls, and domain-specific instructions — ready to use with Batch Translate.

<figure><img src=".gitbook/assets/image (7).png" alt=""><figcaption></figcaption></figure>

### How It Works

#### 1. Start the analysis

On the **Batch Operations** tab, click the **Analyse Project & Generate Prompt…** link next to the prompt dropdown.

#### 2. What gets analysed

Supervertaler gathers the following data from your project and sends it to your configured AI provider:

| Data                    | Purpose                                                      |
| ----------------------- | ------------------------------------------------------------ |
| **All source segments** | Domain detection, document content analysis, project context |
| **Termbase terms**      | Filtered to only document-relevant terms (TermScan), then included as a locked glossary in the generated prompt |
| **Translated segments** | Human-confirmed segments only (Translated, Approved, or Signed-off status) — used as TM reference pairs and style anchors. Unconfirmed AI-generated translations are excluded. |
| **Language pair**       | Embedded in the generated prompt                             |

{% hint style="info" %}
The full document is sent to the AI for analysis. For a typical 30,000-word document, this costs approximately $0.20–$0.25 with a Sonnet-class model, or $1.00–$1.15 with an Opus-class model.
{% endhint %}

#### 2b. TermScan — automatic term filtering

Before building the prompt, Supervertaler runs **TermScan**: it concatenates all source segments in the document and checks each termbase entry against this text. Only terms whose source term, source abbreviation, or source synonyms actually appear in the document are included in the generated prompt.

This dramatically reduces the glossary size — for example, a general patent termbase with 2,680 entries might yield only 123 relevant terms for a specific document. The status message in the AI Assistant confirms the filter: *"Termbase terms (filtered 123 relevant from 2,680 total)"*.

The filtering is case-insensitive and checks all variants of each term (source term, abbreviation forms, and synonyms). Terms that do not appear anywhere in the source text are excluded entirely.

{% hint style="warning" %}
**Termbase quality matters.** Only enable termbases in [AI Settings](settings/ai-settings.md) if you are confident they contain accurate, high-quality terminology for your project. A poorly maintained termbase with incorrect or outdated translations will constrain the AI and produce worse results. Modern LLMs — especially Opus-class and GPT-4-class models — are often better at choosing the right translation on their own than when forced to follow a low-quality glossary. When in doubt, disable termbases and let the AI translate freely, then add terms incrementally as you review.
{% endhint %}

#### 3. Domain detection

Before sending to the AI, Supervertaler runs a local keyword-based analysis to detect the document's domain. Supported domains:

* **Patent** — claims, embodiments, prior art, figure references
* **Legal** — contracts, clauses, statutory references
* **Medical** — clinical terms, dosages, ICD/ATC codes
* **Technical** — specifications, software terms, standards
* **Financial** — figures, IFRS/GAAP, regulatory language
* **Marketing** — brand, audience, campaign language
* **General** — fallback for mixed or unclassified content

The detected domain determines which template the AI uses to generate the prompt — including domain-specific roles, rules, and section structure.

#### 4. Review and refine in the AI Assistant

The generated prompt appears as a message in the **AI Assistant** chat. You can:

* **Read through the prompt** to verify it matches your project
* **Ask follow-up questions** to refine specific sections (e.g., "Make the glossary section more strict" or "Add a rule about chemical formula formatting")
* **Iterate** as many times as needed — each refinement builds on the conversation history

#### 5. Save the prompt

When you are satisfied with the generated prompt:

1. **Right-click** the assistant message containing the prompt
2. Select **Save as Prompt…**
3. Enter a name for your prompt (e.g., "DLCH Patent NL-EN")
4. Click **Save**

The prompt is saved to the **Translate** category in the Prompt Manager and immediately appears in the prompt dropdown on the Batch Operations tab.

### What the Generated Prompt Contains

A generated prompt follows the structure of professional translation prompts used by experienced translators. Depending on the domain, it typically includes:

* **Role** — domain-specific translator role with expertise areas
* **Translation mandate** — strict rules against simplification, paraphrasing, or "improving" the source
* **Anti-truncation controls** — explicit prohibition of omitting repetitive phrases or collapsing clauses
* **Input handling rules** — instructions for segment-by-segment translation in Supervertaler
* **Domain-specific style rules** — mandatory term mappings, register requirements, formatting rules
* **Terminology hierarchy** — priority order: TM matches > project glossary > domain conventions
* **Preflight self-check** — internal verification step before producing output
* **Post-translation integrity assertion** — completeness and faithfulness check
* **Project context** — AI-generated summary of what the document is about
* **Project-specific glossary** — all termbase terms, marked as locked and mandatory
* **TM reference translations** — validated translation pairs as style anchors
* **Output format** — translation only, no commentary, preserve formatting

### Tips

#### Start with a confirmed translated sample

The generator includes **confirmed** segments (Translated, Approved, or Signed-off status) as reference pairs — up to 50, sampled evenly across the document. This gives the AI concrete examples of your preferred style and terminology, resulting in a more accurate prompt. Unconfirmed segments (e.g. from a previous AI batch translation that you haven't reviewed yet) are excluded to avoid feeding unverified output back as "correct" references.

**Tip:** Before generating a prompt, confirm a handful of segments you are happy with. Even 10–20 confirmed segments give the AI meaningful style anchors to work from.

#### Review the glossary section

The generated prompt includes only the document-relevant terms from your enabled termbases (filtered by TermScan). Check that the glossary accurately reflects your terminology preferences. You can ask the AI to reorganise terms by category or add missing mappings.

{% hint style="warning" %}
If your termbase contains incorrect or low-quality entries, these will be injected into the prompt and the AI will be forced to follow them. Only enable termbases that you trust. When starting a new project with no established terminology, consider disabling termbases entirely and letting the AI translate freely — then add terms as you review.
{% endhint %}

#### Use with Batch Translate

After saving the generated prompt, select it from the prompt dropdown on the Batch Operations tab. It works with all scopes and providers, just like any other prompt.

#### Regenerate when the project changes

If your project evolves significantly (new terminology, different document sections, additional termbases), run the analysis again to generate an updated prompt.

***

### See Also

* [Batch Translate](batch-translate.md)
* [Prompts](settings/prompts.md)
* [Supervertaler Assistant](ai-assistant.md)
* [AI Settings](settings/ai-settings.md)
