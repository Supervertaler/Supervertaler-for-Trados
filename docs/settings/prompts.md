{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

# Prompts

Prompts tell the AI how to behave. The Prompt Manager lets you browse built-in domain prompts, create your own, and mark prompts as QuickLauncher shortcuts.

## Accessing the Prompt Manager

Open the plugin **Settings** dialog and switch to the **Prompts** tab.

---

## Built-in prompts

The plugin ships with built-in prompts organised into two categories:

| Category | Prompts | Used in |
|----------|---------|---------|
| **Translate** | Medical, Legal, Patent, Financial, Technical, Marketing, IT, Dutch/English/French/German/Spanish Style Guides, Professional Tone, Preserve Formatting | Batch Translate mode |
| **Proofread** | Default Proofreading Prompt | Batch Proofread mode |

{% hint style="info" %}
Built-in prompts are **read-only**. To customise one, create a new prompt and paste the built-in content as a starting point.
{% endhint %}

---

## Prompt variables

Variables are placeholders in your prompt text that are automatically filled in at runtime. Use them to make prompts context-aware without rewriting them for every project or language pair.

### Language variables — all contexts

These work in Batch Translate, Batch Proofread, and QuickLauncher prompts:

| Variable | Replaced with | Example |
|----------|---------------|---------|
| `{{SOURCE_LANGUAGE}}` | Full name of the source language, including locale | `Dutch (Belgium)` |
| `{{TARGET_LANGUAGE}}` | Full name of the target language, including locale | `English (United States)` |

### Segment variables — QuickLauncher only

These are only available in QuickLauncher prompts, because they refer to the specific segment active at the moment you trigger the menu:

| Variable | Replaced with | Example |
|----------|---------------|---------|
| `{{SOURCE_SEGMENT}}` | Full source text of the **active segment** | `De stand der techniek op het gebied van vloerlijmen...` |
| `{{TARGET_SEGMENT}}` | Full target text of the **active segment** (your translation so far — may be empty or partial) | `The prior art in the field of floor adhesives...` |
| `{{SELECTION}}` | Text currently **selected** in the editor (source side preferred; falls back to target side) | `vloerbekledingen` |

{% hint style="info" %}
**Segment vs selection:** `{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` always give you the **entire active segment**. `{{SELECTION}}` gives you only the **highlighted portion** — useful for looking up or explaining a specific word or phrase within the segment. If nothing is selected, `{{SELECTION}}` is replaced with an empty string.
{% endhint %}

---

## Writing a custom prompt

### Anatomy of a prompt

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

### Example QuickLauncher prompt — explain a selected term

This prompt uses `{{SELECTION}}` to ask the AI to explain a selected term in context:

```
The user is translating a patent from {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}}.

The selected term is: {{SELECTION}}

Please explain what this term means in the context of patent translation,
suggest the standard {{TARGET_LANGUAGE}} equivalent, and note any regional
or register variations the translator should be aware of.
```

When the translator selects "vloerbekledingen" in the source segment and triggers this prompt via QuickLauncher, the AI receives:

```
The user is translating a patent from Dutch (Belgium) to English (United States).

The selected term is: vloerbekledingen

Please explain what this term means...
```

### Example QuickLauncher prompt — assess the current translation

This prompt uses `{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` to ask the AI to review the translation of the active segment:

```
Source ({{SOURCE_LANGUAGE}}):
{{SOURCE_SEGMENT}}

My translation ({{TARGET_LANGUAGE}}):
{{TARGET_SEGMENT}}

Assess how I translated the current segment. Point out any inaccuracies,
awkward phrasing, or terminology issues, and suggest improvements.
```

### Example QuickLauncher prompt — translate a selected term in context

```
Source segment ({{SOURCE_LANGUAGE}}):
{{SOURCE_SEGMENT}}

Current translation ({{TARGET_LANGUAGE}}):
{{TARGET_SEGMENT}}

The translator has selected this word or phrase: {{SELECTION}}

Suggest the best {{TARGET_LANGUAGE}} translation for "{{SELECTION}}"
given the full segment context above. Give a short explanation of your reasoning.
```

### Tips for effective prompts

- **Be explicit about output format.** If you only want the translation, say "Return only the translated text." If you want an explanation, describe the expected structure.
- **Use language variables.** Hardcoding "Dutch to English" breaks the prompt when you switch projects. Always use `{{SOURCE_LANGUAGE}}` and `{{TARGET_LANGUAGE}}`.
- **Keep QuickLauncher prompts focused.** They run on a single selection or segment — a narrow, specific task works better than a broad one.
- **Batch Translate prompts receive one segment at a time.** You do not need to handle lists of segments or loop logic.
- **Proofread prompts receive multiple segment pairs.** The built-in proofreading prompt shows the expected input/output format — follow that structure if you write a custom one.

---

## Marking a prompt as a QuickLauncher shortcut

To make a custom prompt appear in the QuickLauncher right-click menu (`Ctrl+Q`), set `category: QuickLauncher` in the YAML frontmatter:

```yaml
---
name: Explain selected term
description: Explains the selected term in translation context
category: QuickLauncher
quickmenu_label: Explain term
---

Your prompt content here...
```

| Field | Description |
|-------|-------------|
| `category: QuickLauncher` | Marks this prompt as a QuickLauncher item |
| `quickmenu_label` | Optional short label shown in the menu — falls back to `name` if omitted |

You can also organise QuickLauncher prompts by placing them in a folder called `QuickLauncher` inside your `prompt_library` folder. Any prompt in that folder is automatically treated as a QuickLauncher prompt.

{% hint style="info" %}
QuickLauncher prompts are shared with Supervertaler Workbench via the shared prompt library folder.
{% endhint %}

---

## Prompt file format

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

| YAML field | Description |
|------------|-------------|
| `name` | Display name shown in the prompt selector |
| `description` | Optional summary |
| `category` | `Translate`, `Proofread`, or `QuickLauncher` — controls where the prompt appears |
| `quickmenu_label` | Short label for the QuickLauncher menu (optional, falls back to `name`) |
| `built_in` | `true` for shipped prompts (managed by the plugin) |

{% hint style="info" %}
Older prompts using the `domain` key instead of `category` are still supported for backward compatibility.
{% endhint %}

---

## Creating and editing prompts

### New prompt

1. Click **New** in the Prompts tab
2. Fill in Name, Description, Category, and Content
3. Click **Save**

### Edit a prompt

1. Select a prompt in the list
2. Click **Edit**
3. Modify as needed and click **Save**

### Delete a prompt

1. Select a custom prompt
2. Click **Delete** and confirm

Built-in prompts cannot be deleted. Click **Restore** to recreate any built-in prompts you have deleted.

---

## See Also

- [QuickLauncher](../quicklauncher.md)
- [AI Settings](ai-settings.md)
- [Batch Translate](../batch-translate.md)
- [AI Proofreader](../ai-proofreader.md)
- [Keyboard Shortcuts](../keyboard-shortcuts.md)
