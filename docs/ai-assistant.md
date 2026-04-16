# Supervertaler Assistant

{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** -- the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

The Supervertaler Assistant is a conversational chat panel that runs inside Trados Studio as a separate dockable panel. It is context-aware: it automatically includes your current source and target text, matched terminology, and TM matches in every request, so the AI can give you informed answers about the segment you are working on.

<figure><img src=".gitbook/assets/Sv_Supervertaler-Assistant.png" alt=""><figcaption></figcaption></figure>

## Opening the Panel

The Supervertaler Assistant lives in its own dockable panel. To open it, go to **View > Supervertaler Assistant**.

You can dock the panel on the right side, bottom, or as a floating window. Trados remembers the panel position between sessions.

## Chat

Type a message in the input field at the bottom and press **Enter** to send. The AI will consider your current source text, target text, matched terminology from your termbases, and TM fuzzy matches when responding.

| Action                      | How                       |
| --------------------------- | ------------------------- |
| Send a message              | Press **Enter**           |
| Insert a line break         | Press **Shift+Enter**     |
| Stop a response in progress | Click the **Stop** button |

### What You Can Ask

Because the assistant has access to your current segment context, you can ask things like:

* "Translate this segment"
* "What is the difference between these two translations?"
* "Is this terminology correct in a legal context?"
* "Suggest a more formal alternative"
* "Explain this source text"

### Chat History

The conversation is saved automatically after every message and restored the next time Trados starts. Your history persists until you explicitly clear it.

To clear the history, click the **Clear** button in the chat toolbar.

{% hint style="info" %}
Chat history is stored in `~/Supervertaler/trados/chat_history.json`. It is a single global history -- not per project or per file.
{% endhint %}

### Right-Click Menu

Right-click any assistant response bubble to access:

| Action              | Description                                                                 |
| ------------------- | --------------------------------------------------------------------------- |
| **Copy**            | Copies the raw Markdown to the clipboard, preserving tables and formatting  |
| **Apply to target** | Inserts the plain text (Markdown stripped) into the active target segment    |
| **Save as Prompt...** | Saves the response as a reusable prompt template                          |
| **Save to memory bank** | Saves the question + response as an inbox note in the active memory bank so useful answers are not lost. Run [Process Inbox](ai-assistant/super-memory/process-inbox.md) afterwards to compile it into the knowledge base. |

If you select text within a bubble before right-clicking, **Copy** and **Apply to target** operate on the selection only.

## Features

| Feature | Description |
|---------|-------------|
| **[Context Awareness](ai-assistant/context-awareness.md)** | Automatic project, segment, terminology, TM, and document context in every request |
| **[File Attachments](ai-assistant/file-attachments.md)** | Attach images and documents (PDF, DOCX, XLSX, TMX, etc.) for additional context |
| **[Studio Tools](ai-assistant/studio-tools.md)** | Query your Trados Studio projects, TMs, termbases, and statistics using natural language |
| **[Incognito Mode](ai-assistant/incognito-mode.md)** | Anonymise project names, file paths, and personal data in AI responses for safe sharing |
| **[Providers and Models](ai-assistant/providers.md)** | Supports 7 AI providers including OpenAI, Claude, Gemini, Grok, Mistral, Ollama, and custom endpoints |

## See Also

* [QuickLauncher](quicklauncher.md) -- One-click prompt shortcuts
* [Batch Translate](batch-translate.md) -- Translate multiple segments at once
* [AI Settings](settings/ai-settings.md) -- API keys, model selection, context options
* [Keyboard Shortcuts](keyboard-shortcuts.md)
