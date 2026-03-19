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
| **Termbase terms**      | Included as a locked glossary in the generated prompt        |
| **Translated segments** | Used as TM reference pairs (style anchors)                   |
| **Language pair**       | Embedded in the generated prompt                             |

{% hint style="info" %}
The full document is sent to the AI for analysis. For a typical 30,000-word document, this costs approximately $0.20–$0.25 with a Sonnet-class model, or $1.00–$1.15 with an Opus-class model.
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

#### Start with a translated sample

If some segments are already translated (from TM or manual work), the generator includes them as reference pairs. This gives the AI concrete examples of your preferred style and terminology — resulting in a more accurate prompt.

#### Review the glossary section

The generated prompt includes all terms from your enabled termbases. Check that the glossary accurately reflects your terminology preferences. You can ask the AI to reorganise terms by category or add missing mappings.

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
