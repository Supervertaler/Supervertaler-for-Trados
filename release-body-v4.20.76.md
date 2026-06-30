Supervertaler for Trados **v4.20.76** — unsigned builds for Trados Studio 2024 and 2026 (beta) are attached below. Covers 4.20.73 → 4.20.76.

## 📦 Installing from here (unsigned build – read first)

The plugins attached to this release are the **unsigned** builds. The version on the **RWS App Store is signed and notarised** – that's the recommended channel for most users. These downloads are for trying the latest fixes **before App Store approval** (which can take a few days, especially over a weekend).

**To install:**
1. Download the zip for your Trados version (table below).
2. **Extract it** – inside is a single `.sdlplugin` file.
3. Close Trados Studio, then double-click the `.sdlplugin` to run the Plugin Installer. **Do not rename the file** – Trados matches the filename against the plugin manifest.
4. Trados will warn that the plugin is **not signed**; that is expected for the direct build – click through to continue.

| Download | Trados version |
|---|---|
| `Supervertaler-for-Trados-Studio-2024.zip` | Trados Studio 2024 |
| `Supervertaler-for-Trados-Studio-2026-beta.zip` | Trados Studio 2026 (beta) |

## What's changed

## [4.20.76] – 2026-06-29

### Added (Diagnostics · crashes are now captured to the log)

- **Global crash handlers now write any unhandled/terminating exception to the diagnostic log, even when "Enable diagnostic logging" is off.** Previously a silent, no-dialog close left the Supervertaler log empty, with nothing to go on. The plugin now subscribes at startup to `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` and `Application.ThreadException`, and writes a crash banner (plugin version, source, full stack trace) to `…\Supervertaler\trados\logs\diagnostic.log`. A one-line startup marker is also written each launch so a crash can be tied to a version/time. (Managed exceptions are captured; a true native AccessViolation/StackOverflow can still bypass these — in which case Windows Event Viewer's faulting-module entry is the source of truth, and "log still empty after a crash" is itself a strong signal that the fault is native.)

### Fixed (Batch Translate · no longer reads the Trados document model off-thread)

- **Token-usage attribution no longer touches the Trados document model from a background thread.** Batch translation fires its completion callback off the UI thread, and the usage logger was reading thread-affine Trados objects (active file, project/document names, language pair) from there — a potential source of hard, no-dialog crashes during long batch runs. The usage context is now built only on the UI thread, cached, and the cached snapshot is returned to off-thread callers (the cache is warmed on the UI thread when a batch starts). Attribution is unchanged; the off-thread model access is gone.

## [4.20.75] – 2026-06-29

### Changed (Shared TM Bridge · clearer "Workbench" naming throughout)

- **The bridged-TM provider and its dialogs now consistently say "Supervertaler Workbench TM".** Following the 4.20.74 picker-title change, the rest of the bridge UI is aligned so it's obvious these TMs come from the Supervertaler Workbench app:
  - The entry in Trados' *Use…* (add translation provider) menu is now **"Supervertaler Workbench TM (bridged)"** (was "Supervertaler TM").
  - The picker dialog title is now **"Add bridged Supervertaler Workbench TMs"** (pluralised).
  - Each attached TM shows in the Translation Memory list as **"Supervertaler Workbench TM: \<name\>"** (was "Supervertaler TM: \<name\>"), and the related status/error messages match.

## [4.20.74] – 2026-06-29

### Changed (Shared TM Bridge · clearer picker dialog title)

- **The "Add Supervertaler TM" picker is now titled "Add bridged Supervertaler Workbench TM".** New users were mistaking this dialog (reached via *Use… → Supervertaler TM* in a project's TM settings) for a place to create translation memories. It only lists TMs created in the separate **Supervertaler Workbench** app and ticked as **Bridge**, so the title now spells that out. The body text already notes that all listed TMs are flagged "Bridge" in Workbench. Prompted by a support question.

## [4.20.73] – 2026-06-29

### Added (Help · AutoTagger documentation + contextual "?" link)

- **AutoTagger is now documented at [docs.supervertaler.com](https://docs.supervertaler.com/trados/autotagger/), with a contextual "?" link in the app.** The **AutoTagger Instruction** panel in *Settings → Prompts* now has a **"?"** button in its header that opens the AutoTagger help page (what it does, the Ctrl+Alt+G / right-click triggers, validation behaviour, and the instruction placeholders).

## Links

- RWS App Store (signed): https://appstore.rws.com/plugin/432
- Full changelog: https://github.com/Supervertaler/Supervertaler-for-Trados/blob/main/CHANGELOG.md
- Questions & discussion: https://github.com/orgs/Supervertaler/discussions
