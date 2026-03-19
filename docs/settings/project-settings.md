{% hint style="info" %}
You are viewing help for **Supervertaler for Trados** — the Trados Studio plugin. Looking for help with the standalone app? Visit [Supervertaler Workbench help](https://help.supervertaler.com).
{% endhint %}

Supervertaler for Trados automatically saves and restores your termbase configuration when you switch between Trados projects. This means each project can use its own Supervertaler database, write targets, and termbase settings without manual reconfiguration.

## How it works

When you open a different Trados project (or switch to a document from another project), the plugin:

1. **Saves** the current project's settings to a project-specific file
2. **Loads** the new project's settings (if they exist)
3. **Reloads** the termbase with the new configuration

If no project-specific settings exist yet (first time opening a project), the current global settings are used. Once you make any changes and click OK in Settings, those settings are saved for that project.

{% hint style="info" %}
**No action needed:** Per-project settings work automatically in the background. Just configure your termbases as usual — the plugin remembers your choices per project.
{% endhint %}

## What's saved per project

| Setting | Saved per project? | Notes |
|---------|-------------------|-------|
| Supervertaler database path | Yes | Each project can use a different `.db` file |
| Enabled/disabled termbases (Read toggle) | Yes | Different termbases active per project |
| Write targets | Yes | Different write targets per project |
| Project termbase (pink highlighting) | Yes | Different project termbase per project |
| MultiTerm visibility | Yes | Different MultiTerm termbases enabled per project |
| AI context termbase filters | Yes | Different termbases in AI prompts per project |
| API keys and provider settings | No | Shared across all projects |
| Panel font size | No | UI preference, shared |
| Term shortcut style | No | UI preference, shared |
| Dialog sizes | No | UI layout, shared |

## Storage location

Per-project settings are stored as individual JSON files in:

```
%LocalAppData%\Supervertaler.Trados\projects\
```

Each file is named with a hash derived from the Trados project file path (e.g., `a1b2c3d4e5f6.json`). The JSON file also contains the original project path and name for reference.

{% hint style="warning" %}
**Moving a Trados project** to a different folder creates a new project key. The plugin will treat it as a new project and use global defaults until you reconfigure. The old project settings file remains in the `projects` folder and can be safely deleted.
{% endhint %}

## Interaction with global settings

Global settings (`settings.json`) serve as the defaults for projects that don't have their own settings file yet. When you open a project for the first time, the global termbase configuration is used. Once you change settings and click OK, those settings are saved for that specific project.

Settings that are always global (API keys, font size, shortcut preferences) are never overridden by project settings.

---

## See Also

- [TermLens Settings](termlens.md)
- [AI Settings](ai-settings.md)
- [Backup & Restore](backup.md)
