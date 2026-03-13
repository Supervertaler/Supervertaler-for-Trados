# Help Link Reference

All context-sensitive help links in the plugin, mapped to their online documentation pages.

Help links are defined in [`HelpSystem.cs`](src/Supervertaler.Trados/Core/HelpSystem.cs) and opened via `HelpSystem.OpenHelp(topic)`. The base URL is `https://supervertaler.gitbook.io/trados`.

Last audited: 2026-03-13

---

## HelpSystem topics

These are the topic constants defined in `HelpSystem.Topics` and the documentation pages they link to.

| Topic constant | Online URL | Used by |
|---|---|---|
| `Overview` | [/trados](https://supervertaler.gitbook.io/trados) | — (fallback) |
| `Installation` | [/trados/getting-started/installation](https://supervertaler.gitbook.io/trados/getting-started/installation) | — (not yet used in UI) |
| `GettingStarted` | [/trados/getting-started/getting-started](https://supervertaler.gitbook.io/trados/getting-started/getting-started) | — (not yet used in UI) |
| `TermLensPanel` | [/trados/features/termlens](https://supervertaler.gitbook.io/trados/features/termlens) | MainPanelControl (? button, F1 key) |
| `AddTermDialog` | [/trados/features/termlens/adding-terms](https://supervertaler.gitbook.io/trados/features/termlens/adding-terms) | AddTermDialog, BulkAddNTDialog, TermEntryEditorDialog |
| `TermPickerDialog` | [/trados/features/termlens/term-picker](https://supervertaler.gitbook.io/trados/features/termlens/term-picker) | TermPickerDialog |
| `AiAssistantChat` | [/trados/features/ai-assistant](https://supervertaler.gitbook.io/trados/features/ai-assistant) | AiAssistantControl (? button when on Chat tab) |
| `BatchTranslate` | [/trados/features/batch-translate](https://supervertaler.gitbook.io/trados/features/batch-translate) | AiAssistantControl (? button when on Batch tab) |
| `MultiTermSupport` | [/trados/features/multiterm-support](https://supervertaler.gitbook.io/trados/features/multiterm-support) | MainPanelControl (MultiTerm help link) |
| `TermbaseEditor` | [/trados/terminology/termbase-management](https://supervertaler.gitbook.io/trados/terminology/termbase-management) | TermbaseEditorDialog, NewTermbaseDialog |
| `SettingsTermLens` | [/trados/settings/termlens](https://supervertaler.gitbook.io/trados/settings/termlens) | Settings dialog — TermLens tab (index 0) |
| `SettingsAi` | [/trados/settings/ai-settings](https://supervertaler.gitbook.io/trados/settings/ai-settings) | Settings dialog — AI Settings tab (index 1) |
| `SettingsPrompts` | [/trados/settings/prompts](https://supervertaler.gitbook.io/trados/settings/prompts) | Settings dialog — Prompts tab (index 2), PromptEditorDialog |
| `ExampleProject` | [/trados/getting-started/example-project](https://supervertaler.gitbook.io/trados/getting-started/example-project) | MainPanelControl (? menu), AiAssistantControl (? menu) |
| `Licensing` | [/trados/getting-started/licensing](https://supervertaler.gitbook.io/trados/getting-started/licensing) | Settings dialog — Licence tab (index 3) |
| `SettingsBackup` | [/trados/settings/backup](https://supervertaler.gitbook.io/trados/settings/backup) | Settings dialog — Backup tab (index 4) |
| `KeyboardShortcuts` | [/trados/reference/keyboard-shortcuts](https://supervertaler.gitbook.io/trados/reference/keyboard-shortcuts) | — (not yet used in UI) |
| `Troubleshooting` | [/trados/reference/troubleshooting](https://supervertaler.gitbook.io/trados/reference/troubleshooting) | — (not yet used in UI) |

## Other links in the plugin

These are hardcoded URLs outside `HelpSystem`, found in the About dialog and licence manager.

| Link | URL | Location |
|---|---|---|
| Documentation (home) | [supervertaler.gitbook.io/supervertaler](https://supervertaler.gitbook.io/supervertaler) | AboutDialog — "Documentation" link (`HelpSystem.OpenDocsHome()`) |
| Website | [supervertaler.com](https://supervertaler.com) | AboutDialog — "Website" link |
| Support & Community | [supervertaler.com/trados/#support](https://supervertaler.com/trados/#support) | AboutDialog — "Support & Community" link |
| Source code | [github.com/Supervertaler/Supervertaler-for-Trados](https://github.com/Supervertaler/Supervertaler-for-Trados) | AboutDialog — "Source Code" link |
| Changelog | [github.com/.../CHANGELOG.md](https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md) | Website trados page — nav bar + footer |
| Purchase page | supervertaler.com/trados/ | LicenseManager — shown in trial-expired / upgrade-required messages |

## Docs source structure

The documentation source files live in [`docs/`](docs/) and are synced to GitBook from the `main` branch. GitBook generates URLs from the **filename** (not the page title), prefixed by the `## Section` header in [`SUMMARY.md`](docs/SUMMARY.md).

For example: `licensing.md` under `## Getting Started` → `/trados/getting-started/licensing`

## Notes

- GitBook URL slugs come from the **markdown filename**, not the page title. If you rename a `.md` file, update the corresponding topic path in `HelpSystem.cs`.
- The Settings dialog help button (`?` in title bar) and F1 key both call `GetCurrentHelpTopic()` which maps the active tab index to a topic.
- Topics not yet referenced in the UI (`Installation`, `GettingStarted`, `KeyboardShortcuts`, `Troubleshooting`) are defined for future use.
