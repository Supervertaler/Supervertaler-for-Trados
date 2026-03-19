{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

This page walks you through the first-time setup so you can start using TermLens terminology and AI translation inside Trados Studio.

## First-Time Setup

### 1. Open Settings

Click the **gear icon** in the TermLens panel header to open the settings dialog.

### 2. Configure Termbases (TermLens tab)

On the **TermLens** tab:

1. Click **Browse** to select an existing Supervertaler termbase (`.db` file)
2. Or click **New** to create a new empty termbase

You can add multiple termbases. Designate one as the **Project termbase** to give its terms higher priority (shown in pink).

{% hint style="info" %}
Supervertaler for Trados uses the same `.db` termbase format as Supervertaler Workbench. Any termbase created in either tool works in both. On Windows, both tools can point to the same `.db` file in a shared data folder. On a Mac running Trados via Parallels, the two products use separate filesystems — see [Running on a Mac](installation.md#running-on-a-mac-parallels) for details.
{% endhint %}

### 3. Configure AI (AI Settings tab)

On the **AI Settings** tab:

1. Select a **provider** (OpenAI, Anthropic, Google, Ollama, or Custom)
2. Enter your **API key** for the selected provider
3. Choose a **model**

{% hint style="warning" %}
You need at least one API key to use AI features (Supervertaler Assistant, Batch Translate, and single-segment AI translation). TermLens terminology works without an API key.
{% endhint %}

### 4. Click OK

Settings are saved and applied immediately.

{% hint style="success" %}
**Want to try it with a real project?** Download the [Example Project](example-project.md) — a pre-configured patent translation with terminology, a translation memory, and a Supervertaler termbase all ready to go.
{% endhint %}

## Try It Out

### TermLens

1. Open a project in the Trados **Editor** view
2. Navigate to any segment – TermLens automatically displays term matches for the source text
3. Click a term translation to insert it into the target, or press **Alt+1** through **Alt+9**

### AI Translate

1. Place the cursor in a segment
2. Press **Ctrl+T** to translate the active segment with AI
3. The AI translation appears in the target cell

### Supervertaler Assistant

1. Open the **Supervertaler Assistant** panel (View > Supervertaler Assistant)
2. Switch to the **Chat** tab
3. Type a question about the current segment and press **Enter**
4. The assistant responds with context from your terminology and TM matches

## Quick Links

| Feature | Page |
|---------|------|
| TermLens terminology display | [TermLens](termlens.md) |
| AI chat interface | [Supervertaler Assistant](ai-assistant.md) |
| Bulk AI translation | [Batch Translate](batch-translate.md) |
| All shortcuts | [Keyboard Shortcuts](keyboard-shortcuts.md) |

---

## See Also

- [Installation](installation.md)
- [Example Project](example-project.md)
- [TermLens](termlens.md)
- [Supervertaler Assistant](ai-assistant.md)
