{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

# QuickLauncher

QuickLauncher gives you one-click access to your most-used AI prompts directly from the Trados editor, without switching panels or typing anything.

## How it works

1. **Right-click** anywhere in the editor (or press `Ctrl+Q`)
2. Click **QuickLauncher** in the context menu
3. Select a prompt from the list
4. The prompt is filled in with the current segment context and sent to the Supervertaler Assistant

The AI response appears in the **Supervertaler Assistant** chat panel.

## Keyboard shortcut

| Shortcut (Windows) | Shortcut (Mac) | Action |
|---------------------|----------------|--------|
| `Ctrl+Q` | `Control+Q` | Open QuickLauncher prompt menu |

{% hint style="warning" %}
Trados Studio assigns `Ctrl+Q` to **View Internally Source** by default. To use `Ctrl+Q` for QuickLauncher, go to **File → Options → Keyboard Shortcuts**, search for **View Internally Source**, and remove or reassign its shortcut.
{% endhint %}

## Prompt variables

QuickLauncher prompts have access to the full segment context at the moment you trigger them:

| Variable | Replaced with | Example |
|----------|---------------|---------|
| `{{SOURCE_LANGUAGE}}` | Source language name, including locale | `Dutch (Belgium)` |
| `{{TARGET_LANGUAGE}}` | Target language name, including locale | `English (United States)` |
| `{{SOURCE_SEGMENT}}` | Full text of the **active source segment** | `De stand der techniek...` |
| `{{TARGET_SEGMENT}}` | Full text of the **active target segment** (your translation so far) | `The prior art...` |
| `{{SELECTION}}` | Text currently **selected** in the editor | `vloerbekledingen` |

{% hint style="info" %}
**Segment vs selection:** `{{SOURCE_SEGMENT}}` and `{{TARGET_SEGMENT}}` always give the **entire active segment**. `{{SELECTION}}` gives only the **highlighted portion** — useful for term lookups or focused questions. If nothing is selected, `{{SELECTION}}` is an empty string.
{% endhint %}

### Example: explain a selected term

Select a word in the source segment, press `Ctrl+Q`, and choose a prompt like this:

```
The user is translating from {{SOURCE_LANGUAGE}} to {{TARGET_LANGUAGE}}.
The selected term is: {{SELECTION}}

Explain what this term means and suggest the best {{TARGET_LANGUAGE}} equivalent,
considering the full segment context below:

{{SOURCE_SEGMENT}}
```

### Example: assess the current translation

```
Source ({{SOURCE_LANGUAGE}}):
{{SOURCE_SEGMENT}}

My translation ({{TARGET_LANGUAGE}}):
{{TARGET_SEGMENT}}

Assess how I translated the current segment. Point out any inaccuracies,
awkward phrasing, or terminology issues, and suggest improvements.
```

The plugin fills in all variables and sends the expanded prompt straight to the AI.

## Setting up QuickLauncher prompts

Any `.svprompt` file can be made into a QuickLauncher prompt by adding `sv_quicklauncher: true` to its YAML frontmatter, or by placing it in a folder called `QuickLauncher` inside your `prompt_library` folder.

Set `category: QuickLauncher` in the YAML frontmatter, or place the file in a folder called `QuickLauncher` inside your `prompt_library`. See [Prompts → Marking a prompt as a QuickLauncher shortcut](settings/prompts.md#marking-a-prompt-as-a-quicklauncher-shortcut) for full details.

## Shared with Supervertaler Workbench

QuickLauncher prompts live in the shared `prompt_library` folder used by both Supervertaler for Trados and Supervertaler Workbench. Any prompt you create in one application is immediately available in the other.

---

## See Also

- [Prompts](settings/prompts.md)
- [Supervertaler Assistant](ai-assistant.md)
- [Keyboard Shortcuts](keyboard-shortcuts.md)
