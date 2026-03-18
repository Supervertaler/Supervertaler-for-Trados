# Prompts

{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

## Prompts

Prompts tell the AI how to behave. The Prompt Manager lets you browse built-in domain prompts, create your own, and mark prompts as QuickLauncher shortcuts.

### Accessing the Prompt Manager

Open the plugin **Settings** dialog and switch to the **Prompts** tab.

***

### Built-in prompts

The plugin ships with built-in prompts organised into two categories:

| Category            | Prompts                                                                                              | Used in              |
| ------------------- | ---------------------------------------------------------------------------------------------------- | -------------------- |
| **Translate**       | Medical, Legal, Patent, Financial, Technical, Marketing, IT, Professional Tone, Preserve Formatting | Batch Translate mode |
| **Proofread**       | Default Proofreading Prompt                                                                          | Batch Proofread mode |
| **QuickLauncher**   | Assess translation, Define, Explain (in general)                                                     | QuickLauncher menu   |

{% hint style="info" %}
Built-in prompts are **read-only**. To customise one, create a new prompt and paste the built-in content as a starting point.
{% endhint %}

***

### Prompt variables

Variables are placeholders in your prompt text that are automatically filled in at runtime. Use them to make prompts context-aware without rewriting them for every project or language pair.

#### Language variables — all contexts

These work in Batch Translate, Batch Proofread, and QuickLauncher prompts:

| Variable              | Replaced with                                      | Example                   |
| --------------------- | -------------------------------------------------- | ------------------------- |
| `{{SOURCE_LANGUAGE}}` | Full name of the source language, including locale | `Dutch (Belgium)`         |
| `{{TARGET_LANGUAGE}}` | Full name of the target language, including locale | `English (United States)` |

#### Segment variables — QuickLauncher only

These are only available in QuickLauncher prompts, because they refer to the specific segment active at the moment you trigger the menu:

| Variable             | Replaced with                                                                                  | Example                                                      |
| -------------------- | ---------------------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| `{{SOURCE_SEGMENT}}` | Full source text of the **active segment**                                                     | `De uitvinding heeft betrekking op een nieuwe werkwijze...`  |
| `{{TARGET_SEGMENT}}` | Full target text of the **active segment** (your translation so far — may be empty or partial) | `The invention relates to a novel method...`                 |
| `{{SELECTION}}`      | Text currently **selected** in the editor (source side preferred; falls back to target side)   | `werkwijze`                                                  |

{% hint style="info" %}
**Segment vs selection:** `{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` always give you the **entire active segment**. `{{SELECTION}}` gives you only the **highlighted portion** — useful for looking up or explaining a specific word or phrase within the segment. If nothing is selected, `{{SELECTION}}` is replaced with an empty string.
{% endhint %}

#### Project variables — QuickLauncher only

| Variable                   | Replaced with                                                                                                                                                                                                   |
| -------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `{{PROJECT_NAME}}`         | Trados project name (e.g. `Patent_NL_EN_2026`)                                                                                                                                                                  |
| `{{DOCUMENT_NAME}}`        | Active file name (e.g. `source_document.docx`)                                                                                                                                                                  |
| `{{SURROUNDING_SEGMENTS}}` | N source segments before and after the active segment, with actual Trados segment numbers and the active segment marked `← ACTIVE`. N is set in **Settings → AI Settings → Surrounding segments** (default: 5). |
| `{{PROJECT}}`              | All source segments in the document, numbered with their actual Trados segment numbers. In multi-file projects a `=== File N ===` header separates each file (Trados restarts segment numbering per file).      |

{% hint style="warning" %}
`{{PROJECT}}` sends the entire document to the AI and uses significantly more tokens than other variables. For a 10,000-word document, this costs roughly 4–5 cents per call with a Sonnet-class model. Reserve it for prompts where full document context genuinely matters.

To keep the chat history readable, the chat bubble shows a compact summary (e.g. `[source document — 47 segments]`) instead of the full source text. The complete document is still sent to the AI.
{% endhint %}

***

### Writing a custom prompt

#### Anatomy of a prompt

A good prompt has three parts:

1. **Role** — tells the AI who it is
2. **Task** — tells the AI what to do
3. **Constraints** — tells the AI what to avoid or preserve

Here is an annotated example for a Batch Translate prompt:

```
You are an expert {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}} patent translator.  ← Role + language variables

Translate the source segment provided. Return only the translated text —         ← Task
no commentary, no explanations, no repetition of the source.

Preserve all tag placeholders exactly as they appear (e.g. <t1>, <t2/>).        ← Constraints
Preserve numbers, units, and chemical formulas without conversion.
Use formal, technical register throughout.
```

#### Example QuickLauncher prompt — explain a selected term

This prompt uses `{{SELECTION}}` to ask the AI to explain a selected term in context:

```
The user is translating a patent from {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}}.

The selected term is: {{SELECTION}}

Please explain what this term means in the context of patent translation,
suggest the standard {{TARGET_LANGUAGE}} equivalent, and note any regional
or register variations the translator should be aware of.
```

When the translator selects "werkwijze" in the source segment and triggers this prompt via QuickLauncher, the AI receives:

```
The user is translating a patent from Dutch (Belgium) to English (United States).

The selected term is: werkwijze

Please explain what this term means...
```

#### Example QuickLauncher prompt — assess the current translation

This prompt uses `{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` to ask the AI to review the translation of the active segment:

```
Source ({{SOURCE_LANGUAGE}}):
{{SOURCE_SEGMENT}}

My translation ({{TARGET_LANGUAGE}}):
{{TARGET_SEGMENT}}

Assess how I translated the current segment. Point out any inaccuracies,
awkward phrasing, or terminology issues, and suggest improvements.
```

#### Example QuickLauncher prompt — translate a selected term in context

```
Source segment ({{SOURCE_LANGUAGE}}):
{{SOURCE_SEGMENT}}

Current translation ({{TARGET_LANGUAGE}}):
{{TARGET_SEGMENT}}

The translator has selected this word or phrase: {{SELECTION}}

Suggest the best {{TARGET_LANGUAGE}} translation for "{{SELECTION}}"
given the full segment context above. Give a short explanation of your reasoning.
```

#### Example QuickLauncher prompt — translate a term using surrounding passage

Uses `{{SURROUNDING_SEGMENTS}}` for a wider context window than just the active segment:

```
I am translating a {{SOURCE_LANGUAGE}} patent into {{TARGET_LANGUAGE}}.

The selected term is: {{SELECTION}}

Here is the passage surrounding the active segment:

{{SURROUNDING_SEGMENTS}}

Suggest the best {{TARGET_LANGUAGE}} translation for "{{SELECTION}}" given the
surrounding context. Briefly explain your reasoning.
```

#### Example QuickLauncher prompt — full-document term consistency check

Uses `{{PROJECT}}` to give the AI the entire source document. Useful for checking whether a key term is used consistently, or for understanding a term's meaning across all its occurrences. Reserve this for important queries — see the token cost note above.

```
I am translating a {{SOURCE_LANGUAGE}} patent ({{DOCUMENT_NAME}}) into {{TARGET_LANGUAGE}}.
Project: {{PROJECT_NAME}}

Here is the complete source text:

{{PROJECT}}

The selected term is: {{SELECTION}}

What is the most accurate and consistent {{TARGET_LANGUAGE}} translation for
"{{SELECTION}}" throughout this document? Note any variation in meaning between
occurrences and recommend which translation to use where.
```

#### Example QuickLauncher prompt — check a segment against the full document

After sending `{{PROJECT}}`, the AI knows the segment numbers shown in Trados, so you can ask about specific segments by number in follow-up messages — or ask in the prompt itself:

```
I am translating a {{SOURCE_LANGUAGE}} patent into {{TARGET_LANGUAGE}}.

Here is the source document:

{{PROJECT}}

I am currently working on segment {{SOURCE_SEGMENT}} (shown as [{{SOURCE_SEGMENT}}]
above). My translation is:

{{TARGET_SEGMENT}}

Does this translation accurately reflect the source and maintain consistency with the
terminology used elsewhere in the document? Point out any issues.
```

#### Tips for effective prompts

* **Be explicit about output format.** If you only want the translation, say "Return only the translated text." If you want an explanation, describe the expected structure.
* **Use language variables.** Hardcoding "Dutch to English" breaks the prompt when you switch projects. Always use `{{SOURCE_LANGUAGE}}` and `{{TARGET_LANGUAGE}}`.
* **Keep QuickLauncher prompts focused.** A narrow, specific task works better than a broad one — except when you deliberately need the full document context via `{{PROJECT}}`.
* **Use `{{SURROUNDING_SEGMENTS}}` instead of `{{SOURCE_SEGMENT}}` when context matters.** The surrounding passage often gives the AI enough context for a better answer at a fraction of the cost of `{{PROJECT}}`.
* **Use `{{PROJECT}}` sparingly.** It is best suited for high-stakes queries on short-to-medium documents — terminology consistency checks, key term decisions, or reviewing a handful of specific segments. Avoid it in prompts you run on every segment.
* **Segment numbers in `{{PROJECT}}` match the Trados editor.** After sending `{{PROJECT}}`, you can ask the AI about "segment 4" or "segment 12" and it will know exactly which segment you mean — the same number shown in the Trados grid.
* **Batch Translate prompts receive one segment at a time.** You do not need to handle lists of segments or loop logic.
* **Proofread prompts receive multiple segment pairs.** The built-in proofreading prompt shows the expected input/output format — follow that structure if you write a custom one.

***

### Marking a prompt as a QuickLauncher shortcut

To make a custom prompt appear in the QuickLauncher right-click menu (`Ctrl+Q`), set `category: QuickLauncher` in the YAML frontmatter:

```yaml
---
name: Explain selected term
description: Explains the selected term in translation context
category: QuickLauncher
quicklauncher_label: Explain term
---

Your prompt content here...
```

| Field                     | Description                                                              |
| ------------------------- | ------------------------------------------------------------------------ |
| `category: QuickLauncher` | Marks this prompt as a QuickLauncher item                                |
| `quicklauncher_label`     | Optional short label shown in the menu — falls back to `name` if omitted |

You can also organise QuickLauncher prompts by placing them in a folder called `QuickLauncher` inside your `prompt_library` folder. Any prompt in that folder is automatically treated as a QuickLauncher prompt.

{% hint style="info" %}
QuickLauncher prompts are shared with Supervertaler Workbench via the shared prompt library folder.
{% endhint %}

***

### Prompt file format

Prompts are stored as `.svprompt` files (Markdown with YAML frontmatter). This is the same format used by Supervertaler Workbench, so prompts are automatically shared between both applications via the shared `prompt_library` folder.

```yaml
---
name: Medical Translation Specialist
description: Clinical and pharmaceutical content
category: Translate
built_in: true
---

You are a professional medical translator...
```

| YAML field            | Description                                                                      |
| --------------------- | -------------------------------------------------------------------------------- |
| `name`                | Display name shown in the prompt selector                                        |
| `description`         | Optional summary                                                                 |
| `category`            | `Translate`, `Proofread`, or `QuickLauncher` — controls where the prompt appears |
| `quicklauncher_label` | Short label for the QuickLauncher menu (optional, falls back to `name`)          |
| `built_in`            | `true` for shipped prompts (managed by the plugin)                               |

{% hint style="info" %}
Older prompts using the `domain` key instead of `category` are still supported for backward compatibility.
{% endhint %}

***

### Creating and editing prompts

#### New prompt

1. Click **New** in the Prompts tab
2. Fill in Name, Description, Category, and Content
3. Click **Save**

#### Edit a prompt

1. Select a prompt in the list
2. Click **Edit**
3. Modify as needed and click **Save**

#### Inserting variables

While editing prompt content, press **Ctrl+,** to open the variable picker menu. This lists all available variables with a short description. Select a variable to insert it at the cursor position. If text is selected in the editor, it is replaced by the inserted variable.

{% hint style="info" %}
**Ctrl+,** mirrors the variable insertion shortcut used in the Trados Studio editor.
{% endhint %}

#### Delete a prompt

1. Select a custom prompt
2. Click **Delete** and confirm

Built-in prompts cannot be deleted. Click **Restore** to recreate any built-in prompts you have deleted.

***

### See Also

* [QuickLauncher](../quicklauncher.md)
* [AI Settings](ai-settings.md)
* [Batch Translate](../batch-translate.md)
* [AI Proofreader](../ai-proofreader.md)
* [Keyboard Shortcuts](../keyboard-shortcuts.md)
