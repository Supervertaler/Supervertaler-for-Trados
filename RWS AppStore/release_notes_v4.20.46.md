# RWS App Store Manager – v4.20.46.0

**Version number:** `4.20.46.0`
**Minimum studio version:** `18.0`
**Maximum studio version:** `18.9`
**Checksum:** `99263f26b71f71e4fb4b424264acbfec64d46f04453b54776e4ed675f9fa7642`

---

## Changelog

### Changed
- **The AI pane is called "Supervertaler Assistant" again.** The dockable pane that hosts Chat, Batch Operations, Import/Export and Reports had been shortened to bare "Supervertaler", which collided with the product name (Supervertaler for Trados) and read awkwardly in the documentation ("The Supervertaler supports …"). It is restored to "Supervertaler Assistant" – both the pane/dock-tab caption and the View menu entry – so the pane is clearly distinct from the product and the help docs match. No functional change.

### Fixed
- **GPT-5.5 now works – Test Connection and translation no longer fail with an "unsupported temperature" error.** OpenAI's GPT-5.5 only accepts the default temperature value and returns *"Unsupported value: 'temperature' does not support 0.3 with this model. Only the default (1) value is supported."* if any explicit temperature is sent. The plugin sent a fixed `temperature: 0.3` on every OpenAI request for non-reasoning models, so GPT-5.5 failed both the Test Connection check and any actual translation, while GPT-5.4 Mini (which does accept a custom temperature) worked. Models now carry a `SupportsTemperature` flag; GPT-5.5 is marked as temperature-locked, so the parameter is omitted for it across all request paths (Test Connection, single-segment, batch translate, and tool-use), and a heuristic covers any future full GPT-5.x model entered in the custom Model ID field. Reported in issue #36.

For the full changelog, see: https://github.com/Supervertaler/Supervertaler-for-Trados/releases